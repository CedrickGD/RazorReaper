using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Overlay;

/// <summary>Connection state of the Notifier stream client.</summary>
public enum NotifierConnectionState
{
    /// <summary>Not connected and not trying to.</summary>
    Disconnected,
    /// <summary>Attempting to open (or re-open) the stream.</summary>
    Connecting,
    /// <summary>Stream is open and receiving.</summary>
    Connected,
    /// <summary>Last attempt failed; the client is backing off before another try.</summary>
    Error
}

/// <summary>Category of an incoming notifier alert. Drives filtering, HUD severity and the page chip.</summary>
public enum NotifierAlertType
{
    RareDino,
    Resource,
    Osd,
    ElementNode,
    TribeLog
}

/// <summary>Which app sound plays for an alert type. Maps to the existing JS sound helpers.</summary>
public enum NotifierSound
{
    /// <summary>Silent.</summary>
    None,
    /// <summary>The app's notification sound (window.playNotificationSound).</summary>
    Notification,
    /// <summary>The app's UI click sound (window.playClickSound).</summary>
    Click
}

/// <summary>Static metadata for one alert type, so the page and service share one source of labels.</summary>
public sealed record NotifierAlertTypeDef(NotifierAlertType Type, string Key, string Label, string Description);

/// <summary>One rare/notable species the rare-dino filter can whitelist. Facts only.</summary>
public sealed record RareDinoSpecies(string Id, string Name, string Category);

/// <summary>A named cluster the user can enable/disable and rename. Persisted as JSON.</summary>
public sealed class NotifierCluster
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A channel the backend is watching. Managed live from the app via the backend's
/// /notifier/channels endpoint — no Railway edit, no redeploy.</summary>
public sealed record NotifierWatchedChannel(string ChannelId, string Cluster, string Type);

/// <summary>A parsed alert that passed the user's filters (or a local test).</summary>
public sealed record NotifierAlert(
    NotifierAlertType Type,
    string? Subject,
    string Text,
    string? Cluster,
    DateTime TimestampUtc);

/// <summary>
/// Client for the (separate) Notifier backend: opens a server-sent-events / HTTP stream, parses
/// alerts, applies the user's filters, and forwards survivors to the HUD overlay + an in-page list.
/// Client and protocol only — there is no Discord login and no backend shipped here. Until a backend
/// URL is configured and online the client sits cleanly Disconnected and never throws. A singleton:
/// it keeps its connection and filters when the user navigates away.
/// </summary>
public interface INotifierClientService : IDisposable
{
    /// <summary>Fired on any state, filter or recent-list change. May fire on a background thread.</summary>
    event Action? Changed;

    /// <summary>Fired for each alert that passes filters (and for tests), so a mounted page can play sound.</summary>
    event Action<NotifierAlert>? AlertReceived;

    NotifierConnectionState State { get; }

    /// <summary>Human-readable status line (last error, "No endpoint configured", …).</summary>
    string StateDetail { get; }

    /// <summary>Configured stream endpoint (empty = none). Persisted under "notifier.endpoint".</summary>
    string Endpoint { get; }
    void SetEndpoint(string url);

    /// <summary>Recent received/test alerts, newest first.</summary>
    IReadOnlyList<NotifierAlert> RecentAlerts { get; }

    /// <summary>The four alert types with their display labels.</summary>
    IReadOnlyList<NotifierAlertTypeDef> TypeDefs { get; }

    /// <summary>Built-in rare/notable species list for the whitelist grid.</summary>
    IReadOnlyList<RareDinoSpecies> Species { get; }

    /// <summary>Editable clusters.</summary>
    IReadOnlyList<NotifierCluster> Clusters { get; }

    bool IsTypeEnabled(NotifierAlertType type);
    void SetTypeEnabled(NotifierAlertType type, bool enabled);
    NotifierSound GetTypeSound(NotifierAlertType type);
    void SetTypeSound(NotifierAlertType type, NotifierSound sound);

    bool IsSpeciesEnabled(string id);
    void SetSpeciesEnabled(string id, bool enabled);
    void SetAllSpecies(bool enabled);

    void SetClusterEnabled(string id, bool enabled);
    void SetClusterLabel(string id, string label);

