using System.Collections.Concurrent;
using RazorReaper.Components.Pages;
using RazorReaper.Services.Localization;

namespace RazorReaper.Navigation;

/// <summary>
/// Indexes the contents of the data-heavy reference pages so the palette can jump straight
/// to a map, a cave, a drop area or a boss instead of only opening the page that lists them.
///
/// Everything here is derived from the existing static datasets — adding a cave to
/// <see cref="CaveDatabase"/> makes it searchable with no change on this side. The pages
/// themselves read the matching query parameters and preselect what the link points at.
///
/// What is translated here is the frame, never the contents: the page a row belongs to and the
/// count beside it. Map names, cave names, artifacts and bosses are ARK's own proper nouns and
/// stay exactly as the game spells them — a "Ragnarok" translated into Russian is a row nobody
/// can find, in either language.
/// </summary>
public static class DeepLinkIndex
{
    /// <summary>
    /// The four parent page names and the two templates that frame a row, resolved once per
    /// build instead of per row: this index is ~1500 rows and every one of them would otherwise
    /// take four dictionary lookups.
    /// </summary>
    private sealed record Frame(ILocalizer L, string TpLocations, string UnderwaterDrops, string MapMods, string Bosses)
    {
        public static Frame For(ILocalizer localizer) => new(
            localizer,
            localizer.T("nav.page.tp-locations"),
            localizer.T("nav.page.underwater-drops"),
            localizer.T("nav.page.map-mods"),
            localizer.T("nav.page.bosses"));

        /// <summary>"Page · detail" — the separator lives in the dictionary, not in this file.</summary>
        public string Sub(string page, string detail) => L.T("palette.deeplink.parent", page, detail);

        public string Count(string key, int count) => L.T(key, count);
    }

    // Built on first palette open rather than at startup — the app launches into Home and most
    // sessions never need this — and then once more per language that is actually used. Four
    // cached lists is the worst case and only reached by someone touring the picker.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<PaletteItem>> Cache = new(StringComparer.Ordinal);

    /// <summary>Every deep link, with its supporting text in the language <paramref name="localizer"/> is on.</summary>
    public static IReadOnlyList<PaletteItem> Items(ILocalizer localizer)
        => Cache.GetOrAdd(localizer.Language, _ => Build(Frame.For(localizer)));

    private static IReadOnlyList<PaletteItem> Build(Frame frame)
    {
        var items = new List<PaletteItem>();
        AddTpLocations(items, frame);
        AddUnderwaterDrops(items, frame);
        AddCaves(items, frame);
        AddBosses(items, frame);
        return items;
    }

    // ---- TP Locations ------------------------------------------------------

    private static void AddTpLocations(List<PaletteItem> items, Frame frame)
    {
        var page = frame.TpLocations;

        foreach (var map in TpLocationData.Maps)
        {
            var count = TpLocationData.Entries.Count(e => e.Map == map);
            if (count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"tp:map:{map}",
                Title = map,
                Subtitle = frame.Sub(page, frame.L.T("palette.deeplink.tp.count", count)),
                Category = page,
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
                Subtitle = frame.Sub(page, entry.Map),
                Category = page,
                IconSvg = NavIcons.TpLocations,
                Route = $"/tp-locations?map={Encode(entry.Map)}&q={Encode(entry.Name)}",
                Keywords = [entry.Map, entry.Category, "tp", "teleport", "coordinates"]
            });
        }
    }

    // ---- Underwater Drops --------------------------------------------------

    private static void AddUnderwaterDrops(List<PaletteItem> items, Frame frame)
    {
        var page = frame.UnderwaterDrops;

        foreach (var map in UnderwaterDropsData.Maps)
        {
            var count = UnderwaterDropsData.Drops.Count(d => d.Map == map);
            if (count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"uw:map:{map}",
                Title = map,
                Subtitle = frame.Sub(page, frame.L.T("palette.deeplink.uw.count", count)),
                Category = page,
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
                Subtitle = frame.Sub(page, drop.Map),
                Category = page,
                IconSvg = NavIcons.UnderwaterDrops,
                Route = $"/underwater-drops?map={Encode(drop.Map)}&q={Encode(drop.Area)}",
                Keywords = [drop.Map, drop.Tier.ToString(), "underwater", "drop", "loot", "crate"]
            });
        }
    }

    // ---- Map Mods (cave database) -----------------------------------------

    private static void AddCaves(List<PaletteItem> items, Frame frame)
    {
        var page = frame.MapMods;

        foreach (var map in CaveDatabase.Maps)
        {
            var count = CaveDatabase.All.Count(c => c.Map == map);
            if (count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"cave:map:{map}",
                Title = map,
                Subtitle = frame.Sub(page, frame.L.T("palette.deeplink.cave.count", count)),
                Category = page,
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
                    ? frame.Sub(page, cave.Map)
                    : frame.Sub(page, frame.Sub(cave.Map, cave.Artifact)),
                Category = page,
                IconSvg = NavIcons.Caves,
                Route = $"/map-mods?map={Encode(cave.Map)}&spot={Encode(cave.Name)}",
                Keywords = keywords
            });
        }
    }

    // ---- Bosses ------------------------------------------------------------

    private static void AddBosses(List<PaletteItem> items, Frame frame)
    {
        var page = frame.Bosses;

        foreach (var map in Components.Pages.Bosses.BossMaps)
        {
            if (map.Bosses.Count == 0) continue;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.DeepLink,
                Id = $"boss:map:{map.Name}",
                Title = map.Name,
                Subtitle = frame.Sub(page, frame.L.T("palette.deeplink.boss.count", map.Bosses.Count)),
                Category = page,
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
                    Subtitle = frame.Sub(page, map.Name),
                    Category = page,
                    IconSvg = NavIcons.Boss,
                    Route = $"/bosses?map={Encode(map.Name)}&boss={Encode(boss.Name)}",
                    Keywords = keywords
                });
            }
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);
}
