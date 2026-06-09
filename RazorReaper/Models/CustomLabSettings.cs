namespace RazorReaper.Models;

public class CustomLabSettings
{
    // Bump and add a case in CustomLabSettingsService.Migrate when the schema changes.
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Warn (and guard file writes) when ShooterGame.exe is running. Always on — read by the
    // Sky Changer's restore guard; there is no UI toggle.
    public bool GuardArkProcess { get; set; } = true;

    // Sky Injector activity timestamps — used by the status badge to distinguish
    // "Sky injected" (last action was inject) from "Backups available (restored)".
    public DateTimeOffset? LastSkyInjectAt { get; set; }
    public DateTimeOffset? LastSkyRestoreAt { get; set; }
}
