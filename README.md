# TermCity

A terminal-styled medieval settlement and siege game built with **Godot .NET and C# on .NET 10**.
Draw a motte-and-bailey enclosure, welcome settlers and defend its walls against growing goblin raids.
The earlier city-building sandbox and populated scenarios remain available.
It runs in a desktop window on Windows, macOS, or Linux.
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
| `--debug-settlement` | Debug build only: pre-drawn bailey, focused camera and natural immigration |
| `--debug-siege` | Debug build only: 24x24 bailey with 100 residents and the first goblin attack |
| `-h`, `--help` | Print game options and exit; works headlessly |

```bash
godot --path godot -- --size SF --seed 42
godot --path godot -- --load /absolute/path/to/quicksave.json
godot --headless --path godot -- --size CHI --seed 42 --dump-map
```

A command-line load failure exits with an error. In-game load failures keep the current city and
leave a visible error in the dialog. When loading, the saved map/configuration overrides seed and size.

## How to play

### Found and defend a settlement

New random maps start paused. Open **Garrison** with **G** or the shield icon.
Choose **Motte and Bailey enclosure**, click a first corner, then move the mouse to
size a closed rectangular bailey. A live wall preview follows the pointer; click again
or press **Enter** to found it. **Esc** or right-click cancels without spending gold.
Middle-drag, zoom, camera rotation and tilt remain available during placement.
Choose clear, dry ground; invalid terrain, occupied land and unaffordable enclosures
are rejected as one action, not partially built.

The founding cost includes the lord's hall, well, woodlot, walls, corner towers and
additional arrow towers at 15-cell intervals. A gate is generated on the viewer-facing
wall. Homes, shops and workshops then grow naturally **inside the walls** as families
arrive and business demand grows. **No roads or manual zoning are required.**
Repeated journeys wear connected paths between buildings within the bailey, including
neighboring homes. These paths stay inside the walls rather than leading to exterior roads.
Press **P** to resume.

At the permanent **Hamlet milestone (100 residents)**, goblin waves begin approaching
and attacking individual wall components. Towers fire arrows automatically in range.
The sidebar's **NEXT WAVE** bar empties toward the next attack and shows the remaining
time. It follows game speed, freezes when paused, and refills for each new wave.
If it reaches zero during a battle, the next wave waits until the current attackers are gone.
Each new wave sounds an original synthesized battle horn and switches play to
**1x (Slow)** so the attack does not rush past. The horn respects SOUND mute/volume;
you can change speed again after the wave begins.
The opening wave brings **24 goblins with 96 health each**; later waves grow in number
and durability, up to 120 attackers. Older siege saves upgrade goblin health while
preserving the percentage of damage already taken.
Goblins become larger and stronger at later milestones. Damaged walls have visible
health bars; right-click a wall or tower to inspect it and open Garrison.

Garrison offers wall-top **oil cauldrons**, **rocks**, **ballistae** and **trebuchets**.
It uses the same construction catalog as other services: select a component to see
its rendered model and statistics, then choose **Add** to place it. Repairs and wall
upgrades use the same catalog and show their current gold cost.
Every type has an underlined keyboard shortcut. Press its letter to select it, then
**Enter** (or keypad Enter) to activate **Add** from the type list. Arrow-key focus
also updates the selected type and details; locked choices remain inspectable.
When the menu was opened by right-clicking a map spot, fitting buildings and mounted
defenses are placed there immediately. If they do not fit, an unpaid placement preview
opens at that spot with an explanation, ready to move or rotate. Sidebar catalogs
still start ordinary placement previews.
The menu shows each weapon's unlock population, cost, damage, splash, range and reload.
Cauldrons cover nearby groups, rocks hit harder at short range, ballistae reach farther,
and trebuchets have the greatest damage and range, but cannot hit inside their minimum
range. Mounted weapons require an intact, unoccupied wall top. Gold pays for repairs
and upgrades from wood to **stone (250 souls)**, then **iron-reinforced stone (1,000)**.
Unlocks persist after population falls. Walls, weapons, attackers and combat clocks
are saved; paused undo restores an entire founding or defense purchase.

Ordinary walls form spans of up to **three cells**, stopping at towers and the gate.
Each span has one shared health bar and the combined strength of its cells; damage,
breaches, repairs and upgrades apply to the whole span. Towers remain separate.
Selection and valid mounted-defense placement squares sit on the wall or tower top.
Selected buildings, towers and wall spans also receive a subtle warm surface highlight;
the yellow outline remains visible. Clearing selection removes the tint.

