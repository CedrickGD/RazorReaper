using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

public sealed class ArkLauncher : IArkLauncher
{
    private const string ArkAppId = "346110";

    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly ILogger<ArkLauncher> _logger;

    public ArkLauncher(
        IProcessService process,
        IOptions<AppConfiguration> config,
        ILogger<ArkLauncher> logger)
    {
        _process = process;
        _config = config;
        _logger = logger;
    }

    public ArkLaunchResult LaunchNormal()
    {
        if (_process.IsProcessRunning(_config.Value.Ark.GameProcessName))
            return new ArkLaunchResult(false, "ARK is already running.");
        try
        {
            // Hand off to Steam exactly like clicking Play — preserves the user's launch option + BattlEye.
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{ArkAppId}",
                UseShellExecute = true
            });
            _logger.LogInformation("Launched ARK via Steam (normal launch).");
            return new ArkLaunchResult(true, "Launching ARK through Steam — pick your usual launch option in the popup.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Steam launch failed");
            return new ArkLaunchResult(false, $"Launch failed: {ex.Message}");
        }
    }
}
