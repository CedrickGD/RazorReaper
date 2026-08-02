namespace RazorReaper.Navigation;

public enum PaletteKind
{
    /// <summary>A top-level page from <see cref="NavCatalog"/>.</summary>
    Page,

    /// <summary>A place inside a page — a specific map, cave or drop area.</summary>
    DeepLink,

    /// <summary>An action that runs immediately instead of navigating.</summary>
    Command
}

/// <summary>
/// One row in the command palette. Pages, deep links and commands all flatten to this so
/// the matcher and the result list don't need to care which is which.
/// </summary>
public sealed class PaletteItem
{
    public required PaletteKind Kind { get; init; }

    /// <summary>Stable identity, used to de-duplicate and to remember recents.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Supporting line — a page description, or the parent page for a deep link.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Badge on the right of the row, and the grouping key in the result list.</summary>
    public required string Category { get; init; }

    public required string IconSvg { get; init; }

    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Target for <see cref="PaletteKind.Page"/> and <see cref="PaletteKind.DeepLink"/>.</summary>
    public string? Route { get; init; }

    /// <summary>Action for <see cref="PaletteKind.Command"/>.</summary>
    public Func<Task>? Invoke { get; init; }

    /// <summary>
    /// Live state read at render time, e.g. "Running" for a script that's already going.
    /// Evaluated per render so the palette never shows a stale toggle.
    /// </summary>
    public Func<string?>? Status { get; init; }
}

/// <summary>Ranks palette items against a user query.</summary>
public static class PaletteSearch
{
    // Weights: a hit on the title beats a keyword hit, which beats a description hit.
    private const int TitleWeight = 3;
    private const int KeywordWeight = 2;

    /// <summary>
    /// Nudges whole pages above their own deep links at equal relevance, so typing "gamma"
    /// offers the Gamma page before any individual gamma preset.
    /// </summary>
    private static int KindBias(PaletteKind kind) => kind switch
    {
        PaletteKind.Page => 30,
        PaletteKind.Command => 10,
        _ => 0
    };

    /// <summary>
    /// Every term must match somewhere on the item (AND semantics), so "rag obelisk"
    /// narrows instead of widening. Returns matches best-first.
    /// </summary>
    public static List<PaletteItem> Rank(IEnumerable<PaletteItem> items, string? query)
    {
        var terms = FuzzyMatcher.Tokenize(query);
        if (terms.Length == 0) return items.ToList();

        var scored = new List<(PaletteItem Item, int Score)>();

        foreach (var item in items)
        {
            var total = 0;
            var matchedEveryTerm = true;

            foreach (var term in terms)
            {
                var score = ScoreTerm(item, term);
                if (score == FuzzyMatcher.NoMatch)
                {
                    matchedEveryTerm = false;
                    break;
                }
                total += score;
            }

            if (matchedEveryTerm)
            {
                scored.Add((item, total + KindBias(item.Kind)));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Item)
            .ToList();
    }

    private static int ScoreTerm(PaletteItem item, string term)
    {
        var best = FuzzyMatcher.NoMatch;

        var title = FuzzyMatcher.Score(term, item.Title);
        if (title != FuzzyMatcher.NoMatch) best = title * TitleWeight;

        foreach (var keyword in item.Keywords)
        {
            var score = FuzzyMatcher.Score(term, keyword);
            if (score != FuzzyMatcher.NoMatch) best = Math.Max(best, score * KeywordWeight);
        }

        var subtitle = FuzzyMatcher.Score(term, item.Subtitle);
        if (subtitle != FuzzyMatcher.NoMatch) best = Math.Max(best, subtitle);

        var category = FuzzyMatcher.Score(term, item.Category);
        if (category != FuzzyMatcher.NoMatch) best = Math.Max(best, category);

        return best;
    }
}
