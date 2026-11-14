param(
    [string]$Version = "",
    [ValidateSet("win-x64", "win-arm64", "win-x86")][string]$RuntimeIdentifier = "win-x64",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
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

Require-Command -Name "cargo"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$manifestFile = Join-Path $repoRoot "Cargo.toml"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $metadata = & cargo metadata --locked --no-deps --format-version 1 --manifest-path $manifestFile | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Cannot read the Cargo package version." }
    $Version = ($metadata.packages | Where-Object name -eq desktopplus).version
}
Assert-SemVer -InputVersion $Version
$targetTriple = @{ "win-x64" = "x86_64-pc-windows-msvc"; "win-arm64" = "aarch64-pc-windows-msvc"; "win-x86" = "i686-pc-windows-msvc" }[$RuntimeIdentifier]
if (-not $SkipInstaller -and $RuntimeIdentifier -ne "win-x64") {
    throw "The installer targets x64. Use -SkipInstaller for other architectures."
}
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
    $resolvedPublishDir = (Resolve-Path -LiteralPath $publishDir).Path
    $expectedPublishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\publish\$RuntimeIdentifier"))
    if ($resolvedPublishDir -ne $expectedPublishDir -or
        ((Get-Item -LiteralPath $publishDir).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ((Get-Item -LiteralPath $publishRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ((Get-Item -LiteralPath $artifactsRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to clean an unexpected publish directory: $resolvedPublishDir"
    }
    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}

Write-Host "Building native Rust DesktopPlus ($Configuration / $targetTriple / version $Version)..."
$previousVersion = $env:DESKTOPPLUS_VERSION
$previousTargetDir = $env:CARGO_TARGET_DIR
Push-Location $repoRoot
try {
    $env:DESKTOPPLUS_VERSION = $Version
    $env:CARGO_TARGET_DIR = Join-Path $repoRoot "target"
    $cargoArgs = @("build", "--locked", "--manifest-path", $manifestFile, "-p", "desktopplus", "--target", $targetTriple)
    if ($Configuration -eq "Release") { $cargoArgs += "--release" }
    & cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) { throw "Cargo build failed. Install the $targetTriple Rust target and matching MSVC build tools." }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    $binary = Join-Path $repoRoot "target\$targetTriple\$($Configuration.ToLowerInvariant())\DesktopPlus.exe"
    Copy-Item -LiteralPath $binary -Destination $publishedExe
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $publishDir
} finally {
    $env:DESKTOPPLUS_VERSION = $previousVersion
    $env:CARGO_TARGET_DIR = $previousTargetDir
    Pop-Location
}

if (-not (Test-Path $publishedExe))
{
    throw "Expected published executable not found: $publishedExe"
}
& (Join-Path $scriptRoot "verify-native-binary.ps1") -Executable $publishedExe

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

$builtInstaller = $false
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
        throw "Inno Setup 6 not found. Install it or explicitly use -SkipInstaller for a portable-only build."
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
        $builtInstaller = $true
    }
}
else
{
    Write-Host "Installer build skipped (-SkipInstaller)."
}

$artifactList = New-Object System.Collections.Generic.List[string]
$artifactList.Add($portableZip)
if ($builtInstaller)
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
    implementation = "Rust / Win32"
    targetTriple = $targetTriple
    buildTimeUtc = (Get-Date).ToUniversalTime().ToString("o")
    commit = $commitSha
    artifacts = @(
        @{ path = $portableZip; exists = (Test-Path $portableZip) },
        @{ path = $installerExe; exists = $builtInstaller },
        @{ path = $checksumsFile; exists = (Test-Path $checksumsFile) }
    )
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Host "Release manifest written: $manifestPath"

Write-Host "Release artifacts are ready in: $artifactsRoot"
