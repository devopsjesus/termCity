use crate::{CityGame, CityStats, GameMap, GrowthDiagnostic, Pos, Rgb, RoadNetwork, ZoneType};

const BUILDING_BACKGROUND: Rgb = Rgb::hex(0x2b_2b_30);
const DISCONNECTED_ROAD: Rgb = Rgb::hex(0xe8_a3_3d);

/// The UI-independent appearance of one map cell.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CellVisual {
    pub glyph: &'static str,
    pub foreground: Rgb,
    pub background: Rgb,
}

/// Resolves a cell's terrain, feature, zone, building, and road layers.
pub struct CellRenderer;

impl CellRenderer {
    /// # Panics
    ///
    /// Panics if the coordinate is outside the map.
    #[must_use]
    pub fn render(game: &CityGame, x: i32, y: i32) -> CellVisual {
        MapRenderer::new(game).render(x, y)
    }

    /// Returns the cardinal road mask: north = 1, east = 2, south = 4, west = 8.
    #[must_use]
    pub fn road_mask(map: &GameMap, x: i32, y: i32) -> u8 {
        map.road_mask(x, y)
    }
}

/// Reuses map-derived state while rendering multiple cells from one game snapshot.
pub struct MapRenderer<'a> {
    game: &'a CityGame,
    network: RoadNetwork,
    terrain_priority: [i32; 256],
}

impl<'a> MapRenderer<'a> {
    #[must_use]
    pub fn new(game: &'a CityGame) -> Self {
        Self { game, network: game.network(), terrain_priority: terrain_priorities(game) }
    }

    #[must_use]
    pub fn stats(&self) -> CityStats {
        self.game.stats_with_network(&self.network)
    }

    /// # Panics
    ///
    /// Panics if the coordinate is outside the map.
    #[must_use]
    pub fn render(&self, x: i32, y: i32) -> CellVisual {
        let map = &self.game.map;
        let terrain = map.terrain_at(x, y);
        let mut glyph = terrain.glyph_at(x, y);
        let mut foreground = terrain.foreground;
        let mut background = terrain.background;

        let zone = map.zone_at(x, y);
        let building = map.building_at(x, y);
        if let Some(info) = zone.info() {
            background = info.background;
            foreground = if self.network.is_served(map, x, y) {
                info.foreground
            } else {
                info.foreground.scale(0.5)
            };
            glyph = info.empty_glyph;
        } else if let Some(feature) = map.feature_at(x, y) {
            glyph = feature.glyph_at(x, y);
            foreground = feature.foreground;
        }

        if let Some(building) = building {
            glyph = building.glyph_at(x, y);
            foreground = building.foreground;
            if zone == ZoneType::None {
                background = BUILDING_BACKGROUND;
            }
        }

        if let Some(road) = map.road_type_at(x, y) {
            glyph = road.glyph_for(CellRenderer::road_mask(map, x, y));
            foreground = if self.network.is_connected(map, x, y) {
                road.foreground
            } else {
                DISCONNECTED_ROAD
            };
            background = if terrain.buildable { road.background } else { terrain.background };
        }

        CellVisual { glyph, foreground, background }
    }

    /// # Panics
    ///
    /// Panics if the block has no in-bounds cells.
    #[must_use]
    pub fn sample(&self, x0: i32, y0: i32, size: i32) -> CellVisual {
        self.sample_rect(x0, y0, size, size)
    }

    /// # Panics
    ///
    /// Panics if the rectangle has no in-bounds cells.
    #[must_use]
    pub fn sample_rect(&self, x0: i32, y0: i32, width: i32, height: i32) -> CellVisual {
        let (x, y) = sample_position(self.game, &self.terrain_priority, x0, y0, width, height);
        self.render(x, y)
    }
}

/// Produces human-readable, UI-independent descriptions of map cells.
pub struct CellInspector;

impl CellInspector {
    #[must_use]
    pub fn summary(game: &CityGame, position: Pos) -> String {
        Self::describe(game, position).join(" · ")
    }

