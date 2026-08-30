using System.Globalization;
using System.Text;

namespace LumaLauncher.Services;

public static class FuzzyMatcher
{
    public static double Score(string query, string title, string subtitle)
    {
        var normalizedQuery = Normalize(query).Trim();
        if (normalizedQuery.Length == 0)
            return 0;

        var normalizedTitle = Normalize(title);
        var normalizedSubtitle = Normalize(subtitle);
        var total = 0d;

        foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokenScore = ScoreToken(token, normalizedTitle);
            if (tokenScore < 0)
            {
                tokenScore = ScoreToken(token, normalizedSubtitle) * 0.42;
                if (tokenScore < 0)
                    return double.NegativeInfinity;
            }
            total += tokenScore;
        }

        if (normalizedTitle.Equals(normalizedQuery, StringComparison.Ordinal)) total += 520;
        else if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal)) total += 260;
        total -= Math.Min(70, normalizedTitle.Length * 0.45);
        return total;
    }

    private static double ScoreToken(string token, string candidate)
    {
        var exactIndex = candidate.IndexOf(token, StringComparison.Ordinal);
        if (exactIndex >= 0)
        {
            var boundary = exactIndex == 0 || !char.IsLetterOrDigit(candidate[exactIndex - 1]);
            return 250 - exactIndex * 2 + (boundary ? 95 : 0);
        }

        var queryIndex = 0;
        var first = -1;
        var previous = -2;
        var gaps = 0;
        var consecutive = 0;
        for (var index = 0; index < candidate.Length && queryIndex < token.Length; index++)
        {
            if (candidate[index] != token[queryIndex])
                continue;
            if (first < 0) first = index;
            if (index == previous + 1) consecutive++;
            else if (previous >= 0) gaps += index - previous - 1;
            previous = index;
            queryIndex++;
        }

        return queryIndex == token.Length ? 130 + consecutive * 15 - gaps * 4 - Math.Max(0, first) : -1;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
