using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.CustomLab;

public class SkyInjectorSessionState : ISkyInjectorSessionState
{
    public SkyInjectionOptions Options { get; } = new();
}
