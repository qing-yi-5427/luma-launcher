# Changelog

## 0.5.0 (mimo)

### Added
- Free-form global hotkey (Alt/Ctrl/Shift/Win + letter/digit/F-key), with presets in Settings
- Window switcher (search title / process name, Enter to activate)
- System commands (lock, sleep, hibernate, shutdown, restart, recycle bin, Settings, …)
- Browser bookmark search (Chrome / Edge / Brave)
- Search history panel (`Ctrl+H`) and Tab autocomplete from recent queries
- Multi-engine web search prefixes (`g`, `bd`, `gh`, `bili`, …) configurable in Settings
- File preview in the details pane (images via capped Shell decode; metadata for other types)
- Game mode: pause the global hotkey while a fullscreen app is in front (`Ctrl+F12` manual toggle)
- Windows Search (Indexing Service) fallback when Everything is unavailable
- Safe update download: fetch release asset, verify SHA-256, stage a replace script (no silent overwrite)
- UI language switch (zh-CN / en-US)
- `ILumaProvider` pipeline: apps / files / built-ins / windows / system / bookmarks

### Changed
- Smart ranking no longer permanently pins Everything files above applications (cross-provider interleave)
- Settings schema v2 (v1 files load with defaults for the new fields)
- Release workflow publishes `Luma.exe` + `Luma.exe.sha256` on `v*` tags

### Notes
- Code signing still requires SignPath (or other) certificate setup; see `docs/SIGNING.md`
