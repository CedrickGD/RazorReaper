using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Holds the Sky Injector's pending selection (mode, image path, hex, flip, tile) for the
/// lifetime of the app process. Reset on app restart by design — the path is not persisted
/// to disk so a fresh launch starts empty, but tab switches and navigation within one session
/// don't lose the user's picked image.
/// </summary>
public interface ISkyInjectorSessionState
{
    SkyInjectionOptions Options { get; }
}
