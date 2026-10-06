use std::collections::HashSet;
use std::error::Error;
use std::fmt;
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use atomic_write_file::AtomicWriteFile;
use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use flate2::Compression;
use flate2::read::DeflateDecoder;
use flate2::write::DeflateEncoder;
use serde::{Deserialize, Serialize};

use crate::{
    CityGame, GameConfig, GameContent, GameMap, GameRandom, GameSpeed, GrowthState, Household,
    MILESTONES, RegisteredType, TaxRates, WeekReport, ZoneRemoval, ZoneType,
};

pub const RUST_SAVE_FORMAT: &str = "termcity-rust-save";
pub const RUST_SAVE_VERSION: u32 = 1;
pub const RUST_SAVE_DIRECTORY: &str = "TermCityRust";
pub const RUST_QUICKSAVE_FILE: &str = "quicksave-rust.json";

const COMPRESSION: &str = "deflate";
const NONE_MARKER: u8 = u8::MAX;
const MAX_SAVE_CELLS: usize = 4_194_304;

#[derive(Debug)]
pub enum PersistenceError {
    DefaultPathUnavailable,
    Io { operation: &'static str, path: PathBuf, source: io::Error },
    Json(serde_json::Error),
    WrongFormat(String),
    UnsupportedVersion(u32),
    Validation(String),
    InvalidBase64 { layer: &'static str, source: base64::DecodeError },
    InvalidCompression { layer: &'static str, source: io::Error },
}

impl fmt::Display for PersistenceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::DefaultPathUnavailable => formatter
                .write_str("the operating system's local application data path is unavailable"),
            Self::Io { operation, path, source } => {
                write!(formatter, "failed to {operation} '{}': {source}", path.display())
            }
            Self::Json(error) => write!(formatter, "save JSON is invalid: {error}"),
            Self::WrongFormat(format) => {
                write!(
                    formatter,
                    "unsupported save format '{format}' (expected '{RUST_SAVE_FORMAT}')"
                )
            }
            Self::UnsupportedVersion(version) => write!(
                formatter,
                "unsupported Rust save version {version} (expected {RUST_SAVE_VERSION})"
            ),
            Self::Validation(message) => write!(formatter, "save validation failed: {message}"),
            Self::InvalidBase64 { layer, source } => {
                write!(formatter, "the {layer} layer is not valid base64: {source}")
            }
            Self::InvalidCompression { layer, source } => {
                write!(formatter, "the {layer} layer is not valid deflate data: {source}")
            }
        }
    }
}

impl Error for PersistenceError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io { source, .. } | Self::InvalidCompression { source, .. } => Some(source),
            Self::Json(source) => Some(source),
            Self::InvalidBase64 { source, .. } => Some(source),
            Self::DefaultPathUnavailable
            | Self::WrongFormat(_)
            | Self::UnsupportedVersion(_)
            | Self::Validation(_) => None,
        }
    }
}

impl From<serde_json::Error> for PersistenceError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

pub struct SaveGameStore;

impl SaveGameStore {
    /// Returns a Rust-specific quick-save path that cannot collide with the C# save.
    ///
    /// # Errors
    ///
    /// Returns an error when the platform's local application data directory is unavailable.
    pub fn default_path() -> Result<PathBuf, PersistenceError> {
        #[cfg(target_os = "windows")]
        let root = std::env::var_os("LOCALAPPDATA").map(PathBuf::from);
        #[cfg(target_os = "macos")]
        let root = std::env::var_os("HOME")
            .map(PathBuf::from)
            .map(|home| home.join("Library").join("Application Support"));
        #[cfg(all(not(target_os = "windows"), not(target_os = "macos")))]
        let root = std::env::var_os("XDG_DATA_HOME").map(PathBuf::from).or_else(|| {
            std::env::var_os("HOME")
                .map(PathBuf::from)
                .map(|home| home.join(".local").join("share"))
        });

        root.map(|path| path.join(RUST_SAVE_DIRECTORY).join(RUST_QUICKSAVE_FILE))
            .ok_or(PersistenceError::DefaultPathUnavailable)
    }

