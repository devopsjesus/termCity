# Population and city engine

TermCity plays by one of two rule sets (`GameConfig.Rules`):

- **Classic** is an explicit sandbox ruleset: a steady stream of families, a flat tax, no utilities and no disasters.
  It is also used by focused simulation tests, but never selected as a fallback for incomplete saves.
- **Full** is the default for new games. Everything below describes Full.

Everything is deterministic for a seed. Nothing in `TermCity.Core` knows about the UI.

## The loop

Each game week, in order:

1. **Arrivals** (`CityGame.PlanWeek`/`MoveIn`): families arrive at a rate of
   `MaxNewResidentialPerWeek + max(filled homes, population / 5) x MigrationRatePerWeek`, scaled by the city's
   *attraction* and stochastically rounded. A hamlet always gets its few families; a city's inflow compounds with size.
2. **Businesses** open in proportion to residents and the *business climate*.
3. **Population engine** (`PopulationEngine.RunWeek`): the harvest and grain store, births, ageing, deaths, emigration,
   abandonment, business closures, density changes, feast days, then disasters (fires, raiders, plague, floods, quakes).
4. **Settlement** (`Settlement.RunWeek`): the town's rank and, at Michaelmas, the crown's tribute.
5. **Treasury**: tithes and rents in, upkeep out, interest on any loan, and an insolvency event if money runs out.

## What people feel

### Demand and vacant land

Demand bars describe **growth pressure**, not a shortage of zoned land. Empty zoning
does not satisfy resident or business needs and does not change the bars by itself.
Residential pressure comes from the same expected weekly migrant-family rate used
by `CityGame.PlanWeek`, including attraction and seasonal travel under Full rules.
Positive pressure can therefore remain visible while families move into zoned homes;
existing homes with spare room are filled before new houses are built.
Within each group, residents prefer the shortest reachable road approach, measured from
the property perimeter to the visible road bed. Equally close choices remain varied;
farther plots and roadless settlements remain valid when nearer space is unavailable.
This preference does not change arrival quotas or require an outside-connected road.

Commercial and industrial pressure uses the lesser of expected weekly business
arrivals and remaining supported building slots, divided by total supported slots.
The arrival rate includes the same business-climate multiplier used by weekly growth.
Missing customers, full supported capacity or zero trade climate show zero demand,
even when many plots have been zoned. Fractional arrival rates mean some weeks may
have no growth; the bars are forecasts, not promises that every plot fills immediately.
Weekly arrival quotas are sampled at the start of each week. Forecasts reflect current
conditions; arrivals already scheduled for this week may finish after conditions change.

Full-rules business demand and growth capacity share the same housing measure:
the larger of households (including pending residential removals) and population
divided by five. Classic uses occupied residential buildings. Existing business
buildings pending removal still count until they are actually removed.
`Demand.ExpectedArrivals` and `Demand.SupportedBuildings` are shared by growth
planning and demand computation; reading a forecast does not advance random state.
Demand tooltips and reports show forecast rates and separate local blockers:
missing complete plots, road overlaps, unreachable land and low attraction/trade climate.
Demand may be positive while a particular plot is unsuitable; that is not missing demand.

`CityAnalysis.Assess` builds a `CityIndicators` snapshot whenever the layout, budget or week changes. Each home has a
happiness of 70 minus charges, each in points:

| Charge | Cost |
|---|---|
| No power / no water | 30 / 25 |
| Smog | 0.32 x smog x scenario sensitivity |
| Crime | 0.28 x felt crime (the sheriff, greens, taverns, the faith and schools cut it) |
| Fire, health, schools, greens | up to 12 / 14 / 9 / 8, scaled by how much of the city's need is unmet |
| Unguarded (no castle or watch reaching a big enough town) | 7 |
| No solace (no chapel or church in a town big enough to want one) | 4 |
| Hunger | scaled by how far the grain store has run dry |
| Traffic | up to 18 |
| Unemployment | 45 x (rate - 6%) |
| Taxes | rate above the scenario's fair rate, clamped to -4..20 |
| Slow walking journeys without nearby roads | 5 |

