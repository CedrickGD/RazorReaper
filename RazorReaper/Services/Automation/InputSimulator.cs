using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;

namespace RazorReaper.Services.Automation;

/// <summary>Mouse button identifier used by automation input APIs.</summary>
public enum MouseButton
{
    /// <summary>Left mouse button.</summary>
    Left,
    /// <summary>Right mouse button.</summary>
    Right,
    /// <summary>Middle mouse button (wheel click).</summary>
    Middle
}

/// <summary>
/// Pure user-space input synthesis via Win32 <c>SendInput</c>. All coordinates are physical
/// screen pixels (the process is per-monitor DPI aware). No memory reads/writes, no injection —
/// only what a keyboard/mouse driver could produce.
/// </summary>
public interface IInputSimulator
{
    /// <summary>Presses (and holds) the given virtual key.</summary>
    void KeyDown(int virtualKey);

    /// <summary>Releases the given virtual key.</summary>
    void KeyUp(int virtualKey);

    /// <summary>Presses and releases a virtual key with a short, optionally jittered hold time.</summary>
    /// <param name="virtualKey">Win32 virtual-key code.</param>
    /// <param name="holdMs">Milliseconds between down and up.</param>
    /// <param name="jitter">0..1 fractional randomization applied to <paramref name="holdMs"/>.</param>
    Task KeyPressAsync(int virtualKey, int holdMs = 40, double jitter = 0, CancellationToken ct = default);

    /// <summary>Types a string using Unicode key events (layout-independent), one UTF-16 unit at a time.</summary>
    /// <param name="perCharDelayMs">Base delay between characters.</param>
    /// <param name="jitter">0..1 fractional randomization applied per character delay.</param>
    Task TypeTextAsync(string text, int perCharDelayMs = 20, double jitter = 0, CancellationToken ct = default);

    /// <summary>Returns the current cursor position in physical screen pixels.</summary>
    Point GetCursorPosition();

    /// <summary>Moves the cursor to an absolute physical screen position (virtual-desktop aware).</summary>
    void MoveTo(int x, int y);

    /// <summary>Moves the cursor by a relative delta (raw-input friendly).</summary>
    void MoveBy(int dx, int dy);

    /// <summary>Presses (and holds) a mouse button at the current cursor position.</summary>
    void MouseDown(MouseButton button);

    /// <summary>Releases a mouse button at the current cursor position.</summary>
    void MouseUp(MouseButton button);

    /// <summary>Clicks a mouse button, optionally moving to a point first.</summary>
    /// <param name="at">Absolute screen point to click at, or null to click where the cursor is.</param>
    /// <param name="holdMs">Milliseconds the button is held down.</param>
    /// <param name="jitter">0..1 fractional randomization applied to internal delays.</param>
    Task ClickAsync(MouseButton button, Point? at = null, int holdMs = 30, double jitter = 0, CancellationToken ct = default);

    /// <summary>Scrolls the mouse wheel; positive detents scroll up, negative scroll down.</summary>
    void Scroll(int detents);

    /// <summary>Cancellable delay with optional fractional jitter (0..1) applied to the duration.</summary>
    Task DelayAsync(int delayMs, double jitter = 0, CancellationToken ct = default);

    /// <summary>Returns <paramref name="delayMs"/> randomized by ±(<paramref name="jitter"/> × delay), minimum 1 ms.</summary>
    int ApplyJitter(int delayMs, double jitter);
}

/// <summary>SendInput-backed implementation of <see cref="IInputSimulator"/>.</summary>
public sealed class InputSimulator : IInputSimulator
{
    private readonly ILogger<InputSimulator> _logger;

    public InputSimulator(ILogger<InputSimulator> logger)
    {
        _logger = logger;
    }

    // ─── Keyboard ──────────────────────────────────────────────────────────────

    public void KeyDown(int virtualKey) => SendKey(virtualKey, keyUp: false);

    public void KeyUp(int virtualKey) => SendKey(virtualKey, keyUp: true);

    public async Task KeyPressAsync(int virtualKey, int holdMs = 40, double jitter = 0, CancellationToken ct = default)
    {
        KeyDown(virtualKey);
        try
        {
            await DelayAsync(holdMs, jitter, ct);
        }
        finally
        {
            // Always release, even when cancelled mid-hold — a stuck key is worse than an extra event.
            KeyUp(virtualKey);
        }
    }

