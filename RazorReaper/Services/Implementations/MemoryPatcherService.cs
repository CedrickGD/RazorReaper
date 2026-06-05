using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;
using RazorReaper.Services.Implementations.CustomLab;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Live-apply orchestration. See <see cref="IMemoryPatcherService"/>. Derives original/patched
/// byte pairs from the Sky Injector's backups, scans the running ShooterGame for the originals,
/// and (gated) writes the patched bytes + nudges a GPU re-upload via the console.
/// </summary>
public sealed class MemoryPatcherService : IMemoryPatcherService
{
    private const long MaxScanBytes = 8L * 1024 * 1024 * 1024; // bound a worst-case full-RAM sweep
    private const int ExitPollMs = 2000;

    // Module-name fragments that signal an anti-cheat is loaded (warn-but-allow only).
    private static readonly string[] AntiCheatMarkers = { "beclient", "beservice", "battleye", "easyanticheat" };

    // After a successful live write, toggling the streaming mip bias forces high mips to stream
    // out and back in — the best non-hooking attempt at making the engine re-upload our patched
    // CPU bytes to the GPU. May be a no-op depending on how the texture is resident.
    private static readonly string[] ReuploadNudgeCommands = { "r.Streaming.MipBias 8", "r.Streaming.MipBias 0" };

    private readonly IProcessMemoryService _memory;
    private readonly IProcessService _process;
    private readonly ISkyInjectorService _sky;
    private readonly IGameConsoleService _console;
    private readonly ICustomLabSettingsService _settings;
    private readonly IOptions<AppConfiguration> _config;
    private readonly IActivityService _activity;
    private readonly ITelemetryService _telemetry;
    private readonly IArkLauncher _arkLauncher;
    private readonly ILogger<MemoryPatcherService> _logger;

    private readonly object _lock = new();
    private Process? _attachedProc;
    private Timer? _exitPoll;
    private AntiCheatStatus _antiCheat = AntiCheatStatus.None;
    private string? _antiCheatModule;

    public MemoryPatcherService(
        IProcessMemoryService memory,
        IProcessService process,
        ISkyInjectorService sky,
        IGameConsoleService console,
        ICustomLabSettingsService settings,
        IOptions<AppConfiguration> config,
        IActivityService activity,
        ITelemetryService telemetry,
        IArkLauncher arkLauncher,
        ILogger<MemoryPatcherService> logger)
    {
        _memory = memory;
        _process = process;
        _sky = sky;
        _console = console;
        _settings = settings;
        _config = config;
        _activity = activity;
        _telemetry = telemetry;
        _arkLauncher = arkLauncher;
        _logger = logger;
    }

    public event Action? StateChanged;

    public bool IsAttached => _memory.IsAttached;
    public int? AttachedProcessId => _memory.AttachedProcessId;
    public bool AttachedForWrite => _memory.AttachedForWrite;
    public AntiCheatStatus AntiCheat { get { lock (_lock) return _antiCheat; } }
    public string? AntiCheatModule { get { lock (_lock) return _antiCheatModule; } }

    public async Task<MemoryAttachResult> AttachAsync(bool forWrite, CancellationToken ct = default)
    {
        await _settings.LoadAsync();
        var s = _settings.Current;
        if (_arkLauncher.IsBattlEyeActive())
        {
            return new MemoryAttachResult(MemoryAttachStatus.Failed, null, AntiCheatStatus.Detected, "BattlEye", forWrite,
                "BattlEye is active — Live Apply is disabled. Relaunch ARK with No BattlEye (Unofficial only).");
        }
        if (!(s.Accepted && s.MasterEnabled && s.MemoryInjectEnabled))
        {
            return new MemoryAttachResult(MemoryAttachStatus.Disabled, null, AntiCheatStatus.None, null, forWrite,
                "Accept the Read Me, enable Custom Lab, and turn on Live Apply first.");
        }

        return await Task.Run(() =>
        {
            var name = _config.Value.Ark.GameProcessName;
            var procs = _process.GetProcessesByName(name);
            var kept = false;
            try
            {
                if (procs.Length == 0)
                    return new MemoryAttachResult(MemoryAttachStatus.ProcessNotFound, null, AntiCheatStatus.None, null, forWrite,
                        "ShooterGame isn't running — launch ARK first.");
                if (procs.Length > 1)
                    return new MemoryAttachResult(MemoryAttachStatus.MultipleProcesses, null, AntiCheatStatus.None, null, forWrite,
                        "Multiple ShooterGame processes found — close the extras and retry.");

                var proc = procs[0];
                var attach = _memory.Attach(proc.Id, forWrite);
                if (!attach.Attached)
                    return attach;

                var (ac, acMod) = DetectAntiCheat();

                lock (_lock)
                {
                    DetachTrackingLocked();
                    _attachedProc = proc;
                    _antiCheat = ac;
                    _antiCheatModule = acMod;
                    try
                    {
                        proc.EnableRaisingEvents = true;
                        proc.Exited += OnAttachedProcessExited;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not subscribe to process Exited");
                    }
                    _exitPoll = new Timer(PollProcessExit, null, ExitPollMs, ExitPollMs);
                }
                kept = true;

                RaiseStateChanged();
                _ = _telemetry.TrackEventAsync("custom_lab.memory_attach",
                    metrics: new Dictionary<string, object?>
                    {
                        ["for_write"] = forWrite,
                        ["anti_cheat"] = ac.ToString()
                    });

                return new MemoryAttachResult(MemoryAttachStatus.Ok, proc.Id, ac, acMod, forWrite,
                    $"Attached to ShooterGame (PID {proc.Id}).");
            }
            finally
            {
                foreach (var p in procs)
                {
                    if (kept && ReferenceEquals(p, _attachedProc)) continue;
                    try { p?.Dispose(); } catch { /* best-effort */ }
                }
            }
        }, ct);
    }

