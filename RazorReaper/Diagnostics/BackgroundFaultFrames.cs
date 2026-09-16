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
    internal const string UnknownMember = "(unknown)";
    internal const string RedactedUserPath = "%USERPROFILE%";
    internal const string RedactedHost = @"\\%HOST%";

    private const string OwnNamespacePrefix = "RazorReaper.";
    private const int MaxFrameLength = 120;
    private const int MaxMemberLength = 60;
    private const int MaxFrames = 3;

    /// <summary>
    /// The cheap half of <see cref="Describe"/>: which own method, at which IL offset, the fault
    /// came from — enough to tell two throw sites apart, and nothing that needs a symbol lookup.
    /// </summary>
    /// <remarks>
    /// <see cref="Describe"/> is a PDB-backed stack walk, and the first one in a process loads
    /// the symbols from disk (3.8 ms for the test assembly on the machine this was written on;
    /// warm, about 8 µs). The tracker needs a key on the thread that observed the fault, which
    /// for a render dispatch is the renderer's own dispatcher, so that thread must not pay for
    /// the description. This is what it pays instead: a raw frame copy plus method resolution,
    /// no file information — measured at about a sixth of a warm Describe and none of its cold
    /// cost. The result is a key, never a row: the described frames are what leaves the machine.
    /// </remarks>
    public static string CaptureSite(Exception? exception)
    {
        if (exception is null)
        {
            return NoOwnFrame;
        }

        try
        {
            foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
            {
                var method = frame?.GetMethod();
                if (method?.DeclaringType?.FullName is not { } typeName
                    || !typeName.StartsWith(OwnNamespacePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                return $"{typeName}.{method.Name}@{frame!.GetILOffset()}";
            }

            return NoOwnFrame;
        }
        catch
        {
            return Unavailable;
        }
    }

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
    /// Where a faulted render dispatch came from.
    ///
    /// A dispatch that faults because its renderer is gone has no RazorReaper frame left on the
    /// stack — the throw happens inside the framework's dispatcher — so <see cref="Describe"/>
    /// alone would return <see cref="NoOwnFrame"/> for the whole family and the ~62k
    /// NullReferenceException rows would stay exactly as unattributable as they are today. The
    /// gate knows the two facts the stack lost: the owning component type and the dispatching
    /// member. They stand in as the top own frame when there is no real one, and the real frames
    /// win when there are.
    /// </summary>
    public static BackgroundFaultOrigin DescribeRenderDispatch(Exception? exception, string? owner, string? origin)
    {
        var member = DescribeDispatcher(owner, origin);
        var described = Describe(exception);

        return described.TopFrame is NoOwnFrame or Unavailable
            ? new BackgroundFaultOrigin(member, member)
            : new BackgroundFaultOrigin(described.TopFrame, $"{member} > {described.TopFrames}");
    }

    /// <summary>
    /// <c>Owner.Origin</c> — but only when both are already member-shaped.
    ///
    /// Owner is a type name and origin a <c>[CallerMemberName]</c>, so neither can carry personal
    /// data on the sanctioned path — except that <c>origin</c> is an ordinary optional parameter
    /// any caller may pass by hand, and this string is transmitted. A value that is not a C#
    /// member name is therefore dropped whole rather than scrubbed: salvaging the letters out of
    /// <c>C:\Users\someone\…</c> would leave the user name behind, which is exactly what must
    /// never travel.
    /// </summary>
    internal static string DescribeDispatcher(string? owner, string? origin)
        => $"{Identifier(owner)}.{Identifier(origin)}";

    /// <summary>One member name, or <see cref="UnknownMember"/> when the value is not one.</summary>
    internal static string Identifier(string? value)
    {
        // Generic arity and the compiler's state-machine/lambda brackets are part of a member's
        // written name; separators, spaces, quotes and everything else are not.
        static bool IsMemberChar(char c)
            => char.IsAsciiLetterOrDigit(c) || c is '_' or '<' or '>' or '`';

        return string.IsNullOrEmpty(value) || value.Length > MaxMemberLength || !value.All(IsMemberChar)
            ? UnknownMember
            : value;
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
