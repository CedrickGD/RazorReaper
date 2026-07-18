using Microsoft.Extensions.Logging;
using RazorReaper.Services;

namespace RazorReaper.Services
{
    /// <summary>A saved character-appearance preset file in ARK's SavedArksLocal folder.</summary>
    public record CharPresetInfo(
        string FileName,
        string Name,
        string FullPath,
        long SizeBytes,
        DateTime ModifiedLocal);

    /// <summary>Outcome of a single preset file operation.</summary>
    public record CharPresetOperationResult(bool Success, string Message);

    /// <summary>
    /// One editable float slider inside a preset file, anchored to the absolute byte
    /// offset of its little-endian float value so edits can be patched in place.
    /// </summary>
    public class CharPresetSlider
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public int ByteOffset { get; init; }
        public float Value { get; set; }
    }

    /// <summary>Parsed view of a preset file: color sliders plus the bone-proportion sliders.</summary>
    public class CharPresetDocument
    {
        public string FileName { get; init; } = "";
        public string FilePath { get; init; } = "";
        public long FileLength { get; init; }
        public List<CharPresetSlider> ColorSliders { get; init; } = new();
        public List<CharPresetSlider> BoneSliders { get; init; } = new();
    }

    /// <summary>Outcome of parsing a preset file.</summary>
    public record CharPresetParseResult(bool Success, CharPresetDocument? Document, string? Error);

    /// <summary>
    /// Manages ARK character-appearance preset files (*.arkcharactersetting) in
    /// ShooterGame\Saved\SavedArksLocal: list, rename, duplicate, delete, import,
    /// export, plus parsing and in-place editing of the slider float values.
    /// Before any destructive change (delete or edit) the current file is copied to
    /// %LOCALAPPDATA%\RazorReaper\CharPresetBackups with a timestamp suffix.
    /// File layout follows the UE4 property-bag serialization used by
    /// PrimalCharacterSetting (verified against a real install).
    /// </summary>
    public interface ICharPresetService
    {
        /// <summary>Full path to ShooterGame\Saved\SavedArksLocal, or null when no valid ARK install was found.</summary>
        string? GetPresetsFolderPath();

        /// <summary>True when the presets folder exists on disk.</summary>
        bool PresetsFolderExists();

        /// <summary>Enumerates the *.arkcharactersetting files in the presets folder, sorted by name.</summary>
        List<CharPresetInfo> ListPresets();

        /// <summary>Renames a preset file. The display name is the file name without extension.</summary>
        Task<CharPresetOperationResult> RenameAsync(string fileName, string newName, CancellationToken cancellationToken = default);

        /// <summary>Copies a preset to a new file with an auto-generated unique name.</summary>
        Task<CharPresetOperationResult> DuplicateAsync(string fileName, CancellationToken cancellationToken = default);

        /// <summary>Copies the preset to the backup folder, then deletes it from the game folder.</summary>
        Task<CharPresetOperationResult> DeleteAsync(string fileName, CancellationToken cancellationToken = default);

        /// <summary>Copies an external .arkcharactersetting file into the presets folder after validating its header.</summary>
        Task<CharPresetOperationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

        /// <summary>Copies a preset out of the game folder into a user-chosen destination folder.</summary>
        Task<CharPresetOperationResult> ExportAsync(string fileName, string destinationFolder, CancellationToken cancellationToken = default);

        /// <summary>Reads a preset file and extracts its slider values with their byte offsets.</summary>
        Task<CharPresetParseResult> ParseAsync(string fileName, CancellationToken cancellationToken = default);

        /// <summary>Backs the file up, then patches the document's float values in place.</summary>
        Task<CharPresetOperationResult> SaveSlidersAsync(CharPresetDocument document, CancellationToken cancellationToken = default);

        /// <summary>Root folder that holds pre-change copies of deleted or edited presets.</summary>
        string GetBackupFolderPath();
    }
}

namespace RazorReaper.Services.Implementations
{
    public class CharPresetService : ICharPresetService
    {
        private const string PresetExtension = ".arkcharactersetting";
        private const string FileTypeMarker = "PrimalCharacterSetting";

