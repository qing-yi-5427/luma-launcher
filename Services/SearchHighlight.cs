using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace LumaLauncher.Services;

/// <summary>Display-only matching: never consults providers, aliases or the filesystem.</summary>
public static class SearchHighlight
{
    public readonly record struct MatchRange(int Start, int Length);

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SearchHighlight), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(SearchHighlight),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits, Refresh));
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);

    private static void Refresh(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        // Query inherits through the visual tree; only explicitly opted-in TextBlocks are changed.
        if (target is not TextBlock block ||
            DependencyPropertyHelper.GetValueSource(block, TextProperty).BaseValueSource == BaseValueSource.Default) return;
        var text = GetText(block) ?? string.Empty;
        var ranges = Find(text, GetQuery(block));
        block.Inlines.Clear();
        System.Windows.Automation.AutomationProperties.SetName(block, text);
        var position = 0;
        foreach (var range in ranges)
        {
            if (range.Start > position) block.Inlines.Add(new Run(text[position..range.Start]));
            var run = new Run(text.Substring(range.Start, range.Length)) { FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            block.Inlines.Add(run);
            position = range.Start + range.Length;
        }
        if (position < text.Length) block.Inlines.Add(new Run(text[position..]));
    }

    public static IReadOnlyList<MatchRange> Find(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query) || query.Length > 256) return [];
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Do not pretend to parse Everything expressions. A drive-qualified path is plain text.
        if (tokens.Any(token => token is "|" ||
            new[] { "AND", "OR", "NOT" }.Contains(token, StringComparer.OrdinalIgnoreCase) ||
            token.IndexOfAny(['"', '\'', '*', '?', '|', '!', '<', '>', '=', '(', ')']) >= 0 ||
            (token.Contains(':') && !(token.Length >= 3 && char.IsAsciiLetter(token[0]) && token[1] == ':' &&
                (token[2] == '\\' || token[2] == '/') && token.LastIndexOf(':') == 1)))) return [];

        var starts = StringInfo.ParseCombiningCharacters(text);
        var boundaries = new HashSet<int>(starts) { text.Length };
        var hits = new bool[text.Length];
        foreach (var token in tokens)
        {
            var literal = false;
            for (var from = 0; from <= text.Length - token.Length;)
            {
                var index = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                if (boundaries.Contains(index) && boundaries.Contains(index + token.Length))
                {
                    Array.Fill(hits, true, index, token.Length);
                    literal = true;
                }
                from = index + 1;
            }
            if (literal) continue;
            var elements = StringInfo.ParseCombiningCharacters(token);
            // Short/repeated-letter/punctuation queries are too ambiguous for scattered highlighting.
            if (elements.Length < 3 || !token.All(char.IsLetterOrDigit) ||
                token.Distinct().Count() < 2) continue;
            var matched = new List<int>();
            var next = 0;
            for (var i = 0; i < starts.Length && next < elements.Length; i++)
            {
                var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
                var tokenEnd = next + 1 < elements.Length ? elements[next + 1] : token.Length;
                if (!text.AsSpan(starts[i], end - starts[i]).Equals(
                    token.AsSpan(elements[next], tokenEnd - elements[next]), StringComparison.OrdinalIgnoreCase)) continue;
                matched.Add(i);
                next++;
            }
            if (next != elements.Length || matched[^1] - matched[0] + 1 > elements.Length * 2 + 2) continue;
            foreach (var i in matched)
                Array.Fill(hits, true, starts[i], (i + 1 < starts.Length ? starts[i + 1] : text.Length) - starts[i]);
        }
        var ranges = new List<MatchRange>();
        for (var i = 0; i < hits.Length; i++)
        {
            if (!hits[i]) continue;
            var start = i;
            while (i + 1 < hits.Length && hits[i + 1]) i++;
            ranges.Add(new MatchRange(start, i - start + 1));
        }
        return ranges;
    }
}
