param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Executable
)

# Replace this user's installed DesktopPlus, preserving the native configuration
# and user files. Build and code-test the release before invoking this script.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$Executable = (Resolve-Path -LiteralPath $Executable).Path
& (Join-Path $PSScriptRoot 'verify-native-binary.ps1') -Executable $Executable
$expectedHash = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
$installDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\DesktopPlus'))
$installedExe = Join-Path $installDir 'DesktopPlus.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9797F778-02C2-472B-AED5-9480B6DC93AE}_is1'
$nativeSettings = Join-Path $env:APPDATA 'DesktopPlus\Native\settings.json'
$legacySettings = Join-Path $env:APPDATA 'DesktopPlus_Settings.json'
$dataRoot = Join-Path $env:LOCALAPPDATA 'DesktopPlus'
$backupDir = Join-Path $dataRoot ('MigrationBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

function Assert-PlainPath([string]$Path) {
    # Check existing ancestors too: never operate through redirected app folders.
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to modify a redirected path: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
foreach ($path in @($installDir, $nativeSettings, $legacySettings, $dataRoot)) {
    Assert-PlainPath $path
}
if (Test-Path -LiteralPath $uninstallKey) {
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    if ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -ne $installDir) {
        throw "Unexpected DesktopPlus installation: $($registration.InstallLocation)"
    }
    if ($registration.UninstallString -ne ('"' + (Join-Path $installDir 'unins000.exe') + '"')) {
        throw 'Unexpected uninstaller command; no installation changes were made.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe'))) {
        throw 'The registered uninstaller is missing.'
    }
}
$tasks = @(Get-ScheduledTask | Where-Object { $_.TaskName -eq 'DesktopPlus Startup' })
foreach ($task in $tasks) {
    foreach ($action in $task.Actions) {
        if ($action.Execute.Trim('"') -ne $installedExe) {
            throw "Unexpected startup task target: $($action.Execute)"
        }
    }
}
$settingsSource = if (Test-Path -LiteralPath $nativeSettings) { $nativeSettings } elseif (Test-Path -LiteralPath $legacySettings) { $legacySettings } else { $null }
if ($settingsSource) {
    $settings = Get-Content -LiteralPath $settingsSource -Raw | ConvertFrom-Json
    if ($settings -is [Array] -or $null -eq $settings) { throw 'Expected a DesktopPlus settings object.' }
} else {
    $settings = [pscustomobject]@{}
}

New-Item -ItemType Directory -Path $backupDir | Out-Null
foreach ($entry in @(
    @{ Source = $legacySettings; Name = 'legacy-settings.json' },
    @{ Source = $nativeSettings; Name = 'native-settings.json' }
)) {
    if (Test-Path -LiteralPath $entry.Source) {
        Copy-Item -LiteralPath $entry.Source -Destination (Join-Path $backupDir $entry.Name)
    }
}
foreach ($task in $tasks) {
    Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath |
        Set-Content -LiteralPath (Join-Path $backupDir 'legacy-startup-task.xml') -Encoding Unicode
}
if (Test-Path -LiteralPath $uninstallKey) {
    Get-ItemProperty -LiteralPath $uninstallKey |
        Select-Object DisplayName, DisplayVersion, InstallLocation, UninstallString |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $backupDir 'previous-installation.json') -Encoding UTF8
}
Write-Host "Settings and startup backup: $backupDir"

# Native languages must remain available without the former WPF data folder.
$legacyLanguages = Join-Path $env:APPDATA 'DesktopPlus\Languages'
$nativeLanguages = Join-Path $dataRoot 'Native\Languages'
if (Test-Path -LiteralPath $legacyLanguages) {
    Assert-PlainPath $legacyLanguages
    Assert-PlainPath $nativeLanguages
    New-Item -ItemType Directory -Force -Path $nativeLanguages | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $legacyLanguages -Filter '*.json' -File) {
        Assert-PlainPath $file.FullName
        $destination = Join-Path $nativeLanguages $file.Name
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}

foreach ($task in $tasks) {
    Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Out-Null
}
# Closing to the tray does not exit the WPF application. Its persisted settings
# were backed up above; stop only the executable in the validated installation.
Get-CimInstance Win32_Process -Filter "Name = 'DesktopPlus.exe'" |
    Where-Object { $_.ExecutablePath -eq $installedExe } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
foreach ($task in $tasks) {
    Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false
}

