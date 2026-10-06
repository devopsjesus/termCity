use std::collections::HashSet;

use termcity_core::{
    GameContent, GameMap, MapGenerator, RegisteredType, WaterGenerator, WaterKind, WaterMapType,
    WaterPlan,
};

fn generated(seed: i32, width: i32, height: i32) -> GameMap {
    MapGenerator::generate(width, height, seed, GameContent::default()).expect("valid map")
}

fn count_id(layer: &[u8], wanted: u8) -> usize {
    layer.iter().fold(0, |count, &id| count + usize::from(id == wanted))
}

#[test]
fn generated_layers_are_deterministic_and_seeded() {
    let first = generated(42, 160, 96);
    let repeat = generated(42, 160, 96);
    let different = generated(43, 160, 96);

    assert_eq!(first.terrain_layer(), repeat.terrain_layer());
    assert_eq!(first.feature_layer(), repeat.feature_layer());
    assert_eq!(first.road_layer(), repeat.road_layer());
    assert_eq!(first.road_type_layer(), repeat.road_type_layer());
    assert_ne!(first.terrain_layer(), different.terrain_layer());
    assert_ne!(first.feature_layer(), different.feature_layer());
}

#[test]
fn default_map_contains_expected_terrain_and_feature_ranges() {
    for seed in [1, 7, 1_234] {
        let map = generated(seed, 160, 96);
        let water_id = map.content().terrains.find("Water").expect("water exists").id();
        let hill_id = map.content().terrains.find("Hill").expect("hill exists").id();
        let tree_id = map.content().features.find("Tree").expect("tree exists").id();
        let rock_id = map.content().features.find("Rock").expect("rock exists").id();

        let water = count_id(map.terrain_layer(), water_id);
        let hills = count_id(map.terrain_layer(), hill_id);
        let trees = count_id(map.feature_layer(), tree_id);
        let rocks = count_id(map.feature_layer(), rock_id);

        assert!((50..=map.terrain_layer().len() * 45 / 100).contains(&water));
        assert!((300..=map.terrain_layer().len() / 2).contains(&hills));
        assert!(trees > 100, "seed {seed}: trees={trees}");
        assert!(rocks > 30, "seed {seed}: rocks={rocks}");
    }
}

#[test]
fn features_only_occupy_eligible_unoccupied_cells() {
    let mut map =
        GameMap::new(160, 96, GameContent::default()).expect("default dimensions are valid");
    MapGenerator::generate_terrain(&mut map, 5);
    for x in 0..map.width() {
        map.set_has_road(x, map.height() / 2, true);
    }
    MapGenerator::generate_features(&mut map, 5);

    for y in 0..map.height() {
        for x in 0..map.width() {
            if map.feature_at(x, y).is_some() {
                assert!(map.terrain_at(x, y).allows_features);
                assert!(!map.has_road(x, y));
            }
        }
    }
}

#[test]
#[allow(clippy::too_many_lines)]
fn water_plan_preserves_body_and_river_invariants() {
    let content = GameContent::default();
    let water_id = content.terrains.find("Water").expect("water exists").id();
    let mut observed_types = HashSet::new();
    let mut observed_sea = false;
    let mut observed_river = false;

    for seed in 1..=80 {
        let mut map = GameMap::new(160, 96, content.clone()).expect("valid map");
        let plan = WaterGenerator::build(&mut map, water_id, seed, None);
        observed_types.insert(plan.map_type);

        for body in &plan.bodies {
            assert!(body.id < WaterPlan::FIRST_RIVER_ID);
            if body.kind == WaterKind::Sea {
                observed_sea = true;
                let edge = if matches!(
                    body.side.expect("sea has a side"),
                    termcity_core::MapSide::North | termcity_core::MapSide::South
                ) {
                    map.width()
                } else {
                    map.height()
                };
                assert!(
                    f64::from(body.span)
                        <= f64::from(edge) * WaterGenerator::MAX_SEA_SHARE_OF_EDGE + 1.0
                );
                assert!((5..=WaterGenerator::MAX_EDGE_DEPTH).contains(&body.depth));
            }
        }

        let targets: HashSet<_> =
            plan.rivers.iter().map(|river| (river.id, river.target_body_id)).collect();
        for river in &plan.rivers {
            observed_river = true;
            assert!(plan.bodies.iter().any(|body| body.id == river.target_body_id));
            assert!(river.sources == 1 || river.sources == 2);
            if river.sources == 2 {
                assert_eq!(plan.map_type, WaterMapType::RiverConfluence);
            }
        }

        let mut water_cells = 0;
        for y in 0..map.height() {
            for x in 0..map.width() {
                let id = plan.id_at(x, y);
                assert_eq!(id != 0, map.terrain_at(x, y).id() == water_id);
                if id == 0 {
                    continue;
                }
                water_cells += 1;
                if plan.bodies.iter().any(|body| {
                    body.id == id
                        && matches!(
                            body.kind,
                            WaterKind::Sea | WaterKind::EdgeLake | WaterKind::CornerLake
                        )
                }) {
                    let edge_distance = x.min(map.width() - 1 - x).min(y.min(map.height() - 1 - y));
                    assert!(edge_distance <= WaterGenerator::MAX_EDGE_DEPTH);
                }
                for (dx, dy) in [(1, 0), (0, 1)] {
                    let (next_x, next_y) = (x + dx, y + dy);
                    if !map.in_bounds(next_x, next_y) {
                        continue;
                    }
                    let other = plan.id_at(next_x, next_y);
                    if other == 0 || other == id {
                        continue;
                    }
                    assert!(
                        targets.contains(&(id, other)) || targets.contains(&(other, id)),
                        "seed {seed}: water {id} touches {other} at {x},{y}"
                    );
                }
            }
        }
        let water_share =
            f64::from(water_cells) / (f64::from(map.width()) * f64::from(map.height()));
        assert!((0.005..=0.30).contains(&water_share), "seed {seed}: water share {water_share}");

        for river in &plan.rivers {
            let mut reaches_edge = false;
            let mut reaches_body = false;
            for y in 0..map.height() {
                for x in 0..map.width() {
                    if plan.id_at(x, y) != river.id {
                        continue;
                    }
                    reaches_edge |= map.is_edge(x, y);
                    reaches_body |=
                        [(1, 0), (-1, 0), (0, 1), (0, -1)].into_iter().any(|(dx, dy)| {
                            map.in_bounds(x + dx, y + dy)
                                && plan.id_at(x + dx, y + dy) == river.target_body_id
                        });
                }
            }
            assert!(reaches_edge, "seed {seed}: river {} misses the edge", river.id);
            assert!(
                reaches_body,
                "seed {seed}: river {} misses body {}",
                river.id, river.target_body_id
            );
        }
    }

    assert_eq!(observed_types.len(), 5);
    assert!(observed_sea);
    assert!(observed_river);
}

#[test]
fn minimum_and_large_maps_generate_safely() {
    let minimum = generated(9, 8, 8);
    assert_eq!(minimum.terrain_layer().len(), 64);
    assert!(minimum.feature_layer().iter().enumerate().all(|(index, &feature)| {
        let position = minimum.position_of(index);
        feature == 0 || minimum.terrain_at(position.x, position.y).allows_features
    }));

    let large = generated(9, 640, 384);
    let water_id = large.content().terrains.find("Water").expect("water exists").id();
    let water = count_id(large.terrain_layer(), water_id);
    assert!(water > large.terrain_layer().len() / 200);
}
