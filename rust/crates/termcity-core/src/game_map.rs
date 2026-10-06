use std::array;
use std::collections::{HashMap, HashSet};
use std::error::Error;
use std::fmt;

use crate::{
    BuildingType, FeatureType, GameContent, Household, Pos, RegisteredType, RoadType, TerrainType,
    ZoneType,
};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ZoneRemoval {
    pub zone: ZoneType,
    pub remove_at_day: f64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MapError {
    TooSmall,
    TooLarge,
}

impl fmt::Display for MapError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::TooSmall => formatter.write_str("Map must be at least 8x8."),
            Self::TooLarge => formatter.write_str("Map dimensions are too large."),
        }
    }
}

impl Error for MapError {}

#[derive(Clone, Debug)]
pub struct GameMap {
    width: i32,
    height: i32,
    content: GameContent,
    terrain: Vec<u8>,
    features: Vec<u8>,
    roads: Vec<bool>,
    road_types: Vec<u8>,
    zones: Vec<ZoneType>,
    buildings: Vec<u8>,
    households: Vec<Household>,
    zone_removals: HashMap<usize, ZoneRemoval>,
    road_cells: HashSet<usize>,
    zone_cells: [HashSet<usize>; 4],
}

impl GameMap {
    /// Creates an empty layered map filled with the content's base terrain.
    ///
    /// # Errors
    ///
    /// Returns an error when either dimension is below eight or their product cannot be represented.
    pub fn new(width: i32, height: i32, content: GameContent) -> Result<Self, MapError> {
        if width < 8 || height < 8 {
            return Err(MapError::TooSmall);
        }
        let count = usize::try_from(width)
            .ok()
            .and_then(|w| usize::try_from(height).ok().and_then(|h| w.checked_mul(h)))
            .ok_or(MapError::TooLarge)?;
        let base_terrain = content.base_terrain().id();

        Ok(Self {
            width,
            height,
            content,
            terrain: vec![base_terrain; count],
            features: vec![0; count],
            roads: vec![false; count],
            road_types: vec![0; count],
            zones: vec![ZoneType::None; count],
            buildings: vec![0; count],
            households: vec![Household::default(); count],
            zone_removals: HashMap::new(),
            road_cells: HashSet::new(),
            zone_cells: array::from_fn(|_| HashSet::new()),
        })
    }

    #[must_use]
    pub const fn width(&self) -> i32 {
        self.width
    }

    #[must_use]
    pub const fn height(&self) -> i32 {
        self.height
    }

    #[must_use]
    pub const fn content(&self) -> &GameContent {
        &self.content
    }

    #[must_use]
    pub const fn in_bounds(&self, x: i32, y: i32) -> bool {
        x >= 0 && y >= 0 && x < self.width && y < self.height
    }

    #[must_use]
    pub const fn contains(&self, position: Pos) -> bool {
        self.in_bounds(position.x, position.y)
    }

    #[must_use]
    pub const fn is_edge(&self, x: i32, y: i32) -> bool {
        x == 0 || y == 0 || x == self.width - 1 || y == self.height - 1
    }

    /// Returns the flat row-major index of an in-bounds cell.
    ///
    /// # Panics
    ///
    /// Panics if the coordinate is outside the map.
    #[must_use]
    pub fn index(&self, x: i32, y: i32) -> usize {
        assert!(self.in_bounds(x, y), "map coordinate ({x},{y}) is out of bounds");
        usize::try_from(y * self.width + x).expect("validated map index is non-negative")
    }

    /// Returns the coordinate corresponding to a flat map index.
    ///
    /// # Panics
    ///
    /// Panics if the index is outside the map layers.
    #[must_use]
    pub fn position_of(&self, index: usize) -> Pos {
        assert!(index < self.terrain.len(), "map index {index} is out of bounds");
        let index = i32::try_from(index).expect("map size is constrained to i32 dimensions");
        Pos::new(index % self.width, index / self.width)
    }

    #[must_use]
    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or the layer contains an unregistered id.
    pub fn terrain_at(&self, x: i32, y: i32) -> &TerrainType {
        let id = self.terrain[self.index(x, y)];
        self.content.terrains.get_by_id(id).expect("terrain layer contains a registered id")
    }

    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or `terrain_id` is not registered.
    pub fn set_terrain(&mut self, x: i32, y: i32, terrain_id: u8) {
        assert!(self.content.terrains.get_by_id(terrain_id).is_some(), "unknown terrain id");
        let index = self.index(x, y);
        self.terrain[index] = terrain_id;
    }

    #[must_use]
    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or the layer contains an unregistered id.
    pub fn feature_at(&self, x: i32, y: i32) -> Option<&FeatureType> {
        let id = self.features[self.index(x, y)];
        (id != 0).then(|| self.content.features.get_by_id(id)).flatten().or_else(|| {
            assert_eq!(id, 0, "feature layer contains an unregistered id");
            None
        })
    }

    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or a supplied feature id is not registered.
    pub fn set_feature(&mut self, x: i32, y: i32, feature_id: Option<u8>) {
        let id = feature_id.unwrap_or(0);
        assert!(id == 0 || self.content.features.get_by_id(id).is_some(), "unknown feature id");
        let index = self.index(x, y);
        self.features[index] = id;
    }

    #[must_use]
    pub fn has_road(&self, x: i32, y: i32) -> bool {
        self.in_bounds(x, y) && self.roads[self.index(x, y)]
    }

