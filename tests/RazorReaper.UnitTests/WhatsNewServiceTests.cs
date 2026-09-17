using RazorReaper.Models;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests;

/// <summary>
/// What lights the "new version" dot on the sidebar icon, and what puts it out. The manifest
/// says "1.5.3" while the assembly says 1.5.3.0; <see cref="Version"/> orders those, so the
/// service compares on Major.Minor.Build and persists the seen release as "1.5.3".
/// </summary>
public sealed class WhatsNewServiceTests
{
    private static readonly Version Running = new(1, 5, 2, 0);

    [Fact]
    public void NothingIsNewWithoutACheckResult()
    {
        var service = new WhatsNewService(new FakePreferencesStore());

        Assert.Null(service.UnseenRelease(null, Running));
        Assert.Null(service.UnseenRelease(new UpdateCheckResult { CurrentVersion = Running, ErrorMessage = "offline" }, Running));
    }

    [Fact]
    public void ANewerManifestVersionIsNewUntilMarkedSeen()
    {
        var prefs = new FakePreferencesStore();
        var service = new WhatsNewService(prefs);
        var check = Check(new Version(1, 5, 3));

        var release = service.UnseenRelease(check, Running);
        Assert.Equal(new Version(1, 5, 3), release);

        var raised = 0;
        service.OnStateChanged += () => raised++;
        service.MarkReleaseSeen(release!);

        Assert.Equal(1, raised);
        Assert.Equal("1.5.3", prefs.Peek(WhatsNewService.PrefKeyLastSeenRelease));
        Assert.Null(service.UnseenRelease(check, Running));

        // Marking the same release again is a no-op — no second event, no second write.
        service.MarkReleaseSeen(new Version(1, 5, 3, 0));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void TheRunningVersionsOwnNotesCountAsNewOnceAfterAnUpdate()
    {
        // After a forced update the app restarts on the manifest's version: the dot shows the
        // notes for the build you were just moved to, until they are opened.
        var service = new WhatsNewService(new FakePreferencesStore());
        var check = Check(new Version(1, 5, 2));

        Assert.Equal(new Version(1, 5, 2), service.UnseenRelease(check, Running));
    }

    [Fact]
    public void AManifestBehindTheRunningBuildIsNotNew()
    {
        var service = new WhatsNewService(new FakePreferencesStore());

        Assert.Null(service.UnseenRelease(Check(new Version(1, 5, 1)), Running));
    }

    [Fact]
    public void TheSeenReleaseSurvivesARestart()
    {
        var prefs = new FakePreferencesStore();
        prefs.Seed(WhatsNewService.PrefKeyLastSeenRelease, "1.5.3");
        var service = new WhatsNewService(prefs);

        Assert.Null(service.UnseenRelease(Check(new Version(1, 5, 3)), Running));
        Assert.Equal(new Version(1, 5, 4), service.UnseenRelease(Check(new Version(1, 5, 4)), Running));
    }

    [Fact]
    public void AGarbledPreferenceMeansNothingWasSeen()
    {
        var prefs = new FakePreferencesStore();
        prefs.Seed(WhatsNewService.PrefKeyLastSeenRelease, "latest");
        var service = new WhatsNewService(prefs);

        Assert.Equal(new Version(1, 5, 3), service.UnseenRelease(Check(new Version(1, 5, 3)), Running));
    }

    [Fact]
    public void OpenAndCloseRaiseOnceEach()
    {
        var service = new WhatsNewService(new FakePreferencesStore());
        var raised = 0;
        service.OnStateChanged += () => raised++;

        service.Open();
        service.Open();
        Assert.True(service.IsOpen);
        Assert.Equal(1, raised);

        service.Close();
        service.Close();
        Assert.False(service.IsOpen);
        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData(1, 5, 3, -1, "1.5.3")]
    [InlineData(1, 5, 3, 0, "1.5.3")]
    [InlineData(1, 6, -1, -1, "1.6.0")]
    public void FormatDropsTheRevisionAndKeepsThreeParts(int major, int minor, int build, int revision, string expected)
    {
        var version = build < 0 ? new Version(major, minor)
            : revision < 0 ? new Version(major, minor, build)
            : new Version(major, minor, build, revision);

        Assert.Equal(expected, WhatsNewService.Format(version));
    }

    private static UpdateCheckResult Check(Version latest) => new()
    {
        CurrentVersion = Running,
        LatestVersion = latest,
        HasUpdate = latest > Running,
        Notes = ["Something new"],
    };
}