    /// <summary>Trigger phrases for tribe-log alerts. When non-empty, a tribe-log alert only
    /// passes if its subject or text contains one of these (case-insensitive). Empty = all pass.</summary>
    IReadOnlyList<string> TribeTriggers { get; }
    void SetTribeTriggers(IEnumerable<string> phrases);

    /// <summary>List the channels the backend is currently watching. Requires a configured endpoint.</summary>
    Task<IReadOnlyList<NotifierWatchedChannel>> GetWatchedChannelsAsync(CancellationToken ct = default);
    /// <summary>Add or update a watched channel on the backend. Applies live (no redeploy). Returns the new list.</summary>
    Task<IReadOnlyList<NotifierWatchedChannel>> AddWatchedChannelAsync(string channelId, string cluster, string type, CancellationToken ct = default);
    /// <summary>Remove a watched channel from the backend. Applies live. Returns the new list.</summary>
    Task<IReadOnlyList<NotifierWatchedChannel>> RemoveWatchedChannelAsync(string channelId, CancellationToken ct = default);

    /// <summary>Begin connecting (no-op with a clear status when no endpoint is set).</summary>
    void Start();
    /// <summary>Stop and stay disconnected.</summary>
    void Stop();

    /// <summary>Fire a local test alert (cycles the four types) through the real pipeline.</summary>
    void TestAlert();
    /// <summary>Fire a local test alert of a specific type through the real pipeline.</summary>
    void TestAlert(NotifierAlertType type);
}

/// <summary>Default <see cref="INotifierClientService"/> implementation.</summary>
public sealed class NotifierClientService : INotifierClientService
{
    private const string EndpointKey = "notifier.endpoint";
    private const string SpeciesKey = "notifier.species";
    private const string ClustersKey = "notifier.clusters";
    private const string TribeTriggersKey = "notifier.tribetriggers";
    private const int RecentCapacity = 40;
    private const int MaxBackoffSeconds = 30;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IHudOverlayService _hud;
    private readonly ILogger<NotifierClientService> _logger;
    private readonly HttpClient _http;

    private readonly object _gate = new();
    private readonly List<NotifierAlert> _recent = new();
    private readonly Dictionary<NotifierAlertType, bool> _typeEnabled = new();
    private readonly Dictionary<NotifierAlertType, NotifierSound> _typeSound = new();
    private readonly HashSet<string> _enabledSpecies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NotifierCluster> _clusters = new();
    private readonly List<string> _tribeTriggers = new();

    private string _endpoint = "";
    private NotifierConnectionState _state = NotifierConnectionState.Disconnected;
    private string _stateDetail = "Disconnected.";
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _testCounter;
    private bool _disposed;

    public event Action? Changed;
    public event Action<NotifierAlert>? AlertReceived;

    // ── Static metadata ──────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<NotifierAlertTypeDef> TypeDefsStatic = new List<NotifierAlertTypeDef>
    {
        new(NotifierAlertType.RareDino, "rare-dino", "Rare dinos",
            "Wild spawns from the whitelist below."),
        new(NotifierAlertType.Resource, "resource", "Resources",
            "Harvestable nodes and gatherables of note."),
        new(NotifierAlertType.ElementNode, "element-node", "Element nodes",
            "Element veins and charge nodes coming online."),
        new(NotifierAlertType.Osd, "osd", "OSD / events",
            "Orbital supply drops and timed server events."),
        new(NotifierAlertType.TribeLog, "tribe-log", "Tribe log",
            "Tribe-log events matching your trigger phrases below."),
    };