Purchased wall upgrades are built one section at a time, taking **three siege seconds
per section** (six real seconds at 1x). Crews lay visible stone courses over wood, then
add iron reinforcement on the next upgrade. Completed sections gain the new strength
without erasing damage; unfinished sections keep their old strength. Work pauses with
the game and survives saving and undo.

Between waves, civilians gradually repair the walls without spending gold. Crews
finish breaches first, then work through the most damaged sections until everything
is restored. More residents, occupied industry and active service buildings speed
the work. Doorway-scale civilians first throw buckets of water onto burning sections.
Once the flames are out, they hammer on small scaffolds while a second worker
carries replacement boards; repairs pause during
attacks or a paused game. Repair progress survives save/load; paid instant repairs
remain available in Garrison.

Damaged walls, gates and towers progressively lose chunks and crenellations, leaving
ragged tops and rubble. Below **33% health**, coarse animated flames mark the damage,
including breached sections; repairing back to 33% extinguishes them. Flames are a
visual warning only, not additional damage or spreading fire.

Inside an enclosure, residents and trades choose individual sites on shared land;
there are no pre-zoned housing, industry or commercial districts. Markets and supply
shops favor gate traffic and nearby homes, while blacksmiths, woodsmiths and armories
occupy working sites around the edges. Prosperous households spread into broad
courtyard houses. Crowded, lower-value neighborhoods grow stacked wooden shanties
and homes attached to inner wall faces, without replacing defenses or blocking the
gate and repair alley. Existing households survive redevelopment; new residents
still need normal immigration and supplies.

Vacant-zone shading (legacy cities) and service/weapon range overlays start **off**. Use
**VIEWS → AREAS** to show them; this does not change zoning or settlement growth.

Goblins use smooth, sprite-like stepping with alternating limbs and body sway. They
face their walking direction and turn toward the wall when attacking. Arrows are
fast, directional traces rather than slow-floating blocks. These visuals do not
change goblin travel speed, weapon damage or reload timing.
Goblins reserve physical body space at spawn and queue when another attacker blocks
their route; they cannot stack or pass through one another. Larger goblins require
more space. Blocked attackers seek another reachable, unoccupied wall frontage instead
of remaining stuck behind the front rank. They attack the nearest section actually
within melee reach, never through another goblin. At a wall they visibly wind up, lunge into a weapon strike, and recoil
on their normal attack cycle. Older saves with stacked attackers spread those units
onto reachable dry land when loaded.

Fuel smoke is compact enough for enclosed settlements: Charcoal Burners have a
**six-cell pollution radius**, fading out by 7.5 cells, without reducing fuel output
or the smoke's local intensity. Woodlots remain pollution-free.

```bash
godot --path godot -- --seed 42 --debug-settlement
godot --path godot -- --seed 42 --debug-siege
```

These Debug-only starts cannot be combined with each other, loaded cities, populated
scenarios, map dumps or smoke modes.

**Debug Siege map controls:** `--debug-siege` adds a terminal-framed strip at the map's
top edge. Set a swarm size from **1 to 120**, then choose **START WAVE** (or press
Enter in the size field) to replace the current attackers with that exact count.
**REMOVE GOBLINS** clears all attackers and projectiles and restarts the wave countdown.
**REPAIR ALL** restores walls, gates, towers and mounted defenses for free; ordinary
town buildings do not have siege hit points. **RESTART GAME** offers save/continue/cancel,
then restores the same seed's fresh bailey, 100 residents and first wave.
The **Walls width x height** inputs choose the starting enclosure for that restart.
Each dimension must be 12-160 cells and fit the map with exterior space; the exact
rectangle must have affordable dry land and room for 100 residents. Invalid or
unplaceable sizes leave the current game untouched, never silently shrinking the walls.
These controls are absent from ordinary gameplay and Release builds.

### Legacy city-building sandbox

Random cities start paused; **F6 > Start** contains next-step first-city advice. Connect a short track to an existing highway or a map
edge and zone 20-30 nearby homes with **R**, leaving open service plots. **Before resuming with P**,
build a nearby **Woodlot (8,000g)** and **Town Well (4,000g)** through Enter > Services.
Together they cost 12,000g and 60g/week, leaving 38,000g of the starting 50,000g before road costs.
Sims can walk over land without roads. Tracks speed travel sixfold; higher road tiers are faster.
Buildings gradually wear the shortest reachable path from their perimeter to the actual road surface.
Homes do not create house-to-house paths. Road-accessible buildings use their road connection
instead of wearing parallel trails between one another; roadless service/workplace links can still form.
Water crossings require bridges.

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
| 100 | Hamlet | Goblin waves, oil cauldrons and dropped rocks for founded baileys |
| 250 | Growing Hamlet | Ballistae, stone walls; Fire Watch, Watch House, Sheriff's Hall, Apothecary, Chantry School |
| 500 | Village | Stone Keep, Hospice, Parish Church, Market Cross, Granary; grain management and level-2 redevelopment |
| 1,000 | Market Town | Trebuchets, iron-reinforced stone walls; Gaol, Infirmary, Monastery, Guildhall |
| 2,500 | Borough | Castle |
| 5,000 | City | Cathedral; level-3 redevelopment |
| 10,000 | Great City | Final achievement; all city-size gates open |

