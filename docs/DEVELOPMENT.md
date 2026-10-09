# TermCity developer guide

TermCity is a **Godot .NET** desktop game with a UI-independent C# gameplay core.
The [README](../README.md) covers installation, controls, rules and player-facing behavior.

## Build and run

Requirements: **.NET 10 SDK** and **Godot 4.7.2 .NET**. The standard Godot edition cannot run C# scripts.
The root [TermCity.slnx](../TermCity.slnx) includes core, Godot and unit-test projects.
The Godot-local solution also remains available for editor integration.

```bash
dotnet build TermCity.slnx
godot --path godot -- --seed 42 --size medium
godot --headless --path godot -- --size SF --seed 42 --dump-map
dotnet build TermCity.slnx -c Release
dotnet test TermCity.slnx -c Release --no-build
```

Replace `godot` with the executable path when needed; on macOS it is often
`/Applications/Godot_mono.app/Contents/MacOS/godot`.
Game arguments follow `--`; [GodotOptions.cs](../godot/GodotOptions.cs) handles seed, size,
load, frame cap, map dump, help, smoke tests and optional screenshot capture.
`--help` and `--dump-map` are headless-safe and exit without creating music/UI resources.

All projects target `net10.0`, with nullable references and implicit usings enabled.
Godot uses `Godot.NET.Sdk/4.7.2`; tests use VSTest/xUnit v2.
Generated `bin`, `obj`, `.godot` state and export outputs are ignored.

### Windows rendering and resize checks

[project.godot](../godot/project.godot) keeps the Compatibility renderer, with
`rendering/gl_compatibility/driver.windows=opengl3_angle` selecting ANGLE / Direct3D11 only on Windows.
This reduces the native OpenGL resize stall without adopting the Vulkan path that crashed during
live resizing. Other platforms retain their default drivers. Native OpenGL remains an explicit
fallback via `--rendering-driver opengl3`, placed before the game-argument separator.

The graphical smoke test checks large resizes and maximize against a one-second budget on ANGLE.
[WindowsResizeSmoke.cs](../godot/WindowsResizeSmoke.cs) also exercises native enter/sizing/paint/exit
messages and window-size changes on the test window, without moving the system cursor or interacting
with other applications. It restores the original bounds, checks city data is unchanged, and emits
`TERMCITY_WINDOWS_RESIZE_OK`. Headless and non-Windows runs skip native Windows calls.

These checks do not establish stability during physical edge dragging. Rapid live resizing has
also been reported to hang with ANGLE: mouse input remains captured, opening Task Manager releases
the mouse, but the game stays frozen. No managed exception or graphics-reset event was found in
the observed logs. A native rendering/driver wait and a UI-thread deadlock remain unconfirmed
possibilities. Avoid aggressive automated GUI stress on an interactive desktop.
For diagnosis, keep the frozen process alive and use Task Manager's Details view to create a
memory dump of the specific game process before ending it. Keep dumps local; they contain process
memory. Inspect native and managed thread stacks rather than inferring the cause from resize timings.

## Architecture

| Area | Responsibility |
|---|---|
| [src/TermCity.Core](../src/TermCity.Core) | World layers, content registries, terrain generation, scenarios, simulation, session workflows, rendering data and saves |
| [godot](../godot) | Scene tree, desktop UI/input, map/minimap drawing, display settings, synthesized music and engine integration checks |
| [tests/TermCity.Tests](../tests/TermCity.Tests) | Simulation, generation, scenarios, persistence, session, rendering, grid/options and music synthesis tests |

`CityGame` owns rules, money, calendar, growth and lazily cached statistics/road service.
`CityProgression` owns the shared City Grew! stages, permanent building gates, unlock descriptions
and next-step coaching. `CityGame` records peaks independently of any session or guide setting.
`GameSession` owns cursor/camera/selection, zoom level, messages, previews, undo, prompts,
save/load/autosave guards and feedback. It has no Godot dependency.

Accelerated default-rules gameplay regression tests cover two seeds on each random map size,
using paid actions, no loans and a mid-play save/load. They reach the final 10,000-person milestone:

