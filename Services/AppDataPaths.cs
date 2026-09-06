namespace LumaLauncher.Services;

internal static class AppDataPaths
{
    // Tests set this before creating services; production always uses the per-user directory.
    internal static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaLauncher");
}
