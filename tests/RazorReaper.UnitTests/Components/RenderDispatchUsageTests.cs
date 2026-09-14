using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// RR-E1003 came from ~60 dropped InvokeAsync dispatches, and the count sat flat across every
/// released version because nothing stopped the 61st from being written. This is that stop.
/// </summary>
public sealed class RenderDispatchUsageTests
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

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