```bash
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj --filter 'Category=Playthrough' --logger 'console;verbosity=detailed'
```

See [the playtest report](PLAYTEST.md) for measured game-time equivalents and balance decisions.

`Main` connects session events to desktop controls. It remains one Godot `Control`, attached through
[Main.cs](../godot/Main.cs) in [Main.tscn](../godot/Main.tscn), with cohesive partial files:

| File | Responsibility |
|---|---|
| [Main.cs](../godot/Main.cs) | Startup, frame processing, session notifications, focus, shutdown and smoke-test orchestration |
| [Main.Audio.cs](../godot/Main.Audio.cs) | Streaming music, placement sounds, volume/mute controls and audio resource cleanup |
| [Main.Display.cs](../godot/Main.Display.cs) | Preferences, font/window sizing, layout, HUD/status refresh and city-name editing |
| [Main.Dialogs.cs](../godot/Main.Dialogs.cs) | Menus, modal construction/sizing, tabbed prompts, choice help and dialog keyboard navigation |
| [Main.Input.cs](../godot/Main.Input.cs) | Gameplay keys, pointer/trackpad/pinch gestures, selection, panning and edge scrolling |
| [Main.Effects.cs](../godot/Main.Effects.cs) | Effect setup, level/celebration controls and redraw scheduling |

Fields live beside their owning behavior. The partials share the same scene instance and private state;
they do not introduce extra nodes, controllers, subscriptions or a second lifecycle.
`LoadPreferences`/`SavePreferences` handle the shared display/audio settings file.

`TerminalMap` and `TerminalGrid` implement the terminal-styled cell display; they are Godot presentation
components, not a console frontend.
`CityPanel`, `CityMinimap`, `CitySplit` and `TerminalFrame` provide sidebar sections, overview,
bounded divider resizing and closed double-border rendering. `SidebarReport` supplies the reduced
City table and semantic colors without a Godot dependency. `CityReport.ZoneDetails` is shared by
Demand hover tooltips, weekly/growth reports and the full-rules health report.

## World and content

`GameMap` stores flat layers indexed by `y * Width + x`: terrain/feature/road type/building IDs,
road booleans, zones and households. Sparse road/zone index sets keep simulation work proportional
to built cells. `SetRoad`, `SetZone` and `ClearCell` maintain them; loading rebuilds them.
`BuildingType.Width/Height` define player-placement footprints. `PlanBuildings` tiles the
expanded selection, rejecting blocked buildings atomically. `GameMap.BuildingFootprints` and a
sparse cell-owner lookup associate occupied cells with one building; `ServiceCells` contains
only its anchor. Road access through any occupied cell activates the building, but upkeep,
supply, coverage and pilgrimage are counted once. Area effects originate at the anchor.
Saves retain explicit footprints; service buildings without them are rejected on load.

`GameContent` combines terrain, feature, building and road registries. Types receive compact byte
IDs in memory; saves resolve their names instead, allowing content reorderings.
Default zones are Residential, Commercial and Industrial. A residential household stores adults,
children and seniors; occupied businesses contribute income but not population.

