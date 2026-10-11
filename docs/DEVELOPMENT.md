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
| [Main.Construction.cs](../godot/Main.Construction.cs) | Left construction icon rail, category catalogs, statistics/description panels and placement entry |
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
Where roadbeds overlap at a merge, the higher-ranked road retains its pavement and
line colors, including the smooth-min fillet and antialiased outer edge. This prevents a minor branch's smoothing ramp from painting a gray
patch along its highway host; equal-ranked overlaps still use the nearest path.
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
Recurring week-summary messages are not displayed in the bottom bar; completed-week
results remain available in Weekly Report. Cell details, action feedback, important
warnings and save-path metadata retain their normal status displays.
Session saves and autosaves include map camera coordinates and zoom. Loading
restores that view, clamped to the current viewport/map bounds, without following
the old cursor. Older saves without view metadata retain the previous default behavior.
Placement ranges appear only on valid building locations; existing-building
ranges are suppressed during placement so occupied/blocked cells show no coverage preview.
Selected/previewed building ranges use a cached radial texture with a uniform 32%
opacity throughout the defined coverage area. The fade starts at that boundary
and reaches transparency at 125% of the radius. Actual area-service coverage,
pollution and air cleanup share that same falloff: full strength inside the
defined radius, smoothly decreasing to zero across the outer 25% fringe.
Funding still scales service strength, and overlapping services retain diminishing
returns. Pollution cleanup is applied after all sources, independent of map order.
Gameplay falloff measures distance in map-display pixels, accounting for the
12x22 cell aspect ratio. Thus service coverage, pollution and cleanup use the same
screen-space circular boundary and fringe as placement previews, not tall ovals.
Selected buildings show the concise `Active` status in green. Disconnection, missing
Right-clicking any building footprint cell opens a terminal-styled Building Status
table. It shows connection, status, upkeep, capacity and coverage, with Area menu
and Close actions. Granaries also show active storage, city reserves/storage limit,
food consumption, hunger/famine, purchases/import capacity, season and harvest.
Right-clicking empty land retains the usual area menu. Disconnection, missing
Table-dialog footer actions reserve at least 96x36 pixels and their full label
width plus style margins, preventing clipped buttons from collapsing in horizontal rows. Disconnection, missing
Shift-click selection retains the original anchor but replaces its opposite
endpoint, allowing shrinkage or changing direction instead of unioning old bounds. Disconnection, missing
supplies or zero funding show their specific problem in red; reduced funding/service
and scheduled removal show yellow warnings. Current error/warning messages retain
precedence, and Game Menu metadata keeps its neutral color.

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
The map's content margins follow its visible inner borders as they join the header,
construction rail and sidebar. All four visible padding gaps remain equal instead
of measuring a fixed inset from control bounds that differ from joined borders.
The right sidebar also follows its visible inner borders, using the same compact
4-pixel visible-padding treatment as the left construction rail on all four sides.

Undo caches up to ten serialized full-city snapshots for successful placement,
zoning, dezone and demolition actions, evicting the oldest when full. Undo requires
pause and confirmation and preserves older remaining snapshots after each restore.
It restores money, residents, random state and calendar. Failed/cancelled actions
do not consume history; load/new clears it. macOS uses Command+Z and other platforms
use Ctrl+Z; help displays the matching shortcut.
Session prompts/previews suspend simulation. Autosave still tracks elapsed real time and changed cities.

## Godot UI and input

Debug runs start maximized (not fullscreen), except headless runs and smoke tests, which retain their
controlled window sizes. Release runs retain the normal windowed startup. Changing
font size while maximized preserves maximized geometry.
The default window is 1440x900 with a 640x780 base minimum, capped to the usable desktop. Display font size defaults to 20,
supports 16-28, and sets shared content scale `font size / 16`. Window sizing stays within usable
desktop bounds. F3 opens live font controls with OK/RESET.