Service *need* grows with population (fire from 300 people, the sheriff 600, physic 700, schools 450, greens 900, defence 400, faith 500), so a
hamlet is not punished for lacking a sheriff it does not need yet. Jobs come from working commercial and industrial buildings plus a base of
informal work, so a few families are not "unemployed" before the first shop opens.

Locked services are not expected: their missing coverage does not reduce happiness or
trigger petitions until at least one building of that service kind is permanently unlocked.
Fire, sheriff, physic and learning begin at Growing Hamlet (250 souls); commons begin at 500.
Faith, defence and starter supplies have ungated options. Expectations remain available
after a population decline, matching permanent building unlocks. Status reports label
locked services "not expected"; newly available services distinguish missing dispatch,
slow arrivals and capacity.

## Attraction and climate

- **Attraction** = comfort (happiness 32 to 70 mapped to 0..1) x job availability x scenario appeal. Below happiness 32
  nobody new arrives. It scales arrivals from 0 (none) to 2 (a boom).
- **Business climate** falls with excess taxes, unfilled jobs, crime, congestion and shortages of power or water. Below
  about 0.4 businesses close.

## Demographics

Medieval lives are short and hard (see [MEDIEVAL.md](MEDIEVAL.md)):

- Children grow up in about 18 years; adults retire after about 47 working years, though 20% of elders still work
  and 12% of children already herd, glean or are apprenticed.
- Yearly mortality: 5% children, 1.8% adults, 16% seniors, reduced by physic (the Health service).
- Births: 0.14% of adults each week, scaled by happiness.
- Emigration: when happiness is under 42 (or unemployment high) the unhappier of two random homes loses 1-3 people.
- Homes below happiness 40 can be abandoned; failing businesses close.
- Famine adds deaths and emigration; plague adds weeks of extra deaths blunted by physic.

## Seasons, grain and famine

Grain management starts at the permanent **Village (500 souls)** milestone.
Market Cross and Granary unlock at the same milestone; Guildhall keeps its later gate.
Before Village there are no grain imports, consumption, hunger penalties, famine events,
or grain petitions. The sidebar shows the unlock instead of a warning gauge.
After reaching Village, grain rules remain active even if the population later falls.

The year starts in midwinter: winter weeks 49-9, spring 10-22, summer 23-35, autumn 36-48. Farmland yields at harvest
(`Harvest`): the town keeps a grain store measured in weeks of need, households keep up to 10 weeks in their own bins and a
Granary raises the cap (up to 78 weeks). The harvest quality is rolled each autumn (volatility scaled by the scenario),
so a poor harvest drains the store. A town with an empty store buys grain from merchants for gold if it can; a Market
Cross or Guildhall widens the reach and cuts the price. With neither grain nor gold, **Hunger** rises, happiness drops,
people die and leave. Summer is the season of plague and fire (`Seasons.PlagueFactor`, `FireFactor`); winter slows travel.

## Disasters

- **Fire**: ignition 0.009% per building per week (more in summer, more in tall wooden buildings); it spreads by fuel and
  is stopped by a Fire Watch.
- **Plague**: 0.6% weekly chance in a town of 250 or more; it lasts weeks and kills 1.2% of the town a week without physic.
- **Raids**: 0.7% weekly chance in a town of 150 or more, higher with hunger and tribute arrears. A garrison (castle tier)
  or a sheriff turns them away; an unguarded town is sacked.
- **Floods** and **earthquakes** are unchanged and scale with the scenario's risk.

## Settlement: rank and tribute