Density still requires suitable land value, clear expansion space, supplies and a lord's seat
(a motte for level 2; a keep for level 3), but no road class is mandatory.
Smaller essential services remain available from the start.
Service menus show locked buildings and their thresholds; previews and placement enforce the same gates.
**F6 > Dismiss tip** switches off coaching, **not milestones or unlock gates**.
**F6 > Enable guide** turns coaching back on. Achievements and the guide preference survive save/load.
For repeated small/medium/large playthrough results and balance refinements, see
[the playtest report](docs/PLAYTEST.md).

The time section ends with **1x / 2x / 3x** for Slow / Medium / Fast, including while paused.
Press **F6** (or Esc > Guide) for the GUIDE, a tabbed dialog (Start, Zones, Roads, Services, Population, Happiness,
Economy; Left/Right or a click switches tab) that explains how each thing you place drives the town's population.
**F1** is only the short table of controls. Placing a zone, road or building makes a soft click-clack keyboard sound
(it is silenced through **Esc > GAME MENU**, and when the window is unfocused).
**Esc > GAME MENU** provides inline MUSIC and SOUND volume sliders (0-100%) and speaker mute buttons.
New audio preferences default to 70% music and 60% sound; existing saved volume choices are retained.
Muting retains each volume setting; the controls are saved between runs. Music continues during
gameplay pauses and dialogs, but pauses when the window loses focus.
Optional milestone confetti is decorative; founded baileys also begin goblin attacks at Hamlet.
They are off by default. Effects and Celebrations are not shown in the game menu.

### Services, budget and the full city engine

New games run the full rules: homes and businesses need **power and water** (build plants, pumps and towers), residents
want a **Fire Watch, a sheriff, an apothecary, a chantry school and a village green** as the town grows, and a lord's
**castle** to keep the peace. At **Village (500 residents)** grain management unlocks with Market Cross
and Granary; before then, there are no grain costs, hunger penalties, or famine.
Afterward, the harvest sets the grain in store, a bad year brings famine, and
plague, raiders and fires are real dangers. Jobs matter and the treasury (in gold) has to balance, with the crown's
tribute due each Michaelmas. Open **City menu > Budget, taxes and loans** to set service funding, tithes and loans, and
**City health report** for every indicator. Details and tuning are in [docs/POPULATION.md](docs/POPULATION.md); the setting,
the service mapping and ideas for the future are in [docs/MEDIEVAL.md](docs/MEDIEVAL.md).

### Zones and growth

- **Residential (R):** households build homes and add adults, children, and seniors to the population.
- **Commercial (C):** shops and offices become available after 10 occupied residential cells;
  roughly one commercial cell is supported per 20 homes.
- **Industrial (I):** factories and workshops; roughly one industrial cell per 10 homes.
- Zoning is free. Growth needs clear multi-cell plots on walkable land, not road connectivity.
  Vacant plots use a small translucent checkerboard in green residential, blue commercial, or gold industrial;
  it disappears when buildings grow or the land is dezoned.
  Zone overlays are clipped to the actual road geometry, including highway curbs and bridge decks;
  they never paint over road surfaces.
  Checkerboards stop at hill/terrace edges and paint only the top surface of each
  zoned terrace, never stretching up cliff faces.
  Homes, market stalls and workshops normally use **1x1 plots**. Occasional 1x2, 2x1 and
  2x2 variants add variety; newly grown buildings never need more than **2x2**.
  A single zoning click reserves one complete 1x1 starter plot. Larger dense homes,
  merchant premises, mills and forges stay within the same 2x2 limit.
  Weekly growth caps increase with city size. Remote walking arrivals are slower.
  A single R/C/I click reserves the minimum complete plot. Press P to resume after
  zoning; F8 and the cell inspector explain cramped plots or stalled settlement.
- **U** dezones land without immediately removing occupied buildings. Those buildings keep residents
  and tax income for 2-3 game weeks. Restore their original zone before the deadline to keep them.
- **D/Delete** previews demolition. It is free, with no refunds.

