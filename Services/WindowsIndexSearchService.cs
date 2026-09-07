using System.Runtime.InteropServices;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>
/// Fallback file search via the Windows Search (Indexing Service) OLE DB provider.
/// Uses late-bound ADODB COM so no NuGet package is required. Used only when
/// Everything IPC is unavailable. Results are ranked locally.
/// </summary>
public sealed class WindowsIndexSearchService
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public async Task<EverythingSearchResponse> SearchAsync(string query, int maximumResults, string filter, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new EverythingSearchResponse([], true, UiStrings.Get("WindowsIndex"));

        return await Task.Run(() => SearchCore(query, maximumResults, filter, token), token).ConfigureAwait(false);
    }

    public static bool IsAvailable()
    {
        try
        {
            var connectionType = Type.GetTypeFromProgID("ADODB.Connection");
            if (connectionType is null)
                return false;
            dynamic? connection = null;
            try
            {
                connection = Activator.CreateInstance(connectionType);
                if (connection is null)
                    return false;
                connection.Open(ConnectionString);
                return true;
            }
            finally
            {
                if (connection is not null)
                {
                    try { connection.Close(); } catch { }
                    try { Marshal.FinalReleaseComObject(connection); } catch { }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static EverythingSearchResponse SearchCore(string query, int maximumResults, string filter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var like = EscapeLike(query.Trim());
        if (like.Length == 0)
            return new EverythingSearchResponse([], true, UiStrings.Get("WindowsIndex"));

        var kindPredicate = filter switch
        {
            "File" => "AND SCOPE='file:' AND NOT CONTAINS(System.ItemType, 'Directory')",
            "Folder" => "AND SCOPE='file:' AND CONTAINS(System.ItemType, 'Directory')",
            "Application" => string.Empty,
            _ => "AND SCOPE='file:'"
        };

        var sql = $"""
            SELECT TOP {Math.Clamp(maximumResults, 1, 512)}
                System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.Size, System.ItemType
            FROM SYSTEMINDEX
            WHERE CONTAINS(System.Search.Contents, '"{like}"', 1033)
               OR System.FileName LIKE '%{like}%' ESCAPE '\\'
                  {kindPredicate}
            ORDER BY System.ItemPathDisplay
            """;

        var connectionType = Type.GetTypeFromProgID("ADODB.Connection");
        if (connectionType is null)
            return new EverythingSearchResponse([], false, "Windows 索引不可用");

        dynamic? connection = null;
        dynamic? recordset = null;
        try
        {
            connection = Activator.CreateInstance(connectionType);
            if (connection is null)
                return new EverythingSearchResponse([], false, "Windows 索引不可用");
            connection.Open(ConnectionString);
            recordset = connection.Execute(sql);

            var results = new List<LauncherResult>(Math.Min(maximumResults, 64));
            while (!(bool)recordset.EOF && results.Count < maximumResults)
            {
                token.ThrowIfCancellationRequested();
                var path = recordset.Fields[0].Value as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var name = recordset.Fields[1].Value as string;
                    if (string.IsNullOrWhiteSpace(name))
                        name = Path.GetFileName(path);
                    var isDirectory = Directory.Exists(path);
                    if (!(filter == "File" && isDirectory || filter == "Folder" && !isDirectory))
                    {
                        results.Add(new LauncherResult
                        {
                            Title = name!,
                            Subtitle = path,
                            Target = path,
                            Kind = isDirectory ? LauncherResultKind.Folder
                                : path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? LauncherResultKind.Application
                                : LauncherResultKind.File,
                            Score = 0,
                            ProviderOrder = results.Count
                        });
                    }
                }
                recordset.MoveNext();
            }

            if (results.Count > 0)
                return new EverythingSearchResponse(results, true, UiStrings.Get("WindowsIndex"), results.Count, false);
            return new EverythingSearchResponse([], true, UiStrings.Get("WindowsIndex"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticsService.Log("windows-index", exception);
            return new EverythingSearchResponse([], false, "Windows 索引不可用");
        }
        finally
        {
            if (recordset is not null)
            {
                try { recordset.Close(); } catch { }
                try { Marshal.FinalReleaseComObject(recordset); } catch { }
            }
            if (connection is not null)
            {
                try { connection.Close(); } catch { }
                try { Marshal.FinalReleaseComObject(connection); } catch { }
            }
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("'", "''").Replace("\"", "\"\"");
}
