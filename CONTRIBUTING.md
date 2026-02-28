# Contributing to DesktopPlus

## Getting started

1. Fork the repository and create a feature branch.
2. Build locally:

```powershell
dotnet restore
dotnet build -c Release
```

3. Run the app from source:

```powershell
dotnet run -c Debug
```

## Pull request guidelines

- Keep PRs focused and small.
- Describe the problem and the solution clearly.
- Update docs when behavior or build steps change.
- Ensure `dotnet build -c Release` succeeds before opening a PR.

## Coding guidelines

- Follow existing project style and naming patterns.
- Prefer clear and explicit logic over clever shortcuts.
- Add comments only where code is not self-explanatory.

## Reporting bugs

Use the GitHub issue templates and include:

- Steps to reproduce
- Expected behavior
- Actual behavior
- Windows version
- App version
