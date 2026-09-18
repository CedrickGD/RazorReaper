using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The migration itself, checked against the source: a surface that has been translated must not
/// still carry the English it was translated out of, every key it asks for must exist, and no key
/// may sit in the dictionaries with nothing rendering it.
///
/// Half-migrated is the failure mode this guards. A literal left behind in a translated page
/// shows as English inside a Russian window and nothing fails — the app keeps running, the bug is
/// only visible to someone reading that language.
/// </summary>
public sealed class TranslatedSurfaceTests
{
    /// <summary>
    /// Per migrated file, the English that must no longer appear in it. Grows with each surface.
    /// </summary>
    public static TheoryData<string, string> MigratedLiterals()
    {
        var data = new TheoryData<string, string>();

        foreach (var literal in new[]
        {
            ">Settings</h1>",
            "Appearance, audio and app behaviour. Hotkeys live on their own page.",
            "Title=\"My account\"",
            "Your profile picture, Discord account and connected computers.",
            ">Manage account</a>",
            "<h3>Appearance</h3>",
            "The accent recolours the whole app",
            "Appearance reset to defaults.",
            "Title=\"Interface scale\"",
            "Scales every element.",
            "<h3>App behaviour</h3>",
            "How Razor Reaper behaves alongside ARK and Discord.",
            "Title=\"Discord Rich Presence\"",
            "Shows your current tool and version on your Discord profile.",
            "Title=\"Start with ARK\"",
            "Title=\"Close with ARK\"",
            "Title=\"Updates\"",
            "Updates install themselves and restart the app.",
            "<h3>Elsewhere</h3>",
            "Title=\"Global hotkeys\"",
            "Title=\"Gamma triggers\"",
            ">Reset</button>",
            ">Open</a>",
            ">Automatic</span>",
        })
        {
            data.Add("Components/Pages/Settings.razor", literal);
        }

        foreach (var literal in new[]
        {
            "<h3>Accent Color</h3>",
            "Recolor the whole app",
            ">Active</span>",
            "\"Collapse\" : \"Expand\"",
            "aria-label=\"Hue\"",
            "aria-label=\"Hex color\"",
            "title=\"Reset to default purple\"",
            "Advanced — override individual shades",
            "\"Auto\" : \"Custom\"",
            ">Reset to auto</button>",
            "Each shade left on",
            "is derived from the accent color",
            "\"light\", \"Light\"",
            "Hover / highlight accents",
            "Gradient end, pressed states",
            "Deep gradient / shadow tone",
            "Subtle tinted text",
            " hex\"",
        })
        {
            data.Add("Components/Shared/AccentColorCard.razor", literal);
        }

        foreach (var literal in new[]
        {
            "<h3>Interface Font</h3>",
            "Switch the UI font preset",
            ">Active</span>",
            "\"Collapse\" : \"Expand\"",
            "The quick brown fox",
            "Manual install (if auto-install fails)",
            "Download the font from the official link below.",
            "If you get a zip, extract it.",
            "<strong>Install</strong>",
            "Return here and select the preset again.",
            "Fonts install per-user in the background.",
            "Description = \"",
            "return \"System\"",
            "\"Installing\"",
            "\"Installed\" : \"Auto-install\"",
            "Free monthly limit reached",
            "$\"Installing {preset.Name}",
            "{preset.Name} installed.",
            "installation pending. Try the preset again",
            "Font package is invalid or blocked",
            "Font files are in use.",
            "Font download failed.",
            "Font installation failed:",
            "Copied download link for",
            "Failed to copy download link.",
        })
        {
            data.Add("Components/Shared/FontSettingsCard.razor", literal);
        }

        foreach (var literal in new[]
        {
            "left this month",
            "Free monthly quota",
        })
        {
            data.Add("Components/Shared/UsageChip.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Search</span>",
            "title=\"Search pages, locations and commands (Ctrl+K)\"",
            "title=\"Drag to resize\"",
            "Premium — open your license",
            "Freemium — open your license",
            "@group.Name\"",
            ">@entry.Label<",
            ">@_selectedGroup<",
        })
        {
            data.Add("Components/Shared/SharedNavbar.razor", literal);
        }

        foreach (var literal in new[]
        {
            "placeholder=\"Search pages, locations and commands...\"",
            "=> \"Pages\"",
            "=> \"Locations\"",
            "=> \"Commands\"",
            "new(\"Recent\"",
            "new(\"Jump to\"",
            "Title = page.Label",
            "<kbd>↑</kbd><kbd>↓</kbd> navigate",
            "? \"run\" : \"open\"",
            ">esc</kbd> close",
            "No results for",
            "of {_totalMatches}",
            "result{(_flat.Count == 1",
        })
        {
            data.Add("Components/Shared/GlobalSearch.razor", literal);
        }

        foreach (var literal in new[]
        {
            "Subtitle = \"Automation script",
            "? \"Running\" : null",
            "started.\"",
            "stopped.\"",
            "Title = \"Stop all scripts\"",
            "Halts every running automation script",
            "running\" : null",
            "No scripts were running.",
            "Title = \"Toggle crosshair overlay\"",
            "? \"On\" : null",
            "\"Crosshair on.\"",
            "Title = \"Toggle HUD overlay\"",
            "\"HUD overlay on.\"",
            "Title = \"Toggle Auto Antidote\"",
            "_antidote.State.ToString()",
            "Auto Antidote needs calibration first",
            "\"Auto Antidote watching.\"",
            "Title = \"Toggle Fed Suit run\"",
            "$\"Cycle {_fedSuit.CurrentCycle}\"",
            "\"Fed Suit stopped.\"",
            "Fed Suit couldn't start",
            "$\"Gamma: {name}\"",
            "$\"Apply gamma",
            "$\"Gamma set to {name}.\"",
            "Windows clamped the",
            "The display driver rejected the",
            "Title = \"Reset gamma to default\"",
            "Restores the system gamma ramp",
            "\"Gamma reset to default.\"",
            "Title = \"Launch ARK\"",
            "Starts the game through Steam",
            "= \"Script\"",
            "= \"Command\"",
        })
        {
            data.Add("Navigation/PaletteCommandProvider.cs", literal);
        }

        foreach (var literal in new[]
        {
            ">Lifetime only</h2>",
            "This page comes with the Lifetime licence",
            ">Buy Premium</a>",
            "Redeem key\n",
            "<h4>Premium Feature</h4>",
            "This feature requires an active Premium license.",
            "Upgrade to Premium\n",
        })
        {
            data.Add("Components/Shared/PremiumLock.razor", literal);
        }

        foreach (var literal in new[]
        {
            "This feature needs administrator rights.",
            "\"Restarting…\" : \"Restart as Administrator\"",
            "Could not restart with administrator rights.",
        })
        {
            data.Add("Components/Shared/ElevationPrompt.razor", literal);
        }

        foreach (var literal in new[]
        {
            "title=\"Pick color\"",
            ">Done</button>",
        })
        {
            data.Add("Components/Shared/ColorField.razor", literal);
        }

        foreach (var literal in new[]
        {
            "= \"Choose…\"",
        })
        {
            data.Add("Components/Shared/Dropdown.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Not set</span>",
            "= \"Change in Global Hotkeys\"",
        })
        {
            data.Add("Components/Shared/HotkeyLink.razor", literal);
        }

        foreach (var literal in new[]
        {
            "= \"Report an issue\"",
        })
        {
            data.Add("Components/Shared/SendDiagnosticsButton.razor", literal);
        }

        foreach (var literal in new[]
        {
            "title=\"Clear this hotkey\"",
            "aria-label=\"Clear this hotkey\"",
            "= \"Press a key…\"",
        })
        {
            data.Add("Components/Shared/HotkeyField.razor", literal);
        }

        foreach (var literal in new[]
        {
            "title=\"Dismiss\"",
        })
        {
            data.Add("Components/Shared/AnnouncementBanner.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Image unavailable</span>",
            ">Retry</button>",
        })
        {
            data.Add("Components/Shared/HostedImg.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Video unavailable</span>",
            ">Retry</button>",
            "$\"Loading video…",
            ": \"Loading video…\"",
        })
        {
            data.Add("Components/Shared/HostedVideo.razor", literal);
        }

        foreach (var literal in new[]
        {
            "\"Access permanently revoked\"",
            "\"Access suspended\"",
            "Your access to RazorReaper on this machine",
            ">Reason</div>",
            "Access returns automatically on",
            "\"Checking…\" : \"Re-check access\"",
            "Believe this is a mistake?",
        })
        {
            data.Add("Components/Shared/AccessBlocked.razor", literal);
        }

        foreach (var literal in new[]
        {
            "\"1 unread reply\"",
            "unread replies\"",
            "\"update ready — restart to install\"",
            "ready — restart to install\"",
            "$\"new version {label}\"",
            "$\"what's new in {label}\"",
            "\"What's new & inbox\"",
        })
        {
            data.Add("Components/Shared/NotificationIndicator.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">HUD Overlay</h1>",
            "A click-through on-screen panel",
            ">Disable</button>",
            ">Enable</button>",
            "\"Move mode: on\" : \"Move mode\"",
            "Test alert\n",
            ">Modules</h3>",
            "Toggle each panel line",
            "Title=\"@module.Title\"",
            "title=\"Move up\"",
            "title=\"Move down\"",
            ">Placement</h3>",
            "Title=\"Monitor\"",
            "Title=\"Placement\"",
            ">Custom (dragged)</button>",
            "Title=\"Alert placement\"",
            ">Free</button>",
            "Title=\"Alert position\"",
            "Title=\"Alert margin\"",
            "Title=\"Offset X\"",
            "Title=\"Offset Y\"",
            "Title=\"Opacity\"",
            "Title=\"Scale\"",
            "Title=\"Compact mode\"",
            "The overlay is click-through",
            "\"On\" : \"Off\"",
            "Overlay is off — enable it",
            "modules enabled\"",
            "move mode on\"",
            "Current time of day.",
            "On-screen stack of tool alerts.",
            "\"Primary display\"",
            "\" · primary\"",
        })
        {
            data.Add("Components/Pages/HudOverlay.razor", literal);
        }

        foreach (var literal in new[]
        {
            "\"No server set\"",
            "\"Idle\"",
            "$\"Frozen —",
            "\"None\", TextMuted",
            "\"1 active\"",
            "active\"",
            "scripts\")",
            "$\"Desync {snap.DesyncSeconds}s\"",
            "m.Title.ToUpperInvariant()",
        })
        {
            data.Add("Services/Overlay/HudOverlayWindow.cs", literal);
        }

        foreach (var literal in new[]
        {
            "\"Test alert — this is where alerts appear\"",
            "\"Test alert — success\"",
            "\"Test alert — warning\"",
            "\"Test alert — error\"",
            "HudServerInfo(\"Single Player\"",
        })
        {
            data.Add("Services/Overlay/HudOverlayService.cs", literal);
        }

        foreach (var literal in new[]
        {
            ">TP Locations</h1>",
            "Teleport-worthy spots across every map",
            ">All Maps</button>",
            "placeholder=\"Search locations...\"",
            ">All</button>",
            "locations</span>",
            ">Lat</span>",
            ">Lon</span>",
            "title=\"Copy: cheat setplayerpos",
            "<span>Copy</span>",
            "<h3>No locations found</h3>",
            "Try a different search, map, or category",
            "=> \"Obelisk\"",
            "=> \"Landmark\"",
            "teleport command copied.",
            "$\"Copied TP command:",
            "\"Failed to copy the teleport command.\"",
        })
        {
            data.Add("Components/Pages/TpLocations.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Underwater Drops</h1>",
            "Underwater loot crate locations with coordinates",
            ">Search</label>",
            "placeholder=\"Search area, map or note...\"",
            "<span>Showing:</span>",
            ">Map</span>",
            ">All maps</button>",
            ">Crate type</span>",
            ">All types</button>",
            "</span>Deep sea",
            "</span>Cave crate",
            "</span>Shipwreck",
            "Deep-sea crates need character level 80",
            "\"location\" : \"locations\"",
            "<div>Type</div>",
            "<div>Access</div>",
            "<h3>No drops found</h3>",
            "=> \"Deep sea\"",
        })
        {
            data.Add("Components/Pages/UnderwaterDrops.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Boss Tribute Guide</h1>",
            "Bosses and mini-bosses sorted by map",
            "<h3>How to use this list</h3>",
            "Expand each boss to see Gamma",
            "<span>Showing <strong>",
            ">Show all maps</a>",
            "Bosses.Count entries",
            "\"Show tier requirements\"",
            ": \"Single list\"",
        })
        {
            data.Add("Components/Pages/Bosses.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Steam Mods</h1>",
            "Browse installed ARK: Survival Evolved workshop mods",
            ">Installed Mods</span>",
            ">Named by Steam</span>",
            "\"Scanning...\" : \"Refresh Mods\"",
            ">Steam Path:</span>",
            ">Last Scan:</span>",
            "placeholder=\"Search by mod name or workshop ID\"",
            ">Sort</label>",
            ">Installed Filter</label>",
            ">Size Filter</label>",
            "Label=\"Only show mods with Steam titles\"",
            "<h3>Scan Notes</h3>",
            "Scanning Steam libraries and workshop metadata",
            "<h3>Steam not detected</h3>",
            "<h3>No installed ARK workshop mods found</h3>",
            "<h3>No matches for current filters</h3>",
            ">Drive: @",
            ">Size: @",
            ">Installed (Local)</span>",
            ">Updated (Best Source)</span>",
            ">Copy ID</button>",
            ">Open Web</button>",
            ">Open Steam</button>",
            ">Open Folder</button>",
            "\"Latest Installed\"",
            "\"Last 7 Days\"",
            "\"All Sizes\"",
            "\"Under 100 MB\"",
            "\"Steam was not detected on this machine.\"",
            "$\"Loaded {allMods.Count}",
            "$\"Copied ID {workshopId}\"",
            "\"Could not open mod folder.\"",
            "return \"Not scanned yet\"",
            "return \"Unknown\"",
            "d ago\"",
            "return \"Just now\"",
        })
        {
            data.Add("Components/Pages/SteamMods.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Global Hotkeys</h1>",
            "Every system-wide hotkey, set in one place",
            ">Set on its page</a>",
            "Placeholder=\"Press a combination\"",
            "@binding.Description",
        })
        {
            data.Add("Components/Pages/GlobalHotkeys.razor", literal);
        }

        foreach (var literal in new[]
        {
            "Group = \"Scripts\"",
            "Group = \"Overlays\"",
            "Group = \"Automation\"",
            "Description = \"Starts or stops the script.\"",
            "Description = \"Shows or hides the crosshair.\"",
            "Description = \"Starts or stops clicking.\"",
        })
        {
            data.Add("Services/Automation/HotkeyRegistry.cs", literal);
        }

        foreach (var literal in new[]
        {
            ">Credits</h1>",
            "Developer, community, and tools behind",
            ">ARK: Survival Evolved Tools Developer</span>",
            "<h3>Community & Support</h3>",
            "Describe the problem in your in-app report",
            ">Built With</h3>",
            ">Need Help?</h3>",
            "Found a bug or need assistance?",
            ">In-App Feedback</span>",
            ">Bug Reports</span>",
            "$\"Could not open {label}.",
        })
        {
            data.Add("Components/Pages/Credits.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Desync</h1>",
            "Freezes your character server-side",
            "\"Active\" : \"Inactive\"",
            "Adds a temporary Windows Firewall rule",
            ">Activate</button>",
            "Message=\"Desync needs administrator rights",
            ">until traffic is restored</div>",
            ">Stop now</button>",
            "Title=\"Duration\"",
            ">How it works</div>",
            "<li>Activate adds an outbound block rule",
            "Title=\"In-game countdown\"",
            "Title=\"Instant stop\"",
            "Use at your own risk",
        })
        {
            data.Add("Components/Pages/Desync.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Inbox</h1>",
            "Private answers from support",
            ">Report an issue</a>",
            "<p>Loading your inbox…</p>",
            "<h3>No replies yet</h3>",
            "Your replies will appear here automatically",
            "<strong>Support replied</strong>",
            ">New</span>",
            "<strong>Your report</strong>",
            ">Earlier replies</button>",
        })
        {
            data.Add("Components/Pages/SupportInbox.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Sky Changer</h1>",
            "Replace the in-game sky with an image",
        })
        {
            data.Add("Components/Pages/CustomLab/CustomLab.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Gen2 Mutagen Dino Prices</h1>",
            "Browse creature mutation values and search",
            ">Search by Name</label>",
            "placeholder=\"Search dinos...\"",
            "<span>Showing:</span>",
            ">Creature Name</div>",
            ">Mutagen Value</div>",
            "<h3>No creatures found</h3>",
            "Try adjusting your search query",
        })
        {
            data.Add("Components/Pages/DinoPrices.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">OC BPs</h1>",
            "Genesis 2 mission rewards for overcapped blueprints.</p>",
            "<h3>How to get them</h3>",
            "Run the listed Gen2 missions",
            "Entries.Count items",
            "<span>Item</span>",
            "<span>Mission</span>",
            "<span>Difficulty</span>",
            "Title = \"Armor\"",
            "Title = \"Weapons\"",
            "Title = \"Saddles\"",
            "=> \"Best rolls\"",
            "=> \"Solid rolls\"",
            "=> \"Lowest rolls\"",
        })
        {
            data.Add("Components/Pages/OcBps.razor", literal);
        }

        foreach (var literal in new[]
        {
            "= \"TP Locations\"",
            "= \"Underwater Drops\"",
            "= \"Map Mods\"",
            "= \"Bosses\"",
            "· {count} locations",
            "· {count} crates",
            "· {count} spots",
            "{map.Bosses.Count} entries",
        })
        {
            data.Add("Navigation/DeepLinkIndex.cs", literal);
        }

        foreach (var literal in new[]
        {
            "Welcome, @userName",
            "ARK: Survival Evolved Configuration & Server Management Tool",
            "<h3>Status</h3>",
            "<h3>Storage</h3>",
            "<h3>System Resources</h3>",
            "<h3>Hardware</h3>",
            ">Recent Activity</h3>",
            ">Clear All</button>",
            "<h3>Sound Settings</h3>",
            "Control UI click and notification audio.",
            "<h3>System Paths</h3>",
            ">System uptime</div>",
            ">App session</div>",
            ">Network</div>",
            ">Signal</div>",
            ">Timezone</div>",
            ">UTC offset</div>",
            ">Week</div>",
            "of @currentTime.Year",
            ">Installed Drives</span>",
            ">Drive info unavailable</div>",
            "% used</span>",
            "more drives</div>",
            ">CPU Usage</span>",
            ">GPU Usage</span>",
            ">RAM Usage</span>",
            ">Total Memory:</span>",
            ">App Usage:</span>",
            "Initializing system monitoring",
            ">Motherboard</div>",
            ">No recent activity</p>",
            ">Enable UI sounds</CheckBox>",
            "\"Collapse\" : \"Expand\"",
            ">Notification Sound</span>",
            ">Click Sound</span>",
            ">Enabled</CheckBox>",
            ">Current</span>",
            ">Upload</label>",
            ">Reset</button>",
            ">Test</button>",
            ">Custom sounds folder</div>",
            ">Open Folder</button>",
            "Supports MP3, WAV, OGG, M4A, AAC",
            "Change ARK Path",
            "Set a custom ARK installation path",
            "Paste the ARK install folder",
            "Save Path",
            "Auto-Detect",
            ">ARK Installation</div>",
            ">Config Directory</div>",
            "\"Not found\"",
            "= \"Not Connected\"",
            "= \"N/A\"",
            "= \"Unknown\"",
            "\"Default (Notify)\"",
            "\"Default (Global Click)\"",
            "\"Dashboard loaded\"",
            "$\"Opened folder:",
            "$\"ARK path set to:",
            "\"ARK path reset to auto-detect\"",
            "\"Custom sound folder not found.\"",
            "Unsupported audio format.",
            "$\"Sound file too large.",
            "\"Could not read the sound file.\"",
            "\"Notification sound updated\"",
            "\"Click sound updated\"",
            "\"Path does not exist\"",
            "$\"Failed to open folder:",
            "Please enter a valid ARK installation path",
            "The specified directory does not exist",
            "This doesn't appear to be a valid ARK installation",
            "$\"Detected a subfolder.",
            "\"Custom ARK path saved successfully\"",
            "$\"Error saving path:",
            "\"Reset to auto-detect mode\"",
            "$\"Error resetting path:",
        })
        {
            data.Add("Components/Pages/Home.razor", literal);
        }

        foreach (var literal in new[]
        {
            "return \"Just now\";",
            "m ago\"",
            "h ago\"",
            "d ago\"",
        })
        {
            data.Add("Models/ActivityItem.cs", literal);
        }

        foreach (var literal in new[]
        {
            "Page not found",
            "Back to Home",
            "This link points at a page RazorReaper no longer has.",
        })
        {
            data.Add("Components/Pages/NotFound.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">Feedback & Support</h1>",
            "Share an idea or an opinion, or report a problem",
            "A system and feature snapshot is attached when you send your report.",
            "Report ID: @_reportId",
            "\"What went wrong? (required)\"",
            "\"Your feedback (required)\"",
            ">Contact (optional)</label>",
            "Discord or email — an additional way to reach you",
            "\"Send Report\"",
            "\"Send Feedback\"",
            ">Attached automatically</h3>",
            ">Good to know</h3>",
            "<li>Machine name</li>",
            "Answers appear privately in your Inbox.",
        })
        {
            data.Add("Components/Pages/Feedback.razor", literal);
        }

        foreach (var literal in new[]
        {
            ">My account</h1>",
            "Your profile, Discord and RazorReaper installations, together.",
            ">Loading your account…</p>",
            ">Your profile</h3>",
            ">Display name</label>",
            "\"Save profile\"",
            ">Connected accounts</h3>",
            ">Your installations</h3>",
            ">View license</button>",
            ">Redeem key</button>",
            ">Buy Premium</a>",
            ">Sign out</button>",
            "One profile.<br />Every installation.",
            ">Create account with Discord</button>",
            ">Cancel</button>",
            "\"Connect your account\"",
            "\"Is this you?\"",
            "Waiting for Discord authorization",
            "RazorReaper will connect to your Discord identity.",
            "\"Continue with Discord\"",
            "The connection timed out. Please try again.",
            "Your profile has been saved.",
        })
        {
            data.Add("Components/Pages/Account.razor", literal);
        }

        foreach (var literal in new[]
        {
            "aria-label=\"Close\"",
            "title=\"Close (Esc)\"",
            "\"Premium is active\"",
            "\"Unlock Premium\"",
            "<li>Every feature is unlocked on this machine.</li>",
            "\"Included with Premium\"",
            "(\"Auto Clicker\", ",
            "\"Buy / renew\"",
            ">Esc closes this view<",
            ">Your license key<",
            "\"Reveal\"",
            ">Copy</button>",
            "<dd>Bound to this PC</dd>",
            "<dt>Time left</dt>",
            "Your license has expired.",
            ">Renew</a>",
            "\"Verifying…\"",
            "The key is in your order confirmation.",
            "Please enter a license key.",
            "<dd>Not activated</dd>",
            ">Need help?<",
            "License key copied to clipboard.",
            "\"3 Months\"",
            "\"Expired\"",
            "} months\"",
        })
        {
            data.Add("Components/Shared/LicenseOverlay.razor", literal);
        }

        foreach (var literal in new[]
        {
            "aria-label=\"Close\"",
            "title=\"Close (Esc)\"",
            ">Release notes</span>",
            "What's new in v",
            "<strong>Update failed</strong>",
            "is available.\")",
            ">Restarting…</button>",
            "Restart & update to v",
            "\"Downloading…",
            "\"Check again\"",
            ">Full changelog</a>",
            "Release notes appear once the update check has run.",
            "You're up to date",
            "No notes were published for this release.",
            ">Open inbox</a>",
            "Loading your inbox…",
            "No replies yet. Answers to your reports land here.",
            "\"1 unread\"",
            "Support replied · @reply.ReportId",
            ">New</span>",
            "\"just now\"",
        })
        {
            data.Add("Components/Shared/WhatsNewOverlay.razor", literal);
        }

        foreach (var literal in new[]
        {
            "Close ARK (or stop the running macro) first",
            "\"Checking for updates...\"",
            "You're on the latest version.",
            "is ready — restart to install.",
            "is ready — close ARK, then restart to install.",
            "— restarting...\"",
            "it will be applied at the next start.",
            "Update available but download URL is missing.",
            "install it manually.",
            "\"Downloading update...\"",
            // Only the status line; the log template two lines above stays English, as logs do.
            "Update download was incomplete — it will be retried.",
            "\"Download cancelled.\"",
            "\"Failed to download update.\"",
            "could not be installed (installer exit code",
            "Razor Reaper will restart.\"",
        })
        {
            data.Add("Services/Implementations/AutoUpdateManager.cs", literal);
        }

        foreach (var literal in new[]
        {
            ">Stretched Res</h1>",
            "Switch the desktop to a stretched resolution",
            "Keep this resolution?</div>",
            "automatically if you don't confirm",
            "\"the new mode\"",
            "\"the previous mode\"",
            "-unit\">s</span>",
            ">Keep resolution</button>",
            ">Revert now</button>",
            "Restore native",
            ">Current</div>",
            ">Native</div>",
            "The panel's full resolution",
            ">Graphics</div>",
            "? \"Unknown\" :",
            "GPU scaling supported",
            "Set your GPU to full-screen scaling",
            "\"Awaiting confirmation\"",
            "\"Stretched resolution active\"",
            "\"Native resolution\"",
            "Desktop is at",
            "Could not read the current desktop resolution.",
            "<h3>Stretched presets",
            "Each applies the desktop resolution temporarily",
            "· stretched",
            "· native",
            "\"Not detected\"",
            ">Width</span>",
            ">Height</span>",
            ">Apply custom</button>",
            "If the screen goes black or unreadable",
            "<h3>Make the GPU stretch the image</h3>",
            "A stretched resolution only fills the screen",
            "Your NVIDIA GPU handles this",
            "<strong>NVIDIA Control Panel</strong>",
            "Adjust desktop size and position",
            "Perform scaling on",
            "<strong>GPU Scaling</strong>",
            "Maintain Display Scaling",
            "<h3>ARK game resolution</h3>",
            "Title=\"Write resolution to GameUserSettings.ini\"",
            "sets ARK's ResolutionSizeX/Y",
            "\"Close ARK first\"",
            "\"Choose a resolution first\"",
            "$\"Write {lastChosenWidth}",
            "Close ARK before writing",
            "Free monthly limit reached",
            "\"Could not apply that resolution.\"",
            "\"Could not write ARK's resolution.\"",
            "ARK write failed:",
        })
        {
            data.Add("Components/Pages/StretchedRes.razor", literal);
        }

        return data;
    }

