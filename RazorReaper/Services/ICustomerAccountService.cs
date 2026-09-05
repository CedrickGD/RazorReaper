namespace RazorReaper.Services;

public sealed record CustomerAccountProfile(string Id, string DisplayName, string DiscordId, string DiscordUsername, string? Avatar);
public sealed record CustomerAccountDevice(string InstallId, string? AppVersion, string LinkedAt, int SignedIn);
public sealed record CustomerAccountLogin(string RequestId, string UserCode, string VerificationUrl, int ExpiresIn);

public interface ICustomerAccountService
{
    CustomerAccountProfile? Profile { get; }
    IReadOnlyList<CustomerAccountDevice> Devices { get; }
    event Action? Changed;
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<CustomerAccountLogin> BeginSignInAsync(CancellationToken cancellationToken = default);
    Task<CustomerAccountProfile?> PollSignInAsync(string requestId, CancellationToken cancellationToken = default);
    Task ConfirmSignInAsync(string requestId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(string displayName, string? avatar, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
