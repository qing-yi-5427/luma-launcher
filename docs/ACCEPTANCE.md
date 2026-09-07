# Mimo stable-mainline acceptance

## Automated checks completed (2026-09-07)

- [x] Real WPF TextChanged -> coordinator -> actionable selection, blocked file source.
- [x] Apps/tools bypass file debounce; rapid typing coalesces file requests.
- [x] Immediate Enter executes the current query, not the previous query.
- [x] File-only Enter; duplicate pending Enter executes once; hiding cancels execution.
- [x] Uncancellable stale results cannot replace newer results.
- [x] Synthetic WPF IME composition start/commit blocks old-result execution.
- [x] Late file results preserve the selected local result.
- [x] Optional providers fail/timeout independently; shell icon queue bounded.
- [x] Live Everything and Windows Index six-mode sort/pagination integration.
- [x] 100/125/150/200% offscreen layout/scroll benchmark.
- [x] Same-host main/mimo resource comparison, 60 cycles and 30-second settle.
- [x] Self-contained single-file publish.

## Native desktop checks still required

These are not claimed as passed by synthetic/offscreen automation. Run with the
published executable and normal user settings; do not merge/release solely on the
basis of a green smoke suite.

- [ ] Real Microsoft Pinyin / another installed IME: compose, confirm, cancel,
  immediately Enter; no accidental launch or missing committed character.
- [ ] Hotkey -> focused input, 50 show/hide cycles, including another foreground
  app, fullscreen/game mode and repeated hotkey presses; record visible latency.
- [ ] Two monitors with different DPI; move between monitors, remember position,
  disconnect a monitor and wake again; window remains visible and usable.
- [ ] Native Open/Save dialogs: select folder and quick-switch; verify success and
  unsupported-dialog failure without typing into the wrong foreground window.
- [ ] Everything absent/busy, Windows index unavailable/limited scope: useful
  status, app search still usable, no false promise of whole-disk completeness.
- [ ] Preview-heavy long session and rapid image scrolling: stable resource trend,
  stale images never overwrite current selection, UI remains responsive.

File fallback is not syntax-equivalent to Everything. Optional window/bookmark
results arriving beyond the 120 ms grace period are omitted for that query. Both
are intentional current limitations, not verified feature parity.
