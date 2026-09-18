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
            "<h3>Accent Color</h3>",
            "Recolor the whole app",
            ">Active</span>",
            "\"Collapse\" : \"Expand\"",
            "aria-label=\"Hue\"",
            "aria-label=\"Hex color\"",
            "title=\"Reset to default purple\"",
            "Advanced — override individual shades",
            "\"Auto\" : \"Custom\"",
            ">Reset to auto</button>",
            "Each shade left on",
            "is derived from the accent color",
            "\"light\", \"Light\"",
            "Hover / highlight accents",
            "Gradient end, pressed states",
            "Deep gradient / shadow tone",
            "Subtle tinted text",
            " hex\"",
        })
        {
            data.Add("Components/Shared/AccentColorCard.razor", literal);
        }

        foreach (var literal in new[]
        {
            "<h3>Interface Font</h3>",
            "Switch the UI font preset",
            ">Active</span>",
            "\"Collapse\" : \"Expand\"",
            "The quick brown fox",
            "Manual install (if auto-install fails)",
            "Download the font from the official link below.",
            "If you get a zip, extract it.",
            "<strong>Install</strong>",
            "Return here and select the preset again.",
            "Fonts install per-user in the background.",
            "Description = \"",
            "return \"System\"",
            "\"Installing\"",
            "\"Installed\" : \"Auto-install\"",
            "Free monthly limit reached",
            "$\"Installing {preset.Name}",
            "{preset.Name} installed.",
            "installation pending. Try the preset again",
            "Font package is invalid or blocked",
            "Font files are in use.",
            "Font download failed.",
            "Font installation failed:",
            "Copied download link for",
            "Failed to copy download link.",
        })
        {
            data.Add("Components/Shared/FontSettingsCard.razor", literal);
        }

        foreach (var literal in new[]
        {
            "left this month",
            "Free monthly quota",
        })
        {
            data.Add("Components/Shared/UsageChip.razor", literal);
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
            "placeholder=\"Search pages, locations and commands...\"",
            "=> \"Pages\"",
            "=> \"Locations\"",
            "=> \"Commands\"",
            "new(\"Recent\"",
            "new(\"Jump to\"",
            "Title = page.Label",
        })
        {
            data.Add("Components/Shared/GlobalSearch.razor", literal);
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

        foreach (var literal in new[]
        {
            ">Feedback & Support</h1>",
            "Share an idea or an opinion, or report a problem",
            "A system and feature snapshot is attached when you send your report.",
            "Report ID: @_reportId",
            "\"What went wrong? (required)\"",
            "\"Your feedback (required)\"",
            ">Contact (optional)</label>",
            "Discord or email — an additional way to reach you",
            "\"Send Report\"",
            "\"Send Feedback\"",
            ">Attached automatically</h3>",
            ">Good to know</h3>",
            "<li>Machine name</li>",
            "Answers appear privately in your Inbox.",
        })
        {
            data.Add("Components/Pages/Feedback.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">My account</h1>",
            "Your profile, Discord and RazorReaper installations, together.",
            ">Loading your account…</p>",
            ">Your profile</h3>",
            ">Display name</label>",
            "\"Save profile\"",
            ">Connected accounts</h3>",
            ">Your installations</h3>",
            ">View license</button>",
            ">Redeem key</button>",
            ">Buy Premium</a>",
            ">Sign out</button>",
            "One profile.<br />Every installation.",
            ">Create account with Discord</button>",
            ">Cancel</button>",
            "\"Connect your account\"",
            "\"Is this you?\"",
            "Waiting for Discord authorization",
            "RazorReaper will connect to your Discord identity.",
            "\"Continue with Discord\"",
            "The connection timed out. Please try again.",
            "Your profile has been saved.",
        })
        {
            data.Add("Components/Pages/Account.razor", literal);
        }

        foreach (var literal in new[]
        {
            "aria-label=\"Close\"",
            "title=\"Close (Esc)\"",
            "\"Premium is active\"",
            "\"Unlock Premium\"",
            "<li>Every feature is unlocked on this machine.</li>",
            "\"Included with Premium\"",
            "(\"Auto Clicker\", ",
            "\"Buy / renew\"",
            ">Esc closes this view<",
            ">Your license key<",
            "\"Reveal\"",
            ">Copy</button>",
            "<dd>Bound to this PC</dd>",
            "<dt>Time left</dt>",
            "Your license has expired.",
            ">Renew</a>",
            "\"Verifying…\"",
            "The key is in your order confirmation.",
            "Please enter a license key.",
            "<dd>Not activated</dd>",
            ">Need help?<",
            "License key copied to clipboard.",
            "\"3 Months\"",
            "\"Expired\"",
            "} months\"",
        })
        {
            data.Add("Components/Shared/LicenseOverlay.razor", literal);
        }

        foreach (var literal in new[]
        {
            "aria-label=\"Close\"",
            "title=\"Close (Esc)\"",
            ">Release notes</span>",
            "What's new in v",
            "<strong>Update failed</strong>",
            "is available.\")",
            ">Restarting…</button>",
            "Restart & update to v",
            "\"Downloading…",
            "\"Check again\"",
            ">Full changelog</a>",
            "Release notes appear once the update check has run.",
            "You're up to date",
            "No notes were published for this release.",
            ">Open inbox</a>",
            "Loading your inbox…",
            "No replies yet. Answers to your reports land here.",
            "\"1 unread\"",
            "Support replied · @reply.ReportId",
            ">New</span>",
            "\"just now\"",
        })
        {
            data.Add("Components/Shared/WhatsNewOverlay.razor", literal);
        }

        foreach (var literal in new[]
        {
            "Close ARK (or stop the running macro) first",
            "\"Checking for updates...\"",
            "You're on the latest version.",
            "is ready — restart to install.",
            "is ready — close ARK, then restart to install.",
            "— restarting...\"",
            "it will be applied at the next start.",
            "Update available but download URL is missing.",
            "install it manually.",
            "\"Downloading update...\"",
            // Only the status line; the log template two lines above stays English, as logs do.
            "Update download was incomplete — it will be retried.",
            "\"Download cancelled.\"",
            "\"Failed to download update.\"",
            "could not be installed (installer exit code",
            "Razor Reaper will restart.\"",
        })
        {
            data.Add("Services/Implementations/AutoUpdateManager.cs", literal);
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
    [InlineData("settings.accent.title", "Accent Color")]
    [InlineData("settings.accent.description", "Recolor the whole app. Pick any color and every purple accent updates instantly — your choice is saved until you change it.")]
    [InlineData("settings.accent.advanced.toggle", "Advanced — override individual shades")]
    [InlineData("settings.accent.shade.reset", "Reset to auto")]
    [InlineData("settings.font.title", "Interface Font")]
    [InlineData("settings.font.manual.title", "Manual install (if auto-install fails)")]
    [InlineData("settings.font.status.autoinstall", "Auto-install")]
    [InlineData("usage.remaining", "{0}/{1} left this month")]
    [InlineData("common.active", "Active")]
    [InlineData("settings.updates.description", "Updates install themselves and restart the app. There is no opt-out.")]
    [InlineData("notfound.title", "Page not found")]
    [InlineData("notfound.back", "Back to Home")]
    [InlineData("nav.page.feedback", "Feedback & Support")]
    [InlineData("palette.placeholder", "Search pages, locations and commands...")]
    [InlineData("palette.section.jumpto", "Jump to")]
    [InlineData("license.buy.renew", "Buy / renew")]
    [InlineData("license.buy.premium", "Buy Premium")]
    [InlineData("license.fact.device.bound", "Bound to this PC")]
    [InlineData("whatsnew.title", "What's new in v{0}")]
    [InlineData("whatsnew.uptodate", "You're up to date — v{0}.")]
    [InlineData("whatsnew.restartupdate", "Restart & update to v{0}")]
    [InlineData("whatsnew.checkagain", "Check again")]
    [InlineData("whatsnew.failed", "Update failed")]
    [InlineData("whatsnew.downloading.percent", "Downloading… {0}%")]
    [InlineData("update.gated", "Close ARK (or stop the running macro) first, then restart to update.")]
    [InlineData("update.status.ready", "Update v{0} is ready — restart to install.")]
    [InlineData("update.install.failed.retry", "Update to v{0} could not be installed (installer exit code {1}). Restart & update to try again.")]
    public void TheEnglishWordingIsWhatItWas(string key, string expected)
    {
        Assert.True(TranslationParityTests.Read("en").TryGetValue(key, out var english), $"missing {key}");
        Assert.Equal(expected, english);
    }

    /// <summary>
    /// Two sentences on Settings put one emphasised word in the middle of themselves, and no
    /// dictionary value in the app carries markup — so each is three keys: a lead, the word the
    /// razor file wraps in &lt;strong&gt;, and the tail. The spacing around the word lives in the
    /// lead and the tail, because Chinese wants none where English wants one. That makes a
    /// trimmed value a real bug and an invisible one, so it is pinned here: these are the only
    /// entries in the dictionaries whose leading or trailing space is load-bearing.
    /// </summary>
    [Theory]
    [InlineData("settings.accent.advanced.note.lead", "settings.accent.shade.auto", "settings.accent.advanced.note.rest",
        "Each shade left on Auto is derived from the accent color. Pin a shade to lock it; \"Reset to auto\" hands it back to the accent.")]
    [InlineData("settings.font.manual.install.lead", "settings.font.manual.install.action", "settings.font.manual.install.rest",
        "Right-click the .ttf file and choose Install (or double-click and install).")]
    public void ASentenceSplitAroundAnEmphasisedWordStillReadsAsOneSentence(
        string lead, string word, string rest, string english)
    {
        foreach (var code in new[] { "en", "de", "ru", "zh-Hans" })
        {
            var dictionary = TranslationParityTests.Read(code);
            var joined = dictionary[lead] + dictionary[word] + dictionary[rest];

            Assert.DoesNotContain("  ", joined, StringComparison.Ordinal);
            Assert.DoesNotContain(" ,", joined, StringComparison.Ordinal);
            Assert.Equal(joined.Trim(), joined);
        }

        var en = TranslationParityTests.Read("en");
        Assert.Equal(english, en[lead] + en[word] + en[rest]);
    }

    [Theory]
    [MemberData(nameof(MigratedLiterals))]
    public void AMigratedSurfaceKeepsNoEnglishLiteral(string relativePath, string literal)
    {
        var source = WithoutComments(File.ReadAllText(AppFile(relativePath)));

        Assert.DoesNotContain(literal, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drops razor <c>@* … *@</c> blocks and whole-line C# comments. A comment quoting the
    /// wording it explains — "the primary action becomes 'Restart &amp; update to vX'" — is not a
    /// string the app renders, and making the migration delete those comments would cost the
    /// reader the explanation to satisfy a grep. Only whole comment lines go, never a trailing
    /// <c>//</c>, so a literal cannot hide behind a URL on the same line.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        return string.Join('\n', withoutBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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

    /// <summary>
    /// Every key the app spells out, with the file that spells it. Three shapes: the ordinary
    /// <c>Localizer.T("…")</c>, and the update manager's two, which hold a key and its arguments
    /// so a status line that sits on screen for a session can be re-read after a switch.
    /// </summary>
    private static IEnumerable<(string File, string Key)> UsedKeys()
    {
        var root = Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper");
        // "Localizer.T(" must match, so only a word character in front rules a T( call out.
        var pattern = new Regex(@"(?:(?<!\w)T|SetStatus|new StatusLine)\(\s*""(?<key>[^""]+)""");

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
