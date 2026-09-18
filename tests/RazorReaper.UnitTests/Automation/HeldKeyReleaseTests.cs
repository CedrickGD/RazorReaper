using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// A key the app pressed and never released does not stop when the script does: the character
/// keeps sprinting into a wall, or Shift stays down and the user's next click opens the wrong
/// menu. Nothing in the app notices, because from its side the script is off.
///
/// So the scaffold keeps the register of what is down, and every exit — a stop, a run loop that
/// threw, a Dispose — drains it.
///
/// Shares <c>ArkKeyDefaults</c> with <see cref="ArkKeyScanTests"/>: Auto-Walk resolves its forward
/// and sprint keys through a process-wide static that those tests swap out. See the note there.
/// </summary>
[Collection("ArkKeyDefaults")]
public sealed class HeldKeyReleaseTests
{
    private const int VkW = 0x57;
    private const int VkShift = 0xA0;

    [Fact]
    public async Task StoppingReleasesTheKeyTheScriptWasHolding()
    {
        var input = new RecordingInputSimulator();
        using var script = new KeyHoldingScript(input, VkW, Parts());

        Assert.True(script.Start());
        await script.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { VkW }, script.HeldNow(input));

        script.Stop();

        Assert.Empty(input.HeldKeys);
    }

    /// <summary>
    /// The case the per-script try/finally blocks were never going to cover: the loop does not
    /// unwind cleanly, it dies.
    /// </summary>
    [Fact]
    public async Task ARunLoopThatThrowsMidHoldStillReleases()
    {
        var input = new RecordingInputSimulator();
        using var script = new KeyHoldingScript(input, VkW, Parts()) { ThrowAfterHolding = true };

        script.Start();
        await WaitUntil(() => !script.IsRunning);

        Assert.Empty(input.HeldKeys);
    }

    [Fact]
    public async Task DisposingWithoutAStopStillReleases()
    {
        var input = new RecordingInputSimulator();
        var script = new KeyHoldingScript(input, VkShift, Parts());

        Assert.True(script.Start());
        await script.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));

        script.Dispose();

        Assert.Empty(input.HeldKeys);
    }

    /// <summary>
    /// Auto-Walk is the script that holds a key for its whole run — it is the one a stuck key
    /// costs the most, and the one users toggle most often.
    /// </summary>
    [Fact]
    public async Task AutoWalkLetsGoOfForwardWhenItIsStopped()
    {
        var input = new RecordingInputSimulator();
        var gate = new FakeForegroundGate(gameIsForeground: true);
        using var script = new AutoWalkScript(
            input,
            gate,
            new NullAutomationHotkeyService(),
            new RecordingNotificationService(),
            new RecordingActivityService(),
            English(),
            NullLogger<AutoWalkScript>.Instance)
        {
            ForwardKey = "W",
            Sprint = true,
            SprintKey = "LeftShift"
        };

        Assert.True(script.Start());
        await WaitUntil(() => input.HeldKeys.Contains(VkW));
        Assert.Contains(VkShift, input.HeldKeys);

        script.Stop();

        Assert.Empty(input.HeldKeys);
    }

    /// <summary>
    /// Losing focus already released the keys, and the scaffold must not double-count: a later
    /// stop has nothing left to release, and a later refocus takes them again.
    /// </summary>
    [Fact]
    public async Task AutoWalkLetsGoWhenTheGameLosesFocusAndTakesTheKeyBackAfterwards()
    {
        var input = new RecordingInputSimulator();
        var gate = new FakeForegroundGate(gameIsForeground: true);
        using var script = new AutoWalkScript(
            input,
            gate,
            new NullAutomationHotkeyService(),
            new RecordingNotificationService(),
            new RecordingActivityService(),
            English(),
            NullLogger<AutoWalkScript>.Instance)
        {
            ForwardKey = "W",
            Sprint = false
        };

        Assert.True(script.Start());
        await WaitUntil(() => input.HeldKeys.Contains(VkW));

        gate.GameIsForeground = false;
        await WaitUntil(() => !input.HeldKeys.Contains(VkW));

        gate.GameIsForeground = true;
        await WaitUntil(() => input.HeldKeys.Contains(VkW));

        script.Stop();
        Assert.Empty(input.HeldKeys);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The script did not reach the expected state within 10 s.");
    }

    private static ILocalizer English()
        => new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

    private static ScriptParts Parts() => new(
        new FakeForegroundGate(gameIsForeground: true),
        new NullAutomationHotkeyService(),
        new RecordingNotificationService(),
        new RecordingActivityService(),
        English(),
        NullLogger.Instance);

    private sealed record ScriptParts(
        IForegroundGate Foreground,
        IAutomationHotkeyService Hotkeys,
        INotificationService Notifications,
        IActivityService Activity,
        ILocalizer Localizer,
        ILogger Logger);

    /// <summary>A script whose entire job is to take a key and then be interrupted.</summary>
    private sealed class KeyHoldingScript : AutomationScriptBase
    {
        private readonly IInputSimulator _input;
        private readonly int _virtualKey;

        public KeyHoldingScript(IInputSimulator input, int virtualKey, ScriptParts parts)
            : base("test-keyhold", "Key Hold", string.Empty,
                   parts.Foreground, parts.Hotkeys, parts.Notifications, parts.Activity, parts.Localizer, parts.Logger)
        {
            _input = input;
            _virtualKey = virtualKey;
        }

        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThrowAfterHolding { get; init; }

        /// <summary>The scaffold's register, read from outside for the assertion.</summary>
        public int[] HeldNow(IInputSimulator input) => IsKeyHeld(input, _virtualKey) ? new[] { _virtualKey } : Array.Empty<int>();

        protected override async Task RunAsync(CancellationToken ct)
        {
            HoldKey(_input, _virtualKey);
            Holding.TrySetResult();

            if (ThrowAfterHolding) throw new InvalidOperationException("a tick blew up while the key was down");

            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
