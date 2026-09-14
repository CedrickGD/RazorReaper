using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Dispatcher = Microsoft.AspNetCore.Components.Dispatcher;
using RazorReaper.Components;

namespace RazorReaper.UnitTests.Components;

public sealed class RenderDispatchComponentTests
{
    private sealed class TestComponent : ComponentBase;

    private static void Expect(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1);
        }

        Assert.True(condition(), because);
    }

    [Fact]
    public void AFreshComponentDispatches()
    {
        var component = new TestComponent();
        var dispatched = 0;

        Assert.False(component.IsRenderStopped());
        component.DispatchRender(() =>
        {
            Interlocked.Increment(ref dispatched);
            return Task.CompletedTask;
        });

        Assert.Equal(1, dispatched);
    }

    [Fact]
    public void StopRenderDispatchIsTheDisposedFlag()
    {
        var component = new TestComponent();
        var dispatched = 0;

        component.StopRenderDispatch();

        Assert.True(component.IsRenderStopped());
        component.DispatchRender(() =>
        {
            Interlocked.Increment(ref dispatched);
            return Task.CompletedTask;
        });

        Assert.Equal(0, dispatched);
    }

    [Fact]
    public void StateIsPerComponentInstance()
    {
        var disposed = new TestComponent();
        var live = new TestComponent();

        disposed.StopRenderDispatch();

        Assert.True(disposed.IsRenderStopped());
        Assert.False(live.IsRenderStopped());
    }

    [Fact]
    public void ADeadRendererStopsTheComponentSoItsTimersCanStop()
    {
        var component = new TestComponent();

        component.DispatchRender(() => Task.FromException(new ObjectDisposedException("Renderer")));

        // This is what the Home / Crosshair / Autoclicker timer handlers read to stop themselves.
        Expect(() => component.IsRenderStopped(), "a gone renderer must stop the component dispatching");
    }

    [Fact]
    public void AFaultingDispatchNeverReachesTheCaller()
    {
        var component = new TestComponent();

        var exception = Record.Exception(() =>
        {
            component.DispatchRender(() => Task.FromException(new NullReferenceException()));
            component.DispatchRender(() => throw new InvalidOperationException("Renderer disposed."));
        });

        Assert.Null(exception);
    }

    /// <summary>
    /// NotificationContainer's shape: many background threads raising toasts while the renderer
    /// walks the same List and HashSet. With the mutations marshalled onto the dispatcher the
    /// renderer's view is single-threaded, so no work item can see another one half-finished.
    /// </summary>
    [Fact]
    public async Task DispatchedMutationsNeverOverlap()
    {
        var dispatcher = Dispatcher.CreateDefault();
        var component = new TestComponent();
        var inside = 0;
        var overlaps = 0;
        var completed = 0;

        const int Threads = 8;
        const int PerThread = 200;
        const int Total = Threads * PerThread;

        await Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < PerThread; i++)
            {
                component.DispatchRender(() => dispatcher.InvokeAsync(() =>
                {
                    if (Interlocked.Increment(ref inside) != 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    Thread.SpinWait(20);
                    Interlocked.Decrement(ref inside);
                    Interlocked.Increment(ref completed);
                }));
            }
        })));

        Expect(() => Volatile.Read(ref completed) == Total, "every dispatched mutation should run");
        Assert.Equal(0, Volatile.Read(ref overlaps));
        Assert.False(component.IsRenderStopped());
    }

    [Fact]
    public async Task ConcurrentToastTrafficLeavesTheListIntactAndRenderable()
    {
        var dispatcher = Dispatcher.CreateDefault();
        var component = new TestComponent();

        // Same two collections NotificationContainer keeps, read the same way its markup reads them.
        var notifications = new List<string?>();
        var leaving = new HashSet<string>(StringComparer.Ordinal);
        var renderFailures = new ConcurrentBag<Exception>();

        const int Threads = 8;
        const int PerThread = 200;
        const int Total = Threads * PerThread;
        var mutations = 0;

        await Task.WhenAll(Enumerable.Range(0, Threads).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < PerThread; i++)
            {
                var id = $"{thread}-{i}";

                component.DispatchRender(() => dispatcher.InvokeAsync(() =>
                {
                    notifications.Add(id);
                    leaving.Add(id);
                    leaving.Remove(id);
                    Interlocked.Increment(ref mutations);
                }));

                // The render pass: enumerate the list and probe the set, exactly like the markup.
                component.DispatchRender(() => dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        foreach (var notification in notifications)
                        {
                            if (notification is null || leaving.Contains(notification))
                            {
                                renderFailures.Add(new InvalidOperationException("Torn toast state."));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        renderFailures.Add(ex);
                    }
                }));
            }
        })));

        Expect(() => Volatile.Read(ref mutations) == Total, "every toast should have been added");

        await dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(Total, notifications.Count);
            Assert.DoesNotContain(null, notifications);
            Assert.Equal(Total, notifications.Distinct().Count());
            Assert.Empty(leaving);
        });

        Assert.Empty(renderFailures);
    }
}