    // 24 rare / aberrant / tek / notable ARK: Survival Evolved species — facts only.
    private static readonly IReadOnlyList<RareDinoSpecies> SpeciesStatic = new List<RareDinoSpecies>
    {
        new("reaper-king", "Reaper King", "Aberration"),
        new("rock-drake", "Rock Drake", "Aberration"),
        new("basilisk", "Basilisk", "Aberration"),
        new("karkinos", "Karkinos", "Aberration"),
        new("ravager", "Ravager", "Aberration"),
        new("roll-rat", "Roll Rat", "Aberration"),
        new("fire-wyvern", "Fire Wyvern", "Wyvern"),
        new("lightning-wyvern", "Lightning Wyvern", "Wyvern"),
        new("poison-wyvern", "Poison Wyvern", "Wyvern"),
        new("ice-wyvern", "Ice Wyvern", "Wyvern"),
        new("griffin", "Griffin", "Notable"),
        new("deinonychus", "Deinonychus", "Notable"),
        new("managarmr", "Managarmr", "Extinction"),
        new("snow-owl", "Snow Owl", "Extinction"),
        new("velonasaur", "Velonasaur", "Extinction"),
        new("gasbags", "Gasbags", "Extinction"),
        new("gacha", "Gacha", "Extinction"),
        new("giganotosaurus", "Giganotosaurus", "Notable"),
        new("yutyrannus", "Yutyrannus", "Notable"),
        new("therizinosaurus", "Therizinosaurus", "Notable"),
        new("phoenix", "Phoenix", "Notable"),
        new("unicorn", "Unicorn", "Notable"),
        new("tek-rex", "Tek Rex", "Tek"),
        new("tek-quetzal", "Tek Quetzal", "Tek"),
    };

    public IReadOnlyList<NotifierAlertTypeDef> TypeDefs => TypeDefsStatic;
    public IReadOnlyList<RareDinoSpecies> Species => SpeciesStatic;

    // ── Construction / persistence ────────────────────────────────────────────────────────────

    public NotifierClientService(IHudOverlayService hud, ILogger<NotifierClientService> logger)
    {
        _hud = hud;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        LoadSettings();
        // Intentionally does not auto-connect: there is no shipped backend, and the user opts in.
        _stateDetail = string.IsNullOrWhiteSpace(_endpoint)
            ? "No endpoint configured — a backend is required."
            : "Disconnected.";
    }

    private void LoadSettings()
    {
        try
        {
            _endpoint = NotifierEndpointMigration.Migrate(Preferences.Get(EndpointKey, "") ?? "");
            if (_endpoint != (Preferences.Get(EndpointKey, "") ?? ""))
            {
                try { Preferences.Set(EndpointKey, _endpoint); } catch { /* best effort */ }
            }

            foreach (var def in TypeDefsStatic)
            {
                _typeEnabled[def.Type] = Preferences.Get($"notifier.type.{def.Key}", true);
                var soundName = Preferences.Get($"notifier.sound.{def.Key}", NotifierSound.Notification.ToString());
                _typeSound[def.Type] = Enum.TryParse<NotifierSound>(soundName, out var s) ? s : NotifierSound.Notification;
            }

            var speciesCsv = Preferences.Get(SpeciesKey, "");
            if (string.IsNullOrEmpty(speciesCsv))
            {
                // Default: every species enabled.
                foreach (var sp in SpeciesStatic) _enabledSpecies.Add(sp.Id);
            }
            else
            {
                foreach (var id in speciesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    _enabledSpecies.Add(id);
            }

            var clustersJson = Preferences.Get(ClustersKey, "");
            List<NotifierCluster>? loaded = null;
            if (!string.IsNullOrWhiteSpace(clustersJson))
            {
                try { loaded = JsonSerializer.Deserialize<List<NotifierCluster>>(clustersJson); }
                catch { loaded = null; }
            }
            if (loaded is { Count: > 0 })
            {
                _clusters.AddRange(loaded.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id)));
            }
            else
            {
                _clusters.Add(new NotifierCluster { Id = "cluster-1", Label = "Cluster 1", Enabled = true });
                _clusters.Add(new NotifierCluster { Id = "cluster-2", Label = "Cluster 2", Enabled = true });
                _clusters.Add(new NotifierCluster { Id = "cluster-3", Label = "Cluster 3", Enabled = true });
            }

            var triggersRaw = Preferences.Get(TribeTriggersKey, "") ?? "";
            foreach (var t in triggersRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _tribeTriggers.Add(t);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Notifier settings — using defaults");
        }
    }

