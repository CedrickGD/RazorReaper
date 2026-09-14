namespace RazorReaper.Components;

/// <summary>
/// One render-dispatch fault, reduced to what a reporter needs. <see cref="Owner"/> is the
/// component's type name and <see cref="Origin"/> the dispatching member — the two fields a
/// faulted dispatch's stack cannot supply once the renderer behind it is gone.
/// </summary>
public readonly record struct RenderDispatchFault(
    Exception Exception,
    string Owner,
    string Origin,
    int ConsecutiveFaults,
    bool Stopped);

/// <summary>
/// The one seam between the render gate and telemetry.
///
/// <see cref="RenderDispatchGate"/> observes every faulted render dispatch, which is what stops
/// RR-E1003 — but observing a fault and writing it only to the local Serilog file is how v1.4.10
/// made the socket family read as fixed for eight weeks. The dominant fault family has to keep
/// arriving somewhere the owner can see it, so the gate hands its unexpected faults here and App
/// wires this to the same dampened reporting path the unobserved-task handler uses.
///
/// A delegate rather than a service because the component layer must not take a dependency on
/// ITelemetryService: the gate is a static reached from Dispose and from timer threads, with no
/// scope to resolve anything from.
/// </summary>
public static class RenderDispatchReporting
{
    private static Action<RenderDispatchFault>? sink;

    /// <summary>
    /// Guards against a sink that faults its own way back in here. The flag is per thread because
    /// the only recursion that matters is synchronous re-entry on the reporting thread, and the
    /// sink is called from the renderer's dispatcher, timer threads and the finalizer thread at
    /// the same time — a shared flag would drop unrelated concurrent faults instead.
    /// </summary>
    [ThreadStatic]
    private static bool reporting;

    /// <summary>
    /// Installs the reporter, once, from App startup. Null until then and in unit tests, which is
    /// a silent no-op: a component faulting before telemetry exists must not be a second fault.
    /// </summary>
    public static void UseSink(Action<RenderDispatchFault>? faultSink) => Volatile.Write(ref sink, faultSink);

    /// <summary>True when a reporter is installed. For tests and for the gate's fast path.</summary>
    internal static bool HasSink => Volatile.Read(ref sink) is not null;

    /// <summary>
    /// Hands a fault to the reporter. Never throws, never recurses, never blocks on anything the
    /// sink does with it.
    /// </summary>
    internal static void Report(RenderDispatchFault fault)
    {
        var current = Volatile.Read(ref sink);
        if (current is null || reporting)
        {
            return;
        }

        reporting = true;
        try
        {
            current(fault);
        }
        catch
        {
            // A reporter that throws would fault the very dispatch continuation that called it,
            // and that faulted Task is RR-E1003. The report is worth less than the guarantee.
        }
        finally
        {
            reporting = false;
        }
    }
}