`CellRenderer` composes terrain, features, zones, buildings and roads into glyph/foreground/background.
The road tool (T) draws a line at any angle between its two ends (`CellLines.Between`, four-connected so the simulation can follow it); the preview carries just the cells on the line. Straight runs and diagonal staircases are drawn as straight lines (`RoadCurves.Straighten`), rounded only at real bends.
Roads may cross and branch but never run side by side: `CityGame.PlanRoad` (via `RoadRules.Plan`) lets a stroke touch another road only where it runs into it or at its two ends, and never completes a 2x2 block of road (`CompletesRoadBlock`). It also refuses a new junction within `RoadRules.JunctionSpacing` cells of an existing one. Quote, build and the line preview all use that plan, generated maps lay highways first and then the street grid as strokes through the same rules (`CityMapGeometry.Grid`), `RoadSeparation.RemoveFragments` drops tiny detached scraps, and `RoadSeparation.Apply` clears generated roads that form 2x2 blocks (dropping the humblest road where the neighbours stay connected). In `RoadCurves`, `Pair` decides which arms of a junction run through it (highest rank first, then straightest), so a diagonal highway stays one curve; `Join` moves each branch end onto the host curve and, unless the meeting is nearly square, `Ramp` bends it into a quadratic merge along the host. Zoomed out, `RoadVectorLayer.Weight`/`Opacity`/`Shown`/`RankOpacity` thin, fade and (at stride 8+) drop the minor roads, while top-rank highways always keep a narrow bed and a thin double line (`HighwayBedHalf`, `HighwayLineOffset`, `HighwayLineHalf`) so they stay a backdrop.
At four-way junctions where pairing would create opposing bends, only the main road continues
through; the other arms join it as branches. This prevents independently rounded curves from
pulling apart despite connected map cells. `RoadCurveTests` flood-fills the rendered road pixels,
including seed 1981019679 at 160x96 in normal and 2x zoom, to check visible connectivity.
Road glyphs use cardinal neighbor masks; generated highways leave each interchange along a compass arm and then run at any angle as four-connected staircases.
Every road type is drawn as a curve when `vectorRoads` is passed (any zoom level, no overlay; `BlockSampler.VectorRoads`
and `TerminalGrid` pass it, the minimap does not and stays glyph-based): the cell is left blank and
`RoadCurves`/`RoadVectorLayer` supply the picture. Network connectivity remains four-connected.
`Rendering/RoadGeometry` is built once per `RoadNetwork` revision from shared `DrawnPaths`, also reused
by `RoadVectorLayer`. It reserves whole cells crossed by the normal-size road bed or conservative
junction blend, for every road type including disconnected roads. Automatic growth and player
building placement reject these partially occupied cells; existing buildings are retained.
`RoadCurves.Extract` traces roads as paths that run straight through junctions, cuts a path where the road type changes
(`SplitByType`, so each stretch keeps its own colours and the seam is hidden by a one-cell overlap), then straightens,
resamples and smooths each (a branch reaches the host centreline so three-way junctions merge without a separate end cap). `RoadVectorLayer`
rasterises 16x16-cell chunks with a signed-distance field per path, blended across paths with a smooth minimum so
junction corners get fillets. The bed is opaque out to the outer edge of the two edge lines (`BedHalfWidth`), so it hides
terrain glyphs and water; chunks are drawn after the terrain glyph pass. When zoomed out (`Stride` > 1) chunks are
rendered at `Render(chunkX, chunkY, scale, stride)`, covering stride times the map area at the same on-screen width.
Ambient sprites (cars, walkers) follow the drawn curve: `IAmbientWorld.Snap` projects their position onto `RoadGuide`'s
segments (filtered with `FollowRate` so they glide), and speeds are the `AmbientLife` `*Speed` constants.
`godot/TerminalMap` caches the chunks as textures and draws them between the cell backgrounds and the glyphs.
Existing roads over water render as bridges. Unserved zones dim and disconnected roads turn red.
`BlockSampler` chooses representative visuals for coarse zoom, prioritizing buildings, zones,
roads, water, hills, features and open ground. `CellInspector` supplies bottom-line details.

## Generation and scenarios

Random `MapGenerator` stages are deterministic from the seed:

1. Terrain generators ordered by generator order and type ID: hills, then water.
2. Highway/interchange generation.
3. Natural-feature generators: trees and rocks.

`GameRandom` uses independent seeded stage streams so changing one stage does not shift another.
Perlin noise controls terrain coverage. The default hills cover about 17%; water generators
produce sea edges, bays, lakes, deltas or meandering rivers plus smaller bodies.
Highways reach map edges, connect through cardinal links and use straight bridges in random maps.
Highways between interchanges are angled: a straight lead (5 columns or 3 rows) out of each arm, joined by an evenly
stepped staircase, with turns of at most 60 degrees on screen. Where water, crowding or the map rules that out, an L or Z
of right-angle corners with legs of at least four cells is used instead; bridges never bend. Gateway rays drift sideways
as they run to the edge. Nearly aligned interchanges are still snapped into line. Two arms of one interchange fan out
rather than run side by side.

