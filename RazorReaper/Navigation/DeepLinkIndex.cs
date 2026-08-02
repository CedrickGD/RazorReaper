using RazorReaper.Components.Pages;

namespace RazorReaper.Navigation;

/// <summary>
/// Indexes the contents of the data-heavy reference pages so the palette can jump straight
/// to a map, a cave, a drop area or a boss instead of only opening the page that lists them.
///
/// Everything here is derived from the existing static datasets — adding a cave to
/// <see cref="CaveDatabase"/> makes it searchable with no change on this side. The pages
/// themselves read the matching query parameters and preselect what the link points at.
/// </summary>
public static class DeepLinkIndex
{
    private const string TpLocations = "TP Locations";
    private const string UnderwaterDrops = "Underwater Drops";
    private const string MapMods = "Map Mods";
    private const string Bosses = "Bosses";

    // Built once, on first palette open, rather than at startup — the app launches into
    // Home and most sessions never need this.
    private static readonly Lazy<IReadOnlyList<PaletteItem>> Lazy = new(Build);

    public static IReadOnlyList<PaletteItem> Items => Lazy.Value;

    private static IReadOnlyList<PaletteItem> Build()
    {
        var items = new List<PaletteItem>();
        AddTpLocations(items);
        AddUnderwaterDrops(items);
        AddCaves(items);
        AddBosses(items);
        return items;
    }

    // ---- TP Locations ------------------------------------------------------

    private static void AddTpLocations(List<PaletteItem> items)
    {
        foreach (var map in TpLocationData.Maps)
        {
            var count = TpLocationData.Entries.Count(e => e.Map == map);
            if (count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"tp:map:{map}",
                Title = map,
                Subtitle = $"{TpLocations} · {count} locations",
                Category = TpLocations,
                IconSvg = NavIcons.MapPin,
                Route = $"/tp-locations?map={Encode(map)}",
                Keywords = ["tp", "teleport", "map", "coordinates", "setplayerpos"]
            });
        }

        foreach (var entry in TpLocationData.Entries)
        {
            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"tp:{entry.Map}|{entry.Name}",
                Title = entry.Name,
                Subtitle = $"{TpLocations} · {entry.Map}",
                Category = TpLocations,
                IconSvg = NavIcons.TpLocations,
                Route = $"/tp-locations?map={Encode(entry.Map)}&q={Encode(entry.Name)}",
                Keywords = [entry.Map, entry.Category, "tp", "teleport", "coordinates"]
            });
        }
    }

    // ---- Underwater Drops --------------------------------------------------

    private static void AddUnderwaterDrops(List<PaletteItem> items)
    {
        foreach (var map in UnderwaterDropsData.Maps)
        {
            var count = UnderwaterDropsData.Drops.Count(d => d.Map == map);
            if (count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"uw:map:{map}",
                Title = map,
                Subtitle = $"{UnderwaterDrops} · {count} crates",
                Category = UnderwaterDrops,
                IconSvg = NavIcons.MapPin,
                Route = $"/underwater-drops?map={Encode(map)}",
                Keywords = ["underwater", "drops", "loot", "deep sea", "ocean", "map"]
            });
        }

        // Areas repeat across crates within a map, so index each distinct area once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drop in UnderwaterDropsData.Drops)
        {
            var id = $"uw:{drop.Map}|{drop.Area}";
            if (!seen.Add(id)) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = id,
                Title = drop.Area,
                Subtitle = $"{UnderwaterDrops} · {drop.Map}",
                Category = UnderwaterDrops,
                IconSvg = NavIcons.UnderwaterDrops,
                Route = $"/underwater-drops?map={Encode(drop.Map)}&q={Encode(drop.Area)}",
                Keywords = [drop.Map, drop.Tier.ToString(), "underwater", "drop", "loot", "crate"]
            });
        }
    }

    // ---- Map Mods (cave database) -----------------------------------------

    private static void AddCaves(List<PaletteItem> items)
    {
        foreach (var map in CaveDatabase.Maps)
        {
            var count = CaveDatabase.All.Count(c => c.Map == map);
            if (count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"cave:map:{map}",
                Title = map,
                Subtitle = $"{MapMods} · {count} spots",
                Category = MapMods,
                IconSvg = NavIcons.MapPin,
                Route = $"/map-mods?map={Encode(map)}",
                Keywords = ["cave", "caves", "spots", "map", "artifact", "poi"]
            });
        }

        foreach (var cave in CaveDatabase.All)
        {
            var keywords = new List<string> { cave.Map, "cave", "artifact", cave.Difficulty.ToString() };
            if (!string.IsNullOrWhiteSpace(cave.Artifact)) keywords.Add(cave.Artifact);
            if (cave.Hazards is { Length: > 0 }) keywords.AddRange(cave.Hazards);

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"cave:{cave.Map}|{cave.Name}",
                Title = cave.Name,
                Subtitle = string.IsNullOrWhiteSpace(cave.Artifact)
                    ? $"{MapMods} · {cave.Map}"
                    : $"{MapMods} · {cave.Map} · {cave.Artifact}",
                Category = MapMods,
                IconSvg = NavIcons.Caves,
                Route = $"/map-mods?map={Encode(cave.Map)}&spot={Encode(cave.Name)}",
                Keywords = keywords
            });
        }
    }

    // ---- Bosses ------------------------------------------------------------

    private static void AddBosses(List<PaletteItem> items)
    {
        foreach (var map in Components.Pages.Bosses.BossMaps)
        {
            if (map.Bosses.Count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"boss:map:{map.Name}",
                Title = map.Name,
                Subtitle = $"{Bosses} · {map.Bosses.Count} entries",
                Category = Bosses,
                IconSvg = NavIcons.MapPin,
                Route = $"/bosses?map={Encode(map.Name)}",
                Keywords = ["boss", "bosses", "tribute", "map", "requirements"]
            });

            foreach (var boss in map.Bosses)
            {
                var keywords = new List<string> { map.Name, boss.Type, "boss", "tribute", "arena" };
                if (!string.IsNullOrWhiteSpace(boss.Arena)) keywords.Add(boss.Arena);
                keywords.AddRange(boss.Tags);

                items.Add(new PaletteItem
                {
                    Kind = PaletteKind.DeepLink,
                    Id = $"boss:{map.Name}|{boss.Name}",
                    Title = boss.Name,
                    Subtitle = $"{Bosses} · {map.Name}",
                    Category = Bosses,
                    IconSvg = NavIcons.Boss,
                    Route = $"/bosses?map={Encode(map.Name)}&boss={Encode(boss.Name)}",
                    Keywords = keywords
                });
            }
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);
}
