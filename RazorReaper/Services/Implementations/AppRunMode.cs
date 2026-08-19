namespace RazorReaper.Services.Implementations;

public sealed class AppRunMode : IAppRunMode
{
    private const string LocalPreviewArgument = "--local-preview";

    public AppRunMode(IEnumerable<string> arguments)
    {
        IsLocalPreview = IsLocalPreviewRequested(arguments);
    }

    internal static bool IsLocalPreviewRequested(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
#if DEBUG
        return arguments.Any(static argument =>
            string.Equals(argument, LocalPreviewArgument, StringComparison.OrdinalIgnoreCase));
#else
        return false;
#endif
    }

    public bool IsLocalPreview { get; }
}
