param(
    [string]$Version = "1.0.0",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipInstaller,
    [switch]$SignArtifacts,
    [string]$SignToolPath = "",
    [string]$CertificatePath = "",
    [string]$CertificatePassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$DigestAlgorithm = "SHA256"
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd)
    {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Assert-SemVer {
    param([Parameter(Mandatory = $true)][string]$InputVersion)

    # SemVer: MAJOR.MINOR.PATCH with optional -prerelease / +build
    if ($InputVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z\.-]+)?(?:\+[0-9A-Za-z\.-]+)?$')
    {
        throw "Version '$InputVersion' is invalid. Expected format: MAJOR.MINOR.PATCH (optional -prerelease/+build)."
    }
}

function Get-NumericVersionParts {
    param([Parameter(Mandatory = $true)][string]$InputVersion)

    $core = ($InputVersion -split '\+')[0]
    $core = ($core -split '\-')[0]
    $parts = $core.Split('.')
    if ($parts.Count -ne 3)
    {
        throw "Version core '$core' must have exactly 3 numeric parts."
    }

    foreach ($part in $parts)
    {
        if ($part -notmatch '^\d+$')
        {
            throw "Version core '$core' must only contain numeric parts."
        }
    }

    return @{
        Core = $core
        AssemblyVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
        FileVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
    }
}

function Resolve-SignToolPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath))
    {
        if (-not (Test-Path $RequestedPath))
        {
            throw "Configured SignToolPath does not exist: $RequestedPath"
        }
        return $RequestedPath
    }

    $candidates = @()
    if ($env:ProgramFiles)
    {
        $candidates += Join-Path $env:ProgramFiles "Windows Kits\10\bin"
    }
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFilesX86)
    {
        $candidates += Join-Path $programFilesX86 "Windows Kits\10\bin"
    }

    foreach ($base in $candidates)
    {
        if (-not (Test-Path $base)) { continue }

        $found = Get-ChildItem -Path $base -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found)
        {
            return $found.FullName
        }
    }

    throw "SignArtifacts was requested, but signtool.exe could not be found. Provide -SignToolPath."
}

function Sign-File {
    param(
        [Parameter(Mandatory = $true)][string]$ToolPath,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$CertPath,
        [Parameter(Mandatory = $true)][string]$CertPassword,
        [Parameter(Mandatory = $true)][string]$Timestamp,
        [Parameter(Mandatory = $true)][string]$Digest
    )

    if (-not (Test-Path $FilePath))
    {
        throw "Cannot sign missing file: $FilePath"
    }

    Write-Host "Signing $FilePath ..."
    & $ToolPath sign /fd $Digest /td $Digest /tr $Timestamp /f $CertPath /p $CertPassword $FilePath
    if ($LASTEXITCODE -ne 0)
    {
        throw "Code signing failed for '$FilePath'."
    }
}

function Write-ChecksumFile {
    param(
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string[]]$ArtifactPaths
    )

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($artifact in $ArtifactPaths)
    {
        if (-not (Test-Path $artifact)) { continue }
        $hash = (Get-FileHash -Algorithm SHA256 -Path $artifact).Hash.ToLowerInvariant()
        $name = Split-Path -Leaf $artifact
        $lines.Add("$hash *$name")
    }

    $lines | Set-Content -Path $OutputPath -Encoding UTF8
}

Require-Command -Name "dotnet"
Assert-SemVer -InputVersion $Version
$versionParts = Get-NumericVersionParts -InputVersion $Version

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "DesktopPlus.csproj"
$issPath = Join-Path $repoRoot "installer\DesktopPlus.iss"

$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishRoot = Join-Path $artifactsRoot "publish"
$publishDir = Join-Path $publishRoot $RuntimeIdentifier
$installerDir = Join-Path $artifactsRoot "installer"
$portableZip = Join-Path $artifactsRoot "DesktopPlus-$Version-$RuntimeIdentifier-portable.zip"
$checksumsFile = Join-Path $artifactsRoot "SHA256SUMS.txt"
$manifestPath = Join-Path $artifactsRoot "release-manifest.json"
$publishedExe = Join-Path $publishDir "DesktopPlus.exe"
$installerExe = Join-Path $installerDir "DesktopPlus-Setup-$Version.exe"

