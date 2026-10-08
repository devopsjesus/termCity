# Population and city engine

TermCity plays by one of two rule sets (`GameConfig.Rules`):

- **Classic** is the original sandbox: a steady stream of families, a flat tax, no utilities and no disasters. Saves made
  before the full engine load as Classic, and the unit tests that predate it run on it.
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
| No road access | 10 |

Service *need* grows with population (fire from 300 people, the sheriff 600, physic 700, schools 450, greens 900, defence 400, faith 500), so a
hamlet is not punished for lacking a sheriff it does not need yet. Jobs come from working commercial and industrial buildings plus a base of
informal work, so a few families are not "unemployed" before the first shop opens.

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

Buildings come in three levels (house/apartments/tower, shop/office/skyscraper, factory/plant/complex). A building may
upgrade when population, land value (scaled by the scenario's density appetite), road class, power and water, and local
demand all allow it, and downgrades when land value collapses. City Grew! permanently opens population
eligibility at 500 souls for level 2 and 5,000 for level 3. Road class, utilities, land value and the
lord's seat still apply. Current population or the highest achieved milestone satisfies the size gate.

## City-size progression and the optional guide

`CityProgression` is the shared source of milestones and building gates. A building's `MinPopulation`
rounds up to the next milestone (100, 500, 1,000, 2,500, 5,000, 10,000); zero remains ungated.
The simulation records every crossed milestone, including on the final day before weekly mortality.
The session announces all newly crossed stages together in a pausing City Grew! dialog. The service
menu, placement preview, quote and final action all use the same permanent unlock check.
Existing saves retain their achieved population milestones and normalize current population on load;
already-built services continue working even if they now have a placement gate.

The sidebar guide follows road access, supplies, first occupied homes, employment, finances, happiness
complaints, space for expansion and the next milestone. Dismissing it changes only coaching, and
F6 can enable it again. Unlock announcements and the F7 milestone list are always available.

## Money

Gold is the currency (`Fmt.Money` prints `15,000g`). Tithes and rents (the tax) = filled cell value x tax rate, reduced by unemployment and unfilled jobs and lifted by education.
Starter supplies are a Woodlot (8,000g; 300 fuel; 40g/week) and Town Well (4,000g; 260 water; 20g/week).
They remain ungated and leave room in the 50,000g initial treasury for roads, care and reserves.
An Aqueduct is a larger option at 14,000g and 60g/week; terrain can increase its construction cost.
Every utility must have at least one footprint cell served by an edge-connected road. Disconnected
utilities produce nothing but still charge upkeep, and homes without fuel or water pay no taxes.
Placement warns about inactive services without preventing construction; connecting a road activates
them. The building menu shows weekly upkeep, and the cell inspector shows active/inactive status.
Within each service family, higher-priced tiers provide more output and lower upkeep per unit.
Utilities are compared by supply; area services use the sum of actual strength-weighted coverage
over unobstructed land. At full funding, every upgrade has a lower construction-plus-upkeep cost
per output unit within 52 weeks on flat ground. Starter affordability, capacities, coverage,
population gates and special benefits or drawbacks are unchanged.

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
Road access retains the normal two-cell land-based reach and also includes clear ground within
0.75 cell widths horizontally and 0.75 cell heights vertically of a connected visible road bed.
Disconnected roads never grant access. Existing buildings are not removed by this clearance rule.
The crown maintains the King's Road at no cost to the city; tracks and cobbles retain their upkeep.
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
city is. Over two game years from the default start:

| City | Trajectory |
|---|---|
| San Francisco | Booms: high appeal, well served, little room, so it grows taller |
| Chicago | Steady growth on a deep trading and craft base |
| San Diego | Slow growth; water-limited and fire-prone |
| Los Angeles | Flat; smoke and crowding hold it back |
| St. Louis | Shrinks: thin services, flood and crime, low appeal. Fix services to turn it around |

(The old San Francisco, Chicago, San Diego, Los Angeles and St. Louis save names read as these cities.)

## Playing it

- **City menu > Budget, taxes and loans** sets funding per service, taxes and borrowing.
- **City menu > City health report** lists every indicator, complaint and the latest events.
- The sidebar shows mood, jobs, utilities, the season and grain store, standing, net income and the top complaint; the inspector shows power, water,
  happiness and land value per cell.
- Place fuel, water, fire watches, sheriffs, apothecaries, schools, greens, castles, churches, markets and granaries from
  the area menu (**Service buildings**).
