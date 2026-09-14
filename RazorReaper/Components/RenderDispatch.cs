using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace RazorReaper.Components;

/// <summary>
/// The one sanctioned way to push a render from code that is not already on the renderer's
/// dispatcher.
/// </summary>
/// <remarks>
/// <see cref="ComponentBase.InvokeAsync(Action)"/> hands the work to the dispatcher and returns a
/// Task; when the work item throws, that Task <i>faults</i> instead of throwing, so an enclosing
/// try/catch never sees it and a bare <c>InvokeAsync(StateHasChanged);</c> — or the equally blind
/// <c>_ = InvokeAsync(...)</c> — leaves it unobserved. The finalizer then republishes it through
/// TaskScheduler.UnobservedTaskException, which is RR-E1003: ~83k production rows since
/// 2026-06-15, one session alone emitting 36k of them at a flat 4/s for 2.5 hours.
/// </remarks>
public static class RenderDispatch
{
    private static readonly ConditionalWeakTable<ComponentBase, RenderDispatchGate> Gates = new();

    /// <summary>
    /// Dispatches a render and observes the resulting Task. Safe to call from any thread, and a
    /// no-op once the component has stopped dispatching. Never throws.
    /// </summary>
    /// <param name="dispatch">
    /// Produces the dispatch, normally <c>() =&gt; InvokeAsync(StateHasChanged)</c>. It is a
    /// factory rather than a Task so a stopped component never starts the work at all.
    /// </param>
    public static void DispatchRender(
        this ComponentBase component,
        Func<Task> dispatch,
        ILogger? logger = null,
        [CallerMemberName] string? origin = null)
        => GateFor(component).Dispatch(dispatch, logger, origin);

    /// <summary>
    /// True once this component stopped dispatching — it was disposed, or its renderer is gone.
    /// Timers driving a render must check this and stop themselves.
    /// </summary>
    public static bool IsRenderStopped(this ComponentBase component) => GateFor(component).IsStopped;

    /// <summary>Stops all further render dispatches for this component. Call it from Dispose.</summary>
    public static void StopRenderDispatch(this ComponentBase component) => GateFor(component).Stop();

    internal static RenderDispatchGate GateFor(ComponentBase component)
        => Gates.GetValue(component, static c => new RenderDispatchGate(c.GetType().Name));
}

/// <summary>
/// Per-component state behind <see cref="RenderDispatch"/>: the stop flag and the consecutive-fault
/// breaker. Separate from the extension so it can be tested without a renderer.
/// </summary>
public sealed class RenderDispatchGate(string owner)
{
    /// <summary>
    /// A healthy dispatch does not fault, so faults arriving back to back mean the renderer behind
    /// this component is gone rather than momentarily busy. Ten in a row is under a second even at
    /// the app's fastest timer (Crosshair's 20 Hz preview) and is nowhere near the 240 faults per
    /// minute the heaviest production session sustained for 25 minutes — so this trips on the
    /// runaway shape and not on a transient. The counter resets on every dispatch that succeeds.
    /// </summary>
    internal const int MaxConsecutiveFaults = 10;

    private readonly string _owner = owner;
    private int _consecutiveFaults;
    private int _stopped;

    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    internal int ConsecutiveFaults => Volatile.Read(ref _consecutiveFaults);

    /// <summary>Stops further dispatches. Returns true only for the call that did the stopping.</summary>
    public bool Stop() => Interlocked.Exchange(ref _stopped, 1) == 0;

    public void Dispatch(Func<Task> dispatch, ILogger? logger = null, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (IsStopped)
        {
            return;
        }

        Task? task;
        try
        {
            task = dispatch();
        }
        catch (Exception ex)
        {
            // InvokeAsync throws synchronously when the dispatcher itself is already gone.
            OnFault(ex, logger, origin);
            return;
        }

        if (task is null)
        {
            return;
        }

        if (task.IsCompletedSuccessfully)
        {
            Volatile.Write(ref _consecutiveFaults, 0);
            return;
        }

        // ExecuteSynchronously so the fault is observed on the completing thread, before the task
        // can ever reach the finalizer queue.
        _ = task.ContinueWith(
            completed => OnCompleted(completed, logger, origin),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnCompleted(Task completed, ILogger? logger, string? origin)
    {
        try
        {
            if (completed.IsCanceled)
            {
                OnFault(new TaskCanceledException(completed), logger, origin);
                return;
            }

            if (!completed.IsFaulted)
            {
                Volatile.Write(ref _consecutiveFaults, 0);
                return;
            }

            // Reading Exception is what marks the task observed; everything below is bookkeeping.
            OnFault(
                completed.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Render dispatch faulted without an exception."),
                logger,
                origin);
        }
        catch
        {
            // Nothing in here may escape: this runs on the completing thread, and a throw would
            // fault the continuation task and feed the very reporting path this exists to end.
        }
    }

    private void OnFault(Exception exception, ILogger? logger, string? origin)
    {
        var faults = Interlocked.Increment(ref _consecutiveFaults);
        var rendererGone = IsRendererGone(exception);
        var stopping = rendererGone || faults >= MaxConsecutiveFaults;

        // Teardown faults are expected noise; a first unexpected one is worth seeing once.
        var level = IsTeardown(exception) || faults > 1 ? LogLevel.Debug : LogLevel.Warning;
        Write(logger, level, exception, _owner, origin, faults);

        if (stopping && Stop())
        {
            Write(
                logger,
                LogLevel.Warning,
                exception,
                _owner,
                origin,
                faults,
                stopped: true);
        }
    }

    /// <summary>
    /// Faults meaning the renderer is gone for good, so dispatching again can only fault again.
    /// </summary>
    internal static bool IsRendererGone(Exception exception) => exception switch
    {
        ObjectDisposedException => true,
        JSDisconnectedException => true,
        InvalidOperationException invalid => IsDeadRendererMessage(invalid.Message),
        _ => false,
    };

    /// <summary>Faults that are ordinary teardown rather than a bug worth warning about.</summary>
    internal static bool IsTeardown(Exception exception)
        => exception is OperationCanceledException || IsRendererGone(exception);

    private static bool IsDeadRendererMessage(string? message)
        => message is not null
            && message.Contains("renderer", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("dispos", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no browser renderer", StringComparison.OrdinalIgnoreCase));

    private static void Write(
        ILogger? logger,
        LogLevel level,
        Exception exception,
        string owner,
        string? origin,
        int faults,
        bool stopped = false)
    {
        const string FaultTemplate = "Render dispatch from {Owner}.{Origin} faulted ({Faults} in a row).";
        const string StoppedTemplate = "{Owner}.{Origin} stopped dispatching renders after {Faults} consecutive faults.";
        var template = stopped ? StoppedTemplate : FaultTemplate;

        try
        {
            if (logger is not null)
            {
                logger.Log(level, exception, template, owner, origin, faults);
                return;
            }

            // Components without an injected logger still belong in the session log.
            if (level >= LogLevel.Warning)
            {
                Serilog.Log.Warning(exception, template, owner, origin, faults);
            }
            else
            {
                Serilog.Log.Debug(exception, template, owner, origin, faults);
            }
        }
        catch
        {
            // Logging must never be the thing that faults teardown.
        }
    }
}
