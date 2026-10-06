use termcity_core::{
    GameContent, GameMap, MapGenerator, ROAD_NEIGHBOURS, RegisteredType, RoadNetwork,
};

fn generated(seed: i32, width: i32, height: i32) -> GameMap {
    MapGenerator::generate(width, height, seed, GameContent::default()).expect("valid map")
}

fn road_name(map: &GameMap, index: usize) -> &str {
    let position = map.position_of(index);
    &map.road_type_at(position.x, position.y).expect("road has a type").name
}

#[test]
fn generated_network_is_highway_led_with_street_stubs() {
    for seed in 1..=6 {
        let map = generated(seed, 160, 96);
        let highways =
            map.road_cells().filter(|&index| road_name(&map, index) == "Highway").count();
        let streets = map.road_cells().filter(|&index| road_name(&map, index) == "Street").count();

        assert!(highways > 80, "seed {seed}: only {highways} highway cells");
        assert!(highways > streets, "seed {seed}: {highways} highways, {streets} streets");
        assert!(streets > 0, "seed {seed}: no street stub");
        assert!(map.road_count() < map.road_layer().len() / 20);
    }
}

#[test]
fn every_generated_road_reaches_an_edge_gateway() {
    for (seed, width, height) in [(1, 160, 96), (7, 160, 96), (3, 320, 192), (4, 80, 24)] {
        let map = generated(seed, width, height);
        let network = RoadNetwork::compute(&map, 2);
        let gateways = map
            .road_cells()
            .filter(|&index| {
                let position = map.position_of(index);
                map.is_edge(position.x, position.y)
            })
            .count();

        assert!(gateways >= 2, "seed {seed} {width}x{height}: {gateways} gateways");
        assert_eq!(network.connected_road_count(), map.road_count());
    }
}

#[test]
fn generated_roads_have_no_blobs_diagonal_near_misses_or_dirty_junctions() {
    let mut junctions_seen = 0;
    for seed in 1..=10 {
        let map = generated(seed, 160, 96);
        let mut junctions = Vec::new();
        for index in map.road_cells() {
            let position = map.position_of(index);
            let (x, y) = (position.x, position.y);
            assert!(
                !(map.has_road(x + 1, y) && map.has_road(x, y + 1) && map.has_road(x + 1, y + 1)),
                "seed {seed}: 2x2 road block at {x},{y}"
            );
            if map.has_road(x + 1, y + 1) {
                assert!(
                    map.has_road(x + 1, y) || map.has_road(x, y + 1),
                    "seed {seed}: diagonal near miss at {x},{y}"
                );
            }
            if map.has_road(x - 1, y + 1) {
                assert!(
                    map.has_road(x - 1, y) || map.has_road(x, y + 1),
                    "seed {seed}: diagonal near miss at {x},{y}"
                );
            }

            let neighbours =
                ROAD_NEIGHBOURS.iter().filter(|&&(dx, dy)| map.has_road(x + dx, y + dy)).count();
            if neighbours >= 3 {
                assert!(matches!(map.road_mask(x, y), 7 | 11 | 13 | 14 | 15));
                junctions.push(position);
            }
        }
        for (index, a) in junctions.iter().enumerate() {
            for b in &junctions[index + 1..] {
                assert!(
                    (a.x - b.x).abs().max((a.y - b.y).abs()) > 3,
                    "seed {seed}: junctions {a:?} and {b:?} are too close"
                );
            }
        }
        junctions_seen += junctions.len();
    }
    assert!(junctions_seen > 10, "only {junctions_seen} junctions");
}

#[test]
fn bridges_are_bounded_straight_runs() {
    let mut bridge_cells = 0;
    for seed in 1..=12 {
        let map = generated(seed, 160, 96);
        for index in map.road_cells() {
            let position = map.position_of(index);
            if !map.terrain_at(position.x, position.y).buildable {
                bridge_cells += 1;
                assert!(
                    matches!(map.road_mask(position.x, position.y), 1 | 2 | 4 | 5 | 8 | 10),
                    "seed {seed}: bridge bends at {position:?}"
                );
            }
        }
    }
    assert!(bridge_cells > 0, "no generated highway crossed water");
}

#[test]
fn road_masks_connectivity_and_service_reach_match_cardinal_roads() {
    let mut map = GameMap::new(12, 10, GameContent::default()).expect("valid map");
    for x in 0..=2 {
        map.set_has_road(x, 5, true);
    }
    map.set_has_road(8, 5, true);
    map.set_has_road(6, 4, true);
    map.set_has_road(6, 5, true);
    map.set_has_road(6, 6, true);
    map.set_has_road(5, 5, true);
    map.set_has_road(7, 5, true);
    assert_eq!(map.road_mask(6, 5), 15);
    assert_eq!(map.road_mask(0, 5), 2);

    let water = map.content().terrains.find("Water").expect("water exists").id();
    map.set_terrain(2, 4, water);
    let network = RoadNetwork::compute(&map, 2);
    assert_eq!(network.connected_road_count(), 3);
    assert!(network.is_connected(&map, 2, 5));
    assert!(!network.is_connected(&map, 6, 5));
    assert!(network.is_served(&map, 4, 5));
    assert!(!network.is_served(&map, 5, 5));
    assert!(!network.is_served(&map, 2, 4));
    assert!(!network.is_index_served(map.index(8, 5)));
    assert!(!network.is_connected(&map, -1, 5));
    assert!(!network.is_served(&map, 12, 5));
}

#[test]
fn full_generation_keeps_features_off_roads_and_scales_to_supported_extremes() {
    let minimum = generated(9, 8, 8);
    assert!(minimum.road_count() > 0);
    assert_eq!(RoadNetwork::compute(&minimum, 2).connected_road_count(), minimum.road_count());

    let large = generated(3, 640, 384);
    let small = generated(3, 160, 96);
    assert!(large.road_count() > small.road_count() * 8);
    assert_eq!(RoadNetwork::compute(&large, 2).connected_road_count(), large.road_count());
    for index in large.road_cells() {
        let position = large.position_of(index);
        assert!(large.feature_at(position.x, position.y).is_none());
    }
}
