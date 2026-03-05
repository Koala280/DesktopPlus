# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [1.0.34] - 2026-03-05

### Changed

- Automatic background update checks now run every 90 seconds while auto-updates are enabled.

### Fixed

- Prevented false panel `IsHidden=true` persistence during intentional shutdown/update install when WPF reports transient panel invisibility.
- Manual "Install now" update flow now shuts down immediately after installer launch (no blocking dialog race), preventing panels from being saved as hidden during install handoff.

## [1.0.32] - 2026-03-05

### Fixed

- Startup-hide mode now keeps the main window in normal state and hidden, preventing it from reappearing at the bottom edge after update relaunch.
- Opening the main window from tray now re-centers it if its saved bounds are outside the visible work area.

## [1.0.31] - 2026-03-05

### Changed

- Automatic background update checks now run every 60 seconds while auto-updates are enabled.

## [1.0.30] - 2026-03-05

### Fixed

- Pending automatic updates are now applied only during app startup (before normal window initialization), not when opening the main window from tray.
- Prevented panel visibility state corruption during exit/update shutdown by skipping panel hide/save writes while the app is intentionally exiting.

## [1.0.29] - 2026-03-05

### Changed

- Automatic background update checks now run every 90 seconds while auto-updates are enabled (test release).

## [1.0.28] - 2026-03-05

### Changed

- Automatic background update checks now run continuously every 1 minute while auto-updates are enabled.

### Fixed

- Added single-instance startup protection to prevent duplicate DesktopPlus tray instances.
- Improved startup panel restoration reliability after update relaunches by adding a guarded restore fallback path.

## [1.0.27] - 2026-03-05

### Changed

- Automatic background update checks are now scheduled once per app start/enable cycle, executed after a 10-minute delay instead of running repeatedly.

### Fixed

- Prevented duplicate entries in the panel overview after hiding and re-showing an empty panel.

## [1.0.26] - 2026-03-05

### Changed

- Temporary local test build used a 10-second automatic background update check interval.

## [1.0.25] - 2026-03-05

### Changed

- Restored automatic background update checks to a 10-minute interval after local short-cycle validation.
- Startup launches (`--startup`) now keep the main window hidden/minimized consistently while panels continue loading.

### Fixed

- Post-update relaunch now waits for the source DesktopPlus process to exit before starting the new process, preventing duplicate app windows after update.

## [1.0.23] - 2026-03-05

### Changed

- Restored the automatic background update check interval to 10 minutes after the local 10-second test release.

## [1.0.22] - 2026-03-05

### Changed

- Switched automatic background update checks to a 10-second interval for short-cycle testing.

### Fixed

- Post-update relaunch now starts DesktopPlus with `--startup`, so the main window launches minimized/hidden as expected.
- Removed silent installer `/CLOSEAPPLICATIONS` usage in the app-driven update flow to avoid panel windows being persisted as hidden during update shutdown.

## [1.0.21] - 2026-03-04

### Changed

- Restored the automatic background update check interval to 10 minutes (after temporary short-interval test mode).

## [1.0.20] - 2026-03-04

### Changed

- Layout default behavior synchronization now also updates matching tab-level behavior settings (saved and open panels) instead of only panel-level values.
- Automatic background update checks now run continuously at a short fixed interval for testing purposes.
- Start-with-Windows registration from development builds now prefers the installed DesktopPlus executable path, preventing accidental autostart links to the dev binary.

### Added

- Added tray notification text when an automatic update installer has finished downloading and is ready for installation.

## [1.0.19] - 2026-03-04

### Changed

- Reworked layout-level global panel settings to match the single-panel settings structure much more closely.
- Added layout defaults for parent navigation item visibility, single/double-click open behavior, view mode, metadata visibility toggles, and metadata order.
- Layout global settings now apply these defaults consistently to saved and currently open panels while preserving explicitly customized panel values.

### Fixed

