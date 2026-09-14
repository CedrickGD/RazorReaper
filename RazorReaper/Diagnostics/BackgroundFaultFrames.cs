using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RazorReaper.Diagnostics;

/// <summary>Where a background fault came from, reduced to what is safe to transmit.</summary>
internal readonly record struct BackgroundFaultOrigin(string TopFrame, string TopFrames);

/// <summary>
/// Renders the top stack frames that belong to RazorReaper itself.
///
/// RR-E1003 has never carried a call stack, which is the only reason ~83k unobserved-task rows
/// could not be pinned to a line: exception_type is always AggregateException and
/// base_exception_type is the sole discriminator. One frame closes that gap.
///
/// Everything outside our own namespace is dropped (no third-party or framework internals) and
/// a file is reduced to its bare name, so no build path, user path or machine name can travel
/// with the event even when PDBs ship next to the executable.
/// </summary>
internal static partial class BackgroundFaultFrames
{
    internal const string NoOwnFrame = "(no RazorReaper frame)";
    internal const string Unavailable = "(unavailable)";
    internal const string RedactedUserPath = "%USERPROFILE%";
    internal const string RedactedHost = @"\\%HOST%";

    private const string OwnNamespacePrefix = "RazorReaper.";
    private const int MaxFrameLength = 120;
    private const int MaxFrames = 3;

    public static BackgroundFaultOrigin Describe(Exception? exception)
    {
        if (exception is null)
        {
            return new BackgroundFaultOrigin(NoOwnFrame, NoOwnFrame);
        }

        try
        {
            var frames = new List<string>(MaxFrames);

            // A Task faulted through TaskCompletionSource carries no stack at all; that case
            // falls through to NoOwnFrame rather than inventing a location.
            var stackFrames = new StackTrace(exception, fNeedFileInfo: true).GetFrames();
            foreach (var frame in stackFrames)
            {
                var method = frame?.GetMethod();
                var declaringType = method?.DeclaringType;
                if (declaringType?.FullName is not { } typeName
                    || !typeName.StartsWith(OwnNamespacePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                frames.Add(Describe(typeName, method!.Name, frame!));
                if (frames.Count == MaxFrames)
                {
                    break;
                }
            }

            return frames.Count == 0
                ? new BackgroundFaultOrigin(NoOwnFrame, NoOwnFrame)
                : new BackgroundFaultOrigin(frames[0], string.Join(" > ", frames));
        }
        catch
        {
            // Reflection over a dynamic or collectible frame can throw. The reporter is never
            // allowed to fail louder than the fault it is describing.
            return new BackgroundFaultOrigin(Unavailable, Unavailable);
        }
    }

    /// <summary>
    /// Strips the one thing an exception message can carry that must not be transmitted: a path
    /// through the user's own profile or a network host. The app-relative tail survives — the
    /// message text is what made RR-E1003 diagnosable at all, so it is trimmed, not dropped.
    /// </summary>
    public static string? Redact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            var redacted = UserProfilePath().Replace(message, RedactedUserPath);
            return UncHost().Replace(redacted, RedactedHost);
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(string typeName, string methodName, StackFrame frame)
    {
        var member = DescribeMember(typeName, methodName);
        var location = DescribeLocation(frame);
        var text = location is null ? member : $"{member} ({location})";

        return text.Length <= MaxFrameLength ? text : text[..MaxFrameLength];
    }

    /// <summary>
    /// Folds compiler-generated shapes back onto the code the reader wrote: an async state
    /// machine reports as <c>Home+&lt;UpdateResources&gt;d__42.MoveNext</c> and a lambda as
    /// <c>Home+&lt;&gt;c.&lt;OnInit&gt;b__3_0</c>, neither of which is greppable.
    /// </summary>
    private static string DescribeMember(string typeName, string methodName)
    {
        var segments = typeName.Split('+');
        var owner = string.Join('.', segments.Where(segment => !segment.StartsWith('<')));
        var name = ExtractGeneratedName(methodName)
            ?? segments.Select(ExtractGeneratedName).FirstOrDefault(candidate => candidate is not null)
            ?? methodName;

        return string.IsNullOrEmpty(owner) ? name : $"{owner}.{name}";
    }

    private static string? ExtractGeneratedName(string value)
    {
        var start = value.IndexOf('<');
        if (start < 0)
        {
            return null;
        }

        var end = value.IndexOf('>', start + 1);
        return end > start + 1 ? value[(start + 1)..end] : null;
    }

    /// <summary>File name and line only — never the directory the build ran in.</summary>
    private static string? DescribeLocation(StackFrame frame)
    {
        var file = frame.GetFileName();
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var separator = file.LastIndexOfAny(['\\', '/', ':']);
        var name = separator >= 0 ? file[(separator + 1)..] : file;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var line = frame.GetFileLineNumber();
        return line > 0 ? $"{name}:{line}" : name;
    }

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePath();

    [GeneratedRegex(@"\\\\[^\\/:*?""<>|\r\n]+")]
    private static partial Regex UncHost();
}
