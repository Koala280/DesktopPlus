# DesktopPlus

DesktopPlus is a Windows desktop organizer with a **Rust UI and Rust core**. The primary application uses native Win32 windows, virtual list controls and the Windows shell. It does not require WPF, .NET, a browser engine or WebView.

## Build and run

Use Windows 10/11, a current stable Rust MSVC toolchain, and Visual Studio Build Tools with **Desktop development with C++** and a Windows SDK.

```powershell
cargo run --release
```

The standalone executable is `target/release/DesktopPlus.exe`. Release builds use static CRT linking, full LTO and one code generation unit.

For an isolated configuration:

```powershell
cargo run --release -- --settings .\artifacts\sandbox\settings.json --open C:\YourFolder
```

An existing settings file is optional. `--settings` also isolates the native cache and backup directory beside that file.

## Native interface

- Workspace overview with a persistent navigation sidebar, folder and pinned-list action cards, and a roomier panel list. All actions remain reachable at the minimum window width.
- General, Appearance and Keyboard shortcuts are available directly from the workspace sidebar. Each tab saves or cancels its own changes and retains drafts when switching tabs. Language import, the settings folder, help and exit are under General. Forms share consistent typography, rounded controls, animated switches, themed menus and scrollbars; keyboard focus scrolls fields into view.
- Floating folder, pinned-list and Recycle Bin panels.
- Compact dark panel headers with integrated tabs, fine separators and a curved active-tab outline. Hovering over the search button morphs it into a focused input; the field stays open for clicking and selecting text, and collapses when the pointer leaves, focus moves elsewhere, or Escape is pressed. Ctrl+F also opens search. Panels drag by their header, resize at their edges and collapse to a 48px strip while keeping their tabs visible. Settings, navigation, views and tab actions are available from the header's gear menu.
- A quiet trash icon beside search empties the active folder after an anchored confirmation showing the source and item count. It includes hidden items and subfolders even during a search, moves folder contents to the Windows Recycle Bin, and only removes references from pinned lists. Recycle Bin panels use the same header position and retain the Windows empty-bin confirmation.
- The trash icon is hidden by default. Show or hide it under Panel settings → Header → Show clear button. The choice is saved with the panel/tab settings; previously saved choices are retained.
- Tabs with rename, reorder, detach, merge and close.
- Panel settings with General, View, Header, Behavior and Columns pages, styled controls, keyboard focus and hover feedback. Configure the home folder, opening behavior, header actions, alignment, column visibility and panel movement; fit the panel to its contents or create a tab/panel.
- Optional expansion while the pointer is over a collapsed panel, returning to its saved collapsed position after leaving. Per-panel presets apply imported colors and fonts; choose the global appearance to reset them.
- Display-paced expand/collapse animations with precise per-monitor frame timing, finite 300 ms panel transitions and 220 ms control transitions. Centered tab groups stay on the panel center; the complete antialiased active-tab shape and accent slide together. Subpixel switches, button presses and soft color transitions share the same clock. Motion preserves its speed when interrupted or reversed and finishes at an exact endpoint. Panels reveal a stable content layout; row and scrollbar fades repaint only the affected areas, using reusable drawing buffers. Hover panels start closing on the first leave check. Windows' animation preference is respected, and the shared animation clock sleeps when transitions finish.
- Details, icon and photo views, including asynchronous Windows thumbnails and original shortcut icons requested at the current display resolution.
- Ctrl+mouse wheel zoom from 70 to 200% per panel. Text uses DirectWrite; fonts, icons, spacing and columns scale with zoom and monitor DPI. Dark scrollbars support dragging and page scrolling.
- A bottom-right grip resizes the panel. Double-click fits its width to the current columns; double-click again fits its height to the contents, within the monitor work area.
- Configurable metadata columns, header sorting, resizing and drag reordering. Image dimensions, authors, categories, tags and titles load on demand.
- Recursive ranked search, reusable disk caches and filesystem change notifications.
- Windows open/reveal/properties/context menus, clipboard copy/cut/paste, rename, folder creation and Recycle Bin operations.
- File drag out to Explorer; incoming file drops copy by default, Shift moves, Ctrl+Shift creates shortcuts. Pinned-list drops add references.
- Manual and automatic desktop sorting, custom extension rules and duplicate shortcut recovery.
- Saved layouts, appearance colors and font, tray controls, configurable global shortcuts and startup registration.
- English, German and Latvian resources, plus custom language JSON import.
- Restore points and import of legacy ZIP backups, including recovery before uninstall.

