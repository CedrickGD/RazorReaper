using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// What a synthesized key event actually says on the wire.
///
/// ARK reads raw input, not the cooked Windows messages, so the scan code is the half that
/// matters to it — and a KEYBDINPUT that fills in <c>wScan</c> without setting KEYEVENTF_SCANCODE
/// is telling Windows to ignore that field and derive its own from the virtual key. The flag
/// policy lives in a pure function so it can be read back here without putting a keystroke on the
/// machine running the tests.
/// </summary>
public sealed class KeyEventShapeTests
{
    private const int VkW = 0x57;
    private const int VkRight = 0x27;       // extended
    private const int VkRightControl = 0xA3; // extended

    [Fact]
    public void AKeyWithAScanCodeSendsThatScanCode()
    {
        var (scan, flags) = InputSimulator.BuildKeyFlags(VkW, scanCode: 0x11, keyUp: false);

        Assert.Equal(0x11, scan);
        Assert.True((flags & InputSimulator.KEYEVENTF_SCANCODE) != 0, "scan code present but KEYEVENTF_SCANCODE not set");
        Assert.True((flags & InputSimulator.KEYEVENTF_KEYUP) == 0);
    }

    /// <summary>
    /// KEYEVENTF_SCANCODE with a zero scan code is a key event that presses nothing at all, so a
    /// virtual key the layout has no scan code for has to keep the virtual-key path.
    /// </summary>
    [Fact]
    public void AKeyWithoutAScanCodeStaysOnTheVirtualKeyPath()
    {
        var (scan, flags) = InputSimulator.BuildKeyFlags(VkW, scanCode: 0, keyUp: false);

        Assert.Equal(0, scan);
        Assert.True((flags & InputSimulator.KEYEVENTF_SCANCODE) == 0);
    }

    [Fact]
    public void TheReleaseCarriesTheSameScanCodeAsThePress()
    {
        var (downScan, downFlags) = InputSimulator.BuildKeyFlags(VkW, scanCode: 0x11, keyUp: false);
        var (upScan, upFlags) = InputSimulator.BuildKeyFlags(VkW, scanCode: 0x11, keyUp: true);

        Assert.Equal(downScan, upScan);
        Assert.True((upFlags & InputSimulator.KEYEVENTF_KEYUP) != 0);
        Assert.True((upFlags & InputSimulator.KEYEVENTF_SCANCODE) != 0);
        Assert.Equal(downFlags | InputSimulator.KEYEVENTF_KEYUP, upFlags);
    }

    /// <summary>
    /// An extended key shares its base scan code with a numpad key — Right arrow and numpad 6 are
    /// both 0x4D — so dropping the extended flag once the scan code is the thing being sent would
    /// turn arrow keys into numpad presses.
    /// </summary>
    [Theory]
    [InlineData(VkRight)]
    [InlineData(VkRightControl)]
    public void AnExtendedKeyKeepsBothFlags(int virtualKey)
    {
        var (_, flags) = InputSimulator.BuildKeyFlags(virtualKey, scanCode: 0x4D, keyUp: false);

        Assert.True((flags & InputSimulator.KEYEVENTF_EXTENDEDKEY) != 0);
        Assert.True((flags & InputSimulator.KEYEVENTF_SCANCODE) != 0);
    }

    [Fact]
    public void AnOrdinaryKeyIsNotMarkedExtended()
    {
        var (_, flags) = InputSimulator.BuildKeyFlags(VkW, scanCode: 0x11, keyUp: false);

        Assert.True((flags & InputSimulator.KEYEVENTF_EXTENDEDKEY) == 0);
    }

    /// <summary>
    /// ARK samples input once per rendered frame. A press whose down and up land inside the same
    /// frame is a press the game never sees, so no caller gets to ask for less than one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public void APressIsNeverShorterThanAFrame(int requested)
    {
        Assert.True(InputSimulator.EffectiveHoldMs(requested) >= 17, "a hold shorter than a 60 Hz frame got through");
        Assert.Equal(InputSimulator.MinHoldMs, InputSimulator.EffectiveHoldMs(requested));
    }

    [Fact]
    public void ALongerHoldIsLeftAlone()
    {
        Assert.Equal(250, InputSimulator.EffectiveHoldMs(250));
    }
}