    private void SaveSpecies()
    {
        try { Preferences.Set(SpeciesKey, string.Join(',', _enabledSpecies)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Notifier species filter"); }
    }

    private void SaveClusters()
    {
        try { Preferences.Set(ClustersKey, JsonSerializer.Serialize(_clusters, JsonOpts)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Notifier clusters"); }
    }

    private void SaveTribeTriggers()
    {
        try { Preferences.Set(TribeTriggersKey, string.Join('\n', _tribeTriggers)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Notifier tribe triggers"); }
    }

    // ── State exposure ────────────────────────────────────────────────────────────────────────

    public NotifierConnectionState State { get { lock (_gate) return _state; } }
    public string StateDetail { get { lock (_gate) return _stateDetail; } }
    public string Endpoint { get { lock (_gate) return _endpoint; } }

    public IReadOnlyList<NotifierAlert> RecentAlerts
    {
        get { lock (_gate) return _recent.AsEnumerable().Reverse().ToList(); }
    }

    public IReadOnlyList<NotifierCluster> Clusters
    {
        get { lock (_gate) return _clusters.Select(Clone).ToList(); }
    }

    private static NotifierCluster Clone(NotifierCluster c) =>
        new() { Id = c.Id, Label = c.Label, Enabled = c.Enabled };

    public bool IsTypeEnabled(NotifierAlertType type)
    {
        lock (_gate) return _typeEnabled.TryGetValue(type, out var v) && v;
    }

    public NotifierSound GetTypeSound(NotifierAlertType type)
    {
        lock (_gate) return _typeSound.TryGetValue(type, out var v) ? v : NotifierSound.Notification;
    }

    public bool IsSpeciesEnabled(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate) return _enabledSpecies.Contains(id);
    }

    // ── Filter mutation ───────────────────────────────────────────────────────────────────────

    public void SetEndpoint(string url)
    {
        var trimmed = NotifierEndpointMigration.Migrate((url ?? "").Trim());
        lock (_gate)
        {
            if (_endpoint == trimmed) return;
            _endpoint = trimmed;
            if (_state is NotifierConnectionState.Disconnected or NotifierConnectionState.Error)
                _stateDetail = string.IsNullOrWhiteSpace(trimmed)
                    ? "No endpoint configured — a backend is required."
                    : "Disconnected.";
        }
        try { Preferences.Set(EndpointKey, trimmed); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Notifier endpoint"); }
        RaiseChanged();
    }

    public void SetTypeEnabled(NotifierAlertType type, bool enabled)
    {
        lock (_gate) _typeEnabled[type] = enabled;
        try { Preferences.Set($"notifier.type.{KeyOf(type)}", enabled); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Notifier type toggle"); }
        RaiseChanged();
    }

    public void SetTypeSound(NotifierAlertType type, NotifierSound sound)
    {
        lock (_gate) _typeSound[type] = sound;
        try { Preferences.Set($"notifier.sound.{KeyOf(type)}", sound.ToString()); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Notifier type sound"); }
        RaiseChanged();
    }

    public void SetSpeciesEnabled(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            if (enabled) _enabledSpecies.Add(id);
            else _enabledSpecies.Remove(id);
        }
        SaveSpecies();
        RaiseChanged();
    }

    public void SetAllSpecies(bool enabled)
    {
        lock (_gate)
        {
            _enabledSpecies.Clear();
            if (enabled) foreach (var sp in SpeciesStatic) _enabledSpecies.Add(sp.Id);
        }
        SaveSpecies();
        RaiseChanged();
    }

    public void SetClusterEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var c = _clusters.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            c.Enabled = enabled;
        }
        SaveClusters();
        RaiseChanged();
    }

    public void SetClusterLabel(string id, string label)
    {
        lock (_gate)
        {
            var c = _clusters.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            c.Label = string.IsNullOrWhiteSpace(label) ? id : label.Trim();
        }
        SaveClusters();
        RaiseChanged();
    }

    public IReadOnlyList<string> TribeTriggers
    {
        get { lock (_gate) return _tribeTriggers.ToList(); }
    }

    public void SetTribeTriggers(IEnumerable<string> phrases)
    {
        lock (_gate)
        {
            _tribeTriggers.Clear();
            foreach (var p in phrases ?? Enumerable.Empty<string>())
            {
                var t = p?.Trim();
                if (!string.IsNullOrEmpty(t) && !_tribeTriggers.Contains(t, StringComparer.OrdinalIgnoreCase))
                    _tribeTriggers.Add(t);
            }
        }
        SaveTribeTriggers();
        RaiseChanged();
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_loop is { IsCompleted: false }) return; // already running

            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                _state = NotifierConnectionState.Disconnected;
                _stateDetail = "No endpoint configured — a backend is required.";
                RaiseChangedNoLock();
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _state = NotifierConnectionState.Connecting;
            _stateDetail = $"Connecting to {HostOf(_endpoint)}…";
            _loop = Task.Run(() => RunLoopAsync(token), token);
        }
        RaiseChanged();
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _state = NotifierConnectionState.Disconnected;
            _stateDetail = string.IsNullOrWhiteSpace(_endpoint)
                ? "No endpoint configured — a backend is required."
                : "Disconnected.";
        }
        try { cts?.Cancel(); } catch { /* ignore */ }
        RaiseChanged();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            string url;
            lock (_gate) url = _endpoint;
            if (string.IsNullOrWhiteSpace(url))
            {
                SetState(NotifierConnectionState.Disconnected, "No endpoint configured — a backend is required.");
                return;
            }

            try
            {
                SetState(NotifierConnectionState.Connecting, $"Connecting to {HostOf(url)}…");

                // Bound the connect (headers) phase so an unreachable host can't hang forever;
                // once streaming, reads are governed only by the outer cancellation token.
                HttpResponseMessage resp;
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                    resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                        .ConfigureAwait(false);
                }
                using var respScope = resp;
                resp.EnsureSuccessStatusCode();

                SetState(NotifierConnectionState.Connected, $"Streaming from {HostOf(url)}");
                attempt = 0;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var buffer = new StringBuilder();

                string? line;
                while (!ct.IsCancellationRequested &&
                       (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    ProcessLine(line, buffer);
                }
                // Stream closed by the server — fall through to backoff and reconnect.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(NotifierConnectionState.Error, $"{ReasonOf(ex)} — retrying…");
            }

            if (ct.IsCancellationRequested) break;

            attempt++;
            var seconds = Math.Min(MaxBackoffSeconds, (int)Math.Pow(2, Math.Min(attempt, 5)));
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        if (ct.IsCancellationRequested)
            SetState(NotifierConnectionState.Disconnected, "Disconnected.");
    }

    private void ProcessLine(string line, StringBuilder buffer)
    {
        if (line.Length == 0)
        {
            if (buffer.Length > 0)
            {
                var payload = buffer.ToString();
                buffer.Clear();
                HandlePayload(payload);
            }
            return;
        }

        if (line[0] == ':') return; // SSE comment / heartbeat

        if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var data = line.Substring(5).TrimStart();
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(data);
            return;
        }

        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase))
            return; // other SSE fields: ignored