Roads cost money: a street starts at **500g per cell**, an avenue at **900g**, and a highway at
**1,500g**. Upgrades charge the difference; hills multiply construction costs by 1.5.
Dirt tracks and cobbled roads have a **one-cell-wide** surface; King's Roads are
**two cells wide**, with broad curves instead of sharp right-angle bends for long-distance
travel. These widths are measured across the road, not along its centerline.
Straight road edges line up with whole-cell boundaries.
Straight side roads retain their cell-aligned centerline when joining angled highways;
the junction must not tilt the entire side road between neighboring cells.
Normal-zoom selection stays 1x1 even on a two-cell highway, and both cells across its width
are independently selectable.
At 0.5x and 0.25x, a single zoomed-out road selection covers
the whole road width rather than selecting just one half.
Paved roads have raised stone edging, while dirt tracks have low earthy verges.
Local right-angle bends are compact, leaving both sides of their straight legs available
for buildings instead of extending a broad curve into neighboring plots.
Generated highways reject right-angle bends and use broad, tangent curves.
Random maps start with a few long intercity highways spread across the map, with broad,
well-separated intersections instead of a central hub or a lattice of short interchanges.
Small, medium and large maps normally have two, three and five major highway corridors respectively.
Routes use uneven bend locations, substantial meanders and staggered joins; intercity
connectors can end on an existing highway instead of crossing every route into a grid.
Corner regions and the center are covered: every buildable cell is within 96 logical cells
of a highway, using a fixed two-screen reference rather than the current window size.
Local streets and additional road links are left for you to build.
Random maps also add several dirt-road spurs off King's Roads near water,
with adjacent level 4x4 areas for initial settlement. The spurs stay on dry
land and do not add more highways.
Small, medium and large maps aim for three, four and six usable spurs respectively,
with fewer only where terrain leaves no suitable routes. New maps begin centered on
the most central water-adjacent spur; a good site takes priority over forcing a central location.
Loading a city retains its saved camera view.
These trunks span the map rather than clustering on one side. Some random maps also feature
steep-sided river canyons. Rivers favor broad lateral lake/shore mouths and gentle downstream
flow; lakes and coastal water remain level.
The visible surface reserves every cell it crosses; widening cannot consume an occupied
or zoned plot. Cobblestone's firmer, more usable surface carries **20 trips per centerline
cell per week**, versus **6** for dirt and **60** for King's Roads.
 Roads the player builds cost 1g, 2g, or 3g
