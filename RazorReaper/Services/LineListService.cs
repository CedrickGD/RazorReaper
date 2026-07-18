using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services;

/// <summary>
/// A single breeding line the user is tracking: species, stat points,
/// mutation counters, and optional sale info for the WTS/WTB generator.
/// </summary>
public class BreedingLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Species { get; set; } = "";
    public string Name { get; set; } = "";

    // Stat points (levels put into each stat, not the displayed value).
    public int Health { get; set; }
    public int Stamina { get; set; }
    public int Oxygen { get; set; }
    public int Food { get; set; }
    public int Weight { get; set; }
    public int Melee { get; set; }

    public int MaternalMutations { get; set; }
    public int PaternalMutations { get; set; }
    public int Generation { get; set; }
    public int BaseLevel { get; set; }

    public string Notes { get; set; } = "";
    public bool ForSale { get; set; }

    /// <summary>Free text so users can price in mutagen, element, dinos, etc.</summary>
    public string Price { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public BreedingLine Clone() => (BreedingLine)MemberwiseClone();
}

/// <summary>Root object persisted to %LOCALAPPDATA%\RazorReaper\line-list.json.</summary>
public class LineListStore
{
    public List<BreedingLine> Lines { get; set; } = new();

    /// <summary>User-written WTB block, kept alongside the lines so the post generator can combine both.</summary>
    public string WtbText { get; set; } = "";
}

public interface ILineListService
{
    /// <summary>Built-in species suggestions for the editor's species field.</summary>
    IReadOnlyList<string> SpeciesSuggestions { get; }

    Task<LineListStore> LoadAsync();
    Task<bool> AddLineAsync(BreedingLine line);
    Task<bool> UpdateLineAsync(BreedingLine line);
    Task<bool> DeleteLineAsync(string id);
    Task<bool> SaveWtbTextAsync(string text);
}

public class LineListService : ILineListService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Species suggestions dataset v1 — commonly line-bred ARK: Survival Evolved creatures.
    // Suggestions only; the species field stays free text so Tek/event variants can be typed.
    private static readonly string[] SpeciesSuggestionsV1 =
    {
        "Allosaurus", "Amargasaurus", "Andrewsarchus", "Ankylosaurus", "Argentavis",
        "Astrodelphis", "Baryonyx", "Basilosaurus", "Beelzebufo", "Bloodstalker",
        "Carbonemys", "Carcharodontosaurus", "Castoroides", "Crystal Wyvern", "Daeodon",
        "Deinonychus", "Desmodus", "Dinopithecus", "Diplodocus", "Direbear",
        "Direwolf", "Doedicurus", "Dunkleosteus", "Equus", "Ferox",
        "Fjordhawk", "Gasbags", "Giganotosaurus", "Kaprosuchus", "Maewing",
        "Magmasaur", "Mammoth", "Managarmr", "Mantis", "Megaloceros",
        "Megalosaurus", "Megatherium", "Mosasaurus", "Otter", "Ovis",
        "Paraceratherium", "Parasaur", "Procoptodon", "Pteranodon", "Quetzal",
        "Raptor", "Ravager", "Rex", "Rhyniognatha", "Sabertooth",
        "Sarco", "Shadowmane", "Sinomacrops", "Snow Owl", "Spino",
        "Stegosaurus", "Tapejara", "Therizinosaur", "Thylacoleo", "Triceratops",
        "Tropeognathus", "Tusoteuthis", "Velonasaur", "Woolly Rhino", "Yutyrannus"
    };

    private readonly string _filePath;
    private readonly ILogger<LineListService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LineListStore? _store;

    public LineListService(ILogger<LineListService> logger)
    {
        _logger = logger;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper");
        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "line-list.json");
    }

    public IReadOnlyList<string> SpeciesSuggestions => SpeciesSuggestionsV1;

    public async Task<LineListStore> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_store is null)
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        var json = await File.ReadAllTextAsync(_filePath);
                        _store = JsonSerializer.Deserialize<LineListStore>(json, JsonOptions);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load line list from {Path}", _filePath);
                }

                _store ??= new LineListStore();
            }

            return _store;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AddLineAsync(BreedingLine line)
    {
        var store = await LoadAsync();

        await _gate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(line.Id))
                line.Id = Guid.NewGuid().ToString("N");

            line.CreatedAt = DateTime.UtcNow;
            line.UpdatedAt = line.CreatedAt;
            store.Lines.Add(line);

            return await PersistAsync(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateLineAsync(BreedingLine line)
    {
        var store = await LoadAsync();

        await _gate.WaitAsync();
        try
        {
            var index = store.Lines.FindIndex(l => l.Id == line.Id);
            if (index < 0)
            {
                _logger.LogWarning("Tried to update a breeding line that no longer exists: {Id}", line.Id);
                return false;
            }

            line.CreatedAt = store.Lines[index].CreatedAt;
            line.UpdatedAt = DateTime.UtcNow;
            store.Lines[index] = line;

            return await PersistAsync(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteLineAsync(string id)
    {
        var store = await LoadAsync();

        await _gate.WaitAsync();
        try
        {
            var removed = store.Lines.RemoveAll(l => l.Id == id);
            if (removed == 0)
                return false;

            return await PersistAsync(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveWtbTextAsync(string text)
    {
        var store = await LoadAsync();

        await _gate.WaitAsync();
        try
        {
            store.WtbText = text ?? "";
            return await PersistAsync(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes the store to disk. Must be called while holding <see cref="_gate"/>.</summary>
    private async Task<bool> PersistAsync(LineListStore store)
    {
        try
        {
            var json = JsonSerializer.Serialize(store, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save line list to {Path}", _filePath);
            return false;
        }
    }
}
