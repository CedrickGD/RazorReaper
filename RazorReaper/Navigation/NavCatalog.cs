namespace RazorReaper.Navigation;

/// <summary>
/// One page in the app. The sidebar renders these as links; the command palette
/// indexes them as search targets. Both read the same records so the two can't drift.
/// </summary>
/// <param name="Badge">
/// A word the sidebar prints beside the label, for a page that is not in every plan —
/// "Lifetime" today. Null for the ordinary pages, which is nearly all of them; this is a
/// marker, not decoration, so a second use has to mean a second kind of restriction.
/// </param>
public sealed record NavPage(
    string Label,
    string Route,
    string Category,
    string IconSvg,
    string Description,
    string[] Keywords,
    string? Badge = null)
{
    /// <summary>
    /// Where the sidebar reads this page's name from once a language other than English is on.
    /// Derived from the route rather than written out a second time: a key typed by hand is a
    /// key that goes stale the first time a page is renamed. <see cref="Label"/> stays the
    /// English original, which is what diagnostics, telemetry and Rich Presence report.
    /// </summary>
    public string LabelKey => "nav.page." + NavCatalog.Normalize(Route).Replace('/', '.');

    /// <summary>
    /// Where the supporting line under the page's name is read from — the command palette's
    /// subtitle. Derived the same way as <see cref="LabelKey"/> and for the same reason, and
    /// <see cref="Description"/> stays the English original: the palette still matches on it,
    /// so an English phrase someone remembers keeps finding the page in any language.
    /// </summary>
    public string DescriptionKey => LabelKey + ".description";
}

/// <summary>
/// A sidebar category. The sidebar renders one row per group; hovering the row opens a
/// flyout listing <see cref="Pages"/>.
/// </summary>
/// <param name="Name">
/// The English name, and the group's identity: <see cref="NavPage.Category"/> is matched
/// against it and the sidebar remembers the open group by it. Translating it in place would
/// break both, so the translated spelling is looked up under <see cref="NameKey"/> instead.
/// </param>
public sealed record NavGroup(string Name, string IconSvg, IReadOnlyList<NavPage> Pages)
{
    public string NameKey => "nav.group." + NavCatalog.Slug(Name);
}

