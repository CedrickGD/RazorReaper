namespace RazorReaper.Services;

public interface IAppRunMode
{
    bool IsLocalPreview { get; }
}
