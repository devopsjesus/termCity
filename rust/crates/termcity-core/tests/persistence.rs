use std::fs;
use std::io::{Read, Write};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use flate2::Compression;
use flate2::read::DeflateDecoder;
use flate2::write::DeflateEncoder;
use serde_json::{Value, json};
use tempfile::tempdir;
use termcity_core::{
    CellRect, CityGame, GameConfig, GameContent, GameMap, GameSpeed, Household, PersistenceError,
    RUST_QUICKSAVE_FILE, RUST_SAVE_DIRECTORY, RUST_SAVE_FORMAT, RUST_SAVE_VERSION, RegisteredType,
    SaveGameStore, ZoneType,
};

fn representative_game() -> CityGame {
    let config = GameConfig {
        map_width: 32,
        map_height: 24,
        seed: 77,
        starting_money: 90_000,
        slow_seconds_per_week: 9.0,
        medium_seconds_per_week: 4.5,
        fast_seconds_per_week: 1.5,
        ..GameConfig::default()
    };
    let content = GameContent::default();
    let mut map =
        GameMap::new(config.map_width, config.map_height, content).expect("valid test map");
    let hill = map.content().terrains.find("Hill").expect("hill").id();
    let water = map.content().terrains.find("Water").expect("water").id();
    let rock = map.content().features.find("Rock").expect("rock").id();
    let highway = map.content().roads.find("Highway").expect("highway").id();
    map.set_terrain(5, 5, hill);
    map.set_terrain(6, 5, water);
    map.set_feature(7, 5, Some(rock));
    for x in 0..map.width() {
        map.set_has_road(x, 20, true);
    }
    map.set_road(4, 20, highway);

    let mut game = CityGame::from_map(config, map);
    game.touch();
    assert!(game.designate(CellRect::new(0, 21, 20, 1), ZoneType::Residential).success);
    game.advance_week();
    game.advance_day();
    let occupied =
        (0..20).find(|&x| game.map.building_at(x, 21).is_some()).expect("growth produced a house");
    assert!(game.dezone(CellRect::new(occupied, 21, 1, 1)).success);
    game.money = 81_234;
    game.speed = GameSpeed::Fast;
    game.paused = true;
    game.taxes.residential = 0.08;
    game.taxes.commercial = 0.09;
    game.taxes.industrial = 0.10;
    game.highest_milestone = 100;
    game.guide_dismissed = true;
    game.set_day_progress_seconds(0.01);
    game
}

fn assert_games_equal(expected: &CityGame, actual: &CityGame) {
    assert_eq!(expected.config, actual.config);
    assert_eq!(expected.money, actual.money);
    assert_eq!(expected.week, actual.week);
    assert_eq!(expected.day, actual.day);
    assert_eq!(expected.day_progress_seconds(), actual.day_progress_seconds());
    assert_eq!(expected.rng, actual.rng);
    assert_eq!(expected.speed, actual.speed);
    assert_eq!(expected.paused, actual.paused);
    assert_eq!(expected.taxes, actual.taxes);
    assert_eq!(expected.growth_state(), actual.growth_state());
    assert_eq!(expected.last_report, actual.last_report);
    assert_eq!(expected.highest_milestone, actual.highest_milestone);
    assert_eq!(expected.guide_dismissed, actual.guide_dismissed);
    assert_eq!(expected.map.terrain_layer(), actual.map.terrain_layer());
    assert_eq!(expected.map.feature_layer(), actual.map.feature_layer());
    assert_eq!(expected.map.road_layer(), actual.map.road_layer());
    assert_eq!(expected.map.road_type_layer(), actual.map.road_type_layer());
    assert_eq!(expected.map.zone_layer(), actual.map.zone_layer());
    assert_eq!(expected.map.building_layer(), actual.map.building_layer());
    assert_eq!(expected.map.household_layer(), actual.map.household_layer());
    let mut expected_removals: Vec<_> = expected.map.zone_removals().collect();
    let mut actual_removals: Vec<_> = actual.map.zone_removals().collect();
    expected_removals.sort_unstable_by_key(|&(index, _)| index);
    actual_removals.sort_unstable_by_key(|&(index, _)| index);
    assert_eq!(expected_removals, actual_removals);
    assert_eq!(expected.stats(), actual.stats());
}

fn value_for(game: &CityGame) -> Value {
    serde_json::from_str(&SaveGameStore::serialize(game).expect("serialize")).expect("JSON value")
}

fn deserialize_value(value: &Value) -> Result<CityGame, PersistenceError> {
    SaveGameStore::deserialize(&serde_json::to_string(value).expect("JSON text"))
}

fn unpack(encoded: &str) -> Vec<u8> {
    let compressed = BASE64.decode(encoded).expect("base64");
    let mut decoder = DeflateDecoder::new(compressed.as_slice());
    let mut bytes = Vec::new();
    decoder.read_to_end(&mut bytes).expect("deflate");
    bytes
}

