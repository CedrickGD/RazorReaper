using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using RazorReaper.Configuration;
using RazorReaper.Models;
using RazorReaper.Services.Automation;

namespace RazorReaper.Services.Implementations.Game;

/// <summary>
/// Console-injection engine. Owns the window focus + console-key resolution + clipboard
/// save/restore that previously lived inline in Game.razor.
///
/// Keystrokes go through <see cref="IInputSimulator"/> rather than raw <c>keybd_event</c>: the
/// command text is typed as Unicode key events, so every character arrives as itself instead of
/// as whatever virtual key shares its code point, and the keys we do press as keys (the console
/// key, Enter, Ctrl+V) carry scan codes and are recorded in <see cref="SynthesizedInput"/>, so
/// our own global hotkeys no longer fire on our own keystrokes.
/// </summary>
public sealed class GameConsoleService : IGameConsoleService
{
    private const int SW_RESTORE = 9;
    private const byte VK_TAB = 0x09;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const string DefaultConsoleKey = "TAB";
    private const string ConsoleKeyPreferenceKey = "GameConsoleKey";

    /// <summary>Down-to-up gap for the keys we press as keys. One 60 Hz frame is ~17 ms.</summary>
    private const int KeyHoldMs = 50;

    /// <summary>Gap between typed characters — ARK's console drops text typed faster than this.</summary>
    private const int TypeDelayMs = 25;

    /// <summary>Time the console overlay needs to open and take the caret.</summary>
    private const int ConsoleOpenSettleMs = 300;

    /// <summary>Time the typed text needs to land in the field before Enter commits it.</summary>
    private const int BeforeEnterSettleMs = 200;

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

    private readonly IProcessService _process;
    private readonly IInputSimulator _input;
    private readonly IOptions<AppConfiguration> _config;
    private readonly ILogger<GameConsoleService> _logger;

    private byte _consoleKeyCode = VK_TAB;

    public GameConsoleService(
        IProcessService process,
        IInputSimulator input,
        IOptions<AppConfiguration> config,
        ILogger<GameConsoleService> logger)
    {
        _process = process;
        _input = input;
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

                return await SendToFocusedConsoleAsync(command, useClipboard, ct);
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

    /// <summary>
    /// The keystroke half of <see cref="SendCommandAsync"/>: opens the console, puts the command
    /// into it, presses Enter. Split off from the window handling so it can be driven against a
    /// recording input layer — nothing below this line touches a window handle.
    /// </summary>
    internal async Task<bool> SendToFocusedConsoleAsync(string command, bool useClipboard, CancellationToken ct)
    {
        await _input.KeyPressAsync(_consoleKeyCode, KeyHoldMs, ct: ct);
        await _input.DelayAsync(ConsoleOpenSettleMs, ct: ct);

        if (useClipboard)
        {
            if (!await PasteCommandAsync(command, ct)) return false;
        }
        else
        {
            await TypeCommandAsync(command, ct);
        }

        await _input.DelayAsync(BeforeEnterSettleMs, ct: ct);
        await _input.KeyPressAsync(VK_RETURN, KeyHoldMs, ct: ct);
        return true;
    }

    /// <summary>
    /// Types the command verbatim as Unicode key events.
    ///
    /// This used to cast each character to a virtual key and press that, which is only ever right
    /// for A–Z and 0–9. '.' is 0x2E, and 0x2E is VK_DELETE — so "t.maxfps 1", the command the
    /// Noglin counter-measure is built on, left the app as "tmaxfps 1" with a Delete keypress in
    /// the middle of it, and the console never saw the command at all. KEYEVENTF_UNICODE carries
    /// the character itself and does not consult the keyboard layout, so non-US layouts type
    /// correctly too. The text is no longer lowercased on the way out either: ARK's command names
    /// are case-insensitive, but the blueprint paths people paste are not.
    /// </summary>
    private Task TypeCommandAsync(string command, CancellationToken ct)
        => _input.TypeTextAsync(command, TypeDelayMs, ct: ct);

    private async Task<bool> PasteCommandAsync(string command, CancellationToken ct)
    {
        string? previousText = null;
        try
        {
            try { previousText = await Clipboard.Default.GetTextAsync(); }
            catch { previousText = null; }

            await Clipboard.Default.SetTextAsync(command);
            await Task.Delay(50);

            await SendPasteShortcutAsync(ct);
            await Task.Delay(120, ct);

            if (!string.IsNullOrEmpty(previousText))
            {
                try { await Clipboard.Default.SetTextAsync(previousText); }
                catch { /* ignore clipboard restore failures */ }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // A stop mid-paste still owes the user their clipboard back, but the cancellation
            // itself belongs to the caller — swallowing it here would report a plain failure.
            if (!string.IsNullOrEmpty(previousText))
            {
                try { await Clipboard.Default.SetTextAsync(previousText); }
                catch { /* ignore clipboard restore failures */ }
            }
            throw;
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

    private async Task SendPasteShortcutAsync(CancellationToken ct)
    {
        _input.KeyDown(VK_CONTROL);
        try
        {
            await _input.KeyPressAsync(VK_V, KeyHoldMs, ct: ct);
        }
        finally
        {
            // A stuck Ctrl turns the user's next keystroke into a shortcut.
            _input.KeyUp(VK_CONTROL);
        }
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