        // ── Slider-label dataset v1 ──────────────────────────────────────────
        // Order matches the BoneModifierSliderValues array as serialized by the
        // game, which is the same index order the in-game creation UI and the
        // SetTargetPlayerBodyVal console command use. Values run 0..1.
        private static readonly string[] BoneSliderLabels =
        {
            "Head Size",        // 0
            "Neck Size",        // 1
            "Neck Length",      // 2
            "Chest",            // 3
            "Shoulders",        // 4
            "Arm Length",       // 5
            "Upper Arm",        // 6
            "Lower Arm",        // 7
            "Hand",             // 8
            "Leg Length",       // 9
            "Upper Leg",        // 10
            "Lower Leg",        // 11
            "Foot",             // 12
            "Hip",              // 13
            "Torso Width",      // 14
            "Upper Face Size",  // 15
            "Lower Face Size",  // 16
            "Torso Depth",      // 17
            "Head Height",      // 18
            "Head Width",       // 19
            "Head Depth",       // 20
            "Torso Height"      // 21
        };

        // Color-slider properties observed in real files. HairColorSliderValue is
        // part of the format spec but absent from the sampled files — it is shown
        // whenever a file carries it.
        private static readonly Dictionary<string, string> ColorSliderLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BodyColorSliderValue"] = "Skin Tone",
            ["HairColorSliderValue"] = "Hair Color",
            ["EyeColorSliderValue"] = "Eye Color"
        };

        private readonly ILogger<CharPresetService> _logger;
        private readonly IArkPathProvider _arkPathProvider;
        private readonly string _backupRoot;