The top header is one row with uniform base font size 24, fitted on narrow windows. TermCity/name,
date/progress, population and funds have separate double-white frames. The time
section has 12-pixel spacing between its fields; the speed label is omitted.
Funds show the gold amount and weekly balance without a TREASURY label.
The weekly balance uses brackets without `/wk`, e.g. `50,000g [+100g]` or
`50,000g [0g]`; only positive balances have a plus sign.
REPORTS has its own frame between population and funds and opens the weekly report
on click, respecting the same editing/prompt/preview guards as other header links.
POP is display-only and does not open the city menu; its tooltip shows the population.
The icons use shared rendered construction artwork and hover/click pulses rather
than font glyphs, with dedicated clear, pollution, land-value and happiness drawings.
VIEWS opens a horizontal terminal icon rail inside the map, aligned to its top left.
All overlay views retain curved rendered roads; selecting a view never switches
the map back to terminal road glyphs.
Icons select every overlay, including a clear-overlay action. Only clicking VIEWS
O cycles overlays; Shift+O toggles off/on while remembering the last selected
overlay (fuel if none has been chosen yet). Clearing via VIEWS also preserves that choice. Clicking VIEWS again or pressing Esc closes the rail without clearing the overlay.
Outside clicks keep it open. When a dialog is on top, Esc dismisses that dialog first. Below 900 logical pixels,
REPORTS and GUIDE hide to prioritize readable header text; F7/F6 remain available.
The aggregate Services row is omitted from the sidebar; detailed services remain
available in Weekly Report.
Complaint is also omitted: needs become varied medieval petitions at bottom right.
Multiple complaints cycle once per game week in descending severity order and
wrap; the message remains stable while paused.
Needs costing at least 15 happiness points, essential shortages costing at least
5 points, or miserable city happiness show red; milder needs show yellow.
Placement warnings, save metadata and dialogs take priority; full messages are available on hover.
Petitions can use remaining bottom-bar width beyond half the screen while reserving
space for left-side information. Hover shows the full petition and a complaint-specific
remedy, including relevant buildings, road access, funding, taxes or food imports.
While a building catalog is open, the bottom-right message instead shows its
selected service's coverage, supply or grain storage/reserves as a stable label.
Color indicates service level; changing percentages and quantities appear only on
hover. Switching buildings/categories updates the label immediately, without
rewriting it on every UI refresh.
Displayed service level/color and hover details refresh every three Fast weeks,
scaled by configured speed durations to the same real-time interval (one Medium
week or half a Slow week with defaults). Opening a catalog or changing its selected
building refreshes immediately. Paused clocks do not advance the refresh interval.
Gameplay coverage and Add affordability still update normally.
City sidebar table rows open the matching Weekly Report tab when either cell is
clicked: demographics/Mood open Population, Season/Grain open Food & Seasons,
and Fuel/Water/Services/Complaint open Services. Existing dialogs and placement
previews block these links.
Fuel and Water show read-only terminal gauges with white block markers and raw supply/demand
percentages on hover. Their colored bars are 14 pixels thick with no white outline;
the white marker extends two pixels above and below the bar.
Percentages are not capped at 100%. Red is below 95%, yellow is 95%
to below 105%, and green is at least 105%. Tracks span 50-150%; levels at or below
50% pin the marker to the left edge without reserving a lower segment. Higher supply keeps
its exact numeric value while the marker stops at the end. No demand shows 100%.
Fuel/Water cells show only the gauges; percentages and supplied/required units
appear on hover. Gauge clicks retain the row report link.
Grain uses the same gauge with a two-week reserve target (100%). Its tooltip
shows actual reserve weeks, percentage of the target and hunger/famine status.
Clicking Grain continues to open Food & Seasons.
GUIDE sits between REPORTS and funds in its own terminal frame and opens the guide
on click. Below 900 logical pixels of window width its entire frame is hidden and
the other sections reclaim its space; F6 still opens the guide.
Clicking outside an open Guide closes it (or returns to its parent menu), consuming
the click without triggering gameplay underneath. Gold opens economy only when its rendered text is clicked, not when
empty space in its reserved label slot or frame is clicked.
On narrow windows, the gold slot reserves only the width of `99,999g [999g]`
instead of the full large-balance/empty-coffers sample, favoring a larger header
font. Longer values clip with ellipses and remain available in the tooltip.
Header gold amounts above one million use two-decimal millions, e.g. `1.01M`.
Exact gold amounts remain in reports and the header tooltip.
The weekly Reports dialog targets 720 pixels of content width, clamped to the
available map/window area. It uses the shared black/double-white terminal frame,
equal compact padding, wrapped scrollable text, and a bottom-right Close action.
Its summary and zone details are label/value tables using the same
shared single-white grid-cell styles, alternating neutral fills and data colors
as the City sidebar. Values wrap rather than being truncated.
Each table's label column is sized to its longest label so multiword labels stay
on one line, while the value column uses the remaining space.
The main title is `Weekly Report`; each table subheader uses a closed double-white
terminal frame on black, matching the sidebar section headers.
The report retains current settlement size and a green progress bar from the current
achieved size threshold to the next one. Population losses clamp progress at zero
without revoking achieved sizes; Great City shows a full bar and completion text.
Milestone thresholds, unlocks and advice live in the guide's Milestones tab, not in
the report. The growth announcement also provides previous/next milestone browsing.
Weekly Report has Overview, Population, Economy, Services, Growth, Food & Seasons
and Events tabs. Food & Seasons prominently shows famine/food status, reserves,
storage/consumption, merchant grain purchases/prices, harvest quality, current
season and all four seasonal modifiers, plus recent food events.
Shared `ReportContent` supplies both session text and Godot tables from actual city
statistics. Overview includes map/calendar details and size progress. Population
includes demographics, jobs and last-week changes. Economy separates projected
itemized weekly income/expenses from the last completed week's figures. Services
shows supply, health, coverage/funding and complaints; classic rules explicitly omit
unsimulated service data. Growth shows zones and blockers. Events lists the latest
20 recorded events, newest first. Missing history is stated, not fabricated.
Dialog scrollbars use square white-outlined black tracks and gray block thumbs,
highlighting white on hover and yellow while dragged. Native arrow icons are
hidden; wheel, dragging and keyboard scrolling retain their normal behavior.
Dates place
the padded week/feast label before `AD` and the year, e.g. `Week the 17th AD 1289`.
Header text uses reserved slots and fits to window width independently of current
values, preventing movement when weeks, feasts, pause/speed, population or treasury change.
Exceptionally long values are clipped with ellipses; hover shows full date or financial text.
Ordinary weeks display
`Week the 1st` through `Week the 52nd`, with correct ordinal suffixes. In the standard
52-week calendar, fixed medieval English feasts replace the week label: Candlemas (5),
Lady Day (12), May Day (18), Middsommer (25), Lammas (31), Michaelmas (39),
All Hallow's (44), and Christmas (52). These use January-based seven-day week buckets,
not ISO weeks; movable feasts are not modeled. A pixel icon accompanies the date:
a calendar ordinarily, or a candle, flower, maypole, sun, wheat, angel, skull or tree
for the corresponding feast. Custom-length years retain ordinal weeks.
This is presentation only; the simulation calendar is unchanged.
Desktop new maps opt into the current approximate week using January-based
seven-day buckets capped at the year's last week. The date then advances with game
time, so season and feast labels remain consistent. Loaded saves retain their date.
Core callers and smoke/map-dump runs retain week-one startup unless a starting date is supplied.
The day bar comes from `Fmt.WeekBar`. Speed hotkeys still work without a header indicator.
While paused, yellow `[PAUSED]` replaces the week-progress bar in its existing centered
slot; resuming restores the cyan day bar without moving other header fields.
The indicator also reflects clock suspension by any prompt, placement preview or
name editor, without changing the player's manual pause state.
Space toggles pause globally outside text entry, including dialogs and placement.
P remains a menu mnemonic while dialogs are open and toggles pause during gameplay.
Explicit resume allows simulation to run with the current dialog or
preview still open; newly opened dialogs retain automatic suspension. Enter
activates dialog buttons (Space is reserved for pause). Ctrl+P, or Command+P on
macOS, toggles pause even while typing; plain P/Space still enter text.
No pause dialog, facts or artwork are instantiated, leaving the map and controls
unobstructed. Space/P toggles pause; Escape opens Game Menu without changing pause.
The former dialog/animation implementation remains inactive for possible later use.
Top-bar frames use 4-pixel visible top/bottom insets and vertically centered text.
Their layout margins follow the actual inner border positions, including shared
bottom joins, rather than centering against the container rectangle.
There is no shortcut toolbar. Function-key actions remain available through keyboard input.
All modal dialogs, including save-before-quit/continue confirmations, use `TerminalFrame` for
the black background, two white border lines and shared 12-pixel inset.
Popup frames disable adjacency joining so their borders stay closed and do not
extend into neighboring headers. The game menu uses an all-caps GAME MENU title
and option/action labels, with existing keyboard mnemonics and no description text.
The modal's local Button theme gives every clickable action a single square white
outline in all states, including tabs and save controls. The green construction
Add button uses green text on a gray fill and the same one-pixel outline.
Cancel button text is centered; construction Cancel buttons match Add in width.
Save-confirmation action text is centered. Save and quit is green; Quit without
saving is red. Descriptive text is omitted, while the title and errors remain.
Godot debug builds quit immediately without confirmation. Release builds retain
the save confirmation; smoke tests explicitly retain it to validate that flow.
Construction actions have equal space above and below, with additional top padding.
Dialogs reserve the construction rail's width. Construction catalogs anchor at
the map's top-left; other dialogs center on the map, clamped to the available
window area when too large to fit inside the map.
The game menu targets a compact 420-pixel content width.
Its bottom-left SAVE button (underlined S shortcut) writes the existing quick-save file and
keeps the menu open. Save errors remain visible through the normal modal error display.
While it is open, the bottom status row shows seed and map dimensions on the left
and the quick-save path on the right, clipped without overlap with the full path
available as a tooltip. Closing the menu restores the usual gameplay status.
Its content uses equal padding on all sides: the shared 12-pixel frame inset plus
2 pixels, matching the existing top/bottom spacing rather than adding wider side gutters.
Music and Sound volume sliders and mute buttons appear directly below the display
options, without a separate audio dialog or increasing the game menu's width.
Each channel has a single-white border with 4-pixel padding and an uppercase label.
Volume sliders share a row with their mute buttons and omit percentage readouts.
MUSIC and SOUND labels occupy separate single-white bordered cells in those same rows.
Each slider has 12 pixels of additional left/right padding to separate its track
and block handle from the channel frame and mute button.
Slider tracks use square, single-white outlines on dark fills and crisp white block
handles instead of rounded native styling, retaining native pointer and keyboard input.
Mute buttons show pixel-stepped white speakers: unmuted channels have two sound-wave
lines, while muted channels show only the speaker, without a slash. M and U toggle music and sound.
The game menu has no separate column-header row or frame.
Effects and Celebrations are omitted from the game menu; the effects keyboard shortcut remains available.
The NEW MAP button to the right of the bottom-left SAVE action uses green text and the existing unsaved-progress
guard before creating a fresh random map of the same configured size.
Back to game and Quit are not menu rows. Bottom-right Quit and Cancel buttons use
the same action layout as construction catalogs, with red Quit text. Quit opens
the existing confirmation; Cancel closes the game menu. Q and C activate Quit and
Cancel respectively, with their initial letters underlined. City management is
not a game menu option; city-action keyboard shortcuts remain available.
Cancel receives focus whenever
the game menu opens or returns from a submenu, with a subtle blue-gray focus fill
that remains visible when the pointer moves away. Keyboard navigation can still focus other actions.
Titles and section headers use 24-point text with compact vertical frame insets.
Catalog choices use 22-point names and separate 30-point icons, with single-white
name borders and one enclosing single-white border around the list.
Buildings with a capacity also show it directly beneath their choice name in
14-point text and near the top of the details table, after cost and upkeep.
Capacity units distinguish fuel, water, grain measures, residents and jobs.
Area-service buildings without a capacity retain their radius/strength information.
Starter fire, sheriff, physic and learning services unlock at Hamlet (100 souls);
higher-tier service gates remain unchanged. Locked catalog choices show a red
padlock to the right of the name. Unlock details show the milestone name and
remaining population, e.g. `Hamlet (40 souls to go)`; permanent unlocks show zero.
Village Green requires Village (500 souls); Tavern requires Market Town (1,000 souls).
Construction Add is disabled for both locked buildings and insufficient base-cost
funds and refreshes while the submenu stays open, including after income, expenses
or unlock changes. Selection and focus are preserved; disabled Add text is gray,
and affordable/unlocked Add text is green.
The shared session purchase guard also rejects keyboard/direct preview
entry, retaining the menu and showing required/current gold. Terrain-dependent
placement costs remain checked at the chosen location.
Every catalog uses the same name-column width, sized to the longest placeable
building or road name across all categories. The duplicate
selected-building heading is omitted.
At minimum window widths, detail labels and descriptions can wrap within words
to preserve this shared name width without overflowing the window.
Details table cells use uniform one-pixel white lines, drawing shared edges only
once. Cell padding is 6 pixels horizontally and 3 vertically.
An outer single-white description box encloses the text without a description header.
Description text uses 20 points; the box reaches at least the first name row's bottom
and grows for wrapped text.
Catalog category headers also use a single-white border; the main dialog retains its double border.
Enclosing catalog boxes use even 4-pixel border insets, matching the icon rail.
Stacked catalog sections and names use 3-pixel separation for 4-pixel line-to-line spacing.
The name list fills the catalog height and shares its right border with the description
and details table, forming connected intersections without adjacent duplicate vertical lines.
The text body has padding but no separate border.
Each newly achieved milestone awards a one-time gold grant based on the two
cheapest newly unlocked buildings, with a 5g-per-threshold-soul floor and grants
never decreasing at higher milestones. Skipped milestones each pay their grant;
the persisted highest milestone prevents repeat payment after decline or load.
Grants appear in the announcement and guide and do not count as recurring tax income.
Your City Grew! announcements explicitly pause the game and target 720 pixels of
content width, clamped to available space. The reached milestone is selected, with
tables of newly unlocked buildings, summaries, costs/upkeep, footprints and key
capacity/coverage features. Previous/Next browse milestones and disable at the ends;
Continue closes the dialog but leaves the clock paused. Placement feedback does
not overwrite a newly raised milestone banner.
The growth title is centered yellow, the city-size heading is green, and New Services
precedes the unlocked building tables. Left cells are blue and right descriptions
are gray.
The city-size and New Services headings use matching 24-point text, with the city
size centered. Each building section shows its footprint artwork (or fallback
icon) in a bordered cell to the right of its feature table.
The shared square terminal scrollbar is reused; the modal and scroll
viewport clip overflowing content to prevent frame/text drawing outside the dialog.
Area effects are written in one combined Effect row, including service coverage,
clean air, pollution and their radii, alongside other effect text.
Descriptions are unchanged; there is no separate area-icon row or hover icons.
Each effect shows its bounding width x height in cells (2 x radius + 1 on each
axis); the simulated area remains circular. Upkeep omits the funding suffix, and
the redundant Operation row is omitted.
Buildings without an actual area of effect omit the Effect row entirely; capacities
and other non-area details remain visible.
Catalog descriptions appear above statistics/effects at every window size, with
both sections filling the information column. The statistics/effects heading is
omitted. Road details also omit Needs. Details omit Does and Placement, show Cost as only the gold amount,
omit "cells" from Footprint, label area icons "Area of Effect", and capitalize
values beginning with letters. Cost comes first, followed by Upkeep.
Dialogs never cover the construction icons.
Adjacent terminal frames share one double border through the midpoint of their gap.
At three-way intersections, the upper horizontal line continues straight. The lower line
breaks between the vertical stem's two lines and turns downward instead of crossing the branch.
At the bottom edge, vertical stems stop at the inner horizontal line so they do not cross
the double-border channel. At the left and right window edges, the outer vertical
line stays continuous and horizontal branches stop at the inner square corners.
Only the map/city-sidebar drag handle shows a resize cursor;
the construction icon rail is fixed-width with a 4-pixel outline-to-outline inset,
matching the spacing between its double-border lines, not the container rectangle.
Individual icon frames use a compact 6-pixel inset, 40-pixel square buttons and
24-point glyphs. The 3-pixel container separation gives stacked icon outlines the
same 4-pixel line spacing, so every category fits the minimum window height without a scrollbar.
`ConstructionIcon` draws shaded illustrations with up to four palette colors inside
the existing buttons: a warm-toned isometric road with two shaded lanes and a dashed
center divider (no curbs, wheel tracks or tile pattern), water droplet, campfire,
Fire Watch tower with a red warning flag,
sheriff star, health cross, open book, tree, shield, chapel, market stall and wheat.
The trade stall has a red-and-cream striped awning.
Granary wheat uses rounded kernels on thin stalks rather than pointed chevrons.
Education has cream pages and a red bookmark; commons has a brown trunk with
shaded green foliage; granary has golden kernels, green stalks and a brown tie.
Faith has cream walls, a red roof and a blue window.
Hover and click trigger a short pulse in the artwork's main color.
Hover also rocks only the artwork left, right, then upright in three 0.25-second motions,
starting with the top tilting left, then returning upright. This works while a
construction catalog is open as well as during normal gameplay.
Each icon has one white outline, not another double-line frame. The rail's outer
border participates in the shared double-line layout coming down from the header.
Icon buttons have square bounds and zero additional theme-style margins inside
their equally padded outlines.
Border joins refresh with layout and visibility changes. The rail opts into
`VisibleContentPadding`, adjusting its layout margins to preserve equal visible
spacing when shared borders extend beyond its container rectangle.
Hover tooltips use the same treatment through the inherited `TooltipPanel` theme:
`TerminalFrame.CreateTooltipStyle` supplies a nine-slice texture so borders retain
their spacing as tooltip text changes size. This convention is required for all
future menus, dialogs and popups by the repository's Copilot instructions.
`GameSession.HasCursor` starts false: map highlights and selected service ranges remain hidden
until explicit targeting or placement, while the status line shows `Press F1 for HELP`.
Camera movement and zoom do not activate the cursor.

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
The minimap has no section header; its zoom controls sit directly above it.
CITY and DEMAND headings retain larger 22-point text while the sidebar fits.
The zoom section has a single square white border on black with a 1-pixel inset
on all sides, leaving no extra gutter beyond the border.
The centered zoom-level text retains a 20-point size during sidebar fitting.
Its buttons retain centered 22-point symbols, with equal 2-pixel text padding on
all four sides and no forced minimum width or height, retained
when the sidebar adapts to narrow or short windows.
Demand letters use vertically centered 18-point text. Their bars retain matching
18-pixel heights, centered alongside the labels during sidebar fitting.
Its body text targets 18 points, fitting down to 13 points when sidebar height is constrained.
The City table uses a continuous square terminal grid, with single shared lines and
no gaps between cells. White borders and alternating black/dark-gray fills match
the terminal interface. Existing label/value colors are preserved in both the normal
and paired-column compact layout.
Width/height/row-count caching avoids redundant fitting. Screen-constrained layouts preserve the
minimap's shape and fit the largest overview that leaves the remaining details visible.
The Guide sidebar section is removed; optional next-step coaching is on the F6 Start tab, and its
existing dismiss/enable controls and saved preference still work there.
`CitySplit` supports a 12-pixel drag area, 280-pixel sidebar minimum
and 480-pixel maximum, reduced to retain 320 pixels for the map where possible.
The game menu size rows use white text and inline -/RESET/+ buttons. RESET restores
the corresponding default font size or sidebar width. Their labels still
open the separate adjustment dialogs. Font size uses 2-point steps; sidebar width uses 12-pixel steps.
RESET buttons use 16-pixel horizontal and 10-pixel vertical internal padding in all states.
Inline -/+ buttons have 48x44 logical-pixel minimum targets and centered 28-point symbols.
Arrows navigate dialog buttons; Space/Enter activates the focused button. Text-field dialogs retain
native cursor navigation and sliders retain native left/right arrow editing.
Same-menu refreshes preserve the highlighted choice.
Game Menu Tab/Shift+Tab cycles through every button and slider in display order,
wrapping within the menu. Arrows navigate controls; focused sliders use left/right
to adjust volume and up/down to leave the slider. Button focus uses a blue-gray fill;
focused/hovered sliders use a yellow block handle and brighter track fill.
`MenuShortcuts` assigns unique letter/modifier pairs, preferring first
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
Esc opens the configuration-focused Game menu during gameplay; keyboard shortcuts retain access
to save/load, budget, reports and restart. Esc returns to the parent prompt (or closes a root prompt)
and cancels active editing/previews.
Selection drags always edge-scroll; hover edge scrolling is optional.