    public async Task TypeTextAsync(string text, int perCharDelayMs = 20, double jitter = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var c in text)
        {
            ct.ThrowIfCancellationRequested();
            if (c == '\r') continue;
            if (c == '\n')
            {
                await KeyPressAsync(0x0D, 30, jitter, ct); // VK_RETURN
            }
            else
            {
                SendUnicodeChar(c);
            }
            await DelayAsync(perCharDelayMs, jitter, ct);
        }
    }

    private void SendKey(int virtualKey, bool keyUp)
    {
        if (virtualKey <= 0 || virtualKey > 0xFF) return;

        // Record before dispatching: the hotkey pump must already know this key is ours by the
        // time Windows delivers the resulting WM_HOTKEY, or a script's own keystroke toggles
        // whatever hotkey happens to sit on that key.
        if (keyUp) SynthesizedInput.Released(virtualKey);
        else SynthesizedInput.Pressed(virtualKey);

        uint flags = keyUp ? KEYEVENTF_KEYUP : 0u;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    // Provide the scan code too — some games read scan codes rather than VKs.
                    wScan = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
        Dispatch(input);
    }

    private void SendUnicodeChar(char c)
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = UIntPtr.Zero }
            }
        };
        var up = down;
        up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        Dispatch(down, up);
    }

    private static bool IsExtendedKey(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,        // PgUp, PgDn, End, Home
        0x25 or 0x26 or 0x27 or 0x28 => true,        // arrows
        0x2C or 0x2D or 0x2E => true,                // PrintScreen, Insert, Delete
        0x5B or 0x5C or 0x5D => true,                // LWin, RWin, Apps
        0x90 or 0x6F => true,                        // NumLock, Numpad divide
        0xA3 or 0xA5 => true,                        // RControl, RMenu
        _ => false
    };

    // ─── Mouse ─────────────────────────────────────────────────────────────────

    public Point GetCursorPosition()
    {
        return GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : Point.Empty;
    }

    public void MoveTo(int x, int y)
    {
        // Normalize to the 0..65535 virtual-desktop space so multi-monitor setups work.
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 1 || vh <= 1)
        {
            SetCursorPos(x, y);
            return;
        }

        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
        if (!Dispatch(input))
        {
            // SendInput can be blocked by UIPI against elevated windows — fall back to SetCursorPos.
            SetCursorPos(x, y);
        }
    }

    public void MoveBy(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = 0, dwFlags = MOUSEEVENTF_MOVE, time = 0, dwExtraInfo = UIntPtr.Zero }
            }
        };
        Dispatch(input);
    }

    public void MouseDown(MouseButton button) => SendMouseButton(button, down: true);

    public void MouseUp(MouseButton button) => SendMouseButton(button, down: false);

    public async Task ClickAsync(MouseButton button, Point? at = null, int holdMs = 30, double jitter = 0, CancellationToken ct = default)
    {
        if (at.HasValue)
        {
            MoveTo(at.Value.X, at.Value.Y);
            await DelayAsync(15, jitter, ct);
        }
        MouseDown(button);
        try
        {
            await DelayAsync(holdMs, jitter, ct);
        }
        finally
        {
            MouseUp(button);
        }
    }

    public void Scroll(int detents)
    {
        if (detents == 0) return;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = unchecked((uint)(detents * WHEEL_DELTA)),
                    dwFlags = MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
        Dispatch(input);
    }

    private void SendMouseButton(MouseButton button, bool down)
    {
        uint flags = button switch
        {
            MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP
        };
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = UIntPtr.Zero }
            }
        };
        Dispatch(input);
    }

    // ─── Delays / jitter ───────────────────────────────────────────────────────

    public async Task DelayAsync(int delayMs, double jitter = 0, CancellationToken ct = default)
    {
        if (delayMs <= 0) return;
        await Task.Delay(ApplyJitter(delayMs, jitter), ct);
    }

    public int ApplyJitter(int delayMs, double jitter)
    {
        if (delayMs <= 0) return delayMs;
        jitter = Math.Clamp(jitter, 0, 1);
        if (jitter <= 0) return delayMs;
        var factor = 1.0 + ((Random.Shared.NextDouble() * 2.0) - 1.0) * jitter;
        return Math.Max(1, (int)Math.Round(delayMs * factor));
    }

    // ─── Dispatch ──────────────────────────────────────────────────────────────

    private bool Dispatch(params INPUT[] inputs)
    {
        try
        {
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                _logger.LogWarning("SendInput dispatched {Sent}/{Total} events (err=0x{Err:X})",
                    sent, inputs.Length, Marshal.GetLastWin32Error());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendInput dispatch failed");
            return false;
        }
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const int WHEEL_DELTA = 120;
    private const uint MAPVK_VK_TO_VSC = 0;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVEPOINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NATIVEPOINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
