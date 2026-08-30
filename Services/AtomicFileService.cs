using System.Text;

namespace LumaLauncher.Services;

internal static class AtomicFileService
{
    internal static string ReadAllText(string path)
    {
        var temporary = path + ".tmp";
        if (!File.Exists(path) && File.Exists(temporary))
            File.Move(temporary, path);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    internal static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   16 * 1024, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    internal static void PreserveCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".corrupt", true);
        }
        catch { }
    }
}
