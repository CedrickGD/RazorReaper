using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services.ServerQuery;
using RazorReaper.Services.Steam;

namespace RazorReaper.Services.Overlay;

/// <summary>
/// Feeds the HUD's ServerInfo module automatically: detects the server ARK is currently on
/// (newest Steam server-history entry stamped after the game started), A2S-queries it for
/// name/players/ping, and restarts the HUD session timer at the join time. Polls only while
/// the HUD overlay is running; resolved once at startup so no page needs to be open.
/// </summary>
public interface ISessionHudService : IDisposable
{
}

public sealed class SessionHudService : ISessionHudService
{
    private const int PollIntervalMs = 5_000;

    // One poll = one query. The count on the HUD is the one number that should be live —
    // at the old 15s throttle it lagged noticeably behind joins/leaves, and an A2S round
    // is two UDP packets, so there is nothing worth saving.
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(5);

    /// <summary>After this many straight failures the HUD shows nothing rather than old numbers.</summary>
    private const int MaxFailedQueries = 3;

    // A history entry slightly older than the game process still counts as this session's
    // join (Steam stamps it when the game connects; clocks and process starts aren't exact).
    private static readonly TimeSpan JoinSlack = TimeSpan.FromMinutes(2);

    private readonly ILogger<SessionHudService> _logger;
    private readonly IHudOverlayService _hud;
    private readonly IServerQueryService _query;
    private readonly ISteamServerHistoryService _history;
    private readonly IProcessService _process;
    private readonly string _gameProcessName;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _polling;
    private bool _disposed;

    // Bumped under _gate whenever the timer starts/stops or the service is disposed. A poll
    // captures it at launch and bails if it changed, so a poll that outlives its timer (Timer
    // .Dispose doesn't wait for in-flight callbacks) never mutates state for a stale session.
    private int _generation;

    // Current-session state — written only by the poll loop (serialized by _polling) and reset
    // under _gate when a session starts; every write is guarded by the captured generation.
    private string? _currentAddress;
    private string _currentIp = "";
    private int _currentPort;
    private string? _lastGoodName;
    private DateTime _lastQueryUtc = DateTime.MinValue;
    private int _failedQueries;

    public SessionHudService(
        ILogger<SessionHudService> logger,
        IHudOverlayService hud,
        IServerQueryService query,
        ISteamServerHistoryService history,
        IProcessService process,
        IOptions<AppConfiguration> config)
    {
        _logger = logger;
        _hud = hud;
        _query = query;
        _history = history;
        _process = process;
        _gameProcessName = config.Value.Ark.GameProcessName;

        _hud.Changed += SyncTimerToHudState;
        SyncTimerToHudState();
    }

    /// <summary>Poll only while the HUD is on — it is the only consumer of what we produce.</summary>
    private void SyncTimerToHudState()
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (_hud.IsRunning && _timer == null)
            {
                // Fresh session: forget any prior detection so the next poll re-anchors the
                // timer and re-resolves the server. No timer is running here, so this is safe.
                _generation++;
                _currentAddress = null;
                _currentIp = "";
                _currentPort = 0;
                _lastGoodName = null;
                _lastQueryUtc = DateTime.MinValue;
                _timer = new Timer(OnTick, null, 0, PollIntervalMs);
            }
            else if (!_hud.IsRunning && _timer != null)
            {
                _timer.Dispose();
                _timer = null;
                _generation++; // invalidate any in-flight poll from this session
            }
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;

        int generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) { Interlocked.Exchange(ref _polling, 0); return; }
            generation = _generation;
            token = _cts.Token; // safe: the CTS is cancelled but never disposed (see Dispose)
        }
        _ = PollAsync(generation, token);
    }

    private async Task PollAsync(int generation, CancellationToken token)
    {
        try
        {
            var gameStartUtc = GetGameStartUtc(out var gameRunning);

            if (!gameRunning)
            {
                bool clear = false;
                lock (_gate)
                {
                    if (generation != _generation) return;
                    if (_currentAddress != null)
                    {
                        _currentAddress = null;
                        _lastGoodName = null;
                        clear = true;
                    }
                }
                if (clear) _hud.SetServerInfo(null, null, null, null);
                return;
            }

            var entry = _history.GetMostRecentEntry();

            // Only entries stamped during this game session count; older history means the
            // player hasn't joined a server since launch (single player / main menu).
            var joinCutoff = gameStartUtc.HasValue
                ? gameStartUtc.Value - JoinSlack
                : DateTime.UtcNow - TimeSpan.FromHours(6);
            if (entry == null || entry.LastPlayedUtc < joinCutoff) return;

            bool newServer = false;
            string ip;
            int port;
            lock (_gate)
            {
                if (generation != _generation) return;
                if (entry.Address != _currentAddress)
                {
                    _currentAddress = entry.Address;
                    _currentIp = entry.Ip;
                    _currentPort = entry.QueryPort;
                    _lastGoodName = null;
                    _lastQueryUtc = DateTime.MinValue;
                    _failedQueries = 0;
                    newServer = true;
                }
                if (!newServer && DateTime.UtcNow - _lastQueryUtc < QueryInterval) return;
                _lastQueryUtc = DateTime.UtcNow;
                ip = _currentIp;
                port = _currentPort;
            }

            if (newServer)
            {
                _hud.SetSessionStart(entry.LastPlayedUtc);
                // Show the endpoint until the first query resolves the real name.
                _hud.SetServerInfo(entry.Address, null, null, null);
                _logger.LogInformation("Session HUD: detected server join {Address}", entry.Address);
            }

            var info = await _query.QueryAsync(ip, port, token);
            if (info != null)
            {
                string name;
                lock (_gate)
                {
                    if (generation != _generation) return;
                    name = string.IsNullOrWhiteSpace(info.Name) ? entry.Address : info.Name;
                    _lastGoodName = name;
                    _failedQueries = 0;
                }
                _hud.SetServerInfo(name, info.Players, info.MaxPlayers, info.PingMs);
            }
            else
            {
                // One or two misses are UDP being UDP — keep the last good values. From the
                // third on the numbers are stale enough to mislead ("player count from an hour
                // ago"), so the count and ping come off; the name stays as context.
                bool goStale;
                lock (_gate)
                {
                    if (generation != _generation) return;
                    goStale = ++_failedQueries == MaxFailedQueries;
                }
                if (goStale)
                {
                    _logger.LogDebug("Session HUD: {Count} failed queries — clearing stale server numbers", MaxFailedQueries);
                    _hud.SetServerInfo(_lastGoodName ?? entry.Address, null, null, null);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session HUD poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>Earliest start time of the running game processes; null start when unreadable.</summary>
    private DateTime? GetGameStartUtc(out bool gameRunning)
    {
        gameRunning = false;
        DateTime? earliest = null;
        try
        {
            var processes = _process.GetProcessesByName(_gameProcessName);
            foreach (var p in processes)
            {
                gameRunning = true;
                try
                {
                    var startUtc = p.StartTime.ToUniversalTime();
                    if (earliest == null || startUtc < earliest) earliest = startUtc;
                }
                catch
                {
                    // StartTime can be inaccessible; the caller falls back to a recency window.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect {Process} processes", _gameProcessName);
        }
        return earliest;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
        _hud.Changed -= SyncTimerToHudState;
        // Cancel but do NOT dispose: an in-flight poll may still hold this token, and reading a
        // disposed CTS.Token throws. Cancellation is enough; GC reclaims the source.
        _cts.Cancel();
    }
}
