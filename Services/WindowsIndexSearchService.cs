using System.Runtime.InteropServices;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>
/// Fallback file search via the Windows Search (Indexing Service) OLE DB provider.
/// Uses late-bound ADODB COM so no NuGet package is required. Used only when
/// Everything IPC is unavailable or Windows Index is preferred. Explicit ordering
/// is applied before the result limit; one extra row preserves pagination state.
/// </summary>
public sealed class WindowsIndexSearchService
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";
    private readonly SemaphoreSlim _querySlot = new(1, 1);

    public async Task<EverythingSearchResponse> SearchAsync(string query, int maximumResults, string filter, CancellationToken token, string sortMode = "Smart")
    {
        if (string.IsNullOrWhiteSpace(query))
            return new EverythingSearchResponse([], true, UiStrings.Get("WindowsIndex"));

        // Cancellation cannot interrupt connection.Execute in all COM providers.
        // Keep one active native call; obsolete queued queries cancel before COM.
        await _querySlot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SearchCore(query, maximumResults, filter, token, sortMode), token).ConfigureAwait(false);
        }
        finally { _querySlot.Release(); }
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

    internal static string BuildQuery(string query, int maximumResults, string filter, string sortMode)
    {
        var like = EscapeLike(query.Trim());
        var kindPredicate = filter switch
        {
            "File" => "AND System.ItemType <> 'Directory'",
            "Folder" => "AND System.ItemType = 'Directory'",
            _ => string.Empty
        };
        var order = sortMode switch
        {
            "NameDescending" => "System.ItemNameDisplay DESC",
            "SizeAscending" => "System.Size ASC",
            "SizeDescending" => "System.Size DESC",
            "ModifiedNewest" => "System.DateModified DESC",
            "ModifiedOldest" => "System.DateModified ASC",
            _ => "System.ItemNameDisplay ASC"
        };
        return $"""
            SELECT TOP {(long)Math.Max(1, maximumResults) + 1}
                System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.Size, System.ItemType
            FROM SYSTEMINDEX
            WHERE SCOPE='file:' AND System.FileName LIKE '%{like}%'
                  {kindPredicate}
            ORDER BY {order}, System.ItemPathDisplay ASC
            """;
    }

    private static EverythingSearchResponse SearchCore(string query, int maximumResults, string filter, CancellationToken token, string sortMode)
    {
        token.ThrowIfCancellationRequested();
        maximumResults = Math.Max(1, maximumResults);
        var sql = BuildQuery(query, maximumResults, filter, sortMode);

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
            connection.ConnectionTimeout = 3;
            connection.CommandTimeout = 3;
            connection.Open(ConnectionString);
            recordset = connection.Execute(sql);

            var results = new List<LauncherResult>(Math.Min(maximumResults, 64));
            while (!(bool)recordset.EOF && results.Count <= maximumResults)
            {
                token.ThrowIfCancellationRequested();
                var path = recordset.Fields[0].Value as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var name = recordset.Fields[1].Value as string;
                    if (string.IsNullOrWhiteSpace(name))
                        name = Path.GetFileName(path);
                    var isDirectory = string.Equals(recordset.Fields[4].Value as string, "Directory", StringComparison.OrdinalIgnoreCase);
                    if (!(filter == "File" && isDirectory || filter == "Folder" && !isDirectory))
                    {
                        results.Add(new LauncherResult
                        {
                            Title = name!,
                            Subtitle = path,
                            Target = path,
                            Kind = isDirectory ? LauncherResultKind.Folder : LauncherResultKind.File,
                            IndexedSize = recordset.Fields[3].Value is long size ? size : null,
                            IndexedModifiedFileTime = recordset.Fields[2].Value is DateTime modified ? modified.ToUniversalTime().ToFileTimeUtc() : null,
                            Score = 0,
                            ProviderOrder = results.Count
                        });
                    }
                }
                recordset.MoveNext();
            }

            return new EverythingSearchResponse(results.Take(maximumResults).ToList(), true, UiStrings.Get("WindowsIndex"),
                results.Count, results.Count > maximumResults);
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
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]").Replace("'", "''");
}
