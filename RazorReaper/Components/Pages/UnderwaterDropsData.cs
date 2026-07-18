// Underwater drop locations dataset — v1
// Compiled from community-documented spawn points (ARK community wiki tables,
// cave pages and player-verified forum lists). Coordinates are in-game lat/lon.
// All area names and access notes are written in our own words.
// Notes:
//  - Ragnarok's "deep sea" crates famously spawn on land in the desert; they are
//    included here with a land note so players don't comb the ocean for them.
//  - Genesis: Part 1 ocean crates have no reliably documented coordinate set and
//    are deliberately left out of v1.

namespace RazorReaper.Components.Pages
{
    public enum UnderwaterDropTier
    {
        DeepSea,    // red deep-sea crate quality
        CaveCrate,  // underwater cave crates (blue/yellow/red mix)
        Shipwreck   // Fjordur shipwreck chests
    }

    public sealed record UnderwaterDrop(
        string Map,
        string Area,
        double Lat,
        double Lon,
        UnderwaterDropTier Tier,
        string Note);

    public static class UnderwaterDropsData
    {
        public const string TheIsland = "The Island";
        public const string TheCenter = "The Center";
        public const string Ragnarok = "Ragnarok";
        public const string Valguero = "Valguero";
        public const string CrystalIsles = "Crystal Isles";
        public const string LostIsland = "Lost Island";
        public const string Fjordur = "Fjordur";

        public static readonly IReadOnlyList<string> Maps = new List<string>
        {
            TheIsland, TheCenter, Ragnarok, Valguero, CrystalIsles, LostIsland, Fjordur
        };