        public CharPresetService(ILogger<CharPresetService> logger, IArkPathProvider arkPathProvider)
        {
            _logger = logger;
            _arkPathProvider = arkPathProvider;
            _backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RazorReaper",
                "CharPresetBackups");
        }

        public string GetBackupFolderPath() => _backupRoot;

        public string? GetPresetsFolderPath()
        {
            try
            {
                var arkPath = _arkPathProvider.FindArkPath();
                if (arkPath is null || !_arkPathProvider.IsValidArkPath(arkPath))
                {
                    return null;
                }

                return Path.Combine(arkPath, "ShooterGame", "Saved", "SavedArksLocal");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve the ARK character presets folder");
                return null;
            }
        }

        public bool PresetsFolderExists()
        {
            var folder = GetPresetsFolderPath();
            return folder is not null && Directory.Exists(folder);
        }

        public List<CharPresetInfo> ListPresets()
        {
            var presets = new List<CharPresetInfo>();

            try
            {
                var folder = GetPresetsFolderPath();
                if (folder is null || !Directory.Exists(folder))
                {
                    return presets;
                }

                foreach (var filePath in Directory.EnumerateFiles(folder, "*" + PresetExtension))
                {
                    var fileName = Path.GetFileName(filePath);

                    long size = 0;
                    var modified = DateTime.Now;
                    try
                    {
                        var info = new FileInfo(filePath);
                        size = info.Length;
                        modified = info.LastWriteTime;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read metadata of {FilePath}", filePath);
                    }

                    presets.Add(new CharPresetInfo(
                        fileName,
                        Path.GetFileNameWithoutExtension(filePath),
                        filePath,
                        size,
                        modified));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list ARK character presets");
            }

            return presets
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Task<CharPresetOperationResult> RenameAsync(string fileName, string newName, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (folder, sourcePath, error) = ResolveExistingPreset(fileName);
                    if (sourcePath is null)
                    {
                        return new CharPresetOperationResult(false, error!);
                    }

                    var trimmed = (newName ?? "").Trim();
                    if (!IsSafeFileName(trimmed))
                    {
                        return new CharPresetOperationResult(false, "The new name is empty or contains characters Windows does not allow in file names.");
                    }

                    var targetPath = Path.Combine(folder!, trimmed + PresetExtension);
                    if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        return new CharPresetOperationResult(true, "The preset already has that name.");
                    }

                    if (File.Exists(targetPath))
                    {
                        return new CharPresetOperationResult(false, $"A preset named '{trimmed}' already exists.");
                    }

                    File.Move(sourcePath, targetPath);
                    _logger.LogInformation("Renamed character preset {Old} to {New}", fileName, trimmed + PresetExtension);
                    return new CharPresetOperationResult(true, $"Renamed to '{trimmed}'.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to rename character preset {FileName}", fileName);
                    return new CharPresetOperationResult(false, $"Renaming failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<CharPresetOperationResult> DuplicateAsync(string fileName, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (folder, sourcePath, error) = ResolveExistingPreset(fileName);
                    if (sourcePath is null)
                    {
                        return new CharPresetOperationResult(false, error!);
                    }

                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var targetPath = UniquePath(folder!, baseName + " Copy");

                    File.Copy(sourcePath, targetPath, overwrite: false);
                    var newName = Path.GetFileNameWithoutExtension(targetPath);
                    _logger.LogInformation("Duplicated character preset {FileName} as {NewName}", fileName, newName);
                    return new CharPresetOperationResult(true, $"Duplicated as '{newName}'.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to duplicate character preset {FileName}", fileName);
                    return new CharPresetOperationResult(false, $"Duplicating failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<CharPresetOperationResult> DeleteAsync(string fileName, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (_, sourcePath, error) = ResolveExistingPreset(fileName);
                    if (sourcePath is null)
                    {
                        return new CharPresetOperationResult(false, error!);
                    }

                    var backupPath = BackupCopy(sourcePath);
                    File.Delete(sourcePath);
                    _logger.LogInformation("Deleted character preset {FileName}, backup at {BackupPath}", fileName, backupPath);
                    return new CharPresetOperationResult(true, $"Deleted '{Path.GetFileNameWithoutExtension(fileName)}' — a backup copy was kept.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete character preset {FileName}", fileName);
                    return new CharPresetOperationResult(false, $"Deleting failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<CharPresetOperationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var folder = GetPresetsFolderPath();
                    if (folder is null)
                    {
                        return new CharPresetOperationResult(false, "ARK installation not found — is the game installed through Steam?");
                    }

                    if (!Directory.Exists(folder))
                    {
                        return new CharPresetOperationResult(false, $"The presets folder is missing: {folder}");
                    }

                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        return new CharPresetOperationResult(false, "The selected file no longer exists.");
                    }

                    if (!string.Equals(Path.GetExtension(sourcePath), PresetExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CharPresetOperationResult(false, $"Character presets use the {PresetExtension} extension.");
                    }

                    if (!LooksLikePresetFile(sourcePath))
                    {
                        return new CharPresetOperationResult(false, "That file is not a valid ARK character preset.");
                    }

                    var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                    if (!IsSafeFileName(baseName))
                    {
                        baseName = "Imported Preset";
                    }

                    var targetPath = Path.Combine(folder, baseName + PresetExtension);
                    if (File.Exists(targetPath))
                    {
                        targetPath = UniquePath(folder, baseName);
                    }

                    File.Copy(sourcePath, targetPath, overwrite: false);
                    var newName = Path.GetFileNameWithoutExtension(targetPath);
                    _logger.LogInformation("Imported character preset {Source} as {NewName}", sourcePath, newName);
                    return new CharPresetOperationResult(true, $"Imported '{newName}' — it shows up on the character creation screen.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import character preset from {SourcePath}", sourcePath);
                    return new CharPresetOperationResult(false, $"Importing failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<CharPresetOperationResult> ExportAsync(string fileName, string destinationFolder, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (_, sourcePath, error) = ResolveExistingPreset(fileName);
                    if (sourcePath is null)
                    {
                        return new CharPresetOperationResult(false, error!);
                    }

                    if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
                    {
                        return new CharPresetOperationResult(false, "The chosen destination folder does not exist.");
                    }

                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var targetPath = Path.Combine(destinationFolder, fileName);
                    if (File.Exists(targetPath))
                    {
                        targetPath = UniquePath(destinationFolder, baseName);
                    }

                    File.Copy(sourcePath, targetPath, overwrite: false);
                    _logger.LogInformation("Exported character preset {FileName} to {TargetPath}", fileName, targetPath);
                    return new CharPresetOperationResult(true, $"Exported to {targetPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to export character preset {FileName}", fileName);
                    return new CharPresetOperationResult(false, $"Exporting failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<CharPresetParseResult> ParseAsync(string fileName, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (_, sourcePath, error) = ResolveExistingPreset(fileName);
                    if (sourcePath is null)
                    {
                        return new CharPresetParseResult(false, null, error);
                    }

                    var bytes = File.ReadAllBytes(sourcePath);
                    var document = ParseBytes(bytes, fileName, sourcePath);

                    if (document.ColorSliders.Count == 0 && document.BoneSliders.Count == 0)
                    {
                        return new CharPresetParseResult(false, null, "No editable sliders were found in this preset.");
                    }

                    return new CharPresetParseResult(true, document, null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse character preset {FileName}", fileName);
                    return new CharPresetParseResult(false, null, $"This file is not in the expected preset format: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<CharPresetOperationResult> SaveSlidersAsync(CharPresetDocument document, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (folder, sourcePath, error) = ResolveExistingPreset(document.FileName);
                    if (sourcePath is null)
                    {
                        return new CharPresetOperationResult(false, error!);
                    }

                    var bytes = File.ReadAllBytes(sourcePath);
                    if (bytes.Length != document.FileLength)
                    {
                        return new CharPresetOperationResult(false, "The preset file changed on disk since it was opened — close and reopen the editor.");
                    }

                    var sliders = document.ColorSliders.Concat(document.BoneSliders).ToList();
                    foreach (var slider in sliders)
                    {
                        if (slider.ByteOffset < 0 || slider.ByteOffset + 4 > bytes.Length)
                        {
                            return new CharPresetOperationResult(false, "Internal offset mismatch — reopen the editor and try again.");
                        }
                    }

                    var backupPath = BackupCopy(sourcePath);

                    foreach (var slider in sliders)
                    {
                        var clamped = Math.Clamp(slider.Value, 0f, 1f);
                        BitConverter.GetBytes(clamped).CopyTo(bytes, slider.ByteOffset);
                    }

                    File.WriteAllBytes(sourcePath, bytes);
                    _logger.LogInformation("Saved slider edits to character preset {FileName}, backup at {BackupPath}",
                        document.FileName, backupPath);
                    return new CharPresetOperationResult(true, $"Saved changes to '{Path.GetFileNameWithoutExtension(document.FileName)}'.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save character preset {FileName}", document.FileName);
                    return new CharPresetOperationResult(false, $"Saving failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        // ── Binary format ────────────────────────────────────────────────────
        // Verified against real files (all 530 bytes):
        //   int32 version(1) · 16 null bytes · fstring "PrimalCharacterSetting"
        //   int32 (1) · int32 nameCount · nameCount fstrings · 12 null bytes
        //   int32 bodyOffset · 4 null bytes · property bag · 4 null bytes
        // Property bag entry: fstring name · fstring type · int32 size · int32 index · data.
        //   FloatProperty  → 4-byte float.
        //   ArrayProperty  → fstring innerType, then `size` bytes (int32 count + elements);
        //                    size does NOT include the inner type string.
        //   StructProperty → fstring structType, then a nested bag ending at "None".
        // A bag ends at the fstring "None". fstring = int32 length (incl. NUL) + ASCII + NUL.
        private static CharPresetDocument ParseBytes(byte[] bytes, string fileName, string filePath)
        {
            var pos = 0;

            int ReadInt()
            {
                if (pos + 4 > bytes.Length) throw new InvalidDataException("Unexpected end of file.");
                var value = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                return value;
            }

            float ReadFloatAt(int offset)
            {
                if (offset + 4 > bytes.Length) throw new InvalidDataException("Unexpected end of file.");
                return BitConverter.ToSingle(bytes, offset);
            }

            string ReadStr()
            {
                var length = ReadInt();
                if (length <= 0 || pos + length > bytes.Length) throw new InvalidDataException("Corrupt string entry.");
                var text = System.Text.Encoding.ASCII.GetString(bytes, pos, length - 1);
                pos += length;
                return text;
            }

            ReadInt();                       // version, always 1
            pos += 16;                       // null padding
            var fileType = ReadStr();
            if (!string.Equals(fileType, FileTypeMarker, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Missing PrimalCharacterSetting marker.");
            }

            ReadInt();                       // constant 1
            var nameCount = ReadInt();
            if (nameCount is < 0 or > 32) throw new InvalidDataException("Implausible header name count.");
            for (var i = 0; i < nameCount; i++)
            {
                ReadStr();                   // object names (SpawnUI_C_x etc.), not needed
            }

            pos += 12;                       // null padding
            var bodyOffset = ReadInt();      // header size == absolute body offset
            pos += 4;                        // null padding
            if (bodyOffset > 0 && bodyOffset <= bytes.Length)
            {
                pos = bodyOffset;            // trust the recorded offset over our walk
            }

            var document = new CharPresetDocument
            {
                FileName = fileName,
                FilePath = filePath,
                FileLength = bytes.Length
            };

            void ReadBag()
            {
                while (true)
                {
                    var name = ReadStr();
                    if (name == "None")
                    {
                        return;
                    }

                    var type = ReadStr();
                    var size = ReadInt();
                    ReadInt();               // property index

                    if (size < 0 || pos + size > bytes.Length)
                    {
                        throw new InvalidDataException($"Implausible size for property '{name}'.");
                    }

                    switch (type)
                    {
                        case "FloatProperty":
                            if (ColorSliderLabels.TryGetValue(name, out var colorLabel))
                            {
                                document.ColorSliders.Add(new CharPresetSlider
                                {
                                    Key = name,
                                    Label = colorLabel,
                                    ByteOffset = pos,
                                    Value = ReadFloatAt(pos)
                                });
                            }
                            pos += size;
                            break;

                        case "ArrayProperty":
                            var innerType = ReadStr();
                            var dataStart = pos;
                            if (name == "BoneModifierSliderValues" && innerType == "FloatProperty")
                            {
                                var count = ReadInt();
                                if (count < 0 || count > 256) throw new InvalidDataException("Implausible bone slider count.");
                                for (var i = 0; i < count; i++)
                                {
                                    document.BoneSliders.Add(new CharPresetSlider
                                    {
                                        Key = $"Bone{i}",
                                        Label = i < BoneSliderLabels.Length ? BoneSliderLabels[i] : $"Slider {i + 1}",
                                        ByteOffset = pos,
                                        Value = ReadFloatAt(pos)
                                    });
                                    pos += 4;
                                }
                            }
                            pos = dataStart + size;   // size covers count + elements only
                            break;

                        case "StructProperty":
                            ReadStr();       // struct type name (CharacterPreset)
                            ReadBag();       // nested bag ends at its own "None"
                            break;

                        default:
                            pos += size;     // unknown property type — skip its payload
                            break;
                    }
                }
            }

            ReadBag();
            return document;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private (string? Folder, string? FullPath, string? Error) ResolveExistingPreset(string fileName)
        {
            var folder = GetPresetsFolderPath();
            if (folder is null)
            {
                return (null, null, "ARK installation not found — is the game installed through Steam?");
            }

            if (!Directory.Exists(folder))
            {
                return (null, null, $"The presets folder is missing: {folder}");
            }

            if (!IsSafePresetFileName(fileName))
            {
                return (null, null, "Invalid preset file name.");
            }

            var fullPath = Path.Combine(folder, fileName);
            if (!File.Exists(fullPath))
            {
                return (null, null, $"{fileName} no longer exists — rescan the list.");
            }

            return (folder, fullPath, null);
        }

        /// <summary>Copies the file into the backup root with a timestamp suffix; returns the backup path.</summary>
        private string BackupCopy(string sourcePath)
        {
            Directory.CreateDirectory(_backupRoot);
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(_backupRoot, $"{baseName}_{stamp}{PresetExtension}");
            var counter = 2;
            while (File.Exists(backupPath))
            {
                backupPath = Path.Combine(_backupRoot, $"{baseName}_{stamp}-{counter}{PresetExtension}");
                counter++;
            }

            File.Copy(sourcePath, backupPath, overwrite: false);
            return backupPath;
        }

        /// <summary>Builds "{name}{ext}", "{name} 2{ext}", ... until the path is free.</summary>
        private static string UniquePath(string folder, string baseName)
        {
            var candidate = Path.Combine(folder, baseName + PresetExtension);
            var counter = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, $"{baseName} {counter}{PresetExtension}");
                counter++;
            }

            return candidate;
        }

        /// <summary>Cheap validity sniff: the header marker string must appear near the start of the file.</summary>
        private static bool LooksLikePresetFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var head = new byte[128];
                var read = stream.Read(head, 0, head.Length);
                var text = System.Text.Encoding.ASCII.GetString(head, 0, read);
                return text.Contains(FileTypeMarker, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeFileName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !name.Contains("..", StringComparison.Ordinal);

        private static bool IsSafePresetFileName(string fileName) =>
            IsSafeFileName(fileName)
            && string.Equals(Path.GetExtension(fileName), PresetExtension, StringComparison.OrdinalIgnoreCase);
    }
}
