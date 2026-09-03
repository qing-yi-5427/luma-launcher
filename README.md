# Luma Launcher

Luma is a compact Windows launcher for applications, files, quick calculations,
web searches and personal commands. It uses the Everything SDK for file search
and keeps its own lightweight application and usage indexes. It does not install
or run a privileged indexing service.

## Features

- Unified application, file and folder search with fuzzy and Pinyin-initial matching
- 250 ms cancellable input debounce, with Enter available to search immediately
- Fast 64-result preview, expanding to as many as 512 ranked results in the full view
- Expandable full-results view with scrolling, file metadata and contextual actions
- Smart, relevance, usage/favorites, or alphabetical result ordering
- Favorites, recent usage ranking, application aliases and portable-app folders
- Calculator (`= 12 * 8`), URLs, web search (`? query`) and custom commands
- File actions: reveal, copy, open with, properties, terminal and administrator launch
- Listary-style Quick Switch: invoke Luma from a standard Open/Save dialog, choose a
  folder and press `Ctrl+G`
- Luma warm light/dark plus four Windows 11-inspired themes

## Requirements

- Windows 10 or 11, x64
- .NET 10 SDK to build
- The regular edition of Everything running in the background (the Lite
  edition does not expose IPC)

In managed mode, if Everything is installed but not running, Luma starts it with `-startup`,
which does not open a search window. Before starting it, Luma configures the
Everything client to run in the background without a second tray icon.
The Luma settings page can either detect `Everything.exe` automatically or
use a manually selected executable path. Exiting Luma also exits the Everything
client that supplies IPC search results. Connect-only mode leaves an existing
Everything process and its settings untouched.

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
- `PageUp` / `PageDown`: open the full-results view or move by one result page
- `Ctrl+G`: switch an Open/Save dialog to the selected folder
- `Right` or `Ctrl+O`: actions
- `Ctrl+,`: settings
- `Escape`: leave the full-results view, then hide

Drag the launcher from the search icon, shortcut badge, or other empty chrome.

`Luma.exe --settings` opens the settings window directly.

## Personalization formats

Settings accepts one application alias per line:

```text
vsc=Visual Studio Code
wx=微信
```

Custom commands use `keyword|title|executable|arguments|working directory`.
`{query}` is replaced with text following the keyword:

```text
note|新建记事|notepad.exe|{query}|
code|用 VS Code 打开|code.cmd|{query}|%USERPROFILE%
```

## Design notes

The architecture decisions and open-source launcher research are documented in
[`docs/OPEN_SOURCE_RESEARCH.md`](docs/OPEN_SOURCE_RESEARCH.md). The local
before/after snapshot is in [`docs/PERFORMANCE.md`](docs/PERFORMANCE.md).
