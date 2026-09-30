# PS5Craft 1.0.0

**PS5Craft — PS5 Game Package Tool**

Modern Windows WPF frontend for PS5 FPKG inspection/extraction and MkPFS compression.

## Workflow

```text
.fpkg / .pkg  →  fpkg-cli (inspect / extract)  →  game folder  →  mkpfs CLI  →  .ffpfsc / .ffpfs
```

Heavy work runs in **external processes**. The WPF UI stays responsive.

## Requirements

- Windows x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [MkPFS](https://github.com/PSBrew/MkPFS): `python -m pip install -U mkpfs` (or place `mkpfs` on PATH / in Settings)
- Bundled `tools/fpkg/fpkg-cli` (built from [PSVIETHOA-FPKG-Builder](https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder))

## Run

```powershell
dotnet build PS5Craft.slnx -c Release
dotnet publish src/PS5Craft.App/PS5Craft.App.csproj -c Release -r win-x64 -o publish/win-x64
.\publish\win-x64\PS5Craft.exe
```

Or from the IDE: set `PS5Craft.App` as startup project.

## Features

- Dark PS5-inspired UI (Russian labels)
- Package metadata + cover via PSVIETHOA `PackageInspector`
- Extraction via `fpkg-cli pkg-extract` (separate process, cancellable)
- Packing via `mkpfs pack folder` with **FFPFSC** (default) or **FFPFS** (`--raw` + warning)
- CPU count, process priority (default Below Normal), compression level, block size
- Live progress / log / resource monitor
- Settings under `%LOCALAPPDATA%\PS5Craft\`

## Solution layout

| Project | Role |
|---------|------|
| `PS5Craft.App` | WPF shell + DI |
| `PS5Craft.ViewModels` | MVVM (CommunityToolkit) |
| `PS5Craft.Services` | FPKG / MkPFS / metadata |
| `PS5Craft.Infrastructure` | Process runner, settings, log, monitor |
| `PS5Craft.Core` | Models + contracts |
| `PS5Craft.Tests` | Unit tests |

## Legal

See `THIRD-PARTY-NOTICES.txt`. No Sony keys. Retail packages that require a supplied image key are refused. Intended for homebrew / debug-enabled workflows with user-provided files.
