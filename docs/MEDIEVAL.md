# TermCity in the Middle Ages

TermCity is set in a vaguely 11th to 14th century Latin Christendom. You are the steward of a settlement that starts as a
hamlet beside a road and may grow into a chartered city. This note describes the setting, how modern ideas were mapped
onto it, how medieval populations differ in the simulation, and where the game could go next.

## Vocabulary

| Idea | In the game |
|---|---|
| Currency | **Gold** (`15,000g`); every price, upkeep and income figure |
| Taxes | **Tithes and rents** on homesteads, marketplace and craftworks; **tolls** from a market; **tribute** to the crown |
| Zones | Homesteads (R), Marketplace (C), Craftworks (I) |
| Roads | Dirt Track, Cobbled Road, King's Road |
| Homes | Cottage, Burgage House, Tenement |
| Trade | Market Stall, Merchant House, Market Hall |
| Industry | Workshop, Mill, Great Forge |
| Mayor / council | The steward; the **budget menu** sets funding for the watch, sheriff, physic and so on |

Content registries and saves use only the current medieval names; historical modern-name aliases are not supported.

## Services

| Modern idea | Medieval equivalent | Notes |
|---|---|---|
| Power plant | Charcoal Burners, Woodlot | Fuel for hearths and forges; burners smoke, woodlots are clean but small |
| Water works | Aqueduct, Town Well | Aqueducts need a shore |
| Fire station | Fire Watch | Bucket brigade; fires are rarer and burn less |
| Police | Watch House, Sheriff's Hall, Gaol | Deter crime; a sheriff also turns raiders away |
| Hospital, clinic | Apothecary, Hospice, Infirmary | Physic cuts mortality and blunts plague |
| School, university | Chantry School, Monastery | Letters raise tithes; a monastery also hosts pilgrims |
| Park | Village Green, Tavern | Contentment, land value, less crime and smoke |
| (new) Defence | Motte and Bailey, Stone Keep, Castle | The lord's seat: see below |
| (new) Faith | Chapel, Parish Church, Cathedral | Solace, a gentler temper, pilgrims at feasts |
| (new) Trade | Market Cross, Guildhall | Tolls, cheaper and wider grain imports, richer feasts |
| (new) Granary | Granary | A reserve against a bad harvest or a long winter |

Power, water and granaries are city-wide supplies paid in full; the rest are area services funded from the budget.

## Castles: the lord's seat

A castle is the core of a medieval settlement: the lord garrisons the land, protects traders and draws settlers.
Three tiers raise the **seat rank**: Motte and Bailey (1), Stone Keep (2, from 400 people), Castle (3, from 2,500).
The seat's service strength garrisons the surrounding land (defence cover), lifts land value, and a building cannot rise a
level (cottage to burgage house to tenement) unless the seat rank is at least that level. It also cuts the crown's tribute by 12% per
tier. Without defence a town big enough to need it (400 people) is *Unguarded*, which costs happiness and invites sack.

## How medieval populations differ

The simulation changes the demographic and economic levers rather than adding a second engine:

- **Short lives**: children die at 5% a year, adults at 1.8%, elders at 16%. Births are 0.14% of adults a week.
- **Children and elders work**: 12% of children and 20% of elders (herding, gleaning, spinning, apprentices).
- **The farming year** and **famine**: the year starts in midwinter; the harvest is rolled each autumn and stored as weeks
  of grain. A bad harvest or a siege drains the store; without grain or gold there is hunger, deaths and emigration.
- **Plague**: outbreaks last weeks, strike hardest in summer and the dense, and are blunted by physic.
- **Raiders**: bandits and soldiers strike towns of 150 or more; a garrison or sheriff turns them away, hunger and
  tribute arrears make them bolder. Scenarios set their own raid risk (San Francisco, San Diego and St. Louis higher, Chicago lower).
- **Fire**: wooden towns burn; summer is the worst season and tall buildings fuel the spread.
- **Faith and feast days**: Lady Day, Midsummer, Michaelmas and Christmas bring pilgrims who spend gold at shrines and
  markets (none come in a famine).
- **The lord's pull**: a seat draws settlers, so a town with a castle attracts more families than one without.

Details and constants are in [POPULATION.md](POPULATION.md).

## Economy and standing

- **Tithes and rents** are the main income; a market adds tolls, the guilds lift trade and craft, schools lift output.
- **Upkeep** is paid for every civic building and scaled by its funding level in the budget.
- **Town rank** (Hamlet, Village, Market Town, Borough, City) comes from size, a market, a seat and a church. Each rank
  adds 3% to the dues on trade and craftwork.
- **Tribute**: at Michaelmas (week 39) the crown's reeve takes about 2g per soul, relieved by the seat and the
  rank. Anything unpaid is owed again, with the burden, next year.
- **Scenarios**: San Francisco, Los Angeles, San Diego, Chicago and St. Louis keep their names in the medieval setting, each
  with its own appeal, water, fire, flood, raid and harvest character, and are pre-built with fuel, water and civic cover.

## Systems added with the setting

- **Seasons and harvest** (`Seasons`, `Harvest`): the calendar, grain store, imports, famine.
- **Feasts** (`Feasts`): holy days that bring pilgrims and gold.
- **Settlement** (`Settlement`): rank, charters and the crown's tribute with arrears.
- Medieval **disasters** (`Disasters`): plague, raids and fires with seasonal and service effects.

Each is deterministic, required in current-format saves, and covered in `tests/TermCity.Tests`.

## Ideas not implemented

- **Walls and gates**: wall circuits that raise defence and fire protection, cost upkeep, cap growth inside the line, and
  that you can breach, repair or extend as the town spreads; sieges as an event.
- **The lord's favour**: a ruler with moods who grants charters, demands levies, or taxes more in war.
- **Guilds as buildings and a politics system**: guildhalls with their own power, strikes, monopolies and the burgesses' vote.
- **Estates and classes**: serfs, freemen, burghers, clergy and nobles with different taxes, needs and loyalties; the
  steward balancing them; migration from the manor to the town ("town air makes free").
- **Trade routes and a map beyond the town**: caravans, rivers and ports, goods (wool, salt, wine, grain), prices and
  merchant caravans that arrive or raid; the Hanse as an alliance.
- **Wars and levies**: the crown calls up men, raising mortality and lowering workers; mercenaries as a cost; soldiers
  billeted in town.
- **The Church in depth**: tithes, indulgences, heresy, monasteries owning land, pilgrimage routes and relic economy.
- **Disease in detail**: the Black Death as a rare, enormous event; lepers and leper houses; quarantine; bathhouses.
- **Rich agriculture**: field systems, fallow, crop choices, mills and bakers, a commons, livestock, enclosure.
- **Law and order**: courts, stocks, outlaws in forests, tax collectors who steal.
- **Technology and eras**: stone building, windmills, the plough, printing; unlocks over the centuries and a late-medieval
  turn to guns.
- **Named citizens, events and quests**: a reeve, a master mason, a heretic; chains of events with choices.
- **Weather**: cold winters that freeze rivers, droughts and a Little Ice Age slow drift.
- **Cathedrals as long projects**: built over decades by many hands, with the town's wealth ebbing and flowing.
