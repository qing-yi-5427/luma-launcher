using System.Globalization;
using System.Text.RegularExpressions;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

internal sealed class BuiltInSearchService
{
    private sealed record CustomCommand(string Keyword, string Title, string Executable, string Arguments, string WorkingDirectory);

    private static readonly Regex DomainPattern = new(
        @"^(?:https?://)?(?:localhost(?::\d+)?|[\p{L}0-9-]+(?:\.[\p{L}0-9-]+)+)(?:[/?:#].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private CustomCommand[] _commands = [];
    private string _webSearchUrl = "https://www.bing.com/search?q={query}";

    internal void Configure(AppSettings settings)
    {
        _webSearchUrl = settings.WebSearchUrl.Contains("{query}", StringComparison.OrdinalIgnoreCase)
            ? settings.WebSearchUrl
            : "https://www.bing.com/search?q={query}";
        _commands = ParseCommands(settings.CustomCommands).ToArray();
    }

    internal IReadOnlyList<LauncherResult> Search(string rawQuery)
    {
        var query = rawQuery.Trim();
        var results = new List<LauncherResult>(3);

        if (Calculator.TryEvaluate(query, out var value))
        {
            results.Add(new LauncherResult
            {
                Title = value,
                Subtitle = "按 Enter 或 Ctrl+C 复制计算结果",
                Target = value,
                CopyText = value,
                Kind = LauncherResultKind.Calculation,
                Score = 2200
            });
        }

        if (TryCreateUrl(query, out var url))
        {
            results.Add(new LauncherResult
            {
                Title = $"打开 {query}",
                Subtitle = url,
                Target = url,
                Kind = LauncherResultKind.Web,
                Score = 2050
            });
        }
        else if (TryGetWebTerms(query, out var terms))
        {
            var searchUrl = _webSearchUrl.Replace("{query}", Uri.EscapeDataString(terms), StringComparison.OrdinalIgnoreCase);
            results.Add(new LauncherResult
            {
                Title = $"搜索网页：{terms}",
                Subtitle = searchUrl,
                Target = searchUrl,
                Kind = LauncherResultKind.Web,
                Score = 2000
            });
        }

        foreach (var command in _commands)
        {
            if (!MatchesCommand(query, command.Keyword, out var arguments))
                continue;
            results.Add(new LauncherResult
            {
                Title = command.Title,
                Subtitle = string.IsNullOrWhiteSpace(arguments) ? command.Executable : arguments,
                Target = Environment.ExpandEnvironmentVariables(command.Executable),
                Arguments = command.Arguments.Replace("{query}", arguments, StringComparison.OrdinalIgnoreCase),
                WorkingDirectory = Environment.ExpandEnvironmentVariables(command.WorkingDirectory),
                Kind = LauncherResultKind.Command,
                Score = 2100
            });
        }

        return results;
    }

    private static IEnumerable<CustomCommand> ParseCommands(string value)
    {
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
                continue;
            var parts = line.Split('|');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[2]))
                continue;
            yield return new CustomCommand(
                parts[0].Trim(),
                string.IsNullOrWhiteSpace(parts[1]) ? parts[0].Trim() : parts[1].Trim(),
                parts[2].Trim().Trim('"'),
                parts.Length > 3 ? parts[3].Trim() : string.Empty,
                parts.Length > 4 ? parts[4].Trim().Trim('"') : string.Empty);
        }
    }

    private static bool MatchesCommand(string query, string keyword, out string arguments)
    {
        arguments = string.Empty;
        if (query.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!query.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase))
            return false;
        arguments = query[(keyword.Length + 1)..].Trim();
        return true;
    }

    private static bool TryCreateUrl(string query, out string url)
    {
        url = string.Empty;
        if (query.Contains(' ') || !DomainPattern.IsMatch(query))
            return false;
        var candidate = query.Contains("://", StringComparison.Ordinal) ? query : "https://" + query;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

    private static bool TryGetWebTerms(string query, out string terms)
    {
        foreach (var prefix in new[] { "? ", "web ", "g " })
        {
            if (!query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            terms = query[prefix.Length..].Trim();
            return terms.Length > 0;
        }
        terms = string.Empty;
        return false;
    }

    private sealed class Calculator
    {
        private readonly string _text;
        private int _index;

        private Calculator(string text) => _text = text;

        internal static bool TryEvaluate(string input, out string result)
        {
            result = string.Empty;
            var expression = input.StartsWith('=') ? input[1..].Trim() : input;
            if (expression.Length == 0 || !expression.Any(character => "+-*/%^".Contains(character)) ||
                expression.Any(character => !char.IsDigit(character) && !char.IsWhiteSpace(character) &&
                                             ".,+-*/%^()".IndexOf(character) < 0))
                return false;
            try
            {
                var parser = new Calculator(expression.Replace(',', '.'));
                var value = parser.ParseExpression();
                parser.SkipWhitespace();
                if (parser._index != parser._text.Length || double.IsNaN(value) || double.IsInfinity(value))
                    return false;
                result = value.ToString("G12", CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Take('+')) value += ParseTerm();
                else if (Take('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParsePower();
            while (true)
            {
                SkipWhitespace();
                if (Take('*')) value *= ParsePower();
                else if (Take('/')) value /= ParsePower();
                else if (Take('%')) value %= ParsePower();
                else return value;
            }
        }

        private double ParsePower()
        {
            var value = ParseUnary();
            SkipWhitespace();
            return Take('^') ? Math.Pow(value, ParsePower()) : value;
        }

        private double ParseUnary()
        {
            SkipWhitespace();
            if (Take('+')) return ParseUnary();
            if (Take('-')) return -ParseUnary();
            if (Take('('))
            {
                var value = ParseExpression();
                SkipWhitespace();
                if (!Take(')')) throw new FormatException();
                return value;
            }
            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            var start = _index;
            while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '.'))
                _index++;
            if (start == _index || !double.TryParse(_text[start.._index], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
                throw new FormatException();
            return value;
        }

        private bool Take(char character)
        {
            if (_index >= _text.Length || _text[_index] != character)
                return false;
            _index++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                _index++;
        }
    }
}
