using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services
{
    /// <summary>
    /// Which live ARK INI file an operation targets. Both live under
    /// <c>ShooterGame\Saved\Config\WindowsNoEditor\</c> in the ARK install.
    /// </summary>
    public enum GameIniTarget
    {
        GameUserSettings,
        Game
    }

    /// <summary>
    /// A single (section, key, value) tuple applied by the targeted INI editor.
    /// Also used as the row model for the INI Builder custom editor draft.
    /// </summary>
    public sealed class GameIniEntry
    {
        public string Section { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public GameIniEntry()
        {
        }

        public GameIniEntry(string section, string key, string value)
        {
            Section = section;
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// A curated, code-defined set of INI keys applied together with one click.
    /// </summary>
    public sealed class GameIniPreset
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public GameIniTarget Target { get; init; } = GameIniTarget.GameUserSettings;
        public IReadOnlyList<GameIniEntry> Entries { get; init; } = Array.Empty<GameIniEntry>();
    }

    /// <summary>
    /// Metadata for one timestamped backup file stored under
    /// <c>%LOCALAPPDATA%\RazorReaper\IniBackups\</c>.
    /// </summary>
    public sealed class GameIniBackup
    {
        public string FileName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        /// <summary>The live file this backup was taken from ("GameUserSettings.ini" or "Game.ini").</summary>
        public string TargetFileName { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public long SizeBytes { get; init; }
    }

    /// <summary>
    /// Persisted state of the custom key editor so drafts survive app restarts.
    /// </summary>
    public sealed class GameIniDraft
    {
        /// <summary>"GameUserSettings" or "Game".</summary>
        public string Target { get; set; } = "GameUserSettings";
        public List<GameIniEntry> Rows { get; set; } = new();
    }

    /// <summary>
    /// Outcome of an apply/restore operation.
    /// </summary>
    public sealed class GameIniApplyResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? BackupPath { get; init; }
        public int KeysApplied { get; init; }

        public static GameIniApplyResult Ok(int keysApplied, string? backupPath) =>
            new() { Success = true, KeysApplied = keysApplied, BackupPath = backupPath };

        public static GameIniApplyResult Fail(string error) =>
            new() { Success = false, Error = error };
    }

    /// <summary>
    /// Targeted editor for ARK's live <c>GameUserSettings.ini</c> / <c>Game.ini</c> with
    /// automatic timestamped backups. Unlike a parse-and-rewrite INI library, this service
    /// performs line-level surgical edits so duplicate keys, comments and formatting in the
    /// rest of the file are preserved byte-identically.
    /// </summary>
    public interface IGameIniService
    {
        /// <summary>Gets the built-in curated presets (all target GameUserSettings.ini).</summary>
        IReadOnlyList<GameIniPreset> GetBuiltInPresets();

        /// <summary>Resolves the full path of the target INI, or null when ARK is not found.</summary>
        string? GetIniPath(GameIniTarget target);

        /// <summary>True when the target INI file exists on disk (Game.ini may legitimately be absent).</summary>
        bool IniFileExists(GameIniTarget target);

        /// <summary>True while the ARK game process is running (it rewrites GameUserSettings.ini on exit).</summary>
        bool IsArkRunning();

        /// <summary>Backs up the target file, then applies all preset keys via targeted line edits.</summary>
        Task<GameIniApplyResult> ApplyPresetAsync(GameIniPreset preset);

        /// <summary>Backs up the target file, then applies the given (section, key, value) entries.</summary>
        Task<GameIniApplyResult> ApplyEntriesAsync(GameIniTarget target, IReadOnlyList<GameIniEntry> entries);

        /// <summary>Lists stored backups, newest first.</summary>
        List<GameIniBackup> ListBackups();

        /// <summary>Restores a backup over the live file (a safety backup of the current file is taken first).</summary>
        Task<GameIniApplyResult> RestoreBackupAsync(GameIniBackup backup);

        /// <summary>Deletes a stored backup file. Returns true when the file was removed.</summary>
        bool DeleteBackup(GameIniBackup backup);

        /// <summary>Loads the persisted custom editor draft, or null when none exists / unreadable.</summary>
        Task<GameIniDraft?> LoadDraftAsync();

        /// <summary>Persists the custom editor draft to LocalAppData.</summary>
        Task<bool> SaveDraftAsync(GameIniDraft draft);
    }
}

namespace RazorReaper.Services.Implementations
{
    /// <summary>
    /// Implementation of <see cref="IGameIniService"/>.
    ///
    /// CRITICAL INI semantics: ARK INIs contain duplicate keys, comments and mod-added
    /// sections. This service NEVER round-trips the file through a parser. It reads all
    /// lines, updates the FIRST matching "Key=" line inside the requested [Section]
    /// (or appends the key at the section end / creates the section), and leaves every
    /// other line byte-identical. Encoding is preserved: a BOM is detected and re-emitted,
    /// and BOM-less files are round-tripped through Latin-1 so ANSI bytes written by the
    /// game (e.g. server names) survive untouched.
    /// </summary>
    public class GameIniService : IGameIniService
    {
        private const string GameUserSettingsFileName = "GameUserSettings.ini";
        private const string GameIniFileName = "Game.ini";
        private const string BackupTimestampFormat = "yyyy-MM-dd_HHmmss";
        private const int MaxBackupsPerFile = 20;

        private const string SectionScalability = "ScalabilityGroups";
        private const string SectionShooter = "/Script/ShooterGame.ShooterGameUserSettings";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<GameIniService> _logger;
        private readonly IArkPathProvider _arkPathProvider;
        private readonly IProcessService _processService;
        private readonly ITelemetryService _telemetryService;
        private readonly AppConfiguration _config;
        private readonly SemaphoreSlim _ioGate = new(1, 1);
        private readonly string _backupsDir;
        private readonly string _draftPath;
        private readonly IReadOnlyList<GameIniPreset> _builtInPresets;

        public GameIniService(
            ILogger<GameIniService> logger,
            IArkPathProvider arkPathProvider,
            IProcessService processService,
            ITelemetryService telemetryService,
            IOptions<AppConfiguration> config)
        {
            _logger = logger;
            _arkPathProvider = arkPathProvider;
            _processService = processService;
            _telemetryService = telemetryService;
            _config = config.Value;

            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RazorReaper");
            _backupsDir = Path.Combine(appData, "IniBackups");
            _draftPath = Path.Combine(appData, "ini-builder-draft.json");
            _builtInPresets = BuildPresets();
        }

        /// <inheritdoc/>
        public IReadOnlyList<GameIniPreset> GetBuiltInPresets() => _builtInPresets;

        /// <inheritdoc/>
        public string? GetIniPath(GameIniTarget target)
        {
            try
            {
                var arkPath = _arkPathProvider.FindArkPath();
                if (arkPath == null)
                {
                    _logger.LogWarning("Cannot resolve {Target} path - ARK installation not found", target);
                    return null;
                }

                var fileName = target == GameIniTarget.Game ? GameIniFileName : GameUserSettingsFileName;
                return Path.Combine(arkPath, "ShooterGame", "Saved", "Config", "WindowsNoEditor", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving INI path for {Target}", target);
                return null;
            }
        }

        /// <inheritdoc/>
        public bool IniFileExists(GameIniTarget target)
        {
            try
            {
                var path = GetIniPath(target);
                return path != null && File.Exists(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking INI file existence for {Target}", target);
                return false;
            }
        }

        /// <inheritdoc/>
        public bool IsArkRunning()
        {
            try
            {
                return _processService.IsProcessRunning(_config.Ark.GameProcessName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking whether ARK is running");
                return false;
            }
        }

        /// <inheritdoc/>
        public Task<GameIniApplyResult> ApplyPresetAsync(GameIniPreset preset)
        {
            if (preset == null || preset.Entries.Count == 0)
            {
                return Task.FromResult(GameIniApplyResult.Fail("Preset contains no keys."));
            }

            _logger.LogInformation("Applying INI Builder preset: {Preset}", preset.Name);
            return ApplyEntriesAsync(preset.Target, preset.Entries);
        }

        /// <inheritdoc/>
        public async Task<GameIniApplyResult> ApplyEntriesAsync(GameIniTarget target, IReadOnlyList<GameIniEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return GameIniApplyResult.Fail("No keys to apply.");
            }

            var path = GetIniPath(target);
            if (path == null)
            {
                return GameIniApplyResult.Fail("ARK installation not found.");
            }

            await _ioGate.WaitAsync();
            try
            {
                var result = await Task.Run(() => ApplyEntriesCore(path, entries));

                _ = _telemetryService.TrackEventAsync(
                    "ini_builder_apply",
                    result.Success ? TelemetryEventStatus.Ok : TelemetryEventStatus.Down,
                    result.Success ? "INI Builder keys applied." : result.Error,
                    new Dictionary<string, object?>
                    {
                        ["target"] = target.ToString(),
                        ["key_count"] = result.KeysApplied
                    });

                return result;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        /// <inheritdoc/>
        public List<GameIniBackup> ListBackups()
        {
            try
            {
                if (!Directory.Exists(_backupsDir))
                {
                    return new List<GameIniBackup>();
                }

                var backups = new List<GameIniBackup>();
                foreach (var file in Directory.GetFiles(_backupsDir, "*.ini"))
                {
                    var info = new FileInfo(file);
                    string targetFileName;
                    if (info.Name.StartsWith("GameUserSettings_", StringComparison.OrdinalIgnoreCase))
                    {
                        targetFileName = GameUserSettingsFileName;
                    }
                    else if (info.Name.StartsWith("Game_", StringComparison.OrdinalIgnoreCase))
                    {
                        targetFileName = GameIniFileName;
                    }
                    else
                    {
                        continue;
                    }

                    backups.Add(new GameIniBackup
                    {
                        FileName = info.Name,
                        FullPath = info.FullName,
                        TargetFileName = targetFileName,
                        Timestamp = ParseTimestampFromName(info.Name) ?? info.CreationTime,
                        SizeBytes = info.Length
                    });
                }

                return backups.OrderByDescending(b => b.Timestamp).ThenByDescending(b => b.FileName).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing INI backups");
                return new List<GameIniBackup>();
            }
        }

        /// <inheritdoc/>
        public async Task<GameIniApplyResult> RestoreBackupAsync(GameIniBackup backup)
        {
            if (backup == null || string.IsNullOrWhiteSpace(backup.FullPath))
            {
                return GameIniApplyResult.Fail("Invalid backup.");
            }

            if (!IsInsideBackupsDir(backup.FullPath))
            {
                return GameIniApplyResult.Fail("Backup path is outside the backup folder.");
            }

            var target = string.Equals(backup.TargetFileName, GameIniFileName, StringComparison.OrdinalIgnoreCase)
                ? GameIniTarget.Game
                : GameIniTarget.GameUserSettings;

            var livePath = GetIniPath(target);
            if (livePath == null)
            {
                return GameIniApplyResult.Fail("ARK installation not found.");
            }

            await _ioGate.WaitAsync();
            try
            {
                var result = await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(backup.FullPath))
                        {
                            return GameIniApplyResult.Fail("Backup file no longer exists.");
                        }

                        string? safetyBackup = null;
                        if (File.Exists(livePath))
                        {
                            safetyBackup = CreateBackup(livePath);
                            if (safetyBackup == null)
                            {
                                return GameIniApplyResult.Fail("Could not snapshot the current file — restore cancelled to protect your INI.");
                            }
                        }
                        else
                        {
                            var dir = Path.GetDirectoryName(livePath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                        }

                        File.Copy(backup.FullPath, livePath, overwrite: true);
                        _logger.LogInformation("Restored INI backup {Backup} over {Live}", backup.FileName, livePath);
                        return GameIniApplyResult.Ok(0, safetyBackup);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error restoring INI backup {Backup}", backup.FileName);
                        return GameIniApplyResult.Fail($"Restore failed: {ex.Message}");
                    }
                });

                _ = _telemetryService.TrackEventAsync(
                    "ini_builder_restore",
                    result.Success ? TelemetryEventStatus.Ok : TelemetryEventStatus.Down,
                    result.Success ? "INI backup restored." : result.Error,
                    new Dictionary<string, object?> { ["backup"] = backup.FileName });

                return result;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        /// <inheritdoc/>
        public bool DeleteBackup(GameIniBackup backup)
        {
            try
            {
                if (backup == null || string.IsNullOrWhiteSpace(backup.FullPath) || !IsInsideBackupsDir(backup.FullPath))
                {
                    return false;
                }

                if (!File.Exists(backup.FullPath))
                {
                    return false;
                }

                File.Delete(backup.FullPath);
                _logger.LogInformation("Deleted INI backup {Backup}", backup.FileName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting INI backup {Backup}", backup?.FileName);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<GameIniDraft?> LoadDraftAsync()
        {
            await _ioGate.WaitAsync();
            try
            {
                if (!File.Exists(_draftPath))
                {
                    return null;
                }

                var json = await File.ReadAllTextAsync(_draftPath);
                return JsonSerializer.Deserialize<GameIniDraft>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load INI Builder draft from {Path}", _draftPath);
                return null;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SaveDraftAsync(GameIniDraft draft)
        {
            if (draft == null)
            {
                return false;
            }

            await _ioGate.WaitAsync();
            try
            {
                var directory = Path.GetDirectoryName(_draftPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(draft, JsonOptions);
                await File.WriteAllTextAsync(_draftPath, json);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save INI Builder draft to {Path}", _draftPath);
                return false;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        // ------------------------------------------------------------------
        // Core targeted-edit pipeline
        // ------------------------------------------------------------------

        private GameIniApplyResult ApplyEntriesCore(string path, IReadOnlyList<GameIniEntry> entries)
        {
            try
            {
                var valid = entries
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Section) && !string.IsNullOrWhiteSpace(e.Key))
                    .Select(NormalizeEntry)
                    .ToList();

                if (valid.Count == 0)
                {
                    return GameIniApplyResult.Fail("No valid keys to apply. Each row needs a section and a key.");
                }

                string? backupPath = null;

                if (File.Exists(path))
                {
                    backupPath = CreateBackup(path);
                    if (backupPath == null)
                    {
                        return GameIniApplyResult.Fail("Could not create a backup — apply cancelled to protect your INI.");
                    }

                    var bytes = File.ReadAllBytes(path);
                    var encoding = DetectEncoding(bytes, out var bomLength);
                    var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
                    var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                    var lines = SplitLinesKeepEol(text);

                    foreach (var entry in valid)
                    {
                        ApplyEntryToLines(lines, entry, eol);
                    }

                    WritePreservingEncoding(path, bytes, bomLength, encoding, string.Concat(lines));
                }
                else
                {
                    // Game.ini may legitimately not exist yet — build a fresh minimal file.
                    var builder = new StringBuilder();
                    foreach (var group in valid.GroupBy(e => e.Section, StringComparer.OrdinalIgnoreCase))
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append("\r\n");
                        }

                        builder.Append('[').Append(group.Key).Append("]\r\n");
                        foreach (var entry in group)
                        {
                            builder.Append(entry.Key).Append('=').Append(entry.Value).Append("\r\n");
                        }
                    }

                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                }

                _logger.LogInformation("Applied {Count} INI keys to {Path} (backup: {Backup})", valid.Count, path, backupPath ?? "none - new file");
                return GameIniApplyResult.Ok(valid.Count, backupPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying INI keys to {Path}", path);
                return GameIniApplyResult.Fail($"Apply failed: {ex.Message}");
            }
        }

        private static GameIniEntry NormalizeEntry(GameIniEntry entry)
        {
            // Strip stray brackets/whitespace and any embedded line breaks so a malformed
            // row can never inject extra INI lines.
            static string Sanitize(string value) =>
                (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

            var section = Sanitize(entry.Section).Trim('[', ']').Trim();
            return new GameIniEntry(section, Sanitize(entry.Key), Sanitize(entry.Value));
        }

        /// <summary>
        /// Applies one entry against the line list: updates the FIRST "Key=" match inside
        /// [Section], appends the key at the section end when missing, or creates the
        /// section at end-of-file. Every untouched line keeps its exact original content
        /// including its own line terminator.
        /// </summary>
        private static void ApplyEntryToLines(List<string> lines, GameIniEntry entry, string eol)
        {
            var headerIndex = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (TryGetSectionName(lines[i], out var name) &&
                    name.Equals(entry.Section, StringComparison.OrdinalIgnoreCase))
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                EnsureTrailingEol(lines, eol);
                if (lines.Count > 0 && StripEol(lines[^1]).Trim().Length > 0)
                {
                    lines.Add(eol); // blank separator line before the new section
                }

                lines.Add($"[{entry.Section}]{eol}");
                lines.Add($"{entry.Key}={entry.Value}{eol}");
                return;
            }

            var sectionEnd = lines.Count;
            for (var i = headerIndex + 1; i < lines.Count; i++)
            {
                if (TryGetSectionName(lines[i], out _))
                {
                    sectionEnd = i;
                    break;
                }
            }

            // Update the FIRST matching key line in the section.
            for (var i = headerIndex + 1; i < sectionEnd; i++)
            {
                var body = StripEol(lines[i]);
                var trimmed = body.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#')
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var keyPart = trimmed[..eq].TrimEnd();
                if (!keyPart.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lines[i] = $"{entry.Key}={entry.Value}{GetEol(lines[i])}";
                return;
            }

            // Key not present — append after the last non-blank line of the section so the
            // blank separator before the next section stays where it was.
            var insertAt = headerIndex + 1;
            for (var i = headerIndex + 1; i < sectionEnd; i++)
            {
                if (StripEol(lines[i]).Trim().Length > 0)
                {
                    insertAt = i + 1;
                }
            }

            if (insertAt >= lines.Count)
            {
                EnsureTrailingEol(lines, eol);
                lines.Add($"{entry.Key}={entry.Value}{eol}");
            }
            else
            {
                lines.Insert(insertAt, $"{entry.Key}={entry.Value}{eol}");
            }
        }

        private static bool TryGetSectionName(string line, out string name)
        {
            name = string.Empty;
            var body = StripEol(line).Trim();
            if (body.Length < 3 || body[0] != '[' || body[^1] != ']')
            {
                return false;
            }

            name = body[1..^1].Trim();
            return name.Length > 0;
        }

        private static List<string> SplitLinesKeepEol(string text)
        {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines.Add(text.Substring(start, i - start + 1));
                    start = i + 1;
                }
            }

            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
            }

            return lines;
        }

        private static string StripEol(string line)
        {
            if (line.EndsWith("\r\n", StringComparison.Ordinal))
            {
                return line[..^2];
            }

            return line.EndsWith("\n", StringComparison.Ordinal) ? line[..^1] : line;
        }

        private static string GetEol(string line)
        {
            if (line.EndsWith("\r\n", StringComparison.Ordinal))
            {
                return "\r\n";
            }

            return line.EndsWith("\n", StringComparison.Ordinal) ? "\n" : string.Empty;
        }

        private static void EnsureTrailingEol(List<string> lines, string eol)
        {
            if (lines.Count == 0)
            {
                return;
            }

            if (!lines[^1].EndsWith("\n", StringComparison.Ordinal))
            {
                lines[^1] += eol;
            }
        }

        /// <summary>
        /// BOM-aware encoding detection. BOM-less files round-trip through Latin-1, which
        /// maps every byte 1:1 to a char — so ANSI bytes the game wrote (server names etc.)
        /// are preserved exactly on rewrite. All keys/values we insert are ASCII.
        /// </summary>
        private static Encoding DetectEncoding(byte[] bytes, out int bomLength)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bomLength = 3;
                return new UTF8Encoding(false);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                bomLength = 2;
                return new UnicodeEncoding(false, false);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                bomLength = 2;
                return new UnicodeEncoding(true, false);
            }

            bomLength = 0;
            return Encoding.Latin1;
        }

        private static void WritePreservingEncoding(string path, byte[] originalBytes, int bomLength, Encoding encoding, string newText)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            if (bomLength > 0)
            {
                stream.Write(originalBytes, 0, bomLength);
            }

            var payload = encoding.GetBytes(newText);
            stream.Write(payload, 0, payload.Length);
        }

        // ------------------------------------------------------------------
        // Backups
        // ------------------------------------------------------------------

        private string? CreateBackup(string sourcePath)
        {
            try
            {
                Directory.CreateDirectory(_backupsDir);

                var baseName = Path.GetFileNameWithoutExtension(sourcePath); // "GameUserSettings" or "Game"
                var stamp = DateTime.Now.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
                var destPath = Path.Combine(_backupsDir, $"{baseName}_{stamp}.ini");

                // Multiple applies within the same second get a numeric suffix.
                var suffix = 2;
                while (File.Exists(destPath))
                {
                    destPath = Path.Combine(_backupsDir, $"{baseName}_{stamp}_{suffix}.ini");
                    suffix++;
                }

                File.Copy(sourcePath, destPath, overwrite: false);
                PruneBackups(baseName);
                _logger.LogInformation("Created INI backup: {Backup}", destPath);
                return destPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating INI backup for {Path}", sourcePath);
                return null;
            }
        }

        private void PruneBackups(string baseName)
        {
            try
            {
                // The trailing underscore keeps "Game_*" from matching "GameUserSettings_*".
                var files = Directory.GetFiles(_backupsDir, $"{baseName}_*.ini")
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var stale in files.Skip(MaxBackupsPerFile))
                {
                    try
                    {
                        File.Delete(stale);
                        _logger.LogInformation("Pruned old INI backup: {Backup}", stale);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogWarning(deleteEx, "Failed to prune INI backup {Backup}", stale);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error pruning INI backups for {BaseName}", baseName);
            }
        }

        private static DateTime? ParseTimestampFromName(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var separator = stem.IndexOf('_');
            if (separator < 0 || separator + 1 >= stem.Length)
            {
                return null;
            }

            var remainder = stem[(separator + 1)..];
            if (remainder.Length > BackupTimestampFormat.Length)
            {
                remainder = remainder[..BackupTimestampFormat.Length];
            }

            return DateTime.TryParseExact(
                remainder,
                BackupTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : null;
        }

        private bool IsInsideBackupsDir(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullDir = Path.GetFullPath(_backupsDir);
                return fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Built-in presets — curated, known-safe ARK: Survival Evolved keys.
        // Section and key names were verified against a real live
        // GameUserSettings.ini written by the game.
        // ------------------------------------------------------------------

        private static GameIniEntry E(string section, string key, string value) => new(section, key, value);

        private static IReadOnlyList<GameIniPreset> BuildPresets() => new List<GameIniPreset>
        {
            new GameIniPreset
            {
                Name = "Max FPS",
                Description = "Everything at minimum and sky effects fully disabled. The most frames possible for low-end rigs and sweats.",
                Target = GameIniTarget.GameUserSettings,
                Entries = new List<GameIniEntry>
                {
                    E(SectionScalability, "sg.ResolutionQuality", "100"),
                    E(SectionScalability, "sg.ViewDistanceQuality", "0"),
                    E(SectionScalability, "sg.AntiAliasingQuality", "0"),
                    E(SectionScalability, "sg.ShadowQuality", "0"),
                    E(SectionScalability, "sg.PostProcessQuality", "0"),
                    E(SectionScalability, "sg.TextureQuality", "0"),
                    E(SectionScalability, "sg.EffectsQuality", "0"),
                    E(SectionScalability, "sg.TrueSkyQuality", "0"),
                    E(SectionScalability, "sg.GroundClutterQuality", "0"),
                    E(SectionScalability, "sg.IBLQuality", "0"),
                    E(SectionScalability, "sg.HeightFieldShadowQuality", "0"),
                    E(SectionScalability, "sg.GroundClutterRadius", "0"),
                    E(SectionShooter, "TrueSkyQuality", "0.000000"),
                    E(SectionShooter, "GroundClutterDensity", "0.000000"),
                    E(SectionShooter, "bFilmGrain", "False"),
                    E(SectionShooter, "bMotionBlur", "False"),
                    E(SectionShooter, "bUseSSAO", "False"),
                    E(SectionShooter, "bUseDistanceFieldAmbientOcclusion", "False"),
                    E(SectionShooter, "bDistanceFieldShadowing", "False"),
                    E(SectionShooter, "bDisableBloom", "True"),
                    E(SectionShooter, "bDisableLightShafts", "True"),
                    E(SectionShooter, "bLowQualityVFX", "True"),
                    E(SectionShooter, "bUseLowQualityLevelStreaming", "True"),
                    E(SectionShooter, "bHighQualityLODs", "False"),
                    E(SectionShooter, "bEnableColorGrading", "False"),
                    E(SectionShooter, "LODScalar", "0.000000"),
                    E(SectionShooter, "bUseVSync", "False")
                }
            },
            new GameIniPreset
            {
                Name = "Balanced",
                Description = "Medium detail with clean performance. Sensible everyday settings that still look like ARK.",
                Target = GameIniTarget.GameUserSettings,
                Entries = new List<GameIniEntry>
                {
                    E(SectionScalability, "sg.ResolutionQuality", "100"),
                    E(SectionScalability, "sg.ViewDistanceQuality", "2"),
                    E(SectionScalability, "sg.AntiAliasingQuality", "2"),
                    E(SectionScalability, "sg.ShadowQuality", "1"),
                    E(SectionScalability, "sg.PostProcessQuality", "1"),
                    E(SectionScalability, "sg.TextureQuality", "2"),
                    E(SectionScalability, "sg.EffectsQuality", "1"),
                    E(SectionScalability, "sg.TrueSkyQuality", "1"),
                    E(SectionScalability, "sg.GroundClutterQuality", "1"),
                    E(SectionScalability, "sg.IBLQuality", "1"),
                    E(SectionScalability, "sg.HeightFieldShadowQuality", "0"),
                    E(SectionShooter, "TrueSkyQuality", "0.300000"),
                    E(SectionShooter, "GroundClutterDensity", "0.300000"),
                    E(SectionShooter, "bFilmGrain", "False"),
                    E(SectionShooter, "bMotionBlur", "False"),
                    E(SectionShooter, "bUseSSAO", "False"),
                    E(SectionShooter, "bUseDistanceFieldAmbientOcclusion", "False"),
                    E(SectionShooter, "bDistanceFieldShadowing", "False"),
                    E(SectionShooter, "bDisableBloom", "False"),
                    E(SectionShooter, "bDisableLightShafts", "False"),
                    E(SectionShooter, "bLowQualityVFX", "False"),
                    E(SectionShooter, "bUseLowQualityLevelStreaming", "True"),
                    E(SectionShooter, "bHighQualityLODs", "False"),
                    E(SectionShooter, "bUseVSync", "False")
                }
            },
            new GameIniPreset
            {
                Name = "Quality",
                Description = "Near-maximum visuals: full view distance, textures, shadows and sky. For strong hardware and screenshots.",
                Target = GameIniTarget.GameUserSettings,
                Entries = new List<GameIniEntry>
                {
                    E(SectionScalability, "sg.ResolutionQuality", "100"),
                    E(SectionScalability, "sg.ViewDistanceQuality", "3"),
                    E(SectionScalability, "sg.AntiAliasingQuality", "3"),
                    E(SectionScalability, "sg.ShadowQuality", "3"),
                    E(SectionScalability, "sg.PostProcessQuality", "3"),
                    E(SectionScalability, "sg.TextureQuality", "3"),
                    E(SectionScalability, "sg.EffectsQuality", "3"),
                    E(SectionScalability, "sg.TrueSkyQuality", "3"),
                    E(SectionScalability, "sg.GroundClutterQuality", "3"),
                    E(SectionScalability, "sg.IBLQuality", "1"),
                    E(SectionScalability, "sg.HeightFieldShadowQuality", "3"),
                    E(SectionShooter, "TrueSkyQuality", "1.000000"),
                    E(SectionShooter, "GroundClutterDensity", "1.000000"),
                    E(SectionShooter, "bFilmGrain", "False"),
                    E(SectionShooter, "bMotionBlur", "False"),
                    E(SectionShooter, "bUseSSAO", "True"),
                    E(SectionShooter, "bUseDistanceFieldAmbientOcclusion", "True"),
                    E(SectionShooter, "bDistanceFieldShadowing", "True"),
                    E(SectionShooter, "bDisableBloom", "False"),
                    E(SectionShooter, "bDisableLightShafts", "False"),
                    E(SectionShooter, "bLowQualityVFX", "False"),
                    E(SectionShooter, "bUseLowQualityLevelStreaming", "False"),
                    E(SectionShooter, "bHighQualityLODs", "True"),
                    E(SectionShooter, "bHighQualityAnisotropicFiltering", "True"),
                    E(SectionShooter, "bEnableColorGrading", "True"),
                    E(SectionShooter, "HighQualityMaterials", "True"),
                    E(SectionShooter, "HighQualitySurfaces", "True")
                }
            },
            new GameIniPreset
            {
                Name = "PvP Visibility",
                Description = "Competitive clarity: foliage and ground clutter minimized, long view distance, no bloom or light shafts hiding players.",
                Target = GameIniTarget.GameUserSettings,
                Entries = new List<GameIniEntry>
                {
                    E(SectionScalability, "sg.ResolutionQuality", "100"),
                    E(SectionScalability, "sg.ViewDistanceQuality", "3"),
                    E(SectionScalability, "sg.AntiAliasingQuality", "0"),
                    E(SectionScalability, "sg.ShadowQuality", "0"),
                    E(SectionScalability, "sg.PostProcessQuality", "0"),
                    E(SectionScalability, "sg.TextureQuality", "2"),
                    E(SectionScalability, "sg.EffectsQuality", "1"),
                    E(SectionScalability, "sg.TrueSkyQuality", "0"),
                    E(SectionScalability, "sg.GroundClutterQuality", "0"),
                    E(SectionScalability, "sg.IBLQuality", "0"),
                    E(SectionScalability, "sg.HeightFieldShadowQuality", "0"),
                    E(SectionScalability, "sg.GroundClutterRadius", "0"),
                    E(SectionShooter, "TrueSkyQuality", "0.000000"),
                    E(SectionShooter, "GroundClutterDensity", "0.000000"),
                    E(SectionShooter, "bFilmGrain", "False"),
                    E(SectionShooter, "bMotionBlur", "False"),
                    E(SectionShooter, "bUseSSAO", "False"),
                    E(SectionShooter, "bUseDistanceFieldAmbientOcclusion", "False"),
                    E(SectionShooter, "bDistanceFieldShadowing", "False"),
                    E(SectionShooter, "bDisableBloom", "True"),
                    E(SectionShooter, "bDisableLightShafts", "True"),
                    E(SectionShooter, "bLowQualityVFX", "True"),
                    E(SectionShooter, "bHighQualityLODs", "False"),
                    E(SectionShooter, "bUseVSync", "False")
                }
            }
        };
    }
}
