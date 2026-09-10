# Changelog

## 0.5.1 — 2026-09-11

### Fixed
- Prevent context and tray menus from crashing single-file builds when WPF probes the Accessibility bridge.
- Keep context menus, text editing menus, and keyboard focus indicators consistent with the active theme.
- Reserve enough compact-window height for empty and loading states so their icon and guidance are not clipped.

## 0.5.0 — 2026-09-08

### Release validation
- Reviewed the remote mimo hardening and local-results-first changes; merged after regression and live search validation.
- Repair null language fields without discarding otherwise valid settings.
- Fix dark settings navigation contrast and allow small-screen navigation scrolling; refresh the layout render harness after the empty-state redesign.
- Preview decoding is cancellable between stages, concurrency-bounded, and cached by file identity; optional bookmarks and automatic preview are opt-in for new users.
- Known limitations: unsigned Windows executable; Windows Index covers indexed locations only; Quick Switch depends on compatible native file dialogs.

### Added
- Custom global hotkey (Alt/Ctrl/Shift/Win + letter/digit/F-key), captured in Settings
- Window switcher (search title / process name, Enter to activate)
- System commands (lock, sleep, hibernate, shutdown, restart, recycle bin, Settings, …)
- Browser bookmark search (Chrome / Edge / Brave)
- Search history panel (`Ctrl+H`) and Tab autocomplete from recent queries
- Multi-engine web search prefixes (`g`, `bd`, `gh`, `bili`, …) configurable in Settings
- File preview in the details pane (images via bounded WPF decoding; metadata for other types)
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
