# City Grew! progression playtest report

## Method and measured play time

Played the actual default **Full** simulation through paid player actions on small (160x96),
medium (320x192) and large (640x384) generated maps, using seeds **42** and **137**.
After diagnostic replays and refinements, ran all six combinations twice: **12 completed
playthroughs in the comparison batches**, each reaching the final **10,000-person Great City**
milestone, in addition to a successful small-map diagnostic replay.

These were **automated, accelerated gameplay runs**, not real-time manual desktop sessions.
`AdvanceWeek` executes the same growth, ageing, jobs, services, harvest, tribute, disasters and
treasury rules as normal gameplay. Only the waiting between weeks is skipped. No starting-money
override, free buildings, population injection, terrain clearing, loans or disabled disasters
were used in these playthroughs. Targeted unit tests separately inject population to test exact gates.

The two successful six-city batches simulated **8,552 game weeks**, equivalent to **199.55 minutes
at fast speed** (1.4 seconds/week). Their measured simulation/policy execution time totalled
**73.36 seconds**; test-runner overhead is additional. The final batch took 36.08 seconds inside
the gameplay cases. Individual game-time equivalents below exclude placement decisions,
milestone dialogs, other pauses and menu navigation, so manual play will take longer.

Every run saved and loaded at week 100 and continued to the final milestone. The six completed
cities were also saved to the session artifacts and successfully loaded by the Godot frontend
with `--load ... --dump-map`, confirming the correct seed and map dimensions.

## Final-batch results

| Map | Seed | Weeks to final milestone | Fast-speed equivalent | Final population | Final gold | Lowest gold | Weekly net | Execution |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Small | 42 | 774 | 18.06 min | 10,008 | 813,177g | 8,468g | +23,393g | 4.47 s |
| Small | 137 | 601 | 14.02 min | 10,066 | 469,746g | 8,391g | +24,327g | 2.53 s |
| Medium | 42 | 896 | 20.91 min | 10,008 | 295,951g | 7,991g | +18,152g | 10.67 s |
| Medium | 137 | 582 | 13.58 min | 10,032 | 410,380g | 8,484g | +23,466g | 3.37 s |
| Large | 42 | 604 | 14.09 min | 10,039 | 235,250g | 8,846g | +23,337g | 6.25 s |
| Large | 137 | 819 | 19.11 min | 10,017 | 235,174g | 8,557g | +15,380g | 8.79 s |

All six ended solvent, debt-free, with a positive weekly surplus and every default player-building
city-size gate open. Final happiness ranged from **48.8 to 62.3**: the game still requires care,
and a completed milestone does not imply a perfectly happy city. Weekly net does not include
variable grain purchases, tribute or losses from disasters.

### Milestone timing, in game weeks

| Map / seed | 100 | 500 | 1,000 | 2,500 | 5,000 | 10,000 |
|---|---:|---:|---:|---:|---:|---:|
| Small / 42 | 14 | 239 | 390 | 544 | 664 | 774 |
| Small / 137 | 13 | 183 | 300 | 418 | 502 | 601 |
| Medium / 42 | 14 | 182 | 394 | 600 | 736 | 896 |
| Medium / 137 | 13 | 149 | 251 | 394 | 495 | 582 |
| Large / 42 | 14 | 184 | 279 | 401 | 501 | 604 |
| Large / 137 | 13 | 232 | 331 | 460 | 631 | 819 |

A peak can be reached during the final day's arrivals and then dip below the threshold during
weekly deaths. That still earns the achievement; the final implementation records it before mortality.

## Refinements from replaying

1. **Starter supplies were unaffordable together.** A Woodlot cost 45,000g and a Town Well
   12,000g against a 50,000g treasury. Changed them to **8,000g / 40g weekly upkeep** and
   **4,000g / 20g weekly upkeep**, retaining their 300 fuel and 260 water capacities.
   The starting treasury remains **50,000g**. Supplies now leave 38,000g before roads.
   No additional starter building type was needed.
2. **The guide resumed too early.** Reordered startup around road access, homes, then both
   supplies **before resuming**. The live sidebar detects absent or disconnected supplies
   and shortages instead of assuming zoning alone is enough.
