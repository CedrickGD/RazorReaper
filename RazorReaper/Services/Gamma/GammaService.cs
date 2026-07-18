using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Gamma;

/// <summary>
/// Single entry point the gamma page codes against. Owns the <see cref="GammaController"/>
/// (GDI ramp), the <see cref="GammaTriggerService"/> (global hooks) and the persisted
/// <see cref="GammaConfig"/>, and wires a fired trigger to the right preset (cycle advance /
/// direct jump). Register as a singleton.
/// </summary>
public interface IGammaService : IDisposable
{
    /// <summary>The gamma value most recently applied (1.0 = neutral).</summary>
    double CurrentGamma { get; }

    /// <summary>True while global hooks are installed and firing triggers.</summary>
    bool IsListening { get; }

    /// <summary>The active trigger style (Cycle or Direct).</summary>
    TriggerMode Mode { get; }

    /// <summary>The live preset list (edit via <see cref="UpdatePreset"/> then it is persisted).</summary>
    IReadOnlyList<PresetConfig> Presets { get; }

    /// <summary>The cycle trigger (Cycle mode).</summary>
    TriggerInput CycleTrigger { get; }

    /// <summary>The direct bindings (Direct mode).</summary>
    IReadOnlyList<DirectBindingConfig> DirectBindings { get; }

    /// <summary>Whether gamma is applied to all monitors (else the selected subset).</summary>
    bool ApplyToAllMonitors { get; }

    /// <summary>The device names selected when <see cref="ApplyToAllMonitors"/> is false.</summary>
    IReadOnlyList<string> SelectedMonitors { get; }

    /// <summary>Absolute path of the persisted config file.</summary>
    string ConfigFilePath { get; }

    /// <summary>Raised when gamma, listening state, or config changes. MAY fire on a background
    /// thread (trigger callbacks arrive off the UI thread) — marshal with InvokeAsync in Blazor.</summary>
    event Action? StateChanged;

    /// <summary>Applies the preset that sits at <paramref name="level"/>'s ordinal position.</summary>
    GammaController.ApplyResult Apply(GammaLevel level);

    /// <summary>Applies an arbitrary gamma value (clamped to the supported range).</summary>
    GammaController.ApplyResult ApplyValue(double gamma);

    /// <summary>Applies a value live (e.g. while dragging a slider); does not persist config.</summary>
    void Preview(double gamma);

    /// <summary>Restores neutral gamma (1.0).</summary>
    void ResetToDefault();

    /// <summary>Applies a specific preset by id.</summary>
    GammaController.ApplyResult ApplyPreset(string presetId);

    /// <summary>Updates a preset (any null argument is left unchanged) and persists.</summary>
    void UpdatePreset(string id, string? name = null, double? value = null, bool? inCycle = null);

    /// <summary>Resets all presets, triggers and monitor selection to factory defaults.</summary>
    void ResetConfigToDefault();

    void SetMode(TriggerMode mode);
    void SetCycleTrigger(TriggerInput trigger);

    /// <summary>Adds or replaces the direct binding for <paramref name="trigger"/>.</summary>
    void SetDirectBinding(TriggerInput trigger, string presetId);
    void RemoveDirectBinding(TriggerInput trigger);

    /// <summary>Starts global listening. Returns false if the hooks could not be installed.</summary>
    bool StartListening();
    void StopListening();

    /// <summary>Captures the next key / mouse button once (for binding UI). Callbacks may fire
    /// on a background thread.</summary>
    void BeginCapture(Action<TriggerInput> onCaptured, Action onCancelled);
    void CancelCapture();

    IReadOnlyList<GammaMonitor> ListMonitors();
    void SetTargets(bool allMonitors, IEnumerable<string> selectedDeviceNames);

    /// <summary>Generates the Logitech G HUB Lua script for the current config.</summary>
    string GenerateLua();

    /// <summary>Persists the current config to disk.</summary>
    void Save();
}

/// <inheritdoc cref="IGammaService"/>
public sealed class GammaService : IGammaService
{
    private readonly ILogger<GammaService> _logger;
    private readonly GammaConfigStore _store;
    private readonly GammaController _controller;
    private readonly GammaTriggerService _triggers;

