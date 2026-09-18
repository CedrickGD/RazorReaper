using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RazorReaper.Services.Localization;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The dictionaries as files: four of them, the same keys in each, no blanks, and placeholders
/// that survive translation. A key that exists in English and nowhere else still renders — the
/// localizer falls back — but it renders in English inside a Russian window, which is the exact
/// half-translated look this whole stream is meant to avoid.
/// </summary>
public sealed class TranslationParityTests
{
    private static readonly string[] Codes = ["en", "de", "ru", "zh-Hans"];

    [Fact]
    public void EveryShippedLanguageHasAnEmbeddedDictionary()
    {
        Assert.Equal(
            Codes.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            LanguageCatalog.EmbeddedCodes().ToArray());

        Assert.Equal(
            Codes.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            AppLanguages.All.Select(l => l.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryEnglishKeyIsTranslatedEverywhere()
    {
        var english = Read("en");

        foreach (var code in Codes.Where(c => c != "en"))
        {
            var missing = english.Keys.Except(Read(code).Keys, StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.True(missing.Length == 0, $"{code}.json is missing: {string.Join(", ", missing)}");
        }
    }

    /// <summary>The other direction: a key nothing renders is a key nobody maintains.</summary>
    [Fact]
    public void NoTranslationCarriesAnOrphanKey()
    {
        var english = Read("en");

        foreach (var code in Codes.Where(c => c != "en"))
        {
            var orphans = Read(code).Keys.Except(english.Keys, StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.True(orphans.Length == 0, $"{code}.json has keys English does not: {string.Join(", ", orphans)}");
        }
    }

    [Fact]
    public void NoTranslationIsBlank()
    {
        foreach (var code in Codes)
        {
            var blank = Read(code)
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.True(blank.Length == 0, $"{code}.json has empty values: {string.Join(", ", blank)}");
        }
    }

    /// <summary>
    /// A dropped <c>{0}</c> loses the version number out of "Version {0} is available"; an added
    /// one throws a FormatException the localizer then swallows into the raw template. Both are
    /// invisible until someone runs the app in that language, so they are caught here.
    /// </summary>
    [Fact]
    public void PlaceholdersMatchEnglishInEveryTranslation()
    {
        var english = Read("en");

        foreach (var code in Codes.Where(c => c != "en"))
        {
            var translated = Read(code);

            foreach (var (key, value) in english)
            {
                if (!translated.TryGetValue(key, out var other)) continue;

                Assert.True(
                    Placeholders(value).SetEquals(Placeholders(other)),
                    $"{code}.json '{key}' uses {Describe(other)} where English uses {Describe(value)}");
            }
        }
    }

    /// <summary>
    /// The four product names the owner asked to keep in English. A translated "Razor Reaper" or
    /// a localised "Premium" makes the license wording and the shop disagree with each other.
    /// </summary>
    [Theory]
    [InlineData("Razor Reaper")]
    [InlineData("RazorReaper")]
    [InlineData("Premium")]
    [InlineData("Freemium")]
    [InlineData("Lifetime")]
    [InlineData("ARK")]
    [InlineData("Discord")]
    public void ProductNamesSurviveTranslation(string name)
    {
        var english = Read("en");
        var keys = english.Where(pair => pair.Value.Contains(name, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var code in Codes.Where(c => c != "en"))
        {
            var translated = Read(code);
            var dropped = keys
                .Where(key => translated.TryGetValue(key, out var value)
                              && !value.Contains(name, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.True(dropped.Length == 0, $"{code}.json translated '{name}' in: {string.Join(", ", dropped)}");
        }
    }

    /// <summary>Simplified script only — the picker offers no traditional Chinese to fall into.</summary>
    [Fact]
    public void TheChineseDictionaryIsSimplified()
    {
        // A handful of characters whose traditional forms are the ones that would slip in first.
        foreach (var traditional in new[] { "語言", "設定", "個", "關", "頁", "說", "無" })
        {
            var offenders = Read("zh-Hans")
                .Where(pair => pair.Value.Contains(traditional, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();

            Assert.True(offenders.Length == 0, $"traditional '{traditional}' in: {string.Join(", ", offenders)}");
        }
    }

    private static HashSet<string> Placeholders(string value)
        => Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static string Describe(string value)
    {
        var found = Placeholders(value).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return found.Length == 0 ? "no placeholder" : string.Join("/", found);
    }

    internal static IReadOnlyDictionary<string, string> Read(string code)
    {
        var path = Path.Combine(RepositoryRoot(), "RazorReaper", "Resources", "i18n", code + ".json");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    internal static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
