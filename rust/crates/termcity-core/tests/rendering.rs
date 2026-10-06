use termcity_core::{
    BlockSampler, CellInspector, CellRect, CellRenderer, CityGame, GameConfig, GameContent,
    GameMap, Household, MapRenderer, Pos, RegisteredType, Rgb, ZoneType,
};

fn flat_game() -> CityGame {
    let config = GameConfig { map_width: 16, map_height: 12, ..GameConfig::default() };
    let map = GameMap::new(config.map_width, config.map_height, GameContent::default())
        .expect("valid map");
    CityGame::from_map(config, map)
}

fn connect_road(game: &mut CityGame, y: i32, end_x: i32) {
    for x in 0..=end_x {
        game.map.set_has_road(x, y, true);
    }
    game.touch();
}

#[test]
fn renderer_composes_terrain_features_zones_buildings_and_roads_in_order() {
    let mut game = flat_game();
    let grass = game.map.content().base_terrain().clone();
    let grass_visual = CellRenderer::render(&game, 5, 5);
    assert_eq!(grass_visual.glyph, grass.glyph_at(5, 5));
    assert_eq!(grass_visual.foreground, grass.foreground);
    assert_eq!(grass_visual.background, grass.background);

    let tree = game.map.content().features.find("Tree").expect("tree").clone();
    game.map.set_feature(5, 5, Some(tree.id()));
    let feature_visual = CellRenderer::render(&game, 5, 5);
    assert_eq!(feature_visual.glyph, tree.glyph_at(5, 5));
    assert_eq!(feature_visual.foreground, tree.foreground);
    assert_eq!(feature_visual.background, grass.background);

    game.map.set_zone(5, 5, ZoneType::Residential);
    let unserved_zone = CellRenderer::render(&game, 5, 5);
    let residential = ZoneType::Residential.info().expect("residential");
    assert_eq!(unserved_zone.glyph, residential.empty_glyph);
    assert_eq!(unserved_zone.foreground, residential.foreground.scale(0.5));
    assert_eq!(unserved_zone.background, residential.background);

    connect_road(&mut game, 7, 5);
    let served_zone = CellRenderer::render(&game, 5, 5);
    assert_eq!(served_zone.foreground, residential.foreground);

    let house = game.map.content().building_for_zone(ZoneType::Residential).expect("house").clone();
    game.map.set_building(5, 5, Some(house.id()));
    let zoned_building = CellRenderer::render(&game, 5, 5);
    assert_eq!(zoned_building.glyph, house.glyph_at(5, 5));
    assert_eq!(zoned_building.foreground, house.foreground);
    assert_eq!(zoned_building.background, residential.background);

    game.map.set_zone(5, 5, ZoneType::None);
    let standalone = CellRenderer::render(&game, 5, 5);
    assert_eq!(standalone.background, Rgb::hex(0x2b_2b_30));

    game.map.set_has_road(5, 5, true);
    let road = game.map.content().default_road().clone();
    let road_visual = CellRenderer::render(&game, 5, 5);
    assert_eq!(road_visual.glyph, road.glyph_for(game.map.road_mask(5, 5)));
    assert_eq!(road_visual.foreground, Rgb::hex(0xe8_a3_3d));
    assert_eq!(road_visual.background, road.background);
}

#[test]
fn renderer_uses_junction_glyphs_connectivity_colors_and_water_for_bridges() {
    let mut game = flat_game();
    let highway = game.map.content().roads.find("Highway").expect("highway").clone();
    for (x, y) in [(6, 5), (5, 5), (7, 5), (6, 4), (6, 6)] {
        game.map.set_road(x, y, highway.id());
    }
    assert_eq!(CellRenderer::road_mask(&game.map, 6, 5), 15);
    assert_eq!(CellRenderer::render(&game, 6, 5).glyph, "╬");
    assert_eq!(CellRenderer::render(&game, 5, 5).glyph, "═");
    assert_eq!(CellRenderer::render(&game, 6, 4).glyph, "║");
    assert_eq!(CellRenderer::render(&game, 6, 5).foreground, Rgb::hex(0xe8_a3_3d));

    for x in 0..=4 {
        game.map.set_road(x, 5, highway.id());
    }
    assert_eq!(CellRenderer::render(&game, 5, 5).foreground, highway.foreground);

    let water = game.map.content().terrains.find("Water").expect("water").clone();
    game.map.set_terrain(3, 5, water.id());
    let bridge = CellRenderer::render(&game, 3, 5);
    assert_eq!(bridge.glyph, "═");
    assert_eq!(bridge.background, water.background);
}

