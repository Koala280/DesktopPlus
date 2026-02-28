param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path))
    {
        throw "Required file is missing: $Path"
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$solutionPath = Join-Path $repoRoot "DesktopPlus.sln"
$projectPath = Join-Path $repoRoot "DesktopPlus.csproj"
$releaseScript = Join-Path $repoRoot "scripts\build-release.ps1"
$installerScript = Join-Path $repoRoot "installer\DesktopPlus.iss"
$releaseWorkflow = Join-Path $repoRoot ".github\workflows\release.yml"
$ciWorkflow = Join-Path $repoRoot ".github\workflows\ci.yml"
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"

Require-Command -Name "dotnet"
Require-Command -Name "git"

Assert-FileExists -Path $solutionPath
Assert-FileExists -Path $projectPath
Assert-FileExists -Path $releaseScript
Assert-FileExists -Path $installerScript
Assert-FileExists -Path $releaseWorkflow
Assert-FileExists -Path $ciWorkflow
Assert-FileExists -Path $changelogPath

Push-Location $repoRoot
try
{
    if (-not $AllowDirty)
    {
        $dirty = git status --porcelain
        if ($LASTEXITCODE -ne 0)
        {
            throw "git status failed."
        }

        if (-not [string]::IsNullOrWhiteSpace(($dirty -join "")))
        {
            throw "Working tree is not clean. Commit/stash changes or rerun with -AllowDirty."
        }
    }

    $changelogContent = Get-Content -Path $changelogPath -Raw
    if ($changelogContent -notmatch '(?im)^##\s+\[Unreleased\]')
    {
        throw "CHANGELOG.md must contain an [Unreleased] section."
    }

    Write-Host "Running restore ($RuntimeIdentifier)..."
    dotnet restore $solutionPath -r $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet restore failed."
    }

    Write-Host "Running build ($Configuration)..."
    dotnet build $solutionPath -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet build failed."
    }

    $isccPath = $null
    $candidates = @()
    if ($env:ProgramFiles)
    {
        $candidates += Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    }
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFilesX86)
    {
        $candidates += Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"
    }

    $isccPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($isccPath)
    {
        Write-Host "Inno Setup found: $isccPath"
    }
    else
    {
        Write-Warning "Inno Setup not found. Portable release is still possible; Setup.exe build will be skipped locally."
    }

    Write-Host ""
    Write-Host "Preflight successful."
    Write-Host "- Solution build: OK"
    Write-Host "- Release scripts/workflows: OK"
    Write-Host "- Changelog structure: OK"
}
finally
{
    Pop-Location
}
