using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

public class HwidService : IHwidService
{
    private readonly IClientIdentityService _clientIdentityService;

    public HwidService(IClientIdentityService clientIdentityService)
    {
        _clientIdentityService = clientIdentityService
            ?? throw new ArgumentNullException(nameof(clientIdentityService));
    }

    public string GetHardwareId() => _clientIdentityService.GetIdentity().HardwareId;
}
