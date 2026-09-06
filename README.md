# Luma Launcher

Luma is a compact Windows launcher for applications, files, quick calculations,
web searches and personal commands. It uses the Everything SDK for file search
and keeps its own lightweight application and usage indexes. It does not install
or run a privileged indexing service.

## Features

- Unified application, file and folder search with fuzzy and Pinyin-initial matching
- 250 ms cancellable input debounce, with Enter available to search immediately
- Fast 64-result preview, a 512-result expanded view, and repeatable Load more
  (512 additional results per request; the selected sort and type filter are retained)
- Provider-side application/file/folder filters; loaded counts and file-match lower
  bounds are displayed separately, rather than calling a truncated list “all results”
- Expandable full-results view with scrolling, file metadata and contextual actions
- A persistent sort menu in both quick and expanded search: smart, relevance,
  usage/favorites, name A–Z/Z–A, size small/large first, and modified newest/oldest
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

Application search and built-in tools work without Everything. On first use,
settings offers connection detection/retry and the official Everything download page.
Install the regular (not Lite) edition and wait for its index to become available.
Queries use asynchronous SDK replies with a three-second reply timeout and cancellation;
application matches can appear before the file provider finishes.

In managed mode, Luma starts an installed but stopped client with `-startup` and
`-config` pointing to a copy under `%LOCALAPPDATA%\LumaLauncher`. It does not modify
the user's original INI or take ownership of an existing process. A separate `-db`
path also keeps the managed database under Luma's data directory. On exit it verifies
the IPC window's process ID and sends the official IPC exit request only to its own
client, including windowless clients. An unrelated client is never closed. A client
already started by Luma remains Luma-owned if the setting is later changed to Connect.
Connect-only mode never starts Everything. Settings supports automatic
executable detection or a manual path. Reconnection is retried on later searches;
startup attempts are throttled. This is configuration isolation, not a separately
named Everything instance or separate Windows service.

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

## Search ordering and scope

The **排序** button above results is available in quick and expanded views. Its
label always shows the saved choice; the menu marks the current option. It shares
`ResultSort` with Settings, including an already-open Settings window. Changing
order restarts the query, resets the loading limit, and keeps the current type
filter. Load more and expand continue using that order.

- **Smart / relevance / usage and favorites** rank the recalled candidate set;
  they do not promise the globally most relevant or most frequently used file.
  Fuzzy/Pinyin fallback recall is available in these modes.
- **Name / size / modified** pass Everything's SDK sort BEFORE the result limit.
  File-system results therefore come from the full literal Everything match set,
  not from sorting metadata for an earlier 64-result preview. These modes do not
  add independent fuzzy/Pinyin fallback queries (use Everything syntax explicitly
  if needed), or discard provider matches with a local fuzzy filter. Name uses
  Everything's collation, which may differ from .NET's locale-aware app ordering.
- In mixed **All** results, Everything matches retain provider order and come
  **before applications and built-in suggestions**. These remaining local results
  are ordered by name (descending only for name Z–A); the Application filter remains
  available to reach them without paging through files. This is not a single
  cross-provider global ordering. Explicit calculator/URL/command queries retain
  their built-in behavior. Empty search remains recent usage, regardless of sort.
- **Size** compares files only. Folders have no aggregate size: they follow all
  matching files in name A–Z order in both directions; Folder-only is always A–Z.
  Applications/tools have no indexed size/time and follow provider results. No
  synchronous per-file filesystem I/O is used for sorting. Indexed file values
  missing from Everything follow its native ordering, not an invented zero size.
  **Modified** includes indexed files and folders in Everything's native order.
- Expanding/Load more re-queries a larger top-N. Ties use Everything's native order;
  an index changing between requests can naturally change the prefix. Counts and
  has-more describe provider matches, not an immutable snapshot.

### Search-hit highlighting

