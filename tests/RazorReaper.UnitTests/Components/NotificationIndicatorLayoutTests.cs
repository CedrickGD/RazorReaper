using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// The sidebar's Inbox link became a notification icon on the tier row with a "What's new &amp;
/// inbox" overlay behind it. These pin the shape: the icon in the tier row and nothing left of
/// the link, the dot and what lights it, the poll it inherited, the plop and its gating, and the
/// overlay mounted, registered and linked like the license one — with no motion of its own.
/// </summary>
public sealed class NotificationIndicatorLayoutTests
{
    [Fact]
    public void TheIconLivesInTheTierRowAndTheInboxLinkIsGone()
    {
        var navbar = File.ReadAllText(ComponentPath("Shared", "SharedNavbar.razor"));

        var row = Regex.Match(navbar, @"<div class=""nav-status-row"">(?<body>.*?)</div>\s*<div class=""nav-status-meta"">", RegexOptions.Singleline);
        Assert.True(row.Success, "The tier button and the icon must share a .nav-status-row above the version line.");

        var body = row.Groups["body"].Value;
        var tier = body.IndexOf("class=\"nav-status-line\"", StringComparison.Ordinal);
        var icon = body.IndexOf("<NotificationIndicator />", StringComparison.Ordinal);
        Assert.True(tier >= 0, "The tier button stays in the row.");
        Assert.True(icon > tier, "The icon sits to the right of the tier label.");

        Assert.DoesNotContain("SupportInboxLink", navbar, StringComparison.Ordinal);
        Assert.False(File.Exists(ComponentPath("Shared", "SupportInboxLink.razor")), "SupportInboxLink.razor must be deleted, not left unreferenced.");

        var stillReferencing = Directory.GetFiles(Path.Combine(RepositoryRoot(), "RazorReaper"), "*.razor", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("SupportInboxLink", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Empty(stillReferencing);
    }

    [Fact]
    public void TheDotSaysWhatIsNewAndComesFromBothSources()
    {
        var indicator = File.ReadAllText(ComponentPath("Shared", "NotificationIndicator.razor"));

        Assert.Contains("class=\"nav-notify-dot\"", indicator, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Tooltip\"", indicator, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"dialog\"", indicator, StringComparison.Ordinal);
        Assert.Contains("Inbox.UnreadCount", indicator, StringComparison.Ordinal);
        Assert.Contains("WhatsNew.UnseenRelease(AutoUpdateManager.LastCheckResult, UpdateService.CurrentVersion)", indicator, StringComparison.Ordinal);
        Assert.Contains("unread replies", indicator, StringComparison.Ordinal);
        Assert.Contains("new version {label}", indicator, StringComparison.Ordinal);
        Assert.Contains("WhatsNew.Open()", indicator, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIconOwnsThePollAndTakesItDownWithItself()
    {
        var indicator = File.ReadAllText(ComponentPath("Shared", "NotificationIndicator.razor"));

        // The RR-E1003-safe loop, as it was on the link.
        Assert.Contains("_ = PollAsync(_stop.Token);", indicator, StringComparison.Ordinal);
        Assert.Contains("new PeriodicTimer(TimeSpan.FromSeconds(20))", indicator, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) { }", indicator, StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException) { }", indicator, StringComparison.Ordinal);
        Assert.Contains("_stop.Cancel();", indicator, StringComparison.Ordinal);
        Assert.DoesNotContain("_stop.Dispose()", indicator, StringComparison.Ordinal);

        // Everything subscribed comes off again, through the gate.
        Assert.Contains("this.StopRenderDispatch();", indicator, StringComparison.Ordinal);
        Assert.Contains("Inbox.Changed -=", indicator, StringComparison.Ordinal);
        Assert.Contains("AutoUpdateManager.StateChanged -=", indicator, StringComparison.Ordinal);
        Assert.Contains("WhatsNew.OnStateChanged -=", indicator, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlopPlaysOnlyForANewReplyAndUnderTheSoundToggles()
    {
        var indicator = File.ReadAllText(ComponentPath("Shared", "NotificationIndicator.razor"));
        Assert.Contains("JS.InvokeVoidAsync(\"playInboxPlop\")", indicator, StringComparison.Ordinal);
        // Baseline first, sound only on an increase above it.
        Assert.Contains("_knownUnread is int known && unread > known", indicator, StringComparison.Ordinal);

        var index = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "index.html"));
        Assert.Contains("<audio id=\"inbox-plop-audio\" src=\"assets/sounds/inbox-plop.wav\"", index, StringComparison.Ordinal);

        // The baseline is seeded at mount when the inbox has already loaded (a re-mounted navbar),
        // and stays null on the first mount of a session.
        Assert.Contains("_knownUnread = Inbox.HasLoaded ? Inbox.UnreadCount : null;", indicator, StringComparison.Ordinal);

        var helper = index.IndexOf("window.playInboxPlop = function", StringComparison.Ordinal);
        Assert.True(helper >= 0, "index.html must define window.playInboxPlop.");
        var helperBody = index[helper..index.IndexOf("};", helper, StringComparison.Ordinal)];
        // Gated exactly like playNotificationSound: the master toggle and the notification toggle.
        Assert.Contains("!settings.enabled || !settings.notificationEnabled", helperBody, StringComparison.Ordinal);
        Assert.Contains("getElementById('inbox-plop-audio')", helperBody, StringComparison.Ordinal);
        Assert.Contains("playAudioElement(audio, settings.notificationVolume", helperBody, StringComparison.Ordinal);

        var asset = Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "assets", "sounds", "inbox-plop.wav");
        Assert.True(File.Exists(asset), "The plop asset ships next to notify.mp3 and globalclick.mp3.");
        using var stream = File.OpenRead(asset);
        var header = new byte[12];
        Assert.Equal(12, stream.Read(header, 0, 12));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
    }

    /// <summary>
    /// The one place the owner wants motion is the dot. The glyph, the button and the dot
    /// itself hold still; only the dot's ring pulses, a finite number of times, and not at all
    /// under reduced motion.
    /// </summary>
    [Fact]
    public void OnlyTheDotsRingMovesAndOnlyForAWhile()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "navbar.css"));

