# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [1.0.6] - 2026-03-01

### Fixed

- Installer now restores Windows autostart based on `%APPDATA%\DesktopPlus_Settings.json` (`StartWithWindows`) so upgrades recover the setting even if the `Run` key was already missing.
- Installer clears stale `StartupApproved` disable state when it re-registers autostart during upgrade.

## [1.0.5] - 2026-03-01

### Fixed

- Preserved the existing Windows autostart setting during installer upgrades by restoring the `Run` entry after reinstall.

## [1.0.4] - 2026-03-01

### Fixed

- Improved "Start with Windows" reliability by creating the `Run` registry key if needed and handling `%ENV%` paths in startup validation.
- Cleared stale `StartupApproved` state when toggling startup in-app so Windows does not keep the app blocked after re-enabling.

### Added

- Initial open-source project metadata and GitHub workflows.
- Release preflight script (`scripts/release-preflight.ps1`) for build and repository checks.
- Release build manifest (`artifacts/release-manifest.json`) and SHA256 checksum output (`artifacts/SHA256SUMS.txt`).

### Changed

- Hardened release build script (`scripts/build-release.ps1`) with SemVer validation and publish version metadata.
- Extended release workflow to publish checksums and manifest alongside installer/portable artifacts.
- Updated deployment and readme documentation with end-to-end release runbook.