fn pack(bytes: &[u8]) -> String {
    let mut encoder = DeflateEncoder::new(Vec::new(), Compression::fast());
    encoder.write_all(bytes).expect("compress");
    BASE64.encode(encoder.finish().expect("finish compression"))
}

#[test]
fn whole_game_round_trip_preserves_all_state_and_is_pretty_rust_json() {
    let game = representative_game();
    let json = SaveGameStore::serialize(&game).expect("serialize");
    assert!(json.contains('\n'));
    assert!(json.contains("  \"format\": \"termcity-rust-save\""));
    assert!(!json.contains("\"Version\""));
    let value: Value = serde_json::from_str(&json).expect("valid JSON");
    assert_eq!(value["format"], RUST_SAVE_FORMAT);
    assert_eq!(value["version"], RUST_SAVE_VERSION);
    assert_eq!(value["compression"], "deflate");

    let loaded = SaveGameStore::deserialize(&json).expect("deserialize");
    assert_games_equal(&game, &loaded);
}

#[test]
fn loaded_game_continues_deterministically() {
    let mut game = representative_game();
    game.paused = false;
    let mut loaded =
        SaveGameStore::deserialize(&SaveGameStore::serialize(&game).expect("serialize"))
            .expect("deserialize");

    for _ in 0..10 {
        assert_eq!(game.advance_week(), loaded.advance_week());
    }

    assert_games_equal(&game, &loaded);
}

#[test]
fn disk_save_load_and_default_name_are_rust_specific() {
    let directory = tempdir().expect("temp directory");
    let path = directory.path().join("nested").join("city.json");
    let game = representative_game();
    SaveGameStore::save(&game, &path).expect("save");
    assert_games_equal(&game, &SaveGameStore::load(&path).expect("load"));

    let default = SaveGameStore::default_path().expect("default path");
    assert_eq!(default.file_name().and_then(|name| name.to_str()), Some(RUST_QUICKSAVE_FILE));
    assert_eq!(
        default.parent().and_then(|path| path.file_name()).and_then(|name| name.to_str()),
        Some(RUST_SAVE_DIRECTORY)
    );
}

#[test]
fn palettes_are_names_and_survive_palette_reordering() {
    let game = representative_game();
    let mut value = value_for(&game);
    let terrain = value["terrain"].as_object_mut().expect("terrain object");
    let palette = terrain["palette"].as_array_mut().expect("palette");
    assert!(palette.iter().all(Value::is_string));
    assert_eq!(palette[0], "Grass");
    assert_eq!(palette[1], "Hill");
    palette.swap(0, 1);
    let mut bytes = unpack(terrain["data"].as_str().expect("terrain data"));
    for byte in &mut bytes {
        if *byte == 0 {
            *byte = 1;
        } else if *byte == 1 {
            *byte = 0;
        }
    }
    terrain["data"] = Value::String(pack(&bytes));

    let loaded = deserialize_value(&value).expect("reordered palette loads");
    assert_eq!(loaded.map.terrain_layer(), game.map.terrain_layer());
    for layer in ["features", "buildings", "road_types"] {
        assert!(
            value[layer]["palette"]
                .as_array()
                .is_some_and(|items| { items.iter().all(Value::is_string) })
        );
    }
}

#[test]
fn every_map_layer_is_deflate_compressed_then_base64_encoded() {
    let game = representative_game();
    let value = value_for(&game);
    let count = usize::try_from(game.map.width() * game.map.height()).expect("map size");
    for (layer, expected) in
        [("terrain", count), ("features", count), ("buildings", count), ("road_types", count)]
    {
        let encoded = value[layer]["data"].as_str().expect("encoded palette layer");
        assert_eq!(unpack(encoded).len(), expected);
        assert_ne!(BASE64.decode(encoded).expect("base64").len(), expected);
    }
    for (layer, expected) in [("roads", count), ("zones", count), ("households", count * 3)] {
        let encoded = value[layer].as_str().expect("encoded layer");
        assert_eq!(unpack(encoded).len(), expected);
    }
}

#[test]
fn wrong_discriminator_and_version_are_typed_errors() {
    let game = representative_game();
    let mut value = value_for(&game);
    value["format"] = json!("csharp-save");
    assert!(matches!(
        deserialize_value(&value),
        Err(PersistenceError::WrongFormat(format)) if format == "csharp-save"
    ));

    value["format"] = json!(RUST_SAVE_FORMAT);
    value["version"] = json!(RUST_SAVE_VERSION + 1);
    assert!(matches!(
        deserialize_value(&value),
        Err(PersistenceError::UnsupportedVersion(version)) if version == RUST_SAVE_VERSION + 1
    ));
}