- Improved photo view row sizing to use the actual viewport width as the primary layout width reference.
- Adjusted photo collage row behavior for very wide single images so they can occupy a row cleanly without breaking packing.

## [1.0.18] - 2026-03-04

### Changed

- Pending automatic updates are now applied during app startup before the main window is created.
- Startup now defers normal app initialization until a pending silent installer run has been started.

### Fixed

- Automatic installation of already-downloaded updates no longer depends on opening the main window.
- Stale pending-update metadata and orphaned downloaded installers are cleaned up when the pending version is no longer newer than the installed version.

## [1.0.17] - 2026-03-04

### Changed

- Replaced the manual update `Yes/No/Cancel` message box with a custom, styled update action dialog.
- The update dialog now uses self-explanatory action buttons: open release page, install now, or later.

### Added

- Added new localized labels for the update action dialog in built-in `de`, `en`, and `lv` languages.

## [1.0.16] - 2026-03-04

### Changed

- Automatic background update checks now retry at fixed startup offsets: +30 seconds, +1 minute, and +3 minutes.
- Removed eager global file-search index warmup during app startup and switched search to on-demand folder enumeration.

### Fixed

- Improved automatic update reliability after Windows restarts by retrying when the first startup-time network check fails.
- Reduced memory usage in photo-heavy folders by capping preview decode size and enforcing byte budgets for icon/photo caches.

## [1.0.15] - 2026-03-04

### Changed

- Manual "Check for updates" prompt now offers three actions: open release page, install update silently now, or abort.
- Added visible manual update progress text in the main window while downloading/installing from the interactive update flow.

### Fixed

- Restoring the main window via tray icon double-click now consistently returns the app in normal (not minimized) state.
- Added full Latvian coverage for the new interactive update prompts and status texts.
- Corrected Latvian built-in UI strings to use proper diacritics.

## [1.0.14] - 2026-03-04

### Added

- Added built-in Latvian (`lv`) UI language.
- Added custom language import from common JSON formats (flat key/value, nested translation dictionaries, and i18next-style resources).
- Added theme-aware color presets in the color picker plus screen eyedropper support.

### Changed

- Color picker popup now stays inside the MainWindow bounds, including fullscreen/maximized usage.
- Optimized panel zoom (`Ctrl + Mouse Wheel`) by batching wheel events before applying layout updates.
- Default app-state language for fresh settings remains English (`en`).

### Fixed

- Automatically relaunch the app after a silently installed automatic update completes.
- Reduced panel refresh flicker during active downloads by ignoring transient temporary download files in folder watchers.
- Improved photo collage row sizing to better use available width and reserve measured scrollbar width.

## [1.0.13] - 2026-03-04

### Changed

- Set English (`en`) as the default UI language for fresh/default settings.
- Built-in Auto-Sort target panel names now re-localize when the app language changes.

### Fixed

- Preserved user-defined Auto-Sort target panel names during language switches.
- Renamed existing built-in Auto-Sort panels/tabs on language switch only when they still used localized default names.
- Reworked photo collage row packing to reduce empty gaps, use width more consistently, and reserve scrollbar space to avoid overlap.
- Prevented non-photo placeholders and initial tile-to-collage flicker in photo mode.
- Updated progressive photo loading to re-apply collage sizing while items stream in.

## [1.0.12] - 2026-03-04

### Fixed

- Fixed photo collage layout gaps in photo view by keeping item-container spacing chrome-free so justified rows fill consistently.
- Fixed corrupted parent-navigation back symbol rendering in panel lists.

### Changed

- Updated update toggle wording to reflect actual behavior: automatic installation instead of only checking.

## [1.0.11] - 2026-03-03

### Changed

- Installer default target directory now uses `%ProgramFiles%\DesktopPlus` instead of `%LOCALAPPDATA%\Programs\DesktopPlus`.
- Installer now requests administrator rights so installations into `Program Files` succeed reliably.

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
