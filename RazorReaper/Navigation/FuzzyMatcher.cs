namespace RazorReaper.Navigation;

/// <summary>
/// Subsequence scorer in the fzf family: every character of the query must appear in the
/// text in order, but not necessarily adjacently — so "ldscr" finds "Loading Screen" and
/// "uwd" finds "Underwater Drops". Runs of adjacent matches and matches on a word boundary
/// score higher, which keeps the obvious answer on top instead of an accidental subsequence.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Sentinel for "this text does not contain the query at all".</summary>
    public const int NoMatch = -1;

    private const int ExactBonus = 1000;
    private const int PrefixBonus = 600;
    private const int WordPrefixBonus = 420;
    private const int ContainsBonus = 260;

    private const int CharScore = 12;
    private const int ConsecutiveBonus = 16;
    private const int BoundaryBonus = 22;
    private const int FirstCharBonus = 26;
    private const int MaxGapPenalty = 40;

    /// <summary>
    /// Scores a single query term against a single field. Higher is better;
    /// <see cref="NoMatch"/> means the term isn't present as a subsequence.
    /// </summary>
    public static int Score(string term, string? text)
    {
        if (string.IsNullOrEmpty(term)) return 0;
        if (string.IsNullOrEmpty(text)) return NoMatch;

        if (string.Equals(text, term, StringComparison.OrdinalIgnoreCase))
            return ExactBonus + LengthBonus(text);

        if (text.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return PrefixBonus + LengthBonus(text);

        var wordStart = IndexOfWordStart(text, term);
        if (wordStart > 0)
            return WordPrefixBonus + LengthBonus(text);

        if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            return ContainsBonus + LengthBonus(text);

        return Subsequence(term, text);
    }

    /// <summary>
    /// Best score across several fields — used when one term may match a title, a keyword
    /// or a description and we only care about the strongest hit.
    /// </summary>
    public static int BestScore(string term, params string?[] fields)
    {
        var best = NoMatch;
        foreach (var field in fields)
        {
            var score = Score(term, field);
            if (score > best) best = score;
        }
        return best;
    }

    /// <summary>Splits a raw query into whitespace-separated terms, dropping empties.</summary>
    public static string[] Tokenize(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Shorter fields win ties, so "Gamma" outranks "Gamma preset: Night vision".</summary>
    private static int LengthBonus(string text) => Math.Max(0, 40 - text.Length);

    /// <summary>
    /// Index of a word inside <paramref name="text"/> that starts with <paramref name="term"/>,
    /// or -1. Lets "screen" rank highly against "Loading Screen" without an exact prefix.
    /// </summary>
    private static int IndexOfWordStart(string text, string term)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (!IsBoundary(text, i)) continue;
            if (i + term.Length <= text.Length &&
                string.Compare(text, i, term, 0, term.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// A boundary is the start of a word: after a separator, or a lowercase→uppercase
    /// transition so "CharManager"-style names split on the camel hump too.
    /// </summary>
    private static bool IsBoundary(string text, int index)
    {
        if (index <= 0) return true;
        var prev = text[index - 1];
        if (prev is ' ' or '-' or '_' or '/' or '.' or ':' or '(') return true;
        return char.IsLower(prev) && char.IsUpper(text[index]);
    }

    /// <summary>
    /// Greedy left-to-right subsequence walk. Returns <see cref="NoMatch"/> as soon as a
    /// query character has no remaining home in the text.
    /// </summary>
    private static int Subsequence(string term, string text)
    {
        var score = 0;
        var textIndex = 0;
        var previousMatch = -2;

        foreach (var queryChar in term)
        {
            var found = -1;
            for (var i = textIndex; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(queryChar)) continue;
                found = i;
                // A boundary hit is worth skipping ahead for: in "Loading Screen", the 's'
                // of "Screen" beats the 's' that never appears earlier anyway, and for
                // repeated letters it biases toward the start of a word.
                if (IsBoundary(text, i)) break;
            }

            if (found < 0) return NoMatch;

            score += CharScore;
            if (found == 0) score += FirstCharBonus;
            else if (IsBoundary(text, found)) score += BoundaryBonus;

            if (found == previousMatch + 1) score += ConsecutiveBonus;
            else if (previousMatch >= 0) score -= Math.Min(found - previousMatch - 1, MaxGapPenalty);

            previousMatch = found;
            textIndex = found + 1;
        }

        return Math.Max(score, 1) + LengthBonus(text);
    }
}