`MapSize` accepts small 160x96, medium 320x192, large 640x384, custom 80x24 through 640x384,
and case-insensitive city presets **SF/LA/SD/CHI/STL** (San Francisco, Los Angeles, San Diego, Chicago, St. Louis).
Named presets set `GameConfig.Scenario`, normalize new maps to 640x384 and use `CityScenarioMap`.
San Francisco delegates to `SanFranciscoMap`; shared `CityMapGeometry` handles polygons, ellipses and
orthogonally connected road paths.

These are hand-shaped, north-up regional approximations, not GIS/current land-use datasets.
The layouts include city-specific coastlines/rivers/hills/parks and road-served occupied R/C/I
districts. They keep the medieval setting's vocabulary and economy. Households use independent scenario RNG stages.
Scenario road strokes cross water only when the crossing has land approaches at both ends.
Water-starting/ending road spurs are skipped, street grids stay on buildable land and cannot
branch directly onto bridge water cells. Chicago and St. Louis highway bends stay off their rivers,
while deliberate bridges (including Golden Gate and Bay Bridge) remain. These rules apply to
new/restarted scenarios; loading does not rewrite a saved city's roads.
Scenario generation first zones districts, then reserves complete service footprints on clear buildable
land, then populates the remaining district plots. Utility capacity is planned against those future
occupied plots. Existing services and road beds are not overwritten.

Named cities start paused without automatic onboarding. Explicit F6 guide requests still work.
Saved dimensions/layers remain unchanged on load. Scenarios use only the current `CityScenario` names
and `GameConfig.Scenario`; there are no historical aliases or per-city configuration flags.

## Simulation

`CityGame.Update` advances real time, capped to 0.5 seconds per call to prevent catch-up after stalls.
Paused updates do not advance gameplay. A game week has seven days and a year has 52 weeks.
New games capture the local starting year; saves must contain it.

Growth is planned weekly and distributed across days. Empty, served lots fill up to their daily
allocation. Candidates are sorted before random selection so results remain stable after loading
sparse indexes. Base weekly caps are 3 homes, 1 shop and 1 factory, plus 2% of existing capacity.
Commercial/industrial growth unlocks at 10 occupied residential cells, with one supported shop per
20 homes and one factory per 10 homes.