    public void Detach()
    {
        bool changed;
        lock (_lock)
        {
            changed = _attachedProc is not null;
            DetachTrackingLocked();
            _antiCheat = AntiCheatStatus.None;
            _antiCheatModule = null;
        }
        _memory.Detach();
        if (changed) RaiseStateChanged();
    }

    public async Task<IReadOnlyList<TextureScanFinding>> ScanSkyTexturesAsync(
        IProgress<MemoryScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (!_memory.IsAttached) return Array.Empty<TextureScanFinding>();

        return await Task.Run<IReadOnlyList<TextureScanFinding>>(() =>
        {
            var patches = DeriveInjectedTextures();
            if (patches.Count == 0) return Array.Empty<TextureScanFinding>();

            var needles = patches.Select(p => p.Original).ToList();
            var scans = _memory.ScanForSequences(needles, MemoryRegionFilter.Default, MaxScanBytes, progress, ct);

            var findings = new List<TextureScanFinding>(patches.Count);
            for (var i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                var sc = scans[i];
                findings.Add(new TextureScanFinding(p.Path, p.Width, p.Height, p.Kind, p.Original.Length, sc.MatchCount, sc.MatchAddresses));
            }

            _ = _telemetry.TrackEventAsync("custom_lab.memory_scan",
                metrics: new Dictionary<string, object?>
                {
                    ["textures"] = patches.Count,
                    ["matches"] = findings.Sum(f => f.MatchCount),
                    ["anti_cheat"] = _antiCheat.ToString()
                });

            return findings;
        }, ct);
    }

    public async Task<IReadOnlyList<LiveTexturePatchResult>> ApplySkyLiveAsync(
        bool allowWrite, IProgress<MemoryScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (_arkLauncher.IsBattlEyeActive())
            return Single("(blocked)", "BattlEye is active — Live Apply refuses to run. Relaunch ARK No-BattlEye (Unofficial only).");
        if (!allowWrite)
            return Single("(write disabled)", "Live write is off — tick the confirmation to apply.");
        if (!_memory.IsAttached)
            return Single("(not attached)", "Attach to ShooterGame first.");
        if (!_memory.AttachedForWrite)
            return Single("(read-only)", "Attached read-only — re-attach with write access to apply.");

        var results = await Task.Run<IReadOnlyList<LiveTexturePatchResult>>(() =>
        {
            var patches = DeriveInjectedTextures();
            if (patches.Count == 0) return Array.Empty<LiveTexturePatchResult>();

            var needles = patches.Select(p => p.Original).ToList();
            var scans = _memory.ScanForSequences(needles, MemoryRegionFilter.Default, MaxScanBytes, progress, ct);

            var list = new List<LiveTexturePatchResult>(patches.Count);
            for (var i = 0; i < patches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = patches[i];
                var addrs = scans[i].MatchAddresses;
                int written = 0, failed = 0;
                var errors = new List<string>();
                foreach (var addr in addrs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_memory.TryWrite(addr, p.Patched, out var err))
                    {
                        written++;
                        _logger.LogInformation("Live-wrote {Size} bytes at {Addr:X} for {Path}", p.Patched.Length, addr, p.Path);
                    }
                    else
                    {
                        failed++;
                        if (err is not null && errors.Count < 5) errors.Add(err);
                    }
                }
                list.Add(new LiveTexturePatchResult(p.Path, addrs.Count, written, failed, errors));
            }
            return list;
        }, ct);

        var totalWritten = results.Sum(r => r.MatchesWritten);

        // Best-effort GPU re-upload nudge once something changed in memory.
        if (totalWritten > 0)
            await NudgeReuploadAsync(ct);

        _activity.AddActivity(
            totalWritten > 0
                ? $"Memory live-apply → wrote {totalWritten} region(s); nudged re-upload"
                : "Memory live-apply → no live CPU copy found to write (already uploaded to GPU)",
            totalWritten > 0 ? "success" : "warning");

        _ = _telemetry.TrackEventAsync("custom_lab.memory_write",
            totalWritten > 0 ? TelemetryEventStatus.Ok : TelemetryEventStatus.Degraded,
            metrics: new Dictionary<string, object?>
            {
                ["textures"] = results.Count,
                ["written"] = totalWritten,
                ["anti_cheat"] = _antiCheat.ToString()
            });