per cell each week at full funding for dirt tracks, cobbled roads, and King's Roads respectively.
Road/building menus include registered player-placeable types. Service menus show each building's
footprint: a Sheriff's Hall occupies 2x1 cells, a Parish Church 2x2, and a Castle 4x3.
The left icon sidebar opens Roads or an individual service category. Choose a road or building
to see a rendered preview of the actual model and its name, its full description, and a full-width statistics/effects
details table below it. Cost and Upkeep appear first; Does and Placement rows are
omitted. Cost shows only the gold amount and Footprint only the dimensions.
Click another sidebar icon to switch the open catalog directly.
Submenus open at the map's top-left. Building choices have enlarged names and cached model thumbnails;
statistics/effects and descriptions use tightly padded bordered tables.
The game menu, quit confirmation, guide, and other regular dialogs remain centered
on the map, adjusted to stay on screen when space is limited.
Road previews show their real surfaces and widths; building thumbnails reuse the
same compact, detailed medieval models as the map instead of Unicode illustrations.
Homes vary their timber framing, roof form, shutters, masonry and chimneys. Commerce
uses merchant stalls/shopfronts; industry uses smithies, workshops, mills and forges.
The **Add** button, with green text on a gray background, starts building placement or road-line drawing on the map.
Building placement hides the yellow cursor highlight, leaving only green/red placement indicators.
During building placement, **[ / ]** rotates the building 90° left/right, including its footprint.
The placed building keeps its facing through save/load. Outside placement, these keys rotate the map.
Map controls remain available while placing: use **+ / - / 0**, the minimap zoom buttons,
Ctrl/Command-wheel, or pinch to zoom; **ROTATE** or **Shift+[ / Shift+]** to turn the camera;
and **TILT** to change its angle. Middle-drag, trackpad/wheel scrolling, minimap clicks/drags,
overlays, and enabled edge scrolling also work without cancelling the placement tool.
Camera controls preserve the preview's plot and building facing until the placement cursor moves.
The icon rail has one shared double-line outer border and a single white outline around each icon.
Buildings are listed vertically in single-bordered choices sized to fit their names,
with one enclosing single-white border around the complete list,
ordered by unlock progression then cost, with the first entry's details shown immediately.
Statistics labels retain the width of the longest label. Colored catalog section headers use
single-white borders.
The description text has one enclosing box without a header; the details table
also has no separate heading and uses uniform thin white lines.
Locked buildings show their unlock requirements and cannot be added. **Cancel**, **Esc**, or
clicking outside the catalog closes it without building or spending gold.
Buildings use original pixel art inspired by classic city-builder maps at every zoom:
an elevated oblique view, broad shaded roofs, short walls, planted plots and paved courtyards.
Medieval cottages, markets, mills, forges, churches and castles have distinct compact layouts,
not tall front-facing icon silhouettes. Roofs still rise above the logical grid.
The default map uses a tilted orthographic camera with actual building walls and roofs.
Its initial viewing angle faces buildings toward screen-right.
**ROTATE** above the minimap advances 90 degrees; `[` and `]` rotate left and right.
**TILT** cycles through 20, 35 and 50 degrees. Rotation changes the viewing angle, not the buildings'
architecture or simulation footprint. Larger plots expand buildings/courtyards horizontally,
not their tower height. Selection, placement, panning and building clicks use the camera projection.
The minimap stays flat and square. Its entire contents rotate 90 degrees with the map;
the white indicator traces the camera's actual ground footprint and changes angle/shape with
the view. Tilting changes that indicator, not the minimap's shape. Clicks and drags still
navigate to the corresponding world location.
The tilted view's normal **1x** framing is 80% closer than the initial 3D view; **2x** doubles that baseline.
This changes map magnification, not the size of the sidebar or dialogs.
Perspective panning includes half a viewport of space beyond map boundaries, so any edge
can be brought into the middle of the map panel. Off-map placement remains blocked.
Generated hill regions rise through four blocky terraces from their edges toward
their interiors. Dedicated road meshes follow graded centerlines, with narrow
shoulders and cut/fill walls derived from their actual edges. Junctions share
nonoverlapping surfaces rather than stacked road pieces.
Road types meet at ordinary intersections, not invented highway on-ramps.
Shallow-angle minor-road junctions slow travel. The Roads menu also offers a
prebuilt **Traffic Circle**: a 5x5 cobbled ring with a grassy center.
Water crossings and short, deep gaps between hills have bridge decks and supports.
Rivers have consistent downstream flow with gentle gradients; water never inherits
hill terrace levels. Coarse downstream motion remains visible with low effects at normal
and close zoom, including while paused. Effects-off and reduced-motion settings still stop it.
Rocks vary between boulders, slabs, standing stones, split pairs, scattered stones
and layered outcrops. Trees and rocks stay clear of the visible curved road bed.
The 3D map renders at native panel resolution with edge antialiasing; pixel textures remain nearest-filtered.
3D service range guides are circular screen-space overlays, unaffected by camera tilt or terminal-cell aspect.
Grass and water use visible coarse, restrained-color blocks rather than smooth gradients,
bright pixel flecks or thin wave lines. Tree canopies have clustered pixel-leaf textures and
layered cubic foliage matching the buildings. Woodland mixes spreading oaks, rounded
and tall broad-leaf crowns, and two stepped pine silhouettes, with stable variations
in height, width and leaf palette. Tree positions and gameplay remain unchanged.
Riverbanks use a smoothed
visual water contour instead of cell-shaped teeth; logical terrain and bridge rules are unchanged.
The visual direction takes cues from late-1990s medieval strategy games: original
pixel-textured stone, warm timber framing, thatch/slate roofs, and subdued countryside colors.
Subtle voxel-style details extend to varied cubic tree canopies, clustered angular boulders,
stepped hill/bank faces, stone pier bases and well rims, raised grain-store supports,
roof ridge blocks, and castle battlements. These are decorative; footprints and gameplay are unchanged.
Facade glazing uses centered bays aligned with the timber posts and floor bands, with a clear
central doorway. Battlements use spaced square blocks around open tower tops.
Watch House, Apothecary, Chapel and Tavern now use roomier **2x1** plots; costs and service
outputs are unchanged. Existing saved 1x1 versions retain their original footprints.
The minimap and text map retain representative glyphs.
Grass, water, shorelines, raised hills, trees and rocks use the same shaded pixel-art treatment.
Water edges use whole-cell blocky steps matching the hill terraces; roads retain
their existing curves and widths. Dry bank road endings stay ordinary roads,
not partial bridges, and their endcaps stop inside the last dry cell before water.
Coarse blue river patches travel downstream even while
gameplay is paused; effects-off and reduced-motion modes still freeze them.
Highway loops straighten grid staircases just like open roads, without repeated ripples.
Click any opaque part of a building, including raised roofs and overhangs, to select it;
right-click opens Building Status. Overlapping buildings pick front-to-back.
A click previews a whole building; larger selections tile complete, non-overlapping buildings.
Blocked footprints are skipped as a whole. Cost, upkeep and service supply are per building,
not per occupied cell; demolition of any part removes the entire building.