The medieval layer lives in `TermCity.Core/Simulation`: `Seasons` (the farming year), `Harvest` (grain, famine, buying
grain), `Feasts` (pilgrim feast days), `Disasters` (fires, raids, plague, floods, earthquakes), `Settlement` (town rank and the
crown's tribute) and the castle tiers in `CityServices` (`SeatRank`). Each runs from `PopulationEngine.RunWeek` or
`CityGame.AdvanceWeek` in a fixed order and draws only from `game.Rng`, so a seed stays reproducible. See
[POPULATION.md](POPULATION.md) and [MEDIEVAL.md](MEDIEVAL.md).

Taxes arrive weekly. Occupied R/C/I cells have base weekly values 200/350/500 before the default 9% tax rate.
Dezoned occupied buildings retain population/tax income until their random 14-21-game-day deadline.
Restoring their original zone cancels removal. Demolition is immediate, free and unreimbursed.
Full-funded service upgrades compare supply capacity or summed actual strength-weighted coverage.
Higher tiers have strictly cheaper normalized cost over 52 weeks on flat ground; road upgrades
have corresponding value tests.

Statistics visit sparse zones/removal queues and use stack counters. Road connectivity is cached
separately from growth; road changes invalidate it. Utility demand and pollution refresh when
buildings grow, disappear or change zone, before subsequent statistics and weekly settlement assessment.
Road service spreads through buildable
terrain from roads connected to the map boundary. Default reach is two cells.
Nearby connected visible road beds supplement this access when within 0.75 cell widths horizontally
and cell heights vertically of a building cell's footprint edge.

`Changed` notifies possible UI changes; `MapVersion` advances only for map-visible changes.
Renaming, taxes, speed, pause and guide preferences raise non-map change notifications, allowing
dirty/autosave tracking without changing the map version or discarding road geometry and service caches.
Failure results carry user-visible explanations.

## Session and zoom

`GameSession` supports camera pan/scroll/jump, cursor movement, rectangular and keyboard selections,
preview quotes, straight road-line endpoints and confirmation. Camera and selection changes have
separate events. A successful placement clears its selection; failed/cancelled actions retain the
existing undo boundary.

Zoom levels are -2 (0.25x), -1 (0.5x), 0 (1x), and +1 (2x). Negative levels sample square blocks
of four or two cells. At +1, `TerminalGrid` doubles **both** pixel dimensions and `TerminalMap`
doubles glyph size; logical cells are never horizontally duplicated.

`Grid.Fill` sets the session viewport using fully visible-cell counts so keyboard navigation stays
on whole cells. Its buffer includes partial edge cells to paint the entire viewport, clipped by
`TerminalMap`. Pixel hit testing rejects coordinates beyond the actual viewport, including unused
parts of clipped cells, and uses
`PixelWidth`/`PixelHeight`; `TerminalMap.ZoomBy` remaps the world anchor after resizing so pointer
or center zoom stays anchored. Coarse cameras/selections align to whole sampled blocks.

Undo stores one serialized full-city snapshot and is valid while paused or within seven game days.
Confirmation restores the complete city, including money, random state and calendar. Load/new clears it.
Session prompts/previews suspend simulation. Autosave still tracks elapsed real time and changed cities.

## Godot UI and input

The default window is 1440x900 with a 640x780 base minimum, capped to the usable desktop. Display font size defaults to 20,
supports 16-28, and sets shared content scale `font size / 16`. Window sizing stays within usable
desktop bounds. F3 opens live font controls with OK/RESET.

The top header is one row with uniform base font size 24, fitted on narrow windows. TermCity/name,
calendar/clock and population/budget have shared double-white frames. Week is spelled out and
space-padded; the day bar comes from `Fmt.WeekBar`. PAUSED uses a gentle four-second yellow pulse.

Double-click enables a right-aligned city-name `LineEdit`, limited to 16 Unicode code points.
Its native caret width matches a character to form a blinking block; normal/focus right margins
reserve that character cell so the caret is not clipped. Text backspaces toward the
stationary right edge; an overlay slot leaves branding and header geometry unchanged. Editing
pauses the clock and restores its previous state on commit/cancel. Invalid names remain editable
with a visible error.

`TerminalFrame` draws two closed rectangular outlines inside its own bounds, with square corners and
a shared 12-pixel content inset. Neighboring frames are separated instead of extending edges into
crossing or T-shaped joins. The map fills its expanding frame; partial edge cells are clipped rather
than snapping the frame to cell widths. This keeps all four map insets exactly equal and sidebar
dragging continuous. `CitySplit.Divider` matches the map inset, so the external gap is also 12 pixels.
Popups fit the longest title/choice plus five `M` glyph-widths per side,
with inner vertical margins of 2 pixels. Deferred minimum-size changes re-fit wrapped content
and keep smaller dialogs from retaining stale heights. Oversized content scrolls.

The non-scrolling sidebar contains Minimap, Demand and City, with black double-white header cells.
An enclosing `SidebarFrame` retains the full-height double-white outline and the same 12-pixel
content inset as the map frame. The divider gap is measured between the two outer frames.
City uses colored native cells without a Metric/Value heading row. It omits population, tax/budget,
standing and employment rows duplicated elsewhere. Demand tooltips contain all per-zone counts;
keyboard users get the identical formatter through F7, F8 and the full-rules health report.
City body text targets 16 logical pixels (three larger than the previous body setting); section
headers stay at 18. Constrained layouts adapt the body size without changing the header font.
`CityPanel` prioritizes an aspect-correct minimap at the full content width, adjusting text spacing
and using paired City rows when height is constrained. Minimap and Demand remain full-width blocks.
Width/height/row-count caching avoids redundant fitting. Screen-constrained layouts preserve the
minimap's shape and fit the largest overview that leaves the remaining details visible.
The Guide sidebar section is removed; optional next-step coaching is on the F6 Start tab, and its
existing dismiss/enable controls and saved preference still work there.
`CitySplit` supports a 12-pixel drag area, 280-pixel sidebar minimum
and 480-pixel maximum, reduced to retain 320 pixels for the map where possible.
Esc > Resize sidebar offers 12-pixel steps via focused minus/plus buttons.
Arrows navigate dialog buttons; Space/Enter activates the focused button. Text-field dialogs retain
native cursor navigation and sliders retain native arrow editing. Same-menu refreshes preserve
the highlighted choice. `MenuShortcuts` assigns unique letter/modifier pairs, preferring first
letters and then subordinate letters. `MnemonicButton` underlines the assigned character; rare
Shift fallbacks show a visible hint. Number shortcuts are not supported.
Shift+click or Shift+Enter inspects a dialog option without activating it; plain Enter still activates.
`SessionChoice.Help` supplies registered building/road glyphs, descriptions, current-funded upkeep,
cost, capacity, radius/strength and requirements. Ring icons come from `AreaOfEffect.ForBuilding`,
not a separate effect list. Other choices have a text fallback. The independently scrolling help
panel stays on the right, with compact proportional widths in small windows; Close help does not
change menu focus/history. Shift+letter mnemonics and ordinary selection Shift+click are unchanged.
The guide's Glossary tab lists current terrain, feature, zone, road and building content (including
footprint art), actual area-of-effect icons, and the shared `EffectGlyphs` with their meanings.
`GameSession` retains parent prompts, input text and selected indexes. Cancellation pops one parent;
successful actions explicitly close the menu chain. Placement previews retain their originating
prompt/history and restore it only on cancellation, not successful confirmation.

