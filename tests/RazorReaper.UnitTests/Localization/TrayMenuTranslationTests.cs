using System.Text.RegularExpressions;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The tray menu is the one surface in the app that a repaint cannot reach. It is drawn by
/// Win32, from a popup menu built in <c>CrosshairOverlayWindow.Tray.cs</c>, on the overlay's own
/// STA thread — there is no component to re-render and no cascade to receive.
///
/// What replaces the repaint is that the menu does not persist: <c>ShowTrayMenu</c> creates it,
/// fills it and destroys it on every right-click, so reading each label from the dictionary at
/// that moment is the whole of the fix. That leaves exactly one string with a longer life than a
/// click — the tooltip on the icon itself, set once at registration — and the service re-sends it
/// when the language changes.
///
/// These are source assertions because the alternative is a real tray icon in a test run.
/// </summary>
public sealed class TrayMenuTranslationTests
{
    private static string TraySource()
        => File.ReadAllText(Path.Combine(TranslationParityTests.RepositoryRoot(),
            "RazorReaper", "Services", "Implementations", "Crosshair", "CrosshairOverlayWindow.Tray.cs"));

    private static string ServiceSource()
        => File.ReadAllText(Path.Combine(TranslationParityTests.RepositoryRoot(),
            "RazorReaper", "Services", "Implementations", "Crosshair", "CrosshairService.cs"));

    /// <summary>Every label the menu appends is a key, and the dictionary has all four of them.</summary>
    [Theory]
    [InlineData("tray.tooltip", "Razor Reaper — crosshair overlay")]
    [InlineData("tray.open", "Open Razor Reaper")]
    [InlineData("tray.overlay.show", "Show overlay")]
    [InlineData("tray.overlay.hide", "Hide overlay")]
    [InlineData("tray.update", "Restart & update (v{0})")]
    [InlineData("tray.quit", "Quit")]
    public void EveryTrayLabelComesFromTheDictionary(string key, string english)
    {
        Assert.Contains($"\"{key}\"", TraySource(), StringComparison.Ordinal);

        foreach (var code in new[] { "en", "de", "ru", "zh-Hans" })
        {
            Assert.True(TranslationParityTests.Read(code).ContainsKey(key), $"{code}.json is missing {key}");
        }

        Assert.Equal(english, TranslationParityTests.Read("en")[key]);
    }

    /// <summary>
    /// No literal survives next to an AppendMenu call. The menu is built in one method, so a
    /// label left behind is visible right here rather than only to someone right-clicking the
    /// tray in Russian.
    /// </summary>
    [Fact]
    public void NoMenuItemIsStillSpelledOut()
    {
        foreach (Match call in Regex.Matches(TraySource(), @"AppendMenu\((?<args>[^;]*)\);", RegexOptions.Singleline))
        {
            var args = call.Groups["args"].Value;

            foreach (Match literal in Regex.Matches(args, "\"(?<text>[^\"]*)\""))
            {
                var text = literal.Groups["text"].Value;

                // What is left is the key itself, plus the "&&" the mnemonic escape produces.
                Assert.True(
                    text is "&" or "&&" || Regex.IsMatch(text, @"^[a-z][a-z0-9.]*$"),
                    $"AppendMenu still spells out \"{text}\"");
            }
        }
    }

    /// <summary>
    /// The menu is built per open, which is what makes reading the dictionary at build time
    /// enough. A menu handle cached in a field would be the defect this replaces.
    /// </summary>
    [Fact]
    public void TheMenuIsBuiltFreshOnEveryRightClick()
    {
        var source = TraySource();

        Assert.Contains("IntPtr menu = CreatePopupMenu();", source, StringComparison.Ordinal);
        Assert.Contains("DestroyMenu(menu);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_menu;", source, StringComparison.Ordinal);
    }

    /// <summary>The tooltip outlives a click, so it is the one thing that needs telling.</summary>
    [Fact]
    public void TheTooltipIsResentWhenTheLanguageChanges()
    {
        var service = ServiceSource();

        Assert.Contains("_localizer.LanguageChanged += OnLanguageChanged;", service, StringComparison.Ordinal);
        Assert.Contains("_localizer.LanguageChanged -= OnLanguageChanged;", service, StringComparison.Ordinal);
        Assert.Contains("_overlay.RefreshTrayTooltip();", service, StringComparison.Ordinal);

        var tray = TraySource();

        // Posted, not called: the icon belongs to the overlay's window and therefore to its thread.
        Assert.Contains("PostMessage(_hwnd, WM_USER_TRAY_RETIP", tray, StringComparison.Ordinal);
        Assert.Contains("Shell_NotifyIcon(NIM_MODIFY, ref nid);", tray, StringComparison.Ordinal);
    }

    /// <summary>
    /// AppendMenu eats a single ampersand as a mnemonic prefix. English hits this with
    /// "Restart &amp; update"; now that the labels come from four files, any of them can.
    /// </summary>
    [Fact]
    public void AnAmpersandInATranslationSurvivesIntoTheMenu()
    {
        Assert.Contains(
            "private static string Mnemonic(string label) => label.Replace(\"&\", \"&&\", StringComparison.Ordinal);",
            TraySource(),
            StringComparison.Ordinal);

        foreach (Match call in Regex.Matches(TraySource(), @"AppendMenu\(menu, MF_STRING[^;]*\);", RegexOptions.Singleline))
        {
            Assert.Contains("Mnemonic(", call.Value, StringComparison.Ordinal);
        }
    }

    /// <summary>The whole menu really does read differently once the language moves.</summary>
    [Fact]
    public void TheLabelsThemselvesChange()
    {
        var localizer = new Localizer(new FakePreferencesStore(), System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var keys = new[] { "tray.open", "tray.overlay.show", "tray.overlay.hide", "tray.quit" };

        var english = keys.Select(localizer.T).ToArray();
        localizer.SetLanguage(AppLanguages.German);
        var german = keys.Select(localizer.T).ToArray();

        Assert.All(english.Zip(german), pair => Assert.NotEqual(pair.First, pair.Second));
    }
}
