using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The migration is not finished, and the half-done state is the one worth writing down.
/// docs/i18n.md carries a table of the pages that are still English end to end; this keeps that
/// table honest in both directions.
///
/// Both directions matter. A page that gets migrated and stays in the table sends the next
/// reader to do work that is already done. A page that never gets migrated and is quietly
/// dropped from the table reads as finished, and nobody goes looking for it again.
///
/// What this test cannot tell you is how much of a page is migrated: one <c>Localizer.T(</c> is
/// enough to take a page off the list, and six pages came off it with every toast still in
/// English. That half is <see cref="TranslatedFilesWordNoToastInEnglishTests"/>; read the two
/// together before calling anything translated.
/// </summary>
public sealed class UntranslatedPagesAreListedTests
{
    /// <summary>
    /// Components with no user-visible text of their own: routes that redirect, wrappers that
    /// render only what they are handed, and the router's own plumbing. They render no literal,
    /// so they are neither translated nor waiting to be.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NothingToTranslate =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Components/Routes.razor"] = "the router itself; the only page it names is NotFound",
            ["Components/_Imports.razor"] = "a list of using directives and nothing else",
            ["Components/FocusOnPageChange.razor"] = "arms a framework component; renders nothing",
            ["Components/Pages/Macros.razor"] = "a redirect to /scripts",
            ["Components/Pages/CraftingScripts.razor"] = "a redirect to /scripts",
            ["Components/Shared/CheckBox.razor"] = "a box and a tick; its label is child content",
            ["Components/Shared/Switch.razor"] = "a track and a knob; its label is a parameter",
            ["Components/Shared/ImageLightbox.razor"] = "an overlay around an <img>",
            ["Components/Shared/SettingRow.razor"] = "a row; its title and description are parameters",
            ["Components/Shared/StatusHeader.razor"] = "a banner; its label and description are parameters",
            ["Components/Shared/NotificationContainer.razor"] =
                "renders the message the notification service hands it, and nothing of its own",
        };

    /// <summary>The paths named in the "Not yet" table of docs/i18n.md.</summary>
    private static IReadOnlyList<string> DocumentedRemainder()
    {
        var docs = File.ReadAllText(Path.Combine(TranslationParityTests.RepositoryRoot(), "docs", "i18n.md"));

        var section = docs[docs.IndexOf("Not yet —", StringComparison.Ordinal)..];
        return Regex.Matches(section, @"`(Components/Pages/[A-Za-z]+\.razor)`")
            .Select(m => m.Groups[1].Value)
            .ToArray();
    }

    private static IEnumerable<string> ComponentFiles()
    {
        var root = Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper");

        return Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    private static bool IsTranslated(string relativePath)
        => File.ReadAllText(Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper",
                relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Contains("Localizer.T(", StringComparison.Ordinal);

    [Fact]
    public void TheDocumentedRemainderIsNotEmptyAndEveryEntryExists()
    {
        var listed = DocumentedRemainder();

        Assert.NotEmpty(listed);
        Assert.All(listed, path => Assert.True(
            File.Exists(Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper",
                path.Replace('/', Path.DirectorySeparatorChar))),
            $"docs/i18n.md lists {path}, which does not exist."));
    }

    /// <summary>A page that has been migrated must come off the list.</summary>
    [Fact]
    public void NothingOnTheListHasAlreadyBeenMigrated()
    {
        var done = DocumentedRemainder().Where(IsTranslated).ToArray();

        Assert.True(done.Length == 0,
            "docs/i18n.md still lists these as untranslated, but they render Localizer.T: "
            + string.Join(", ", done));
    }

    /// <summary>And a page that has not been migrated must be on it.</summary>
    [Fact]
    public void EveryComponentThatRendersNoTranslationIsAccountedFor()
    {
        var listed = DocumentedRemainder().ToHashSet(StringComparer.Ordinal);

        var unaccounted = ComponentFiles()
            .Where(path => !IsTranslated(path))
            .Where(path => !listed.Contains(path))
            .Where(path => !NothingToTranslate.ContainsKey(path))
            .ToArray();

        Assert.True(unaccounted.Length == 0,
            "these render no translated text and are neither listed in docs/i18n.md nor exempt: "
            + string.Join(", ", unaccounted));
    }

    /// <summary>
    /// An exemption that no longer describes anything is a hole nobody is looking at — the same
    /// rule the Language cascade's allow-list lives under.
    /// </summary>
    [Fact]
    public void EveryExemptionStillNamesAFileThatRendersNothing()
    {
        var files = ComponentFiles().ToHashSet(StringComparer.Ordinal);

        Assert.All(NothingToTranslate, entry =>
        {
            Assert.True(files.Contains(entry.Key), $"{entry.Key} is exempt but no longer exists.");
            Assert.False(IsTranslated(entry.Key), $"{entry.Key} is exempt but now renders Localizer.T.");
            Assert.True(entry.Value.Length > 10, $"{entry.Key} needs a reason, not a note.");
        });
    }
}