#[test]
fn malformed_truncated_unknown_type_and_bad_length_are_rejected() {
    assert!(matches!(
        SaveGameStore::deserialize("{ definitely not JSON"),
        Err(PersistenceError::Json(_))
    ));

    let game = representative_game();
    let mut truncated = value_for(&game);
    let roads = truncated["roads"].as_str().expect("roads");
    truncated["roads"] = json!(&roads[..roads.len() / 2]);
    assert!(deserialize_value(&truncated).is_err());

    let mut unknown = value_for(&game);
    unknown["terrain"]["palette"][0] = json!("Unobtainium");
    assert!(matches!(
        deserialize_value(&unknown),
        Err(PersistenceError::Validation(message)) if message.contains("unknown terrain type")
    ));

    let mut short = value_for(&game);
    let mut zones = unpack(short["zones"].as_str().expect("zones"));
    zones.pop();
    short["zones"] = json!(pack(&zones));
    assert!(matches!(
        deserialize_value(&short),
        Err(PersistenceError::Validation(message)) if message.contains("zones layer has length")
    ));

    let mut unknown_speed = value_for(&game);
    unknown_speed["speed"] = json!("ludicrous");
    assert!(matches!(deserialize_value(&unknown_speed), Err(PersistenceError::Json(_))));
}

#[test]
fn invalid_dimensions_values_and_pending_removals_are_rejected() {
    let game = representative_game();
    let mut dimensions = value_for(&game);
    dimensions["config"]["map_width"] = json!(7);
    assert!(matches!(deserialize_value(&dimensions), Err(PersistenceError::Validation(_))));

    let mut non_finite = value_for(&game);
    non_finite["taxes"]["residential"] = json!(2.0);
    assert!(matches!(deserialize_value(&non_finite), Err(PersistenceError::Validation(_))));

    let mut invalid_index = value_for(&game);
    invalid_index["zone_removals"][0]["index"] = json!(usize::MAX);
    assert!(matches!(
        deserialize_value(&invalid_index),
        Err(PersistenceError::Validation(message)) if message.contains("removal index")
    ));

    let mut no_building = value_for(&game);
    let index = no_building["zone_removals"][0]["index"]
        .as_u64()
        .and_then(|value| usize::try_from(value).ok())
        .expect("removal index");
    let mut building_bytes = unpack(no_building["buildings"]["data"].as_str().expect("buildings"));
    building_bytes[index] = u8::MAX;
    no_building["buildings"]["data"] = json!(pack(&building_bytes));
    assert!(matches!(
        deserialize_value(&no_building),
        Err(PersistenceError::Validation(message)) if message.contains("pending zone removal")
    ));
}

#[test]
fn caller_supplied_content_is_used_and_unknown_names_do_not_fallback() {
    let game = representative_game();
    let json = SaveGameStore::serialize(&game).expect("serialize");
    let loaded =
        SaveGameStore::deserialize_with_content(&json, GameContent::default()).expect("load");
    assert_games_equal(&game, &loaded);

    let mut value: Value = serde_json::from_str(&json).expect("JSON");
    value["features"]["palette"][0] = json!("Missing Tree Replacement");
    assert!(
        SaveGameStore::deserialize_with_content(
            &serde_json::to_string(&value).expect("JSON text"),
            GameContent::default()
        )
        .is_err()
    );
}

#[test]
fn failed_serialization_and_replace_preserve_existing_save() {
    let directory = tempdir().expect("temp directory");
    let path = directory.path().join("city.json");
    fs::write(&path, "prior save").expect("write prior save");
    let mut invalid = representative_game();
    invalid.taxes.residential = f64::NAN;
    assert!(SaveGameStore::save(&invalid, &path).is_err());
    assert_eq!(fs::read_to_string(&path).expect("read prior save"), "prior save");

    let destination_directory = directory.path().join("cannot-replace-directory");
    fs::create_dir(&destination_directory).expect("destination directory");
    let marker = destination_directory.join("marker");
    fs::write(&marker, "preserved").expect("marker");
    assert!(SaveGameStore::save(&representative_game(), &destination_directory).is_err());
    assert_eq!(fs::read_to_string(marker).expect("read marker"), "preserved");
}

#[test]
fn household_layer_round_trips_exact_bytes() {
    let mut game = representative_game();
    let house_index = game
        .map
        .building_layer()
        .iter()
        .enumerate()
        .find(|(index, building)| {
            **building != 0 && game.map.zone_layer()[*index] == ZoneType::Residential
        })
        .map(|(index, _)| index)
        .expect("residential building");
    let position = game.map.position_of(house_index);
    game.map.set_household(position.x, position.y, Household::new(2, 3, 1));
    let loaded = SaveGameStore::deserialize(&SaveGameStore::serialize(&game).expect("serialize"))
        .expect("deserialize");
    assert_eq!(loaded.map.household_at(position.x, position.y), Household::new(2, 3, 1));
}
