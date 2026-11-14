# DesktopPlus Windows Release

## Prerequisites

- Windows 10/11
- Current stable Rust MSVC toolchain, including rustfmt and clippy
- Visual Studio Build Tools: Desktop development with C++ and a Windows SDK
- Inno Setup 6 (optional, required for `Setup.exe`)
- Optional for signed binaries: `signtool.exe` + code-signing certificate (`.pfx`)

## 1) Preflight (mandatory before release)

From repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\release-preflight.ps1
```

What it checks:

- required files/scripts/workflows exist
- changelog contains `[Unreleased]`
- Rust formatting, lint, unit/integration tests and Release build pass
- working tree is clean (unless `-AllowDirty` is used)

## 2) Build release artifacts

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0
```

Outputs:

- `artifacts\publish\win-x64\` -> published app folder
- `artifacts\DesktopPlus-1.0.0-win-x64-portable.zip` -> portable package
- `artifacts\installer\DesktopPlus-Setup-1.0.0.exe` -> installer (if Inno Setup is installed)
- `artifacts\SHA256SUMS.txt` -> SHA256 hashes of release files
- `artifacts\release-manifest.json` -> build metadata

## 3) Optional: signed artifacts

```powershell
$env:DP_SIGN_CERT_PASSWORD = "your-password"
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 `
  -Version 1.0.0 `
  -SignArtifacts `
  -CertificatePath "C:\certs\desktopplus.pfx"
```

Optional parameters:

- `-SignToolPath "C:\...\signtool.exe"`
- `-TimestampUrl "http://timestamp.digicert.com"`
- `-DigestAlgorithm SHA256`

Omitting `-Version` uses the `desktopplus` Cargo package version. The GitHub workflow requires its tag/input version to match that package version. Without `-SkipInstaller`, Inno Setup is required and a missing installation fails the build.

## 4) Optional variants

Only portable output:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0 -SkipInstaller
```

Different architecture:

```powershell
rustup target add aarch64-pc-windows-msvc
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0 -RuntimeIdentifier win-arm64 -SkipInstaller
```

## 5) GitHub release (automated)

The workflow `.github/workflows/release.yml` runs on:

- tag push `v*.*.*`
- manual `workflow_dispatch` with optional `version`
  - if `version` is empty, the workflow resolves the newest version header from `CHANGELOG.md` (first `## [X.Y.Z]` after `[Unreleased]`)

It performs:

- preflight checks
- release build
- artifact upload
- GitHub release publish with:
  - portable zip
  - installer exe
  - `SHA256SUMS.txt`
  - `release-manifest.json`

Release notes are taken from the matching changelog section. All files are uploaded to a draft first; the workflow publishes it as the latest release only after every asset is attached, so auto-update checks cannot see an incomplete upload. CI runs formatting, lint, code tests and a release build. Full GUI suites require a manual CI dispatch with `run_gui_tests` enabled.

## 6) Release checklist (manual)

1. Run `release-preflight.ps1` locally.
2. Update `CHANGELOG.md` for the new version.
3. Build with `build-release.ps1 -Version X.Y.Z`.
4. Verify hashes in `artifacts\SHA256SUMS.txt`.
5. If the visible interface changed, visually check only the affected area. Run a full GUI smoke suite only when explicitly requested.
6. Create and push tag `vX.Y.Z` (or trigger workflow manually).
7. Validate attached GitHub release files.
8. Ensure the release page contains a visible `Code signing policy` link (added automatically by workflow body).

The portable build contains the Rust `DesktopPlus.exe` and LICENSE. No .NET runtime or native core DLL is required. The x64 installer is the supported installer target; ARM64/x86 portable builds also require matching MSVC libraries and should be tested on their target machines.

The native updater consumes the latest non-prerelease GitHub release from `Koala280/DesktopPlus`. Publish `release-manifest.json`, `SHA256SUMS.txt` and `DesktopPlus-X.Y.Z-win-ARCH-portable.zip` together. The manifest must identify `Rust / Win32`, `Release`, the exact release version and the current runtime identifier. The ZIP must include a root-level native `DesktopPlus.exe`; older WPF releases, other architectures, invalid hashes, drafts and prereleases are rejected. The manifest currently describes one architecture per release, matching this build script. The updater replaces only the executable and does not run Inno Setup or change uninstall registration metadata. Installer changes or additional required files must be delivered through the installer. See README for scheduling, backups and recovery logs.

The full GUI suites are optional and require an explicit request; building or distributing an artifact does not require clicking through all windows. When explicitly requested, `scripts/test-native-ui.ps1 -Executable artifacts/publish/win-x64/DesktopPlus.exe -Capture` uses isolated settings and temporary fixture files.

## Windows startup behavior

The native "Start with Windows" option registers `DesktopPlus Startup` in Windows Task Scheduler, restoring the WPF version's immediate logon behavior. It starts the current executable with `--startup` for the current interactive user, with no trigger delay, idle/network requirement, battery restriction or execution time limit. It runs without elevation at normal process priority. Registration removes the former `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DesktopPlus` entry only after the task is registered successfully. Disabling startup removes both registrations. Explorer's tray can appear later; the app retries attaching its icon without changing the saved close behavior.

The installer reads native settings first, then legacy settings, and configures the task using `DesktopPlus.exe --configure-startup enable|disable`. This command does not open windows or change saved settings. Installation updates the executable path; uninstall removes the task before deleting the executable. Portable builds register their own path when "Start with Windows" is enabled; toggle it off and on to migrate an existing portable registration. The trigger runs at user logon, not before authentication, and cannot guarantee ordering ahead of every other program.

Interactive uninstall supports native JSON restore points and legacy ZIP backups via `--restore-before-uninstall`. Cancellation or incomplete recovery cancels uninstall. Silent uninstall leaves backups in place without prompting.

## Replace an existing WPF installation with the native app

Build and validate the native release with the code checks in preflight, then run the migration as the Windows user who owns the existing installation. If the visible interface changed, visually check only the affected area; a full GUI suite still requires an explicit request.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 2.0.0
powershell -ExecutionPolicy Bypass -File .\scripts\install-native.ps1 `
  -Installer .\artifacts\installer\DesktopPlus-Setup-2.0.0.exe `
  -Executable .\artifacts\publish\win-x64\DesktopPlus.exe
```

This explicitly replaces the standard per-user installation in `%LOCALAPPDATA%\Programs\DesktopPlus`, preserving existing native settings in preference to legacy settings. It backs up both configurations and the old scheduled task to `%LOCALAPPDATA%\DesktopPlus\MigrationBackups`, stops the installed application, removes the old task, runs the old uninstaller, and installs the native release. Custom language files are copied to native storage if needed. It enables Windows startup, archives the former active WPF settings file, and starts the native executable with `--startup`.

The script verifies that the installed executable matches the tested release, has no CLR header or legacy runtime imports, starts successfully without loaded WPF/CLR modules, and has an enabled `DesktopPlus Startup` task targeting the native executable with no trigger delay and no duplicate Run-key entry. Installation logs and `migration-report.json` are stored beside the configuration backup. A failed step stops with an error; the backup is retained for recovery. Other installation locations must be handled separately.

After successful startup, the script removes the obsolete WPF search cache and updater cache. `AutoSortStorage` contains user files and remains in place as the native application's managed storage. Existing restore archives are retained. Neither needs the old executable or its runtime. C#/WPF source and build projects have been removed from the repository; v1.x tags retain their history.