    private readonly object _gate = new();
    private GammaConfig _config;
    private int _cycleIndex = -1;
    private bool _disposed;

    public GammaService(ILogger<GammaService> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _store = new GammaConfigStore(loggerFactory.CreateLogger<GammaConfigStore>());
        _config = _store.Load();

        _controller = new GammaController(loggerFactory.CreateLogger<GammaController>());
        _controller.SetTargets(_config.ApplyToAllMonitors, _config.SelectedMonitors);

        _triggers = new GammaTriggerService(loggerFactory.CreateLogger<GammaTriggerService>());
        _triggers.TriggerFired += OnTriggerFired;
        RebuildBindings();
    }

    public event Action? StateChanged;

    public double CurrentGamma => _controller.CurrentGamma;
    public bool IsListening => _triggers.IsListening;

    public TriggerMode Mode { get { lock (_gate) return _config.Mode; } }
    public IReadOnlyList<PresetConfig> Presets { get { lock (_gate) return _config.Presets.ToList(); } }
    public TriggerInput CycleTrigger { get { lock (_gate) return _config.Cycle.Trigger; } }
    public IReadOnlyList<DirectBindingConfig> DirectBindings { get { lock (_gate) return _config.Direct.ToList(); } }
    public bool ApplyToAllMonitors { get { lock (_gate) return _config.ApplyToAllMonitors; } }
    public IReadOnlyList<string> SelectedMonitors { get { lock (_gate) return _config.SelectedMonitors.ToList(); } }
    public string ConfigFilePath => _store.FilePath;

    // ─── Applying gamma ─────────────────────────────────────────────────────────

    public GammaController.ApplyResult Apply(GammaLevel level)
    {
        double value;
        lock (_gate)
        {
            int idx = (int)level;
            value = idx >= 0 && idx < _config.Presets.Count
                ? _config.Presets[idx].Value
                : GammaPresets.DefaultValue(level);
        }
        return ApplyValue(value);
    }

    public GammaController.ApplyResult ApplyValue(double gamma)
    {
        var result = _controller.Apply(gamma);
        RaiseStateChanged();
        return result;
    }

    public void Preview(double gamma)
    {
        _controller.Apply(gamma);
        RaiseStateChanged();
    }

    public void ResetToDefault()
    {
        _controller.Apply(GammaPresets.Default);
        RaiseStateChanged();
    }

    public GammaController.ApplyResult ApplyPreset(string presetId)
    {
        double? value = null;
        lock (_gate)
        {
            var p = _config.Presets.FirstOrDefault(x => x.Id == presetId);
            if (p != null)
                value = p.Value;
        }
        return value is { } v ? ApplyValue(v) : GammaController.ApplyResult.Failed;
    }

    // ─── Presets / config mutation ──────────────────────────────────────────────

    public void UpdatePreset(string id, string? name = null, double? value = null, bool? inCycle = null)
    {
        lock (_gate)
        {
            var p = _config.Presets.FirstOrDefault(x => x.Id == id);
            if (p == null)
                return;
            if (name != null)
                p.Name = name;
            if (value is { } v)
                p.Value = GammaPresets.Clamp(v);
            if (inCycle is { } c)
                p.InCycle = c;
        }
        Save();
        RebuildBindings();
        RaiseStateChanged();
    }

    public void ResetConfigToDefault()
    {
        lock (_gate)
        {
            _config = GammaConfig.CreateDefault();
            _cycleIndex = -1;
            _controller.SetTargets(_config.ApplyToAllMonitors, _config.SelectedMonitors);
        }
        Save();
        RebuildBindings();
        RaiseStateChanged();
    }

    public void SetMode(TriggerMode mode)
    {
        lock (_gate)
        {
            _config.Mode = mode;
            _cycleIndex = -1;
        }
        Save();
        RebuildBindings();
        RaiseStateChanged();
    }

    public void SetCycleTrigger(TriggerInput trigger)
    {
        lock (_gate)
            _config.Cycle.Trigger = trigger;
        Save();
        RebuildBindings();
        RaiseStateChanged();
    }