Native input routes gameplay before button focus, but preserves path/name editing and modal guards.
Pan uses mouse drag, wheel or fractional `InputEventPanGesture` accumulation.
Ctrl/Command zoom keys accept logical, physical and Unicode forms. Magnify/pinch factors accumulate
logarithmically at a 20% threshold per step. Gesture state resets on keyboard input, dialogs and focus loss.
Dialogs capture otherwise unmatched keys and zoom gestures rather than dispatching gameplay.
Esc opens the city menu during gameplay; it returns to the parent prompt (or closes a root prompt)
and cancels active editing/previews.
Selection drags always edge-scroll; hover edge scrolling is optional.

## Rendering, animation and music

The map uses cached native draw commands, a reusable visible-cell buffer and `QueueRedraw`.
There is no node per glyph and no cell rebuild on every rendered frame. Overlays reuse base visuals.
`TerminalGrid` caches animation eligibility: a salted coordinate hash selects about 10% of hills,
trees and water. Hills move vertically; trees/water horizontally in four held 80 BPM steps.
Directions alternate by world-coordinate parity. Glyph offsets do not change backgrounds/hit targets.
Animation continues during gameplay pause, freezes while unfocused and leaves bridges static.

`CityMinimap` caches the shared `MinimapImage` by game/version. Its image is at most 240 pixels wide
and neither axis exceeds the source map, avoiding empty sampling bands on small maps.
Linear texture filtering smooths scaling. The camera outline/navigation uses the actual view rectangle.
Overlay count/occupancy buffers are reused.

`GreensleevesTrack` synthesizes 54 BPM, 22,050 Hz PCM16 with an FM lead, arpeggio and bass.
The fixed opening lasts about 94 seconds. `GreensleevesSequence` then chooses complete verse/refrain
phrases, related dominant/subdominant keys and bounded timbre/arpeggio variations. All voices transpose
together within one octave; phrases fade to zero at their boundaries.

