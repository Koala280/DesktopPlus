# Contributing to DesktopPlus

## Getting started

1. Fork the repository and create a feature branch.
2. Build locally:

```powershell
cargo build --locked --release
```

3. Run the app from source:

```powershell
cargo run
```

## Pull request guidelines

- Keep PRs focused and small.
- Describe the problem and the solution clearly.
- Update docs when behavior or build steps change.
- Run `cargo fmt --all --check`, `cargo clippy --locked --workspace --all-targets -- -D warnings` and `cargo test --locked --workspace` before opening a PR.
- Run visual checks only for changes to the visible interface, and inspect only the affected area.
- Full GUI suites (`scripts/test-native-ui.ps1` and `scripts/test-native-display.ps1`) are opt-in and require an explicit request. Do not run them as routine verification or click through unrelated windows.

## Coding guidelines

- Follow existing project style and naming patterns.
- Native application code is in `native/desktopplus-app`; the reusable search/traversal engine is in `native/desktopplus-core`.
- Keep filesystem and shell property reads off the UI thread. Keep queues/caches bounded and preserve cancellation between work batches.
- The repository contains only the Rust application. Use Git history or v1.x tags when investigating the former C#/WPF implementation.
- Prefer clear and explicit logic over clever shortcuts.
- Add comments only where code is not self-explanatory.

## Reporting bugs

Use the GitHub issue templates and include:

- Steps to reproduce
- Expected behavior
- Actual behavior
- Windows version
- App version