    public void SetDirectBinding(TriggerInput trigger, string presetId)
    {
        if (trigger.IsEmpty)
            return;
        lock (_gate)
        {
            _config.Direct.RemoveAll(b => b.Trigger.Equals(trigger));
            _config.Direct.Add(new DirectBindingConfig { Trigger = trigger, PresetId = presetId });
        }
        Save();
        RebuildBindings();
        RaiseStateChanged();
    }

    public void RemoveDirectBinding(TriggerInput trigger)
    {
        lock (_gate)
            _config.Direct.RemoveAll(b => b.Trigger.Equals(trigger));
        Save();
        RebuildBindings();
        RaiseStateChanged();
    }

    // ─── Listening / capture ────────────────────────────────────────────────────

    public bool StartListening()
    {
        if (!_triggers.Start())
        {
            RaiseStateChanged();
            return false;
        }
        RebuildBindings();
        _triggers.IsListening = true;
        lock (_gate) _config.Listening = true;
        Save();
        RaiseStateChanged();
        return true;
    }

    public void StopListening()
    {
        _triggers.IsListening = false;
        _triggers.Stop();
        lock (_gate) _config.Listening = false;
        Save();
        RaiseStateChanged();
    }

    public void BeginCapture(Action<TriggerInput> onCaptured, Action onCancelled)
        => _triggers.BeginCapture(onCaptured, onCancelled);

    public void CancelCapture() => _triggers.CancelCapture();

    // ─── Monitors ───────────────────────────────────────────────────────────────

    public IReadOnlyList<GammaMonitor> ListMonitors() => _controller.ListMonitors();

    public void SetTargets(bool allMonitors, IEnumerable<string> selectedDeviceNames)
    {
        var selected = selectedDeviceNames?.ToList() ?? new List<string>();
        lock (_gate)
        {
            _config.ApplyToAllMonitors = allMonitors;
            _config.SelectedMonitors = new List<string>(selected);
        }
        _controller.SetTargets(allMonitors, selected);
        Save();
        RaiseStateChanged();
    }

    // ─── Lua / persistence ──────────────────────────────────────────────────────

    public string GenerateLua()
    {
        lock (_gate)
            return LuaGenerator.Generate(_config);
    }

    public void Save()
    {
        GammaConfig snapshot;
        lock (_gate)
            snapshot = _config;
        _store.Save(snapshot);
    }

    // ─── Trigger dispatch ───────────────────────────────────────────────────────

    private void OnTriggerFired(TriggerInput trigger)
    {
        try
        {
            if (Mode == TriggerMode.Cycle)
            {
                if (trigger.Equals(CycleTrigger))
                    AdvanceCycle();
            }
            else
            {
                string? presetId = null;
                lock (_gate)
                {
                    var b = _config.Direct.FirstOrDefault(d => d.Trigger.Equals(trigger));
                    presetId = b?.PresetId;
                }
                if (presetId != null)
                    ApplyPreset(presetId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling gamma trigger failed");
        }
    }

    private void AdvanceCycle()
    {
        double value;
        lock (_gate)
        {
            var enabled = _config.Presets.Where(p => p.InCycle).ToList();
            if (enabled.Count == 0)
                return;
            _cycleIndex++;
            if (_cycleIndex >= enabled.Count)
                _cycleIndex = _config.Cycle.Wrap ? 0 : enabled.Count - 1;
            if (_cycleIndex < 0)
                _cycleIndex = 0;
            value = enabled[_cycleIndex].Value;
        }
        ApplyValue(value);
    }

    /// <summary>Feeds the trigger service the set of triggers to watch for the active mode.</summary>
    private void RebuildBindings()
    {
        List<TriggerInput> bound = new();
        lock (_gate)
        {
            if (_config.Mode == TriggerMode.Cycle)
            {
                if (!_config.Cycle.Trigger.IsEmpty)
                    bound.Add(_config.Cycle.Trigger);
            }
            else
            {
                foreach (var b in _config.Direct)
                    if (!b.Trigger.IsEmpty)
                        bound.Add(b.Trigger);
            }
        }
        _triggers.UpdateBindings(bound);
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch { /* subscriber errors are not ours */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _triggers.TriggerFired -= OnTriggerFired; } catch { }
        _triggers.Dispose();
        _controller.Dispose(); // restores original gamma
    }
}
