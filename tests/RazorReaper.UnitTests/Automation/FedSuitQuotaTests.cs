using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// One start, one quota.
///
/// Fed Suit is the only script in the list that predates the list: the macro brought its own
/// <c>fed_suit</c> quota with it, and the scaffold charges <c>input_scripts</c> for every script
/// start. Wrapping one in the other meant a free user pressing the Fed Suit tile once spent two
/// of their monthly runs, and nothing on screen said so — the quota chips just moved twice.
///
/// Shares <c>ArkKeyDefaults</c> with <see cref="ArkKeyScanTests"/>: the macro rescans its open and
/// transfer keys through a process-wide static that those tests swap out. See the note there.
/// </summary>
[Collection("ArkKeyDefaults")]
public sealed class FedSuitQuotaTests
{
    [Fact]
    public async Task StartingFedSuitFromTheScriptsListCostsExactlyOneRun()
    {
        var gate = new CountingUsageGateService();
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine, gate);
        using var script = Script(macro, gate);

        Assert.True(script.Start());
        await WaitUntil(() => engine.Runner("fed-suit").RunCount == 1);

        // The scaffold's charge trails the start, so give the second one every chance to appear.
        await Task.Delay(150);

        Assert.Equal(new[] { UsageFeatures.InputScripts }, gate.Consumed);
        Assert.Equal(0, gate.CountOf(UsageFeatures.FedSuit));

        script.Stop();
    }

    /// <summary>
    /// The macro is still startable on its own — the command palette does exactly that — and that
    /// path has no scaffold above it, so it keeps metering itself.
    /// </summary>
    [Fact]
    public async Task StartingTheMacroOnItsOwnStillChargesItsOwnQuota()
    {
        var gate = new CountingUsageGateService();
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine, gate);

        Assert.True(macro.Start());
        await WaitUntil(() => gate.Consumed.Count == 1);
        await Task.Delay(150);

        Assert.Equal(new[] { UsageFeatures.FedSuit }, gate.Consumed);

        macro.Stop();
    }

    /// <summary>A stop and a restart is a second run, and costs a second one — but only one.</summary>
    [Fact]
    public async Task ARestartCostsOneMore()
    {
        var gate = new CountingUsageGateService();
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine, gate);
        using var script = Script(macro, gate);

        Assert.True(script.Start());
        await WaitUntil(() => engine.Runner("fed-suit").RunCount == 1);
        script.Stop();
        await WaitUntil(() => !macro.IsRunning);

        Assert.True(script.Start());
        await WaitUntil(() => engine.Runner("fed-suit").RunCount == 2);
        await Task.Delay(150);

        Assert.Equal(2, gate.CountOf(UsageFeatures.InputScripts));
        Assert.Equal(0, gate.CountOf(UsageFeatures.FedSuit));

        script.Stop();
    }

    private static FedSuitMacro Macro(IMacroEngine engine, IUsageGateService gate)
        => new(
            engine,
            new FakeGameDisplayService(),
            new FakeArkPathProvider(),
            new FakeScreenSampler(),
            new RecordingNotificationService(),
            new RecordingActivityService(),
            gate,
            English(),
            NullLogger<FedSuitMacro>.Instance);

    private static FedSuitScript Script(IFedSuitMacro macro, IUsageGateService gate)
    {
        var script = new FedSuitScript(
            macro,
            new FakeForegroundGate(gameIsForeground: true),
            new NullAutomationHotkeyService(),
            new RecordingNotificationService(),
            new RecordingActivityService(),
            English(),
            NullLogger<FedSuitScript>.Instance);

        // Outside a MAUI application there is no service provider to find the gate in, so the
        // scaffold's own charge would silently never happen and the test would prove nothing.
        script.ResolveUsageGate = () => gate;
        return script;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Fed Suit did not reach the expected state within 10 s.");
    }

    private static ILocalizer English()
        => new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));
}