`AudioStreamGenerator` streams small reusable buffers instead of looping a fixed WAV. A single
background producer fills its one-second ring buffer every 10 ms, independently of map rendering,
zooming and scrolling. It accesses only the audio playback resource, not scene nodes. One future
phrase is synthesized off-thread while the current one plays, with an RNG independent of gameplay.
Full-volume music is -24 dB and placement sounds are -12 dB; smoke tests use -80 dB.
Missing volume preferences default to 70% music and 60% sound without overwriting saved choices.
Esc > Audio controls has independent 0-100% sliders and mute buttons; Esc > Music and Sound
also toggle their respective channels. Preferences are saved alongside font size in `user://display.cfg`.
Older settings without a sound toggle inherit the old shared mute state once.
Music pauses on focus loss, not gameplay pause/dialogs. Worker failures are reported on the main
thread; cancellation and joining precede releasing playback resources at shutdown.
The engine smoke test checks underruns through a 1.5-second render-thread stall and repeated zoom/pan.
No external recording or package is needed.

`AreaOfEffect` supplies service and pollution radii (sharing `CityServices.PollutionRadius`) and
cached pixelated outlines in map-cell space. `TerminalMap` draws faint white rings at every zoom
only for buildings whose footprints intersect the active selection (or cursor block), culls offscreen
ranges, includes valid placement previews and labels multiple effects side by side.
Ring alpha is 0.38; icon alpha is 1.0.
Utilities with citywide supply and no spatial radius do not have a fictitious range circle.

`BuildingType.FootprintArt` defines ASCII pictures at two columns and two rows per map cell.
`FootprintGlyphAt` slices each occupied cell into a two-line tile. `CellRenderer` supplies those
tiles at normal/close zoom, and `TerminalMap.DrawCellGlyph` draws them at half font size within
the original cell bounds, including effect scaling/tints. `MapSnapshot` preserves their footprint
ownership so demolition ghosts retain the art. `BlockSampler` and `--dump-map` explicitly request
the canonical single-line glyph.

The raw bundled DejaVu Sans Mono bytes load directly, without depending on an import cache.
Startup verifies registered map glyph coverage. Export presets include the font and
[DejaVu license](../godot/Assets/DejaVu-LICENSE.txt).

Terminal effects (glyphs that shrink, grow, burn and roam) are a separate engine-independent layer; see
[EFFECTS.md](EFFECTS.md).

## Persistence

`SaveGameStore` writes JSON version 2, atomically via `<path>.tmp` followed by replacement.
Development saves are not backward compatible: other versions, obsolete names, unknown fields,
missing state and unsupported compression are rejected explicitly rather than migrated or defaulted.
Godot supplies `user://quicksave.json` in the distinct `TermCityGodot` user-data directory.
The reusable core's fallback path remains local application data plus `TermCity/quicksave.json`;
the desktop application does not use that fallback.
`GameSession.SaveDisplayPath` abbreviates the home directory for the right-aligned bottom status label.
The label is visible only while a save-confirmation dialog is open (`SessionPrompt.IsSaveDialog`).
It shares a single row with cell information, uses at most half the row width, and exposes
the full absolute path in a tooltip. Save guards do not duplicate the path in prompt footers.

Saves include configuration/scenario, name, cash/calendar, RNG state, speed/pause/taxes, growth plan,
last report, the current week's birth/death/migration/event tally, milestones, guide dismissal,
pending dezone removals, building footprints and all map layers.
Terrain/features/buildings/road types use name palettes. Layers are deflate-compressed and
base64-encoded. Encoding resolves palette indexes once per registered type, not once per cell.
Terrain uses every byte ID, including 255; the empty-cell marker is only used by optional layers.
Load preserves saved configuration and clock timings, rebuilds sparse indexes and starts the session
paused. Medieval state (grain, harvest, hunger, outbreaks, town rank, tribute arrears) is required.
Classic remains an explicit sandbox ruleset, not a fallback for incomplete saves.

Autosave checks every 60 real seconds, rotates three sibling files and never overwrites the
quick-save. Unsaved-progress guards offer save/continue, discard/continue or cancel, and failed
saves block continuation. Failed loads leave the city unchanged and surface an error.

## Rules and extensions

