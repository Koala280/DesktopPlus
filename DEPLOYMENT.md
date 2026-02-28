# DesktopPlus Windows Release

## Prerequisites

- Windows 10/11
- .NET SDK 8.x
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
- restore/build passes in Release
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

## 4) Optional variants

Only portable output:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0 -SkipInstaller
```

Different architecture:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0 -RuntimeIdentifier win-arm64
```

## 5) GitHub release (automated)

The workflow `.github/workflows/release.yml` runs on:

- tag push `v*.*.*`
- manual `workflow_dispatch` with `version`

It performs:

- preflight checks
- release build
- artifact upload
- GitHub release publish with:
  - portable zip
  - installer exe
  - `SHA256SUMS.txt`
  - `release-manifest.json`

## 6) Release checklist (manual)

1. Run `release-preflight.ps1` locally.
2. Update `CHANGELOG.md` for the new version.
3. Build with `build-release.ps1 -Version X.Y.Z`.
4. Verify hashes in `artifacts\SHA256SUMS.txt`.
5. Smoke-test on clean Windows account:
   - app starts
   - tray menu works
   - panel create/load/save works
   - auto-sort and shortcuts work
6. Create and push tag `vX.Y.Z` (or trigger workflow manually).
7. Validate attached GitHub release files.

## Windows startup behavior

The "Start with Windows" option writes/removes:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DesktopPlus`

DesktopPlus re-syncs this entry on startup when enabled.
