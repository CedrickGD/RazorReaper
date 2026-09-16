using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// RR-E1003 came from ~60 dropped InvokeAsync dispatches, and the count sat flat across every
/// released version because nothing stopped the 61st from being written. This is that stop.
/// </summary>
public sealed partial class RenderDispatchUsageTests
{
    /// <summary>The only ways a component may spell a render dispatch.</summary>
    private static readonly string[] SanctionedPrefixes =
    [
        "await ",                    // the caller owns the Task
        "return ",                   // the Task is handed to whoever called (e.g. JS interop)
        "DispatchRender(() => ",     // observed by RenderDispatch
    ];

    [Fact]
    public void NoComponentDropsARenderDispatch()
    {
        var offenders = new List<string>();

        foreach (var path in ComponentFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]))
                {
                    continue;
                }

                foreach (var column in Occurrences(lines[i]))
                {
                    var before = lines[i][..column];

                    // "x.InvokeAsync(...)" is an EventCallback or IJSRuntime call, not a dispatch.
                    if (before.EndsWith('.'))
                    {
                        continue;
                    }

                    if (SanctionedPrefixes.Any(prefix => before.EndsWith(prefix, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The dispatch scan above allows <c>await InvokeAsync(...)</c>, because in an awaited chain
    /// the caller owns the fault. An <c>async void</c> method has no caller to own it: the fault
    /// is rethrown on whatever thread raised the service event, which is worse than RR-E1003.
    /// </summary>
    /// <remarks>
    /// Scans the whole app project, not just Components. The first version of this test read only
    /// <c>RazorReaper/Components/**/*.razor</c>, which left the service layer unguarded.
    /// </remarks>
    [Fact]
    public void NothingInTheAppIsDeclaredAsyncVoid()
    {
        var offenders = new List<string>();

        foreach (var path in ProjectFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsComment(lines[i]) && lines[i].Contains("async void", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The declaration scan above only catches the spelling <c>async void</c>. An async
    /// <i>lambda</i> handed to a void-returning delegate is the same thing with no keyword to grep
    /// for: <c>TimerCallback</c>, <c>ElapsedEventHandler</c>, <c>EventHandler</c> and plain
    /// <c>Action</c> all return void, so <c>new Timer(async _ =&gt; ...)</c> compiles into an
    /// <c>async void</c> state machine on a thread-pool thread. That is strictly worse than the
    /// dropped dispatches this file was written for: a dropped dispatch fault waits for the
    /// finalizer and becomes an RR-E1003 row, while this one is rethrown on a pool thread as an
    /// unhandled exception and takes the process down without ever being reported.
    /// </summary>
    /// <remarks>
    /// Deliberately an allowlist, like <see cref="SanctionedPrefixes"/> above: a blocklist of bad
    /// shapes can only ever name the void delegates someone already thought of, and the four timer
    /// callbacks this test was added for (Server's 20 s refresh, Game's 10 s status poll,
    /// AccessGateService, LicenseService) all sailed through the <c>async void</c> grep above for
    /// eighteen commits. An async lambda passes here only where the surrounding call is known to
    /// take a <c>Func&lt;Task&gt;</c>, so a new one in an unrecognised position fails until it is
    /// awaited, guarded, or listed in <see cref="SanctionedContexts"/>.
    /// </remarks>
    [Fact]
    public void NoAsyncLambdaIsHandedToAVoidReturningDelegate()
    {
        var offenders = ProjectFiles()
            .SelectMany(path => ScanForAsyncLambdas(path, WithoutCommentLines(File.ReadAllLines(path))))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The named regression: <c>System.Threading.Timer</c> and <c>System.Timers.Timer</c> both take
    /// a void-returning callback, and every periodic refresh in the app goes through one. Spelled
    /// out separately from the allowlist above so loosening that list can never quietly re-open it.
    /// </summary>
    [Fact]
    public void TimerCallbacksAreNeverAsyncLambdas()
    {
        var offenders = ProjectFiles()
            .SelectMany(path => ScanForAsyncTimerCallbacks(path, WithoutCommentLines(File.ReadAllLines(path))))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The two scans above are only worth having if they fail on the shapes they exist to stop, and
    /// a guard that passes because its pattern never matches anything reads exactly like a guard
    /// that passes because the tree is clean. These pin the difference.
    /// </summary>
    [Theory]
    [InlineData("refreshTimer = new Timer(async _ => { await InvokeAsync(StateHasChanged); }, null, d, p);")]
    [InlineData("statusTimer = new System.Threading.Timer(async _ => await PollAsync(), null, d, p);")]
    [InlineData("timer.Elapsed += async (sender, e) => { await TickAsync(); };")]
    [InlineData("webView.NavigationCompleted += async (_, _) => await InstallGuardAsync();")]
    [InlineData("token.Register(async () => await StopAsync());")]
    [InlineData("private Action? deleteAction;\n        deleteAction = async () => { await SaveAsync(); };")]
    public void TheAsyncLambdaScanRejectsVoidReturningDelegates(string source)
        => Assert.NotEmpty(ScanForAsyncLambdas("Sample.cs", source));

    [Theory]
    [InlineData("_ = Task.Run(async () => await WorkAsync());")]
    [InlineData("this.DispatchRender(() => InvokeAsync(async () => await WorkAsync()), Logger);")]
    [InlineData("RunStartupTask(\"telemetry-start\", async () => await StartAsync());")]
    // Same lambda as the rejected case above; only the declared delegate type differs.
    [InlineData("private Func<Task>? deleteAction;\n        deleteAction = async () => { await SaveAsync(); };")]
    public void TheAsyncLambdaScanAcceptsTaskReturningDelegates(string source)
        => Assert.Empty(ScanForAsyncLambdas("Sample.cs", source));

    [Fact]
    public void TheTimerScanRejectsAnAsyncCallbackAndAcceptsAGuardedOne()
    {
        Assert.NotEmpty(ScanForAsyncTimerCallbacks("Sample.cs", "_timer = new Timer(async _ => await PollAsync(), null, d, p);"));
        Assert.Empty(ScanForAsyncTimerCallbacks("Sample.cs", "_timer = new Timer(state => { _ = PollAsync(); }, null, d, p);"));
    }

    private static List<string> ScanForAsyncLambdas(string path, string text)
    {
        var offenders = new List<string>();
        var taskDelegates = TaskReturningDelegateNames(text);

        foreach (Match lambda in AsyncLambda.Matches(text))
        {
            var before = text[..lambda.Index];

            if (SanctionedContexts.Any(context => context.IsMatch(before))
                || taskDelegates.Any(name => AssignmentTo(name).IsMatch(before)))
            {
                continue;
            }

            offenders.Add($"{Path.GetFileName(path)}:{LineOf(text, lambda.Index)}: {LineAt(text, lambda.Index)}");
        }

        return offenders;
    }

    private static List<string> ScanForAsyncTimerCallbacks(string path, string text)
        => AsyncTimerCallback.Matches(text)
            .Select(match => $"{Path.GetFileName(path)}:{LineOf(text, match.Index)}: {LineAt(text, match.Index)}")
            .ToList();

    [Fact]
    public void TheNotificationContainerNeverTouchesItsCollectionsOffTheDispatcher()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Components", "Shared", "NotificationContainer.razor"));

        // The fire-and-forget handlers ran their bodies on whichever thread raised the toast.
        Assert.DoesNotContain("=> _ = HandleNotificationAddedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=> _ = HandleNotificationRemovedAsync", source, StringComparison.Ordinal);
        Assert.Contains("InvokeAsync(() => HandleNotificationAddedAsync(n))", source, StringComparison.Ordinal);
        Assert.Contains("InvokeAsync(() => HandleNotificationRemovedAsync(id))", source, StringComparison.Ordinal);

        // The row is dropped through the dispatcher, not on whatever thread the delay resumed on.
        Assert.DoesNotContain("\n            notifications.Remove(notification);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TimersDrivingARenderStopThemselvesWhenTheRendererIsGone()
    {
        var components = Path.Combine(RepositoryRoot(), "RazorReaper", "Components");

        // The three heaviest producers: Home's two 1 Hz timers, Crosshair's 20 Hz preview and
        // Autoclicker's 10 Hz mouse tracker, which had no disposal check at all.
        foreach (var page in new[] { "Home.razor", "Crosshair.razor", "Autoclicker.razor" })
        {
            var source = File.ReadAllText(Path.Combine(components, "Pages", page));
            Assert.Contains("this.IsRenderStopped()", source, StringComparison.Ordinal);
            Assert.Contains("this.StopRenderDispatch();", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The gate is only half a fix while its faults go nowhere but the local Serilog file. That
    /// is what v1.4.10's Discord filter did to the socket family — silenced it and let eight
    /// weeks of quiet read as a fix — and the NullReferenceException family the gate now catches
    /// is 74 % of RR-E1003. Deleting either line below would silently reinstate that.
    /// </summary>
    [Fact]
    public void AppCarriesTheGatesFaultsOutThroughTheDampenedReporter()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "App.xaml.cs"));

        Assert.Contains("RenderDispatchReporting.UseSink(HandleRenderDispatchFault);", app, StringComparison.Ordinal);
        Assert.Contains("backgroundFaults.RecordRenderDispatch(", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sink is installed before any window exists; it has to go before the app does, on
    /// every shutdown path. It used to be removed after the <c>telemetry is null</c> early
    /// return, so a shutdown on which telemetry was never resolved kept it installed while the
    /// comment above it claimed the removal was unconditional.
    /// </summary>
    [Fact]
    public void TheShutdownPathRemovesTheGatesSinkBeforeItCanReturnEarly()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "App.xaml.cs"));
        var shutdown = app.IndexOf("private void FlushTelemetryShutdown()", StringComparison.Ordinal);
        Assert.True(shutdown >= 0, "FlushTelemetryShutdown must still exist");

        var body = app[shutdown..];
        var removeSink = body.IndexOf("RenderDispatchReporting.UseSink(null);", StringComparison.Ordinal);
        var firstReturn = body.IndexOf("return;", StringComparison.Ordinal);

        Assert.True(removeSink >= 0, "the shutdown path must remove the gate's sink");
        Assert.True(
            removeSink < firstReturn,
            "the sink is removed after an early return, so a shutdown with telemetry never resolved keeps it installed");
    }

    /// <summary><c>async () =&gt;</c>, <c>async x =&gt;</c>, <c>async (a, b) =&gt;</c>, <c>async delegate</c>.</summary>
    private static readonly Regex AsyncLambda = new(
        @"\basync\s*(?:\(|delegate\b|[A-Za-z_]\w*\s*=>)",
        RegexOptions.Compiled);

    private static readonly Regex AsyncTimerCallback = new(
        @"new\s+(?:System\.(?:Threading|Timers)\.)?Timer\s*\(\s*async\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The calls known to take a <c>Func&lt;Task&gt;</c>, matched against the text immediately
    /// before the lambda. Anything else is assumed to be a void-returning delegate.
    /// </summary>
    private static readonly Regex[] SanctionedContexts =
    [
        new(@"Task\.Run\(\s*\z", RegexOptions.Compiled),          // the returned Task is the owner
        new(@"InvokeAsync\(\s*\z", RegexOptions.Compiled),        // ComponentBase.InvokeAsync(Func<Task>)
        new(@"\.Select\(\s*\z", RegexOptions.Compiled),           // materialised by WhenAll/ToList
        new(@"RunGuardedAsync\(\s*\z", RegexOptions.Compiled),    // AutoClickerRuntime's own guard
        new(@"RunLoopAsync\([^;\r\n]*,\s*\z", RegexOptions.Compiled),
        new(@"RunStartupTask\([^;\r\n]*,\s*\z", RegexOptions.Compiled),
        new(@"Execute\(\s*\z", RegexOptions.Compiled),            // Account.Execute(Func<Task>)
        new("=\\s*\"@\\(\\s*\\z", RegexOptions.Compiled),         // @onclick="@(async ...)" — EventCallback
    ];

    /// <summary>
    /// Names declared as <c>Func&lt;...Task&gt;</c> in this file. Assigning an async lambda to one
    /// is fine — whoever invokes it gets the Task. Assigning one to an <c>Action</c> is the bug.
    /// </summary>
    private static string[] TaskReturningDelegateNames(string text)
        => Regex.Matches(text, @"Func\s*<[^;=\r\n]*\bTask\b[^;=\r\n]*>\s*\??\s+(\w+)\s*[;=,)]")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

    private static Regex AssignmentTo(string name) => new($@"(?<!\w){Regex.Escape(name)}\s*=\s*\z");

    /// <summary>Blanks whole-line comments so the scans above never trip over prose about them.</summary>
    private static string WithoutCommentLines(string[] lines)
        => string.Join('\n', lines.Select(line => IsComment(line) ? string.Empty : line));

    private static int LineOf(string text, int index)
        => text.Take(index).Count(c => c == '\n') + 1;

    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

    private static IEnumerable<int> Occurrences(string line)
    {
        for (var index = line.IndexOf("InvokeAsync(", StringComparison.Ordinal);
             index >= 0;
             index = line.IndexOf("InvokeAsync(", index + 1, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("@*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    private static string[] ComponentFiles()
        => Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "RazorReaper", "Components"),
            "*.razor",
            SearchOption.AllDirectories);

    /// <summary>Every hand-written source file in the shipping project — services included.</summary>
    private static string[] ProjectFiles()
    {
        var project = Path.Combine(RepositoryRoot(), "RazorReaper");

        var files = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(project, "*.razor", SearchOption.AllDirectories))
            .Where(path => !IsGenerated(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // A wrong root would make every scan above pass by finding nothing to scan.
        Assert.NotEmpty(files);
        return files;
    }

    private static bool IsGenerated(string path)
    {
        var relative = path[RepositoryRoot().Length..];
        return relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