Core tuning is in [GameConfig.cs](../src/TermCity.Core/Simulation/GameConfig.cs):

| Property | Default |
|---|---|
| MapWidth / MapHeight | 160 / 96 |
| Seed / Scenario | 1 / Random; startup picks a seed |
| StartingYear | Current local year captured for new games |
| StartingMoney / RoadCostPerCell | 50,000 / 500 |
| MinResidentialCells | 10 |
| ResidentialPerCommercial / ResidentialPerIndustrial | 20 / 10 |
| DefaultTaxRate / RoadServiceReach | 0.09 / 2 |
| MaxNewResidential/Commercial/IndustrialPerWeek | 3 / 1 / 1 |
| GrowthRatePerWeek | 0.02 |
| WeeksPerYear / DaysPerWeek | 52 / 7 |
| Slow/Medium/FastSecondsPerWeek | 8.4 / 4.2 / 1.4 |

Add registered terrain/feature/building/road types through `GameContent`. Generators implement
their existing ordered interfaces. Each glyph must be covered by the bundled font.
Player-placeable roads/buildings appear automatically in area menus; growth buildings declare
their zone. Named scenarios require the default named Meadow/Hill/Water, Tree and road content,
plus growth buildings for every zone.

## Tests and CI

```bash
dotnet test TermCity.slnx -c Release
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj \
  --filter "FullyQualifiedName~GodotPresentationTests|FullyQualifiedName~CityScenarioTests"
godot --headless --path godot -- --smoke-test --seed 42 --size large
godot --path godot -- --smoke-test --seed 42 --size SF --capture /absolute/path/city.png
```

Unit tests cover economy/growth, calendar, road networks, generation/water, named-city geography,
occupied districts/service, current-format save/restart round trips and invalid-save rejection, sessions/previews/undo/autosave guards,
minimap images, zoom/pixel mapping, animation density/rhythm, options, city names and synthesized music.
Pure Godot helpers are linked into the test project; running unit tests does not require a display.

[GodotSmoke.cs](../godot/GodotSmoke.cs) runs inside the engine and verifies scene layout, native
keyboard/pointer/trackpad/pinch routing, modal focus/errors, name editing, font/sidebar resizing,
menu highlight retention, save/load/restart, minimap dragging, pause/focus animation and streaming
music. It ignores saved display settings and uses an isolated smoke-save directory.
Success prints `TERMCITY_GODOT_SMOKE_OK`; failures log an error and exit nonzero.
Headless tests supply a logical viewport; screenshots require a graphical display.

CI runs on pull requests targeting main. Windows/Linux/macOS jobs build the complete Release
solution and run tests without rebuilding. A Linux Godot .NET job builds the desktop project and
runs headless engine checks. Unit coverage and engine checks are complementary.

## Export

Install Godot 4.7.2 .NET and matching .NET export templates from the
[official release](https://github.com/godotengine/godot/releases/tag/4.7.2-stable).
Presets in [export_presets.cfg](../godot/export_presets.cfg) support Windows x86-64, Linux x86-64
and macOS. Create the destination directories, then:

```bash
godot --headless --path godot --export-release "Windows" exports/windows/termcity-godot.exe
godot --headless --path godot --export-release "Linux" exports/linux/termcity-godot.x86_64
godot --headless --path godot --export-release "macOS" exports/macos/TermCityGodot.zip
```

Export paths are relative to the project directory when not absolute; prefer absolute output paths
if launching from a different working directory. macOS signing/distribution is a separate release step.
Retain the bundled font license in exports.

## Diagnostics and performance

F12 shows the last input and loop-gap measurements in the bottom status line.
`--dump-map` offers a UI-free generator check. Engine checks measure visible-cell rebuild timing
and assert that animation does not rebuild cells or mutate saves.

Profile .NET allocations and Godot native resources when investigating leaks or frame-time regressions.
Sparse indexes, lazy network/statistics caches, reusable map/minimap buffers and independent music
prefetching avoid repeated whole-map work. Keep invalid input and save/audio errors visible rather
than substituting silent success-shaped fallbacks.