        var start = css.IndexOf(".nav-notify {", StringComparison.Ordinal);
        var ring = css.IndexOf(".nav-notify-dot::after {", start, StringComparison.Ordinal);
        Assert.True(start > 0);
        Assert.True(ring > start);
        Assert.Empty(MotionProperties(css[start..ring]));

        var pulse = Regex.Match(css, @"animation:\s*nav-notify-pulse\s+[\d.]+s\s+[\w-]+\s+(?<count>\d+)\s*;");
        Assert.True(pulse.Success, "The ring's animation must name a finite iteration count.");
        Assert.InRange(int.Parse(pulse.Groups["count"].Value), 1, 5);
        Assert.Contains("@keyframes nav-notify-pulse", css, StringComparison.Ordinal);

        var reduced = css.IndexOf("@media (prefers-reduced-motion: reduce)", ring, StringComparison.Ordinal);
        Assert.True(reduced > ring);
        Assert.Contains("animation: none;", css[reduced..], StringComparison.Ordinal);

        // The glyph stays muted with news: only the dot signals, nothing brightens the bell.
        Assert.DoesNotContain(".nav-notify.has-news", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayIsMountedRegisteredAndLinked()
    {
        var layout = File.ReadAllText(ComponentPath("Layout", "MainLayout.razor"));
        Assert.Contains("<WhatsNewOverlay />", layout, StringComparison.Ordinal);

        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "MauiProgram.cs"));
        Assert.Contains("services.AddSingleton<IWhatsNewService, WhatsNewService>();", program, StringComparison.Ordinal);