`Settlement.RankOf` assigns a rank from size and standing: Hamlet, Village (120 people), Market Town (800 and a market),
Borough (5,000, a market and a keep) and City (25,000, a market, a castle and a church). Each rank above Hamlet lifts the
tithes on shops and workshops by 3%. At Michaelmas (week 39) the crown's reeve takes about 2g per soul, trimmed by the
strength of the lord's seat (12% per tier) and the rank (8% per rank). A town of under 150 people is below notice. A
shortfall becomes **arrears**, added to next year's bill, and makes raiders bolder.

## Density

Starter homes, market stalls and workshops normally occupy one logical cell.
Growth may choose a rare 1x2, 2x1 or 2x2 variant, but new default zoned buildings
never exceed 2x2. A single zoning click reserves one minimum 1x1 plot, so thin
strips and lone clear cells can settle. A larger visual plot is still one building:
capacity, households, jobs, taxes and upkeep are not multiplied by footprint area.
Compact footprints retain existing per-building population/economic capacities and tier gates.

Buildings come in three levels (house/apartments/tower, shop/office/skyscraper, factory/plant/complex). A building may
upgrade when population, land value (scaled by the scenario's density appetite), clear expansion space, fuel and water, and local
demand all allow it, and downgrades when land value collapses. City Grew! permanently opens population
eligibility at 500 souls for level 2 and 5,000 for level 3. Utilities, land value and the
lord's seat still apply. Current population or the highest achieved milestone satisfies the size gate.

## City-size progression and the optional guide

`CityProgression` is the shared source of milestones and building gates. A building's `MinPopulation`
rounds up to the next milestone (100, 250, 500, 1,000, 2,500, 5,000, 10,000); zero remains ungated.
The simulation records every crossed milestone, including on the final day before weekly mortality.
The session announces all newly crossed stages together in a pausing City Grew! dialog. The service
menu, placement preview, quote and final action all use the same permanent unlock check.
Existing saves retain their achieved population milestones and normalize current population on load;
already-built services continue working even if they now have a placement gate.

The F6 guide's Start-tab advice follows road access, supplies, first occupied homes, employment, finances, happiness
complaints, space for expansion and the next milestone. Dismissing it changes only coaching, and
F6 can enable it again. Unlock announcements and the F7 milestone list are always available.

## Money

Gold is the currency (`Fmt.Money` prints `15,000g`). Tithes and rents (the tax) = filled cell value x tax rate, reduced by unemployment and unfilled jobs and lifted by education.
Starter supplies are a Woodlot (8,000g; 300 fuel; 40g/week) and Town Well (4,000g; 260 water; 20g/week).
These starter fuel/water supplies remain ungated and leave room in the 50,000g initial treasury for roads, care and reserves.
An Aqueduct is a larger option at 14,000g and 60g/week; terrain can increase its construction cost.
Utilities operate without roads. Fuel and water deliveries must have time to arrive over land
or bridges, and demand must fit supply. Unsupplied homes pay no taxes.
Area services route to residents by least travel time, without a hard radius limit. Their radius
sets a walking-distance timely-response target (`radius * 6 / 24` game days). Open land costs 6 travel units per step,
compacted paths 2, roads 1/rank; terrain modifies walking cost. Longer trips reduce timely
coverage but never end it. First arrival is delayed by travel time after construction.
Area service staffing is `max(300, radius² * 6) * effective funding` residents per provider.
Residents use their nearest provider; overload increases response time and reduces quality.
Bottom-right petitions distinguish waiting for arrival, slow response and nearing/full capacity.
Starter prices, utility outputs, population gates and special benefits remain unchanged.

Area-service ranges are 50% larger than the previous defaults (rounded up). Examples:
Fire Watch 27, Apothecary/Chantry School 18, Sheriff's Hall 24, Village Green 12,
Parish Church/Market Cross 27, Monastery/Stone Keep 42 and Castle 57 cells.
The increased ranges also enlarge response targets and radius-based staffing; prices
and upkeep are unchanged. Pollution and air-cleanup radii are not enlarged.

| Rebalanced upgrade | Construction | Upkeep/week |
|---|---:|---:|
| Charcoal Burners | 34,000g | 100g |
| Aqueduct | 14,000g | 60g |
| Gaol | 26,000g | 180g |
| Tavern | 6,000g | 35g |
| Stone Keep | 38,000g | 200g |
| Castle | 85,000g | 280g |
| Parish Church | 26,000g | 130g |
| Cathedral | 110,000g | 280g |
| Guildhall | 28,000g | 160g |

Road tiers already have better construction and running cost per unit of traffic capacity.

Buildings occupy complete logical cells. New civic buildings and automatic zone growth reject
cells intersected by the visible road bed, including angled-road smoothing and junction blends.
Road proximity gives a land-value and travel bonus, including roads not connected to the map edge.
Walking is permitted anywhere on land; bridges allow water crossings.
Buildings wear a new path segment toward the nearest reachable road each day; repeated journeys
compact it into a faster path. Paths persist but are not paid roads.
Established home gardens are protected from footpath wear and rendering. A journey may
meet a garden at its boundary, but cannot cut through it or hide its lawn and planting.
Paths connect to ordinary road approaches, not bridge decks or sides. Bridges remain
usable for normal travel and service deliveries, including crossings over water.
Natural footpaths never grow or render on hill terrain, even on flat terrace tops or
hill remnants leveled by nearby road cuts. Roads and ordinary service travel can still
cross hills. Existing hill wear stays in saves but is not drawn.
Existing buildings are not removed by the road-clearance rule.
Roads generated with the map have no upkeep. The city pays upkeep only for roads the player builds.
Expenses are the upkeep of every civic building (scaled by the funding level), local road upkeep, loan interest and
administration: a share of tax income (up to 55%) that grows with population from 2,000 up to 50,000 people, so large
cities cannot coast on surpluses.

The weekly finance forecast excludes variable grain purchases, tribute and disaster losses. Keep a
cash reserve for those, as well as at least 8,000g to replace a lost starter fuel supply. Care should
be added near homes and prioritized by actual complaints; smoky workshops belong in a separate district.

Funding a service below 100% saves money but gives only `funding^0.6` of the benefit (half funding is about two
thirds as good); an insolvent city's services run at half strength. Power and water are always paid in full. Loans are
limited to 40 weeks of income and cost 0.2% a week.

## Events

Fires, plague, raids, famine, floods and earthquakes, harvests, feast days and rank changes are reported through
`CityGame.EventOccurred` and the `Events` list. Milestones, upgrades, closures and insolvency are events too.

## Scenarios

`CityProfile.For(scenario)` tilts the shared engine (appeal, water, power load, crime, fire, flood, quake, smog, car
dependence, density appetite, fair tax). The pre-built cities are seeded by `ScenarioSeeder` with power plants, water
works and a lattice of civic buildings sized to the city, thinner or thicker depending on how well provided for the real
city is. Full service plots are reserved before the remaining district plots receive buildings and households.
Over two game years from the default start:

| City | Trajectory |
|---|---|
| San Francisco | Booms: high appeal, well served, little room, so it grows taller |
| Chicago | Steady growth on a deep trading and craft base |
| San Diego | Slow growth; water-limited and fire-prone |
| Los Angeles | Flat; smoke and crowding hold it back |
| St. Louis | Shrinks: thin services, flood and crime, low appeal. Fix services to turn it around |

## Playing it

- **City menu > Budget, taxes and loans** sets funding per service, taxes and borrowing.
- **City menu > City health report** lists every indicator, complaint and the latest events.
- The sidebar shows mood, jobs, utilities, the season and grain store, standing, net income and the top complaint; the inspector shows power, water,
  happiness and land value per cell.
- Place fuel, water, Fire Watch buildings, sheriffs, apothecaries, schools, greens, castles, churches, markets and granaries from
  the area menu (**Service buildings**).
