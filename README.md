# TermCity

A DOS-terminal-styled city builder built with **Godot .NET and C# on .NET 10**, set in the **Middle Ages**.
Raise a hamlet into a chartered city: lay out tracks and burgage plots, build a lord's castle, feed your people through
the farming year, and keep the crown's reeve paid. It runs in a desktop window on Windows, macOS, or Linux.
Godot is the game's only frontend; no interactive console is required.

For architecture, extension points, builds, exports, and tests, see the
[developer guide](docs/DEVELOPMENT.md).

## Quick start

Install **Godot 4.7.2 .NET** (not the standard edition) and the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```bash
git clone https://github.com/devopsjesus/termCity.git
cd termCity
dotnet build TermCity.slnx
godot --path godot -- --seed 42 --size medium
```

Replace `godot` with your Godot .NET executable's full path if it is not on `PATH`.
On macOS, a typical path is `/Applications/Godot_mono.app/Contents/MacOS/godot`.
The bundled map font loads directly from the project, without requiring an existing import cache.

On Windows, Compatibility rendering uses **ANGLE / Direct3D11** to reduce native OpenGL resize
stalls without switching to Vulkan. macOS and Linux retain their existing rendering drivers.
To explicitly fall back to native OpenGL, put `--rendering-driver opengl3` before the game's `--` separator:

```powershell
godot --path godot --rendering-driver opengl3 -- --seed 42
```

Rapid live-edge resizing can still hang the game on some Windows systems, retaining mouse capture
until another application gains focus. Scripted resize checks do not establish live-drag stability.
Until this is resolved, avoid rapid edge dragging; native OpenGL is an alternative but may resize slowly.

### Startup options

Game options follow Godot's `--` separator:

| Option | Behavior |
|---|---|
| `--seed <n>` | Reproducible signed 32-bit seed; random by default |
| `--size <size>` | `small`, `medium`, `large`, `SF`, `LA`, `SD`, `CHI`, `STL`, or `WIDTHxHEIGHT`; default `small` |
| `--load [file]` | Load a city; without a path, use the quick-save |
| `--fps <n>` | Render frame cap, 5-60; default 30 |
| `--reduced-motion` | Disable all terminal effects (sprites, shakes, ambient life) |
| `--dump-map` | Print the generated/loaded map as text and exit; works headlessly |
| `-h`, `--help` | Print game options and exit; works headlessly |

```bash
godot --path godot -- --size SF --seed 42
godot --path godot -- --load /absolute/path/to/quicksave.json
godot --headless --path godot -- --size CHI --seed 42 --dump-map
```

A command-line load failure exits with an error. In-game load failures keep the current city and
leave a visible error in the dialog. When loading, the saved map/configuration overrides seed and size.

## How to play

Random cities start paused with a next-step first-city guide. Connect a short track to an existing highway or a map
edge and zone 20-30 nearby homes with **R**, leaving open service plots. **Before resuming with P**,
build a road-connected **Woodlot (8,000g)** and **Town Well (4,000g)** through Enter > Services.
Together they cost 12,000g and 60g/week, leaving 38,000g of the starting 50,000g before road costs.
A connected road serves land within
two cells; water blocks service from spreading across it. Disconnected roads are red.

At 10 occupied homes, zone shops and workshops with **C/I**. Put smoky workshops in a separate
district, not among homes. Save for the largest happiness complaint rather than buying every service
at once. Budget can reduce local-service funding to 75% (about 84% effectiveness). Keep at least
8,000g for replacing a lost Woodlot, plus reserves for winter grain, tribute and expansion. Extend
short tracks when districts run out of room; empty zoned plots can be dezoned with **U** for services.

### City Grew! milestones

These achievements use the **highest population reached**, not the current population. Each new
city size opens a **City Grew!** dialog that pauses simulation, names everything newly available
and gives advice. **F7** lists reached and upcoming gates. Falling population does not relock buildings.

| Population | Milestone | Newly available |
|---|---|---|
| 100 | Hamlet | Tavern |
| 500 | Village | Stone Keep, Hospice, Parish Church; level-2 redevelopment |
| 1,000 | Market Town | Gaol, Infirmary, Monastery, Guildhall |
| 2,500 | Borough | Castle |
| 5,000 | City | Cathedral; level-3 redevelopment |
| 10,000 | Great City | Final achievement; all city-size gates open |