/// <summary>
/// Single source of truth for the app's page structure.
///
/// To add a page: add one entry to the appropriate group below. The sidebar picks it up,
/// the palette indexes it, and pinned/recents can resolve it — no second list to update.
/// </summary>
public static class NavCatalog
{
    public static readonly IReadOnlyList<NavGroup> Groups = new[]
    {
        new NavGroup("Core", NavIcons.CatCore, new[]
        {
            new NavPage("Home", "/home", "Core", NavIcons.Home,
                "Dashboard, updates & recent activity",
                new[] { "home", "dashboard", "updates", "activity", "welcome" }),

            new NavPage("Server", "/server", "Core", NavIcons.Server,
                "Connect and manage ARK servers",
                new[] { "server", "connect", "ip", "manage", "query" }),

            new NavPage("Game", "/game", "Core", NavIcons.Game,
                "Control and monitor ARK: Survival Evolved",
                new[] { "game", "launch", "start", "ark", "monitor", "status", "running" }),

            new NavPage("My account", "/account", "Core", NavIcons.CharManager,
                "Your profile picture, Discord account and connected installations",
                new[] { "account", "profile", "avatar", "picture", "discord", "login", "sign in", "register" }),

            new NavPage("Settings", "/settings", "Core", NavIcons.Settings,
                "Appearance, audio and app behaviour",
                new[] { "settings", "preferences", "options", "config", "appearance", "theme", "accent", "colour", "color", "font", "scale", "audio", "sound", "discord", "app" }),

            new NavPage("Global Hotkeys", "/hotkeys", "Core", NavIcons.Hotkeys,
                "Every system-wide hotkey in one place",
                new[] { "hotkeys", "keys", "keybinds", "shortcuts", "bindings", "toggle", "overview", "global" }),
        }),

        new NavGroup("ARK Tweaks", NavIcons.CatTweaks, new[]
        {
            new NavPage("INI Changer", "/ini-changer", "ARK Tweaks", NavIcons.Ini,
                "Manage & optimize ARK configuration presets",
                new[] { "ini", "config", "configuration", "presets", "settings", "basedeviceprofiles", "optimize" }),

            new NavPage("INI Builder", "/ini-builder", "ARK Tweaks", NavIcons.IniBuilder,
                "One-click Game.ini & GameUserSettings.ini presets with auto-backup",
                new[] { "ini", "builder", "gameusersettings", "game.ini", "presets", "backup", "restore", "graphics", "fps", "pvp" }),

            new NavPage("Vision Tools", "/vision", "ARK Tweaks", NavIcons.Vision,
                "TEK camera, scope visibility, and custom FOV",
                new[] { "vision", "fov", "camera", "tek", "suit", "scope", "zoom" }),

            new NavPage("Gamma", "/gamma", "ARK Tweaks", NavIcons.Gamma,
                "System-wide screen gamma with hotkey and Logitech G HUB triggers, presets and a cycle",
                new[] { "gamma", "brightness", "screen", "vision", "ghub", "g hub", "logitech", "hotkey", "preset", "cycle", "monitor", "dark", "night" }),

            new NavPage("Launch Options", "/launch-options", "ARK Tweaks", NavIcons.Launch,
                "Quick ARK startup flags with trade-offs",
                new[] { "launch", "options", "flags", "startup", "arguments", "steam", "parameters" }),

            new NavPage("Fonts", "/fonts", "ARK Tweaks", NavIcons.Fonts,
                "Customize ARK font settings",
                new[] { "fonts", "text", "typography", "chinese", "global", "install" }),

            new NavPage("Pixel Glitch", "/pixel", "ARK Tweaks", NavIcons.Pixel,
                "Manage ARK texture files for visual mods",
                new[] { "pixel", "glitch", "texture", "visual", "riot", "saddle", "armor" }),

            new NavPage("Paintings", "/paintings", "ARK Tweaks", NavIcons.Paintings,
                "Manage your in-game paintings",
                new[] { "paintings", "mypaintings", "art", "images" }),
        }),

        new NavGroup("Custom ARK", NavIcons.CatCustom, new[]
        {
            new NavPage("Sky Changer", "/custom-lab", "Custom ARK", NavIcons.Lab,
                "Replace the in-game sky with an image or solid color",
                new[] { "sky", "changer", "injector", "custom", "texture", "color" }),

            new NavPage("Loading Screen", "/loading-screen", "Custom ARK", NavIcons.LoadingScreen,
                "Replace ARK's startup and loading videos with your own — reversible",
                new[] { "loading", "screen", "video", "movie", "startup", "intro", "replace", "custom" }),

            new NavPage("Char Manager", "/char-manager", "Custom ARK", NavIcons.CharManager,
                "Manage saved ARK character appearance presets — rename, edit sliders, import and export",
                new[] { "char", "character", "preset", "appearance", "manager", "slider", "body", "face", "skin", "import", "export" }),

            new NavPage("Stretched Res", "/stretched-res", "Custom ARK", NavIcons.StretchedRes,
                "Switch the desktop to a stretched resolution with a safe 15-second auto-revert",
                new[] { "stretched", "resolution", "res", "stretch", "1440x1080", "1280x1024", "1024x768", "hitbox", "aspect", "display", "nvidia", "scaling" }),
        }),

        new NavGroup("Automation", NavIcons.CatAutomation, new[]
        {
            new NavPage("Auto Clicker", "/autoclicker", "Automation", NavIcons.Clicker,
                "Advanced mouse automation tool",
                new[] { "autoclicker", "auto", "clicker", "mouse", "automation", "click", "hotkey" }),

            // Scripts replaced the macro pages entirely — /macros and /crafting-scripts
            // both redirect here, and their keywords moved across so searching "macro"
            // or "ahk" still lands somewhere useful.
            new NavPage("Scripts", "/scripts", "Automation", NavIcons.ScriptsHub,
                "Premade automation that runs natively — no external tools",
                new[] { "scripts", "script", "automation", "macro", "macros", "ahk", "autohotkey", "jitbit", "crafting",
                        "yuty", "auto walk", "mammoth", "turret", "turret filler", "ammo", "filler", "fill", "farm", "afk", "anti afk", "exo", "noglin", "flak", "take all", "astro", "tek saddle", "dino ready", "fast tp", "download", "inv size",
                        // The armor script is called "Armor Swap" now and works with riot and tek
                        // too — "flak" stays above because that is what people still type.
                        "armor swap", "armor", "riot", "durability" }),



            new NavPage("HUD Overlay", "/hud-overlay", "Automation", NavIcons.Hud,
                "In-game HUD overlay with clock, session timer, server info, tool status and alerts",
                new[] { "hud", "overlay", "clock", "session", "timer", "server info", "notifier", "alerts", "on-screen", "osd", "ingame" }),

            new NavPage("Notifier", "/notifier", "Automation", NavIcons.Notifier,
                "Live alerts for rare dinos, resources, element nodes and OSD events, shown on the HUD with sound",
                new[] { "notifier", "scanner", "alerts", "rare dino", "resource", "element node", "osd", "notifications", "hud", "discord", "cluster" }),
        }),

        new NavGroup("Mods & Intel", NavIcons.CatIntel, new[]
        {
            new NavPage("Mutagen Prices", "/dino-prices", "Mods & Intel", NavIcons.Mutagen,
                "Browse creature mutation values by name",
                new[] { "mutagen", "prices", "dino", "creature", "gen2", "mutation", "values" }),

            new NavPage("Line List", "/line-list", "Mods & Intel", NavIcons.LineList,
                "Track breeding lines with stats and mutations, and generate WTS/WTB trade posts",
                new[] { "breeding", "line", "lines", "mutations", "stats", "wts", "wtb", "trade", "sell", "buy", "mutagen", "post" }),

            new NavPage("OC BPs", "/oc-bps", "Mods & Intel", NavIcons.Blueprint,
                "Genesis 2 mission rewards for overcapped blueprints",
                new[] { "oc", "blueprints", "bps", "genesis", "mission", "rewards", "overcapped" }),

            new NavPage("Bosses", "/bosses", "Mods & Intel", NavIcons.Boss,
                "Boss tribute guide sorted by map",
                new[] { "bosses", "tribute", "guide", "map", "requirements", "mini-boss", "fight" }),

            // The one page in the app that is not in every plan. It sits with the other ARK
            // knowledge pages rather than in a section of its own — a paywall is not a category.
            //
            // "Dino levels", not the page's own "Higher dino levels": this row is the only one
            // carrying a marker, so its label has the least room of any in the sidebar, and the
            // longer name clipped to "High…" beside the chip at the default width. The heading on
            // the page keeps the full name, where there is room for it.
            new NavPage("Dino levels", "/guides/dino-level", "Mods & Intel", NavIcons.DinoLevels,
                "How to push a tamed creature's level higher",
                new[] { "dino", "dinos", "level", "levels", "higher", "tame", "taming", "retame", "re-tame",
                        "noglin", "unclaim", "transmitter", "upload", "guide", "lifetime" },
                Badge: "Lifetime"),

            new NavPage("TP Locations", "/tp-locations", "Mods & Intel", NavIcons.TpLocations,
                "Teleport location database — obelisks, caves, terminals and landmarks for every map",
                new[] { "tp", "teleport", "setplayerpos", "coordinates", "obelisk", "cave", "artifact", "terminal", "locations", "lat", "lon" }),

            new NavPage("Underwater Drops", "/underwater-drops", "Mods & Intel", NavIcons.UnderwaterDrops,
                "Underwater loot crate locations with coordinates for every ocean map",
                new[] { "underwater", "drops", "loot", "crates", "deep sea", "ocean", "shipwreck", "coordinates", "dive", "sea" }),

            new NavPage("Map Mods", "/map-mods", "Mods & Intel", NavIcons.Caves,
                "Modded map spots — caves, landmarks, obelisks and POIs — plus the artifact-cave database, with coordinates and notes",
                new[] { "map", "mods", "map-mods", "caves", "cave", "artifact", "artifacts", "landmarks", "obelisk", "poi", "spots", "coordinates", "hazards", "loot", "mesa", "dungeon" }),

            new NavPage("Steam Mods", "/steam-mods", "Mods & Intel", NavIcons.SteamMods,
                "Browse installed workshop mods",
                new[] { "steam", "workshop", "mods", "installed", "browse" }),
        }),

        new NavGroup("Utilities", NavIcons.CatUtilities, new[]
        {
            new NavPage("Building", "/building", "Utilities", NavIcons.Building,
                "Foundation raising and lowering tutorials",
                new[] { "building", "foundation", "raising", "lowering", "tutorials", "video" }),

            new NavPage("Desync", "/desync", "Utilities", NavIcons.Desync,
                "Freeze your position server-side (admin, auto-reverts)",
                new[] { "desync", "lag", "freeze", "network", "qos", "firewall", "block" }),

            new NavPage("File Modifier", "/file-modifier", "Utilities", NavIcons.FileMod,
                "Remove/replace game files + clear redundant SeekFree data",
                new[] { "file", "modifier", "seekfree", "cleanup", "delete", "replace", "remove", "disk", "space", "texture", "loose" }),

            new NavPage("Crosshair", "/crosshair", "Utilities", NavIcons.Crosshair,
                "Always-on-top crosshair overlay with editor & presets",
                new[] { "crosshair", "overlay", "reticle", "aim", "dot", "valorant", "cs", "sniper", "workshop", "crosshairx", "rainbow", "preset" }),

            new NavPage("Convert", "/convert", "Utilities", NavIcons.Convert,
                "Convert video, image and audio files between formats",
                new[] { "convert", "converter", "convertx", "format", "video", "image", "audio",
                        "mp4", "wmv", "mkv", "avi", "webm", "mov", "gif", "png", "jpg", "webp",
                        "mp3", "wav", "flac", "transcode", "ffmpeg", "loading", "screen" }),

            new NavPage("Compact ARK", "/compact-ark", "Utilities", NavIcons.Compact,
                "Shrink the ARK install with Windows NTFS compression",
                new[] { "compact", "compress", "ntfs", "lzx", "disk", "space", "shrink", "size", "storage" }),
        }),

        new NavGroup("Help & About", NavIcons.CatHelp, new[]
        {
            new NavPage("Feedback & Support", "/feedback", "Help & About", NavIcons.Feedback,
                "Share feedback, or report a problem with a system snapshot attached",
                new[] { "feedback", "support", "bug", "report", "problem", "issue", "idea", "opinion", "suggestion", "feature", "request", "contact", "help", "diagnostics" }),
            new NavPage("Support inbox", "/inbox", "Help & About", NavIcons.Feedback,
                "Private replies to your reports", new[] { "inbox", "reply", "messages", "support" }),

            new NavPage("Troubleshoot", "/troubleshoot", "Help & About", NavIcons.Troubleshoot,
                "Logging, diagnostics, and quick fixes",
                new[] { "troubleshoot", "diagnostics", "logging", "error", "fix", "debug", "logs" }),

            new NavPage("Credits", "/credits", "Help & About", NavIcons.Credits,
                "Developer information & contact",
                new[] { "credits", "about", "developer", "contact", "info", "support" }),
        }),
    };

