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

The idle measurement is taken after Luma's single delayed maintenance pass. The optimized package keeps `IncludeAllContentForSelfExtract=true`: an isolated A/B run showed substantially lower private memory than loading managed content directly from the bundle, while changing the EXE size by only about 29 KiB. Cold single-file startup is intentionally not used as a regression gate because extraction-cache state dominates that number; resident hotkey search is the relevant launcher path.
