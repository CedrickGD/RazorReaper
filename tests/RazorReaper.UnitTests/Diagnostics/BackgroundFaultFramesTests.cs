using RazorReaper.Diagnostics;

namespace RazorReaper.UnitTests.Diagnostics;

/// <summary>
/// The top own-frame is the field whose absence left ~83k RR-E1003 rows unattributable:
/// exception_type is always AggregateException, so base_exception_type was the only
/// discriminator and no line could ever be named. These tests pin both halves of the deal —
/// the frame is useful, and it carries nothing personal.
/// </summary>
public sealed class BackgroundFaultFramesTests
{
    [Fact]
    public void DescribesTheThrowingMethod()
    {
        var exception = Capture(() => throw new InvalidOperationException("boom"));

        var origin = BackgroundFaultFrames.Describe(exception);

        Assert.Contains(nameof(BackgroundFaultFramesTests), origin.TopFrame, StringComparison.Ordinal);
        Assert.Contains(nameof(Throw), origin.TopFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldsAsyncStateMachineBackOntoTheWrittenMethod()
    {
        // Without the fold this reads as `+<ThrowAsync>d__7.MoveNext`, which is not greppable.
        var exception = CaptureAsync();

        var origin = BackgroundFaultFrames.Describe(exception);

        Assert.Contains(nameof(ThrowAsync), origin.TopFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveNext", origin.TopFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("d__", origin.TopFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldsLambdaBackOntoItsEnclosingMethod()
    {
        var exception = CaptureFromLambda();

        var origin = BackgroundFaultFrames.Describe(exception);

        Assert.Contains(nameof(CaptureFromLambda), origin.TopFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("b__", origin.TopFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("<>c", origin.TopFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsFileNameAndLineWithoutAnyDirectory()
    {
        var exception = Capture(() => throw new InvalidOperationException("boom"));

        var origin = BackgroundFaultFrames.Describe(exception);

        // The redaction that matters: PDBs embed the full build path, and that path runs
        // through a user profile on every developer machine.
        Assert.DoesNotContain('\\', origin.TopFrame);
        Assert.DoesNotContain('/', origin.TopFrame);
        Assert.DoesNotContain("Users", origin.TopFrame, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsNoOwnFrameForATaskFaultedWithoutAStack()
    {
        // A TaskCompletionSource fault carries no stack at all — say so rather than invent one.
        var source = new TaskCompletionSource();
        source.SetException(new InvalidOperationException("no stack"));

        var origin = BackgroundFaultFrames.Describe(source.Task.Exception!.GetBaseException());

        Assert.Equal(BackgroundFaultFrames.NoOwnFrame, origin.TopFrame);
    }

    [Fact]
    public void ReportsNoOwnFrameForNull()
    {
        var origin = BackgroundFaultFrames.Describe(null);

        Assert.Equal(BackgroundFaultFrames.NoOwnFrame, origin.TopFrame);
        Assert.Equal(BackgroundFaultFrames.NoOwnFrame, origin.TopFrames);
    }

    [Fact]
    public void KeepsAtMostThreeFramesAndLeadsWithTheTopOne()
    {
        var exception = Capture(() => Nested(3));

        var origin = BackgroundFaultFrames.Describe(exception);

        Assert.StartsWith(origin.TopFrame, origin.TopFrames, StringComparison.Ordinal);
        Assert.True(origin.TopFrames.Split(" > ").Length <= 3);
    }

    [Theory]
    [InlineData(
        @"Access to the path 'C:\Users\alice\AppData\Local\RazorReaper\app.log' is denied.",
        @"Access to the path '%USERPROFILE%\AppData\Local\RazorReaper\app.log' is denied.")]
    [InlineData(
        @"Could not find file 'D:\Users\Bob Smith\Documents\preset.ini'.",
        @"Could not find file '%USERPROFILE%\Documents\preset.ini'.")]
    public void RedactsUserProfilePaths(string message, string expected)
    {
        Assert.Equal(expected, BackgroundFaultFrames.Redact(message));
    }

    [Fact]
    public void RedactsUncHostNames()
    {
        var redacted = BackgroundFaultFrames.Redact(@"The network path \\NAS-CEDRIC\backup\ark was not found.");

        Assert.Equal(@"The network path \\%HOST%\backup\ark was not found.", redacted);
        Assert.DoesNotContain("NAS-CEDRIC", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsTheAppRelativeTailThatMadeTheInvestigationPossible()
    {
        // The IOException family was diagnosed from exactly this substring; redaction must not
        // cost us the part that names the file.
        var redacted = BackgroundFaultFrames.Redact(
            @"The process cannot access C:\Users\cedri\AppData\Local\com.companyname.razorreaper\Settings\preferences.dat");

        Assert.Contains("com.companyname.razorreaper", redacted, StringComparison.Ordinal);
        Assert.Contains("preferences.dat", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("cedri", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RedactsEmptyMessagesToNull(string? message)
    {
        Assert.Null(BackgroundFaultFrames.Redact(message));
    }

    [Fact]
    public void LeavesAMessageWithoutPathsAlone()
    {
        const string message = "Object reference not set to an instance of an object.";

        Assert.Equal(message, BackgroundFaultFrames.Redact(message));
    }

    private static Exception Capture(Action work)
    {
        try
        {
            Throw(work);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The test helper was expected to throw.");
    }

    private static void Throw(Action work) => work();

    private static void Nested(int depth)
    {
        if (depth <= 0)
        {
            throw new InvalidOperationException("deep");
        }

        Nested(depth - 1);
    }

    private static Exception CaptureAsync()
    {
        try
        {
            ThrowAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The test helper was expected to throw.");
    }

    private static async Task ThrowAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("async boom");
    }

    private static Exception CaptureFromLambda()
    {
        Action work = () => throw new InvalidOperationException("lambda boom");

        try
        {
            work();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The test helper was expected to throw.");
    }
}