    /// <summary>
    /// The English these surfaces used to spell out, pinned where it moved to. Layout tests used
    /// to assert this wording against the razor files; it is a dictionary entry now, and it still
    /// may not drift by accident.
    /// </summary>
    [Theory]
    [InlineData("settings.title", "Settings")]
    [InlineData("settings.accent.title", "Accent Color")]
    [InlineData("settings.accent.description", "Recolor the whole app. Pick any color and every purple accent updates instantly — your choice is saved until you change it.")]
    [InlineData("settings.accent.advanced.toggle", "Advanced — override individual shades")]
    [InlineData("settings.accent.shade.reset", "Reset to auto")]
    [InlineData("settings.font.title", "Interface Font")]
    [InlineData("settings.font.manual.title", "Manual install (if auto-install fails)")]
    [InlineData("settings.font.status.autoinstall", "Auto-install")]
    [InlineData("usage.remaining", "{0}/{1} left this month")]
    [InlineData("common.active", "Active")]
    [InlineData("settings.updates.description", "Updates install themselves and restart the app. There is no opt-out.")]
    [InlineData("notfound.title", "Page not found")]
    [InlineData("notfound.back", "Back to Home")]
    [InlineData("nav.page.feedback", "Feedback & Support")]
    [InlineData("palette.placeholder", "Search pages, locations and commands...")]
    [InlineData("palette.section.jumpto", "Jump to")]
    [InlineData("nav.page.home.description", "Dashboard, updates & recent activity")]
    [InlineData("palette.category.command", "Command")]
    [InlineData("palette.cmd.launch.title", "Launch ARK")]
    [InlineData("palette.deeplink.parent", "{0} · {1}")]
    [InlineData("gate.lifetime.title", "Lifetime only")]
    [InlineData("gate.premium.title", "Premium Feature")]
    [InlineData("account.redeemkey", "Redeem key")]
    [InlineData("elevation.restart", "Restart as Administrator")]
    [InlineData("hotkey.changelink", "Change in Global Hotkeys")]
    [InlineData("access.heading.ban", "Access permanently revoked")]
    [InlineData("notify.update.ready", "update ready — restart to install")]
    [InlineData("notify.tooltip.idle", "What's new & inbox")]
    [InlineData("license.buy.renew", "Buy / renew")]
    [InlineData("license.buy.premium", "Buy Premium")]
    [InlineData("license.fact.device.bound", "Bound to this PC")]
    [InlineData("whatsnew.title", "What's new in v{0}")]
    [InlineData("whatsnew.uptodate", "You're up to date — v{0}.")]
    [InlineData("whatsnew.restartupdate", "Restart & update to v{0}")]
    [InlineData("whatsnew.checkagain", "Check again")]
    [InlineData("whatsnew.failed", "Update failed")]
    [InlineData("whatsnew.downloading.percent", "Downloading… {0}%")]
    [InlineData("update.gated", "Close ARK (or stop the running macro) first, then restart to update.")]
    [InlineData("update.status.ready", "Update v{0} is ready — restart to install.")]
    [InlineData("update.install.failed.retry", "Update to v{0} could not be installed (installer exit code {1}). Restart & update to try again.")]
    [InlineData("stretchedres.title", "Stretched Res")]
    [InlineData("stretchedres.monitor.label", "Monitor")]
    [InlineData("stretchedres.monitor.option", "Monitor {0} — {1}×{2}")]
    [InlineData("stretchedres.monitor.option.primary", "Monitor {0} — {1}×{2} (primary)")]
    [InlineData("stretchedres.monitor.fallback", "The monitor you last used is not connected — {0} is selected instead.")]
    [InlineData("stretchedres.confirm.title", "Keep this resolution?")]
    [InlineData("stretchedres.custom.title", "Custom resolution")]
    [InlineData("stretchedres.ark.title", "ARK game resolution")]
    public void TheEnglishWordingIsWhatItWas(string key, string expected)
    {
        Assert.True(TranslationParityTests.Read("en").TryGetValue(key, out var english), $"missing {key}");
        Assert.Equal(expected, english);
    }

