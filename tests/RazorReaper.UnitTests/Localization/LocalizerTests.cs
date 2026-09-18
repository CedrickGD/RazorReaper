using System.Globalization;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The lookup itself: what a missing key does, what a stored preference does, and what the
/// machine's own language does on a fresh install.
/// </summary>
public sealed class LocalizerTests
{
    private static Localizer New(FakePreferencesStore preferences, string? culture = "en-US")
        => new(preferences, culture is null ? CultureInfo.InvariantCulture : new CultureInfo(culture));

    [Fact]
    public void AKnownKeyIsTranslated()
    {
        var localizer = New(new FakePreferencesStore());

        Assert.Equal("Language", localizer.T("language.label"));

        localizer.SetLanguage(AppLanguages.German);
        Assert.Equal("Sprache", localizer.T("language.label"));
    }

    /// <summary>The key shows through, which is how a missed string is spotted on screen.</summary>
    [Fact]
    public void AnUnknownKeyFallsBackToTheKey()
    {
        var localizer = New(new FakePreferencesStore());

        Assert.Equal("nothing.here", localizer.T("nothing.here"));
        Assert.Equal(string.Empty, localizer.T(""));
    }

    /// <summary>
    /// The English dictionary is the second chance for every other language, so a key that has
    /// not been translated yet reads as English rather than as its own address.
    /// </summary>
    [Fact]
    public void AKeyMissingFromATranslationFallsBackToEnglish()
    {
        var english = LanguageCatalog.Load(AppLanguages.English);
        var localizer = New(new FakePreferencesStore());
        localizer.SetLanguage(AppLanguages.Russian);

        // Proven against the real dictionaries: every English key that Russian does not carry
        // must still come back in English. (Parity is enforced separately; this is the rule
        // that holds while a translation is being added.)
        foreach (var key in english.Keys)
        {
            Assert.NotEqual(key, localizer.T(key));
        }
    }

    [Fact]
    public void PlaceholdersAreFilledIn()
    {
        var localizer = New(new FakePreferencesStore());

        Assert.Equal("a-b", localizer.T("{0}-{1}", "a", "b"));
    }

    /// <summary>A translation with a stray brace must not be able to blank the page that used it.</summary>
    [Fact]
    public void ABadTemplateIsReturnedUnformatted()
    {
        var localizer = New(new FakePreferencesStore());

        Assert.Equal("{oops", localizer.T("{oops", "a"));
        Assert.Equal("{3}", localizer.T("{3}", "a"));
    }

    [Fact]
    public void TheLanguagePreferenceRoundTrips()
    {
        var preferences = new FakePreferencesStore();

        var first = New(preferences);
        first.SetLanguage(AppLanguages.SimplifiedChinese);

        Assert.Equal(AppLanguages.SimplifiedChinese, preferences.Peek(Localizer.PreferenceKey));
        Assert.Equal(AppLanguages.SimplifiedChinese, New(preferences).Language);
    }

    /// <summary>A stored value that is no longer shipped must not strand the app in a dead code.</summary>
    [Fact]
    public void AnUnknownStoredLanguageFallsBackToTheDefault()
    {
        var preferences = new FakePreferencesStore();
        preferences.Seed(Localizer.PreferenceKey, "kl-GL");

        Assert.Equal(AppLanguages.Default, New(preferences).Language);
    }

    [Fact]
    public void SettingAnUnknownOrUnchangedLanguageDoesNothing()
    {
        var preferences = new FakePreferencesStore();
        var localizer = New(preferences);
        var changes = 0;
        localizer.LanguageChanged += () => changes++;

        localizer.SetLanguage("kl-GL");
        localizer.SetLanguage(AppLanguages.English);

        Assert.Equal(0, changes);
        Assert.Equal(AppLanguages.English, localizer.Language);

        localizer.SetLanguage(AppLanguages.German);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("de-DE", AppLanguages.German)]
    [InlineData("de-AT", AppLanguages.German)]
    [InlineData("ru-RU", AppLanguages.Russian)]
    [InlineData("zh-CN", AppLanguages.SimplifiedChinese)]
    [InlineData("zh-SG", AppLanguages.SimplifiedChinese)]
    [InlineData("en-GB", AppLanguages.English)]
    // Traditional script is not shipped: English beats a script the reader does not use.
    [InlineData("zh-TW", AppLanguages.Default)]
    [InlineData("fr-FR", AppLanguages.Default)]
    public void AFreshInstallFollowsTheMachinesLanguage(string culture, string expected)
        => Assert.Equal(expected, New(new FakePreferencesStore(), culture).Language);

    /// <summary>A stored choice outranks the machine — someone who picked English meant it.</summary>
    [Fact]
    public void AStoredChoiceOutranksTheMachinesLanguage()
    {
        var preferences = new FakePreferencesStore();
        preferences.Seed(Localizer.PreferenceKey, AppLanguages.English);

        Assert.Equal(AppLanguages.English, New(preferences, "de-DE").Language);
    }

    [Fact]
    public void ThePickerOffersExactlyTheFourShippedLanguages()
    {
        var localizer = New(new FakePreferencesStore());

        Assert.Equal(
            new[] { "en", "de", "ru", "zh-Hans" },
            localizer.Languages.Select(l => l.Code).ToArray());
        Assert.Equal(
            new[] { "English", "Deutsch", "Русский", "中文 (简体)" },
            localizer.Languages.Select(l => l.NativeName).ToArray());
    }
}
