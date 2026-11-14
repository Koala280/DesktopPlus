# DesktopPlus working instructions

## Testing scope

- The user's testing preference is normal code tests and checks. Use relevant Rust tests, formatting, lint and build checks for code changes.
- Only perform visual checks when changing the visible interface, and inspect only the affected area.
- Do not routinely run `scripts/test-native-ui.ps1`, `scripts/test-native-display.ps1`, `--smoke-test`, or another workflow that opens and clicks through all application windows. These full GUI suites require an explicit user request, even after visual changes or when packaging a release.
- A general request to test or verify changes is not a request for a full GUI suite. Do not replace these scripts with equivalent manual or automated clicking through unrelated windows.
- Documentation-only and instruction-only changes require only content/diff verification; do not launch the app or run application tests for them.

## Delivering application changes

- After building an updated application, start it directly for the user without asking again. If DesktopPlus is already running, exit it gracefully so settings are saved, replace the executable, and launch the updated build.
- Starting the updated application is a delivery step, not a request to run a full GUI test suite. Documentation-only and instruction-only changes still do not require an application launch.