#[test]
fn inspector_reports_roads_vacancies_pause_residents_and_zone_removal() {
    let mut game = flat_game();
    let highway = game.map.content().roads.find("Highway").expect("highway").clone();
    game.map.set_road(8, 3, highway.id());
    assert_eq!(
        CellInspector::summary(&game, Pos::new(8, 3)),
        "(8,3) Grass · Highway (NOT connected)"
    );

    game.map.set_zone(6, 6, ZoneType::Residential);
    game.paused = true;
    let vacant = CellInspector::describe(&game, Pos::new(6, 6));
    assert!(vacant.contains(&"Residential zone (no road access)".to_owned()));
    assert!(vacant.iter().any(|line| line.starts_with("Vacant: Connect a road")));
    assert!(vacant.contains(&"Paused - press P to resume".to_owned()));

    connect_road(&mut game, 7, 6);
    let house = game.map.content().building_for_zone(ZoneType::Residential).expect("house").clone();
    game.map.set_building(6, 6, Some(house.id()));
    game.map.set_household(6, 6, Household::new(2, 1, 1));
    let occupied = CellInspector::describe(&game, Pos::new(6, 6));
    assert!(occupied.contains(&"House".to_owned()));
    assert!(occupied.contains(&"4 residents: 2A 1C 1S".to_owned()));

    assert!(game.dezone(CellRect::single(Pos::new(6, 6))).success);
    let unzoned = CellInspector::summary(&game, Pos::new(6, 6));
    assert!(unzoned.contains("Unzoned: removal in"));
    assert!(unzoned.contains("restore Residential to keep it"));
    assert!(unzoned.contains("4 residents: 2A 1C 1S"));
    assert_eq!(CellInspector::describe(&game, Pos::new(-1, 0)), Vec::<String>::new());
}

#[test]
fn block_sampler_preserves_priority_and_first_cell_ties_in_two_by_two_blocks() {
    let mut game = flat_game();
    let water = game.map.content().terrains.find("Water").expect("water").clone();
    let highway = game.map.content().roads.find("Highway").expect("highway").clone();
    let shop = game.map.content().building_for_zone(ZoneType::Commercial).expect("shop").clone();

    game.map.set_terrain(3, 3, water.id());
    assert_eq!(BlockSampler::new(&game).sample(2, 2, 2), CellRenderer::render(&game, 3, 3));

    game.map.set_road(2, 3, highway.id());
    assert_eq!(BlockSampler::new(&game).sample(2, 2, 2), CellRenderer::render(&game, 2, 3));

    game.map.set_zone(3, 2, ZoneType::Commercial);
    assert_eq!(BlockSampler::new(&game).sample(2, 2, 2), CellRenderer::render(&game, 3, 2));

    game.map.set_building(2, 2, Some(shop.id()));
    game.map.set_building(3, 2, Some(shop.id()));
    assert_eq!(BlockSampler::new(&game).sample(2, 2, 2), CellRenderer::render(&game, 2, 2));
}

#[test]
fn block_sampler_handles_four_by_four_road_ranks_and_clips_at_edges() {
    let mut game = flat_game();
    let street = game.map.content().roads.find("Street").expect("street").clone();
    let highway = game.map.content().roads.find("Highway").expect("highway").clone();
    game.map.set_road(5, 5, street.id());
    game.map.set_road(7, 7, highway.id());

    assert_eq!(BlockSampler::new(&game).sample(4, 4, 4), CellRenderer::render(&game, 7, 7));
    let edge = BlockSampler::new(&game).sample(15, 11, 4);
    assert_ne!(edge.glyph, "");
}

#[test]
fn block_sampler_covers_non_square_minimap_buckets() {
    let mut game = flat_game();
    let highway = game.map.content().roads.find("Highway").expect("highway").clone();
    game.map.set_road(2, 9, highway.id());

    assert_eq!(BlockSampler::new(&game).sample_rect(2, 4, 2, 6), CellRenderer::render(&game, 2, 9));
}

#[test]
fn map_renderer_reuses_one_snapshot_for_cells_samples_and_stats() {
    let mut game = flat_game();
    connect_road(&mut game, 7, 6);
    game.map.set_zone(6, 6, ZoneType::Residential);

    let renderer = MapRenderer::new(&game);
    let sampler = BlockSampler::new(&game);

    assert_eq!(renderer.render(6, 6), CellRenderer::render(&game, 6, 6));
    assert_eq!(renderer.sample_rect(4, 4, 4, 4), sampler.sample_rect(4, 4, 4, 4));
    assert_eq!(renderer.stats(), game.stats());
}
