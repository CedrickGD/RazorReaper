namespace RazorReaper.Telemetry;

public interface IInstallIdProvider
{
    Task<InstallIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default);
}
