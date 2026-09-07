namespace LumaLauncher.Services;

internal static class AppDataPaths
{
    private static string? _directory;

    /// <summary>
    /// Data root. Portable mode (sibling `portable.txt` or `--portable`) stores data
    /// next to the executable so a USB copy keeps settings/history.
    /// </summary>
    internal static string DirectoryPath
    {
        get => _directory ??= Resolve();
        set => _directory = value; // tests override before first use
    }

    internal static bool IsPortable { get; private set; }

    private static string Resolve()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            var marker = Path.Combine(exeDir, "portable.txt");
            var args = Environment.GetCommandLineArgs();
            if (File.Exists(marker) || args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase)))
            {
                IsPortable = true;
                return Path.Combine(exeDir, "LumaData");
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaLauncher");
    }
}
