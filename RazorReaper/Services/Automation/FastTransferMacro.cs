using Microsoft.Extensions.Logging;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;

namespace RazorReaper.Services.Automation;

/// <summary>Timing and key options for the premade Fast Transfer macro.</summary>
public sealed record FastTransferSettings
{
    /// <summary>Virtual key that opens/closes the inventory in game (default 'F').</summary>
    public int InventoryVirtualKey { get; init; } = 0x46;
    /// <summary>How many times the calibrated Transfer All point is clicked.</summary>
    public int ClickCount { get; init; } = 3;
    /// <summary>Wait after opening the inventory before the first click, in milliseconds.</summary>
    public int OpenDelayMs { get; init; } = 500;
    /// <summary>Wait between consecutive clicks, in milliseconds.</summary>
    public int PerClickDelayMs { get; init; } = 150;
}

/// <summary>
/// Premade Fast Transfer macro: focus the game window, press the inventory key, wait for the UI
/// to open, click the calibrated Transfer All point N times, then press the inventory key again.
/// The click target comes from <see cref="ICalibrationService"/> under <see cref="PointName"/>,
/// scoped to the current primary-screen resolution.
/// </summary>
public interface IFastTransferMacro
{
    /// <summary>Calibration point name the macro clicks ("Fast Transfer - Transfer All").</summary>
    string PointName { get; }

    /// <summary>True when the Transfer All point is calibrated for the current resolution.</summary>
    bool IsCalibrated { get; }

    /// <summary>Gets the calibrated Transfer All point for the current resolution.</summary>
    bool TryGetTransferPoint(out Point point);

    /// <summary>
    /// Builds the runnable sequence from the given settings, or null when the Transfer All point
    /// is not calibrated for the current resolution. Values are clamped to safe ranges.
    /// </summary>
    MacroSequence? Build(FastTransferSettings settings);
}

/// <summary>Default <see cref="IFastTransferMacro"/> implementation.</summary>
public sealed class FastTransferMacro : IFastTransferMacro
{
    /// <summary>Stable calibration point name shared with the Macros page.</summary>
    public const string TransferPointName = "Fast Transfer - Transfer All";

    private readonly ICalibrationService _calibration;
    private readonly ILogger<FastTransferMacro> _logger;

    public FastTransferMacro(ICalibrationService calibration, ILogger<FastTransferMacro> logger)
    {
        _calibration = calibration;
        _logger = logger;
    }

    public string PointName => TransferPointName;

    public bool IsCalibrated => _calibration.HasPoint(TransferPointName);

    public bool TryGetTransferPoint(out Point point) => _calibration.TryGetPoint(TransferPointName, out point);

    public MacroSequence? Build(FastTransferSettings settings)
    {
        try
        {
            if (settings is null) return null;
            if (!_calibration.TryGetPoint(TransferPointName, out var point))
            {
                _logger.LogWarning(
                    "Fast Transfer point is not calibrated for resolution {Resolution}",
                    _calibration.CurrentResolutionKey);
                return null;
            }

            var inventoryVk = settings.InventoryVirtualKey is > 0 and <= 0xFF ? settings.InventoryVirtualKey : 0x46;
            var clicks = Math.Clamp(settings.ClickCount, 1, 50);
            var openDelay = Math.Clamp(settings.OpenDelayMs, 0, 10000);
            var clickDelay = Math.Clamp(settings.PerClickDelayMs, 0, 5000);

            var steps = new List<MacroStep>
            {
                MacroStep.FocusGameWindow(),
                MacroStep.KeyPress(inventoryVk)
            };
            if (openDelay > 0)
                steps.Add(MacroStep.Delay(openDelay));

            for (var i = 0; i < clicks; i++)
            {
                steps.Add(MacroStep.ClickAt(point.X, point.Y));
                if (clickDelay > 0 && i < clicks - 1)
                    steps.Add(MacroStep.Delay(clickDelay));
            }

            // Let the last click land before the inventory key closes the UI again.
            if (clickDelay > 0)
                steps.Add(MacroStep.Delay(clickDelay));
            steps.Add(MacroStep.KeyPress(inventoryVk));

            return new MacroSequence
            {
                Name = "Fast Transfer",
                Steps = steps,
                RepeatCount = 1,
                LoopDelayMs = 0,
                InterStepDelayMs = 0,
                DelayJitter = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the Fast Transfer sequence");
            return null;
        }
    }
}