        // NDJSON convenience: a bare JSON object per line.
        if (line[0] == '{') HandlePayload(line);
    }

    private void HandlePayload(string json)
    {
        NotifierAlert? alert;
        try { alert = ParseAlert(json); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Notifier payload");
            return;
        }
        if (alert != null) Dispatch(alert, bypassFilters: false);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────────────────────

    private NotifierAlert? ParseAlert(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var typeStr = GetString(root, "type") ?? GetString(root, "kind");
        var type = ParseType(typeStr);
        if (type == null) return null;

        var subject = GetString(root, "subject") ?? GetString(root, "species") ?? GetString(root, "resource");
        var text = GetString(root, "text") ?? GetString(root, "message");
        var cluster = GetString(root, "cluster") ?? GetString(root, "channel");

        var ts = DateTime.UtcNow;
        var tsRaw = GetString(root, "timestamp") ?? GetString(root, "time");
        if (tsRaw != null && DateTime.TryParse(tsRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
            ts = parsed;

        var clusterLabel = ResolveClusterLabel(cluster);
        var display = string.IsNullOrWhiteSpace(text) ? ComposeText(type.Value, subject, clusterLabel) : text!.Trim();
        return new NotifierAlert(type.Value, string.IsNullOrWhiteSpace(subject) ? null : subject!.Trim(), display,
            string.IsNullOrWhiteSpace(cluster) ? null : cluster!.Trim(), ts);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static NotifierAlertType? ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var k = raw.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return k switch
        {
            "rare-dino" or "raredino" or "dino" or "rare" => NotifierAlertType.RareDino,
            "resource" or "resources" or "harvest" => NotifierAlertType.Resource,
            "osd" or "drop" or "event" => NotifierAlertType.Osd,
            "element-node" or "element" or "node" or "elementnode" => NotifierAlertType.ElementNode,
            "tribe-log" or "tribelog" or "tribe" => NotifierAlertType.TribeLog,
            _ => null
        };
    }

    // ── Dispatch (filters + fan-out) ──────────────────────────────────────────────────────────

    private void Dispatch(NotifierAlert alert, bool bypassFilters)
    {
        if (!bypassFilters && !PassesFilters(alert)) return;

        lock (_gate)
        {
            _recent.Add(alert);
            while (_recent.Count > RecentCapacity) _recent.RemoveAt(0);
        }

        try { _hud.PushAlert(alert.Text, SeverityFor(alert.Type)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to push Notifier alert to HUD"); }

        RaiseAlertReceived(alert);
        RaiseChanged();
    }

    private bool PassesFilters(NotifierAlert alert)
    {
        lock (_gate)
        {
            if (!(_typeEnabled.TryGetValue(alert.Type, out var on) && on)) return false;

            if (alert.Type == NotifierAlertType.RareDino)
            {
                var id = MatchSpeciesId(alert.Subject);
                if (id == null || !_enabledSpecies.Contains(id)) return false;
            }

            if (alert.Type == NotifierAlertType.TribeLog && _tribeTriggers.Count > 0)
            {
                var hay = $"{alert.Subject} {alert.Text}";
                if (!_tribeTriggers.Any(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(alert.Cluster))
            {
                var cl = _clusters.FirstOrDefault(c =>
                    string.Equals(c.Id, alert.Cluster, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Label, alert.Cluster, StringComparison.OrdinalIgnoreCase));
                if (cl != null && !cl.Enabled) return false; // unknown clusters pass
            }
        }
        return true;
    }

    private static string? MatchSpeciesId(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var s = subject.Trim();
        foreach (var sp in SpeciesStatic)
        {
            if (string.Equals(sp.Id, s, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sp.Name, s, StringComparison.OrdinalIgnoreCase))
                return sp.Id;
        }
        return null;
    }

    private string? ResolveClusterLabel(string? cluster)
    {
        if (string.IsNullOrWhiteSpace(cluster)) return null;
        lock (_gate)
        {
            var cl = _clusters.FirstOrDefault(c =>
                string.Equals(c.Id, cluster, StringComparison.OrdinalIgnoreCase));
            return cl?.Label ?? cluster;
        }
    }

    // ── Test alerts ───────────────────────────────────────────────────────────────────────────

    public void TestAlert()
    {
        var n = Interlocked.Increment(ref _testCounter);
        var type = (n % 5) switch
        {
            1 => NotifierAlertType.RareDino,
            2 => NotifierAlertType.Resource,
            3 => NotifierAlertType.ElementNode,
            4 => NotifierAlertType.TribeLog,
            _ => NotifierAlertType.Osd
        };
        TestAlert(type);
    }

    public void TestAlert(NotifierAlertType type)
    {
        var subject = type switch
        {
            NotifierAlertType.RareDino => SpeciesStatic[Random.Shared.Next(SpeciesStatic.Count)].Name,
            NotifierAlertType.Resource => "Metal node",
            NotifierAlertType.ElementNode => "Charge node",
            NotifierAlertType.TribeLog => "Your Tribe: Bob was killed by a Raptor!",
            _ => "Orbital supply drop"
        };
        var text = ComposeText(type, subject, "Test") + " (test)";
        Dispatch(new NotifierAlert(type, subject, text, null, DateTime.UtcNow), bypassFilters: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static string KeyOf(NotifierAlertType type) =>
        TypeDefsStatic.First(d => d.Type == type).Key;

    private static HudAlertSeverity SeverityFor(NotifierAlertType type) => type switch
    {
        NotifierAlertType.RareDino => HudAlertSeverity.Success,
        NotifierAlertType.ElementNode => HudAlertSeverity.Warning,
        NotifierAlertType.TribeLog => HudAlertSeverity.Warning,
        _ => HudAlertSeverity.Info
    };

    private static string ComposeText(NotifierAlertType type, string? subject, string? clusterLabel)
    {
        var body = type switch
        {
            NotifierAlertType.RareDino => string.IsNullOrWhiteSpace(subject) ? "Rare dino spotted" : $"Rare dino: {subject}",
            NotifierAlertType.Resource => string.IsNullOrWhiteSpace(subject) ? "Resource available" : $"Resource: {subject}",
            NotifierAlertType.ElementNode => string.IsNullOrWhiteSpace(subject) ? "Element node active" : $"Element node: {subject}",
            NotifierAlertType.Osd => string.IsNullOrWhiteSpace(subject) ? "OSD event" : subject!,
            NotifierAlertType.TribeLog => string.IsNullOrWhiteSpace(subject) ? "Tribe log" : subject!,
            _ => subject ?? "Alert"
        };
        return string.IsNullOrWhiteSpace(clusterLabel) ? body : $"{body} · {clusterLabel}";
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }

    private static string ReasonOf(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.StatusCode is { } code
            ? $"Server returned {(int)code}"
            : "Could not reach the backend",
        TaskCanceledException => "Connection timed out",
        _ => "Connection failed"
    };

    // ── Channel management (backend /notifier/channels) ────────────────────────────────────────
    // Derives the channels URL from the configured stream endpoint, reusing its token, so the user
    // never touches the backend host directly. All three mutate the backend's live watched-channel
    // map and return the updated list.

    private static readonly JsonSerializerOptions ChannelJsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record ChannelsResponse(bool Ok, List<ChannelDto>? Channels);
    private sealed record ChannelDto(string? ChannelId, string? Cluster, string? Type);

    /// <summary>Turn ".../notifier/stream?token=X" into ".../notifier/channels?token=X[&extra]".</summary>
    private string BuildChannelsUrl(string? extraQuery = null)
    {
        string ep;
        lock (_gate) ep = _endpoint;
        if (string.IsNullOrWhiteSpace(ep) || !Uri.TryCreate(ep, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Set the stream endpoint first (Endpoint field above).");
        var url = $"{uri.GetLeftPart(UriPartial.Authority)}/notifier/channels{uri.Query}";
        if (!string.IsNullOrEmpty(extraQuery))
            url += (string.IsNullOrEmpty(uri.Query) ? "?" : "&") + extraQuery;
        return url;
    }

    public async Task<IReadOnlyList<NotifierWatchedChannel>> GetWatchedChannelsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildChannelsUrl(), ct).ConfigureAwait(false);
        return await ParseChannelsAsync(resp, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NotifierWatchedChannel>> AddWatchedChannelAsync(string channelId, string cluster, string type, CancellationToken ct = default)
    {
        var payload = new { channelId = (channelId ?? "").Trim(), cluster = (cluster ?? "").Trim(), type = (type ?? "").Trim() };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildChannelsUrl(), content, ct).ConfigureAwait(false);
        return await ParseChannelsAsync(resp, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NotifierWatchedChannel>> RemoveWatchedChannelAsync(string channelId, CancellationToken ct = default)
    {
        var url = BuildChannelsUrl("id=" + Uri.EscapeDataString(channelId ?? ""));
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await ParseChannelsAsync(resp, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<NotifierWatchedChannel>> ParseChannelsAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(resp.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Unauthorized — the token in your endpoint URL is wrong.",
                System.Net.HttpStatusCode.ServiceUnavailable => "The backend has no token configured yet.",
                System.Net.HttpStatusCode.BadRequest => "That doesn't look like a valid Discord channel ID (digits only).",
                _ => $"The backend returned {(int)resp.StatusCode}."
            });
        }
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var parsed = JsonSerializer.Deserialize<ChannelsResponse>(body, ChannelJsonOpts);
            return (parsed?.Channels ?? new List<ChannelDto>())
                .Select(c => new NotifierWatchedChannel(c.ChannelId ?? "", c.Cluster ?? "", c.Type ?? ""))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Couldn't read the backend's response.", ex);
        }
    }

    private void SetState(NotifierConnectionState state, string detail)
    {
        lock (_gate)
        {
            if (_state == state && _stateDetail == detail) return;
            _state = state;
            _stateDetail = detail;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notifier Changed subscriber threw"); }
    }

    // Caller already holds _gate.
    private void RaiseChangedNoLock() => Task.Run(RaiseChanged);

    private void RaiseAlertReceived(NotifierAlert alert)
    {
        try { AlertReceived?.Invoke(alert); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notifier AlertReceived subscriber threw"); }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { _http.Dispose(); } catch { /* ignore */ }
    }
}
