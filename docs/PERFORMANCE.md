# Performance snapshot

Measured on the development machine. These numbers are regression indicators, not
hardware-independent guarantees.

## Input scheduling and same-host comparison (2026-09-07)

Typed apps/tools now start immediately; only file queries use a cancellable
100 ms tail debounce. Enter bypasses it and can execute the first actionable app
batch without waiting for file IPC. Explicit filters/load-more remain immediate.
IME composition invalidates old results; cancelled generations cannot clear a new
query's progress state or publish errors into it. Shell icons have a 32-request
in-flight/queued cap (four native workers), and Windows Index COM queries are
serialized so obsolete queued requests cancel before entering native code.

### Same-host main / mimo sample

Built `main@6b8b82b` and the optimized mimo sources in Release, then used the exact
same `Tools/Luma.ResourceProbe` host/configuration on .NET 10.0.11. Real app index
and Everything, optional sources off, offscreen WPF layout plus shell icons,
60 search/hide cycles. Final paired run was sequential, mimo then main. A prior
pair in reverse order produced similar timing (~315 vs ~145 ms). Neither probe
forced GC or trimmed the working set.

| Metric | main | optimized mimo |
|---|---:|---:|
| Input to final results P95, 60 queries | 315.50 ms | 140.47 ms |
| Initialized hidden private memory | 96.44 MiB | 97.19 MiB |
| Private memory after 20 cycles | 178.11 MiB | 179.54 MiB |
| Private memory after 40 cycles | 178.87 MiB | 181.70 MiB |
| Private memory after 60 cycles | 182.70 MiB | 181.87 MiB |
| Private memory after additional 30 s | 182.50 MiB | 181.57 MiB |
| Working set after additional 30 s | 220.43 MiB | 221.48 MiB |
| Handles / threads after additional 30 s | 1164 / 42 | 1149 / 43 |

The measured input-to-final P95 improved about **55%**. This includes file debounce
and real provider work, but not native window activation/DWM. The two revisions'
private-memory cost is similar under this workload; **no memory reduction is
claimed**. Both retain substantial WPF/shell/runtime allocations after first use.
Mimo's 40-to-60-cycle private bytes were nearly flat, but handles and GC heap still
change during the run: 60 cycles do not prove absence of a long-session leak.

This is a framework-dependent comparison host, not the published EXE. It does not
reproduce the earlier 31 MiB claim or isolate why that historical sample differed.
An initial exploratory run used `GC.GetTotalMemory(false)` and returned negative
values on this local runtime; those invalid managed-memory samples were discarded.
The committed probe reports `GC.GetGCMemoryInfo().HeapSizeBytes` at the last GC,
not current live bytes. Process-private bytes above are separate OS counters.

### Additional final checks

- Real WPF pipeline with synthetic local apps / blocked files: 12-sample
  input-to-actionable P95 **3.36 ms** in the final integration run. This verifies no
  fixed 250 ms delay; it is not a real-world app scan or hotkey latency claim.
- Live integration: 1,482 apps, rebuild 38 ms, warm coordinator P95 **36 ms**.
- Offscreen scroll P95 at 100/125/150/200%: **1.33 / 1.35 / 1.57 / 1.35 ms**.
- Enter/current-query, duplicate Enter, hidden-window cancellation, IME event
  handling, stale results, selection retention and bounded icon queue passed.

See [acceptance checklist](ACCEPTANCE.md) for unverified native desktop scenarios
and [probe instructions](../Tools/Luma.ResourceProbe/README.md) for reproduction.
These results justify candidate testing, not an automatic mainline merge.

## Earlier mimo hardening verification (2026-09-07, resumed after reboot)

Scope: branch-local reliability/performance hardening; **not merged into main**.
Existing user opt-ins are preserved. New profiles default bookmarks, automatic
image preview and clipboard history to off.

- Application scoring runs on a worker and checks cancellation during scanning.
- Optional window/bookmark providers have separate single-call slots, no pending
  work queue, and a 120 ms grace period after core results are published. Results
  arriving after that deadline are omitted for that query. Uncancellable native
  work can retain its own slot but cannot consume the other provider's slot.
- Preview selection debounce is 140 ms. Decode concurrency is one; output is capped
  to 240 px on the long edge. Inputs over 50 MiB or 40 million pixels are skipped.
  Path/mtime/length keyed cache writes are atomic, with 200-file / 40 MiB / 6-hour
  limits. Cancellation cannot interrupt a native decoder already executing; it
  prevents stale publication and cancels queued work.
- Regression tests cover synchronous blocked optional work, failure isolation,
  cancellation propagation, app caller responsiveness, empty bookmark caching,
  thumbnail reuse/invalidation/limits, and real settings save/import state.

Commands (local SDK: `C:/Users/qingy/AppData/Local/Temp/dotnet-sdk-luma/dotnet.exe`):

```powershell
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release -- --integration
dotnet run --project Tests/Luma.SmokeTests.csproj -c Release -- --dpi-scroll
dotnet publish Launcher.csproj -p:PublishProfile=SingleFile
```

| Measurement | Result |
|---|---:|
| Isolated smoke/regression suite | Passed, including three consecutive final runs |
| Live Windows Index | Six explicit sort modes, 16/64 prefix and lookahead passed |
| Live Everything | Six explicit sort modes, 16/128 prefix; 1,024 file results passed |
| Application index rebuild | 1,482 apps, 40 ms |
| Warm coordinator P95 (six queries) | 48 ms |
| Offscreen scroll P95, 100/125/150/200% | 2.77 / 2.48 / 2.57 / 2.80 ms |
| Single EXE | 62,073,905 bytes (59.20 MiB) |
| Hidden portable process, ~25 s working set | 117.69 MiB |
| Hidden portable process, private bytes | 127.98 MiB |
| Handles / threads | 719 / 29 |
| Idle CPU time over 10 seconds | 15.62 ms (about 0.16% of one logical CPU) |
| Existing Everything processes combined, working set / private bytes | 268.29 / 298.59 MiB |

The idle probe used a fresh isolated portable profile, `--silent`, Connect-only
Everything, no foreground window and no forced working-set trim by the harness.
It did not modify the user's profile or start/stop Everything. It stopped only
its own Luma process. The combined observed Luma + Everything footprint was
approximately **386 MiB working set / 427 MiB private bytes** on this machine;
Everything also serves other applications, so this is not Luma's incremental cost.

The new memory sample is materially higher than the earlier 84/31 MB snapshot
below. Different profile/cache/warmup conditions prevent attributing that difference
to this patch; the old result is **not reproduced** and the suggested 80 MB private
bytes soft target is not met in this probe. Do not claim a memory win or superiority
over Wox/SwiftList from these measurements. This is one machine/run, not a sustained
memory-growth test. Coordinator timings exclude debounce, icon load and desktop
composition. Real IME, multi-monitor scaling, repeated hotkey wakeup and file-dialog
quick-switch still need hands-on acceptance before a mainline/release decision.

## Earlier v0.5.0 snapshot (2026-09-07)

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
| File-only debounce 100 ms + cancel | OK | apps/tools immediate; Enter skips delay |
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