Density still requires suitable land value, supplies, road class and a lord's seat (a motte for
level 2; a keep and cobbles for level 3). Smaller essential services remain available from the start.
Service menus show locked buildings and their thresholds; previews and placement enforce the same gates.
**F6 > Dismiss tip** switches off coaching, **not milestones or unlock gates**.
**F6 > Enable guide** turns coaching back on. Achievements and the guide preference survive save/load.
For repeated small/medium/large playthrough results and balance refinements, see
[the playtest report](docs/PLAYTEST.md).

Press **F6** (or Esc > Guide) for the GUIDE, a tabbed dialog (Start, Zones, Roads, Services, Population, Happiness,
Economy; Left/Right or a click switches tab) that explains how each thing you place drives the town's population.
**F1** is only the short table of controls. Placing a zone, road or building makes a soft click-clack keyboard sound
(it is silenced with **Esc > Sound**, and when the window is unfocused).
**Esc > Audio controls** provides separate Music and Sound volume sliders (0-100%) and mute buttons.
New audio preferences default to 70% music and 60% sound; existing saved volume choices are retained.
Muting retains each volume setting; the controls are saved between runs. Music continues during
gameplay pauses and dialogs, but pauses when the window loses focus.
Population milestones can trigger decorative confetti bursts, not damage or disasters.
They are off by default; **Esc > Celebrations** toggles them independently of other visual effects.

### Services, budget and the full city engine

New games run the full rules: homes and businesses need **power and water** (build plants, pumps and towers), residents
want a **fire watch, a sheriff, an apothecary, a chantry school and a village green** as the town grows, and a lord's
**castle** to keep the peace. People must be fed: the harvest sets the grain in store, a bad year brings famine, and
plague, raiders and fires are real dangers. Jobs matter and the treasury (in gold) has to balance, with the crown's
tribute due each Michaelmas. Open **City menu > Budget, taxes and loans** to set service funding, tithes and loans, and
**City health report** for every indicator. Details and tuning are in [docs/POPULATION.md](docs/POPULATION.md); the setting,
the service mapping and ideas for the future are in [docs/MEDIEVAL.md](docs/MEDIEVAL.md).

### Zones and growth

- **Residential (R):** households build homes and add adults, children, and seniors to the population.
- **Commercial (C):** shops and offices become available after 10 occupied residential cells;
  roughly one commercial cell is supported per 20 homes.
- **Industrial (I):** factories and workshops; roughly one industrial cell per 10 homes.
- Zoning is free. Growth needs vacant, road-served lots. Weekly growth caps increase with city size.
- **U** dezones land without immediately removing occupied buildings. Those buildings keep residents
  and tax income for 2-3 game weeks. Restore their original zone before the deadline to keep them.
- **D/Delete** previews demolition. It is free, with no refunds.

Roads cost money: a street starts at **500g per cell**, an avenue at **900g**, and a highway at
**1,500g**. Upgrades charge the difference; hills multiply construction costs by 1.5.
Roads generated with the map have no weekly upkeep. Roads the player builds cost 1g, 2g, or 3g
per cell each week at full funding for dirt tracks, cobbled roads, and King's Roads respectively.
Road/building menus include registered player-placeable types. Service menus show each building's
footprint: a Sheriff's Hall occupies 2x1 cells, a Parish Church 2x2, and a Castle 4x3.
Larger buildings fill their footprint with type-specific ASCII art at normal and close zoom,
including roofs, walls, church crosses, aqueduct arches and castle battlements.
Coarse zoom uses the representative building glyph.
A click previews a whole building; larger selections tile complete, non-overlapping buildings.
Blocked footprints are skipped as a whole. Cost, upkeep and service supply are per building,
not per occupied cell; demolition of any part removes the entire building.

Selected area services and smoke/clean-air sources show faint white pixelated range circles,
including during valid placement previews. Selecting any cell of a building's footprint shows
its ranges; moving the selection away hides them. Their radii match the simulation's reach and scale with zoom.
The rings are translucent white; fully opaque glyphs beneath each source's outermost circle
identify its effects, side by side for multiple effects.
Terminal cells are taller than they are wide, so a circular cell-distance range appears elliptical.

