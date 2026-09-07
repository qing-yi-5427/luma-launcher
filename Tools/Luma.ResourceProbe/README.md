# Comparable resource probe

Runs the same WPF host against two built `Luma.dll` assemblies. It overrides the
data directory before constructing services and sets `App.IsTestHost`; it never
opens an app, activates/shows a native window, registers a hotkey, launches or stops
Everything, or edits the user's settings. Everything must already be running.

The probe initializes the real application index, performs 60 typed queries with
real Everything results, measures/arranges the real view, loads eight shell icons,
and calls HideLauncher. Samples are taken after initialization, every 20 cycles,
and after a final 30-second settling period. No forced GC or working-set trim.
Optional sources/preview are disabled equally in both versions.

```powershell
# Build each revision's Launcher.csproj -c Release first.
# Use separate output directories; run probes sequentially, not concurrently.
dotnet build Tools/Luma.ResourceProbe/Luma.ResourceProbe.csproj -c Release `
  -p:LumaAssemblyPath=C:/baseline/bin/Release/net10.0-windows/Luma.dll `
  -o C:/temp/probe-main
dotnet build Tools/Luma.ResourceProbe/Luma.ResourceProbe.csproj -c Release `
  -o C:/temp/probe-mimo
dotnet C:/temp/probe-main/Luma.ResourceProbe.dll main
dotnet C:/temp/probe-mimo/Luma.ResourceProbe.dll mimo
```

Both are framework-dependent hosts on the same runtime. Absolute memory is not
comparable with a published self-contained executable. The reported input-to-final
P95 includes typing debounce, coordinator and UI publication; it is **not** hotkey
to first visible frame, keyboard focus latency, or input-to-first-app latency.
Native shell work, JIT, COM, WPF and runtime pools contribute to private bytes.
`gcHeapAfterLastCollectionMiB` is the heap size at the last collection, not current
live bytes; zero means a collection has not yet been reported. No hard memory
assertion is imposed on an arbitrary developer machine.

60 cycles are a bounded regression sample, not a long-duration leak certification.
Use actual desktop/manual acceptance in `docs/ACCEPTANCE.md` for native focus,
DWM, real IME and multi-monitor behavior.
