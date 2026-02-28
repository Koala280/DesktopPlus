# DesktopPlus

DesktopPlus is a Windows desktop companion app that adds customizable desktop panels for folders and pinned files.
It is built with WPF on .NET 8.

## Features

- Multiple desktop panels (list and folder panels)
- Drag and drop files/folders into panels
- Per-panel settings and visibility controls
- Layouts (save/apply panel positions and visibility)
- Theme/preset system with fine-tuning
- System tray integration
- Optional "Start with Windows"
- German and English UI

## Requirements

- Windows 10/11
- .NET 8 Desktop Runtime (only for framework-dependent builds)

## Run from source

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Debug
```

## Build release artifacts

Run preflight first:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\release-preflight.ps1
```

Then build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.0.0
```

Output paths:

- `artifacts\publish\win-x64\` (published app)
- `artifacts\DesktopPlus-<version>-win-x64-portable.zip`
- `artifacts\installer\DesktopPlus-Setup-<version>.exe` (if Inno Setup is installed)
- `artifacts\SHA256SUMS.txt`
- `artifacts\release-manifest.json`

Full details: [DEPLOYMENT.md](DEPLOYMENT.md)

## Contributing

Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening pull requests.

## Security

Please report vulnerabilities according to [SECURITY.md](SECURITY.md).

## License

This project is licensed under the MIT License.
See [LICENSE](LICENSE).