        return results;
    }

    public async Task<MemoryScanResult> ScanForHexAsync(string hexPattern,
        IProgress<MemoryScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (!_memory.IsAttached || !TryParseHex(hexPattern, out var needle))
            return new MemoryScanResult(Array.Empty<ulong>(), 0, 0, false, false);

        return await Task.Run(() => _memory.ScanForSequence(needle, MemoryRegionFilter.Default, MaxScanBytes, progress, ct), ct);
    }

    public bool TryReadHex(ulong address, int length, out string hex, out string? error)
    {
        hex = string.Empty;
        error = null;
        if (!_memory.IsAttached) { error = "Not attached."; return false; }
        if (length <= 0 || length > 4096) { error = "Length must be between 1 and 4096."; return false; }

        var buf = new byte[length];
        if (!_memory.TryRead(address, buf, length, out var read) || read <= 0)
        {
            error = "Read failed at that address (unreadable / freed page).";
            return false;
        }
        hex = Convert.ToHexString(buf, 0, read);
        return true;
    }

    public bool TryWriteHex(ulong address, string hexBytes, out string? error)
    {
        error = null;
        if (!TryParseHex(hexBytes, out var data)) { error = "Invalid hex — use pairs like 'DE AD BE EF'."; return false; }
        return _memory.TryWrite(address, data, out error);
    }

    public Task<bool> SendConsoleAsync(string command, CancellationToken ct = default)
        => _console.SendCommandAsync(command, useClipboard: true, ct);

    // ── Internals ───────────────────────────────────────────────────────────

    private sealed record SkyPatch(string Path, int Width, int Height, SkyTextureKind Kind, byte[] Original, byte[] Patched);

    private List<SkyPatch> DeriveInjectedTextures()
    {
        var list = new List<SkyPatch>();
        foreach (var (livePath, backupPath) in _sky.EnumerateBackupPairs())
        {
            try
            {
                var backup = File.ReadAllBytes(backupPath);

                SkyTextureKind kind;
                int w, h, off, size;
                if (UAssetTextureParser.TryParseDxt5(backup, out w, out h, out off, out size))
                    kind = SkyTextureKind.Dxt5;
                else if (UAssetTextureParser.TryParseBgra8(backup, out w, out h, out off, out size))
                    kind = SkyTextureKind.Bgra8;
                else
                    continue;

                if (off < 0 || size <= 0 || (long)off + size > backup.Length) continue;

                byte[] live;
                try { live = File.ReadAllBytes(livePath); }
                catch { continue; }
                if ((long)off + size > live.Length) continue;

                var original = new byte[size];
                Array.Copy(backup, off, original, 0, size);
                var patched = new byte[size];
                Array.Copy(live, off, patched, 0, size);

                // Identical → texture isn't currently injected (or was restored); nothing to find/write.
                if (original.AsSpan().SequenceEqual(patched)) continue;

                list.Add(new SkyPatch(livePath, w, h, kind, original, patched));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sky patch derivation skipped: {Path}", livePath);
            }
        }
        return list;
    }

    private (AntiCheatStatus, string?) DetectAntiCheat()
    {
        var modules = _memory.EnumerateModuleNames();
        if (modules.Count == 0) return (AntiCheatStatus.Unknown, null);
        foreach (var m in modules)
        {
            foreach (var marker in AntiCheatMarkers)
            {
                if (m.Contains(marker, StringComparison.Ordinal))
                    return (AntiCheatStatus.Detected, m);
            }
        }
        return (AntiCheatStatus.None, null);
    }

    private async Task NudgeReuploadAsync(CancellationToken ct)
    {
        try
        {
            await _console.SendCommandsAsync(ReuploadNudgeCommands, useClipboard: false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Re-upload nudge failed");
        }
    }

    private void OnAttachedProcessExited(object? sender, EventArgs e) => Detach();

    private void PollProcessExit(object? state)
    {
        try
        {
            Process? p;
            lock (_lock) p = _attachedProc;
            if (p is null) return;
            if (p.HasExited) Detach();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exit poll error — detaching");
            Detach();
        }
    }

    // Caller must hold _lock.
    private void DetachTrackingLocked()
    {
        if (_exitPoll is not null)
        {
            try { _exitPoll.Dispose(); } catch { /* ignore */ }
            _exitPoll = null;
        }
        if (_attachedProc is not null)
        {
            try { _attachedProc.Exited -= OnAttachedProcessExited; } catch { /* ignore */ }
            try { _attachedProc.Dispose(); } catch { /* ignore */ }
            _attachedProc = null;
        }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { _logger.LogDebug(ex, "StateChanged handler threw"); }
    }

    private static IReadOnlyList<LiveTexturePatchResult> Single(string path, string message)
        => new[] { new LiveTexturePatchResult(path, 0, 0, 0, new[] { message }) };

    private static bool TryParseHex(string? s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(s)) return false;

        var clean = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (clean.Length == 0 || clean.Length % 2 != 0) return false;

        var outBytes = new byte[clean.Length / 2];
        for (var i = 0; i < outBytes.Length; i++)
        {
            if (!byte.TryParse(clean.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out outBytes[i]))
                return false;
        }
        bytes = outBytes;
        return true;
    }

    public void Dispose() => Detach();
}
