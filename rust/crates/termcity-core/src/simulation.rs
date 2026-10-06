#![allow(clippy::cast_possible_truncation, clippy::cast_precision_loss, clippy::cast_sign_loss)]

use crate::{
    BuildingType, CellRect, GameConfig, GameContent, GameMap, GameRandom, GameSpeed, Household,
    MapError, MapGenerator, PLACEABLE_ZONES, RegisteredType, RoadNetwork, RoadType, ZoneRemoval,
    ZoneType,
};

const MAX_SECONDS_PER_UPDATE: f64 = 0.5;
pub const MILESTONES: [i32; 5] = [100, 500, 1_000, 5_000, 10_000];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ActionResult {
    pub success: bool,
    pub message: String,
    pub cost: i32,
    pub cells: i32,
}

impl ActionResult {
    fn ok(message: String, cost: i32, cells: i32) -> Self {
        Self { success: true, message, cost, cells }
    }

    fn fail(message: String) -> Self {
        Self { success: false, message, cost: 0, cells: 0 }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct Quote {
    pub cells: i32,
    pub cost: i32,
    pub skipped: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WeekReport {
    pub week: i32,
    pub income: i32,
    pub new_households: i32,
    pub new_commercial: i32,
    pub new_industrial: i32,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct GrowthState {
    pub planned: bool,
    pub homes: i32,
    pub shops: i32,
    pub factories: i32,
    pub week_homes: i32,
    pub week_shops: i32,
    pub week_factories: i32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TaxRates {
    pub residential: f64,
    pub commercial: f64,
    pub industrial: f64,
}

impl TaxRates {
    #[must_use]
    pub const fn new(rate: f64) -> Self {
        Self { residential: rate, commercial: rate, industrial: rate }
    }

    #[must_use]
    pub const fn get(self, zone: ZoneType) -> f64 {
        match zone {
            ZoneType::Residential => self.residential,
            ZoneType::Commercial => self.commercial,
            ZoneType::Industrial => self.industrial,
            ZoneType::None => 0.0,
        }
    }

    pub fn set(&mut self, zone: ZoneType, rate: f64) {
        match zone {
            ZoneType::Residential => self.residential = rate,
            ZoneType::Commercial => self.commercial = rate,
            ZoneType::Industrial => self.industrial = rate,
            ZoneType::None => {}
        }
    }
}

impl Default for TaxRates {
    fn default() -> Self {
        Self::new(0.05)
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ZoneCount {
    pub zoned: i32,
    pub filled: i32,
    pub served: i32,
    pub awaiting_removal: i32,
}

impl ZoneCount {
    #[must_use]
    pub const fn occupied(self) -> i32 {
        self.filled + self.awaiting_removal
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct CityStats {
    pub population: i32,
    pub adults: i32,
    pub children: i32,
    pub seniors: i32,
    pub households: i32,
    pub residential: ZoneCount,
    pub commercial: ZoneCount,
    pub industrial: ZoneCount,
    pub road_cells: i32,
    pub connected_road_cells: i32,
    pub weekly_income: i32,
}

impl CityStats {
    #[must_use]
    pub const fn for_zone(self, zone: ZoneType) -> ZoneCount {
        match zone {
            ZoneType::Residential => self.residential,
            ZoneType::Commercial => self.commercial,
            ZoneType::Industrial => self.industrial,
            ZoneType::None => ZoneCount { zoned: 0, filled: 0, served: 0, awaiting_removal: 0 },
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Demand {
    pub residential: f64,
    pub commercial: f64,
    pub industrial: f64,
}

impl Demand {
    #[must_use]
    pub const fn for_zone(self, zone: ZoneType) -> f64 {
        match zone {
            ZoneType::Residential => self.residential,
            ZoneType::Commercial => self.commercial,
            ZoneType::Industrial => self.industrial,
            ZoneType::None => 0.0,
        }
    }

    #[must_use]
    pub fn compute(stats: CityStats, config: &GameConfig) -> Self {
        let zoned_residential = stats.residential.zoned;
        let filled_residential = stats.residential.occupied();
        let needed_for_commercial = if stats.commercial.zoned == 0 {
            0
        } else {
            config.residential_per_commercial * (stats.commercial.zoned - 1) + 1
        };
        let needed_for_industrial = if stats.industrial.zoned == 0 {
            0
        } else {
            config.residential_per_industrial * (stats.industrial.zoned - 1) + 1
        };
        let needed_residential =
            config.min_residential_cells.max(needed_for_commercial.max(needed_for_industrial));
        let mut residential = (f64::from(needed_residential - zoned_residential)
            / f64::from(needed_residential))
        .clamp(0.0, 1.0);
        if zoned_residential > 0 && stats.residential.filled >= zoned_residential {
            residential = residential.max(0.6);
        }

        let (commercial, industrial) = if filled_residential >= config.min_residential_cells {
            (
                shortfall(
                    filled_residential,
                    config.residential_per_commercial,
                    stats.commercial.zoned,
                ),
                shortfall(
                    filled_residential,
                    config.residential_per_industrial,
                    stats.industrial.zoned,
                ),
            )
        } else {
            (0.0, 0.0)
        };
        Self { residential, commercial, industrial }
    }
}

fn shortfall(filled_residential: i32, ratio: i32, zoned: i32) -> f64 {
    let allowed = ceil_div(filled_residential, ratio);
    (f64::from(allowed - zoned) / f64::from(allowed)).clamp(0.0, 1.0)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum GrowthStatus {
    Ready,
    NoVacancies,
    NoRoadAccess,
    NeedsHomes,
    CapacityReached,
    MissingBuilding,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GrowthDiagnostic {
    pub status: GrowthStatus,
    pub message: String,
    pub eligible_vacancies: i32,
    pub paused: bool,
}

impl GrowthDiagnostic {
    #[must_use]
    /// # Panics
    ///
    /// Panics if the map contains more than [`i32::MAX`] eligible cells.
    pub fn for_zone(game: &CityGame, zone: ZoneType) -> Self {
        let network = game.network();
        let eligible = i32::try_from(
            game.map
                .zone_cells(zone)
                .filter(|&index| {
                    game.map.building_layer()[index] == 0 && network.is_index_served(index)
                })
                .count(),
        )
        .expect("eligible vacancy count fits in i32");
        let count = game.stats().for_zone(zone);
        if count.filled >= count.zoned {
            return Self::result(GrowthStatus::NoVacancies, "Zone more land.", eligible, game);
        }
        if eligible == 0 {
            return Self::no_road(game, eligible);
        }
        Self::capacity(game, zone, eligible)
    }

    #[must_use]
    pub fn for_cell(game: &CityGame, x: i32, y: i32) -> Self {
        if !game.map.in_bounds(x, y) {
            return Self::result(GrowthStatus::NoVacancies, "No vacant zone here.", 0, game);
        }
        let zone = game.map.zone_at(x, y);
        if zone == ZoneType::None || game.map.building_at(x, y).is_some() {
            return Self::result(GrowthStatus::NoVacancies, "No vacant zone here.", 0, game);
        }
        if game.network().is_served(&game.map, x, y) {
            Self::capacity(game, zone, 1)
        } else {
            Self::no_road(game, 0)
        }
    }

    fn no_road(game: &CityGame, eligible: i32) -> Self {
        Self::result(
            GrowthStatus::NoRoadAccess,
            &format!(
                "Connect a road to the map edge within {} cells.",
                game.config.road_service_reach
            ),
            eligible,
            game,
        )
    }

    fn capacity(game: &CityGame, zone: ZoneType, eligible: i32) -> Self {
        if game.map.content().building_for_zone(zone).is_none() {
            return Self::result(
                GrowthStatus::MissingBuilding,
                "No growth building registered for this zone.",
                eligible,
                game,
            );
        }
        let homes = game.stats().residential.occupied();
        if zone != ZoneType::Residential && homes < game.config.min_residential_cells {
            return Self::result(
                GrowthStatus::NeedsHomes,
                &format!(
                    "Needs {} occupied homes; you have {homes}.",
                    game.config.min_residential_cells
                ),
                eligible,
                game,
            );
        }
        let supported = game.supported_cells(zone);
        let filled = game.stats().for_zone(zone).occupied();
        if zone != ZoneType::Residential && filled >= supported {
            let ratio = if zone == ZoneType::Commercial {
                game.config.residential_per_commercial
            } else {
                game.config.residential_per_industrial
            };
            let next = game.config.min_residential_cells.max(filled * ratio + 1);
            return Self::result(
                GrowthStatus::CapacityReached,
                &format!("{filled}/{supported} supported. Next slot at {next} occupied homes."),
                eligible,
                game,
            );
        }
        Self::result(
            GrowthStatus::Ready,
            "Ready for growth; arrivals are spread across the week.",
            eligible,
            game,
        )
    }

    fn result(
        status: GrowthStatus,
        message: &str,
        eligible_vacancies: i32,
        game: &CityGame,
    ) -> Self {
        Self { status, message: message.to_owned(), eligible_vacancies, paused: game.paused }
    }
}

#[derive(Clone, Debug)]
pub struct CityGame {
    pub config: GameConfig,
    pub map: GameMap,
    pub rng: GameRandom,
    pub money: i32,
    pub week: i32,
    pub day: i32,
    pub speed: GameSpeed,
    pub paused: bool,
    pub taxes: TaxRates,
    pub last_report: Option<WeekReport>,
    pub highest_milestone: i32,
    pub guide_dismissed: bool,
    day_progress: f64,
    growth: GrowthState,
    map_version: u64,
}

impl CityGame {
    /// Generates a new city using the default content.
    ///
    /// # Errors
    ///
    /// Returns an error when the configured map dimensions are invalid.
    pub fn new(config: GameConfig) -> Result<Self, MapError> {
        Self::new_with_content(config, GameContent::default())
    }

    /// Generates a new city with a caller-supplied content registry.
    ///
    /// # Errors
    ///
    /// Returns an error when the configured map dimensions are invalid.
    pub fn new_with_content(config: GameConfig, content: GameContent) -> Result<Self, MapError> {
        let map =
            MapGenerator::generate(config.map_width, config.map_height, config.seed, content)?;
        Ok(Self::from_map(config, map))
    }

    #[must_use]
    pub fn from_map(config: GameConfig, map: GameMap) -> Self {
        let default_tax_rate = config.default_tax_rate;
        let seed = config.seed;
        let money = config.starting_money;
        Self {
            config,
            map,
            rng: GameRandom::for_stage(seed, "simulation"),
            money,
            week: 0,
            day: 0,
            speed: GameSpeed::Medium,
            paused: false,
            taxes: TaxRates::new(default_tax_rate),
            last_report: None,
            highest_milestone: 0,
            guide_dismissed: false,
            day_progress: 0.0,
            growth: GrowthState::default(),
            map_version: 0,
        }
    }

    #[must_use]
    pub const fn map_version(&self) -> u64 {
        self.map_version
    }

    #[must_use]
    pub const fn growth_state(&self) -> GrowthState {
        self.growth
    }

    pub const fn restore_growth_state(&mut self, state: GrowthState) {
        self.growth = state;
    }

    #[must_use]
    pub const fn day_progress_seconds(&self) -> f64 {
        self.day_progress
    }

    pub const fn set_day_progress_seconds(&mut self, seconds: f64) {
        self.day_progress = seconds;
    }

    #[must_use]
    pub fn elapsed_days(&self) -> f64 {
        f64::from(self.week * self.config.days_per_week + self.day)
            + (self.day_progress / self.seconds_per_day()).clamp(0.0, 1.0)
    }

    #[must_use]
    pub fn week_progress(&self) -> f64 {
        (f64::from(self.day) + (self.day_progress / self.seconds_per_day()).clamp(0.0, 1.0))
            / f64::from(self.config.days_per_week)
    }

    #[must_use]
    pub const fn year(&self) -> i32 {
        self.week / self.config.weeks_per_year + 1
    }

    #[must_use]
    pub const fn week_of_year(&self) -> i32 {
        self.week % self.config.weeks_per_year + 1
    }

    #[must_use]
    pub fn network(&self) -> RoadNetwork {
        RoadNetwork::compute(&self.map, self.config.road_service_reach)
    }

    #[must_use]
    pub fn stats(&self) -> CityStats {
        let network = self.network();
        self.stats_with_network(&network)
    }

    #[must_use]
    pub(crate) fn stats_with_network(&self, network: &RoadNetwork) -> CityStats {
        self.compute_stats(network)
    }

    #[must_use]
    pub fn demand(&self) -> Demand {
        Demand::compute(self.stats(), &self.config)
    }

    #[must_use]
    pub fn supported_cells(&self, zone: ZoneType) -> i32 {
        let homes = self.stats().residential.occupied();
        if zone == ZoneType::Residential {
            return i32::MAX;
        }
        if homes < self.config.min_residential_cells {
            return 0;
        }
        match zone {
            ZoneType::Commercial => ceil_div(homes, self.config.residential_per_commercial),
            ZoneType::Industrial => ceil_div(homes, self.config.residential_per_industrial),
            ZoneType::None | ZoneType::Residential => 0,
        }
    }

    #[must_use]
    pub fn next_milestone(&self) -> Option<i32> {
        MILESTONES.iter().copied().find(|&milestone| milestone > self.highest_milestone)
    }

    pub fn set_tax_rate(&mut self, zone: ZoneType, rate: f64) {
        self.taxes.set(zone, rate);
        self.notify(false, false);
    }

    /// Invalidates derived state after callers mutate the map directly.
    pub fn touch(&mut self) {
        self.notify(true, true);
    }

    fn notify(&mut self, _roads_changed: bool, map_changed: bool) {
        if map_changed {
            self.map_version = self.map_version.wrapping_add(1);
        }
        let population = self.stats().population;
        if let Some(reached) =
            MILESTONES.iter().copied().rev().find(|&milestone| milestone <= population)
        {
            self.highest_milestone = self.highest_milestone.max(reached);
        }
    }

    fn seconds_per_day(&self) -> f64 {
        self.config.seconds_per_week(self.speed) / f64::from(self.config.days_per_week)
    }

    pub fn update(&mut self, elapsed_seconds: f64) {
        if self.paused || elapsed_seconds <= 0.0 {
            return;
        }
        self.day_progress += elapsed_seconds.min(MAX_SECONDS_PER_UPDATE);
        let day_length = self.seconds_per_day();
        while self.day_progress >= day_length {
            self.day_progress -= day_length;
            self.advance_day();
        }
        if self.remove_dezoned_buildings(self.elapsed_days()) > 0 {
            self.notify(false, true);
        }
    }

    /// # Panics
    ///
    /// Panics if `days_per_week` is not positive.
    pub fn advance_week(&mut self) -> WeekReport {
        loop {
            self.advance_day();
            if self.day == 0 {
                return self.last_report.expect("finishing a week creates a report");
            }
        }
    }

    pub fn advance_day(&mut self) {
        if !self.growth.planned {
            self.plan_week();
        }
        let day = self.day;
        let homes = self.grow(ZoneType::Residential, self.share(self.growth.homes, day), i32::MAX);
        let commercial_room =
            self.supported_cells(ZoneType::Commercial) - self.stats().commercial.occupied();
        let shops =
            self.grow(ZoneType::Commercial, self.share(self.growth.shops, day), commercial_room);
        let industrial_room =
            self.supported_cells(ZoneType::Industrial) - self.stats().industrial.occupied();
        let factories = self.grow(
            ZoneType::Industrial,
            self.share(self.growth.factories, day),
            industrial_room,
        );
        self.growth.week_homes += homes;
        self.growth.week_shops += shops;
        self.growth.week_factories += factories;
        self.day += 1;
        let removed = self
            .remove_dezoned_buildings(f64::from(self.week * self.config.days_per_week + self.day));
        if self.day < self.config.days_per_week {
            if homes + shops + factories + removed > 0 {
                self.notify(false, true);
            }
            return;
        }

        let income = self.stats().weekly_income;
        self.money += income;
        self.week += 1;
        self.day = 0;
        self.growth.planned = false;
        let report = WeekReport {
            week: self.week,
            income,
            new_households: self.growth.week_homes,
            new_commercial: self.growth.week_shops,
            new_industrial: self.growth.week_factories,
        };
        self.last_report = Some(report);
        self.growth.week_homes = 0;
        self.growth.week_shops = 0;
        self.growth.week_factories = 0;
        self.notify(
            false,
            homes + shops + factories + removed > 0
                || report.new_households + report.new_commercial + report.new_industrial > 0,
        );
    }

    fn remove_dezoned_buildings(&mut self, elapsed_days: f64) -> i32 {
        let due: Vec<usize> = self
            .map
            .zone_removals()
            .filter_map(|(index, removal)| (removal.remove_at_day <= elapsed_days).then_some(index))
            .collect();
        for index in &due {
            let position = self.map.position_of(*index);
            self.map.set_building(position.x, position.y, None);
            self.map.set_household(position.x, position.y, Household::default());
        }
        i32::try_from(due.len()).expect("removal count fits in i32")
    }

    fn plan_week(&mut self) {
        let filled_residential = self.stats().residential.occupied();
        let allowed_commercial = self.supported_cells(ZoneType::Commercial);
        let allowed_industrial = self.supported_cells(ZoneType::Industrial);
        self.growth.homes =
            self.weekly_cap(self.config.max_new_residential_per_week, filled_residential);
        self.growth.shops =
            self.weekly_cap(self.config.max_new_commercial_per_week, allowed_commercial);
        self.growth.factories =
            self.weekly_cap(self.config.max_new_industrial_per_week, allowed_industrial);
        self.growth.planned = true;
    }

    #[must_use]
    pub fn daily_share(&self, total: i32, day: i32) -> i32 {
        self.share(total, day)
    }

    fn share(&self, total: i32, day: i32) -> i32 {
        let days = i64::from(self.config.days_per_week);
        let total = i64::from(total);
        i32::try_from(total * i64::from(day + 1) / days - total * i64::from(day) / days)
            .expect("daily growth share fits in i32")
    }

    #[must_use]
    pub fn weekly_cap(&self, base_cap: i32, existing: i32) -> i32 {
        base_cap
            + (f64::from(existing.max(0)) * self.config.growth_rate_per_week.max(0.0)).ceil() as i32
    }

    fn grow(&mut self, zone: ZoneType, limit: i32, room: i32) -> i32 {
        let count = limit.min(room);
        let Some(building_id) = self.map.content().building_for_zone(zone).map(RegisteredType::id)
        else {
            return 0;
        };
        if count <= 0 {
            return 0;
        }
        let network = self.network();
        let mut candidates: Vec<usize> = self
            .map
            .zone_cells(zone)
            .filter(|&index| {
                self.map.building_layer()[index] == 0 && network.is_index_served(index)
            })
            .collect();
        candidates.sort_unstable();
        let mut filled = 0;
        while filled < count && !candidates.is_empty() {
            let candidate_count =
                i32::try_from(candidates.len()).expect("candidate count fits in i32");
            let pick = usize::try_from(self.rng.next(candidate_count))
                .expect("random candidate index is non-negative");
            let index = candidates.swap_remove(pick);
            let position = self.map.position_of(index);
            self.map.set_building(position.x, position.y, Some(building_id));
            if zone == ZoneType::Residential {
                self.map.set_household(position.x, position.y, Household::random(&mut self.rng));
            }
            filled += 1;
        }
        filled
    }

    #[must_use]
    pub fn default_road(&self) -> &RoadType {
        self.map.content().default_road()
    }

    #[must_use]
    pub fn road_cost_at(&self, x: i32, y: i32, road: Option<&RoadType>) -> i32 {
        let road = road.unwrap_or_else(|| self.default_road());
        let terrain = self.map.terrain_at(x, y).build_cost_modifier;
        let existing = self.map.road_type_at(x, y).map_or(0.0, |existing| existing.cost_multiplier);
        (f64::from(self.config.road_cost_per_cell)
            * (road.cost_multiplier - existing).max(0.0)
            * terrain)
            .round() as i32
    }

    #[must_use]
    pub fn building_cost_at(&self, building: &BuildingType, x: i32, y: i32) -> i32 {
        (f64::from(building.cost) * self.map.terrain_at(x, y).build_cost_modifier).round() as i32
    }

    #[must_use]
    pub fn can_build_on(&self, x: i32, y: i32) -> bool {
        self.map.in_bounds(x, y)
            && self.map.terrain_at(x, y).buildable
            && !self.map.has_road(x, y)
            && self.map.zone_at(x, y) == ZoneType::None
            && self.map.building_at(x, y).is_none()
    }

    #[must_use]
    pub fn can_place_road(&self, x: i32, y: i32, road: Option<&RoadType>) -> bool {
        let road = road.unwrap_or_else(|| self.default_road());
        if !self.map.in_bounds(x, y)
            || !self.map.terrain_at(x, y).buildable
            || self.map.zone_at(x, y) != ZoneType::None
            || self.map.building_at(x, y).is_some()
        {
            return false;
        }
        self.map.road_type_at(x, y).is_none_or(|existing| existing.rank < road.rank)
    }

    #[must_use]
    pub fn quote_road(&self, area: CellRect, road: Option<&RoadType>) -> Quote {
        self.quote_cells(
            area,
            |x, y| self.can_place_road(x, y, road),
            |x, y| self.road_cost_at(x, y, road),
        )
    }

    #[must_use]
    pub fn quote_building(&self, building: &BuildingType, area: CellRect) -> Quote {
        self.quote_cells(
            area,
            |x, y| self.can_build_on(x, y),
            |x, y| self.building_cost_at(building, x, y),
        )
    }

    fn quote_cells(
        &self,
        area: CellRect,
        valid: impl Fn(i32, i32) -> bool,
        cost: impl Fn(i32, i32) -> i32,
    ) -> Quote {
        let mut quote = Quote::default();
        for position in area.cells() {
            if !self.map.contains(position) {
                continue;
            }
            if valid(position.x, position.y) {
                quote.cells += 1;
                quote.cost += cost(position.x, position.y);
            } else {
                quote.skipped += 1;
            }
        }
        quote
    }

    pub fn build_road(&mut self, area: CellRect, road: Option<&RoadType>) -> ActionResult {
        let road = road.cloned().unwrap_or_else(|| self.default_road().clone());
        if !road.player_placeable {
            return ActionResult::fail(format!("{} cannot be built by the player.", road.name));
        }
        let quote = self.quote_road(area, Some(&road));
        if let Some(failure) = self.check_spend(quote, &road.name.to_lowercase()) {
            return failure;
        }
        for position in area.cells() {
            if self.can_place_road(position.x, position.y, Some(&road)) {
                self.map.set_feature(position.x, position.y, None);
                self.map.set_road(position.x, position.y, road.id());
            }
        }
        self.money -= quote.cost;
        self.notify(true, true);
        ActionResult::ok(
            format!(
                "Built {} {} cell(s) for {}.{}",
                quote.cells,
                road.name.to_lowercase(),
                format_money(quote.cost),
                skipped_note(quote)
            ),
            quote.cost,
            quote.cells,
        )
    }

    pub fn place_building(&mut self, building: &BuildingType, area: CellRect) -> ActionResult {
        if !building.player_placeable {
            return ActionResult::fail(format!(
                "{} cannot be placed by the player.",
                building.name
            ));
        }
        let quote = self.quote_building(building, area);
        if let Some(failure) = self.check_spend(quote, &building.name.to_lowercase()) {
            return failure;
        }
        for position in area.cells() {
            if self.can_build_on(position.x, position.y) {
                self.map.set_feature(position.x, position.y, None);
                self.map.set_building(position.x, position.y, Some(building.id()));
            }
        }
        self.money -= quote.cost;
        self.notify(false, true);
        ActionResult::ok(
            format!(
                "Built {} {}(s) for {}.{}",
                quote.cells,
                building.name,
                format_money(quote.cost),
                skipped_note(quote)
            ),
            quote.cost,
            quote.cells,
        )
    }

    fn check_spend(&self, quote: Quote, what: &str) -> Option<ActionResult> {
        if self.money <= 0 {
            return Some(ActionResult::fail(
                "You are out of money: no more roads or buildings can be placed.".to_owned(),
            ));
        }
        if quote.cells == 0 {
            return Some(ActionResult::fail(format!(
                "Nothing to build: no valid cells for a {what} in the selection (water, roads, zones and buildings are skipped)."
            )));
        }
        if quote.cost > self.money {
            return Some(ActionResult::fail(format!(
                "Not enough money: {} {what} cell(s) cost {} but you have {}.",
                quote.cells,
                format_money(quote.cost),
                format_money(self.money)
            )));
        }
        None
    }

    /// # Panics
    ///
    /// Panics if a non-`None` zone has no registered metadata.
    pub fn designate(&mut self, area: CellRect, zone: ZoneType) -> ActionResult {
        if zone == ZoneType::None {
            return self.dezone(area);
        }
        let info = zone.info().expect("a placeable zone has metadata");
        let mut changed = 0;
        let mut skipped = 0;
        for position in area.cells() {
            if !self.map.contains(position) || self.map.zone_at(position.x, position.y) == zone {
                continue;
            }
            let restoring = self
                .map
                .zone_removal_at(position.x, position.y)
                .is_some_and(|removal| removal.zone == zone);
            let allowed = self.map.terrain_at(position.x, position.y).buildable
                && !self.map.has_road(position.x, position.y)
                && (self.map.building_at(position.x, position.y).is_none() || restoring);
            if allowed {
                self.map.set_feature(position.x, position.y, None);
                self.map.set_zone(position.x, position.y, zone);
                changed += 1;
            } else {
                skipped += 1;
            }
        }
        if changed == 0 {
            return ActionResult::fail(format!(
                "No cells could be designated {}: water, roads and buildings are skipped.",
                info.name
            ));
        }
        self.notify(false, true);
        let note = if skipped > 0 {
            format!(" ({skipped} blocked cell(s) skipped.)")
        } else {
            String::new()
        };
        ActionResult::ok(format!("Designated {changed} {} cell(s).{note}", info.name), 0, changed)
    }

    pub fn dezone(&mut self, area: CellRect) -> ActionResult {
        let mut changed = 0;
        let mut scheduled = 0;
        for position in area.cells() {
            if !self.map.contains(position)
                || self.map.zone_at(position.x, position.y) == ZoneType::None
            {
                continue;
            }
            let zone = self.map.zone_at(position.x, position.y);
            self.map.set_zone(position.x, position.y, ZoneType::None);
            if self.map.building_at(position.x, position.y).is_some() {
                let delay = self
                    .rng
                    .next_range(2 * self.config.days_per_week, 3 * self.config.days_per_week + 1);
                self.map.set_zone_removal(
                    position.x,
                    position.y,
                    ZoneRemoval { zone, remove_at_day: self.elapsed_days() + f64::from(delay) },
                );
                scheduled += 1;
            }
            changed += 1;
        }
        if changed == 0 {
            return ActionResult::fail("No zones to remove here.".to_owned());
        }
        self.notify(false, true);
        let note = if scheduled > 0 {
            format!(
                " {scheduled} building(s) will be removed in 2-3 game weeks unless their original zone is restored."
            )
        } else {
            String::new()
        };
        ActionResult::ok(format!("Dezoned {changed} cell(s).{note}"), 0, changed)
    }

    pub fn demolish(&mut self, area: CellRect) -> ActionResult {
        let mut changed = 0;
        let mut roads_removed = false;
        for position in area.cells() {
            if !self.map.contains(position) {
                continue;
            }
            let had_road = self.map.has_road(position.x, position.y);
            let something = had_road
                || self.map.zone_at(position.x, position.y) != ZoneType::None
                || self.map.building_at(position.x, position.y).is_some()
                || self.map.feature_at(position.x, position.y).is_some();
            if something {
                self.map.clear_cell(position.x, position.y);
                roads_removed |= had_road;
                changed += 1;
            }
        }
        if changed == 0 {
            return ActionResult::fail("Nothing to demolish here.".to_owned());
        }
        self.notify(roads_removed, true);
        ActionResult::ok(format!("Demolished {changed} cell(s)."), 0, changed)
    }

    fn compute_stats(&self, network: &RoadNetwork) -> CityStats {
        let mut adults = 0;
        let mut children = 0;
        let mut seniors = 0;
        let mut households = 0;
        let mut zoned = [0; 4];
        let mut filled = [0; 4];
        let mut served = [0; 4];
        let mut awaiting_removal = [0; 4];
        for zone in PLACEABLE_ZONES {
            let zone_index = zone as usize;
            for index in self.map.zone_cells(zone) {
                zoned[zone_index] += 1;
                if network.is_index_served(index) {
                    served[zone_index] += 1;
                }
                if self.map.building_layer()[index] != 0 {
                    filled[zone_index] += 1;
                    if zone == ZoneType::Residential {
                        let household = self.map.household_layer()[index];
                        adults += i32::from(household.adults);
                        children += i32::from(household.children);
                        seniors += i32::from(household.seniors);
                        households += 1;
                    }
                }
            }
        }
        for (index, removal) in self.map.zone_removals() {
            awaiting_removal[removal.zone as usize] += 1;
            if removal.zone == ZoneType::Residential {
                let household = self.map.household_layer()[index];
                adults += i32::from(household.adults);
                children += i32::from(household.children);
                seniors += i32::from(household.seniors);
                households += 1;
            }
        }
        let mut income = 0.0;
        for zone in PLACEABLE_ZONES {
            let index = zone as usize;
            let occupied = filled[index] + awaiting_removal[index];
            income += f64::from(occupied * zone.info().expect("zone has metadata").weekly_value)
                * self.taxes.get(zone);
        }
        let count = |zone: ZoneType| ZoneCount {
            zoned: zoned[zone as usize],
            filled: filled[zone as usize],
            served: served[zone as usize],
            awaiting_removal: awaiting_removal[zone as usize],
        };
        CityStats {
            population: adults + children + seniors,
            adults,
            children,
            seniors,
            households,
            residential: count(ZoneType::Residential),
            commercial: count(ZoneType::Commercial),
            industrial: count(ZoneType::Industrial),
            road_cells: i32::try_from(self.map.road_count()).expect("road count fits in i32"),
            connected_road_cells: i32::try_from(network.connected_road_count())
                .expect("connected road count fits in i32"),
            weekly_income: income.round() as i32,
        }
    }
}

fn ceil_div(value: i32, divisor: i32) -> i32 {
    (value + divisor - 1) / divisor
}

fn skipped_note(quote: Quote) -> String {
    if quote.skipped > 0 {
        format!(" ({} blocked cell(s) skipped.)", quote.skipped)
    } else {
        String::new()
    }
}

#[must_use]
pub fn format_money(amount: i32) -> String {
    let magnitude = i64::from(amount).abs().to_string();
    let mut grouped = String::with_capacity(magnitude.len() + magnitude.len() / 3 + 2);
    for (index, character) in magnitude.chars().enumerate() {
        if index > 0 && (magnitude.len() - index) % 3 == 0 {
            grouped.push(',');
        }
        grouped.push(character);
    }
    if amount < 0 { format!("-${grouped}") } else { format!("${grouped}") }
}
