namespace LumaLauncher.Models;

public sealed record SearchBatch(IReadOnlyList<LauncherResult> Results, string StatusText, bool EverythingAvailable);