3. **Pre-existing highways drained the new settlement.** King's Roads formerly charged
   4g per cell each week, making startup depend on map-wide highway length. They are now
   **maintained by the crown with zero city upkeep**, including player-built King's Roads.
   Construction costs remain unchanged; tracks and cobbles still charge 1g/2g per cell weekly.
4. **A destroyed starter utility could trigger a death spiral.** One early replay lost its
   Woodlot and went bankrupt at week 53. The guide now preserves a replacement reserve and
   explicitly prioritizes rebuilding supplies. The replay strategy spends that reserve on
   emergency replacement rather than treating it as untouchable.
5. **Mixed workshops and homes caused a smoke-driven stall.** Separated industry from housing
   in subsequent play. Coaching and startup instructions now explain this rather than
   encouraging indiscriminate zoning. Smoke, unemployment and disaster rules remain active.
6. **Buying cheap services repeatedly prevented saving for the important ones.** Several
   diagnostic runs stagnated in the hundreds of residents, even with spare cash. The guide
   now prioritizes the largest happiness complaint, advises saving for it and suggests
   **75% local-service funding** (about 84% effectiveness). Basic care prices/upkeep were
   otherwise left unchanged.
7. **Density eligibility came too late relative to progression.** Level-2 eligibility moved
   from 1,500 to **500** and level-3 eligibility from 9,000 to **5,000**, matching announced
   milestones. Land value, road class, supplies and a suitable lord's seat are still required.
8. **An overfilled district could lack room for care or new utilities.** Later play extended
   short paid tracks into open land, retained service plots and reclaimed empty zoned lots.
   City-wide supplies can be placed along other connected roads; care must remain near homes.
   The guide explicitly explains both distinctions. Waiting for every unattractive empty
   home to fill is not a good expansion strategy.

The longest diagnostic replays ran for **4,000 weeks** each (93.33 fast-speed-equivalent minutes)
before being classified as stalled. These failed experiments are not counted in the 12 successful
playthroughs or the 199.55-minute successful-run total.

## Progression delivered

| Population | Milestone | Newly available |
|---|---|---|
| 100 | Hamlet | Tavern |
| 500 | Village | Stone Keep, Hospice, Parish Church; level-2 redevelopment eligibility |
| 1,000 | Market Town | Gaol, Infirmary, Monastery, Guildhall |
| 2,500 | Borough | Castle |
| 5,000 | City | Cathedral; level-3 redevelopment eligibility |
| 10,000 | Great City | Final achievement; all default city-size gates open |

Essential smaller services stay available from the start. Previously gated building population
requirements round up to the next milestone. Existing placed services are not removed or disabled.
Achievements are permanent across population decline and save/load. Turning off the sidebar guide
does not affect gates or announcements; F6 can enable it again, and F7 always lists progress.

## Verification and reproduction

- **640 non-playthrough tests passed**, including inclusive gates for every gated default building,
  atomic rejection, quotes/previews/menus, guide toggling, save/load, decline and final-day deaths.
- **All six end-to-end playthrough cases passed**, twice across the successful batches.
- The solution build passed. One existing nullable warning remains in Godot's smoke-test code.
- Godot 4.7.2 headless engine/input/UI integration returned `TERMCITY_GODOT_SMOKE_OK`.
- All six final save files loaded successfully through Godot at their respective dimensions.

```bash
dotnet build TermCity.slnx
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj \
  --filter 'Category=Playthrough' --logger 'console;verbosity=detailed'
```

Set `TERMCITY_PLAYTEST_OUTPUT` to an artifact directory to retain final cities:

```bash
TERMCITY_PLAYTEST_OUTPUT=/absolute/path/to/artifacts \
  dotnet test tests/TermCity.Tests/TermCity.Tests.csproj --filter 'Category=Playthrough'
godot --path godot -- --load /absolute/path/to/artifacts/small-42.json
```

The replay policy is in [GuidedPlaythroughTests.cs](../tests/TermCity.Tests/GuidedPlaythroughTests.cs). It follows the documented
startup and expansion approach using normal player actions. It does not replace manual usability
testing or establish balance for every possible seed; this report covers the six specified combinations.