Placement previews show valid/skipped cells and the price. **Enter/Y** confirms and **Esc/N**
cancels; insufficient funds leave the city unchanged. **T** starts a straight street line:
arrows or dragging move its endpoint while the preview remains active.

Undo restores a **whole-city snapshot**, not just the selected cells. It requires confirmation
and is available while paused or for one game week after the action. Cancelled/failed actions do
not replace the undo slot. New/load clears it.

### Calendar and economy

New cities begin in the current local year. Saves retain their starting year.
A year has 52 weeks and a week has 7 days.
Growth is spread over the week and tax income arrives at its end.

The day bar uses `>` for today and `=` for completed days: `[>......]`, `[==>....]`, `[======>]`.

| Speed | Key | Real seconds per game week |
|---|---|---|
| Slow | `1` | 8.4 |
| Medium | `2` | 4.2 |
| Fast | `3` | 1.4 |

Choosing a speed resumes the simulation. `P`/Space pauses or resumes it. Dialogs and placement
previews suspend gameplay; losing window focus stops updates. Returning after a stall does not
fast-forward the simulation.

## Interface

The interface uses a black background and shared/intersecting double-white borders.
Top-header text is uniformly larger than body text and fits to narrower windows:

- **Left:** TermCity and the right-aligned city name.
- **Middle:** left-aligned year/Week, centered day bar, and right-aligned clock status.
- **Right:** left-aligned population and right-aligned budget; depleted funds are red.

PAUSED is yellow with a gentle four-second brightness/glow pulse, frozen while unfocused.
The sidebar has an enlarged minimap, `[-] 1x [+]` zoom controls, and always-open Demand, City, and
Zones sections with larger blue headings. Demand letters match their bar colors.
Cell details appear in the bottom status line, alongside dimensions/counts for selections larger
than one cell and any current message.

About 10% of visible hills, trees, and water animate at 80 BPM (one step every 0.75 seconds).
Hills move vertically; trees and water move horizontally. The chosen tiles remain stable while
scrolling, adjacent tiles move oppositely, and only glyphs move: backgrounds and hit targets stay fixed.
Bridges do not animate. Terrain decoration continues while gameplay is paused; cars, pedestrians,
and birds (including wing animation) pause and resume with the game. All decoration stops while unfocused.

### City name

**Double-click** the city name to rename it, up to **16 characters**. A full-character blinking
block caret appears only while editing; the text remains right-aligned, with backspacing shifting
the remaining text toward the stationary cursor. A reserved character cell keeps the full block
visible at the right edge. TermCity and the header geometry stay fixed.

Delete/Backspace removes characters. Ctrl+A/Command+A selects all. Enter or clicking outside commits;
Esc cancels. Editing pauses the game and restores its previous clock state afterwards.
Names are stored in city saves.

### Font, sidebar, and menus

- **F3 FONT** opens `[-] size [+]` with **OK** and **RESET**. Default interface size is 20;
  supported sizes are 16-28, with map spacing and input scaled consistently.
- Increasing font size grows the preferred 1440x900 window when desktop space permits, without
  exceeding the usable screen. Panels scroll and the function header wraps on smaller screens.
- Drag the map/sidebar divider to resize it, or use **Esc > Resize sidebar**.
  Sidebar widths are bounded to 280-480 logical pixels; the maximum reduces to preserve map space.
- **Arrows navigate dialog controls**, without changing values. **Space/Enter** activates the
  focused button; minus/plus resize the sidebar by one logical character step.
- Menu choices are left-aligned. Dialog width fits its longest choice/title with **five
  character-widths** of padding on each side, capped to the window. Inner vertical padding is
  **2 pixels**, in addition to the frame inset. Long content wraps/scrolls and dialogs stay centered.
- Changing an option keeps that item highlighted, including repeated music toggles.
- Options are unnumbered. Press the **underlined letter** to select an option: first letters are
  preferred, with another letter used for collisions. Large menus may show **[Shift]** beside an
  option; use Shift plus its underlined letter.
- Dialogs capture keyboard input so gameplay shortcuts cannot run behind them. Text fields retain
  normal editing and volume sliders retain their native arrow controls.