        public static readonly IReadOnlyList<UnderwaterDrop> Drops = new List<UnderwaterDrop>
        {
            // ===== The Island — 16 deep-sea crates + 2 underwater caves =====
            new(TheIsland, "Far southeast of Herbivore Island", 89.7, 90.6, UnderwaterDropTier.DeepSea, "Deep sea floor close to the map corner."),
            new(TheIsland, "South of the Southern Islets", 91.8, 69.8, UnderwaterDropTier.DeepSea, "Open sea floor along the southern border."),
            new(TheIsland, "Far south of The Footpaw", 91.4, 34.8, UnderwaterDropTier.DeepSea, "Deep water well off the southern coast."),
            new(TheIsland, "Southwest of Cragg's Island", 90.8, 11.4, UnderwaterDropTier.DeepSea, "Sea floor near the southwest corner."),
            new(TheIsland, "Southwest of Cragg's Island (outer)", 86.5, 9.1, UnderwaterDropTier.DeepSea, "Slightly further out on the same shelf."),
            new(TheIsland, "Western coast, middle", 59.3, 7.5, UnderwaterDropTier.DeepSea, "Deep trench running along the west border."),
            new(TheIsland, "Western coast, middle", 51.3, 11.2, UnderwaterDropTier.DeepSea, "Sea floor a short swim off the west coast."),
            new(TheIsland, "Western coast, north", 34.8, 8.1, UnderwaterDropTier.DeepSea, "Deep water off the northwest coastline."),
            new(TheIsland, "Northwest of Whitesky Peak", 10.0, 9.4, UnderwaterDropTier.DeepSea, "Cold deep water at the northwest corner."),
            new(TheIsland, "Northern shores, west", 10.6, 29.8, UnderwaterDropTier.DeepSea, "Sea floor along the northern border."),
            new(TheIsland, "Northern shores, middle", 10.5, 40.2, UnderwaterDropTier.DeepSea, "Open floor in the northern deep."),
            new(TheIsland, "Northern shores, east", 9.6, 61.7, UnderwaterDropTier.DeepSea, "Northern deep water, east section."),
            new(TheIsland, "North-northwest of the Dead Island", 7.9, 80.2, UnderwaterDropTier.DeepSea, "Deep floor above the Dead Island."),
            new(TheIsland, "Northeast of the Dead Island", 8.2, 91.2, UnderwaterDropTier.DeepSea, "Sea floor near the northeast corner."),
            new(TheIsland, "South of the Dead Island", 23.7, 86.3, UnderwaterDropTier.DeepSea, "Deep channel below the Dead Island."),
            new(TheIsland, "South of the Dead Island (inner)", 21.8, 78.6, UnderwaterDropTier.DeepSea, "Closer to the eastern coastline."),
            new(TheIsland, "Caverns of Lost Hope", 45.9, 88.9, UnderwaterDropTier.CaveCrate, "East-coast underwater cave; long swim with strong predators inside."),
            new(TheIsland, "Caverns of Lost Faith", 53.7, 10.4, UnderwaterDropTier.CaveCrate, "West-coast underwater cave; the easier of the two sea caves."),

            // ===== The Center — 29 sea spawns (4 inside triple-depth sea caves) =====
            new(TheCenter, "Southwest of Lava Island", 19.6, 46.3, UnderwaterDropTier.DeepSea, "Sea floor southwest of the volcano."),
            new(TheCenter, "South of Lava Island", 21.9, 58.6, UnderwaterDropTier.DeepSea, "Deep water below Lava Island."),
            new(TheCenter, "West of Half-Burnt Island", 21.4, 67.8, UnderwaterDropTier.DeepSea, "Open floor between the fire islands."),
            new(TheCenter, "South of Half-Burnt Island", 23.6, 75.0, UnderwaterDropTier.DeepSea, "Sea floor just south of the island."),
            new(TheCenter, "South of Half-Burnt Island", 21.3, 82.3, UnderwaterDropTier.DeepSea, "Further east on the same shelf."),
            new(TheCenter, "Eastern Trench, north", 2.6, 89.7, UnderwaterDropTier.DeepSea, "Top end of the deep eastern trench."),
            new(TheCenter, "Far east of Half-Burnt Island", 16.7, 91.6, UnderwaterDropTier.DeepSea, "Trench floor near the east border."),
            new(TheCenter, "Southeast of Half-Burnt Island", 22.9, 87.8, UnderwaterDropTier.DeepSea, "Deep water on the trench edge."),
            new(TheCenter, "Eastern Trench, middle", 30.6, 91.8, UnderwaterDropTier.DeepSea, "Mid-trench sea floor."),
            new(TheCenter, "Eastern Trench sea cave", 35.9, 93.7, UnderwaterDropTier.DeepSea, "Inside a deep sea cave at triple depth; heavy pressure."),
            new(TheCenter, "Eastern Trench, middle", 37.5, 92.1, UnderwaterDropTier.DeepSea, "Open trench floor."),
            new(TheCenter, "Sea cave north of South Tropical Island", 42.6, 78.2, UnderwaterDropTier.DeepSea, "Crate sits inside a deep sea cave, not on the open floor."),
            new(TheCenter, "Eastern Trench, middle", 43.3, 86.1, UnderwaterDropTier.DeepSea, "Trench floor west of the border wall."),
            new(TheCenter, "Eastern Trench, middle", 44.8, 92.0, UnderwaterDropTier.DeepSea, "Deep trench floor."),
            new(TheCenter, "Eastern Trench, middle", 46.3, 94.1, UnderwaterDropTier.DeepSea, "Close to the eastern border."),
            new(TheCenter, "Eastern Trench, south", 51.6, 95.8, UnderwaterDropTier.DeepSea, "Trench floor near the map edge."),
            new(TheCenter, "Eastern Trench, middle", 54.5, 91.2, UnderwaterDropTier.DeepSea, "Open trench floor."),
            new(TheCenter, "Sea cave east of South Tropical Island", 51.7, 83.8, UnderwaterDropTier.DeepSea, "Inside a triple-depth sea cave; bring a strong mount."),
            new(TheCenter, "Eastern Trench, south", 56.1, 86.1, UnderwaterDropTier.DeepSea, "Southern stretch of the trench."),
            new(TheCenter, "Eastern Trench, south", 65.5, 85.3, UnderwaterDropTier.DeepSea, "Deep floor toward the trench end."),
            new(TheCenter, "Eastern Trench, south", 65.4, 87.7, UnderwaterDropTier.DeepSea, "Neighbouring spawn on the same floor."),
            new(TheCenter, "Eastern Trench, south", 67.7, 88.3, UnderwaterDropTier.DeepSea, "Bottom section of the trench."),
            new(TheCenter, "Northeast of the Redwoods", 70.1, 95.7, UnderwaterDropTier.DeepSea, "Sea floor near the southeast border."),
            new(TheCenter, "North of the Redwoods, east", 74.4, 84.6, UnderwaterDropTier.DeepSea, "Open deep water above the forest coast."),
            new(TheCenter, "Sea cave north of the Redwoods", 72.7, 83.1, UnderwaterDropTier.DeepSea, "Crate spawns inside a deep sea cave pocket."),
            new(TheCenter, "North of the Redwoods, east", 71.4, 75.4, UnderwaterDropTier.DeepSea, "Sea floor between the islands."),
            new(TheCenter, "North of the Redwoods, middle", 74.0, 55.4, UnderwaterDropTier.DeepSea, "Deep channel floor."),
            new(TheCenter, "North of the Redwoods, middle", 71.0, 50.4, UnderwaterDropTier.DeepSea, "Open water on the channel floor."),
            new(TheCenter, "North of the Redwoods, middle", 68.0, 46.8, UnderwaterDropTier.DeepSea, "Western end of the channel."),

            // ===== Ragnarok — 5 documented deep-sea-tier crates, all on land =====
            new(Ragnarok, "Southern desert, near the coast", 84.1, 77.0, UnderwaterDropTier.DeepSea, "Land spawn — Ragnarok's deep-sea crates sit in the desert, not the ocean."),
            new(Ragnarok, "Middle of the desert", 70.0, 76.5, UnderwaterDropTier.DeepSea, "Land spawn on the open dunes."),
            new(Ragnarok, "Middle of the desert", 63.2, 78.1, UnderwaterDropTier.DeepSea, "Land spawn on the open dunes."),
            new(Ragnarok, "Middle of the desert", 59.7, 76.5, UnderwaterDropTier.DeepSea, "Land spawn on the open dunes."),
            new(Ragnarok, "Desert plateau top", 79.7, 51.9, UnderwaterDropTier.DeepSea, "Land spawn on top of a plateau; fly up to reach it."),

            // ===== Valguero — 10 player-verified spawns in the western ocean =====
            new(Valguero, "Western ocean floor", 48.7, 35.6, UnderwaterDropTier.DeepSea, "Quiet stretch of sea floor with little danger."),
            new(Valguero, "Western ocean floor", 46.2, 32.8, UnderwaterDropTier.DeepSea, "Calm open floor, easy grab."),
            new(Valguero, "Western ocean floor", 54.3, 30.6, UnderwaterDropTier.DeepSea, "Megalodons patrol this spot."),
            new(Valguero, "Western ocean floor", 35.7, 33.8, UnderwaterDropTier.DeepSea, "Usually clear of predators."),
            new(Valguero, "Western ocean floor", 36.2, 18.6, UnderwaterDropTier.DeepSea, "Eels roam nearby; keep your distance."),
            new(Valguero, "Western ocean floor", 39.8, 17.9, UnderwaterDropTier.DeepSea, "Jellyfish in the area; avoid dismounting."),
            new(Valguero, "Western ocean floor", 41.3, 37.2, UnderwaterDropTier.DeepSea, "Megalodons hunt around this drop."),
            new(Valguero, "Western ocean floor", 39.8, 29.8, UnderwaterDropTier.DeepSea, "A few megalodons nearby."),
            new(Valguero, "Western ocean floor", 38.1, 25.0, UnderwaterDropTier.DeepSea, "Megalodon territory."),
            new(Valguero, "Western ocean floor", 34.2, 39.0, UnderwaterDropTier.DeepSea, "Occasional megalodon packs pass through."),

            // ===== Crystal Isles — 23 deep-sea spawns + 10 underwater tunnel crates =====
            new(CrystalIsles, "Northwest ocean", 13.6, 31.6, UnderwaterDropTier.DeepSea, "Deep sea floor in the northwest waters."),
            new(CrystalIsles, "Northwest ocean", 17.0, 39.4, UnderwaterDropTier.DeepSea, "Open floor toward the bay mouth."),
            new(CrystalIsles, "Northwest ocean", 25.6, 17.1, UnderwaterDropTier.DeepSea, "Deep water off the western isles."),
            new(CrystalIsles, "Northwest ocean", 27.6, 13.2, UnderwaterDropTier.DeepSea, "Sea floor near the west border."),
            new(CrystalIsles, "Northwest ocean", 21.9, 16.9, UnderwaterDropTier.DeepSea, "Open deep water."),
            new(CrystalIsles, "Northwest ocean", 11.2, 17.4, UnderwaterDropTier.DeepSea, "Northern corner sea floor."),
            new(CrystalIsles, "Western ocean", 49.4, 14.9, UnderwaterDropTier.DeepSea, "Deep floor off the west coast."),
            new(CrystalIsles, "Western ocean", 42.0, 16.6, UnderwaterDropTier.DeepSea, "Open sea floor."),
            new(CrystalIsles, "Northwest ocean", 24.2, 14.1, UnderwaterDropTier.DeepSea, "Deep water near the border."),
            new(CrystalIsles, "Northwest ocean", 13.7, 40.1, UnderwaterDropTier.DeepSea, "Sea floor north of the bay."),
            new(CrystalIsles, "North-central waters", 25.2, 43.6, UnderwaterDropTier.DeepSea, "Deep pocket near the tunnel mouths."),
            new(CrystalIsles, "Western ocean", 40.2, 19.2, UnderwaterDropTier.DeepSea, "Open floor off the coast."),
            new(CrystalIsles, "Northwest ocean", 12.8, 28.7, UnderwaterDropTier.DeepSea, "Northern deep water."),
            new(CrystalIsles, "Northwest ocean", 11.9, 32.4, UnderwaterDropTier.DeepSea, "Sea floor along the north border."),
            new(CrystalIsles, "Northwest ocean", 29.4, 18.1, UnderwaterDropTier.DeepSea, "Deep water between the isles."),
            new(CrystalIsles, "Northwest ocean", 15.2, 16.2, UnderwaterDropTier.DeepSea, "Corner shelf floor."),
            new(CrystalIsles, "Northwest ocean", 15.4, 19.2, UnderwaterDropTier.DeepSea, "Nearby spawn on the same shelf."),
            new(CrystalIsles, "Northwest ocean", 11.4, 24.7, UnderwaterDropTier.DeepSea, "Northern border floor."),
            new(CrystalIsles, "Northwest ocean", 15.3, 26.2, UnderwaterDropTier.DeepSea, "Open deep water."),
            new(CrystalIsles, "Northwest ocean", 14.3, 20.1, UnderwaterDropTier.DeepSea, "Sea floor near the corner."),
            new(CrystalIsles, "Southwest ocean", 75.9, 14.6, UnderwaterDropTier.DeepSea, "Lone spawn in the southwest deep."),
            new(CrystalIsles, "Deep sea tunnel", 37.9, 19.9, UnderwaterDropTier.DeepSea, "Sits inside an underwater tunnel rather than the open floor."),
            new(CrystalIsles, "Beneath the bridge", 32.6, 76.3, UnderwaterDropTier.DeepSea, "Underwater spawn below a land bridge on the east side."),
            new(CrystalIsles, "Underwater tunnel network", 25.1, 45.2, UnderwaterDropTier.CaveCrate, "Tunnel crate; follow the flooded passages."),
            new(CrystalIsles, "Underwater tunnel network", 26.0, 48.0, UnderwaterDropTier.CaveCrate, "Tunnel crate inside the central passages."),
            new(CrystalIsles, "Underwater tunnel network", 28.4, 48.4, UnderwaterDropTier.CaveCrate, "Tunnel crate; watch your oxygen on the way in."),
            new(CrystalIsles, "Underwater tunnel network", 30.2, 51.5, UnderwaterDropTier.CaveCrate, "Tunnel crate deeper into the system."),
            new(CrystalIsles, "Underwater tunnel network", 22.3, 41.2, UnderwaterDropTier.CaveCrate, "Tunnel crate near a western entrance."),
            new(CrystalIsles, "Underwater tunnel network", 20.4, 41.1, UnderwaterDropTier.CaveCrate, "Tunnel crate close to the tunnel mouth."),
            new(CrystalIsles, "Underwater tunnel network", 30.9, 46.7, UnderwaterDropTier.CaveCrate, "Tunnel crate in a side passage."),
            new(CrystalIsles, "Underwater tunnel network", 30.2, 48.5, UnderwaterDropTier.CaveCrate, "Tunnel crate; tight squeeze for mounts."),
            new(CrystalIsles, "Underwater tunnel network", 28.3, 54.1, UnderwaterDropTier.CaveCrate, "Tunnel crate toward the eastern exits."),
            new(CrystalIsles, "Underwater tunnel network", 30.8, 54.7, UnderwaterDropTier.CaveCrate, "Tunnel crate at the far end of the system."),

            // ===== Lost Island — 24 curated water spawns (land spawns excluded) =====
            new(LostIsland, "Southern ocean", 70.8, 69.2, UnderwaterDropTier.DeepSea, "Open ocean floor."),
            new(LostIsland, "Southern ocean", 77.1, 63.7, UnderwaterDropTier.DeepSea, "Deep open water."),
            new(LostIsland, "Southern ocean", 76.6, 61.7, UnderwaterDropTier.DeepSea, "Sea floor near its neighbouring spawn."),
            new(LostIsland, "Southern ocean", 75.5, 59.5, UnderwaterDropTier.DeepSea, "Open floor in the southern deep."),
            new(LostIsland, "Southern ocean", 75.9, 54.3, UnderwaterDropTier.DeepSea, "General area confirmed; exact spot drifts."),
            new(LostIsland, "Southwest ocean", 88.8, 19.1, UnderwaterDropTier.DeepSea, "Open ocean floor."),
            new(LostIsland, "Southwest ocean", 92.4, 10.0, UnderwaterDropTier.DeepSea, "Deep water near the corner."),
            new(LostIsland, "Western ocean", 57.9, 9.2, UnderwaterDropTier.DeepSea, "Open sea floor off the west coast."),
            new(LostIsland, "Western border", 63.5, 6.9, UnderwaterDropTier.DeepSea, "Right against the world border."),
            new(LostIsland, "Northwest trench", 27.5, 9.4, UnderwaterDropTier.DeepSea, "Hidden in the trenches; oyster beds south, a dark cave northeast."),
            new(LostIsland, "Northwest trench", 17.6, 12.7, UnderwaterDropTier.DeepSea, "Bottom of a small but very deep trench."),
            new(LostIsland, "Northern ocean", 17.3, 58.8, UnderwaterDropTier.DeepSea, "Open ocean floor."),
            new(LostIsland, "Underwater lava falls", 16.6, 64.9, UnderwaterDropTier.DeepSea, "On a ledge near the underwater lava falls."),
            new(LostIsland, "Eastern border", 34.1, 90.9, UnderwaterDropTier.DeepSea, "Right beside the world border."),
            new(LostIsland, "Eastern ocean", 43.3, 88.5, UnderwaterDropTier.DeepSea, "Open sea floor."),
            new(LostIsland, "Hidden deep-sea loot cave", 13.0, 9.9, UnderwaterDropTier.CaveCrate, "Cave centre; the crate can sink into the sand and become unreachable."),
            new(LostIsland, "Hidden deep-sea loot cave", 12.6, 10.5, UnderwaterDropTier.CaveCrate, "Side pocket of the hidden cave."),
            new(LostIsland, "Hidden deep-sea loot cave", 15.9, 10.3, UnderwaterDropTier.CaveCrate, "Near the cave's southern entrance."),
            new(LostIsland, "Northern ocean", 11.6, 20.2, UnderwaterDropTier.DeepSea, "Open floor along the north border."),
            new(LostIsland, "Northern ocean", 13.2, 66.1, UnderwaterDropTier.DeepSea, "Deep water in the northeast."),
            new(LostIsland, "Eastern ocean", 25.1, 90.0, UnderwaterDropTier.DeepSea, "Sea floor near the east border."),
            new(LostIsland, "Eastern ocean", 52.9, 90.6, UnderwaterDropTier.DeepSea, "Open border water."),
            new(LostIsland, "Eastern ocean", 60.9, 87.8, UnderwaterDropTier.DeepSea, "Deep floor off the east coast."),
            new(LostIsland, "Southern ocean", 87.9, 22.9, UnderwaterDropTier.DeepSea, "Open floor in the southwest deep."),

            // ===== Fjordur — 20 shipwreck chests =====
            new(Fjordur, "Western ocean", 37.7, 12.9, UnderwaterDropTier.Shipwreck, "Sunken wreck on the sea floor."),
            new(Fjordur, "Western ocean", 37.2, 1.8, UnderwaterDropTier.Shipwreck, "Wreck close to the west border."),
            new(Fjordur, "Northwest ocean", 13.7, -1.4, UnderwaterDropTier.Shipwreck, "Wreck beyond the normal border line; approach from the northwest."),
            new(Fjordur, "Kairuku Islands", 2.7, 2.7, UnderwaterDropTier.Shipwreck, "Wreck by the penguin isles in the corner."),
            new(Fjordur, "Northern ocean", 0.6, 25.6, UnderwaterDropTier.Shipwreck, "Wreck along the northern edge."),
            new(Fjordur, "Northern ocean", -0.9, 51.3, UnderwaterDropTier.Shipwreck, "Wreck sits past the border line; swim carefully."),
            new(Fjordur, "Northeast corner", -0.5, 98.0, UnderwaterDropTier.Shipwreck, "Corner wreck at the map edge."),
            new(Fjordur, "Eastern ocean", 16.7, 102.3, UnderwaterDropTier.Shipwreck, "Wreck beyond the east border line."),
            new(Fjordur, "Eastern ocean", 57.1, 97.2, UnderwaterDropTier.Shipwreck, "Open-water wreck off the east coast."),
            new(Fjordur, "Southeast ocean", 78.6, 102.1, UnderwaterDropTier.Shipwreck, "Border wreck in the southeast."),
            new(Fjordur, "Southeast corner", 97.9, 99.2, UnderwaterDropTier.Shipwreck, "Corner wreck at the map edge."),
            new(Fjordur, "South of Balheimr", 99.6, 75.7, UnderwaterDropTier.Shipwreck, "Wreck off the fire island's south coast."),
            new(Fjordur, "Southern ocean", 95.8, 59.9, UnderwaterDropTier.Shipwreck, "Open-water wreck along the south edge."),
            new(Fjordur, "Central-south channel", 77.2, 62.8, UnderwaterDropTier.Shipwreck, "Wreck in the channel between the islands."),
            new(Fjordur, "Central channel", 61.0, 53.5, UnderwaterDropTier.Shipwreck, "Wreck on the channel floor."),
            new(Fjordur, "Central channel", 56.6, 49.7, UnderwaterDropTier.Shipwreck, "Wreck a short swim from the coast."),
            new(Fjordur, "East of Vardiland", 70.6, 45.6, UnderwaterDropTier.Shipwreck, "Wreck off Vardiland's eastern shore."),
            new(Fjordur, "Southern ocean", 95.5, 45.2, UnderwaterDropTier.Shipwreck, "Open-water wreck in the southern deep."),
            new(Fjordur, "Southwest of Vardiland", 98.7, 1.4, UnderwaterDropTier.Shipwreck, "Corner wreck far off the southwest coast."),
            new(Fjordur, "Abyssal Depths", 67.8, 20.6, UnderwaterDropTier.Shipwreck, "Wreck in the deep trench; alpha mosasaurs patrol here.")
        };
    }
}