Selected area services and smoke/clean-air sources show faint white pixelated range circles,
including during valid placement previews. Selecting any cell of a building's footprint shows
its ranges; moving the selection away hides them. Service rings indicate approximate timely-response
targets, not limits; pollution/cleanup rings still represent actual spatial falloff.
The rings are translucent white; fully opaque glyphs beneath each source's outermost circle
identify its effects, side by side for multiple effects.
Range rings are drawn as screen-space circles using the horizontal cell scale.
Service crews and fuel/water deliveries follow least-time land routes. Long distances delay
first arrival and lower timely service quality, but never impose a distance cutoff.
Insufficient staffing adds queues. Bottom-right petitions and Building Status report waiting,
slow responses and near/at-capacity workloads; roads, nearer providers and funding help.
Area-service ranges are 50% larger: Fire Watch and Market Cross now have 27-cell timely
ranges, Apothecary and Chantry School 18, Sheriff's Hall 24, and Castle 57.
Costs/upkeep and pollution/cleanup ranges are unchanged.

Placement previews show valid/skipped cells and the price. **Enter/Y** confirms and **Esc/N**
cancels; insufficient funds leave the city unchanged. **T** or a selected road's **Add**
starts road drawing: move the pointer to an endpoint and **click to place the shown stroke immediately**,
or use arrows and **Enter**. Holding a click and dragging previews a further extension,
which is built on release. Each placement retains the selected road type and starts the next
stroke at its endpoint. **Esc** finishes road placement; middle-drag pans while the tool is active.
Traffic-circle placement also stays active until cancelled.
Perspective previews show the selected dirt, cobbled or King's Road surface, width, edging,
curves and bridges using the same mesh rules as completed roads, with red feedback for blocked cells.
Completed roads render normally while the tool remains active; only uncommitted changes stay ghosted.
Overlapping or adjacent nearly parallel roadbeds are rejected as a whole, without end fragments
or charges. Road extensions, upgrades and ordinary perpendicular/oblique junctions remain available.
While paused, Command/Ctrl+Z can undo the last stroke and resume the selected road tool.

Undo keeps the last **10 successful placement, zoning, dezone and demolition actions**.
Each confirmed undo restores a **whole-city snapshot**, not just selected cells, and
is available only while paused. Failed/cancelled actions do not consume history;
new/load clears it. Use **Command+Z on macOS** or **Ctrl+Z elsewhere**.
Building catalogs show meaningful fuel, water, grain, resident or job capacities
under the choices and in their details, alongside costs and upkeep.
Area services and pollution have full strength within their listed radius, then
fade smoothly to zero at 125% of that radius. The range overlay follows the same pattern.

### Calendar and economy

New cities begin in the current local year. Saves retain their starting year.
A year has 52 weeks and a week has 7 days.
The date header uses ordinal weeks such as **Week the 1st** and **Week the 22nd**.
The week comes first, followed by **AD** and the year (e.g. **Week the 17th AD 1289**).
Desktop new maps start at approximately the current real-world week (January-based
seven-day blocks, capped at week 52), then advance with gameplay. Loaded saves retain their date.
Padded week labels and reserved header slots keep changing values from shifting the layout.
Feast weeks instead display **Candlemas**, **Lady Day**, **May Day**, **Middsommer**,
**Lammas**, **Michaelmas**, **All Hallow's**, or **Christmas**, with a corresponding
pixel icon. These fixed feasts follow the game's January-based 52-week calendar;
they do not change simulation timing.
Growth is spread over the week and tax income arrives at its end.

The day bar uses `>` for today and `=` for completed days: `[>......]`, `[==>....]`, `[======>]`.

| Speed | Key | Real seconds per game week |
|---|---|---|
| Slow | `1` | 16.8 |
| Medium | `2` | 8.4 |
| Fast | `3` | 2.8 |

These defaults apply to new cities. Saves retain their configured clock timings.