- **Esc**, **Cancel**, and **Back** return to the previous menu and restore its highlighted option.
  Cancelling a menu-launched placement preview returns to the originating submenu; completing it
  returns to the city. Esc closes a root menu.

### Music

Quiet **54 BPM Greensleeves** uses an original FM/chiptune arrangement of the traditional
public-domain melody. The opening theme is always the same. Later complete verse/refrain
phrases modulate to related keys and vary their voicing and arpeggios, rather than replaying a fixed loop.

Use **F10** or **Esc > Music: ON/OFF** to mute or resume it. Music continues during game pauses/dialogs but
pauses when unfocused. Font and music preferences are remembered in `display.cfg`, separately from
city saves. No external recording, audio asset, or audio package is required.

## Controls

| Action | Input |
|---|---|
| Move cursor | Arrow keys |
| Jump a screen | Ctrl+arrows, Home/End/PageUp/PageDown |
| Extend selection | Shift+arrows, or Shift+click (grows the selection to whole rows and columns up to the clicked cell) |
| Keyboard selection mode | `S`, move, `S` to finish |
| Pan | Left-drag, middle-drag, wheel, two-finger trackpad scroll |
| Select with mouse | Left-drag; Shift+click extends; Ctrl/Alt+left-drag starts a new box |
| Area menu | Right-click, Enter, or `M` |
| Zone homes / shops / factories | `R` / `C` / `I` |
| Dezone | `U` |
| Road preview / straight-line tool | `B` / `T` |
| Demolish preview | `D` / Delete |
| Confirm / cancel preview | Enter/Y / Esc/N |
| Undo | Ctrl+Z or Command+Z |
| Pause/resume / speed | Space/P / `1`, `2`, `3` |
| Zoom / reset zoom | `+` or `=`, `-`, / `0` |
| Mouse/trackpad zoom | Ctrl/Command+wheel or scroll; pinch |
| Horizontal wheel/trackpad pan | Shift/Alt modifier; tilt wheels |
| Minimap navigation | Click/drag |
| Edge scrolling | `E` toggles hover scrolling; selection drags always edge-scroll |
| Help / font settings | F1 or `?` / F3 |
| Terminal effects high / low / off | `V` (or Esc > Effects) |
| Mute / unmute music | F10 |
| Quick-save / quick-load | F5 / F9 |
| Guide (tabs: how to play, population, economy) | F6 |
| Weekly report / growth report | F7 / F8 |
| City menu | Esc during normal gameplay |
| Input/loop diagnostics | F12 (keyboard-only) |
| Quit | `Q`, Ctrl/Command+Q, or close the window |

Ctrl/Command zoom shortcuts are accepted. Pinch and fractional two-finger gestures preserve the
pointer's map anchor. Close dialogs or finish name/path editing before using gameplay zoom.
Esc returns to the previous menu, closes a root dialog, or cancels an edit/preview instead of
opening another menu.

Zoom levels are **0.25x, 0.5x, 1x, and 2x**. Coarse zoom samples 4x4 or 2x2 map-cell blocks;
2x doubles both tile and glyph dimensions, preserving the normal ratio and one-cell selection.

## Maps

| Preset | Cells | Description |
|---|---|---|
| `small` | 160x96 | Default random map |
| `medium` | 320x192 | Random map |
| `large` | 640x384 | Random map |
| `SF` | 640x384 | San Francisco: a walled peninsula on the strait |
| `LA` | 640x384 | Los Angeles: a sprawling coastal bay town |
| `SD` | 640x384 | San Diego: a harbour town on dry, burnable hills |
| `CHI` | 640x384 | Chicago: a cold lakeside freight town |
| `STL` | 640x384 | St. Louis: a flood-prone river town |