Quick and expanded results share case-insensitive highlighting in names, paths,
and the selected detail title/location. All literal occurrences take priority
(`ddd` highlights the complete `ddd`, not unrelated individual `d`s), while
preserving the original spelling and Unicode text elements. Whitespace-separated
words are highlighted independently. Only complete, compact, ordered fuzzy matches
of at least three characters are shown; repeated-letter queries do not use fuzzy
highlighting. Highlighting is display-only and does not affect ranking or recall.

Everything expressions (filters, operators, quotes, wildcards and grouping) are
conservatively left unhighlighted rather than interpreting syntax as matched text;
plain drive-qualified paths are supported. Pinyin initials, aliases and accent
normalization can retrieve results without literal characters in the displayed
text, so those transformations do not manufacture highlights. Theme accent color
and semibold weight follow live theme changes. The existing detail location is a
read-only TextBlock; its accessible text and the **复制路径** action are retained.

## Keyboard

- `Alt+Space`: show or hide Luma (configurable)
- `Up` / `Down`: select a result
- `Enter`: open
- `Ctrl+Enter`: reveal in File Explorer
- `Ctrl+Shift+Enter`: run as administrator
- `Ctrl+C`: normal text copy while editing the search box; copy the selected path
  when focus is in the result list (path copying is also available in actions)
- `PageUp` / `PageDown`: open the full-results view or move by one result page
- `Ctrl+G`: switch an Open/Save dialog to the selected folder
- `Right`: caret movement in the search box, actions when focus is in results
- `Ctrl+O`: actions
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

## Data, updates and verification

Settings, history, favorites, the application cache and diagnostic logs live under
`%LOCALAPPDATA%\LumaLauncher`. Settings imports validate/normalize legacy fields;
future schema versions are preserved with a warning and saving disabled until upgrade.
Export includes saved configuration, not history
or favorites. Review imported custom commands before saving. Pause history recording
or clear history (preserving favorites) in settings. Diagnostic timing entries contain
elapsed time and counts, not search text; error logs can still include local paths.

Update checks contact GitHub only on demand. Download the release from the project's
release page, exit Luma, and replace `Luma.exe`; local data remains separate. The
launcher is **not Authenticode-signed**: the SDK DLL's signature does not sign Luma.
SmartScreen may warn. Verify provenance and published SHA-256 hashes where available.
No automatic download, executable replacement, or elevation is performed.

```powershell
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release
# Optional read-only integration against an ALREADY running regular Everything:
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release -- --integration
```

Default tests use a temporary data directory and fake file provider, plus WPF layout
checks; they require no installed Everything. Windows CI builds, runs these tests,
publishes, checks that the artifact contains one EXE, and prints its SHA-256.
`--render` writes light/dark offscreen PNGs (including 200% pixel-density samples)
under the test executable's `renders` directory without showing/activating a window.
These are layout samples, not verification of actual per-monitor DPI transitions.
`--preview` opens an isolated interactive harness (no global hotkey registration,
no auto-start/exit of Everything, no user configuration writes); it activates a
window, so do not use it while another foreground workflow must remain undisturbed.

Manual Windows acceptance should cover IME composition, caret/clipboard behavior,
nonempty-query hide/wake, repeated Load more and each type filter, keyboard focus,
screen-reader labels, system theme/high contrast, and 100/150/200% DPI. Quick Switch
supports modern common file dialogs with a shell view and breadcrumb address bar;
legacy/custom dialogs are intentionally rejected. Foreground checks and full
SendInput counts reduce misdirected input, but do not make input delivery atomic or
verify that the destination actually finished navigating. Elevated dialogs may
reject injection through UIPI. Do not test lifecycle by killing a user's Everything.

## Design notes

The architecture decisions and open-source launcher research are documented in
[`docs/OPEN_SOURCE_RESEARCH.md`](docs/OPEN_SOURCE_RESEARCH.md). The local
before/after snapshot is in [`docs/PERFORMANCE.md`](docs/PERFORMANCE.md).
