using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The migration itself, checked against the source: a surface that has been translated must not
/// still carry the English it was translated out of, every key it asks for must exist, and no key
/// may sit in the dictionaries with nothing rendering it.
///
/// Half-migrated is the failure mode this guards. A literal left behind in a translated page
/// shows as English inside a Russian window and nothing fails — the app keeps running, the bug is
/// only visible to someone reading that language.
/// </summary>
public sealed class TranslatedSurfaceTests
{
    /// <summary>
    /// Per migrated file, the English that must no longer appear in it. Grows with each surface.
    /// </summary>
    public static TheoryData<string, string> MigratedLiterals()
    {
        var data = new TheoryData<string, string>();

        foreach (var literal in new[]
        {
            ">Settings</h1>",
            "Appearance, audio and app behaviour. Hotkeys live on their own page.",
            "Title=\"My account\"",
            "Your profile picture, Discord account and connected computers.",
            ">Manage account</a>",
            "<h3>Appearance</h3>",
            "The accent recolours the whole app",
            "Appearance reset to defaults.",
            "Title=\"Interface scale\"",
            "Scales every element.",
            "<h3>App behaviour</h3>",
            "How Razor Reaper behaves alongside ARK and Discord.",
            "Title=\"Discord Rich Presence\"",
            "Shows your current tool and version on your Discord profile.",
            "Title=\"Start with ARK\"",
            "Title=\"Close with ARK\"",
            "Title=\"Updates\"",
            "Updates install themselves and restart the app.",
            "<h3>Elsewhere</h3>",
            "Title=\"Global hotkeys\"",
            "Title=\"Gamma triggers\"",
            ">Reset</button>",
            ">Open</a>",
            ">Automatic</span>",
        })
        {
            data.Add("Components/Pages/Settings.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Search</span>",
            "title=\"Search pages, locations and commands (Ctrl+K)\"",
            "title=\"Drag to resize\"",
            "Premium — open your license",
            "Freemium — open your license",
            "@group.Name\"",
            ">@entry.Label<",
            ">@_selectedGroup<",
        })
        {
            data.Add("Components/Shared/SharedNavbar.razor", literal);
        }

        foreach (var literal in new[]
        {
            "Welcome, @userName",
            "ARK: Survival Evolved Configuration & Server Management Tool",
            "<h3>Status</h3>",
            "<h3>Storage</h3>",
            "<h3>System Resources</h3>",
            "<h3>Hardware</h3>",
            ">Recent Activity</h3>",
            ">Clear All</button>",
            "<h3>Sound Settings</h3>",
            "Control UI click and notification audio.",
            "<h3>System Paths</h3>",
        })
        {
            data.Add("Components/Pages/Home.razor", literal);
        }

        foreach (var literal in new[]
        {
            "Page not found",
            "Back to Home",
            "This link points at a page RazorReaper no longer has.",
        })
        {
            data.Add("Components/Pages/NotFound.razor", literal);
        }

        return data;
    }

    /// <summary>
    /// The English these surfaces used to spell out, pinned where it moved to. Layout tests used
    /// to assert this wording against the razor files; it is a dictionary entry now, and it still
    /// may not drift by accident.
    /// </summary>
    [Theory]
    [InlineData("settings.title", "Settings")]
    [InlineData("settings.updates.description", "Updates install themselves and restart the app. There is no opt-out.")]
    [InlineData("notfound.title", "Page not found")]
    [InlineData("notfound.back", "Back to Home")]
    [InlineData("nav.page.feedback", "Feedback & Support")]
    public void TheEnglishWordingIsWhatItWas(string key, string expected)
    {
        Assert.True(TranslationParityTests.Read("en").TryGetValue(key, out var english), $"missing {key}");
        Assert.Equal(expected, english);
    }

    [Theory]
    [MemberData(nameof(MigratedLiterals))]
    public void AMigratedSurfaceKeepsNoEnglishLiteral(string relativePath, string literal)
    {
        var source = File.ReadAllText(AppFile(relativePath));

        Assert.DoesNotContain(literal, source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKeyTheAppAsksForExists()
    {
        var english = TranslationParityTests.Read("en");

        var unknown = UsedKeys().Concat(GeneratedKeys())
            .Where(used => !english.ContainsKey(used.Key))
            .Select(used => $"{used.File}: {used.Key}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unknown.Length == 0, $"keys with no English entry: {string.Join(", ", unknown)}");
    }

    /// <summary>A key nothing renders is a key four people keep translating for nothing.</summary>
    [Fact]
    public void NoEnglishKeyIsUnused()
    {
        var used = UsedKeys().Concat(GeneratedKeys()).Select(u => u.Key).ToHashSet(StringComparer.Ordinal);

        var dead = TranslationParityTests.Read("en").Keys
            .Where(key => !used.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(dead.Length == 0, $"keys nothing renders: {string.Join(", ", dead)}");
    }

    /// <summary>
    /// Keys nothing spells out: the sidebar asks for <c>nav.page.*</c> and <c>nav.group.*</c>
    /// through properties the catalog derives from the route and the group name, so they are
    /// read off the real catalog rather than grepped for.
    /// </summary>
    private static IEnumerable<(string File, string Key)> GeneratedKeys()
    {
        foreach (var group in RazorReaper.Navigation.NavCatalog.Groups)
        {
            yield return ("NavCatalog.cs", group.NameKey);
        }

        foreach (var page in RazorReaper.Navigation.NavCatalog.Pages)
        {
            yield return ("NavCatalog.cs", page.LabelKey);
        }
    }

    /// <summary>Every <c>T("…")</c> in the app project, with the file that spells it.</summary>
    private static IEnumerable<(string File, string Key)> UsedKeys()
    {
        var root = Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper");
        // "Localizer.T(" must match, so only a word character in front rules a call out.
        var pattern = new Regex(@"(?<!\w)T\(\s*""(?<key>[^""]+)""");

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(IsProjectSource))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(path)))
            {
                yield return (Path.GetFileName(path), match.Groups["key"].Value);
            }
        }
    }

    private static bool IsProjectSource(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".cs" or ".razor")) return false;

        // bin/obj carry generated copies of the razor files; scanning them double-counts.
        return !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string AppFile(string relativePath)
        => Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
}
