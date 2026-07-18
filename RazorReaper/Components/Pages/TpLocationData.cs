// TP Locations dataset v1
// Static teleport-location reference data for the TP Locations page.
// Facts (names, lat/lon, setplayerpos XYZ) compiled from community documentation;
// all notes written in-house. XYZ values are only included where reliably documented.
// Categories: obelisk | cave | loot | resource | landmark | terminal

namespace RazorReaper.Components.Pages;

public sealed record TpLocationEntry(
    string Map,
    string Name,
    string Category,
    double Lat,
    double Lon,
    string? SetPlayerPos,
    string Note);

public static class TpLocationData
{
    public const string Version = "v1";

    public static readonly IReadOnlyList<string> Maps = new List<string>
    {
        "The Island",
        "The Center",
        "Scorched Earth",
        "Ragnarok",
        "Aberration",
        "Extinction",
        "Valguero",
        "Genesis: Part 1",
        "Crystal Isles",
        "Genesis: Part 2",
        "Lost Island",
        "Fjordur"
    };

    public static readonly IReadOnlyList<string> Categories = new List<string>
    {
        "obelisk",
        "cave",
        "loot",
        "resource",
        "landmark",
        "terminal"
    };

    public static readonly IReadOnlyList<TpLocationEntry> Entries = new List<TpLocationEntry>
    {
        // ===== The Island =====
        new("The Island", "Red Obelisk", "obelisk", 79.8, 17.4, "-260638 238923 -11202", "South-western obelisk on Cragg's Island with a boss summon terminal."),
        new("The Island", "Green Obelisk", "obelisk", 58.9, 72.3, "178454 71660 -10081", "Eastern obelisk overlooking the plains east of the redwoods."),
        new("The Island", "Blue Obelisk", "obelisk", 25.4, 25.6, "-195154 -195984 33858", "High-altitude obelisk on the north-western mountains; bring fur gear."),
        new("The Island", "Central Cave", "cave", 41.5, 46.9, null, "Holds the Artifact of the Clever; tight tunnels with bats and spiders."),
        new("The Island", "North West Cave", "cave", 19.2, 19.0, null, "Holds the Artifact of the Skylord in the cold northern hills."),
        new("The Island", "Lower South Cave", "cave", 80.2, 53.5, null, "Beginner-friendly run for the Artifact of the Hunter."),
        new("The Island", "North East Cave", "cave", 14.7, 85.4, null, "Short coastal cave holding the Artifact of the Devourer."),
        new("The Island", "Upper South Cave", "cave", 68.3, 56.1, null, "Holds the Artifact of the Pack; watch for narrow crouch passages."),
        new("The Island", "Lava Cave", "cave", 70.6, 86.1, null, "Holds the Artifact of the Massive; lava channels line the route."),
        new("The Island", "Swamp Cave", "cave", 62.6, 37.2, null, "Holds the Artifact of the Immune; gas mask strongly recommended."),
        new("The Island", "Snow Cave", "cave", 29.4, 32.0, null, "Holds the Artifact of the Strong; one of the hardest land caves."),
        new("The Island", "Caverns of Lost Faith", "cave", 53.7, 10.4, null, "Mostly underwater run to the Artifact of the Brute on the west coast."),
        new("The Island", "Caverns of Lost Hope", "cave", 45.8, 89.3, null, "Deep underwater cave holding the Artifact of the Cunning."),
        new("The Island", "The Volcano", "resource", 42.4, 39.2, null, "Dense metal nodes on the slopes; the Tek Cave sits beneath the summit."),
        new("The Island", "Herbivore Island", "landmark", 82.5, 88.5, null, "Predator-free island in the south-east, a classic safe starter spot."),
        new("The Island", "Hidden Lake", "landmark", 43.6, 63.7, null, "Sheltered forest basin in the north-east, popular protected base site."),
        new("The Island", "Redwood Forests", "resource", 59.0, 45.5, null, "Tree-platform territory with rich wood and sap; thylas lurk on trunks."),

        // ===== The Center =====
        new("The Center", "Red Obelisk", "obelisk", 8.3, 59.0, null, "Northern obelisk; the guardian arena is summoned from any terminal."),
        new("The Center", "Green Obelisk", "obelisk", 34.8, 15.8, null, "Western obelisk near the jungle coast."),
        new("The Center", "Blue Obelisk", "obelisk", 50.7, 81.2, "250968 194402 -8487", "Eastern obelisk on the outer rim islands."),
        new("The Center", "Lava Oasis Cave", "cave", 15.8, 50.5, null, "Holds the Artifact of the Hunter beneath the northern lava fields."),
        new("The Center", "South Ice Cave", "cave", 60.0, 22.5, null, "Holds the Artifact of the Skylord; freezing interior."),
        new("The Center", "Lava Cave", "cave", 11.2, 67.4, null, "Holds the Artifacts of the Massive and the Strong; deadly lava jumps."),
        new("The Center", "North Ice Cave", "cave", 18.7, 29.7, null, "Holds the Artifacts of the Clever and the Devourer."),
        new("The Center", "Southeastern Trench", "cave", 69.1, 92.2, null, "Underwater trench dive for the Artifact of the Brute."),
        new("The Center", "Floating Island", "landmark", 31.0, 36.2, null, "The map's iconic levitating landmass; approximate centre coordinates."),

        // ===== Scorched Earth =====
        new("Scorched Earth", "Red Obelisk", "obelisk", 71.0, 39.2, null, "Southern obelisk; Manticore is summoned from any terminal."),
        new("Scorched Earth", "Green Obelisk", "obelisk", 50.0, 74.3, null, "Eastern obelisk out in the open dunes."),
        new("Scorched Earth", "Blue Obelisk", "obelisk", 17.3, 34.0, null, "North-western obelisk near the badlands."),
        new("Scorched Earth", "Grave of the Tyrants", "cave", 28.5, 29.4, null, "Holds the Artifact of the Crag; toughest cave on the map."),
        new("Scorched Earth", "Old Tunnels", "cave", 58.8, 47.9, null, "Holds the Artifact of the Gatekeeper beneath the central canyons."),
        new("Scorched Earth", "Ruins of Nosti", "cave", 77.9, 75.7, null, "Holds the Artifact of the Destroyer under the buried city."),
        new("Scorched Earth", "The World Scar", "loot", 78.0, 21.0, null, "Wyvern trench along the south-western rim; egg runs at your own risk."),

        // ===== Ragnarok =====
        new("Ragnarok", "Red Obelisk", "obelisk", 35.0, 85.7, "467435 -195919 -14209", "Eastern obelisk; arena tribute uses all ten artifacts."),
        new("Ragnarok", "Green Obelisk", "obelisk", 57.0, 38.2, "-155773 91671 11619", "Central obelisk in the green highlands."),
        new("Ragnarok", "Blue Obelisk", "obelisk", 18.1, 17.3, "-427662 -417081 -13747", "North-western obelisk near the snow border."),
        new("Ragnarok", "Jungle Dungeon", "cave", 18.7, 27.8, null, "Vine-covered land entrance; Artifact of the Hunter and the Lava Elemental inside."),
        new("Ragnarok", "Frozen Dungeon", "cave", 31.3, 33.7, null, "Waterfall entrance (flyer entrance at 30.9 / 37.8); Artifact of the Pack and the Iceworm Queen."),
        new("Ragnarok", "Life's Labyrinth", "cave", 51.6, 77.5, null, "Puzzle dungeon in the desert; several artifacts and trap gauntlets."),
        new("Ragnarok", "Carnivorous Caverns", "cave", 36.2, 49.2, null, "Holds the Artifacts of the Cunning and the Immune."),
        new("Ragnarok", "Fallen Redwood Cave", "cave", 85.8, 51.2, null, "Holds the Artifact of the Brute in the southern redwoods."),
        new("Ragnarok", "The Monkey's Puzzle", "cave", 24.6, 25.0, null, "Climbing cave holding the Artifact of the Strong."),
        new("Ragnarok", "Sunken Ships", "loot", 47.4, 2.3, null, "Deep-ocean wreck holding the Artifact of the Devourer."),

        // ===== Aberration =====
        new("Aberration", "Red Obelisk", "obelisk", 80.8, 20.3, null, "Southern terminal platform; used for tributes and transfers."),
        new("Aberration", "Green Obelisk", "obelisk", 22.5, 77.7, null, "Eastern terminal platform in the upper caverns."),
        new("Aberration", "Blue Obelisk", "obelisk", 18.9, 16.1, null, "North-western terminal platform."),
        new("Aberration", "Old Railway Cave", "cave", 48.3, 27.2, null, "Holds the Artifact of the Depths; ziplines make the route easier."),
        new("Aberration", "Hidden Grotto", "cave", 55.2, 65.9, null, "Holds the Artifact of the Shadows in the blue zone."),
        new("Aberration", "Elemental Vault", "cave", 82.4, 48.2, null, "Holds the Artifact of the Stalker; deep red-zone radiation run."),

        // ===== Extinction =====
        new("Extinction", "Red Obelisk", "obelisk", 77.6, 76.9, null, "Obelisk spire above the desert dome region."),
        new("Extinction", "Green Obelisk", "obelisk", 50.6, 29.7, null, "Obelisk spire on the western edge of the wasteland."),
        new("Extinction", "Blue Obelisk (Crater Forest)", "obelisk", 25.4, 22.5, null, "Blue spire reached through the crater forest."),
        new("Extinction", "Blue Obelisk (Snow Dome)", "obelisk", 21.8, 78.2, null, "Blue spire access on the snow dome side."),
        new("Extinction", "Desert Cave", "terminal", 87.4, 70.4, null, "Artifact of Chaos and the Desert Titan summon terminal."),
        new("Extinction", "Forest Cave", "terminal", 11.8, 39.3, null, "Artifact of Growth and the Forest Titan summon terminal."),
        new("Extinction", "Ice Cave", "terminal", 20.3, 62.2, null, "Artifact of the Void and the Ice Titan summon terminal."),
        new("Extinction", "Sanctuary City", "landmark", 50.0, 50.0, null, "Ruined city at the map's centre; City Terminals line the streets."),
        new("Extinction", "King Titan Terminal", "terminal", 4.0, 50.0, null, "Arena gate at the far northern edge of the Forbidden Zone (approximate)."),

        // ===== Valguero =====
        new("Valguero", "Red Obelisk", "obelisk", 76.1, 17.1, "-268136 213499 -39", "South-western obelisk; tributes go to the Forsaken Oasis fight."),
        new("Valguero", "Green Obelisk", "obelisk", 48.8, 76.1, "213785 -9234 -12739", "Eastern obelisk near the white cliffs."),
        new("Valguero", "Blue Obelisk", "obelisk", 9.3, 17.3, "-267025 -331553 688", "North-western obelisk in the snow."),
        new("Valguero", "The Lost Temple", "cave", 48.7, 90.3, null, "Cave system holding the Artifacts of the Devourer and the Brute."),
        new("Valguero", "Crag Cave", "cave", 33.5, 51.7, null, "Holds the Artifact of the Crag."),
        new("Valguero", "White Cliff Cave", "cave", 81.2, 88.1, null, "Start of the route to the Artifact of the Destroyer."),
        new("Valguero", "Skylord Cave", "cave", 8.7, 79.2, null, "Holds the Artifact of the Skylord; second entrance at 9.2 / 70.9."),
        new("Valguero", "Cunning Cave", "cave", 15.4, 27.3, null, "Holds the Artifact of the Cunning in the northern snow."),
        new("Valguero", "Gatekeeper Cave", "cave", 37.5, 57.9, null, "Holds the Artifact of the Gatekeeper deeper at 48.0 / 58.9."),
        new("Valguero", "Chalk Hills Cave", "cave", 73.6, 41.3, null, "Artifact of the Pack; the Strong and Immune sit deeper near a wild Broodmother."),

        // ===== Genesis: Part 1 =====
        new("Genesis: Part 1", "Magmasaur Cave (West Entrance)", "cave", 24.2, 86.2, null, "Volcano-biome cave leading to lava nests with Magmasaur eggs."),
        new("Genesis: Part 1", "Magmasaur Cave (East Entrance)", "cave", 29.0, 91.3, null, "Second way into the Magmasaur nesting chambers."),
        new("Genesis: Part 1", "Lunar Element Cave", "resource", 32.6, 17.1, null, "Lunar-biome cave with element shard rocks; watch the low gravity."),

        // ===== Crystal Isles =====
        new("Crystal Isles", "Red Obelisk", "obelisk", 63.8, 58.4, null, "Obelisk terminal; the Crystal Wyvern Queen is summoned separately."),
        new("Crystal Isles", "Green Obelisk", "obelisk", 51.4, 25.4, null, "Western obelisk terminal."),
        new("Crystal Isles", "Blue Obelisk", "obelisk", 25.6, 56.7, null, "Northern obelisk terminal."),
        new("Crystal Isles", "Brute Artifact Cliff", "loot", 71.9, 77.3, null, "Artifact of the Brute on the highest swamp cliff, no cave required."),
        new("Crystal Isles", "Clever Cave", "cave", 58.5, 33.2, null, "Cave entrance near Blood Falls; Artifact of the Clever inside."),
        new("Crystal Isles", "Crag Artifact", "loot", 76.2, 42.4, null, "Artifact of the Crag in the southern desert region."),
        new("Crystal Isles", "Cunning Artifact", "loot", 83.1, 22.6, null, "Artifact of the Cunning in the far south-west."),
        new("Crystal Isles", "Depths Cave", "cave", 32.7, 31.1, null, "Entrance to the Artifact of the Depths; artifact sits at 32.2 / 24.4."),
        new("Crystal Isles", "Destroyer Artifact", "loot", 66.6, 64.9, null, "Artifact of the Destroyer in the swamp biome."),
        new("Crystal Isles", "Desert Wyvern Den", "cave", 74.8, 42.8, null, "Artifact of the Devious inside the heir wyvern den."),
        new("Crystal Isles", "Devourer Artifact", "loot", 15.5, 44.8, null, "Artifact of the Devourer in the arctic north."),
        new("Crystal Isles", "Gatekeeper Artifact", "loot", 68.4, 50.5, null, "Artifact of the Gatekeeper at the desert's edge."),
        new("Crystal Isles", "Hunter Artifact", "loot", 66.5, 40.7, null, "Artifact of the Hunter south of the central plains."),
        new("Crystal Isles", "Immune Artifact", "loot", 30.0, 24.2, null, "Artifact of the Immune in the north-west."),
        new("Crystal Isles", "Lost Ice Cave", "cave", 17.9, 39.9, null, "Ice cave entrance; Artifact of the Lost sits at 22.4 / 43.9."),
        new("Crystal Isles", "Massive Artifact", "loot", 54.7, 52.0, null, "Artifact of the Massive near the map's centre."),
        new("Crystal Isles", "Pack Temple Cave", "cave", 48.8, 74.8, null, "Floating-islands temple; Artifact of the Pack just inside."),
        new("Crystal Isles", "Skylord Waterfall", "loot", 38.8, 44.8, null, "Artifact of the Skylord at the base of a waterfall."),
        new("Crystal Isles", "Shadows Artifact", "loot", 23.7, 73.5, null, "Artifact of the Shadows in the north-east."),
        new("Crystal Isles", "Stalker Dive Point", "cave", 13.8, 24.4, null, "Underwater entry for the Artifact of the Stalker; bring SCUBA."),
        new("Crystal Isles", "Strong Cave", "cave", 31.4, 50.7, null, "Snow-biome cave by a large waterfall; artifact at 33.9 / 55.5."),

        // ===== Genesis: Part 2 =====
        new("Genesis: Part 2", "Mutagen Cluster 1", "resource", 41.0, 15.0, null, "Mutagen bulb spawn inside Rockwell's Innards; GPS reading from within."),
        new("Genesis: Part 2", "Mutagen Cluster 2", "resource", 58.0, 27.0, null, "Mutagen bulb spawn inside Rockwell's Innards; GPS reading from within."),
        new("Genesis: Part 2", "Mutagen Cluster 3", "resource", 60.0, 25.0, null, "Mutagen bulb spawn inside Rockwell's Innards; GPS reading from within."),
        new("Genesis: Part 2", "Mutagen Cluster 4", "resource", 64.0, 28.0, null, "Mutagen bulb spawn inside Rockwell's Innards; GPS reading from within."),

        // ===== Lost Island =====
        new("Lost Island", "Red Obelisk", "obelisk", 28.6, 63.8, null, "Northern obelisk terminal."),
        new("Lost Island", "Green Obelisk", "obelisk", 62.7, 56.9, null, "Central-southern obelisk terminal."),
        new("Lost Island", "Blue Obelisk", "obelisk", 25.2, 34.4, null, "North-western obelisk terminal."),
        new("Lost Island", "Pack Cave", "cave", 37.3, 32.7, null, "Small den by the waterfall; Artifact of the Pack at 35.5 / 32.2."),
        new("Lost Island", "Strong Cave", "cave", 25.3, 54.9, null, "Entrance behind the waterfall; artifact deeper at 29.4 / 54.7."),
        new("Lost Island", "Hunter Cave", "cave", 36.5, 14.2, null, "Western cave holding the Artifact of the Hunter."),
        new("Lost Island", "Clever Dive Point", "cave", 24.9, 43.0, null, "Underwater jungle entrance; Artifact of the Clever at 25.5 / 42.6."),
        new("Lost Island", "Brute Cave", "cave", 81.8, 82.7, null, "South-eastern cave; artifact deeper at 84.4 / 80.2."),
        new("Lost Island", "Cunning Cave", "cave", 56.0, 70.4, null, "Holds the Artifact of the Cunning at 55.4 / 73.1."),
        new("Lost Island", "Immune Cave", "cave", 25.3, 71.2, null, "Holds the Artifact of the Immune at 24.2 / 68.9."),
        new("Lost Island", "Sunken Ship Ruin", "loot", 78.4, 20.9, null, "Underwater wreck holding the Artifact of the Devourer, no cave."),
        new("Lost Island", "Devious and Massive Cave", "cave", 58.9, 47.3, null, "One system holding both the Devious and Massive artifacts."),
        new("Lost Island", "Skylord Cave", "cave", 58.4, 57.0, null, "Holds the Artifact of the Skylord at 57.7 / 59.4."),

        // ===== Fjordur =====
        new("Fjordur", "Red Obelisk", "obelisk", 79.3, 96.3, null, "South-eastern obelisk terminal."),
        new("Fjordur", "Green Obelisk", "obelisk", 17.7, 80.7, null, "North-eastern obelisk terminal."),
        new("Fjordur", "Blue Obelisk", "obelisk", 74.6, 7.4, null, "South-western obelisk terminal."),
        new("Fjordur", "Bear Caverns", "cave", 8.8, 24.5, null, "Holds the Artifact of the Strong north of the redwoods."),
        new("Fjordur", "Drengrheimr", "cave", 3.3, 32.6, null, "Temple cave in the far north; Artifact of the Hunter inside."),
        new("Fjordur", "Mount Doom Caverns", "cave", 90.8, 78.1, null, "Holds the Artifact of the Immune in the volcanic south."),
        new("Fjordur", "Molten Caverns", "cave", 21.2, 57.4, null, "One cave holding both the Clever and Pack artifacts."),
        new("Fjordur", "The Snakepit", "cave", 49.4, 14.3, null, "Toxic, hot cave on Bolbjord; Artifact of the Brute. Bring a gas mask."),
        new("Fjordur", "The Frozen Fortress", "cave", 7.7, 23.5, null, "Ice fortress holding the Artifact of the Skylord."),
        new("Fjordur", "The Forgotten Caverns", "cave", 76.8, 66.0, null, "Underwater entry; Artifact of the Cunning inside."),
        new("Fjordur", "Mariana Caverns", "cave", 3.3, 3.7, null, "Deep-sea cave in the far north-west; Artifact of the Devourer."),
        new("Fjordur", "Nidisheim Depths", "cave", 71.8, 1.2, null, "Holds the Artifact of the Massive on the western edge."),
        new("Fjordur", "Stalker Cave (Asgard)", "cave", 56.9, 84.9, null, "Artifact of the Stalker; reached in the Asgard realm."),
        new("Fjordur", "Shadows Cave", "cave", 10.0, 84.4, null, "Holds the Artifact of the Shadows in the north-east."),
        new("Fjordur", "Beyla Terminal", "terminal", 4.2, 47.3, null, "Spider Cavern terminal; summon Beyla with 30 Runestones."),
        new("Fjordur", "Steinbjorn Terminal", "terminal", 77.1, 30.9, null, "Summon terminal for Steinbjorn; 30 Runestones required."),
        new("Fjordur", "Hati and Skoll Terminal", "terminal", 20.5, 37.1, null, "Altar terminal for the wolf pair; 30 Runestones required."),
        new("Fjordur", "Broodmother Terminal", "terminal", 57.4, 65.6, null, "World-boss terminal for the Broodmother fight."),
        new("Fjordur", "Megapithecus Terminal", "terminal", 56.9, 85.1, null, "World-boss terminal for the Megapithecus fight."),
        new("Fjordur", "Dragon Terminal", "terminal", 86.2, 4.5, null, "World-boss terminal for the Dragon fight."),
        new("Fjordur", "Realm Portal Room", "terminal", 40.7, 57.5, null, "Portal chamber used to hop between the Fjordur realms.")
    };
}
