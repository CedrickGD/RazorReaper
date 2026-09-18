using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;
using RazorReaper.Services;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Implementations.Game;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The Noglin counter-measure is the script that lives or dies on the console: its whole defence
/// is <c>t.maxfps 1</c> reaching the game while someone is riding your character. It sends three
/// console commands over a run — throttle, restore after the icon clears, restore on stop — and
/// every one of them contains a '.' and a ' ', the two characters the old typing path destroyed.
/// So the script is driven for real here, and the keystrokes are read off the input layer at the
/// far end of the console service rather than off the script's own return values.
/// </summary>
public sealed class NoglinConsoleCommandTests
{
    private const int VkDelete = 0x2E;

    [Fact]
    public async Task AllThreeOfNoglinsCommandsReachTheInputLayerIntact()
    {
        var input = new RecordingInputSimulator();
        var console = new ConsoleThroughTheRealTypingPath(input);
        var sampler = new FakeScreenSampler();
        var calibration = new FakeCalibrationService();
        calibration.SetRegion("noglin-region", new Rectangle(10, 10, 40, 40));
        sampler.References.Add("noglin-region");

        using var script = new NoglinScript(
            console,
            sampler,
            calibration,
            new FakeForegroundGate(gameIsForeground: true),
            new NullAutomationHotkeyService(),
            new RecordingNotificationService(),
            new RecordingActivityService(),
            English(),
            NullLogger<NoglinScript>.Instance)
        {
            ScanIntervalMs = 100,
            RestoreAfterCleanScans = 1,
            ThrottledFps = 1,
            NormalFps = 1000
        };

        // The icon shows up: FPS goes to 1.
        sampler.TargetVisible = true;
        Assert.True(script.Start());
        await WaitUntil(() => console.Commands.Count >= 1);

        // The icon clears: FPS goes back.
        sampler.TargetVisible = false;
        await WaitUntil(() => console.Commands.Count >= 2);

        // Stopping while unthrottled still restores — the third call.
        sampler.TargetVisible = true;
        await WaitUntil(() => console.Commands.Count >= 3);
        script.Stop();
        await WaitUntil(() => console.Commands.Count >= 4);

        var commands = console.Commands;
        Assert.Equal("t.maxfps 1", commands[0]);
        Assert.Equal("t.maxfps 1000", commands[1]);
        Assert.Equal("t.maxfps 1", commands[2]);
        Assert.Equal("t.maxfps 1000", commands[3]);

        // Every command went out as text, character for character.
        var typed = input.Events.OfType<SimulatedInput.Text>().Select(t => t.Value).ToArray();
        Assert.Equal(commands, typed);
        Assert.All(typed, t => Assert.Contains('.', t));
        Assert.All(typed, t => Assert.Contains(' ', t));

        // And nothing was pressed as a key except the console key and Enter — no stray Delete
        // standing in for the dot.
        Assert.DoesNotContain(VkDelete, input.Events.OfType<SimulatedInput.KeyPress>().Select(p => p.VirtualKey));
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

    /// <summary>
    /// Writes down the command Noglin asked for, then hands it to the real
    /// <see cref="GameConsoleService"/> typing path so the test sees both halves: what the script
    /// meant to say, and what the keyboard actually produced.
    /// </summary>
    private sealed class ConsoleThroughTheRealTypingPath : IGameConsoleService
    {
        private readonly GameConsoleService _real;
        private readonly object _sync = new();
        private readonly List<string> _commands = new();

        public ConsoleThroughTheRealTypingPath(RecordingInputSimulator input)
            => _real = new GameConsoleService(
                new UnusedProcessService(),
                input,
                Options.Create(new AppConfiguration()),
                NullLogger<GameConsoleService>.Instance);

        public IReadOnlyList<string> Commands
        {
            get { lock (_sync) return _commands.ToArray(); }
        }

        public bool IsGameRunning => true;

        public void RefreshConsoleKey() => _real.RefreshConsoleKey();

        public async Task<bool> SendCommandAsync(string command, bool useClipboard, CancellationToken ct = default)
        {
            lock (_sync) _commands.Add(command);
            return await _real.SendToFocusedConsoleAsync(command, useClipboard, ct);
        }

        public async Task<ConsoleBatchResult> SendCommandsAsync(IEnumerable<string> commands, bool useClipboard, CancellationToken ct = default)
        {
            var list = commands.ToList();
            foreach (var c in list) await SendCommandAsync(c, useClipboard, ct);
            return new ConsoleBatchResult(list.Count, list.Count, 0, true, Array.Empty<string>());
        }
    }

    private sealed class UnusedProcessService : IProcessService
    {
        public System.Diagnostics.Process[] GetProcessesByName(string processName) => Array.Empty<System.Diagnostics.Process>();

        public bool IsProcessRunning(string processName) => false;

        public string? GetExecutablePath(System.Diagnostics.Process process) => null;

        public System.Diagnostics.Process? Start(string filePath) => null;

        public void Kill(System.Diagnostics.Process process) { }
    }
}
