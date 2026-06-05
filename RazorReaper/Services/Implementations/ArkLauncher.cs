using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

public sealed class ArkLauncher : IArkLauncher
{
    private const string ArkAppId = "346110";

    // Anti-cheat signals. BattlEye runs a service process and injects its client DLL into the game.
    private static readonly string[] BattlEyeProcessNames = { "BEService", "BEService_x64", "BEDaisy", "BattlEye" };
    private static readonly string[] BattlEyeModuleFragments = { "beclient", "bedaisy", "beservice", "battleye" };

    private readonly IArkPathProvider _arkPaths;
    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly ILogger<ArkLauncher> _logger;

    public ArkLauncher(
        IArkPathProvider arkPaths,
        IProcessService process,
        IOptions<AppConfiguration> config,
        ILogger<ArkLauncher> logger)
    {
        _arkPaths = arkPaths;
        _process = process;
        _config = config;
        _logger = logger;
    }

    public string GetSteamLaunchOptions()
    {
        try
        {
            var steam = GetSteamPath();
            if (steam is null) return "";
            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) return "";

            foreach (var dir in Directory.EnumerateDirectories(userdata))
            {
                var lc = Path.Combine(dir, "config", "localconfig.vdf");
                if (!File.Exists(lc)) continue;
                var opt = StripCulture(ExtractLaunchOptions(File.ReadAllText(lc)));
                if (!string.IsNullOrWhiteSpace(opt)) return opt;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading Steam launch options failed");
        }
        return "";
    }

    private static string ExtractLaunchOptions(string vdf)
    {
        var idx = vdf.IndexOf($"\"{ArkAppId}\"", StringComparison.Ordinal);
        if (idx < 0) return "";
        var seg = vdf.Substring(idx, Math.Min(800, vdf.Length - idx));
        var m = Regex.Match(seg, "\"LaunchOptions\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    // Drop culture=* options — that's the custom-font switch the user doesn't want (giant/ugly font).
    private static string StripCulture(string opts)
    {
        if (string.IsNullOrWhiteSpace(opts)) return "";
        var kept = opts.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.TrimStart('-').StartsWith("culture=", StringComparison.OrdinalIgnoreCase));
        return string.Join(' ', kept).Trim();
    }

    public bool IsBattlEyeActive()
    {
        // 1) A BattlEye service/process is running.
        foreach (var name in BattlEyeProcessNames)
        {
            if (_process.IsProcessRunning(name)) return true;
        }

        // 2) BattlEye's client DLL is loaded inside ShooterGame.
        var procs = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    foreach (ProcessModule m in p.Modules)
                    {
                        var n = m.ModuleName.ToLowerInvariant();
                        foreach (var frag in BattlEyeModuleFragments)
                        {
                            if (n.Contains(frag, StringComparison.Ordinal)) return true;
                        }
                    }
                }
                catch
                {
                    // Module enumeration can fail; fall through (other signals already checked).
                }
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        return false;
    }

    public ArkLaunchResult LaunchNoBattlEye()
    {
        if (_process.IsProcessRunning(_config.Value.Ark.GameProcessName))
            return new ArkLaunchResult(false, "ARK is already running.");

        var ark = _arkPaths.FindArkPath();
        if (string.IsNullOrEmpty(ark))
            return new ArkLaunchResult(false, "ARK installation not found.");

        var exe = Path.Combine(ark, _config.Value.Ark.ExecutableRelativePath);
        if (!File.Exists(exe))
            return new ArkLaunchResult(false, $"ShooterGame.exe not found at {exe}.");

        var opts = GetSteamLaunchOptions();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = opts,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false
            };
            Process.Start(psi);
            _logger.LogInformation("Launched ARK No-BattlEye with options '{Opts}'", opts);
            return new ArkLaunchResult(true, string.IsNullOrWhiteSpace(opts)
                ? "Launched ARK (No BattlEye, Unofficial only)."
                : $"Launched ARK (No BattlEye) with your launch options: {opts}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No-BattlEye launch failed");
            return new ArkLaunchResult(false, $"Launch failed: {ex.Message}");
        }
    }

    private static string? GetSteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string p && !string.IsNullOrEmpty(p))
                return p.Replace('/', '\\');
        }
        catch { /* fall through to common locations */ }

        foreach (var c in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }
}
