# Performance snapshot

Measured on the development machine. These numbers are regression indicators, not
hardware-independent guarantees.

## Current (v0.5.0, 2026-09-07)

| Metric | Value |
|---|---:|
| Single-file size | 62,070,226 bytes (59.19 MB) |
| Hidden idle working set (silent, ~14 s) | 84.2 MB |
| Hidden idle private memory | 31.3 MB |
| Hidden idle handles | 478 |
| Hidden idle threads | 16 |
| Source | 50 C# files, ~6.7k lines (+ 3 XAML) |

Sample: `Luma.exe --silent`, no window shown, Everything not forced to start.

### What dominates the 59 MB EXE

Self-contained .NET 10 WPF single-file. Typical payload:

| Component | Approx. |
|---|---:|
| System.Private.CoreLib | ~15 MB |
| PresentationFramework | ~15 MB |
| PresentationCore | ~8 MB |
| System.Private.Xml + D3DCompiler + coreclr/jit | ~15+ MB |
| Everything64.dll (SDK) | ~89 KB |
| Luma app IL + resources | ~1 MB class |

Application code is a rounding error; **WPF + runtime is the package**.

### Size levers (if needed later)

| Lever | Effect | Cost |
|---|---|---|
| Framework-dependent publish | drops ~40–50 MB | user must install Desktop Runtime |
| Drop `IncludeAllContentForSelfExtract` | small EXE delta | historically higher private bytes |
| `PublishReadyToRun` | slightly faster cold start | **larger** EXE |
| Trim unused managed (not WPF-safe) | limited | risk of missing types |

**Recommendation:** stay self-contained for a launcher; 59 MB is expected for WPF single-file.

## Historical (0.4.0 worktree, 2026-08-31 / 09-06)

| Metric | `main` baseline (`61aa7ff`) | optimized branch |
|---|---:|---:|
| Single-file size | 75,754,673 bytes | 62,072,285 bytes |
| Hidden idle working set (7–10 s, after trim) | 9.9 MiB | 7.1 MiB median |
| Hidden idle private memory | 139.1 MiB | 137.7 MiB median |
| Hidden idle handles | 712 | 696 |
| Warm combined-search P95 | not recorded | 22–26 ms |
| Application index rebuild | not recorded | 26–89 ms |
| Integration sample (09-06) | `apps=1485 index_ms=27 everything=5 combined=8 search_p95_ms=43` |

Notes:

- Old working-set numbers were taken after an explicit `EmptyWorkingSet` trim; do **not** compare them directly to the current 84 MB sample.
- Current private bytes (~31 MB) are a cleaner allocation signal than working set.
- Single-file still extracts to the .NET bundle cache; “one EXE” ≠ zero extraction.

## Runtime cost centers (by design)

| Area | Status | Notes |
|---|---|---|
| Input debounce 250 ms + cancel | OK | Enter skips delay |
| Icons async + LRU | OK | no block on first paint |
| Everything query on STA worker thread | OK | 3 s reply timeout |
| `AllowsTransparency` windows | **watch** | software composition; 100/150/200% DPI should be rechecked on low-end GPUs |
| Start Menu `FileSystemWatcher` ×4 | low | 2 s debounce rebuild |
| Clipboard listener (opt-in) | low | in-memory only |
| Game mode poll (2 s) | low | only when enabled |
| Preview decode | off UI thread | cache 200 files / 40 MB / 6 h |
| Hotkey probe window | transient | settings save only |

## Code weight

| File | Lines |
|---|---:|
| MainWindow.xaml.cs | 1294 |
| SettingsWindow.xaml.cs | 508 |
| EverythingSearchService.cs | 448 |
| AppIndexService.cs | 294 |
| Rest of Services/Models | ~3.5k |

MainWindow is the main maintainability hotspot, not a measured FPS issue.

## DPI / scroll bench (2026-09-07)

Command:

```powershell
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release -- --dpi-scroll
```

Environment: Windows 11, system DPI 96 (100%), test host `PROCESS_DPI_AWARENESS=1` (SystemAware). Offscreen `RenderTargetBitmap` at 96/120/144/192 DPI; 512 synthetic results; virtualized ListBox scroll via `ScrollViewer.LineDown`.

| Scale | measure first | measure p95 | render first | render p95 | scroll p95 | scroll avg |
|---|---:|---:|---:|---:|---:|---:|
| 100% | 70.87 ms | **0.10 ms** | 6.49 ms | 6.49 ms | **2.56 ms** | 0.89 ms |
| 125% | 15.14 ms | 0.04 ms | 0.38 ms | 0.66 ms | 2.28 ms | 0.81 ms |
| 150% | 17.65 ms | 0.42 ms | 6.15 ms | 0.63 ms | 2.31 ms | 0.77 ms |
| 200% | 13.88 ms | 0.07 ms | 6.09 ms | 0.50 ms | **2.51 ms** | 0.74 ms |

Interpretation:

- Warm layout after first pass is **sub-millisecond** at every scale — virtualization is working.
- Scroll p95 stays **≤ 2.6 ms**, well under a 16 ms frame budget at 60 Hz.
- DPI scaling does **not** introduce a clear scroll cliff from 100% → 200% in this harness.
- First measure/arrange is cold JIT + template build; not representative of hotkey-to-list latency.
- This harness uses offscreen bitmap render + LineDown, not a composited DWM window with `AllowsTransparency`. Real desktop composition cost on low-end GPUs is still an open empirical question; numbers here bound **layout/scroll logic**, not GPU fill.

Report CSV written next to the test binary as `dpi-scroll-report.csv`.

## Gate suggestions

1. Keep CI hard gate: single-file ≤ 100 MB (already in `build.yml`).
2. Soft gate: private bytes after 10 s `--silent` ≤ ~80 MB on a reference VM.
3. Soft gate: warm coordinator P95 ≤ 150 ms with fake Everything (tests already cover routing).
4. Do **not** gate cold single-file start time — bundle-cache state dominates it.
