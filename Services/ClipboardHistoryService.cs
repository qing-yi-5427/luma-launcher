using System.Runtime.InteropServices;
using System.Windows.Interop;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>Keeps a small in-memory clipboard history (text only) for `clip` search.</summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const int MaxEntries = 40;
    private readonly object _sync = new();
    private readonly List<string> _entries = [];
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value) Start();
            else Stop();
        }
    }

    public IReadOnlyList<LauncherResult> Search(string query, int limit)
    {
        string[] snapshot;
        lock (_sync) snapshot = _entries.ToArray();
        if (snapshot.Length == 0)
            return [];
        var prepared = FuzzyMatcher.Prepare(query.Length == 0 ? "clip" : query);
        var results = new List<LauncherResult>();
        for (var i = 0; i < snapshot.Length; i++)
        {
            var text = snapshot[i];
            var preview = text.Length > 80 ? text[..80] + "…" : text;
            var score = 2000.0 - i * 10;
            if (query.Length > 0 && !query.Equals("clip", StringComparison.OrdinalIgnoreCase) &&
                !query.StartsWith("clip ", StringComparison.OrdinalIgnoreCase))
            {
                var match = FuzzyMatcher.Score(prepared, FuzzyMatcher.PrepareCandidate(preview), string.Empty);
                if (double.IsNegativeInfinity(match))
                    continue;
                score = match;
            }
            results.Add(new LauncherResult
            {
                Title = preview.Replace('\n', ' ').Replace('\r', ' '),
                Subtitle = $"剪贴板 #{i + 1} · {text.Length} 字符",
                Target = text,
                Kind = LauncherResultKind.Calculation,
                CopyText = text,
                Score = score
            });
            if (results.Count >= limit)
                break;
        }
        return results;
    }

    private void Start()
    {
        _source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LumaClipboard")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(_hwnd))
            DiagnosticsService.Log("clipboard", "AddClipboardFormatListener failed");
    }

    private void Stop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
        lock (_sync) _entries.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmClipboardUpdate = 0x031D;
        if (msg != WmClipboardUpdate)
            return IntPtr.Zero;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
                return IntPtr.Zero;
            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 8 * 1024)
                return IntPtr.Zero;
            lock (_sync)
            {
                _entries.RemoveAll(e => e.Equals(text, StringComparison.Ordinal));
                _entries.Insert(0, text);
                if (_entries.Count > MaxEntries)
                    _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("clipboard-read", exception);
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
