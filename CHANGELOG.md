# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [1.0.10] - 2026-03-03

### Changed

- Panel-to-panel drag and drop now always uses move semantics for internal DesktopPlus transfers.
- Removing entries from a panel no longer prompts for confirmation when the action is panel-only.

### Fixed

- Fixed inconsistent copy/move behavior when dragging files from one panel into another panel.
- Fixed unnecessary confirmation prompts for "Remove from panel" actions from keyboard/context menu flows.
- Fixed sporadically broken `.lnk` shortcuts after file transfers by preserving and normalizing shortcut targets during move/copy operations.

## [1.0.9] - 2026-03-02

### Added

- Added live folder synchronization for folder panels via filesystem watchers, including automatic refresh on create/delete/rename/metadata changes.
- Added recovery handling for bound folder path changes so panels react when the target folder is renamed, recreated, or temporarily removed.

### Changed

- Improved drag and drop behavior in folder panels to default to move in the expected Windows-style scenarios.
- Improved panel creation flow from panel settings to place new panels adjacent instead of overlapping.
- Improved panel hover feedback on file/folder items with higher-contrast hover background and outline.
- Improved folder loading performance for very large directories with lightweight rendering mode and icon cache reuse.
- Limited image preview/dimension caches to prevent excessive memory growth in photo-heavy folders.

### Fixed

- Fixed panel refresh reliability when external file operations changed panel-bound folders (including stale entries after folder delete).
- Fixed folder-load cancellation path to avoid debugger-breaking cancellation exceptions during normal refresh/cancel cycles.
- Fixed subtle visual size jitter during panel collapse/expand transitions by stabilizing content/header state handling.

## [1.0.8] - 2026-03-01

### Fixed

- Stabilized drag source selection in panels so dragging starts from the clicked item and no longer pulls the wrong entry.
- Remapped panel paths after Desktop Auto-Sort moves files, preventing stale list entries/icons and wrong drag-out behavior.
- Expanded desktop source coverage for Auto-Sort to include public desktop items and improved shortcut handling.

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
