namespace RazorReaper.Services.Implementations;

public sealed class LicenseOverlayService : ILicenseOverlayService
{
    public bool IsOpen { get; private set; }

    public event Action? OnStateChanged;

    public void Open() => Set(open: true);

    public void Close() => Set(open: false);

    private void Set(bool open)
    {
        if (IsOpen == open)
        {
            return;
        }

        IsOpen = open;
        OnStateChanged?.Invoke();
    }
}