Custom sizes range from 80x24 to 640x384. Named presets are case-insensitive.
Random maps include hills, forests, water systems, existing bridges and highways/interchanges. Highways run
straight out of each interchange, then at any angle (not just right angles) between them, and bridges stay straight.
Every road type (track, road, avenue, highway and any added later) is drawn as smooth curves that round every bend and
merge into junctions with fillets, deliberately breaking out of the glyph grid like the terminal effects do. The road
bed is opaque: no terrain, trees or water show inside the lines, while the terrain background stays outside them.
Roads never run side by side: a new road meets another only end-on or at a crossing, and junctions keep a cell apart, and generated maps are cleaned of 2x2 road blocks and tiny fragments.
At a junction the highest-ranked road runs whole through it, and a road that meets another at a slant curves in alongside it like a slip road.
Zooming out keeps the smoothed roads but much thinner and fainter with each step (side streets drop out at the widest zooms), so they stay a backdrop to the terrain, while highways keep a thin double yellow line; the minimap and map overlays stay pixelated with box glyphs.
Cars, walkers and birds are deliberately slow, and cars and walkers follow the drawn curve, including angled roads.
Fish leap from the water and whales surface (with a spout) in the open sea only; rivers, lakes and bays get leaping fish and the occasional rise of bubbles, but never whales.

City scenarios are **stylized, north-up regional approximations**, not current land-use datasets
or street-accurate GIS maps. They include occupied R/C/I districts, residents, roads and tax income:

- **SF:** the peninsula city on the strait: sea walls, hills, harbours and islands, with
  dense houses, a market quarter, a southeastern port and populated suburbs around it.
- **LA:** a broad coast and bay, a river, ranges behind it, a wide scatter of districts.
- **SD:** two bays, hills and a green hill park; water is scarce and fires run in dry hills.
- **CHI:** a lake and its river branches, flat ground, a market quarter and busy yards.
- **STL:** rivers and a floodplain, a minster park and riverside trades.

Saves are development-version-specific. Only the current format and type names are supported;
obsolete or incomplete saves show a load error rather than being migrated.

Scenarios start paused without the automatic first-city guide; F6 still offers it explicitly.
Geography and districts stay fixed across seeds; seeds vary households and future growth.
Save/load preserves the map. Restart/new-city actions retain the selected scenario.

## Saving and loading

**F5** writes a quick-save; **F9** requests a load. Unsaved progress is guarded before loading,
starting another city, or quitting: save and continue, continue without saving, or cancel.
A failed save prevents the guarded action.
The quick-save path stays at the **bottom right**, on the same row as cell information, rather than
inside confirmation dialogs. Long text is clipped to keep both sides visible; hover for the full path.

Godot stores files in its `TermCityGodot` user-data directory:

| OS | Typical quick-save path |
|---|---|
| Windows | `%APPDATA%\TermCityGodot\quicksave.json` |
| macOS | `~/Library/Application Support/TermCityGodot/quicksave.json` |
| Linux | `~/.local/share/TermCityGodot/quicksave.json` |

**Autosave** checks every 60 real seconds and saves changed cities, including while paused.
Three files rotate beside the quick-save: `quicksave.autosave1.json` (newest), then slots 2 and 3.
They do not overwrite the quick-save or dismiss unsaved-progress guards.
Use **Esc > Load city** to recover an autosave or enter a file path.

Saves preserve map layers, city name, scenario/configuration, money/calendar, speed, random state,
in-progress growth, recent report, milestones, guide preference, taxes, and pending dezone removals.
Loaded cities start paused. Save failures appear as errors; autosave retries at the next interval.

## Troubleshooting

- **C# scripts do not run:** use Godot **4.7.2 .NET**, install .NET 10, and build the solution before launching.
- **Save/load error:** check the path and permissions. Failed loads do not replace the current city.
- **Zoom keys do nothing:** close modal dialogs and finish text editing. Use the minimap zoom buttons
  or pinch/Ctrl/Command gestures; `+`, `=`, `-`, and `0` also work.
- **Text is too small:** open F3 FONT. The top header uses a uniform larger font, fitted on narrow windows.
- **Input issue:** press F12 and include the input/loop diagnostics in the bug report.
- **No sound:** check **Esc > Music**, OS output volume, and whether the window is focused.

## Development and license

```bash
dotnet build TermCity.slnx -c Release
dotnet test TermCity.slnx -c Release --no-build
```

Engine integration tests, export commands and extension examples are in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
TermCity is MIT licensed; see [LICENSE](LICENSE). The bundled DejaVu font retains its
[license](godot/Assets/DejaVu-LICENSE.txt).
