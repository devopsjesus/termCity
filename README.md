# TermCity

A DOS-terminal-styled city builder built with **Godot .NET and C# on .NET 10**.
Zone land, connect streets, and grow a city in a desktop window on Windows, macOS, or Linux.
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

### Startup options

Game options follow Godot's `--` separator:

| Option | Behavior |
|---|---|
| `--seed <n>` | Reproducible signed 32-bit seed; random by default |
| `--size <size>` | `small`, `medium`, `large`, `SF`, `LA`, `SD`, `CHI`, `STL`, or `WIDTHxHEIGHT`; default `small` |
| `--load [file]` | Load a city; without a path, use the quick-save |
| `--fps <n>` | Render frame cap, 5-60; default 30 |
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

Random cities start paused with a first-city guide. Connect roads to an existing highway or a map
edge, zone nearby homes with **R**, and resume with **P**. A connected road serves land within
two cells; water blocks service from spreading across it. Disconnected roads are amber.

### Zones and growth

- **Residential (R):** households build homes and add adults, children, and seniors to the population.
- **Commercial (C):** shops and offices become available after 10 occupied residential cells;
  roughly one commercial cell is supported per 20 homes.
- **Industrial (I):** factories and workshops; roughly one industrial cell per 10 homes.
- Zoning is free. Growth needs vacant, road-served lots. Weekly growth caps increase with city size.
- **U** dezones land without immediately removing occupied buildings. Those buildings keep residents
  and tax income for 2-3 game weeks. Restore their original zone before the deadline to keep them.
- **D/Delete** previews demolition. It is free, with no refunds.

Roads cost money: a street starts at **$500 per cell**, an avenue at **$900**, and a highway at
**$1,500**. Upgrades charge the difference; hills multiply construction costs by 1.5.
Road/building menus include registered player-placeable types. Default content has no
player-placeable service buildings, and the building menu explains this.

Placement previews show valid/skipped cells and the price. **Enter/Y** confirms and **Esc/N**
cancels; insufficient funds leave the city unchanged. **T** starts a straight street line:
arrows or dragging move its endpoint while the preview remains active.

Undo restores a **whole-city snapshot**, not just the selected cells. It requires confirmation
and is available while paused or for one game week after the action. Cancelled/failed actions do
not replace the undo slot. New/load clears it.

### Calendar and economy

New cities begin in the current local year. Saves retain their starting year; legacy saves without
that value retain year-1 behavior. A year has 52 weeks and a week has 7 days.
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
Bridges do not animate. Decoration continues while gameplay is paused and stops while unfocused.

### City name

**Double-click** the city name to rename it, up to **16 characters**. A full-character blinking
block caret appears only while editing; the text remains right-aligned, with backspacing shifting
the remaining text toward the stationary cursor. TermCity and the header geometry stay fixed.

Delete/Backspace removes characters. Ctrl+A/Command+A selects all. Enter or clicking outside commits;
Esc cancels. Editing pauses the game and restores its previous clock state afterwards.
Names are stored in city saves; legacy cities without a name display **New City**.

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

### Music

Quiet **54 BPM Greensleeves** uses an original FM/chiptune arrangement of the traditional
public-domain melody. The opening theme is always the same. Later complete verse/refrain
phrases modulate to related keys and vary their voicing and arpeggios, rather than replaying a fixed loop.

Use **Esc > Music: ON/OFF** to mute or resume it. Music continues during game pauses/dialogs but
pauses when unfocused. Font and music preferences are remembered in `display.cfg`, separately from
city saves. No external recording, audio asset, or audio package is required.

## Controls

| Action | Input |
|---|---|
| Move cursor | Arrow keys |
| Jump a screen | Ctrl+arrows, Home/End/PageUp/PageDown |
| Extend selection | Shift+arrows |
| Keyboard selection mode | `S`, move, `S` to finish |
| Pan | Left-drag, middle-drag, wheel, two-finger trackpad scroll |
| Select with mouse | Shift/Ctrl/Alt+left-drag |
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
| Quick-save / quick-load | F5 / F9 |
| First-city guide | F6 |
| Weekly report / growth report | F7 / F8 |
| City menu | Esc during normal gameplay |
| Input/loop diagnostics | F12 (keyboard-only) |
| Quit | `Q`, Ctrl/Command+Q, or close the window |

Ctrl/Command zoom shortcuts are accepted. Pinch and fractional two-finger gestures preserve the
pointer's map anchor. Close dialogs or finish name/path editing before using gameplay zoom.
Esc closes a dialog or cancels an edit/preview instead of opening another menu.

Zoom levels are **0.25x, 0.5x, 1x, and 2x**. Coarse zoom samples 4x4 or 2x2 map-cell blocks;
2x doubles both tile and glyph dimensions, preserving the normal ratio and one-cell selection.

## Maps

| Preset | Cells | Description |
|---|---|---|
| `small` | 160x96 | Default random map |
| `medium` | 320x192 | Random map |
| `large` | 640x384 | Random map |
| `SF` | 640x384 | San Francisco/Bay Area |
| `LA` | 640x384 | Los Angeles |
| `SD` | 640x384 | San Diego |
| `CHI` | 640x384 | Chicago |
| `STL` | 640x384 | St. Louis |

Custom sizes range from 80x24 to 640x384. Named presets are case-insensitive.
Random maps include hills, forests, water systems, highways/interchanges, and existing bridges.

City scenarios are **stylized, north-up regional approximations**, not current land-use datasets
or street-accurate GIS maps. They include occupied R/C/I districts, residents, roads and tax income:

- **SF:** Pacific/bay, Marin, East Bay, Alcatraz, Angel/Treasure/Yerba Buena Islands, Alameda,
  Lake Merced, Presidio/Golden Gate Park and city hills. Residential west/south, downtown/SoMa
  commerce, southeastern waterfront/Port of Oakland industry, and populated surrounding communities.
- **LA:** Pacific coast, LA River, Santa Monica/San Gabriel mountains, Griffith Park, downtown,
  Hollywood/Santa Monica commerce, Westside housing, and Vernon/Long Beach industry.
- **SD:** Mission/San Diego bays, Coronado, coastal/inland hills, Balboa Park, downtown,
  residential neighborhoods, and working-waterfront/National City industry.
- **CHI:** Lake Michigan, Chicago River branches, flat inland terrain, lakefront parks, the Loop,
  North Side housing, and South/West Side industrial corridors.
- **STL:** Mississippi/Missouri/Meramec rivers, western rolling terrain, Forest Park, downtown,
  South City housing, and riverfront/Metro East industry.

Scenarios start paused without the automatic first-city guide; F6 still offers it explicitly.
Geography and districts stay fixed across seeds; seeds vary households and future growth.
Save/load preserves the map. Restart/new-city actions retain the selected scenario.
Older medium-sized SF saves load at their original dimensions; restarting generates the large map.

## Saving and loading

**F5** writes a quick-save; **F9** requests a load. Unsaved progress is guarded before loading,
starting another city, or quitting: save and continue, continue without saving, or cancel.
A failed save prevents the guarded action.

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