    #[must_use]
    /// # Panics
    ///
    /// Panics if a pending zone removal contains [`ZoneType::None`].
    pub fn describe(game: &CityGame, position: Pos) -> Vec<String> {
        let map = &game.map;
        if !map.contains(position) {
            return Vec::new();
        }

        let x = position.x;
        let y = position.y;
        let terrain = map.terrain_at(x, y);
        let mut lines = vec![format!("({x},{y}) {}", terrain.name)];

        if let Some(feature) = map.feature_at(x, y) {
            lines.push(feature.name.clone());
        }

        if let Some(road) = map.road_type_at(x, y) {
            let bridge = if terrain.buildable { "" } else { " bridge" };
            let connection =
                if game.network().is_connected(map, x, y) { "connected" } else { "NOT connected" };
            lines.push(format!("{}{bridge} ({connection})", road.name));
        }

        let zone = map.zone_at(x, y);
        if let Some(info) = zone.info() {
            let access = if game.network().is_served(map, x, y) { "" } else { " (no road access)" };
            lines.push(format!("{} zone{access}", info.name));
            if let Some(building) = map.building_at(x, y) {
                lines.push(building.name.clone());
                Self::add_household(&mut lines, map, x, y);
            } else {
                let diagnostic = GrowthDiagnostic::for_cell(game, x, y);
                lines.push(format!("Vacant: {}", diagnostic.message));
                if diagnostic.paused {
                    lines.push("Paused - press P to resume".to_owned());
                }
            }
        } else if let Some(building) = map.building_at(x, y) {
            lines.push(building.name.clone());
            if let Some(removal) = map.zone_removal_at(x, y) {
                let days = (removal.remove_at_day - game.elapsed_days()).max(0.0);
                let zone_name = removal.zone.info().expect("removal stores a placeable zone").name;
                lines.push(format!(
                    "Unzoned: removal in {days:.1} game days; restore {zone_name} to keep it"
                ));
                Self::add_household(&mut lines, map, x, y);
            }
        }

        lines
    }

    fn add_household(lines: &mut Vec<String>, map: &GameMap, x: i32, y: i32) {
        let household = map.household_at(x, y);
        if !household.is_empty() {
            lines.push(format!(
                "{} residents: {}A {}C {}S",
                household.total(),
                household.adults,
                household.children,
                household.seniors
            ));
        }
    }
}

/// Selects the highest-priority cell to represent a zoomed-out block.
pub struct BlockSampler<'a> {
    game: &'a CityGame,
    terrain_priority: [i32; 256],
}

impl<'a> BlockSampler<'a> {
    #[must_use]
    pub fn new(game: &'a CityGame) -> Self {
        Self { game, terrain_priority: terrain_priorities(game) }
    }

    /// # Panics
    ///
    /// Panics if the block has no in-bounds cells.
    #[must_use]
    pub fn sample(&self, x0: i32, y0: i32, size: i32) -> CellVisual {
        self.sample_rect(x0, y0, size, size)
    }

    /// # Panics
    ///
    /// Panics if the rectangle has no in-bounds cells.
    #[must_use]
    pub fn sample_rect(&self, x0: i32, y0: i32, width: i32, height: i32) -> CellVisual {
        let renderer = MapRenderer::new(self.game);
        let (x, y) = sample_position(self.game, &self.terrain_priority, x0, y0, width, height);
        renderer.render(x, y)
    }
}

fn terrain_priorities(game: &CityGame) -> [i32; 256] {
    let mut priorities = [0; 256];
    for terrain in game.map.content().terrains.iter() {
        priorities[usize::from(crate::RegisteredType::id(terrain))] = if !terrain.buildable {
            30
        } else if terrain.build_cost_modifier > 1.0 {
            20
        } else {
            0
        };
    }
    priorities
}

fn sample_position(
    game: &CityGame,
    terrain_priority: &[i32; 256],
    x0: i32,
    y0: i32,
    width: i32,
    height: i32,
) -> (i32, i32) {
    let map = &game.map;
    let right = map.width().min(x0.saturating_add(width));
    let bottom = map.height().min(y0.saturating_add(height));
    let mut best = None;

    for y in y0.max(0)..bottom {
        for x in x0.max(0)..right {
            let index = map.index(x, y);
            let mut priority = terrain_priority[usize::from(map.terrain_layer()[index])];
            if map.feature_layer()[index] != 0 {
                priority = priority.max(10);
            }
            if map.road_layer()[index] {
                priority = priority.max(40 + i32::from(map.road_type_layer()[index]));
            }
            if map.zone_layer()[index] != ZoneType::None {
                priority = priority.max(50);
            }
            if map.building_layer()[index] != 0 {
                priority = priority.max(60);
            }
            if best.is_none_or(|(_, _, best_priority)| priority > best_priority) {
                best = Some((x, y, priority));
            }
        }
    }

    let (x, y, _) = best.expect("sample block contains an in-bounds cell");
    (x, y)
}