if ($SignArtifacts)
{
    if ([string]::IsNullOrWhiteSpace($CertificatePassword))
    {
        $CertificatePassword = [Environment]::GetEnvironmentVariable("DP_SIGN_CERT_PASSWORD")
    }

    if ([string]::IsNullOrWhiteSpace($CertificatePath) -or [string]::IsNullOrWhiteSpace($CertificatePassword))
    {
        throw "When -SignArtifacts is used, -CertificatePath and -CertificatePassword (or env:DP_SIGN_CERT_PASSWORD) are required."
    }
}

New-Item -ItemType Directory -Force -Path $artifactsRoot, $publishRoot, $installerDir | Out-Null
if (Test-Path $publishDir)
{
    Remove-Item -Path $publishDir -Recurse -Force
}

Write-Host "Publishing DesktopPlus ($Configuration / $RuntimeIdentifier / version $Version)..."
& dotnet restore $projectPath -r $RuntimeIdentifier
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet restore failed."
}

& dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --no-restore `
    --self-contained true `
    -p:Version=$Version `
    -p:AssemblyVersion=$($versionParts.AssemblyVersion) `
    -p:FileVersion=$($versionParts.FileVersion) `
    -p:InformationalVersion=$Version `
    -p:ContinuousIntegrationBuild=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed."
}

if (-not (Test-Path $publishedExe))
{
    throw "Expected published executable not found: $publishedExe"
}

$resolvedSignTool = $null
if ($SignArtifacts)
{
    $resolvedSignTool = Resolve-SignToolPath -RequestedPath $SignToolPath
    Sign-File `
        -ToolPath $resolvedSignTool `
        -FilePath $publishedExe `
        -CertPath $CertificatePath `
        -CertPassword $CertificatePassword `
        -Timestamp $TimestampUrl `
        -Digest $DigestAlgorithm
}

if (Test-Path $portableZip)
{
    Remove-Item -Path $portableZip -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portableZip -Force
Write-Host "Portable package created: $portableZip"

if (-not $SkipInstaller)
{
    $isccCandidates = @()
    if ($env:ProgramFiles)
    {
        $isccCandidates += Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFilesX86)
    {
        $isccCandidates += Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"
    }

    $isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $isccPath)
    {
        Write-Warning "Inno Setup 6 not found. Install Inno Setup and rerun this script to generate Setup.exe."
    }
    else
    {
        Write-Host "Building installer with Inno Setup..."
        & $isccPath `
            "/DMyAppVersion=$Version" `
            "/DMyPublishDir=$publishDir" `
            "/DMyOutputDir=$installerDir" `
            $issPath

        if ($LASTEXITCODE -ne 0)
        {
            throw "Inno Setup build failed."
        }

        if ($SignArtifacts -and (Test-Path $installerExe))
        {
            Sign-File `
                -ToolPath $resolvedSignTool `
                -FilePath $installerExe `
                -CertPath $CertificatePath `
                -CertPassword $CertificatePassword `
                -Timestamp $TimestampUrl `
                -Digest $DigestAlgorithm
        }
    }
}
else
{
    Write-Host "Installer build skipped (-SkipInstaller)."
}

$artifactList = New-Object System.Collections.Generic.List[string]
$artifactList.Add($portableZip)
if (Test-Path $installerExe)
{
    $artifactList.Add($installerExe)
}

Write-ChecksumFile -OutputPath $checksumsFile -ArtifactPaths $artifactList.ToArray()
Write-Host "Checksums written: $checksumsFile"

if (Get-Command git -ErrorAction SilentlyContinue)
{
    try
    {
        $commitSha = (git rev-parse --short HEAD 2>$null).Trim()
    }
    catch
    {
        $commitSha = ""
    }
}
else
{
    $commitSha = ""
}

$manifest = [ordered]@{
    version = $Version
    runtimeIdentifier = $RuntimeIdentifier
    configuration = $Configuration
    buildTimeUtc = (Get-Date).ToUniversalTime().ToString("o")
    commit = $commitSha
    artifacts = @(
        @{ path = $portableZip; exists = (Test-Path $portableZip) },
        @{ path = $installerExe; exists = (Test-Path $installerExe) },
        @{ path = $checksumsFile; exists = (Test-Path $checksumsFile) }
    )
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Host "Release manifest written: $manifestPath"

Write-Host "Release artifacts are ready in: $artifactsRoot"
