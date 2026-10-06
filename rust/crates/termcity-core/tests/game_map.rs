use termcity_core::{
    GameContent, GameMap, Household, MapError, RegisteredType, ZoneRemoval, ZoneType,
};

#[test]
fn map_validates_size_and_starts_with_empty_layers() {
    assert_eq!(GameMap::new(7, 8, GameContent::default()).unwrap_err(), MapError::TooSmall);

    let map = GameMap::new(12, 10, GameContent::default()).expect("valid map");
    assert_eq!((map.width(), map.height()), (12, 10));
    assert!(map.terrain_layer().iter().all(|&id| id == map.base_terrain_id()));
    assert!(map.feature_layer().iter().all(|&id| id == 0));
    assert!(map.road_layer().iter().all(|&road| !road));
    assert!(map.zone_layer().iter().all(|&zone| zone == ZoneType::None));
    assert_eq!(map.road_count(), 0);
}

#[test]
fn road_and_zone_sparse_indexes_follow_layer_changes() {
    let mut map = GameMap::new(12, 10, GameContent::default()).expect("valid map");
    let avenue = map.content().roads.find("Avenue").expect("avenue exists").id();

    map.set_has_road(2, 3, true);
    map.set_road(3, 3, avenue);
    map.set_zone(4, 4, ZoneType::Residential);
    map.set_zone(5, 4, ZoneType::Residential);

    assert_eq!(map.road_count(), 2);
    assert_eq!(map.road_type_at(2, 3).expect("street exists").name, "Street");
    assert_eq!(map.road_type_at(3, 3).expect("avenue exists").name, "Avenue");
    assert_eq!(map.zone_cells(ZoneType::Residential).count(), 2);

    map.set_has_road(2, 3, false);
    map.set_zone(4, 4, ZoneType::None);
    assert_eq!(map.road_count(), 1);
    assert_eq!(map.zone_cells(ZoneType::Residential).count(), 1);
}

#[test]
fn layers_and_cell_cleanup_match_csharp_behavior() {
    let mut map = GameMap::new(12, 10, GameContent::default()).expect("valid map");
    let tree = map.content().features.find("Tree").expect("tree exists").id();
    let house = map.content().buildings.find("House").expect("house exists").id();

    map.set_feature(6, 6, Some(tree));
    map.set_has_road(6, 6, true);
    map.set_zone(6, 6, ZoneType::Residential);
    map.set_building(6, 6, Some(house));
    map.set_household(6, 6, Household::new(2, 2, 1));
    map.set_zone_removal(6, 6, ZoneRemoval { zone: ZoneType::Residential, remove_at_day: 5.0 });

    assert!(map.is_filled(6, 6));
    assert_eq!(map.household_at(6, 6).total(), 5);
    assert!(map.zone_removal_at(6, 6).is_some());

    map.clear_cell(6, 6);
    assert!(!map.has_road(6, 6));
    assert_eq!(map.zone_at(6, 6), ZoneType::None);
    assert!(map.feature_at(6, 6).is_none());
    assert!(map.building_at(6, 6).is_none());
    assert!(map.household_at(6, 6).is_empty());
    assert!(map.zone_removal_at(6, 6).is_none());
}

trait BaseTerrainId {
    fn base_terrain_id(&self) -> u8;
}

impl BaseTerrainId for GameMap {
    fn base_terrain_id(&self) -> u8 {
        self.content().base_terrain().id()
    }
}