    /// <summary>Every page, flattened, in sidebar order.</summary>
    public static readonly IReadOnlyList<NavPage> Pages =
        Groups.SelectMany(g => g.Pages).ToList();

    private static readonly IReadOnlyDictionary<string, NavPage> ByRoute =
        Pages.ToDictionary(p => Normalize(p.Route), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Strips leading/trailing slashes and any query/fragment so "/bosses/", "bosses"
    /// and "bosses?map=Ragnarok" all resolve to the same page.
    /// </summary>
    public static string Normalize(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return string.Empty;
        return route.Split('?', '#')[0].Trim('/');
    }

    /// <summary>
    /// A group name as a dictionary key fragment: "Help &amp; About" becomes "help-about".
    /// Lowercase ASCII with the ampersand dropped, so the key survives a wording change to the
    /// English name only when that change is deliberate enough to also move the key.
    /// </summary>
    internal static string Slug(string name) =>
        string.Join('-', name.Replace("&", " ", StringComparison.Ordinal)
                             .ToLowerInvariant()
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Resolves a route back to its page — used by pinned, recents and deep links.</summary>
    public static NavPage? FindByRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;
        var key = Normalize(route);
        // The app's root URL renders Home, which is registered under "home".
        if (key.Length == 0) key = "home";
        return ByRoute.TryGetValue(key, out var page) ? page : null;
    }

    /// <summary>The group a route belongs to, or null when the route isn't a catalog page.</summary>
    public static string? FindGroupName(string? route)
        => FindByRoute(route)?.Category;
}