if (Test-Path -LiteralPath $uninstallKey) {
    $uninstaller = Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $backupDir 'uninstall.log') + '"')
    ) -WindowStyle Hidden -Wait -PassThru
    if ($uninstaller.ExitCode -ne 0) { throw "Uninstall failed ($($uninstaller.ExitCode)); backup: $backupDir" }
    if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $installedExe)) {
        throw "The previous installation was not fully removed; backup: $backupDir"
    }
}

$setup = Start-Process -FilePath $Installer -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
    ('/DIR="' + $installDir + '"'), ('/LOG="' + (Join-Path $backupDir 'install.log') + '"')
) -WindowStyle Hidden -Wait -PassThru
if ($setup.ExitCode -ne 0) { throw "Native installation failed ($($setup.ExitCode)); backup: $backupDir" }
if ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Installed executable does not match the tested native release.'
}
& (Join-Path $PSScriptRoot 'verify-native-binary.ps1') -Executable $installedExe

$settings | Add-Member -NotePropertyName StartWithWindows -NotePropertyValue $true -Force
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $nativeSettings) | Out-Null
$temporarySettings = $nativeSettings + '.migration.tmp'
[IO.File]::WriteAllText($temporarySettings, ($settings | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding $false))
if (Test-Path -LiteralPath $nativeSettings) {
    [IO.File]::Replace($temporarySettings, $nativeSettings, (Join-Path $backupDir 'replaced-native-settings.json'))
} else {
    [IO.File]::Move($temporarySettings, $nativeSettings)
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = Start-Process -FilePath $installedExe -ArgumentList '--configure-startup enable' -WindowStyle Hidden -Wait -PassThru
if ($startup.ExitCode -ne 0) { throw "Startup registration failed ($($startup.ExitCode)); backup: $backupDir" }

# Archive the old configuration so subsequent launches prove they use native data.
# AutoSortStorage contains user files, and Backups contains recovery archives;
# neither directory belongs to the application binaries being uninstalled.
if (Test-Path -LiteralPath $legacySettings) {
    Move-Item -LiteralPath $legacySettings -Destination (Join-Path $backupDir 'retired-legacy-settings.json')
}
$app = Start-Process -FilePath $installedExe -ArgumentList '--startup' -WorkingDirectory $installDir -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 5
$app.Refresh()
if ($app.HasExited) { throw "Native startup failed ($($app.ExitCode)); backup: $backupDir" }
$badModules = @($app.Modules | Where-Object { $_.ModuleName -match '^(coreclr|clr|hostfxr|desktopplus_core|PresentationNative.*|wpfgfx.*)\.dll$' })
if ($badModules.Count -ne 0) { throw 'Unexpected legacy runtime loaded by the installed application.' }
$startupTask = Get-ScheduledTask -TaskName 'DesktopPlus Startup' -TaskPath '\'
if (-not $startupTask.Settings.Enabled -or $startupTask.Actions.Execute -ne $installedExe -or
    $startupTask.Actions.Arguments -ne '--startup' -or $startupTask.Triggers.Delay -notin @($null, '', 'PT0S') -or
    $startupTask.Principal.LogonType -ne 'Interactive') {
    throw 'The immediate native logon task was not configured correctly.'
}
if (Get-ItemProperty -LiteralPath $runKey -Name DesktopPlus -ErrorAction SilentlyContinue) {
    throw 'A duplicate Run-key startup entry remains.'
}
$retiredCaches = @(
    (Join-Path $env:APPDATA 'DesktopPlus\SearchIndex'),
    (Join-Path $dataRoot 'Updates')
)
foreach ($cache in $retiredCaches) {
    if (-not (Test-Path -LiteralPath $cache)) { continue }
    Assert-PlainPath $cache
    $resolved = (Resolve-Path -LiteralPath $cache).Path
    if ($resolved -ne [IO.Path]::GetFullPath($cache)) { throw "Unexpected cache path: $resolved" }
    $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($links.Count -ne 0) { throw "Refusing to remove a cache containing redirected paths: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$report = [ordered]@{
    installedExecutable = $installedExe
    version = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    sha256 = $expectedHash
    processId = $app.Id
    startupTask = $startupTask.TaskName
    startupCommand = '"' + $startupTask.Actions.Execute + '" ' + $startupTask.Actions.Arguments
    nativeSettings = $nativeSettings
    legacySettingsRetired = -not (Test-Path -LiteralPath $legacySettings)
    legacyStartupTaskReplaced = $true
    legacyRuntimeModules = @($badModules | ForEach-Object { $_.ModuleName })
    retiredCacheDirectories = $retiredCaches
    backupDirectory = $backupDir
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backupDir 'migration-report.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 8