    /// <summary>
    /// Two sentences on Settings put one emphasised word in the middle of themselves, and no
    /// dictionary value in the app carries markup — so each is three keys: a lead, the word the
    /// razor file wraps in &lt;strong&gt;, and the tail. The spacing around the word lives in the
    /// lead and the tail, because Chinese wants none where English wants one. That makes a
    /// trimmed value a real bug and an invisible one, so it is pinned here: these are the only
    /// entries in the dictionaries whose leading or trailing space is load-bearing.
    /// </summary>
    [Theory]
    [InlineData("settings.accent.advanced.note.lead", "settings.accent.shade.auto", "settings.accent.advanced.note.rest",
        "Each shade left on Auto is derived from the accent color. Pin a shade to lock it; \"Reset to auto\" hands it back to the accent.")]
    [InlineData("settings.font.manual.install.lead", "settings.font.manual.install.action", "settings.font.manual.install.rest",
        "Right-click the .ttf file and choose Install (or double-click and install).")]
    public void ASentenceSplitAroundAnEmphasisedWordStillReadsAsOneSentence(
        string lead, string word, string rest, string english)
    {
        foreach (var code in new[] { "en", "de", "ru", "zh-Hans" })
        {
            var dictionary = TranslationParityTests.Read(code);
            var joined = dictionary[lead] + dictionary[word] + dictionary[rest];

            Assert.DoesNotContain("  ", joined, StringComparison.Ordinal);
            Assert.DoesNotContain(" ,", joined, StringComparison.Ordinal);
            Assert.Equal(joined.Trim(), joined);
        }

        var en = TranslationParityTests.Read("en");
        Assert.Equal(english, en[lead] + en[word] + en[rest]);
    }

