using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations.Game;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Services;

/// <summary>
/// What actually leaves the app when a console command is "sent".
///
/// The console gives no read-back, so nothing downstream can tell a mistyped command from one the
/// game rejected — which is how <c>t.maxfps 1</c> spent its whole life arriving as <c>tmaxfps 1</c>
/// with a Delete keypress where the dot should have been. These tests read the keystrokes off the
/// input layer instead of trusting the return value.
/// </summary>
public sealed class GameConsoleTypingTests
{
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkDelete = 0x2E;

    [Fact]
    public async Task TheCommandIsTypedVerbatimDotsSpacesAndAll()
    {
        var input = new RecordingInputSimulator();
        var console = Console(input);

        var ok = await console.SendToFocusedConsoleAsync("t.maxfps 1", useClipboard: false, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("t.maxfps 1", input.TypedText);
    }

    /// <summary>
    /// The specific regression: '.' is 0x2E and 0x2E is VK_DELETE, so the old char-to-virtual-key
    /// cast pressed Delete in the middle of the command. Nothing in the sequence may be a key
    /// press standing in for a character.
    /// </summary>
    [Fact]
    public async Task NoCharacterIsSentAsAVirtualKey()
    {
        var input = new RecordingInputSimulator();
        var console = Console(input);

        await console.SendToFocusedConsoleAsync("t.maxfps 1", useClipboard: false, CancellationToken.None);

        var pressed = input.Events.OfType<SimulatedInput.KeyPress>().Select(p => p.VirtualKey).ToArray();

        // Only the console key and Enter are keys. Not '.', not ' ', not 't'.
        Assert.Equal(new[] { VkTab, VkReturn }, pressed);
        Assert.DoesNotContain(VkDelete, pressed);
    }

    [Fact]
    public async Task TheConsoleKeyOpensAndEnterCommitsAroundTheTypedText()
    {
        var input = new RecordingInputSimulator();
        var console = Console(input);

        await console.SendToFocusedConsoleAsync("t.maxfps 1000", useClipboard: false, CancellationToken.None);

        var shape = input.Events
            .Where(e => e is SimulatedInput.KeyPress or SimulatedInput.Text)
            .Select(e => e switch
            {
                SimulatedInput.KeyPress k => $"key:{k.VirtualKey}",
                SimulatedInput.Text t => $"text:{t.Value}",
                _ => e.GetType().Name
            }).ToArray();

        Assert.Equal(new[] { $"key:{VkTab}", "text:t.maxfps 1000", $"key:{VkReturn}" }, shape);
    }

    /// <summary>
    /// Every press has to be held long enough for the game to sample it: ARK polls input per
    /// frame, and a down/up pair inside one frame is a press that never happened.
    /// </summary>
    [Fact]
    public async Task EveryKeyPressIsHeldForAtLeastAFrame()
    {
        var input = new RecordingInputSimulator();
        var console = Console(input);

        await console.SendToFocusedConsoleAsync("t.maxfps 1", useClipboard: false, CancellationToken.None);

        Assert.All(input.Events.OfType<SimulatedInput.KeyPress>(), p => Assert.True(p.HoldMs >= 17, $"held only {p.HoldMs} ms"));
    }

    /// <summary>
    /// ARK's command names are case-insensitive, but the blueprint paths people paste into the
    /// console are not — the old path lowercased everything on its way out.
    /// </summary>
    [Fact]
    public async Task TheCaseOfABlueprintPathSurvives()
    {
        var input = new RecordingInputSimulator();
        var console = Console(input);

        const string command = "GiveItem \"Blueprint'/Game/PrimalEarth/CoreBlueprints/Weapons/PrimalItem_WeaponPike.PrimalItem_WeaponPike'\" 1 0 0";
        await console.SendToFocusedConsoleAsync(command, useClipboard: false, CancellationToken.None);

        Assert.Equal(command, input.TypedText);
    }

    private static GameConsoleService Console(RecordingInputSimulator input)
        => new(
            new UnusedProcessService(),
            input,
            Options.Create(new AppConfiguration()),
            NullLogger<GameConsoleService>.Instance);

    /// <summary>
    /// <see cref="GameConsoleService.SendToFocusedConsoleAsync"/> is the half that runs after the
    /// window is already focused, so it never asks about processes.
    /// </summary>
    private sealed class UnusedProcessService : IProcessService
    {
        public System.Diagnostics.Process[] GetProcessesByName(string processName) => Array.Empty<System.Diagnostics.Process>();

        public bool IsProcessRunning(string processName) => false;

        public string? GetExecutablePath(System.Diagnostics.Process process) => null;

        public System.Diagnostics.Process? Start(string filePath) => null;

        public void Kill(System.Diagnostics.Process process) { }
    }
}
