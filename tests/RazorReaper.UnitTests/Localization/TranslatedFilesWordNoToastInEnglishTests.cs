using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// What "translated" is allowed to mean.
///
/// The page scan next door decides a file is migrated when it contains one <c>Localizer.T(</c>.
/// That is a cheap test and it was wrong in the direction that hurts: six files sat on the "Done"
/// list of docs/i18n.md — Compact ARK, Home, Steam Mods, Map Mods, the accent and font cards —
/// with the markup translated and the toasts still written out in English, and the whole suite
/// stayed green. The markup is what a reviewer reads; the toast is what the user reads at the
/// moment something went wrong, in the language the app promised them.
///
/// So the second half is pinned here. In a file that has a localizer at all, no message handed to
/// the notification or activity service may be an English literal. Anything else is a file that
/// knows how to translate and chose not to, on the line where it matters most.
///
/// The scan is deliberately narrow: only the *first* argument of those calls, because the second
/// is a severity token ("success", "warning") that is never read by a person, and only a literal
/// that still has words in it once its interpolation holes are removed, so
/// <c>$"{alreadyTranslated} {ex.Message}"</c> — a join, not a sentence — is not a finding.
/// </summary>
public sealed class TranslatedFilesWordNoToastInEnglishTests
{
    /// <summary>
    /// Files that hold a localizer for one surface while their messages belong to another that is
    /// still English end to end. An entry here is a promise that the file is on the "Not yet" side
    /// of docs/i18n.md, and the second test below fails if it stops being a finding — an exemption
    /// that no longer exempts anything is a hole nobody is looking at.
    /// </summary>
    /// <remarks>
    /// Empty since the Crosshair page was migrated. CrosshairService was the one entry — it held
    /// a localizer for the tray tooltip while its own "Saving profile failed" toast stayed
    /// English, and that toast is a key now. The dictionary stays: the next service that gets a
    /// localizer ahead of its page belongs in it with a reason, not in a silent pass.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> KnownEnglishMessages =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A message handed to the notification or activity service. The call may be broken across
    /// lines, so the argument is not looked for on the same line as the call.
    /// </summary>
    private static readonly Regex MessageCall = new(
        @"(?:NotificationService|Notifications|_notifications|ActivityService|_activity)\s*\.\s*"
        + @"(?:Show(?:Success|Error|Warning|WarningWithCountdown|Info)|AddActivity)\s*\(\s*"
        + @"(?<literal>\$?""(?:[^""\\]|\\.)*"")",
        RegexOptions.Singleline);

    /// <summary>An interpolation hole is not English; what is left around the holes is.</summary>
    private static readonly Regex Hole = new(@"\{[^}]*\}");

    /// <summary>Two letters in a row is a word. One is a unit, an initial or an escape.</summary>
    private static readonly Regex Word = new(@"[A-Za-z]{2,}");

    private static bool ReadsAsEnglish(string literal)
        => Word.IsMatch(Hole.Replace(literal.TrimStart('$').Trim('"').Replace("\\\"", string.Empty), " "));

    /// <summary>Every project source that knows how to translate, with its text.</summary>
    private static IEnumerable<(string Path, string Source)> FilesWithALocalizer()
    {
        var root = Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper");

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".razor", StringComparison.Ordinal)
                                 || p.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                     StringComparison.Ordinal)
                                 && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                     StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var source = File.ReadAllText(path);
            if (source.Contains("ILocalizer", StringComparison.Ordinal))
            {
                yield return (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), source);
            }
        }
    }

    private static IEnumerable<(string Path, string Literal)> EnglishMessages()
    {
        foreach (var (path, source) in FilesWithALocalizer())
        {
            foreach (Match match in MessageCall.Matches(source))
            {
                var literal = match.Groups["literal"].Value;
                if (ReadsAsEnglish(literal))
                {
                    yield return (path, literal);
                }
            }
        }
    }

    [Fact]
    public void AFileThatCanTranslateDoesNotWordItsToastsInEnglish()
    {
        var found = EnglishMessages()
            .Where(hit => !KnownEnglishMessages.ContainsKey(hit.Path))
            .Select(hit => $"{hit.Path}: {hit.Literal}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(found.Length == 0,
            "these files inject ILocalizer and still hand English straight to a toast or the "
            + "activity feed, which is what let six 'translated' pages ship half-English: "
            + string.Join("; ", found));
    }

    [Fact]
    public void EveryExemptionStillNamesAFileWithAnEnglishMessage()
    {
        var offenders = EnglishMessages().Select(hit => hit.Path).ToHashSet(StringComparer.Ordinal);

        Assert.All(KnownEnglishMessages, entry =>
        {
            Assert.True(offenders.Contains(entry.Key),
                $"{entry.Key} is exempt but no longer words a message in English — drop the exemption.");
            Assert.True(entry.Value.Length > 10, $"{entry.Key} needs a reason, not a note.");
        });
    }

    /// <summary>
    /// The six files this scan was written for are in range of it. A page that stops injecting
    /// ILocalizer under its own name, or moves, would drop out of the scan silently and take its
    /// toasts with it — which is the same silence that let these six ship as "Done".
    /// </summary>
    [Theory]
    [InlineData("Components/Pages/CompactArk.razor")]
    [InlineData("Components/Pages/Home.razor")]
    [InlineData("Components/Pages/SteamMods.razor")]
    [InlineData("Components/Pages/MapMods.razor")]
    [InlineData("Components/Shared/AccentColorCard.razor")]
    [InlineData("Components/Shared/FontSettingsCard.razor")]
    [InlineData("Services/Implementations/FeedbackService.cs")]
    public void TheFilesThisScanWasWrittenForAreInRangeOfIt(string relativePath)
        => Assert.Contains(relativePath, FilesWithALocalizer().Select(f => f.Path).ToArray());

    /// <summary>
    /// The scan has to be able to see the shape it exists for. Two joins that are not sentences
    /// stay quiet, two sentences are found — including the one broken over a line, which is how
    /// Compact ARK's cancellation toast was written and how it escaped a line-by-line grep.
    /// </summary>
    [Theory]
    [InlineData("NotificationService.ShowError($\"{errorMessage} {ex.Message}\");", false)]
    [InlineData("_notifications.ShowInfo($\"{a}: {b}\");", false)]
    [InlineData("NotificationService.ShowError(\"ARK is currently running.\");", true)]
    [InlineData("NotificationService.ShowWarning(\n    \"Compression cancelled.\");", true)]
    [InlineData("ActivityService.AddActivity(\"Uncompressed ARK install\", \"success\");", true)]
    [InlineData("ActivityService.AddActivity(Localizer.T(\"compact.activity.failed\"), \"warning\");", false)]
    public void TheScanFindsASentenceAndLetsAJoinThrough(string snippet, bool expected)
    {
        var match = MessageCall.Match(snippet);

        Assert.Equal(expected, match.Success && ReadsAsEnglish(match.Groups["literal"].Value));
    }
}
