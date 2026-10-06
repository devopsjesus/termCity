#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum GameSpeed {
    Slow,
    #[default]
    Medium,
    Fast,
}

#[derive(Clone, Debug, PartialEq)]
pub struct GameConfig {
    pub map_width: i32,
    pub map_height: i32,
    pub seed: i32,
    pub starting_money: i32,
    pub road_cost_per_cell: i32,
    pub min_residential_cells: i32,
    pub residential_per_commercial: i32,
    pub residential_per_industrial: i32,
    pub default_tax_rate: f64,
    pub road_service_reach: i32,
    pub max_new_residential_per_week: i32,
    pub max_new_commercial_per_week: i32,
    pub max_new_industrial_per_week: i32,
    pub growth_rate_per_week: f64,
    pub weeks_per_year: i32,
    pub days_per_week: i32,
    pub slow_seconds_per_week: f64,
    pub medium_seconds_per_week: f64,
    pub fast_seconds_per_week: f64,
}

impl Default for GameConfig {
    fn default() -> Self {
        Self {
            map_width: 160,
            map_height: 96,
            seed: 1,
            starting_money: 50_000,
            road_cost_per_cell: 500,
            min_residential_cells: 10,
            residential_per_commercial: 20,
            residential_per_industrial: 10,
            default_tax_rate: 0.05,
            road_service_reach: 2,
            max_new_residential_per_week: 3,
            max_new_commercial_per_week: 1,
            max_new_industrial_per_week: 1,
            growth_rate_per_week: 0.02,
            weeks_per_year: 52,
            days_per_week: 7,
            slow_seconds_per_week: 8.4,
            medium_seconds_per_week: 4.2,
            fast_seconds_per_week: 1.4,
        }
    }
}

impl GameConfig {
    #[must_use]
    pub const fn seconds_per_week(&self, speed: GameSpeed) -> f64 {
        match speed {
            GameSpeed::Slow => self.slow_seconds_per_week,
            GameSpeed::Medium => self.medium_seconds_per_week,
            GameSpeed::Fast => self.fast_seconds_per_week,
        }
    }
}
