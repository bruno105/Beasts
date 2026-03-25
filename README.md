# Beasts — PoEHelper Plugin

A [PoEHelper / ExileCore](https://github.com/exApiTools/ExileCore) plugin for Path of Exile that tracks, prices and automatically processes captured beasts in the Bestiary.

---

## Features

### Beast Tracking
- Highlights rare (red) beasts on the **world map** and in **3D world space** with their poe.ninja price
- Tracks **yellow beasts** (generic capturable monsters not in the named database) separately
- Color-coded by beast family: Vivid (yellow), Wild (pink), Primal (cyan), Black (white)
- Exposes `Beasts.IsAllowedBeastNearby(int range)` via Plugin Bridge for other plugins to query

### Bestiary Panel Overlay
- Overlays **price badges** on every captured beast slot in the Bestiary UI
- Prices fetched automatically from **poe.ninja** (configurable refresh interval)
- Shows prices on captured beast items in **inventory** and **stash**
- Optional highlight frame on selected (tracked) beasts

### Automation
- Automatically **itemizes** beasts worth ≥ *X chaos* and **releases** the rest
- Configurable **Itemize Yellow Beasts** toggle (independent of chaos threshold)
- Stops automatically when **inventory is full** or when the **Bestiary panel is closed**
- Only acts on beasts visible in the current scroll viewport — never clicks off-screen slots
- Waits for server confirmation before the next action (no double-clicks)
- Hotkey to toggle automation on/off without opening settings
- **Status badge** in the Tracker window: "● Automation in progress..." while running, "✓ Automation completed" (with fade-out) after it stops

### Human-like Input
- **Perlin noise** position jitter — click positions vary smoothly, avoiding pixel-perfect patterns
- **Min/max random delay** between actions (configurable range)
- **Pre-click settle delay** — simulates reaction time between cursor arriving and clicking
- Mouse travels directly from last button to next button, no intermediate moves

---

## Installation

1. Place the `Beasts` folder inside `PoEHelper/Plugins/Source/`
2. Launch the HUD — ExileCore compiles and loads the plugin automatically

### Building for IDE (optional)

```powershell
$env:exapiPackage = "C:\path\to\PoEHelper"
dotnet build
```

---

## Settings

### General
| Setting | Description |
|---|---|
| Show Beast Prices On Large Map | Draw price labels on the large map for tracked beasts |
| Show Bestiary Panel | Draw name/price overlay on the Bestiary UI panel |
| Show All Prices In Bestiary Panel | Price badge on every captured beast slot |
| Show Captured Beasts In Inventory | Highlight itemised beasts in player inventory |
| Show Captured Beasts In Stash | Highlight itemised beasts in stash |
| Auto Refresh Prices | Fetch poe.ninja prices on a timer |
| Price Refresh Minutes | How often to auto-refresh (default: 15 min) |

### Automation
| Setting | Description |
|---|---|
| Enable | Toggle the automation loop on/off |
| Hotkey | Keybind to toggle automation without opening settings |
| Itemize Above Chaos | Integer threshold (default: 4c) — beasts at or above are itemized; below are released |
| Itemize Yellow Beasts | When ON, itemize all yellow beasts regardless of price |
| Check Inventory Before Itemize | Stop automation when inventory is full |
| Action Delay Ms | Fixed delay (ms) between consecutive itemize/release actions (default: 300 ms) |

### Automation → Delays
| Setting | Description |
|---|---|
| Min/Max Pre-Click Delay Ms | Random delay between cursor arriving and clicking |
| Click Jitter | Pixel spread applied to click position within the button |

### Beast Picker
The settings panel includes a sortable table of all known beasts with their current poe.ninja price, craft descriptions, and an enable checkbox to mark which beasts should be highlighted in-world.

---

## Credits

- Original plugin by [danio0106](https://github.com/danio0106/Beasts)
- Human-like input patterns inspired by [WheresMyCraftAt](https://github.com/exApiTools/WheresMyCraftAt)
- Prices provided by [poe.ninja](https://poe.ninja)
