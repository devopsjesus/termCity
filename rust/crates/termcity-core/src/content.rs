use std::collections::HashMap;
use std::error::Error;
use std::fmt;

use crate::{Rgb, ZoneType, pick_cell_variant};

pub trait RegisteredType {
    fn name(&self) -> &str;
    fn id(&self) -> u8;
    fn set_id(&mut self, id: u8);
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RegistryError {
    DuplicateName(String),
    TooManyTypes,
}

impl fmt::Display for RegistryError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::DuplicateName(name) => write!(formatter, "'{name}' is already registered."),
            Self::TooManyTypes => formatter.write_str("Too many registered types."),
        }
    }
}

impl Error for RegistryError {}

#[derive(Clone, Debug)]
pub struct TypeRegistry<T> {
    items: Vec<T>,
    by_name: HashMap<String, usize>,
    first_id: u8,
}

impl<T: RegisteredType> TypeRegistry<T> {
    #[must_use]
    pub fn new(first_id: u8) -> Self {
        Self { items: Vec::new(), by_name: HashMap::new(), first_id }
    }

    #[must_use]
    pub fn len(&self) -> usize {
        self.items.len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    /// Registers a type and assigns its compact in-memory id.
    ///
    /// # Errors
    ///
    /// Returns an error when the name is already present (case-insensitively) or no byte id remains.
    pub fn register(&mut self, mut item: T) -> Result<&T, RegistryError> {
        let key = item.name().to_lowercase();
        if self.by_name.contains_key(&key) {
            return Err(RegistryError::DuplicateName(item.name().to_owned()));
        }

        let offset = u8::try_from(self.items.len()).map_err(|_| RegistryError::TooManyTypes)?;
        let id = self.first_id.checked_add(offset).ok_or(RegistryError::TooManyTypes)?;
        item.set_id(id);
        self.items.push(item);
        let index = self.items.len() - 1;
        self.by_name.insert(key, index);
        Ok(&self.items[index])
    }

    #[must_use]
    pub fn find(&self, name: &str) -> Option<&T> {
        self.by_name.get(&name.to_lowercase()).map(|&index| &self.items[index])
    }

    #[must_use]
    pub fn get_by_id(&self, id: u8) -> Option<&T> {
        id.checked_sub(self.first_id).and_then(|index| self.items.get(usize::from(index)))
    }

    pub fn iter(&self) -> impl ExactSizeIterator<Item = &T> {
        self.items.iter()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TerrainGeneratorKind {
    Hills,
    Water,
}

#[derive(Clone, Debug, PartialEq)]
pub struct TerrainType {
    id: u8,
    pub name: String,
    pub glyphs: Vec<&'static str>,
    pub foreground: Rgb,
    pub background: Rgb,
    pub buildable: bool,
    pub build_cost_modifier: f64,
    pub allows_features: bool,
    pub feature_density: HashMap<String, f64>,
    pub generator: Option<TerrainGeneratorKind>,
    pub description: String,
}

impl TerrainType {
    #[must_use]
    pub fn glyph_at(&self, x: i32, y: i32) -> &'static str {
        self.glyphs[pick_cell_variant(x, y, self.glyphs.len())]
    }

    #[must_use]
    pub fn density_for(&self, feature_name: &str) -> f64 {
        self.feature_density.get(feature_name).copied().unwrap_or(1.0)
    }
}

impl RegisteredType for TerrainType {
    fn name(&self) -> &str {
        &self.name
    }

    fn id(&self) -> u8 {
        self.id
    }

    fn set_id(&mut self, id: u8) {
        self.id = id;
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ScatterFeatureGenerator {
    pub order: i32,
    pub base_density: f64,
    pub cluster_coverage: f64,
    pub cluster_density: f64,
    pub cluster_frequency: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct FeatureType {
    id: u8,
    pub name: String,
    pub glyphs: Vec<&'static str>,
    pub foreground: Rgb,
    pub generator: Option<ScatterFeatureGenerator>,
    pub description: String,
}

impl FeatureType {
    #[must_use]
    pub fn glyph_at(&self, x: i32, y: i32) -> &'static str {
        self.glyphs[pick_cell_variant(x, y, self.glyphs.len())]
    }
}

impl RegisteredType for FeatureType {
    fn name(&self) -> &str {
        &self.name
    }

    fn id(&self) -> u8 {
        self.id
    }

    fn set_id(&mut self, id: u8) {
        self.id = id;
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct BuildingType {
    id: u8,
    pub name: String,
    pub glyphs: Vec<&'static str>,
    pub foreground: Rgb,
    pub zone: ZoneType,
    pub cost: i32,
    pub player_placeable: bool,
    pub description: String,
}

impl BuildingType {
    #[must_use]
    pub fn new(
        name: impl Into<String>,
        glyphs: Vec<&'static str>,
        foreground: Rgb,
        zone: ZoneType,
        cost: i32,
        player_placeable: bool,
        description: impl Into<String>,
    ) -> Self {
        Self {
            id: 0,
            name: name.into(),
            glyphs,
            foreground,
            zone,
            cost,
            player_placeable,
            description: description.into(),
        }
    }

    #[must_use]
    pub fn glyph_at(&self, x: i32, y: i32) -> &'static str {
        self.glyphs[pick_cell_variant(x, y, self.glyphs.len())]
    }
}

impl RegisteredType for BuildingType {
    fn name(&self) -> &str {
        &self.name
    }

    fn id(&self) -> u8 {
        self.id
    }

    fn set_id(&mut self, id: u8) {
        self.id = id;
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct RoadType {
    id: u8,
    pub name: String,
    pub glyphs: [&'static str; 16],
    pub foreground: Rgb,
    pub background: Rgb,
    pub cost_multiplier: f64,
    pub rank: i32,
    pub player_placeable: bool,
    pub description: String,
}

impl RoadType {
    #[must_use]
    pub fn glyph_for(&self, neighbour_mask: u8) -> &'static str {
        self.glyphs[usize::from(neighbour_mask & 15)]
    }
}

impl RegisteredType for RoadType {
    fn name(&self) -> &str {
        &self.name
    }

    fn id(&self) -> u8 {
        self.id
    }

    fn set_id(&mut self, id: u8) {
        self.id = id;
    }
}

#[derive(Clone, Debug)]
pub struct GameContent {
    pub terrains: TypeRegistry<TerrainType>,
    pub features: TypeRegistry<FeatureType>,
    pub buildings: TypeRegistry<BuildingType>,
    pub roads: TypeRegistry<RoadType>,
}

impl Default for GameContent {
    fn default() -> Self {
        Self {
            terrains: default_terrains(),
            features: default_features(),
            buildings: default_buildings(),
            roads: default_roads(),
        }
    }
}

impl GameContent {
    /// Returns the terrain that fills a new map before generators run.
    ///
    /// # Panics
    ///
    /// Panics if built-in content was constructed without a terrain at id zero.
    #[must_use]
    pub fn base_terrain(&self) -> &TerrainType {
        self.terrains.get_by_id(0).expect("default content has a base terrain")
    }

    /// Returns the lowest-ranked registered road.
    ///
    /// # Panics
    ///
    /// Panics if built-in content was constructed without any road types.
    #[must_use]
    pub fn default_road(&self) -> &RoadType {
        self.roads.iter().min_by_key(|road| road.rank).expect("default content has a road")
    }

    #[must_use]
    pub fn building_for_zone(&self, zone: ZoneType) -> Option<&BuildingType> {
        self.buildings.iter().find(|building| building.zone == zone)
    }
}

fn default_terrains() -> TypeRegistry<TerrainType> {
    let mut registry = TypeRegistry::new(0);
    let mut grass_glyphs = vec!["·"; 33];
    grass_glyphs.extend([",", "'", "\""]);
    register_default(
        &mut registry,
        TerrainType {
            id: 0,
            name: "Grass".into(),
            glyphs: grass_glyphs,
            foreground: Rgb::hex(0x4f_8f_4a),
            background: Rgb::hex(0x16_30_1a),
            buildable: true,
            build_cost_modifier: 1.0,
            allows_features: true,
            feature_density: HashMap::new(),
            generator: None,
            description: "Open grassland".into(),
        },
    );
    let mut hill_density = HashMap::new();
    hill_density.insert("Tree".into(), 0.5);
    hill_density.insert("Rock".into(), 3.0);
    register_default(
        &mut registry,
        TerrainType {
            id: 0,
            name: "Hill".into(),
            glyphs: vec!["∩", "∩", "∩", "∩", "∩", "∩", "∩", "⌒"],
            foreground: Rgb::hex(0xc9_a4_68),
            background: Rgb::hex(0x3d_33_20),
            buildable: true,
            build_cost_modifier: 1.5,
            allows_features: true,
            feature_density: hill_density,
            generator: Some(TerrainGeneratorKind::Hills),
            description: "Rolling hills (buildings cost 1.5x)".into(),
        },
    );
    register_default(
        &mut registry,
        TerrainType {
            id: 0,
            name: "Water".into(),
            glyphs: vec!["≈", "≈", "≈", "≈", "≈", "~"],
            foreground: Rgb::hex(0x7f_c4_ff),
            background: Rgb::hex(0x0f_3a_66),
            buildable: false,
            build_cost_modifier: 1.0,
            allows_features: false,
            feature_density: HashMap::new(),
            generator: Some(TerrainGeneratorKind::Water),
            description:
                "Rivers, lakes and the sea (cannot be built on; existing roads use bridges)".into(),
        },
    );
    registry
}

fn default_features() -> TypeRegistry<FeatureType> {
    let mut registry = TypeRegistry::new(1);
    register_default(
        &mut registry,
        FeatureType {
            id: 0,
            name: "Tree".into(),
            glyphs: vec!["♣", "♣", "♣", "♣", "♣", "♠"],
            foreground: Rgb::hex(0x3f_bf_4f),
            generator: Some(ScatterFeatureGenerator {
                order: 10,
                base_density: 0.012,
                cluster_coverage: 0.22,
                cluster_density: 0.55,
                cluster_frequency: 0.07,
            }),
            description: "Trees (cleared when built over)".into(),
        },
    );
    register_default(
        &mut registry,
        FeatureType {
            id: 0,
            name: "Rock".into(),
            glyphs: vec!["●", "●", "●", "●", "●", "◦"],
            foreground: Rgb::hex(0xa8_a8_a8),
            generator: Some(ScatterFeatureGenerator {
                order: 20,
                base_density: 0.012,
                cluster_coverage: 0.0,
                cluster_density: 0.0,
                cluster_frequency: 0.07,
            }),
            description: "Boulders (cleared when built over)".into(),
        },
    );
    registry
}

fn default_buildings() -> TypeRegistry<BuildingType> {
    let mut registry = TypeRegistry::new(1);
    for building in [
        BuildingType {
            id: 0,
            name: "House".into(),
            glyphs: vec!["⌂", "⌂", "⌂", "▟"],
            foreground: Rgb::hex(0x9d_ff_9d),
            zone: ZoneType::Residential,
            cost: 0,
            player_placeable: false,
            description: "A family home".into(),
        },
        BuildingType {
            id: 0,
            name: "Shop".into(),
            glyphs: vec!["▣", "▦", "▣"],
            foreground: Rgb::hex(0x9f_d0_ff),
            zone: ZoneType::Commercial,
            cost: 0,
            player_placeable: false,
            description: "Stores and offices".into(),
        },
        BuildingType {
            id: 0,
            name: "Factory".into(),
            glyphs: vec!["▤", "▩", "▤"],
            foreground: Rgb::hex(0xff_e0_8a),
            zone: ZoneType::Industrial,
            cost: 0,
            player_placeable: false,
            description: "Workshops and plants".into(),
        },
    ] {
        register_default(&mut registry, building);
    }
    registry
}

const LIGHT_ROAD: [&str; 16] =
    ["•", "│", "─", "└", "│", "│", "┌", "├", "─", "┘", "─", "┴", "┐", "┤", "┬", "┼"];
const HEAVY_ROAD: [&str; 16] =
    ["•", "┃", "━", "┗", "┃", "┃", "┏", "┣", "━", "┛", "━", "┻", "┓", "┫", "┳", "╋"];
const DOUBLE_ROAD: [&str; 16] =
    ["•", "║", "═", "╚", "║", "║", "╔", "╠", "═", "╝", "═", "╩", "╗", "╣", "╦", "╬"];

fn default_roads() -> TypeRegistry<RoadType> {
    let mut registry = TypeRegistry::new(1);
    for road in [
        RoadType {
            id: 0,
            name: "Street".into(),
            glyphs: LIGHT_ROAD,
            foreground: Rgb::hex(0xd6_d6_d6),
            background: Rgb::hex(0x2b_2b_30),
            cost_multiplier: 1.0,
            rank: 1,
            player_placeable: true,
            description: "Local street".into(),
        },
        RoadType {
            id: 0,
            name: "Avenue".into(),
            glyphs: HEAVY_ROAD,
            foreground: Rgb::hex(0xa9_d4_ff),
            background: Rgb::hex(0x2b_2f_3a),
            cost_multiplier: 1.8,
            rank: 2,
            player_placeable: true,
            description: "Wide avenue".into(),
        },
        RoadType {
            id: 0,
            name: "Highway".into(),
            glyphs: DOUBLE_ROAD,
            foreground: Rgb::hex(0xff_d1_66),
            background: Rgb::hex(0x33_30_2a),
            cost_multiplier: 3.0,
            rank: 3,
            player_placeable: true,
            description: "Highway".into(),
        },
    ] {
        register_default(&mut registry, road);
    }
    registry
}

fn register_default<T: RegisteredType>(registry: &mut TypeRegistry<T>, item: T) {
    registry.register(item).expect("built-in type names and ids are valid");
}
