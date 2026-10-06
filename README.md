# TermCity

A city builder that lives in your terminal. Zone some land, lay some roads, and watch a handful of families turn into a bustling (if slightly boxy) metropolis. Written in C# on .NET 10 with [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) v2. Runs on Windows, macOS and Linux, with keyboard and mouse.

> **Developers:** the architecture, extension points, file formats and test setup are in **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**. This README is for players.

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and a terminal that is at least **80x24** with a Unicode font and truecolor support (Windows Terminal, iTerm2, GNOME Terminal, kitty and friends are all fine).

**Windows Terminal:** use **v1.25.2733.0 or newer**. Delayed highlights after scrolling and idling were reproduced on v1.24.12741.0 and confirmed resolved with [v1.25.2733.0](https://github.com/microsoft/terminal/releases/tag/v1.25.2733.0).

### 1. Install the .NET 10 SDK

| OS | One way to do it |
|---|---|
| Windows | `winget install Microsoft.DotNet.SDK.10` |
| macOS | `brew install --cask dotnet-sdk`, or the installer from the link above |
| Linux | Your package manager (for example `sudo apt install dotnet-sdk-10.0`), or Microsoft's [install script](https://learn.microsoft.com/dotnet/core/install/linux) |

Check it worked with `dotnet --version` (it should say `10.x`).

### 2. Get the code

```
git clone https://github.com/devopsjesus/termCity.git
cd termCity
```

### 3. Compile and run

**Windows (PowerShell or Windows Terminal)**

```powershell
dotnet run --project src\TermCity.App                       # build and play
dotnet publish src\TermCity.App -c Release -o out           # or build a standalone folder...
.\out\termcity.exe                                          # ...and run it
```

**Linux**

```bash
dotnet run --project src/TermCity.App                       # build and play
dotnet publish src/TermCity.App -c Release -o out           # or build a standalone folder...
./out/termcity                                              # ...and run it
```

**macOS**

```bash
dotnet run --project src/TermCity.App                       # build and play
dotnet publish src/TermCity.App -c Release -o out           # or build a standalone folder...
./out/termcity                                              # ...and run it
```

If `./out/termcity` is not found or not executable on your system, `dotnet out/termcity.dll` does the same thing.

Add options after a `--` when using `dotnet run`, or directly after the program name when running the published build:

```
dotnet run --project src/TermCity.App -- --seed 42 --size medium
./out/termcity --seed 42 --size medium
```

### Command-line options

| Option | What it does | Default and range |
|---|---|---|
| `--seed <n>` | Generates the map from this number, so you can replay or share a map. | A random seed each run. Any whole number. |
| `--size <size>` | The size of the map. `small`, `medium`, `large`, or `WIDTHxHEIGHT`. | `small` (160x96). Custom sizes run from 80x24 up to 640x384. |
| `--load [file]` | Loads a saved game instead of starting a new one. | With no file, loads the quick-save (see [Saving](#saving-and-loading)). |
| `--fps <n>` | Redraws per second while the map is scrolling. Lower it if the screen lags behind your mouse. | 30. Range 5 to 60. |
| `--dump-map` | Prints the generated map as text and exits. Handy for peeking at a seed. | Off |
| `-h`, `--help` | Lists the options. | |

TermCity needs a real, interactive terminal; it will politely refuse to run with its input or output redirected.

## How to play

You start with **$50,000** (the default; roughly 100 street cells) on a freshly generated map: hills, rivers, lakes, a sea or a bay, trees, rocks, and a sparse network of highways already crossing it. New cities start **paused**, with a short, dismissible guide.

For a quick first neighborhood: connect a street to the highways (`T` draws a straight line), zone nearby homes with `R`, then press `P` to let the city grow. `F6` shows or dismisses the guide. Shops and factories unlock at **10 occupied homes**, and the game tells you when you reach that point.

1. **Zone some land** (free): Residential (`R`), Commercial (`C`) and Industrial (`I`).
2. **Build roads** from your zones to the existing highways. Water can't be built on, but the generated highways already bridge it for you.
3. Once a zone is within **2 cells** of a road that is **connected to the outside world** (a road chain that reaches the map edge), people start moving in. Zones with no road access are drawn dim; roads that don't reach the map edge are drawn amber.
4. The first **10 residential cells** have to fill before any commercial or industrial cell can. After that, the city wants roughly **1 commercial cell per 20 filled homes** and **1 industrial cell per 10** (rounded up, so the first shop and the first factory can arrive as soon as 10 homes are occupied).
5. At the end of every week, each filled cell pays tax: **5% of its weekly value**.
6. If your money hits **$0**, no more roads can be built. Zoning and demolishing stay free, so you can always tidy up while waiting for taxes.

### The numbers

| | Residential | Commercial | Industrial |
|---|---|---|---|
| Weekly value of a filled cell | $200 | $350 | $500 |
| Tax rate (default) | 5% | 5% | 5% |
| **Tax per filled cell per week** | **$10** | **$17.50** | **$25** |
| Needs | 10 filled to unlock the others | 1 per 20 filled homes | 1 per 10 filled homes |
| New cells per week, brand new city | up to 3 | up to 1 | up to 1 |

Tax rates are stored per zone type and cannot be changed through the gameplay interface. The total is rounded to a whole dollar amount each week.

**Growth compounds.** Each week, the cap on newcomers grows by 2% of the cells already filled (for commercial and industrial, of the amount the residents currently allow). A city of 1,000 homes can take in about 23 new homes a week, so small towns grow slowly and big ones grow fast. Cells fill at random among the zoned cells that have road access, so the real limit is how fast you can build roads. A fully built `large` map takes years of game time, not centuries.

**Who lives here.** A home averages 2 adults and 2 children, and about a third of homes have 1 or 2 seniors, so a home is about 4.5 people on average. You can click any home to see its residents.

**Demand bars.** The side panel shows how much the city wants more of each zone type, from empty to full. Residential demand is high until you have enough homes zoned to support the shops and factories you have zoned, and jumps up whenever every home is taken. Commercial and industrial demand wait until 10 homes are filled.

**Why isn't it growing?** Inspect a vacant zoned cell to see whether it needs road access, more occupied homes, or more capacity. `F8` opens a city-wide growth report with counts of vacant cells served by roads and an explanation for each zone type. Demand measures how much more land to zone; an empty demand bar does not necessarily stop existing zones from filling. Eligible cells are chosen randomly, with arrivals spread across the week.

**A little direction.** `F7` shows the latest completed week's new homes, shops, factories and tax income, plus your population milestone progress. Milestones are **100, 500, 1,000, 5,000 and 10,000 people**. Reaching one earns a notification, not a change to the rules; keep building at your own pace.

### Time

A game **week is 7 days**, a year is 52 weeks. People move in a few at a time over the days of the week, and taxes arrive on the last day. The week bar in the top line has one character per day, with `>` marking today (`[===>...]`).

| Speed | Key | A week lasts | A year lasts |
|---|---|---|---|
| Slow | `1` | 8.4 seconds | about 7 minutes |
| **Medium (default)** | `2` | 4.2 seconds | about 3.6 minutes |
| Fast | `3` | 1.4 seconds | about 1.2 minutes |

`Space` or `P` pauses and resumes. Choosing a speed also resumes a paused game. If the game window stalls (say it spends a while in the background), the clock does not fast-forward to catch up.

The clock also holds while a placement preview, city menu, report or confirmation is open. Closing it restores normal clock behavior; it does not resume a game you explicitly paused.

### Building

- **Zoning** (`R`, `C`, `I`, or Right-click > Area) is free. It works on any open, buildable cell, clears trees and rocks for free, and skips water, roads and occupied cells. You can re-zone empty zoned cells to another type; changing an occupied cell to a different type requires demolishing its building or waiting for it to leave after dezoning.
- **Dezoning** (`U`, or Right-click > Area > Unzone / dezone) removes the designation immediately, for free. Empty land is ready to reuse. Existing buildings stay for a randomly chosen **2-3 game weeks (14-21 days)** each, keeping their residents and paying taxes until they are automatically removed. The clock stops while paused. Inspect a building to see its remaining time; restore its original zone with `R`, `C` or `I` to cancel removal. Roads, terrain and natural features are left alone. You can demolish a remaining building sooner if you need the space.
- **Roads** fill every valid cell of your selection. Select a one-cell-wide strip, or press `T` for the straight-line tool: move the endpoint with arrows, or left-drag from a starting cell. The line follows the dominant horizontal or vertical direction and stays one cell wide at every zoom level. Other road types and their line tools are in the Road menu.
- **Preview before committing.** `B`, road-menu actions and `D`/`Delete` show a preview without changing the city. Green marks valid cells, red marks blocked/skipped cells, and yellow marks a mixture when zoomed out. A small **Yes/No popup beside the target** shows the cell count, skipped cells and price. Click a button, press `Y`/`N`, or select with arrows/Tab and press `Enter`; `Esc` cancels. Yes is initially selected. While drawing a road line, arrows continue moving the endpoint and `Enter` confirms. If the price exceeds your money, nothing is built. Otherwise, all valid cells are built and blocked cells are skipped, so check line previews for gaps.
- **Upgrading.** Building a bigger road type over a smaller one is an upgrade and costs only the difference. A road is never replaced by a smaller one.
- **Demolish** (`D` or `Delete`) is free and gives no refund. It clears roads, zones, buildings, trees and rocks. It will also remove the generated highways, so be gentle with them.
- **Buildings** grow automatically in zones. There are no player-placeable service buildings (fire stations, police stations or schools); the Buildings menu contains a disabled placeholder.

### Undo

`Ctrl+Z` (`Control+Z` on macOS) undoes the **last successful zoning, dezoning, construction or demolition action**. It restores the **entire city** to immediately before that action, including the money, residents, calendar, scheduled removals and intervening growth—not just the cells you changed.

- While the game is running, undo is available within **7 game days** of the action.
- While paused, the last action can be undone regardless of its age.
- Requesting an available undo first pauses the game and asks you to confirm the full-city rollback. Cancelling leaves the city unchanged and paused.
- There is one undo slot and no redo. Failed actions do not replace it; loading or starting another city clears it. Undo history is not saved.

### Roads

The glyphs below are the real ones. Every road connects to every other road, and all three types serve nearby zones the same way; the types differ in price and in looks.

| Type | Cost per cell | On a hill | Upgrade from a street | Glyphs | How to build |
|---|---|---|---|---|---|
| Street | $500 | $750 | | `─ │ ┌ ┐ └ ┘ ├ ┤ ┬ ┴ ┼` | `B`, or the menu |
| Avenue | $900 | $1,350 | $400 | `━ ┃ ┏ ┓ ┗ ┛ ┣ ┫ ┳ ┻ ╋` | Menu: Road > Avenue |
| Highway | $1,500 | $2,250 | $1,000 | `═ ║ ╔ ╗ ╚ ╝ ╠ ╣ ╦ ╩ ╬` | Menu: Road > Highway |

Hills multiply every cost by 1.5. The menu shows the exact price for your current selection before you commit.

## The screen

```
+----------------------------------------------------------+-----------------+
| TermCity $50,000 | Year 1, Week 01 [>......] | Pop 0 | >> Medium          |
+----------------------------------------------------------+-----------------+
|                                                          |   minimap       |
|                                                          |  ZOOM [-] 1x [+]|
|                       the map                           |  DEMAND         |
|                                                          |  CITY           |
|                                                          |  ZONES          |
+----------------------------------------------------------+-----------------+
| cell details, or the latest message          F1 Help  Enter Menu  Ctrl+Q Quit|
+----------------------------------------------------------------------------+
```

- **Top bar.** Money (green, or red at $0), year and week, the week bar, population, and the clock (`>` Slow, `>>` Medium, `>>>` Fast, `|| PAUSED`). `OUT OF MONEY` appears when you are.
- **Minimap.** The whole map at a glance: water, hills and open land in muted colors, roads as faint lines, your zones in bright colors, and a white box showing the part you are looking at. Click or drag it to jump there.
- **Zoom bar.** `ZOOM [-] 1x [+]`, clickable.
- **Side panel.** Three sections that fold up when you click their heading (or press `F2`, `F3`, `F4`):
  - **Demand**: three bars for R, C and I.
  - **City**: population (adults, kids, seniors, homes), weekly tax income and the tax rates.
  - **Zones**: filled / zoned for each type, with a note such as `(4 no road)` for zoned cells that road service does not reach, or `+2 leaving` for dezoned buildings awaiting removal.
- **Bottom line.** Details of the cell under the cursor (terrain, road and whether it is connected, zone, residents) or of your selection's size. After something happens it shows a message for about five seconds first.

**City menu (`F10`).** Save, load the quick-save or one of three autosaves, enter a save-file path, start a random city of the same size, restart the same seed, undo, view reports or the guide, and quit. Use arrows and `Enter`, number keys, or click a choice. `Esc` closes the menu. The file-path field accepts typing and pasted text; `Ctrl+A` clears it.

### Zoom

The zoom levels are **0.25x, 0.5x, 1x (normal) and 2x**. Zoomed out, each character stands for a square block of 2 or 4 cells (the most important thing in the block is drawn: buildings, zones, roads, water, hills, trees); arrow keys move a block at a time, and selecting snaps outward to whole blocks. Zoomed in, each cell is drawn two characters wide. Zooming keeps the cell under the pointer (or the middle of the screen, for keys and buttons) in place.

## Controls

On macOS, in-game labels use the Mac key names **Control**, **Option**, **Return**, **Escape** and **Forward Delete**. Use `Fn+Arrow` to jump a full screen: terminals report these as Page Up, Page Down, Home and End, while macOS reserves `Control+Arrow` for switching Spaces and terminals commonly translate `Option+Arrow` into word navigation. The game still accepts `Control+Arrow` when the terminal delivers it. Control and Option are used rather than Command because terminal applications do not reliably receive Command-key combinations. Function-key shortcuts may require holding `Fn`, depending on the keyboard settings. Every function-key action is also available from the city menu, context menu, side panel or another listed key. Windows and Linux retain the Ctrl, Alt, Enter and Esc labels shown below.

| Action | Keyboard | Mouse |
|---|---|---|
| Move cursor | Arrow keys | Click a cell |
| Jump a full screen | Ctrl+Arrow (`Fn+Arrow` on macOS) | |
| Move the map | Cursor at the screen edge | Drag with the left button; wheel scrolls up and down (Alt + wheel = sideways); click or drag the minimap; edge scrolling (off by default) |
| Zoom | `+` / `-`, `0` = normal | Ctrl + wheel (up = in), or the `[-]` `[+]` buttons |
| Select an area | Shift+Arrows, or `S`, arrows, `S` | Shift+click or Shift+drag (Ctrl or Alt + click work too) |
| Context menu (Area, Road, Buildings, Demolish) | `Enter` or `M` | Right-click |
| Zone the selection | `R` / `C` / `I` | Menu: Area |
| Dezone the selection | `U` | Menu: Area > Unzone / dezone |
| Preview street / demolition | `B` / `D` or `Delete` | Menu: Road, Demolish |
| Draw a straight street | `T`, then arrows | Menu: Road > Draw ... line; left-drag the endpoint |
| Confirm / cancel preview | `Y` / `N`, arrows/Tab then `Enter`; `Esc` cancels | Nearby `[Yes]` / `[No]` popup |
| Undo last successful action | `Ctrl+Z`, then confirm | City menu |
| Pause and resume | `Space` or `P` | |
| Speed | `1` slow, `2` medium, `3` fast | |
| Edge scrolling on/off | `E` | |
| Fold a side-panel section | `F2` / `F3` / `F4` (Demand / City / Zones) | Click its heading |
| Save / load quick-save | `F5` / `F9` | City menu |
| City menu | `F10` | Context menu > City menu |
| First-city guide | `F6` | City menu |
| Weekly report / milestones | `F7` | City menu |
| Growth explanations | `F8` | |
| Help | `F1` or `?` | |
| Input and loop diagnostics | `F12` | |
| Cancel selection | `Esc` | |
| Quit | `Ctrl+Q` | |

Actions (zone, dezone, road, demolish) apply to your selection, or to the cell under the cursor if there is no selection. Road and demolition actions require preview confirmation; zoning and dezoning apply immediately. After a successful action the selection clears.

**Mouse notes**

- A plain left-click highlights a cell; a left-drag pans the map instead of selecting. Holding Shift (or Ctrl or Alt) turns the same click or drag into a selection, and dragging a selection to the edge scrolls the map.
- **Edge scrolling** is off until you press `E`, because it is easy to scroll by accident. Once on, rest the pointer within 3 columns (or 2 rows) of the map's edge; the nearer the edge, the faster it goes. Pressing a key stops it until you move the mouse again.
- **Some terminals keep modifier keys for themselves.** Windows Terminal, for example, uses Shift for its own text selection and may use Ctrl+wheel for font size, so Shift+click or Ctrl+wheel might never reach the game. Try Alt+click to select, and `+` / `-` or the zoom buttons to zoom, which always work. `F12` shows exactly which events your terminal delivers.
- Terminal.Gui reports Ctrl+wheel and a horizontal (tilt) wheel identically, so both zoom.

## The world

### Map size

| Preset | Cells | Screens of 80x24 |
|---|---|---|
| `small` (default) | 160x96 | 2x4 |
| `medium` | 320x192 | 4x8 |
| `large` | 640x384 | 8x16 |

Characters are about twice as tall as they are wide, so these come out roughly square on screen. Bigger maps get more lakes, more rivers and more highway interchanges. A custom size can be anything from 80x24 to 640x384.

### Land

Hills cover roughly a sixth of the map. Trees grow in forests with a few stragglers; rocks are scattered about, and are more common on hills. Both are cleared for free when you build over them.

### Water

Each map has one of five kinds of water, chosen from its seed, plus a few small lakes inland (more on bigger maps):

| Type | What you get |
|---|---|
| **Sea edge** | A shallow sea along one side of the map, with the main river emptying into it. |
| **Bay** | A body of water cut off by an edge or a corner of the map: a city on a bay. |
| **Large lake** | A big lake in the middle of the land, fed by a river (sometimes two). |
| **River confluence** | Two rivers that meet and flow on as one into the sea or a lake. The only kind of map where rivers meet. |
| **Inland lakes** | No sea or big lake, just a few lakes inside the land. |

Water that touches an edge of the map reaches at most 10 cells in, a sea covers at most 30 percent of the edge it lies along, and every edge of the map is mostly land. Total water is typically 3 to 12 percent of the map. Rivers start at the edge, cross the land and flow into a body of water.

### Highways

Every map starts with a sparse highway network: a few interchanges (about three on a small map, more on bigger ones) joined by long, straight highways that run off the map through gateways on its edges. Highways turn through clean 90-degree corners, chamfered corners or a diagonal jog; since Unicode has no double-line diagonal, a diagonal is drawn as a staircase (`╚╗╚╗`). Highways keep a respectful distance from each other and only meet at interchanges. They only bridge short stretches of water, and a few short streets leave each interchange as a place to start building. The rest of the map is yours.

## Glyphs and colors

Only single-width characters are used (no emoji), so the grid stays aligned in every terminal. Colors are 24-bit; the hex codes are the real ones, in case you want to theme your own screenshots.

### Land and city

| Thing | Glyphs | Foreground | Background |
|---|---|---|---|
| Grass | `· , ' "` (mostly `·`) | `#4f8f4a` | `#16301a` |
| Hill | `∩ ⌒` | `#c9a468` | `#3d3320` |
| Water | `≈ ~` | `#7fc4ff` | `#0f3a66` |
| Tree | `♣ ♠` | `#3fbf4f` | the ground |
| Rock | `● ◦` | `#a8a8a8` | the ground |
| Residential zone (empty) | `░` | `#58d068` | `#1f4a28` |
| Commercial zone (empty) | `░` | `#58a6ff` | `#1b3a66` |
| Industrial zone (empty) | `░` | `#f2c94c` | `#57481a` |
| House | `⌂ ▟` | `#9dff9d` | the zone |
| Shop | `▣ ▦` | `#9fd0ff` | the zone |
| Factory | `▤ ▩` | `#ffe08a` | the zone |
| Street | `─ │ ┼` and friends | `#d6d6d6` | `#2b2b30` |
| Avenue | `━ ┃ ╋` and friends | `#a9d4ff` | `#2b2f3a` |
| Highway | `═ ║ ╬` and friends | `#ffd166` | `#33302a` |
| Bridge | the road glyphs | the road's color | water |
| Road not connected to the map edge | the road glyphs | `#e8a33d` (amber) | as the road |
| Zone with no road access | `░` | the zone color at half brightness | the zone |
| Cursor | an inverted cell | `#101010` | `#f5f5f5` |
| Selection | blue tint | | `#58a6ff` blended in |

### Interface

| Use | Color |
|---|---|
| Side panel background | `#14161c` |
| Top and bottom bars | `#1d2330` |
| Text | `#c8ccd4` |
| Dim text | `#7a808c` |
| Headings | `#f2c94c` |
| Good (money, tax income) | `#58d068` |
| Bad (out of money, errors) | `#ff6b6b` |
| Accent (week bar, folds, selection) | `#58a6ff` |
| Empty demand bar | `#2a2e38` |

The map has a deliberately plain texture: nearly every ground cell is the same glyph. A busier texture means more to redraw when the map scrolls, which means choppier scrolling.

## Saving and loading

`F5` writes a quick-save and `F9` requests a load. If you have unsaved changes, loading, starting another city or quitting offers **Save and continue**, **Continue without saving**, or **Cancel**. A failed save prevents “Save and continue” from proceeding.

The quick-save is `TermCity/quicksave.json` in your per-user data folder:

| OS | Typical location |
|---|---|
| Windows | `%LOCALAPPDATA%\TermCity\quicksave.json` |
| Linux and macOS | `~/.local/share/TermCity/quicksave.json` |

**Autosave** checks every **60 real seconds** and saves changed cities, even while paused. It keeps **three rotating files** beside the quick-save: `quicksave.autosave1.json` (newest), `quicksave.autosave2.json` and `quicksave.autosave3.json`. Autosaves do not overwrite the quick-save or dismiss the save-before-leaving prompt. Restore them through `F10` > Load city. Save failures appear as errors; autosave retries at the next interval.

A save keeps the map, money and calendar, clock speed, random-number state, in-progress weekly growth plan, latest weekly report, milestones and guide-dismissal state. A game you load starts **paused**; press `P` to carry on. `--load` at the command line takes the map from the save, so `--seed` and `--size` don't apply to it. A command-line load failure reports an error and exits; an in-game failure reports an error without replacing your city.

## Troubleshooting

- **Highlights arrive late after scrolling and idling in Windows Terminal.** Upgrade to [v1.25.2733.0](https://github.com/microsoft/terminal/releases/tag/v1.25.2733.0) or newer. This delay was reproduced on v1.24.12741.0 and confirmed resolved on v1.25.2733.0; changing drivers, lowering FPS, software rendering, and full repaint did not resolve it on the affected version. If the Microsoft Store says the older version is up to date, use the official GitHub release or the classic console fallback below.
- **Scrolling continuously lags behind.** Try a real terminal or lower `--fps` (try 15 or 20) to reduce redraw output.
- **Shift+click or Ctrl+wheel does nothing.** Your terminal is keeping the keys for itself. Use Alt+click and the `+` / `-` keys. See the mouse notes above.
- **The map looks squashed, or the glyphs overlap.** Use a font with good Unicode box-drawing and block-element coverage (Cascadia Code / Mono, JetBrains Mono, Fira Code, DejaVu Sans Mono...).
- **"TermCity needs an interactive terminal."** You ran it with input or output piped somewhere. Run it directly in a terminal window.
- **Something odd with the mouse?** Press `F12`. The bottom line then shows the last event the terminal sent, and how long the game loop took between ticks. That is the first thing to include in a bug report.

If upgrading Windows Terminal is not possible, run a medium map in the classic Windows console host from the repository root:

```powershell
conhost.exe powershell.exe -NoExit -Command "Set-Location '$PWD'; dotnet run --project .\src\TermCity.App -- --size medium"
```

## Contributing and license

Want to see how it works, add a terrain, or run the tests? Start at **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**. The project is released under the MIT license; see [LICENSE](LICENSE).
