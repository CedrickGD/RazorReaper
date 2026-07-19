namespace RazorReaper.Models;

public class CustomSpot
{
    public string Name { get; set; } = "";
    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    public string Type { get; set; } = "";
    public string Note { get; set; } = "";
}

public class CustomMap
{
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public List<CustomSpot> Spots { get; set; } = new();
}

public class CustomServer
{
    public string Name { get; set; } = "";
    public string LogoDataUrl { get; set; } = "";
    public List<CustomMap> Maps { get; set; } = new();
}

public class CustomServerStore
{
    public List<CustomServer> Servers { get; set; } = new();
    public Dictionary<string, List<CustomSpot>> MesaExtraSpots { get; set; } = new();

    /// <summary>User-attached preview image/GIF per spot, keyed by "Map|Name", stored as a data URL.</summary>
    public Dictionary<string, string> SpotImages { get; set; } = new();
}