    /// Serializes the complete game to pretty-printed Rust save JSON.
    ///
    /// # Errors
    ///
    /// Returns an error if the live game contains state that cannot be represented safely.
    pub fn serialize(game: &CityGame) -> Result<String, PersistenceError> {
        let data = SaveData::from_game(game)?;
        serde_json::to_string_pretty(&data).map_err(Into::into)
    }

    /// Deserializes a game using the built-in content registry.
    ///
    /// # Errors
    ///
    /// Returns an error for malformed, unsupported, incomplete, or inconsistent save data.
    pub fn deserialize(json: &str) -> Result<CityGame, PersistenceError> {
        Self::deserialize_with_content(json, GameContent::default())
    }

    /// Deserializes a game and resolves all palette names against caller-supplied content.
    ///
    /// # Errors
    ///
    /// Returns an error for malformed, unsupported, incomplete, or inconsistent save data.
    pub fn deserialize_with_content(
        json: &str,
        content: GameContent,
    ) -> Result<CityGame, PersistenceError> {
        let data: SaveData = serde_json::from_str(json)?;
        data.into_game(content)
    }

    /// Atomically writes a save using a same-directory temporary file and replacement.
    ///
    /// # Errors
    ///
    /// Returns serialization, directory creation, write, synchronization, or replacement errors.
    pub fn save(game: &CityGame, path: impl AsRef<Path>) -> Result<(), PersistenceError> {
        let path = path.as_ref();
        let json = Self::serialize(game)?;
        if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
            fs::create_dir_all(parent)
                .map_err(|source| io_error("create save directory", parent, source))?;
        }
        let mut file = AtomicWriteFile::options()
            .open(path)
            .map_err(|source| io_error("open atomic save", path, source))?;
        file.write_all(json.as_bytes())
            .map_err(|source| io_error("write atomic save", path, source))?;
        file.commit().map_err(|source| io_error("replace save", path, source))
    }

    /// Saves to [`SaveGameStore::default_path`].
    ///
    /// # Errors
    ///
    /// Returns path discovery or save errors.
    pub fn save_default(game: &CityGame) -> Result<(), PersistenceError> {
        Self::save(game, Self::default_path()?)
    }

    /// Loads a save using the built-in content registry.
    ///
    /// # Errors
    ///
    /// Returns file read or deserialization errors.
    pub fn load(path: impl AsRef<Path>) -> Result<CityGame, PersistenceError> {
        let path = path.as_ref();
        let json =
            fs::read_to_string(path).map_err(|source| io_error("read save", path, source))?;
        Self::deserialize(&json)
    }

    /// Loads a save and resolves palettes against caller-supplied content.
    ///
    /// # Errors
    ///
    /// Returns file read or deserialization errors.
    pub fn load_with_content(
        path: impl AsRef<Path>,
        content: GameContent,
    ) -> Result<CityGame, PersistenceError> {
        let path = path.as_ref();
        let json =
            fs::read_to_string(path).map_err(|source| io_error("read save", path, source))?;
        Self::deserialize_with_content(&json, content)
    }

    /// Loads the Rust-specific quick save using the built-in content registry.
    ///
    /// # Errors
    ///
    /// Returns path discovery, file read, or deserialization errors.
    pub fn load_default() -> Result<CityGame, PersistenceError> {
        Self::load(Self::default_path()?)
    }
}