        var index = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "index.html"));
        Assert.Contains("css/shared/whats-new-overlay.css", index, StringComparison.Ordinal);

        var js = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "js", "license-overlay.js"));
        Assert.Contains("window.razorReaperWhatsNewOverlay = createOverlayRuntime('whats-new-overlay-open')", js, StringComparison.Ordinal);

        var overlay = File.ReadAllText(ComponentPath("Shared", "WhatsNewOverlay.razor"));
        Assert.Contains("razorReaperWhatsNewOverlay.open", overlay, StringComparison.Ordinal);
        Assert.Contains("razorReaperWhatsNewOverlay.close", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card hugs its content instead of stretching to the window around a void; the notes
    /// keep a reading measure; the inbox is a preview of three, not a second inbox.
    /// </summary>
    [Fact]
    public void TheWhatsNewFrameHugsItsContent()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "whats-new-overlay.css"));

        var root = css.IndexOf(".whats-new-overlay {", StringComparison.Ordinal);
        Assert.Contains("align-items: center;", css[root..css.IndexOf('}', root)], StringComparison.Ordinal);
        var frame = css.IndexOf(".whats-new-frame {", StringComparison.Ordinal);
        Assert.Contains("max-height: 100%;", css[frame..css.IndexOf('}', frame)], StringComparison.Ordinal);
        var note = css.IndexOf(".whats-new-notes li {", StringComparison.Ordinal);
        Assert.Contains("max-width: 68ch;", css[note..css.IndexOf('}', note)], StringComparison.Ordinal);

        var overlay = File.ReadAllText(ComponentPath("Shared", "WhatsNewOverlay.razor"));
        Assert.Contains("private const int MaxPreview = 3;", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayShowsTheNotesTheUpdateActionAndTheInbox()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "WhatsNewOverlay.razor"));

        // Same frame family as the license overlay.
        Assert.Contains("role=\"dialog\"", overlay, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Close\"", overlay, StringComparison.Ordinal);
        Assert.Contains("e.Key == \"Escape\"", overlay, StringComparison.Ordinal);

        // What's new on top: the manifest notes, the two states, the update action on the manager's own path.
        Assert.Contains("What's new in v", overlay, StringComparison.Ordinal);
        Assert.Contains("You're up to date", overlay, StringComparison.Ordinal);
        Assert.Contains("Update now", overlay, StringComparison.Ordinal);
        Assert.Contains("AutoUpdateManager.CheckNowAsync()", overlay, StringComparison.Ordinal);
        Assert.Contains("check.Notes", overlay, StringComparison.Ordinal);
        Assert.Contains("check.ChangelogUrl", overlay, StringComparison.Ordinal);
        Assert.Contains("Overlay.MarkReleaseSeen(", overlay, StringComparison.Ordinal);

        // Inbox below: the latest replies, each into the inbox on that reply.
        Assert.Contains("Inbox.Replies.Take(MaxPreview)", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"/inbox\"", overlay, StringComparison.Ordinal);
        Assert.Contains("/inbox?open=", overlay, StringComparison.Ordinal);

        // Subscriptions come off again, through the gate.
        Assert.Contains("this.StopRenderDispatch();", overlay, StringComparison.Ordinal);
        Assert.Contains("Overlay.OnStateChanged -=", overlay, StringComparison.Ordinal);
        Assert.Contains("AutoUpdateManager.StateChanged -=", overlay, StringComparison.Ordinal);
        Assert.Contains("Inbox.Changed -=", overlay, StringComparison.Ordinal);

        var inbox = File.ReadAllText(ComponentPath("Pages", "SupportInbox.razor"));
        Assert.Contains("[SupplyParameterFromQuery(Name = \"open\")]", inbox, StringComparison.Ordinal);
        Assert.Contains("Inbox.MarkReadAsync(id)", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManagerOffersTheSamePassOnDemand()
    {
        var contract = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Services", "IAutoUpdateManager.cs"));
        Assert.Contains("Task CheckNowAsync(CancellationToken cancellationToken = default);", contract, StringComparison.Ordinal);

        var manager = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Services", "Implementations", "AutoUpdateManager.cs"));
        var method = manager.IndexOf("public async Task CheckNowAsync(", StringComparison.Ordinal);
        Assert.True(method >= 0);
        var body = manager[method..manager.IndexOf("StartRecurringChecks()", method, StringComparison.Ordinal)];
        Assert.Contains("if (isChecking || isInstallerReady || isDownloading) return;", body, StringComparison.Ordinal);
        Assert.Contains("await CheckAndInstallAsync(cancellationToken);", body, StringComparison.Ordinal);
    }

    private static string[] MotionProperties(string css)
        => Regex.Matches(css, @"(?<![\w-])(transform|animation|transition)(?:-[\w-]+)?\s*:", RegexOptions.IgnoreCase)
            .Select(match => match.Value.Trim())
            .ToArray();

    private static string ComponentPath(string folder, string file)
        => Path.Combine(RepositoryRoot(), "RazorReaper", "Components", folder, file);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
