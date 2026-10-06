# TermCity developer guide

The technical companion to the [README](../README.md), which covers how to install and play. This document covers how the game is put together, the numbers behind it, and how to change it.

Contents

- [Build, run and test](#build-run-and-test)
- [Architecture](#architecture)
- [The simulation](#the-simulation)
- [The map and its layers](#the-map-and-its-layers)
- [Map generation](#map-generation)
- [Rendering](#rendering)
- [The session: cursor, camera, selection, zoom](#the-session-cursor-camera-selection-zoom)
- [The terminal front end](#the-terminal-front-end)
- [Persistence](#persistence)
- [Configuration reference](#configuration-reference)
- [Extending the game](#extending-the-game)
- [Tests](#tests)
- [Diagnostics and performance](#diagnostics-and-performance)
- [Design decisions](#design-decisions)
- [Color palette](#color-palette)

## Build, run and test

Requires the .NET 10 SDK. The solution file is `TermCity.slnx`.

```
dotnet build                                        # build everything
dotnet run --project src/TermCity.App                 # random map
dotnet run --project src/TermCity.App -- --seed 42     # reproducible map
dotnet run --project src/TermCity.App -- --dump-map    # print the map as text and exit (no terminal needed)
dotnet test                                         # run all tests
dotnet publish src/TermCity.App -c Release -o out     # framework-dependent build
```

The published executable is `out/termcity` on Linux and macOS, or `out\termcity.exe` on Windows. Use backslashes in project paths on Windows.

Command-line options are parsed in [Program.cs](../src/TermCity.App/Program.cs) and are documented in the README. `--dump-map` prints a header with the seed and map dimensions, followed by the glyphs rendered through `CellRenderer`, which makes it a quick way to eyeball a generator change.

Both projects target `net10.0` with nullable reference types and implicit usings on. The assembly name of the app is `termcity`. `TieredPGO` and concurrent GC are switched off in the app project: steady frame times matter more than peak throughput here.

## Architecture

```
src/TermCity.Core     Game logic. No UI dependency. Everything here is unit-testable.
  Terrain/              TerrainType, TerrainRegistry, hill and water (sea, river, lake) generators
  Features/             FeatureType (trees, rocks), FeatureRegistry, scatter generator
  Buildings/            BuildingType, BuildingRegistry
  Roads/                RoadType, RoadRegistry (street, avenue, highway)
  World/                GameMap (layers), Zones and Household, MapGenerator, HighwayGenerator
  Simulation/           CityGame (clock, economy, growth), GrowthDiagnostics, RoadNetwork, CityStats and Demand, GameConfig, MapSize
  Persistence/          SaveGameStore: JSON saves
  Rendering/            CellRenderer (layers to glyph and colors), CellInspector, BlockSampler
  Session/              GameSession and SessionFeatures (camera, actions, previews, undo, saves, prompts, feedback), EdgeScroller
  Registry/             TypeRegistry<T> and RegisteredType
  Util/                 Pos, CellRect, Rgb, GameRandom, PerlinNoise, CellHash
src/TermCity.App      Terminal.Gui v2 front end: window, views, menu, input handling
tests/TermCity.Tests  xUnit: unit tests plus headless UI tests that inject keys and mouse input
```

The dependency rule is one way: `App` depends on `Core`; `Core` knows nothing about Terminal.Gui. `GameSession` is the seam. It holds everything about the player's view that is not simulation state, and it raises events the UI listens to. Both `Core` and `App` expose their `internal` members to the test project through `InternalsVisibleTo`.

The [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) package (2.5.0) is the app's only direct NuGet dependency; it brings its own transitive dependencies.

## The simulation

`CityGame` ([CityGame.cs](../src/TermCity.Core/Simulation/CityGame.cs)) is the whole simulation: money, calendar, player actions and weekly growth. It has no knowledge of any user interface.

### Time

Drive it two ways:

- `Update(elapsedSeconds)` advances in real time. It does nothing while `Paused`. Each call is capped at **0.5 s** (`MaxSecondsPerUpdate`), so a stalled window does not fast-forward the game when it returns.
- `AdvanceWeek()` runs the rest of the current week deterministically (a whole week when called at the start of one). The tests use this.

A week is `DaysPerWeek` (7) days. `Update` accumulates seconds and calls `AdvanceDay()` each time a day's worth has passed (`SecondsPerWeek(speed) / DaysPerWeek`). There are three speeds, defined by `GameConfig.SlowSecondsPerWeek` (8.4), `MediumSecondsPerWeek` (4.2) and `FastSecondsPerWeek` (1.4). A year is `WeeksPerYear` (52) weeks. `CityGame` defaults to medium speed; the front end starts new cities paused with a guide.

### The weekly plan

Growth is decided on the first day of a week (`PlanWeek`) from the size of the city at that moment, and spread over the days with `Share`, so the shares of the seven days add up to the plan exactly.

```
filledR   = occupied residential cells, including those awaiting removal
unlocked  = filledR >= MinResidentialCells                          (10)
allowedC  = unlocked ? ceil(filledR / ResidentialPerCommercial) : 0 (20)
allowedI  = unlocked ? ceil(filledR / ResidentialPerIndustrial) : 0 (10)

weeklyCap(base, existing) = base + ceil(existing * GrowthRatePerWeek)   (rate 0.02)

planHomes     = weeklyCap(MaxNewResidentialPerWeek = 3, filledR)
planShops     = weeklyCap(MaxNewCommercialPerWeek  = 1, allowedC)
planFactories = weeklyCap(MaxNewIndustrialPerWeek  = 1, allowedI)
```

Each day, `AdvanceDay` fills that day's share: residential first, then commercial limited to `allowedC - occupiedC`, then industrial limited to `allowedI - occupiedI`. Occupied counts include dezoned buildings awaiting removal. It recomputes stats between steps so the later zones see the new homes. On the last day it collects tax, adds it to `Money`, advances `Week`, resets `Day` to 0 and records a `WeekReport`.

`Grow(zone, limit, room)` builds a candidate list of cells in that zone that are empty and served by the road network, **sorts it** (the zone index has no meaningful order, and sorting keeps a seed reproducible, including after a save is loaded and the index is rebuilt) and then picks randomly using the game's `GameRandom`. Residential cells get a household generated by `Household.Random`.

### Households

`Household(Adults, Children, Seniors)` is stored per residential cell. The weights in [Zones.cs](../src/TermCity.Core/World/Zones.cs) are tuned to average 2.0 adults and 2.0 children per home, with about one home in three having seniors:

| | Weights (index = count) |
|---|---|
| Adults | `[0, 20, 60, 20]` for 0 to 3 |
| Children | `[15, 20, 30, 20, 15]` for 0 to 4 |
| Seniors | `[65, 15, 20]` for 0 to 2 |

### Economy

Tax per week is `sum over occupied cells of WeeklyValue(zone) x rate(zone)`, including dezoned buildings awaiting removal at their original zone's rate, rounded to the nearest whole dollar, with midpoint ties rounded away from zero. `WeeklyValue` is $200 / $350 / $500 for residential / commercial / industrial. `TaxRates` stores a rate per zone type, initialized from `GameConfig.DefaultTaxRate` (0.05), and is saved with the game. The gameplay interface does not expose tax-rate controls.

Player actions (`BuildRoad`, `PlaceBuilding`, `Designate`, `Dezone`, `Demolish`) return an `ActionResult`. Spending goes through `CheckSpend`, which fails the whole action when money is `<= 0`, when there are no valid cells, or when the quote exceeds money. Nothing is partially built. `QuoteRoad` and `QuoteBuilding` return a `Quote(Cells, Cost, Skipped)` without changing anything. The context menu uses them to show prices.

Road cost per cell is:

```
round(RoadCostPerCell x max(0, type.CostMultiplier - existingRoadMultiplier) x terrain.BuildCostModifier)
```

With the defaults (street 1.0, avenue 1.8, highway 3.0, hill 1.5), an avenue on open ground is $900, a street on a hill is $750, and a street upgraded to a highway costs $1,000. `CanPlaceRoad` allows open buildable ground, or a cell holding a road of lower `Rank`. Placing a road or zone clears the feature (tree or rock) on that cell for free. `Demolish` and `Designate` are free.

**Dezoning.** `Dezone` (also `Designate(..., ZoneType.None)`) removes designations immediately, preserving roads, features and occupied buildings. Each occupied cell enters the sparse `GameMap.ZoneRemovals` dictionary with its original zone and an absolute `RemoveAtDay`, chosen with the simulation RNG as `ElapsedDays + Next(2 * DaysPerWeek, 3 * DaysPerWeek + 1)`. `AdvanceDay` processes completed-day deadlines before taxes; real-time `Update` also processes fractional-day deadlines after normalizing day progress. Expiration clears only the building and household, invalidates statistics/map rendering and leaves the terrain and features alone. Paused updates do not advance deadlines. `Designate` permits restoring a pending building's original zone, which cancels removal; a different occupied zone remains blocked. `SetBuilding` and `ClearCell` cancel stale deadlines. These deadlines are included in saves, autosaves and undo snapshots.

### Road network and service

`RoadNetwork.Compute(map, serviceReach)` ([RoadNetwork.cs](../src/TermCity.Core/Simulation/RoadNetwork.cs)):

1. **Connected roads.** A breadth-first search over 4-neighbor road cells, seeded from every road cell on the map border. Roads that this does not reach are not connected to the outside world.
2. **Served cells.** The connected cells are served, and service spreads outward one ring (4-neighbor) per step for `RoadServiceReach` (2) steps across cells whose terrain is `Buildable`. Water therefore blocks service, and the shape is a diamond.

It only visits roads and their surroundings, so it does not scale with map size. The result is cached and only recomputed when the road version changes (`Network`).

### Statistics and demand

`CityStats` ([CityStats.cs](../src/TermCity.Core/Simulation/CityStats.cs)) is computed from the sparse zone indexes and removal queue (not the whole map) and cached until the `_version` counter changes. It carries population, the adult/child/senior split, households, a `ZoneCount(Zoned, Filled, Served, AwaitingRemoval)` per zone, road counts and weekly income. `Filled` counts only designated cells; `Occupied = Filled + AwaitingRemoval` includes retained buildings. Pending buildings retain residents and tax contributions and count toward growth/unlock capacity until removal; they do not count as zoned land or vacancies. The zone panel shows them separately as `+N leaving`, and the inspector shows the remaining game days and original zone to restore.

`GrowthDiagnostics` ([GrowthDiagnostics.cs](../src/TermCity.Core/Simulation/GrowthDiagnostics.cs)) provides read-only per-cell and per-zone explanations. Results include a structural status, actionable text, the number of road-served vacancies, and a separate pause flag. Statuses distinguish no vacancies, no road access, the residential unlock requirement, capacity limits, missing registered growth buildings and readiness. `CityGame.SupportedCells` is shared with actual growth, so thresholds match. Demand is not an eligibility gate. Diagnostics do not advance time or consume random numbers; ready cells still compete for the daily allocation.

`Demand.Compute` returns 0 to 1 per zone for the side panel:

```
neededForCommercial = commercialZoned == 0 ? 0 : ResidentialPerCommercial x (commercialZoned - 1) + 1
neededForIndustrial = industrialZoned == 0 ? 0 : ResidentialPerIndustrial x (industrialZoned - 1) + 1
neededR             = max(MinResidentialCells, neededForCommercial, neededForIndustrial)

residential = clamp((neededR - zonedR) / neededR, 0, 1)
              and at least 0.6 once every zoned home is filled
commercial  = 0 until filledR >= MinResidentialCells,
              then clamp((allowedC - zonedC) / allowedC, 0, 1)
industrial  = the same with allowedI
```

### Change notification

`CityGame.Changed` fires whenever anything the UI shows may have changed. Three counters keep work down: `_version` (stats are stale), `_roadVersion` (the road network is stale) and the public `MapVersion` (what the map looks like may have changed; it does **not** move on every clock tick). A day on which nobody moved in raises no event at all, because the week bar has its own cheap redraw. `Touch()` invalidates everything and is for tests and for code that edits the map directly.

## The map and its layers

`GameMap` ([GameMap.cs](../src/TermCity.Core/World/GameMap.cs)) is a stack of flat arrays indexed by `y * Width + x`:

| Layer | Type | Notes |
|---|---|---|
| Terrain | `byte` id | The first registered terrain (grass) fills the map before generators run |
| Feature | `byte` id | 0 = none |
| Road | `bool` | |
| Road type | `byte` id | 0 = none. A road cell with no type reads as the default road |
| Zone | `ZoneType` | `None`, `Residential`, `Commercial`, `Industrial` |
| Building | `byte` id | 0 = none |
| Household | `Household` struct | residents of a residential cell |

Roads and zones also keep **sparse index sets** (`RoadCells`, `ZoneCells(zone)`) so work that concerns only roads or zones scales with how much has been built. `SetRoad`, `SetZone` and `ClearCell` keep them in step; `RebuildIndexes()` rebuilds them after a save is loaded. Minimum map size is 8x8 (the CLI enforces 80x24 to 640x384 through `MapSize`).

The registries hold the content: `GameContent` bundles `Terrains`, `Features`, `Buildings` and `Roads`. Types are registered by name and get a byte id from the registry (terrains from 0, the others from 1, since 0 means "none"). Ids are an in-memory detail; saves store names.

### Zones

`Zones` ([Zones.cs](../src/TermCity.Core/World/Zones.cs)) defines R, C and I: name, letter, empty glyph, colors and `WeeklyValue`. `Zones.Placeable` lists them in display order.

## Map generation

`MapGenerator.Generate(width, height, seed, content)` runs these stages in order:

1. Terrain generators, ordered by `ITerrainGenerator.Order` then registry id (hills at 10, water at 20, so water paints over hills).
2. `HighwayGenerator.Generate`.
3. Feature generators, ordered by `Order` (trees at 10, rocks at 20).

Everything is **deterministic from the seed**. `GameRandom` is SplitMix64, with state that can be saved and restored, and `GameRandom.ForStage(seed, "name")` gives each stage its own independent stream, so changing one generator does not shift the randomness of the others. `PerlinNoise` and `PerlinNoise.ThresholdForCoverage(values, coverage)` turn noise into "cover this fraction of the map" blobs. Noise is squashed horizontally (x scaled by 0.5) because terminal cells are about twice as tall as wide, which keeps blobs round on screen.

| Generator | Key numbers |
|---|---|
| Hills | `Coverage` 0.17, `Frequency` 0.055, 3 octaves. Hill terrain has `BuildCostModifier` 1.5 |
| Trees | Base density 0.012; forest clusters cover 22% of the map at density 0.55. Half as common on hills |
| Rocks | Base density 0.012, three times as common on hills |
| Water | See below |

### Water

`WaterGenerator` ([WaterGenerator.cs](../src/TermCity.Core/Terrain/WaterGenerator.cs)) first picks a `WaterMapType` from the seed, then places a primary body, some extra lakes, and finally carves rivers.

| Type | Chance | Primary body |
|---|---|---|
| `SeaEdge` | 28% | A sea along one edge |
| `Bay` | 22% | A lake cut by an edge, or by a corner |
| `LargeLake` | 22% | An inland lake, 1.5 times the usual size |
| `RiverConfluence` | 20% | A sea or inland lake fed by two rivers that join |
| `InlandLakes` | 8% | A small inland lake |

- Water touching a map edge reaches at most `MaxEdgeDepth` = **10** cells in. A sea covers at most `MaxSeaShareOfEdge` = **30%** of its edge. Every edge stays mostly land.
- Extra inland lakes: `clamp(round(scale x 0.8 - 0.5 + random), 0, 6)` where `scale = sqrt(area / (160 x 96))`. Overridable through `WaterGenerator.ExtraLakes`.
- Each body is fed by a river with probability 0.92 (sea), 0.95 (primary large lake) or 0.75 (anything else); a confluence is always fed. A large lake gets a second inlet 40% of the time. A body without a river is simply fed from off the map.
- Rivers start at an edge, cross the land (never running along an edge) and never touch one another or another body, except in a confluence. If two rivers cannot be fitted, the map falls back to an ordinary one.
- `WaterGenerator.Build` returns a `WaterPlan` (type, bodies, rivers, a per-cell id grid) that the tests inspect.

### Highways

`HighwayGenerator` ([HighwayGenerator.cs](../src/TermCity.Core/World/HighwayGenerator.cs)) lays out the pre-existing road network:

1. Plan interchange nodes on dry ground, `EdgeMargin` (7) away from the map edge.
2. Join them into one network nearest first (Prim's algorithm), then add some extra links for loops.
3. Give dangling highways a gateway to the map edge, a link onward, or remove them. Make sure the network reaches the edge in enough places for the map size.
4. Add short street stubs off the interchanges as starting places.

Routes are straight, L-shaped or Z-shaped, with 90-degree, chamfered or diagonal bends. A diagonal is drawn as a staircase of ordinary road cells (one step sideways, one step up or down), so it connects through the normal 4-neighbor rules. The constants worth knowing:

| Constant | Value | Meaning |
|---|---|---|
| `ClearanceX` / `ClearanceY` | 16 / 8 | Window around each interchange that no other highway may enter (half as many rows, since characters are twice as tall as wide) |
| `WindowX` / `WindowY` | 3 / 2 | Room an interchange needs for its own arms |
| `MinLeg` | 4 | Shortest straight run |
| `MaxBridgeRun` / `MaxBridgeTotal` | 8 / 16 | Longest single bridge, and most bridge cells on one route |
| `EdgeMargin` | 7 | Distance of interchanges from the map edge |

If no interchange network fits (say, a map that is mostly water) it falls back to running straight highways across.

## Rendering

[CellRenderer](../src/TermCity.Core/Rendering/CellRenderer.cs) composes a cell from its layers, back to front:

1. **Terrain** gives glyph, foreground and background. The glyph is picked from the terrain's glyph list by `CellHash.Pick(x, y, count)`, a stable hash of the coordinate, so variety needs no per-cell storage.
2. **Zone** replaces the background; the foreground is the zone color (halved with `Rgb.Scale(0.5)` if the cell is not served) and the glyph is `░`.
3. **Feature** (only if there is no zone) replaces glyph and foreground.
4. **Building** replaces glyph and foreground. Outside a zone it also takes a neutral background.
5. **Road** replaces everything. The glyph comes from a 16-entry table indexed by a neighbor mask (N = 1, E = 2, S = 4, W = 8). The foreground is the road color, or amber (`#e8a33d`) if it is not connected. Over water (a bridge) the background stays water.

`CellInspector` produces the one-line description for the status bar. `BlockSampler` picks what to draw for one character when zoomed out. The most important thing in the block wins: buildings, then zones, then roads (bigger first), then terrain by priority (unbuildable 30, costly 20, else 0), then features.

Only single-width BMP characters are used, so the grid stays aligned in every terminal. Textures are deliberately sparse (about one grass cell in twelve is not `·`), because every cell whose glyph differs from the cell it replaces must be rewritten when the map scrolls. Keeping the texture simple reduces terminal output during scrolling.

## The session: cursor, camera, selection, zoom

`GameSession` ([GameSession.cs](../src/TermCity.Core/Session/GameSession.cs)) is pure logic with no UI dependency, so it is fully testable. It owns:

- the `Game` (replaced wholesale on load and new game; the session re-subscribes),
- the cursor, the `Selection` rectangle and the drag `Anchor`,
- the camera (`CameraX`, `CameraY`) and view size,
- the zoom level, the status message and its kind, the folded state of the side-panel sections, the edge-scroll and input-debug switches.

It raises three events, from broadest to narrowest, so the UI can redraw as little as possible:

| Event | Meaning | UI response |
|---|---|---|
| `Changed` | Anything, including game changes and zooming | Redraw the whole window |
| `CameraChanged` | Scrolling and panning | Redraw the map and minimap |
| `SelectionChanged` | Cursor or selection only | Redraw the changed cells and the status line |

**Zoom.** `ZoomLevel` runs from `MinZoom` = -2 to `MaxZoom` = 1.

| Level | Label | Meaning |
|---|---|---|
| -2 | 0.25x | `Stride` 4: one character is a 4x4 block |
| -1 | 0.5x | `Stride` 2: one character is a 2x2 block |
| 0 | 1x | One cell per character |
| 1 | 2x | `SpanX` 2: each cell is two characters wide |

Zoomed out, the camera is clamped to multiples of the stride so blocks do not shift as it moves, cursor movement is in blocks, and `MakeSelection` snaps outward to whole blocks. `SetZoom` keeps the cell under a given view position fixed.

**Messages.** `SetMessage` displays a message in the status line for `MessageDurationMs` = 5,000 ms, after which the line goes back to cell details.

**Actions** (`Zone`, `BuildRoad`, `Demolish`, `PlaceBuilding`) act on `ActiveArea`, which is the selection or, with none, the cell (or block, when zoomed out) under the cursor. A successful action clears the selection. `LoadFrom` replaces the game and **pauses** it.

### Previews, undo and session controls

[SessionFeatures.cs](../src/TermCity.Core/Session/SessionFeatures.cs) contains the additional session workflows:

- **Placement previews.** `PlacementPreview` captures the action kind, area, type and quote. Road/building validity reuses the simulation's placement checks; demolition previews identify cells with removable content. The UI requests a preview, then `ConfirmPreview` executes it or `CancelPreview` discards it. Preview rendering uses green/red tints and yellow for mixed zoomed-out blocks. `PlacementConfirmationView` draws a seven-row Yes/No popup near the target, clamped inside the map at every zoom and terminal size. Keyboard arrows/Tab select, Enter accepts, Y/N choose directly, and Esc cancels; mouse buttons work too. Road-line editing keeps map focus so arrows still adjust its endpoint. Confirmation buttons are not drawn in the message bar.
- **Straight roads.** `BeginRoadLine` anchors a cursor or mouse position. Moving the endpoint chooses the dominant axis and creates a one-cell-wide rectangle, independent of zoom stride. The quote exposes skipped cells; confirmation uses the same road action as rectangular placement.
- **Undo.** Before an action, `Execute` serializes the full city. Only a successful action replaces the single undo slot. `ElapsedDays` includes fractional day progress; running games allow undo through seven elapsed game days, while paused games allow the stored action regardless of age. `RequestUndo` checks eligibility before pausing, then shows a rollback warning. Confirmation restores the snapshot using the same content registry and `preserveTimings: true`, and leaves the game paused. Cancellation also leaves it paused. New/load clears the slot; snapshots are not written into saves.
- **Progress guards.** Revision and elapsed-day tracking detect changes since a manual save or load. Quit, new/restart and UI load requests offer save, discard or cancel. A failed save does not run the continuation. Low-level `LoadFrom` is also used for command-line loading.
- **Prompts.** `SessionPrompt` holds text, choices and an optional file-path input. `InteractionView` presents it without nested application loops. Menus, reports and confirmations freeze simulation through `GameSession.Update`; previews do too. Closing a prompt preserves the game's explicit pause state.
- **Autosave.** Every 60 real seconds, session updates check for changes since the last autosave. Three slots are derived from `SavePath` as `<basename>.autosave1.json` through `3.json`. A pending save is written first, backups are copied oldest-first, then the pending file replaces slot 1. Failures leave the newest slot intact, display an error and retry at the next interval. Autosave does not mark progress manually saved.
- **Guide and achievements.** The application requests onboarding for fresh interactive games. The guide can be dismissed, and crossing the configured residential unlock threshold produces a notification. Population milestones are 100, 500, 1,000, 5,000 and 10,000; the highest reached threshold is persisted so a load does not repeat the award. `F7` shows `LastReport` and milestone progress; `F8` shows the zone diagnostics.

### Edge scrolling

`EdgeScroller` turns "pointer near the map's edge" into whole-cell scroll steps. The sensitive band is `HorizontalZone` = 3 columns and `VerticalZone` = 2 rows. Speed rises with depth into the band:

| Depth | Horizontal (cells/s) | Vertical (cells/s) |
|---|---|---|
| 1 | 12 | 6 |
| 2 | 28 | 12 |
| 3 | 48 | |

Fractions carry over between frames so the speed is independent of frame rate. An axis is skipped when the view is too small for its bands not to overlap.

## The terminal front end

Everything in `src/TermCity.App`.

### Layout

`GameApp.Build` creates a borderless full-size `Window` holding:

| View | Position | Role |
|---|---|---|
| `HudView` | Top row | Money, date, week bar, population, clock |
| `MapView` | Fills the rest, minus the panel | Map drawing, mouse and keyboard input |
| `MinimapView` | Right, 34 wide x 14 tall | Whole-map overview in half-block characters |
| `ZoomBarView` | Right, under the minimap | `[-] 1x [+]` buttons |
| `InfoPanelView` | Right, remaining height | Demand, City, Zones sections |
| `MessageBarView` | Bottom row | Message or cell details, key hints |
| `PlacementConfirmationView` | Small map-adjacent popup | Yes/No placement and demolition confirmation, quote, keyboard/mouse input |
| `HelpView` | Overlay | Hidden until `F1` or `?`; any key or click dismisses |
| `InteractionView` | Full-window overlay | City menu, file-path input, progress guards, undo confirmation, guide and reports |

`PanelView` is the base for custom-drawn text panels (`ClearPanel`, `DrawText`). `PanelWidth` is 34 columns. The minimap is `MinimapHeight` = 14 rows: one title row and 13 rows of half-block "pixels" (26 pixels tall), enough for a landscape map 28 pixels wide (the panel minus a 3-column margin each side) once stretched by `VerticalStretch` (1.5). The right-click menu is a Terminal.Gui `PopoverMenu` built by `ContextMenuBuilder`.

### The loop and redraw throttling

- On start, `Application.MaximumIterationsPerSecond` is set to 60 to keep keyboard and mouse input responsive.
- A timeout requests an 8 ms interval. It records the loop gap, ticks edge scrolling, flushes pending redraws and updates the message state. It calls `GameSession.Update` once at least 100 ms have elapsed since the previous update; the session holds simulation for previews/prompts and schedules autosaves.
- Session events do not redraw directly. They set a `Pending` flag (`Camera`, `Selection`, `Everything`), and `Flush` redraws at most every `_frameMs` (derived from `--fps`, default 30, range 5 to 60). Cursor-only redraws are rate-limited to every 15 ms because they are cheap.
- A gap between loop ticks over `StallMs` = 400 means the loop stalled (for example the window was in the background); the map then ignores the old pointer position until the mouse moves, so it does not keep scrolling towards an edge the pointer left earlier.
- On Windows, `FineTimer` calls `timeBeginPeriod(1)` for the life of the app to request finer timer resolution and support steady loop timing. It does nothing on other platforms.

### Input quirks

- Plain left-click highlights on button-down; left-drag pans the map; Shift, Ctrl or Alt + left-button selects. Right-click opens the menu (a release is followed by a synthesized click, so the menu is guarded against opening twice within 250 ms).
- Terminals often keep modifier keys for themselves (Windows Terminal reserves Shift for its own text selection). That is why sideways scrolling is Alt+wheel only, and why `+`/`-` and the zoom bar always work.
- Terminal.Gui defines the "sideways" wheel flags as the vertical ones plus Ctrl, so Ctrl+wheel and a tilt wheel are the same event, and both zoom here. Wheel steps are `WheelStep` = 3 characters.
- Edge scrolling is off by default and toggled with `E`. A pointer resting in the very first terminal column is dropped after `OuterEdgeStaleMs` = 1,200 ms, because terminals report nothing when the pointer leaves the window. Dragging out a selection to the edge always scrolls.
- Moving the pointer is not delivered as an event, so `MapView` polls the application's last known mouse position.

`F12` (`ToggleInputDebug`) shows the last key and mouse events and the loop gap in the status bar. It is the first thing to ask for in a bug report about input.

## Persistence

`SaveGameStore` ([SaveGameStore.cs](../src/TermCity.Core/Persistence/SaveGameStore.cs)) writes JSON, version `CurrentVersion` = 1. The default path is `Environment.SpecialFolder.LocalApplicationData/TermCity/quicksave.json`: `%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux and macOS.

| Field | Notes |
|---|---|
| `Version` | Loading a different version throws `InvalidDataException` |
| `Config` | The whole `GameConfig`, so a loaded city keeps its rules and map size |
| `Money`, `Week`, `Day`, `DayProgressSeconds` | Calendar and cash |
| `RngState` | `GameRandom` state, so growth continues exactly as it would have |
| `Growth` | Planned weekly allocations and accumulated growth totals; preserves midweek continuation |
| `LastReport` | Latest completed week's income and growth |
| `HighestMilestone`, `GuideDismissed` | Achievement progress and guide preference |
| `ZoneRemovals` | Sparse cell-index dictionary of original zone and absolute game-day removal deadline |
| `Speed`, `Paused`, `Taxes` | |
| `Terrain`, `Features`, `Buildings`, `RoadTypes` | A palette of **names** plus data, so stored types are resolved by name rather than in-memory registry id |
| `Roads`, `Zones`, `Households` | Raw byte layers |
| `Compression` | `"deflate"`: layers are deflate-compressed, then base64-encoded. The loader also accepts uncompressed base64 layers when this field is absent |

Saves are written to `<path>.tmp` and moved over the target to protect the existing save during writing. On load, the speed table and `DaysPerWeek` are taken from the application's `GameConfig` defaults rather than the file. The map's sparse indexes are rebuilt by `RebuildIndexes`.

## Configuration reference

All rules live in `GameConfig` ([GameConfig.cs](../src/TermCity.Core/Simulation/GameConfig.cs)), an immutable record that is serialized into saves. Construct one with `with` expressions.

| Property | Default | Meaning |
|---|---|---|
| `MapWidth` / `MapHeight` | 160 / 96 | Map size in cells. The CLI accepts 80x24 up to 640x384 |
| `Seed` | 1 | Map seed (the CLI picks a random one) |
| `StartingMoney` | 50,000 | Cash at the start; enough for 100 street cells on grass |
| `RoadCostPerCell` | 500 | Base cost, multiplied by the road type and the terrain |
| `MinResidentialCells` | 10 | Homes that must fill before shops and factories can |
| `ResidentialPerCommercial` | 20 | Filled homes per commercial cell |
| `ResidentialPerIndustrial` | 10 | Filled homes per industrial cell |
| `DefaultTaxRate` | 0.05 | Initial tax rate for each zone |
| `RoadServiceReach` | 2 | Cells a connected road serves |
| `MaxNewResidentialPerWeek` | 3 | Weekly cap for a brand new city |
| `MaxNewCommercialPerWeek` | 1 | |
| `MaxNewIndustrialPerWeek` | 1 | |
| `GrowthRatePerWeek` | 0.02 | The weekly cap grows by this fraction of what already exists |
| `WeeksPerYear` | 52 | |
| `DaysPerWeek` | 7 | |
| `Slow/Medium/FastSecondsPerWeek` | 8.4 / 4.2 / 1.4 | Real seconds per game week |

Road and terrain numbers live with their types: `DefaultRoads` (cost multipliers 1.0, 1.8, 3.0; ranks 1, 2, 3) and `DefaultTerrains` (hill `BuildCostModifier` 1.5).

## Extending the game

### A terrain

Terrain types live in a registry with glyphs, colors, `Buildable`, `BuildCostModifier` and an optional `Generator`:

```csharp
var content = new GameContent();
content.Terrains.Register(new TerrainType
{
    Name = "Swamp",
    Glyphs = ["%", "~"],
    Foreground = Rgb.Hex(0x6b8e23),
    Background = Rgb.Hex(0x2a3318),
    BuildCostModifier = 2.0,
    Generator = new MySwampGenerator(),   // implements ITerrainGenerator { Order, Generate(...) }
});
var game = CityGame.New(config, content);
```

`Generator.Order` decides whether it paints over or under hills (10) and water (20). Remember the scrolling cost: keep glyph lists mostly one repeated glyph (`Enumerable.Repeat`), as the defaults do. `TerrainType` also has `AllowsFeatures` and a per-feature `FeatureDensity` multiplier.

### A feature, building or road

`FeatureRegistry`, `BuildingRegistry` and `RoadRegistry` work the same way.

- **Feature** (trees, rocks): glyphs, color and an `IFeatureGenerator`. `ScatterFeatureGenerator` handles scatter and clustering for you.
- **Building**: a building with a `Zone` grows there automatically; one with `PlayerPlaceable = true` and a `Cost` is placed by the player and appears under Buildings in the menu. The default registry contains only zone-growth buildings, so the Buildings menu shows a disabled placeholder. `CityGame.PlaceBuilding` and its quoting logic support custom player-placeable types.
- **Road**: provide 16 glyphs indexed by neighbor mask, colors, `CostMultiplier` and `Rank`. A higher rank can replace a lower one for the price difference; types are otherwise cosmetic and all connect and serve the same way. `PlayerPlaceable = false` keeps a type for generation only.

### Tuning

Most rules are `GameConfig` values, and the growth feel comes down to `GrowthRatePerWeek`. A rough check: with the defaults, a well-roaded city reaches about 1,000 homes (4,500 people) after two years and 8,000 homes after four; a fully built `large` map takes around seven years.

## Tests

`dotnet test` runs the xUnit suite without requiring external services or an interactive terminal. The test project lives in [tests/TermCity.Tests](../tests/TermCity.Tests).

| Area | Files |
|---|---|
| Simulation | `EconomyTests`, `GrowthTests`, `ClockAndSessionTests` |
| Generation | `MapGenerationTests`, `WaterTests`, `RoadTests` (road types and highway generation), `DiagonalAndPanelTests`, `LargeMapTests` (sizes up to 640x384) |
| Session and input logic | `ClockAndSessionTests`, `EdgeScrollTests`, `ZoomTests` |
| Persistence | `PersistenceTests` |
| Whole UI | `UiTests` |
| Session workflows | `SessionFeatureTests` (previews, undo boundaries, snapshots, autosaves, guards, guide, milestones, diagnostics), `SessionFeatureUiTests` (keys, mouse, prompts and rendering) |
| Dezoning and nearby confirmation | `DezoneTests` (timing, occupants/taxes, capacity, rezoning, persistence and undo), `PlacementConfirmationUiTests` (popup position, edges/zoom, choices and dezone controls) |

Two helpers make tests short:

- `TestCity.Flat(seed, config)` is an empty flat map with a single road along row 20 from edge to edge, and `TestCity.Advance(game, weeks)` runs weeks deterministically.
- `UiHarness` runs the **real** Terminal.Gui application headlessly (ANSI driver, 120x30) and injects keys and mouse input directly (`InputInjectionMode.Direct`, which avoids the flakiness of the asynchronous terminal-input pipeline), so the wiring between input, session and views is covered without a terminal.
- A focused queued-click test injects an ANSI mouse press through the input pipeline while paused and verifies that a completed draw changes the highlight without forcing a redraw. Its three-second timeout is a test-hang guard, not a latency benchmark; it does not measure a real terminal's presentation latency.

## Diagnostics and performance

- Set the environment variable **`TERMCITY_TRACE`** to a file path and the game writes a timing trace there: one line per slow event (loop gaps, draw times, camera moves and the first few mouse events). It costs nothing when the variable is not set. See [FrameTrace.cs](../src/TermCity.App/FrameTrace.cs).
- **`F12`** in the game shows input events and the loop gap on the status line (about 16 ms when the loop keeps up, with the worst gap since it was turned on). A gap of hundreds of milliseconds is a stall, which is what delayed clicks after switching windows look like.
- Terminal.Gui rewrites every cell a view draws, so a scroll step rewrites most of the map view: about 18 KB of color codes, mostly 24-bit color changes. Redrawing 60 times a second during a drag is over a megabyte a second. A terminal (or an editor's built-in terminal, which adds layers) that cannot render that fast falls further behind the longer a drag lasts. The game itself neither slows nor leaks, so it limits scroll redraws to 30 a second (about 0.55 MB/s). `--fps` changes this.
- Moving the cursor, clicking and extending a selection redraw only the cells they change. A redraw with no scrolling skips cells that already show the right thing, keeping terminal output low for cursor movement and clock updates.
- **Verified Windows Terminal version-specific delay:** on v1.24.12741.0, scrolling for about ten seconds and then idling for another ten seconds could make the next click's highlight appear roughly two seconds late, even while paused. The captured game-loop gaps stayed below 100 ms, and the late selection change was followed by a completed draw about 2 ms later. Lowering FPS, switching between ANSI and native Windows input, software rendering, and full repaint did not resolve the delay. The classic Windows console host and the tested Mac terminal were responsive. Running the same game in [Windows Terminal v1.25.2733.0](https://github.com/microsoft/terminal/releases/tag/v1.25.2733.0) confirmed that upgrading resolves the reproduction.
- That release includes [microsoft/terminal#20723](https://github.com/microsoft/terminal/pull/20723), which fixes presentation, queued-frame latency, and deferred rendering issues in AtlasEngine. This is relevant upstream context, not proof that this individual change was the sole cause of the observed delay. The headless regression cannot detect terminal presentation latency; keep the real-terminal scroll/idle/click check when investigating similar reports.

## Design decisions

The gameplay rules and their rationale:

- "1 commercial to 20 residential" is applied as `ceil(filledResidential / 20)` filled commercial cells once 10 residential cells are filled (industrial: `/ 10`).
- Tax is `rate x weekly value` per filled cell (5% of $200 = $10 per residential cell per week). Rates are stored per zone and saved; the gameplay interface does not expose tax-rate controls.
- Roads on hills cost 1.5x. Building on trees and rocks clears them for free.
- Zoning skips water, roads and occupied cells, except when restoring a dezoned building's original zone to cancel removal. Changing an occupied building to another zone requires demolition or waiting for its removal.
- Dezoning immediately removes the designation but keeps buildings and occupants for a random two to three game weeks. Their residents, taxes and capacity contributions remain active until removal.
- Starting money is $50,000, enough for an access road and a small neighborhood grid before tax income begins.
- Roads over water exist only as generated bridges. The player cannot build on water.
- A week is seven days. New residents arrive a few at a time over the days, and tax lands on the last day.
- Growth compounds (`GrowthRatePerWeek`) so a big map does not take centuries to fill.

## Color palette

The interface colors are in [Colors.cs](../src/TermCity.App/Colors.cs). Terrain, feature, road, building and zone colors are on their definitions (`DefaultTerrains`, `DefaultFeatures`, `DefaultRoads`, `BuildingRegistry.CreateDefault`, `Zones`), and the color tables in the README list the lot. The minimap has its own muted set in `MinimapView` (water `#3c86dc`, hill tint `#7a6840`, land `#2b5233`, roads `#bdbdbd`, camera box white), and the cursor and selection colors are in `MapView` (cursor foreground `#101010` on background `#f5f5f5`; selection blends `#58a6ff` at 50% into the background).
