using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using RazorReaper.Configuration;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.Game;

/// <summary>
/// Console-injection engine. Owns the user32 P/Invoke + console-key resolution + clipboard
/// save/restore that previously lived inline in Game.razor. Behavior is preserved 1:1
/// (same focus sequence, delays, paste/type logic, clipboard restore).
/// </summary>
public sealed class GameConsoleService : IGameConsoleService
{
    private const int SW_RESTORE = 9;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_TAB = 0x09;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const string DefaultConsoleKey = "TAB";
    private const string ConsoleKeyPreferenceKey = "GameConsoleKey";

    private static readonly Dictionary<string, byte> ConsoleKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TAB"] = VK_TAB,
        ["ENTER"] = VK_RETURN,
        ["RETURN"] = VK_RETURN,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20,
        ["BACKSPACE"] = 0x08,
        ["TILDE"] = 0xC0,
        ["`"] = 0xC0,
        ["~"] = 0xC0,
        ["GRAVE"] = 0xC0,
        ["BACKQUOTE"] = 0xC0,
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PAGEUP"] = 0x21,
        ["PGUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["PGDN"] = 0x22
    };

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly ILogger<GameConsoleService> _logger;

    private byte _consoleKeyCode = VK_TAB;

    public GameConsoleService(IProcessService process, IOptions<AppConfiguration> config, ILogger<GameConsoleService> logger)
    {
        _process = process;
        _config = config;
        _logger = logger;
        RefreshConsoleKey();
    }

    public bool IsGameRunning => _process.IsProcessRunning(_config.Value.Ark.GameProcessName);

    public void RefreshConsoleKey()
    {
        try
        {
            var saved = Preferences.Get(ConsoleKeyPreferenceKey, DefaultConsoleKey);
            _consoleKeyCode = TryGetVirtualKeyCode(NormalizeConsoleKey(saved), out var code) ? code : VK_TAB;
        }
        catch
        {
            _consoleKeyCode = VK_TAB;
        }
    }

    public async Task<bool> SendCommandAsync(string command, bool useClipboard, CancellationToken ct = default)
    {
        try
        {
            var processName = _config.Value.Ark.GameProcessName;
            var processes = _process.GetProcessesByName(processName);
            try
            {
                if (processes.Length == 0) return false;

                var hwnd = processes[0].MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    _logger.LogWarning("ARK is running but has no MainWindowHandle (minimized to tray / fullscreen-exclusive?). Can't focus the console.");
                    return false;
                }

                ShowWindow(hwnd, SW_RESTORE);
                await Task.Delay(100, ct);
                SetForegroundWindow(hwnd);
                await Task.Delay(500, ct);

                keybd_event(_consoleKeyCode, 0, 0, UIntPtr.Zero);
                await Task.Delay(50, ct);
                keybd_event(_consoleKeyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(300, ct);

                if (useClipboard)
                {
                    if (!await PasteCommandAsync(command)) return false;
                }
                else
                {
                    await TypeCommandAsync(command, ct);
                }

                await Task.Delay(200, ct);
                keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                await Task.Delay(50, ct);
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                return true;
            }
            finally
            {
                foreach (var p in processes) p?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending console command: {Command}", command);
            return false;
        }
    }

    public async Task<ConsoleBatchResult> SendCommandsAsync(IEnumerable<string> commands, bool useClipboard, CancellationToken ct = default)
    {
        var list = commands?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>();
        var running = IsGameRunning;
        if (!running || list.Count == 0)
            return new ConsoleBatchResult(list.Count, 0, list.Count, running, list);

        var sent = 0;
        var failed = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await SendCommandAsync(list[i], useClipboard, ct);
            if (ok) sent++;
            else failed.Add(list[i]);

            if (i < list.Count - 1)
                await Task.Delay(150, ct);
        }

        return new ConsoleBatchResult(list.Count, sent, failed.Count, running, failed);
    }

    private async Task TypeCommandAsync(string command, CancellationToken ct)
    {
        foreach (var c in command.ToLowerInvariant())
        {
            SendChar(c);
            await Task.Delay(25, ct);
        }
    }

    private static void SendChar(char c)
    {
        var vk = (byte)char.ToUpperInvariant(c);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private async Task<bool> PasteCommandAsync(string command)
    {
        string? previousText = null;
        try
        {
            try { previousText = await Clipboard.Default.GetTextAsync(); }
            catch { previousText = null; }

            await Clipboard.Default.SetTextAsync(command);
            await Task.Delay(50);

            SendPasteShortcut();
            await Task.Delay(120);

            if (!string.IsNullOrEmpty(previousText))
            {
                try { await Clipboard.Default.SetTextAsync(previousText); }
                catch { /* ignore clipboard restore failures */ }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pasting game command");
            if (!string.IsNullOrEmpty(previousText))
            {
                try { await Clipboard.Default.SetTextAsync(previousText); }
                catch { /* ignore clipboard restore failures */ }
            }
            return false;
        }
    }

    private static void SendPasteShortcut()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static string NormalizeConsoleKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return DefaultConsoleKey;
        var trimmed = rawKey.Trim();
        return trimmed.Length == 1 ? trimmed.ToUpperInvariant() : trimmed.Replace(" ", "").ToUpperInvariant();
    }

    private static bool TryGetVirtualKeyCode(string key, out byte keyCode)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            keyCode = VK_TAB;
            return false;
        }

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                keyCode = (byte)c;
                return true;
            }
        }

        if (ConsoleKeyMap.TryGetValue(key, out var mapped))
        {
            keyCode = mapped;
            return true;
        }

        keyCode = VK_TAB;
        return false;
    }
}