fn io_error(operation: &'static str, path: &Path, source: io::Error) -> PersistenceError {
    PersistenceError::Io { operation, path: path.to_owned(), source }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct SaveData {
    format: String,
    version: u32,
    compression: String,
    config: ConfigData,
    money: i32,
    week: i32,
    day: i32,
    day_progress_seconds: f64,
    rng_state: u64,
    speed: SpeedData,
    paused: bool,
    taxes: TaxData,
    growth: GrowthData,
    last_report: Option<ReportData>,
    highest_milestone: i32,
    guide_dismissed: bool,
    terrain: PaletteLayer,
    features: PaletteLayer,
    buildings: PaletteLayer,
    roads: String,
    road_types: PaletteLayer,
    zones: String,
    households: String,
    zone_removals: Vec<RemovalData>,
}

impl SaveData {
    fn from_game(game: &CityGame) -> Result<Self, PersistenceError> {
        validate_config(&game.config)?;
        validate_game_scalars(game)?;
        validate_map_semantics(game)?;
        let map = &game.map;
        let roads: Vec<u8> = map.road_layer().iter().map(|&road| u8::from(road)).collect();
        let zones: Vec<u8> = map.zone_layer().iter().map(|&zone| zone as u8).collect();
        let households = encode_households(map.household_layer());
        let mut removals: Vec<_> = map
            .zone_removals()
            .map(|(index, removal)| RemovalData {
                index,
                zone: ZoneData::from(removal.zone),
                remove_at_day: removal.remove_at_day,
            })
            .collect();
        removals.sort_unstable_by_key(|removal| removal.index);

        Ok(Self {
            format: RUST_SAVE_FORMAT.to_owned(),
            version: RUST_SAVE_VERSION,
            compression: COMPRESSION.to_owned(),
            config: ConfigData::from(&game.config),
            money: game.money,
            week: game.week,
            day: game.day,
            day_progress_seconds: game.day_progress_seconds(),
            rng_state: game.rng.state(),
            speed: SpeedData::from(game.speed),
            paused: game.paused,
            taxes: TaxData::from(game.taxes),
            growth: GrowthData::from(game.growth_state()),
            last_report: game.last_report.map(ReportData::from),
            highest_milestone: game.highest_milestone,
            guide_dismissed: game.guide_dismissed,
            terrain: encode_palette_layer(
                map.terrain_layer(),
                &map.content().terrains,
                false,
                "terrain",
            )?,
            features: encode_palette_layer(
                map.feature_layer(),
                &map.content().features,
                true,
                "feature",
            )?,
            buildings: encode_palette_layer(
                map.building_layer(),
                &map.content().buildings,
                true,
                "building",
            )?,
            roads: pack(&roads, "roads")?,
            road_types: encode_palette_layer(
                map.road_type_layer(),
                &map.content().roads,
                true,
                "road type",
            )?,
            zones: pack(&zones, "zones")?,
            households: pack(&households, "households")?,
            zone_removals: removals,
        })
    }

    fn into_game(self, content: GameContent) -> Result<CityGame, PersistenceError> {
        self.validate_header()?;
        let config = self.config.into_config();
        validate_config(&config)?;
        let mut map = GameMap::new(config.map_width, config.map_height, content)
            .map_err(|error| PersistenceError::Validation(error.to_string()))?;
        let count = map.terrain_layer().len();

        let terrain =
            decode_palette_layer(self.terrain, &map.content().terrains, false, count, "terrain")?;
        let features =
            decode_palette_layer(self.features, &map.content().features, true, count, "feature")?;
        let buildings = decode_palette_layer(
            self.buildings,
            &map.content().buildings,
            true,
            count,
            "building",
        )?;
        let road_types =
            decode_palette_layer(self.road_types, &map.content().roads, true, count, "road type")?;
        let roads = unpack(&self.roads, count, "roads")?;
        let zones = unpack(&self.zones, count, "zones")?;
        let household_bytes = unpack(
            &self.households,
            count.checked_mul(3).ok_or_else(|| {
                PersistenceError::Validation("household layer size overflows".to_owned())
            })?,
            "households",
        )?;
        let households = decode_households(&household_bytes);

        for index in 0..count {
            let position = map.position_of(index);
            map.set_terrain(position.x, position.y, terrain[index]);
            map.set_feature(
                position.x,
                position.y,
                (features[index] != 0).then_some(features[index]),
            );
            match roads[index] {
                0 if road_types[index] == 0 => {}
                0 => {
                    return validation(format!("road type exists without a road at index {index}"));
                }
                1 if road_types[index] != 0 => {
                    map.set_road(position.x, position.y, road_types[index]);
                }
                1 => return validation(format!("road at index {index} has no road type")),
                value => {
                    return validation(format!("invalid road value {value} at index {index}"));
                }
            }
            let zone = ZoneType::try_from(zones[index]).map_err(|()| {
                PersistenceError::Validation(format!(
                    "invalid zone value {} at index {index}",
                    zones[index]
                ))
            })?;
            map.set_zone(position.x, position.y, zone);
            map.set_building(
                position.x,
                position.y,
                (buildings[index] != 0).then_some(buildings[index]),
            );
            map.set_household(position.x, position.y, households[index]);
        }

        let mut removal_indexes = HashSet::new();
        for removal in self.zone_removals {
            if removal.index >= count || !removal_indexes.insert(removal.index) {
                return validation(format!(
                    "invalid or duplicate pending removal index {}",
                    removal.index
                ));
            }
            let zone = ZoneType::from(removal.zone);
            let position = map.position_of(removal.index);
            map.set_zone_removal(
                position.x,
                position.y,
                ZoneRemoval { zone, remove_at_day: removal.remove_at_day },
            );
        }

        let mut game = CityGame::from_map(config, map);
        game.money = self.money;
        game.week = self.week;
        game.day = self.day;
        game.set_day_progress_seconds(self.day_progress_seconds);
        game.rng = GameRandom::new(self.rng_state);
        game.speed = GameSpeed::from(self.speed);
        game.paused = self.paused;
        game.taxes = self.taxes.into_tax_rates();
        game.restore_growth_state(self.growth.into_growth_state());
        game.last_report = self.last_report.map(ReportData::into_week_report);
        game.highest_milestone = self.highest_milestone;
        game.guide_dismissed = self.guide_dismissed;
        validate_game_scalars(&game)?;
        validate_map_semantics(&game)?;
        Ok(game)
    }

    fn validate_header(&self) -> Result<(), PersistenceError> {
        if self.format != RUST_SAVE_FORMAT {
            return Err(PersistenceError::WrongFormat(self.format.clone()));
        }
        if self.version != RUST_SAVE_VERSION {
            return Err(PersistenceError::UnsupportedVersion(self.version));
        }
        if self.compression != COMPRESSION {
            return validation(format!("unsupported compression '{}'", self.compression));
        }
        Ok(())
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct PaletteLayer {
    palette: Vec<String>,
    data: String,
}

fn encode_palette_layer<T: RegisteredType>(
    layer: &[u8],
    registry: &crate::TypeRegistry<T>,
    has_none: bool,
    label: &'static str,
) -> Result<PaletteLayer, PersistenceError> {
    let palette: Vec<String> = registry.iter().map(|item| item.name().to_owned()).collect();
    if palette.len() > usize::from(NONE_MARKER) {
        return validation(format!("{label} palette is too large"));
    }
    let mut bytes = Vec::with_capacity(layer.len());
    for (index, &id) in layer.iter().enumerate() {
        if has_none && id == 0 {
            bytes.push(NONE_MARKER);
            continue;
        }
        let item = registry.get_by_id(id).ok_or_else(|| {
            PersistenceError::Validation(format!(
                "{label} layer has unknown id {id} at index {index}"
            ))
        })?;
        let palette_index = registry
            .iter()
            .position(|candidate| candidate.id() == item.id())
            .expect("registered item is in its registry");
        bytes.push(u8::try_from(palette_index).map_err(|_| {
            PersistenceError::Validation(format!("{label} palette index does not fit in a byte"))
        })?);
    }
    Ok(PaletteLayer { palette, data: pack(&bytes, label)? })
}

fn decode_palette_layer<T: RegisteredType>(
    layer: PaletteLayer,
    registry: &crate::TypeRegistry<T>,
    has_none: bool,
    expected_len: usize,
    label: &'static str,
) -> Result<Vec<u8>, PersistenceError> {
    if layer.palette.len() > usize::from(NONE_MARKER) {
        return validation(format!("{label} palette is too large"));
    }
    let mut names = HashSet::with_capacity(layer.palette.len());
    let mut ids = Vec::with_capacity(layer.palette.len());
    for name in layer.palette {
        if name.is_empty() || !names.insert(name.to_lowercase()) {
            return validation(format!("{label} palette contains an empty or duplicate name"));
        }
        let item = registry.find(&name).ok_or_else(|| {
            PersistenceError::Validation(format!("unknown {label} type name '{name}'"))
        })?;
        ids.push(item.id());
    }
    let bytes = unpack(&layer.data, expected_len, label)?;
    bytes
        .into_iter()
        .enumerate()
        .map(|(index, value)| {
            if has_none && value == NONE_MARKER {
                Ok(0)
            } else {
                ids.get(usize::from(value)).copied().ok_or_else(|| {
                    PersistenceError::Validation(format!(
                        "{label} palette index {value} is invalid at map index {index}"
                    ))
                })
            }
        })
        .collect()
}

fn pack(bytes: &[u8], label: &'static str) -> Result<String, PersistenceError> {
    let mut encoder = DeflateEncoder::new(Vec::new(), Compression::fast());
    encoder
        .write_all(bytes)
        .map_err(|source| PersistenceError::InvalidCompression { layer: label, source })?;
    let compressed = encoder
        .finish()
        .map_err(|source| PersistenceError::InvalidCompression { layer: label, source })?;
    Ok(BASE64.encode(compressed))
}

fn unpack(
    encoded: &str,
    expected_len: usize,
    label: &'static str,
) -> Result<Vec<u8>, PersistenceError> {
    let compressed = BASE64
        .decode(encoded)
        .map_err(|source| PersistenceError::InvalidBase64 { layer: label, source })?;
    let limit = u64::try_from(expected_len)
        .ok()
        .and_then(|length| length.checked_add(1))
        .ok_or_else(|| PersistenceError::Validation(format!("{label} layer size overflows")))?;
    let mut inflater = DeflateDecoder::new(compressed.as_slice()).take(limit);
    let mut bytes = Vec::with_capacity(expected_len.min(64 * 1024));
    inflater
        .read_to_end(&mut bytes)
        .map_err(|source| PersistenceError::InvalidCompression { layer: label, source })?;
    if bytes.len() != expected_len {
        return validation(format!(
            "{label} layer has length {}, expected {expected_len}",
            bytes.len()
        ));
    }
    Ok(bytes)
}

fn encode_households(households: &[Household]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(households.len() * 3);
    for household in households {
        bytes.extend([household.adults, household.children, household.seniors]);
    }
    bytes
}

fn decode_households(bytes: &[u8]) -> Vec<Household> {
    bytes.chunks_exact(3).map(|values| Household::new(values[0], values[1], values[2])).collect()
}

fn validate_config(config: &GameConfig) -> Result<(), PersistenceError> {
    require(
        config.map_width >= 8 && config.map_height >= 8,
        "map dimensions must each be at least eight",
    )?;
    let cell_count = usize::try_from(config.map_width)
        .ok()
        .and_then(|width| {
            usize::try_from(config.map_height).ok().and_then(|height| width.checked_mul(height))
        })
        .ok_or_else(|| PersistenceError::Validation("map dimensions overflow".to_owned()))?;
    require(
        cell_count <= MAX_SAVE_CELLS,
        format!("map exceeds the Rust save limit of {MAX_SAVE_CELLS} cells"),
    )?;
    require(config.road_cost_per_cell >= 0, "road cost must be non-negative")?;
    require(config.min_residential_cells > 0, "minimum residential cells must be positive")?;
    require(
        config.residential_per_commercial > 0 && config.residential_per_industrial > 0,
        "residential capacity ratios must be positive",
    )?;
    finite_range(config.default_tax_rate, 0.0, 1.0, "default tax rate")?;
    require(config.road_service_reach >= 0, "road service reach must be non-negative")?;
    require(
        config.max_new_residential_per_week >= 0
            && config.max_new_commercial_per_week >= 0
            && config.max_new_industrial_per_week >= 0,
        "weekly growth caps must be non-negative",
    )?;
    finite_range(config.growth_rate_per_week, 0.0, 1.0, "weekly growth rate")?;
    require(config.weeks_per_year > 0, "weeks per year must be positive")?;
    require(config.days_per_week > 0, "days per week must be positive")?;
    positive_finite(config.slow_seconds_per_week, "slow seconds per week")?;
    positive_finite(config.medium_seconds_per_week, "medium seconds per week")?;
    positive_finite(config.fast_seconds_per_week, "fast seconds per week")
}

fn validate_game_scalars(game: &CityGame) -> Result<(), PersistenceError> {
    require(game.week >= 0, "week must be non-negative")?;
    require(
        (0..game.config.days_per_week).contains(&game.day),
        "day is outside the configured week",
    )?;
    let seconds_per_day =
        game.config.seconds_per_week(game.speed) / f64::from(game.config.days_per_week);
    finite_range(game.day_progress_seconds(), 0.0, seconds_per_day, "day progress seconds")?;
    require(
        game.day_progress_seconds() < seconds_per_day,
        "day progress must be less than one day",
    )?;
    for (label, rate) in [
        ("residential tax rate", game.taxes.residential),
        ("commercial tax rate", game.taxes.commercial),
        ("industrial tax rate", game.taxes.industrial),
    ] {
        finite_range(rate, 0.0, 1.0, label)?;
    }
    let growth = game.growth_state();
    require(
        [
            growth.homes,
            growth.shops,
            growth.factories,
            growth.week_homes,
            growth.week_shops,
            growth.week_factories,
        ]
        .into_iter()
        .all(|value| value >= 0),
        "growth counts must be non-negative",
    )?;
    if let Some(report) = game.last_report {
        require(
            report.week > 0
                && report.week <= game.week
                && report.new_households >= 0
                && report.new_commercial >= 0
                && report.new_industrial >= 0,
            "last report is inconsistent with the game clock",
        )?;
    }
    require(
        game.highest_milestone == 0 || MILESTONES.contains(&game.highest_milestone),
        "highest milestone is not a known milestone",
    )
}

fn validate_map_semantics(game: &CityGame) -> Result<(), PersistenceError> {
    let map = &game.map;
    require(
        map.width() == game.config.map_width && map.height() == game.config.map_height,
        "map dimensions do not match the game configuration",
    )?;
    let count = usize::try_from(map.width())
        .ok()
        .and_then(|width| {
            usize::try_from(map.height()).ok().and_then(|height| width.checked_mul(height))
        })
        .ok_or_else(|| PersistenceError::Validation("map dimensions overflow".to_owned()))?;
    require(
        map.terrain_layer().len() == count
            && map.feature_layer().len() == count
            && map.road_layer().len() == count
            && map.road_type_layer().len() == count
            && map.zone_layer().len() == count
            && map.building_layer().len() == count
            && map.household_layer().len() == count,
        "map layer lengths do not match its dimensions",
    )?;
    let removal_map: std::collections::HashMap<_, _> = map.zone_removals().collect();
    require(removal_map.len() == map.zone_removal_count(), "duplicate pending removals")?;

    for index in 0..count {
        let terrain = map.terrain_layer()[index];
        require(
            map.content().terrains.get_by_id(terrain).is_some(),
            format!("unknown terrain id {terrain} at index {index}"),
        )?;
        let feature = map.feature_layer()[index];
        require(
            feature == 0 || map.content().features.get_by_id(feature).is_some(),
            format!("unknown feature id {feature} at index {index}"),
        )?;
        let road_type = map.road_type_layer()[index];
        require(
            (!map.road_layer()[index] && road_type == 0)
                || (map.road_layer()[index]
                    && road_type != 0
                    && map.content().roads.get_by_id(road_type).is_some()),
            format!("road and road type are inconsistent at index {index}"),
        )?;
        let building_id = map.building_layer()[index];
        let building =
            (building_id != 0).then(|| map.content().buildings.get_by_id(building_id)).flatten();
        require(
            building_id == 0 || building.is_some(),
            format!("unknown building id {building_id} at index {index}"),
        )?;
        let zone = map.zone_layer()[index];
        let removal = removal_map.get(&index);
        if let Some(removal) = removal {
            require(
                zone == ZoneType::None
                    && removal.zone != ZoneType::None
                    && building.is_some_and(|item| item.zone == removal.zone)
                    && removal.remove_at_day.is_finite()
                    && removal.remove_at_day >= 0.0,
                format!("invalid pending zone removal at index {index}"),
            )?;
        }
        if let Some(building) = building {
            if building.zone != ZoneType::None {
                require(
                    zone == building.zone
                        || removal.is_some_and(|pending| pending.zone == building.zone),
                    format!("building and zone are inconsistent at index {index}"),
                )?;
            }
        } else {
            require(
                removal.is_none(),
                format!("pending removal has no building at index {index}"),
            )?;
        }
        require(
            map.household_layer()[index].is_empty()
                || building.is_some_and(|item| item.zone == ZoneType::Residential),
            format!("household exists outside residential housing at index {index}"),
        )?;
    }
    Ok(())
}

fn require(condition: bool, message: impl Into<String>) -> Result<(), PersistenceError> {
    if condition { Ok(()) } else { validation(message) }
}

fn validation<T>(message: impl Into<String>) -> Result<T, PersistenceError> {
    Err(PersistenceError::Validation(message.into()))
}

fn positive_finite(value: f64, label: &str) -> Result<(), PersistenceError> {
    require(value.is_finite() && value > 0.0, format!("{label} must be finite and positive"))
}

fn finite_range(
    value: f64,
    minimum: f64,
    maximum: f64,
    label: &str,
) -> Result<(), PersistenceError> {
    require(
        value.is_finite() && (minimum..=maximum).contains(&value),
        format!("{label} must be finite and between {minimum} and {maximum}"),
    )
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ConfigData {
    map_width: i32,
    map_height: i32,
    seed: i32,
    starting_money: i32,
    road_cost_per_cell: i32,
    min_residential_cells: i32,
    residential_per_commercial: i32,
    residential_per_industrial: i32,
    default_tax_rate: f64,
    road_service_reach: i32,
    max_new_residential_per_week: i32,
    max_new_commercial_per_week: i32,
    max_new_industrial_per_week: i32,
    growth_rate_per_week: f64,
    weeks_per_year: i32,
    days_per_week: i32,
    slow_seconds_per_week: f64,
    medium_seconds_per_week: f64,
    fast_seconds_per_week: f64,
}

impl From<&GameConfig> for ConfigData {
    fn from(config: &GameConfig) -> Self {
        Self {
            map_width: config.map_width,
            map_height: config.map_height,
            seed: config.seed,
            starting_money: config.starting_money,
            road_cost_per_cell: config.road_cost_per_cell,
            min_residential_cells: config.min_residential_cells,
            residential_per_commercial: config.residential_per_commercial,
            residential_per_industrial: config.residential_per_industrial,
            default_tax_rate: config.default_tax_rate,
            road_service_reach: config.road_service_reach,
            max_new_residential_per_week: config.max_new_residential_per_week,
            max_new_commercial_per_week: config.max_new_commercial_per_week,
            max_new_industrial_per_week: config.max_new_industrial_per_week,
            growth_rate_per_week: config.growth_rate_per_week,
            weeks_per_year: config.weeks_per_year,
            days_per_week: config.days_per_week,
            slow_seconds_per_week: config.slow_seconds_per_week,
            medium_seconds_per_week: config.medium_seconds_per_week,
            fast_seconds_per_week: config.fast_seconds_per_week,
        }
    }
}

impl ConfigData {
    fn into_config(self) -> GameConfig {
        GameConfig {
            map_width: self.map_width,
            map_height: self.map_height,
            seed: self.seed,
            starting_money: self.starting_money,
            road_cost_per_cell: self.road_cost_per_cell,
            min_residential_cells: self.min_residential_cells,
            residential_per_commercial: self.residential_per_commercial,
            residential_per_industrial: self.residential_per_industrial,
            default_tax_rate: self.default_tax_rate,
            road_service_reach: self.road_service_reach,
            max_new_residential_per_week: self.max_new_residential_per_week,
            max_new_commercial_per_week: self.max_new_commercial_per_week,
            max_new_industrial_per_week: self.max_new_industrial_per_week,
            growth_rate_per_week: self.growth_rate_per_week,
            weeks_per_year: self.weeks_per_year,
            days_per_week: self.days_per_week,
            slow_seconds_per_week: self.slow_seconds_per_week,
            medium_seconds_per_week: self.medium_seconds_per_week,
            fast_seconds_per_week: self.fast_seconds_per_week,
        }
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum SpeedData {
    Slow,
    Medium,
    Fast,
}

impl From<GameSpeed> for SpeedData {
    fn from(speed: GameSpeed) -> Self {
        match speed {
            GameSpeed::Slow => Self::Slow,
            GameSpeed::Medium => Self::Medium,
            GameSpeed::Fast => Self::Fast,
        }
    }
}

impl From<SpeedData> for GameSpeed {
    fn from(speed: SpeedData) -> Self {
        match speed {
            SpeedData::Slow => Self::Slow,
            SpeedData::Medium => Self::Medium,
            SpeedData::Fast => Self::Fast,
        }
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum ZoneData {
    Residential,
    Commercial,
    Industrial,
}

impl From<ZoneType> for ZoneData {
    fn from(zone: ZoneType) -> Self {
        match zone {
            ZoneType::Residential => Self::Residential,
            ZoneType::Commercial => Self::Commercial,
            ZoneType::Industrial => Self::Industrial,
            ZoneType::None => unreachable!("pending removals cannot use the none zone"),
        }
    }
}

impl From<ZoneData> for ZoneType {
    fn from(zone: ZoneData) -> Self {
        match zone {
            ZoneData::Residential => Self::Residential,
            ZoneData::Commercial => Self::Commercial,
            ZoneData::Industrial => Self::Industrial,
        }
    }
}

impl TryFrom<u8> for ZoneType {
    type Error = ();

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::None),
            1 => Ok(Self::Residential),
            2 => Ok(Self::Commercial),
            3 => Ok(Self::Industrial),
            _ => Err(()),
        }
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct TaxData {
    residential: f64,
    commercial: f64,
    industrial: f64,
}

impl From<TaxRates> for TaxData {
    fn from(taxes: TaxRates) -> Self {
        Self {
            residential: taxes.residential,
            commercial: taxes.commercial,
            industrial: taxes.industrial,
        }
    }
}

impl TaxData {
    fn into_tax_rates(self) -> TaxRates {
        TaxRates {
            residential: self.residential,
            commercial: self.commercial,
            industrial: self.industrial,
        }
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct GrowthData {
    planned: bool,
    homes: i32,
    shops: i32,
    factories: i32,
    week_homes: i32,
    week_shops: i32,
    week_factories: i32,
}

impl From<GrowthState> for GrowthData {
    fn from(growth: GrowthState) -> Self {
        Self {
            planned: growth.planned,
            homes: growth.homes,
            shops: growth.shops,
            factories: growth.factories,
            week_homes: growth.week_homes,
            week_shops: growth.week_shops,
            week_factories: growth.week_factories,
        }
    }
}

impl GrowthData {
    fn into_growth_state(self) -> GrowthState {
        GrowthState {
            planned: self.planned,
            homes: self.homes,
            shops: self.shops,
            factories: self.factories,
            week_homes: self.week_homes,
            week_shops: self.week_shops,
            week_factories: self.week_factories,
        }
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ReportData {
    week: i32,
    income: i32,
    new_households: i32,
    new_commercial: i32,
    new_industrial: i32,
}

impl From<WeekReport> for ReportData {
    fn from(report: WeekReport) -> Self {
        Self {
            week: report.week,
            income: report.income,
            new_households: report.new_households,
            new_commercial: report.new_commercial,
            new_industrial: report.new_industrial,
        }
    }
}

impl ReportData {
    fn into_week_report(self) -> WeekReport {
        WeekReport {
            week: self.week,
            income: self.income,
            new_households: self.new_households,
            new_commercial: self.new_commercial,
            new_industrial: self.new_industrial,
        }
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct RemovalData {
    index: usize,
    zone: ZoneData,
    remove_at_day: f64,
}