Choosing a speed resumes the simulation. `P`/Space pauses or resumes it.
Space pauses/resumes in dialogs and placement previews; P retains menu shortcuts.
Explicitly resuming
lets time run without closing them. Use Enter for dialog actions.
While entering text, use Ctrl+P (Command+P on macOS) to toggle pause.
Pausing displays yellow **[PAUSED]** in place of the header's week-progress bar.
The speed indicator is omitted, and fixed header slots prevent any layout shift.
Date/progress fields have extra spacing; POP and funds occupy separate frames,
with no TREASURY label before the gold amount.
The weekly gold balance uses brackets without `/wk`; zero displays as **[0g]**.
**REPORTS** sits between POP and gold in its own header frame; click it for the weekly report.
**GUIDE** follows REPORTS and opens the guide. Below 900 logical pixels of window
width it disappears; **F6** remains available. POP is display-only.
**VIEWS** opens a horizontal overlay-icon bar inside the map's top-left corner,
including a clear overlay button. Close it by clicking **VIEWS** again or pressing **Esc**;
the chosen overlay remains active when closed. REPORTS also hides on narrow windows;
**F7** still opens reports.
Click the gold text itself to open economy; empty header space does not activate it.
Weekly Report's **Milestones** tab opens with the current settlement size and progress
toward the next one. Scroll down to review the other reached and upcoming milestones,
including their newly unlocked buildings/services and model illustrations.
Its **Overview, Population, Economy, Services, Growth, Food & Seasons, and Events** tabs cover city/map
details, demographics, jobs, income/expense breakdowns, supplies, growth blockers,
and recent events. Projected budgets are distinguished from completed-week results.
**F6 > Milestones** also retains a summary of sizes, thresholds and unlocks.
Reaching a new size pauses the game and opens **Your City Grew!**, with tables of
newly unlocked buildings and their key features. **Continue** is its only action and
closes the dialog while keeping the game paused; milestone browsing lives in Reports.
New milestones award a one-time gold grant scaled to newly unlocked building
costs—enough for the two cheapest new buildings, with larger sizes never receiving
a smaller grant. The amount is shown in the growth dialog and Milestones guide.
There is no pause dialog, fact or animation covering the map: building, selection,
camera movement and menus remain available while paused.
Space/P resumes and restores the day bar; Escape opens Game Menu without unpausing.
Dialogs and placement previews suspend gameplay; losing window focus stops updates. Returning after a stall does not
fast-forward the simulation.

## Interface

The interface uses a black background and closed double-white borders with square corners.
Frames stay separated; their borders never extend into neighboring frames or form crossing joins.
Top-header text is uniformly larger than body text and fits to narrower windows:

- **Left:** TermCity and the right-aligned city name.
- **Middle:** left-aligned year/Week, centered day bar, and right-aligned clock status.
- **Right:** left-aligned population and right-aligned budget; depleted funds are red.

PAUSED is yellow with a gentle four-second brightness/glow pulse, frozen while unfocused.
The sidebar retains its enclosing double-white border and an aspect-correct minimap sized to the
full available width where desktop space permits, plus `[-] 1x [+]` zoom controls, Demand and City.
Section titles use black backgrounds and the same double-white borders as the map.
The City table has no redundant column-heading row and shows only age groups/hearths, season,
grain, mood, supplies, service strength and the top complaint. Healthy readings are green and
warnings use gold or red. Population and treasury remain in the top header.
Mood shows its descriptive label without a numeric score. Services reads gray **None** until
at least one service building is placed; existing services show full or half strength.
Hover over any R/C/I Demand letter, bar or row for its demand, built/zoned/road-served cells,
expected arrival rate, growth blockers and pending removals. Demand reflects settlers
and supported businesses, not empty zoned land: zoning alone does not erase it.
Business demand needs enough residents and a viable trade climate; cramped plots or
missing deliveries can prevent growth. Fractional arrivals can leave some weeks empty.
Those same details are always available in
**F7 Report**, **F8 Growth**, and the full-rules City health report for keyboard users.
There is no separate Zones section and the sidebar never scrolls. Its minimap and table layout
adapt to smaller windows so all remaining details stay visible.
The sidebar has no Guide section; next-step advice remains in the **F6 > Start** dialog.
New games start in the **2x close view** to make compact building details readable.
The 1x view is eased back slightly; the close view is about 1.68 times its visual
magnification. The existing 1x/2x controls select these tuned view presets; overview zooms
retain their previous scale, and loading a save restores its saved zoom.
The map fills its frame with the same 12-logical-pixel inset on every side; partial cells are
clipped at the right/bottom edges. The map/sidebar gap is also 12 logical pixels.
Cell details appear in the bottom status line, alongside dimensions/counts for selections larger
than one cell and any current message.

In the perspective map, water keeps its stationary coarse blue texture. Sparse chunky ripples
travel downstream along rounded cell-to-cell tracks at 0.75 cells per second, with irregular
spacing, varied cluster sizes and subdued brightness. Occasional small splashes and fish jumping
in short arcs add life to rivers. Motion updates eight times per second without rebuilding terrain.
Most water pixels stay still; rivers continue flowing while gameplay is paused.
Effects-off and reduced motion freeze the decorative flow and hide splashes/fish; lakes/sea
do not receive river effects. River-adjacent hilltops retain their ordinary grass colors.

