namespace RazorReaper.Models;

public class CustomLabSettings
{
    // Bump and add a case in CustomLabSettingsService.Migrate when the schema changes.
    public const int CurrentSchemaVersion = 1;

    // Bump when the Read Me copy changes materially — existing acceptances stamped with
    // an older value get invalidated on load and the user is re-prompted.
    public const string RequiredAcceptanceVersion = "1.0";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Accepted { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string? AcceptedAppVersion { get; set; }
    public bool MasterEnabled { get; set; }
    public bool MemoryInjectEnabled { get; set; }
    public bool GuardArkProcess { get; set; } = true;
}