    pub fn set_has_road(&mut self, x: i32, y: i32, value: bool) {
        let index = self.index(x, y);
        if self.roads[index] == value {
            return;
        }

        self.roads[index] = value;
        if value {
            self.road_types[index] = self.content.default_road().id();
            self.road_cells.insert(index);
        } else {
            self.road_types[index] = 0;
            self.road_cells.remove(&index);
        }
    }

    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or `road_id` is not registered.
    pub fn set_road(&mut self, x: i32, y: i32, road_id: u8) {
        assert!(self.content.roads.get_by_id(road_id).is_some(), "unknown road id");
        self.set_has_road(x, y, true);
        let index = self.index(x, y);
        self.road_types[index] = road_id;
    }

    #[must_use]
    /// # Panics
    ///
    /// Panics if a present road contains an unregistered type id.
    pub fn road_type_at(&self, x: i32, y: i32) -> Option<&RoadType> {
        if !self.in_bounds(x, y) {
            return None;
        }
        let index = self.index(x, y);
        if !self.roads[index] {
            return None;
        }
        let stored_id = self.road_types[index];
        let id = if stored_id == 0 { self.content.default_road().id() } else { stored_id };
        Some(self.content.roads.get_by_id(id).expect("road layer contains a registered id"))
    }

    pub fn road_cells(&self) -> impl ExactSizeIterator<Item = usize> + '_ {
        self.road_cells.iter().copied()
    }

    #[must_use]
    pub fn road_count(&self) -> usize {
        self.road_cells.len()
    }

    /// Which cardinal neighbours are roads: north = 1, east = 2, south = 4, west = 8.
    #[must_use]
    pub fn road_mask(&self, x: i32, y: i32) -> u8 {
        u8::from(self.has_road(x, y - 1))
            | (u8::from(self.has_road(x + 1, y)) << 1)
            | (u8::from(self.has_road(x, y + 1)) << 2)
            | (u8::from(self.has_road(x - 1, y)) << 3)
    }

    pub fn zone_cells(&self, zone: ZoneType) -> impl ExactSizeIterator<Item = usize> + '_ {
        self.zone_cells[zone as usize].iter().copied()
    }

    #[must_use]
    pub fn zone_at(&self, x: i32, y: i32) -> ZoneType {
        self.zones[self.index(x, y)]
    }

    pub fn set_zone(&mut self, x: i32, y: i32, zone: ZoneType) {
        let index = self.index(x, y);
        let old = self.zones[index];
        if old == zone {
            return;
        }

        self.zones[index] = zone;
        self.zone_cells[old as usize].remove(&index);
        self.zone_cells[zone as usize].insert(index);
        if zone != ZoneType::None {
            self.zone_removals.remove(&index);
        }
    }

    #[must_use]
    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or the layer contains an unregistered id.
    pub fn building_at(&self, x: i32, y: i32) -> Option<&BuildingType> {
        let id = self.buildings[self.index(x, y)];
        (id != 0).then(|| self.content.buildings.get_by_id(id)).flatten().or_else(|| {
            assert_eq!(id, 0, "building layer contains an unregistered id");
            None
        })
    }

    /// # Panics
    ///
    /// Panics if the coordinate is out of bounds or a supplied building id is not registered.
    pub fn set_building(&mut self, x: i32, y: i32, building_id: Option<u8>) {
        let id = building_id.unwrap_or(0);
        assert!(id == 0 || self.content.buildings.get_by_id(id).is_some(), "unknown building id");
        let index = self.index(x, y);
        self.buildings[index] = id;
        self.zone_removals.remove(&index);
    }

    #[must_use]
    pub fn household_at(&self, x: i32, y: i32) -> Household {
        self.households[self.index(x, y)]
    }

    pub fn set_household(&mut self, x: i32, y: i32, household: Household) {
        let index = self.index(x, y);
        self.households[index] = household;
    }

    #[must_use]
    pub fn zone_removal_at(&self, x: i32, y: i32) -> Option<ZoneRemoval> {
        self.zone_removals.get(&self.index(x, y)).copied()
    }

    pub fn set_zone_removal(&mut self, x: i32, y: i32, removal: ZoneRemoval) {
        let index = self.index(x, y);
        self.zone_removals.insert(index, removal);
    }

    pub fn zone_removals(&self) -> impl ExactSizeIterator<Item = (usize, ZoneRemoval)> + '_ {
        self.zone_removals.iter().map(|(&index, &removal)| (index, removal))
    }

    #[must_use]
    pub fn zone_removal_count(&self) -> usize {
        self.zone_removals.len()
    }

    #[must_use]
    pub fn is_filled(&self, x: i32, y: i32) -> bool {
        let index = self.index(x, y);
        self.zones[index] != ZoneType::None && self.buildings[index] != 0
    }

    pub fn clear_cell(&mut self, x: i32, y: i32) {
        let index = self.index(x, y);
        self.set_has_road(x, y, false);
        self.set_zone(x, y, ZoneType::None);
        self.set_building(x, y, None);
        self.households[index] = Household::default();
        self.features[index] = 0;
    }

    #[must_use]
    pub fn terrain_layer(&self) -> &[u8] {
        &self.terrain
    }

    #[must_use]
    pub fn feature_layer(&self) -> &[u8] {
        &self.features
    }

    #[must_use]
    pub fn road_layer(&self) -> &[bool] {
        &self.roads
    }

    #[must_use]
    pub fn road_type_layer(&self) -> &[u8] {
        &self.road_types
    }

    #[must_use]
    pub fn zone_layer(&self) -> &[ZoneType] {
        &self.zones
    }

    #[must_use]
    pub fn building_layer(&self) -> &[u8] {
        &self.buildings
    }

    #[must_use]
    pub fn household_layer(&self) -> &[Household] {
        &self.households
    }
}