## Rendering, animation and music

The map uses cached native draw commands, a reusable visible-cell buffer and `QueueRedraw`.
There is no node per glyph and no cell rebuild on every rendered frame. Overlays reuse base visuals.
`TerminalGrid` caches animation eligibility: a salted coordinate hash selects about 10% of hills,
trees and water. Hills move vertically; trees/water horizontally in four held 80 BPM steps.
Directions alternate by world-coordinate parity. Glyph offsets do not change backgrounds/hit targets.
Animation continues during gameplay pause, freezes while unfocused and leaves bridges static.
Successful session placements are explicitly distinguished from wholesale map
rewrites, so zoning more than 1,500 visible cells still animates instead of resyncing.
Large zone ripples sample up to 600 cells across the entire changed area. Wholesale
loads/new games still resync silently, and disabled/reduced-motion effects stay disabled.

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
| StartingYear | Current local year minus 800, captured for new games |
| StartingMoney / RoadCostPerCell | 50,000 / 500 |
| MinResidentialCells | 10 |
| ResidentialPerCommercial / ResidentialPerIndustrial | 20 / 10 |
| DefaultTaxRate / RoadServiceReach | 0.09 / 2 |
| MaxNewResidential/Commercial/IndustrialPerWeek | 3 / 1 / 1 |
| GrowthRatePerWeek | 0.02 |
| WeeksPerYear / DaysPerWeek | 52 / 7 |
| Slow/Medium/FastSecondsPerWeek | 16.8 / 8.4 / 2.8 |

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
