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
3. **Population engine** (`PopulationEngine.RunWeek`): births, ageing, deaths, emigration, abandonment, business
   closures, density changes, then disasters.
4. **Treasury**: tax in, upkeep out, interest on any loan, and an insolvency event if money runs out.

## What people feel

`CityAnalysis.Assess` builds a `CityIndicators` snapshot whenever the layout, budget or week changes. Each home has a
happiness of 66 minus charges, each in points:

| Charge | Cost |
|---|---|
| No power / no water | 30 / 25 |
| Smog | 0.32 x smog x scenario sensitivity |
| Crime | 0.28 x felt crime (police, parks and schools cut it) |
| Fire, health, schools, parks | up to 12 / 14 / 9 / 8, scaled by how much of the city's need is unmet |
| Traffic | up to 18 |
| Unemployment | 45 x (rate - 6%) |
| Taxes | rate above the scenario's fair rate, clamped to -4..20 |
| No road access | 10 |

Service *need* grows with population (fire from 300 people, health 450, schools 700...), so a hamlet is not punished for
lacking a police force it does not need yet. Jobs come from working commercial and industrial buildings plus a base of
informal work, so a few families are not "unemployed" before the first shop opens.

## Attraction and climate

- **Attraction** = comfort (happiness 32 to 66 mapped to 0..1) x job availability x scenario appeal. Below happiness 32
  nobody new arrives. It scales arrivals from 0 (none) to 2 (a boom).
- **Business climate** falls with excess taxes, unfilled jobs, crime, congestion and shortages of power or water. Below
  about 0.4 businesses close.

## Demographics

- Children grow up in about 18 years; adults retire after about 47 working years.
- Yearly mortality: 0.06% children, 0.3% adults, 6.5% seniors, reduced by health cover.
- Births: 0.06% of adults each week, scaled by happiness.
- Emigration: when happiness is under 42 (or unemployment high) the unhappier of two random homes loses 1-3 people.
- Homes below happiness 40 can be abandoned; failing businesses close.

## Density

Buildings come in three levels (house/apartments/tower, shop/office/skyscraper, factory/plant/complex). A building may
upgrade when population, land value (scaled by the scenario's density appetite), road class, power and water, and local
demand all allow it, and downgrades when land value collapses. Upgrades are gated by population: 1,500 for level 2 and
9,000 for level 3.

## Money

Tax income = filled cell value x tax rate, reduced by unemployment and unfilled jobs and lifted by education.
Expenses are the upkeep of every civic building (scaled by the funding level), road upkeep, loan interest and
administration: a share of tax income (up to 55%) that grows with population from 2,000 up to 50,000 people, so large
cities cannot coast on surpluses.

Funding a service below 100% saves money but gives only `funding^0.6` of the benefit (half funding is about two
thirds as good); an insolvent city's services run at half strength. Power and water are always paid in full. Loans are
limited to 40 weeks of income and cost 0.2% a week.

## Events

Fires (damped by fire cover), outbreaks (health cover), floods (low land beside water, mostly in spring) and earthquakes
(scenario risk) are reported through `CityGame.EventOccurred` and the `Events` list. Milestones, upgrades, closures and
insolvency are events too.

## Scenarios

`CityProfile.For(scenario)` tilts the shared engine (appeal, water, power load, crime, fire, flood, quake, smog, car
dependence, density appetite, fair tax). The pre-built cities are seeded by `ScenarioSeeder` with power plants, water
works and a lattice of civic buildings sized to the city, thinner or thicker depending on how well provided for the real
city is. Over two game years from the default start:

| City | Trajectory |
|---|---|
| San Francisco | Booms: high appeal, well served, little room, so it grows taller |
| Chicago | Steady growth on a deep industrial base |
| San Diego | Slow growth; water-limited |
| Los Angeles | Flat; smog and traffic hold it back |
| St. Louis | Shrinks: thin services, high crime, low appeal. Fix services to turn it around |

## Playing it

- **City menu > Budget, taxes and loans** sets funding per service, taxes and borrowing.
- **City menu > City health report** lists every indicator, complaint and the latest events.
- The sidebar shows mood, jobs, utilities, net income and the top complaint; the inspector shows power, water,
  happiness and land value per cell.
- Place power, water, fire, police, clinics, schools and parks from the area menu (**Service buildings**).
