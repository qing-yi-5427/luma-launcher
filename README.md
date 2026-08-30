# Luma Launcher

Luma is a compact Windows launcher for applications and files. It uses the
Everything SDK for file search and keeps its own lightweight application and
usage indexes. It does not install or run a privileged indexing service.

## Requirements

- Windows 10 or 11, x64
- .NET 10 SDK to build
- The regular edition of Everything running in the background (the Lite
  edition does not expose IPC)

If Everything is installed but not running, Luma starts it with `-startup`,
which does not open a search window. The Everything tray icon can be disabled
with **Tools → Options → UI → Show tray icon** without affecting Luma search.
The Luma settings page can either detect `Everything.exe` automatically or
use a manually selected executable path. Exiting Luma also exits the Everything
client that supplies IPC search results.

## Build

```powershell
dotnet build Launcher.csproj -c Release
```

The signed `dll/Everything64.dll` from the official Everything SDK is copied
beside `Luma.exe` at build time. See `THIRD_PARTY_NOTICES.md` for its license.

The release executable is written to `bin/Release/net10.0-windows/Luma.exe`.

## Single-file package

```powershell
dotnet publish Launcher.csproj -p:PublishProfile=SingleFile
```

This creates one self-contained x64 executable at
`publish/win-x64/Luma.exe`. It bundles the .NET desktop runtime and the
Everything SDK DLL, so the target machine does not need a separate .NET
installation. Everything itself is still required for indexed file search.

## Keyboard

- `Alt+Space`: show or hide Luma (configurable)
- `Up` / `Down`: select a result
- `Enter`: open
- `Ctrl+Enter`: reveal in File Explorer
- `Ctrl+Shift+Enter`: run as administrator
- `Ctrl+C`: copy the selected path
- `Right` or `Ctrl+O`: actions
- `Ctrl+,`: settings
- `Escape`: hide

Drag the launcher from the search icon, shortcut badge, or other empty chrome.

`Luma.exe --settings` opens the settings window directly.
