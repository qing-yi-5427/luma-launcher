# Performance snapshot

Measured on the development machine on 2026-08-31. These numbers are regression indicators, not hardware-independent guarantees.

| Metric | `main` baseline (`61aa7ff`) | optimized branch |
|---|---:|---:|
| Single-file size | 75,754,673 bytes | 62,072,285 bytes |
| Hidden idle working set (7–10 s) | 9.9 MiB | 7.1 MiB median |
| Hidden idle private memory | 139.1 MiB | 137.7 MiB median |
| Hidden idle handles | 712 | 696 |
| Warm combined-search P95 | not recorded | 22–26 ms |
| Application index rebuild | not recorded | 26–89 ms |

These are historical measurements, not measurements of the current 0.4.0 worktree.
The old hidden-idle working set was measured after an explicit working-set trim;
it must not be described as total application memory consumption. That trim has
now been removed. Private bytes above remain the more useful allocation indicator.
The current code logs `input_to_results_ms` at the first displayed batch, including
input debounce, but this is not a full-results latency or a statistically robust P95.
The six-query integration sample measures coordinator time only (no debounce,
rendering, icons, cold extraction or user-perceived hotkey latency).

A read-only integration run on 2026-09-06 with .NET SDK 10.0.400 reported
`apps=1485 index_ms=27 everything=5 combined=8 search_p95_ms=43`.
This is one six-query warm coordinator sample, not an end-to-end latency guarantee.

No new idle-memory measurement was taken during this pass: launching/activating a
new window would disturb the user's foreground workload. For a fresh measurement,
report private bytes AND working set, process/runtime version, visible/hidden state,
cache state, and sample time. Do not manually call EmptyWorkingSet before sampling.
A self-contained bundle still extracts to the .NET bundle cache; single EXE does
not mean zero extraction or a separate-memory-free runtime.

The historical idle measurement was taken after Luma's single delayed maintenance pass. The optimized package keeps `IncludeAllContentForSelfExtract=true`: an isolated A/B run showed substantially lower private memory than loading managed content directly from the bundle, while changing the EXE size by only about 29 KiB. Cold single-file startup is intentionally not used as a regression gate because extraction-cache state dominates that number; resident hotkey search is the relevant launcher path.
