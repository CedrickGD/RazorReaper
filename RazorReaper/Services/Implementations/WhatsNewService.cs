using RazorReaper.Diagnostics;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

public sealed class WhatsNewService : IWhatsNewService
{
    /// <summary>"1.5.3" — the last release whose notes were opened in the overlay.</summary>
    internal const string PrefKeyLastSeenRelease = "rr.whatsnew.lastseenrelease";

    private readonly IPreferencesStore _preferences;
    private Version? _lastSeen;
    private bool _lastSeenLoaded;

    public WhatsNewService(IPreferencesStore preferences)
    {
        _preferences = preferences;
    }

    public bool IsOpen { get; private set; }

    public event Action? OnStateChanged;

    public void Open() => SetOpen(open: true);

    public void Close() => SetOpen(open: false);

    public Version? UnseenRelease(UpdateCheckResult? lastCheck, Version current)
    {
        var latest = lastCheck?.LatestVersion;
        if (latest is null)
        {
            return null;
        }

        var release = Normalize(latest);
        if (release < Normalize(current))
        {
            return null;
        }

        return release == LastSeen ? null : release;
    }

    public void MarkReleaseSeen(Version release)
    {
        var normalized = Normalize(release);
        if (normalized == LastSeen)
        {
            return;
        }

        _lastSeen = normalized;
        _lastSeenLoaded = true;
        _preferences.Set(PrefKeyLastSeenRelease, Format(normalized));
        Raise();
    }

    private Version? LastSeen
    {
        get
        {
            if (!_lastSeenLoaded)
            {
                _lastSeenLoaded = true;
                var raw = _preferences.Get(PrefKeyLastSeenRelease, string.Empty);
                _lastSeen = Version.TryParse(raw, out var stored) ? Normalize(stored) : null;
            }

            return _lastSeen;
        }
    }

    /// <summary>
    /// Major.Minor.Build with no revision: the manifest says "1.5.3", the assembly says 1.5.3.0,
    /// and <see cref="Version"/> would otherwise order the former <i>below</i> the latter.
    /// </summary>
    internal static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>The label the sidebar and the overlay show for a release: "1.5.3".</summary>
    public static string Format(Version version) => AppVersionInfo.FormatVersion(Normalize(version));

    private void SetOpen(bool open)
    {
        if (IsOpen == open)
        {
            return;
        }

        IsOpen = open;
        Raise();
    }

    private void Raise()
    {
        try
        {
            OnStateChanged?.Invoke();
        }
        catch
        {
            // A subscriber must not break the state; the components re-render through the gate.
        }
    }
}