In the legacy cell view, about 10% of visible hills, trees, and water animate at 80 BPM (one step every 0.75 seconds).
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

- Click the treasury text (including gold and weekly amounts) to open the economy/budget menu.
  Click population to open the city menu. These shortcuts do not interrupt dialogs or placement.
  City information includes weekly reports, growth and health. Funding, taxes and loans
  are in the treasury menu rather than the city menu.

- The map starts without a highlighted cell; the bottom-left status reads **Press F1 for HELP**.
  Clicking a cell, moving with the keyboard, or starting placement activates the cursor.
  The shortcut toolbar is hidden; all keyboard shortcuts remain available.
- **F3 FONT** opens `[-] size [+]` with **OK** and **RESET**. Default interface size is 20;
  supported sizes are 16-28, with map spacing and input scaled consistently.
- Increasing font size grows the preferred 1440x900 window when desktop space permits, without
  exceeding the usable screen. Both sidebars fit without scrolling, including every construction
  icon at minimum window size; dialogs may scroll.
  The minimum logical window height is 780 (800 in Debug, to fit the Monuments entry) so the larger minimap and remaining details have room;
  actual minimum bounds remain capped to the usable desktop.
- Drag the map/sidebar divider to resize it, or use **Esc > Resize sidebar**.
  Sidebar widths are bounded to 280-480 logical pixels; the maximum reduces to preserve map space.
  Mouse dragging slides by pixels rather than snapping to character widths.
- Font size and Resize sidebar are simple game-menu rows that open separate adjustment dialogs.
  The **GAME MENU** uses all-caps options without descriptions and bottom-right
  **QUIT** (red text) and **CANCEL** buttons instead of navigation rows.
  Their underlined **Q** and **C** shortcuts activate those actions. MUSIC and SOUND
  controls appear directly in the same menu; there is no City Management option.
  White display-option labels have inline **-**/**RESET**/**+** controls.
  **RESET** restores that row's default. Audio sliders sit
  beside their mute buttons without percentage readouts.
  **CANCEL** starts focused with a subtle highlight, including when returning from a submenu.
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

The quiet medieval soundtrack shuffles **Greensleeves** and five original themes:
**Willow Court**, **Cloister Light**, **The Evening Reeve**, **River Lanterns**, and **Orchard Vigil**.
Gentle 48-56 BPM PCM16/chiptune arrangements alternate wind, strings and soft-brass melodies
with sustained harmonies, soft plucks and a restrained moving bass. Songs have silent transition
edges and do not repeat back-to-back. Greensleeves uses the traditional public-domain melody;
the other scores and all synthesis/arrangements are original.

Use **F10** or **Esc > GAME MENU** to mute or resume it. Music continues during game pauses/dialogs but
pauses when unfocused. Font and music preferences are remembered in `display.cfg`, separately from
city saves. No external recording, audio asset, or audio package is required.

### Debug monuments

Debug builds include a **Monuments** construction entry with an original colossal **Bewildered Duck
Monument**, clasping its helmet in its hands. Its normal unlock is 100,000 residents, but Debug
launches bypass that gate and construction cost for immediate inspection. The 8x6 footprint,
full-sculpture clicking, placement, demolition and saving use the normal building system.
This prototype is hidden and cannot be placed in Release builds.

## Controls

| Action | Input |
|---|---|
| Move cursor | Arrow keys |
| Jump a screen | Ctrl+arrows, Home/End/PageUp/PageDown |
| Resize selection | Shift+arrows, or Shift+click (moves the endpoint from the original anchor; can shrink) |
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
| Rotate map left / right | `[` / `]` (90-degree steps) |
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
| Game configuration menu | Esc during normal gameplay (display, inline sound/music controls; Q quits and C cancels) |
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
While **GAME MENU** is open, the seed and map dimensions appear at the **bottom left**
and the quick-save path at the **bottom right**. Closing it restores gameplay status.

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
- **No sound:** check **Esc > GAME MENU**, OS output volume, and whether the window is focused.

## Development and license

```bash
dotnet build TermCity.slnx -c Release
dotnet test TermCity.slnx -c Release --no-build
```

Engine integration tests, export commands and extension examples are in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
TermCity is MIT licensed; see [LICENSE](LICENSE). The bundled DejaVu font retains its
[license](godot/Assets/DejaVu-LICENSE.txt).
