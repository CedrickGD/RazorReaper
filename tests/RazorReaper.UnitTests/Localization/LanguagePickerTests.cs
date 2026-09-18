using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The one control the whole feature hangs off: a Dropdown on Settings listing the four shipped
/// languages, switching live. The owner asked for a dropdown in the app settings that "switches
/// the whole thing" — a picker that needed a restart would not be that.
/// </summary>
public sealed class LanguagePickerTests
{
    private static string SettingsPage() => File.ReadAllText(Path.Combine(
        TranslationParityTests.RepositoryRoot(), "RazorReaper", "Components", "Pages", "Settings.razor"));

    [Fact]
    public void SettingsCarriesTheLanguageDropdown()
    {
        var page = SettingsPage();

        Assert.Contains("@inject ILocalizer Localizer", page, StringComparison.Ordinal);

        var row = Regex.Match(page, @"<SettingRow Title=""@Localizer\.T\(""language\.label""\).*?</SettingRow>",
            RegexOptions.Singleline);
        Assert.True(row.Success, "Settings must have a language row.");
        Assert.Contains("Value=\"@Localizer.Language\"", row.Value, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"OnLanguageChanged\"", row.Value, StringComparison.Ordinal);
        Assert.Contains("Options=\"LanguageOptions\"", row.Value, StringComparison.Ordinal);

        // Reuses the app's own Dropdown, not a native <select>: a system menu on Windows is drawn
        // outside the page and comes out white in a dark app.
        Assert.Contains("<Dropdown ", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", row.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four options, each in its own script: someone who landed in the wrong language has to be
    /// able to find their own without reading the one they cannot.
    /// </summary>
    [Fact]
    public void TheDropdownListsExactlyTheFourLanguagesInTheirOwnScript()
    {
        var page = SettingsPage();

        Assert.Contains(
            "AppLanguages.All.Select(l => new Dropdown.Option(l.Code, l.NativeName)).ToList()",
            page,
            StringComparison.Ordinal);

        var languages = RazorReaper.Services.Localization.AppLanguages.All;
        Assert.Equal(4, languages.Count);
        Assert.Equal(
            new[] { "English", "Deutsch", "Русский", "中文 (简体)" },
            languages.Select(l => l.NativeName).ToArray());
    }

    /// <summary>
    /// Switching repaints the window, so exactly one component owns the repaint. A page that also
    /// called StateHasChanged here would be the second owner, and the two would drift.
    /// </summary>
    [Fact]
    public void TheSwitchIsLiveAndTheLayoutOwnsTheRepaint()
    {
        Assert.Contains(
            "private void OnLanguageChanged(string code) => Localizer.SetLanguage(code);",
            SettingsPage(),
            StringComparison.Ordinal);

        var layout = File.ReadAllText(Path.Combine(
            TranslationParityTests.RepositoryRoot(),
            "RazorReaper", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("@inject ILocalizer Localizer", layout, StringComparison.Ordinal);
        Assert.Contains("Localizer.LanguageChanged += HandleLanguageChanged;", layout, StringComparison.Ordinal);
        Assert.Contains("Localizer.LanguageChanged -= HandleLanguageChanged;", layout, StringComparison.Ordinal);
        Assert.Contains(
            "private void HandleLanguageChanged() => this.DispatchRender(() => InvokeAsync(StateHasChanged));",
            layout,
            StringComparison.Ordinal);
    }
}