    [Theory]
    [MemberData(nameof(MigratedLiterals))]
    public void AMigratedSurfaceKeepsNoEnglishLiteral(string relativePath, string literal)
    {
        var source = WithoutComments(File.ReadAllText(AppFile(relativePath)));

        Assert.DoesNotContain(literal, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drops razor <c>@* … *@</c> blocks and whole-line C# comments. A comment quoting the
    /// wording it explains — "the primary action becomes 'Restart &amp; update to vX'" — is not a
    /// string the app renders, and making the migration delete those comments would cost the
    /// reader the explanation to satisfy a grep. Only whole comment lines go, never a trailing
    /// <c>//</c>, so a literal cannot hide behind a URL on the same line.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        return string.Join('\n', withoutBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void EveryKeyTheAppAsksForExists()
    {
        var english = TranslationParityTests.Read("en");

        var unknown = UsedKeys().Concat(GeneratedKeys())
            .Where(used => !english.ContainsKey(used.Key))
            .Select(used => $"{used.File}: {used.Key}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unknown.Length == 0, $"keys with no English entry: {string.Join(", ", unknown)}");
    }

    /// <summary>A key nothing renders is a key four people keep translating for nothing.</summary>
    [Fact]
    public void NoEnglishKeyIsUnused()
    {
        var used = UsedKeys().Concat(GeneratedKeys()).Select(u => u.Key).ToHashSet(StringComparer.Ordinal);

        var dead = TranslationParityTests.Read("en").Keys
            .Where(key => !used.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(dead.Length == 0, $"keys nothing renders: {string.Join(", ", dead)}");
    }

    /// <summary>
    /// Keys nothing spells out: the sidebar asks for <c>nav.page.*</c> and <c>nav.group.*</c>
    /// through properties the catalog derives from the route and the group name, so they are
    /// read off the real catalog rather than grepped for.
    /// </summary>
    private static IEnumerable<(string File, string Key)> GeneratedKeys()
    {
        foreach (var group in RazorReaper.Navigation.NavCatalog.Groups)
        {
            yield return ("NavCatalog.cs", group.NameKey);
        }

        foreach (var page in RazorReaper.Navigation.NavCatalog.Pages)
        {
            yield return ("NavCatalog.cs", page.LabelKey);
            yield return ("NavCatalog.cs", page.DescriptionKey);
        }

        // The HUD's module titles and placements, the same way: derived from the enum, so the
        // page and the overlay window cannot ask for a key the other one does not know.
        foreach (var kind in Enum.GetValues<RazorReaper.Services.Overlay.HudModuleKind>())
        {
            yield return ("HudModels.cs", RazorReaper.Services.Overlay.HudSettings.TitleKey(kind));
        }

        foreach (var anchor in Enum.GetValues<RazorReaper.Services.Overlay.HudAnchor>())
        {
            yield return ("HudModels.cs", RazorReaper.Services.Overlay.HudSettings.AnchorKey(anchor));
        }

        // The hotkeys page renders its labels off the binding, so a grep for T("…") cannot
        // see them. The registry publishes the set instead.
        foreach (var key in RazorReaper.Services.Automation.HotkeyRegistry.Keys)
        {
            yield return ("HotkeyRegistry.cs", key);
        }

        // The Lifetime guide numbers its steps rather than naming them: a key named after a
        // step would put the method in the markup above the paywall.
        for (var step = 1; step <= RazorReaper.Components.Pages.DinoLevelGuide.StepCount; step++)
        {
            yield return ("DinoLevelGuide.razor", $"dinolevel.step.{step}.head");
            yield return ("DinoLevelGuide.razor", $"dinolevel.step.{step}.detail");
        }
    }

    /// <summary>
    /// Every key the app spells out, with the file that spells it. Three call shapes: the ordinary
    /// <c>Localizer.T("…")</c>, and the update manager's two, which hold a key and its arguments
    /// so a status line that sits on screen for a session can be re-read after a switch.
    ///
    /// Which key a call asks for is not always its first token. <c>T(on ? "a.on" : "a.off")</c> is
    /// the shape a toggle's two messages take all over the app, and reading only the literal that
    /// directly follows the bracket declared both of them dead. So the whole argument list is
    /// read, and every key-shaped literal in it counts as asked for.
    /// </summary>
    private static IEnumerable<(string File, string Key)> UsedKeys()
    {
        var root = Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper");

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(IsProjectSource))
        {
            var source = File.ReadAllText(path);

            foreach (var key in KeysAskedFor(source))
            {
                yield return (Path.GetFileName(path), key);
            }
        }
    }

    /// <summary>"Localizer.T(" must match, so only a word character in front rules a T( call out.</summary>
    private static readonly Regex CallSite = new(@"(?:(?<!\w)T|SetStatus|new StatusLine)\(");

    /// <summary>
    /// A dictionary key as it is written: lowercase, dotted, hyphens inside a segment. Tight
    /// enough that an ordinary argument — a name, a path, a number — is not mistaken for one, so
    /// a key that really is dead still shows up as dead.
    /// </summary>
    private static readonly Regex KeyShaped = new(@"^[a-z][a-z0-9]*(?:\.[a-z0-9\-]+)+$");

    /// <summary>
    /// A key handed to something that will resolve it later rather than resolved on the spot:
    /// <c>TitleKey = "ocbps.category.armor"</c> on a record the page renders through
    /// <c>T(category.TitleKey)</c>.
    /// </summary>
    /// <remarks>
    /// The names are listed rather than matched on the "Key" suffix alone, because the app is
    /// full of <c>PreferenceKey</c>s: "rr.ui.language" is key-shaped, is assigned to something
    /// ending in Key, and is a Preferences address rather than a translation.
    /// </remarks>
    private static readonly Regex KeyField =
        new(@"\b(?:Title|Label|Description|Name|Group|Text|Hint|Note|Subtitle)Key\s*[:=]\s*""(?<key>[^""]*)""");

    private static IEnumerable<string> KeysAskedFor(string source)
    {
        foreach (Match call in CallSite.Matches(source))
        {
            foreach (var literal in ArgumentLiterals(source, call.Index + call.Length))
            {
                if (KeyShaped.IsMatch(literal)) yield return literal;
            }
        }

        foreach (Match field in KeyField.Matches(source))
        {
            var key = field.Groups["key"].Value;
            if (KeyShaped.IsMatch(key)) yield return key;
        }
    }

    /// <summary>
    /// The string literals in one argument list, from just after its opening bracket to the
    /// matching close. Nested brackets are followed so a call inside a call does not end it
    /// early, and brackets inside a string or a char literal are stepped over rather than
    /// counted — <c>Split('(')</c> in an argument would otherwise leave the list open and run
    /// this off the end of the file.
    /// </summary>
    private static IEnumerable<string> ArgumentLiterals(string source, int start)
    {
        var depth = 1;

        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '"')
            {
                // A verbatim string is not used for a key anywhere, so only the ordinary
                // escape has to be stepped over.
                var end = i + 1;
                while (end < source.Length && source[end] != '"') end += source[end] == '\\' ? 2 : 1;
                if (end >= source.Length) yield break;

                yield return source[(i + 1)..end];
                i = end;
            }
            else if (c == '\'')
            {
                var end = i + 1;
                while (end < source.Length && source[end] != '\'') end += source[end] == '\\' ? 2 : 1;
                if (end >= source.Length) yield break;

                i = end;
            }
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) yield break;
        }
    }

    private static bool IsProjectSource(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".cs" or ".razor")) return false;

        // bin/obj carry generated copies of the razor files; scanning them double-counts.
        return !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string AppFile(string relativePath)
        => Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
}
