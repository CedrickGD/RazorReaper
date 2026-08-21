using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests;

[Collection("SynthesizedInput")]
public class SynthesizedInputTests : IDisposable
{
    private const int VkW = 0x57;
    private const int VkT = 0x54;

    public SynthesizedInputTests() => SynthesizedInput.Reset();

    public void Dispose() => SynthesizedInput.Reset();

    [Fact]
    public void AKeyWeAreNotTouchingIsNotOurs()
    {
        Assert.False(SynthesizedInput.IsActive(VkW));
    }

    [Fact]
    public void AHeldKeyStaysOursForAsLongAsWeHoldIt()
    {
        // The Auto-Walk case: W is held for minutes, and Ctrl+Alt+W must not fire in that window.
        SynthesizedInput.Pressed(VkW);

        Assert.True(SynthesizedInput.IsActive(VkW));
        Assert.False(SynthesizedInput.IsActive(VkT));
    }

    [Fact]
    public void ReleasingClearsItAfterTheGraceWindow()
    {
        SynthesizedInput.Pressed(VkW);
        SynthesizedInput.Released(VkW);

        // Still ours immediately after release — key-up and WM_HOTKEY race each other.
        Assert.True(SynthesizedInput.IsActive(VkW));

        Thread.Sleep(90);
        Assert.False(SynthesizedInput.IsActive(VkW));
    }

    [Fact]
    public void NestedPressesNeedMatchingReleases()
    {
        // Two scripts can hold the same key; the first release must not hand it back early.
        SynthesizedInput.Pressed(VkW);
        SynthesizedInput.Pressed(VkW);
        SynthesizedInput.Released(VkW);

        Assert.True(SynthesizedInput.IsActive(VkW));
    }

    [Fact]
    public void APressAfterAReleaseMakesItOursAgainImmediately()
    {
        SynthesizedInput.Pressed(VkW);
        SynthesizedInput.Released(VkW);
        SynthesizedInput.Pressed(VkW);

        Assert.True(SynthesizedInput.IsActive(VkW));
    }

    [Fact]
    public void AnUnbalancedReleaseDoesNotGoNegative()
    {
        SynthesizedInput.Released(VkW);
        SynthesizedInput.Pressed(VkW);
        SynthesizedInput.Released(VkW);

        Thread.Sleep(90);
        Assert.False(SynthesizedInput.IsActive(VkW));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidKeysAreIgnored(int vk)
    {
        SynthesizedInput.Pressed(vk);

        Assert.False(SynthesizedInput.IsActive(vk));
    }

    [Fact]
    public void KeysAreTrackedIndependently()
    {
        // The cascade needed both: Auto-Walk holding W while Exo Suit tapped T.
        SynthesizedInput.Pressed(VkW);
        SynthesizedInput.Pressed(VkT);
        SynthesizedInput.Released(VkT);
        Thread.Sleep(90);

        Assert.True(SynthesizedInput.IsActive(VkW));
        Assert.False(SynthesizedInput.IsActive(VkT));
    }
}