The native controls change the visual design. Decorative WPF preset effects and custom Explorer column definitions are not implemented in this version. Their imported configuration is retained where possible.

### Updates

General includes **Automatically install updates** and **Check for updates now**. The automatic setting is imported from WPF; new configurations default to off. When enabled, release builds check GitHub one minute after startup and every six hours, retrying failed checks after 15 minutes. Downloads run in the background. A prepared automatic update is installed at the next application start; a manual check offers immediate installation and restart. Disabling automatic updates cancels the prepared automatic installation. Debug and smoke-test builds do not run the updater.

Updates require a newer stable release with a matching Rust/Win32 release manifest, architecture-specific portable ZIP and `SHA256SUMS.txt`. Downloads use Windows HTTPS/proxy support and checksum verification. The updater replaces only `DesktopPlus.exe`, for both installed and portable copies, after saving settings and waiting for the running application to exit. It keeps the previous executable beside the app and a settings snapshot under the native data directory's `Updates` folder, restores the previous executable if relaunch cannot be started, and records the outcome in `Updates/last-result.txt`. The installation directory must be writable. Installer registration metadata changes only when running the installer; releases that need additional installation changes should use the installer.

## Efficiency

The UI blocks on the Windows message queue when idle; it has no continuous render loop. File listings and search work run on a background worker, and obsolete requests are cancelled. Virtual lists request visible cells instead of creating a widget for every file. Thumbnail and metadata caches are bounded.

Search starts using a saved index immediately. Initial scans start after five seconds and perform up to 64 enumeration steps or eight milliseconds of work, followed by a 40 ms pause. Directory links, junctions and protected system folders are skipped. Ranking retains at most 80 matches. Index storage targets 24 MiB per root and 64 MiB total before falling back to disk, excluding visible row data and transient buffers. Direct file events update in-memory indexes; subtree changes and lost events schedule a cooperative reconciliation. Index changes are saved atomically.

These design choices reduce overhead. They are not a guarantee of a particular speedup over WPF; compare equivalent folders, views and workloads when benchmarking.

## Settings and recovery

On first normal launch, the application imports `%APPDATA%\DesktopPlus_Settings.json` if no native settings exist. The legacy file is left untouched and its original contents are copied into the native data directory.

| Data | Location |
| --- | --- |
| Native settings | `%APPDATA%\DesktopPlus\Native\settings.json` |
| Caches, languages and restore points | `%LOCALAPPDATA%\DesktopPlus\Native\` |
| Sorted desktop files | `%LOCALAPPDATA%\DesktopPlus\AutoSortStorage\` |

Restore points store configuration, custom languages and file identities/locations; **they do not copy file contents or recover deleted files**. Recovery never overwrites an occupied destination. Failed or ambiguous moves are reported. Five native restore points are retained; legacy backups remain available.

The repository contains only the Rust application and core. The former C#/WPF application is available in Git history and the v1.x tags; importing its settings and restore archives remains supported.

## Verification and packaging

Use code checks for routine verification:

```powershell
cargo fmt --all --check
cargo clippy --locked --workspace --all-targets -- -D warnings
cargo test --locked --workspace
cargo build --locked --release
```

Package a release when needed:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 2.1.1
```

Visual checks are only needed when the visible interface changes and must stay limited to the affected area. Full GUI suites are not routine verification or packaging steps; run them only when explicitly requested.

The optional `scripts/test-native-ui.ps1` suite creates isolated fixture folders and checks listing, pinned items, photos, recursive search, filesystem notifications, collapse/expand and settings persistence. Reports and optional window screenshots are written under `artifacts/native-ui-tests/`. Its memory/CPU figures describe only that small fixture.

The optional `scripts/test-native-display.ps1` suite checks DirectWrite, original multi-resolution shortcut icons, zoom limits and persistence, text selection, scrollbar dragging and grip resizing/auto-fit. It writes screenshots and a report under `artifacts/display-tests/`.

See [DEPLOYMENT.md](DEPLOYMENT.md) for installer and signing options, [CONTRIBUTING.md](CONTRIBUTING.md) for development, and [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) for signing policy. Local builds are unsigned unless signing is explicitly configured.

To replace an installed WPF version, use the tested-release migration procedure in [DEPLOYMENT.md](DEPLOYMENT.md#replace-an-existing-wpf-installation-with-the-native-app). It uninstalls the old application, removes its scheduled startup task, preserves native settings and user files, and installs and starts the standalone Rust release with Windows startup enabled.
