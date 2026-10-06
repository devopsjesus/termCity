use termcity_core::{
    BuildingType, CellRect, CityGame, GameConfig, GameContent, GameMap, GameSpeed,
    GrowthDiagnostic, GrowthStatus, Household, RegisteredType, Rgb, ZoneCount, ZoneType,
    format_money,
};

fn flat_game_with(config: GameConfig, mut content: GameContent) -> CityGame {
    let grass = content.base_terrain().id();
    let mut map = GameMap::new(config.map_width, config.map_height, std::mem::take(&mut content))
        .expect("valid map");
    for y in 0..map.height() {
        for x in 0..map.width() {
            map.set_terrain(x, y, grass);
        }
    }
    for x in 0..map.width() {
        map.set_has_road(x, 20, true);
    }
    let mut game = CityGame::from_map(config, map);
    game.touch();
    game
}

fn flat_game() -> CityGame {
    flat_game_with(GameConfig::default(), GameContent::default())
}

fn zone_row(game: &mut CityGame, y: i32, x: i32, count: i32, zone: ZoneType) {
    assert!(game.designate(CellRect::new(x, y, count, 1), zone).success);
}

fn fill_homes(game: &mut CityGame, count: i32, people: u8) {
    let house = game.map.content().building_for_zone(ZoneType::Residential).expect("house").id();
    for x in 0..count {
        game.map.set_zone(x, 21, ZoneType::Residential);
        game.map.set_building(x, 21, Some(house));
        game.map.set_household(x, 21, Household::new(people, 0, 0));
    }
    game.touch();
}

#[test]
fn economy_starts_at_fifty_thousand_and_roads_are_atomic() {
    let mut game = flat_game();
    assert_eq!(game.money, 50_000);

    let too_expensive = game.build_road(CellRect::new(0, 0, 101, 1), None);
    assert!(!too_expensive.success);
    assert_eq!(game.money, 50_000);
    assert!(!game.map.has_road(0, 0));

    let built = game.build_road(CellRect::new(10, 18, 1, 2), None);
    assert!(built.success, "{}", built.message);
    assert_eq!((built.cells, built.cost, game.money), (2, 1_000, 49_000));
    assert!(game.map.has_road(10, 18));
    assert!(game.map.has_road(10, 19));
}

#[test]
fn road_quotes_include_terrain_cost_and_upgrade_differences() {
    let mut game = flat_game();
    let hill = game.map.content().terrains.find("Hill").expect("hill").id();
    game.map.set_terrain(5, 5, hill);
    assert_eq!(game.road_cost_at(5, 5, None), 750);

    let highway = game.map.content().roads.find("Highway").expect("highway").clone();
    let quote = game.quote_road(CellRect::new(10, 20, 4, 1), Some(&highway));
    assert_eq!((quote.cells, quote.cost), (4, 4_000));
    assert!(game.build_road(CellRect::new(10, 20, 4, 1), Some(&highway)).success);
    assert_eq!(game.map.road_type_at(11, 20).expect("road").name, "Highway");
    assert_eq!(game.quote_road(CellRect::new(10, 20, 4, 1), Some(&highway)).cells, 0);
}

#[test]
fn player_buildings_quote_spend_and_clear_features() {
    let mut content = GameContent::default();
    content
        .buildings
        .register(BuildingType::new(
            "Civic Hall",
            vec!["H"],
            Rgb::new(255, 255, 255),
            ZoneType::None,
            1_250,
            true,
            "A civic building",
        ))
        .expect("register civic building");
    let mut game = flat_game_with(GameConfig::default(), content);
    let tree = game.map.content().features.find("Tree").expect("tree").id();
    game.map.set_feature(5, 5, Some(tree));
    let hall = game.map.content().buildings.find("Civic Hall").expect("hall").clone();

    let quote = game.quote_building(&hall, CellRect::new(5, 5, 2, 1));
    assert_eq!((quote.cells, quote.cost), (2, 2_500));
    let result = game.place_building(&hall, CellRect::new(5, 5, 2, 1));
    assert!(result.success);
    assert_eq!(game.money, 47_500);
    assert!(game.map.feature_at(5, 5).is_none());
    assert_eq!(game.map.building_at(6, 5).expect("hall").name, "Civic Hall");
}

#[test]
fn taxes_use_zone_values_rates_and_away_from_zero_rounding() {
    let mut game = flat_game();
    zone_row(&mut game, 21, 0, 10, ZoneType::Residential);
    let report = game.advance_week();
    assert_eq!(game.stats().residential.filled, 3);
    assert_eq!(report.income, 30);
    assert_eq!(game.money, 50_030);

    game.set_tax_rate(ZoneType::Residential, 0.10);
    assert_eq!(game.stats().weekly_income, 60);

    let shop = game.map.content().building_for_zone(ZoneType::Commercial).expect("shop").id();
    game.map.set_zone(30, 19, ZoneType::Commercial);
    game.map.set_building(30, 19, Some(shop));
    game.touch();
    assert_eq!(game.stats().weekly_income, 78);
}

#[test]
fn growth_requires_connected_roads_and_residential_capacity() {
    let mut game = flat_game();
    for x in 0..game.map.width() {
        game.map.set_has_road(x, 20, (40..60).contains(&x));
    }
    game.touch();
    zone_row(&mut game, 21, 40, 20, ZoneType::Residential);
    for _ in 0..5 {
        game.advance_week();
    }
    assert_eq!(game.stats().residential.filled, 0);

    assert!(game.build_road(CellRect::new(0, 20, 40, 1), None).success);
    game.advance_week();
    assert!(game.stats().residential.filled > 0);

    zone_row(&mut game, 19, 0, 5, ZoneType::Commercial);
    zone_row(&mut game, 19, 10, 5, ZoneType::Industrial);
    while game.stats().residential.filled < 10 {
        game.advance_week();
    }
    for _ in 0..3 {
        game.advance_week();
    }
    assert_eq!(game.stats().commercial.filled, game.supported_cells(ZoneType::Commercial).min(5));
    assert_eq!(game.stats().industrial.filled, game.supported_cells(ZoneType::Industrial).min(5));
}

#[test]
fn weekly_growth_caps_compound_and_daily_shares_sum_exactly() {
    let game = flat_game();
    assert_eq!(game.weekly_cap(3, 0), 3);
    assert_eq!(game.weekly_cap(3, 50), 4);
    assert_eq!(game.weekly_cap(3, 100), 5);
    assert_eq!(game.weekly_cap(3, 1_000), 23);
    assert_eq!(game.weekly_cap(1, 5_000), 101);
    assert_eq!(game.weekly_cap(3, -5), 3);

    for cap in [0, 1, 3, 6, 7, 8, 13, 100, 1_599] {
        assert_eq!((0..7).map(|day| game.daily_share(cap, day)).sum::<i32>(), cap);
    }
}

#[test]
fn household_population_demand_and_seeded_continuation_are_deterministic() {
    let mut game = flat_game();
    assert_eq!(game.demand().residential, 1.0);
    zone_row(&mut game, 21, 0, 40, ZoneType::Residential);
    for _ in 0..10 {
        game.advance_week();
    }
    let stats = game.stats();
    assert!(stats.population > stats.households);
    assert!(game.demand().commercial > 0.5);
    assert!(game.demand().industrial > 0.5);

    let mut continued = game.clone();
    for _ in 0..5 {
        assert_eq!(game.advance_week(), continued.advance_week());
    }
    assert_eq!(game.stats(), continued.stats());
    assert_eq!(game.rng.state(), continued.rng.state());
    assert_eq!(game.map.building_layer(), continued.map.building_layer());
    assert_eq!(game.map.household_layer(), continued.map.household_layer());
}

#[test]
fn clock_obeys_speed_pause_day_boundaries_and_update_cap() {
    let mut game = flat_game();
    let fast_week = game.config.seconds_per_week(GameSpeed::Fast);
    game.speed = GameSpeed::Fast;
    for _ in 0..13 {
        game.update(0.1);
    }
    assert_eq!(game.week, 0);
    game.update(fast_week - 1.3 + 0.11);
    assert_eq!(game.week, 1);

    game.paused = true;
    let before = game.elapsed_days();
    game.update(30.0);
    assert_eq!(game.elapsed_days(), before);

    game.paused = false;
    game.speed = GameSpeed::Slow;
    game.update(60.0);
    assert_eq!(game.week, 1);
    assert!(game.elapsed_days() - before <= 1.0);
}

#[test]
fn clock_calendar_and_reports_roll_over_at_week_end() {
    let mut game = flat_game();
    zone_row(&mut game, 21, 0, 60, ZoneType::Residential);
    for _ in 0..6 {
        game.advance_day();
        assert_eq!(game.money, 50_000);
    }
    game.advance_day();
    assert_eq!((game.day, game.week), (0, 1));
    assert_eq!(game.last_report.expect("report").new_households, 3);
    for _ in 1..52 {
        game.advance_week();
    }
    assert_eq!((game.year(), game.week_of_year()), (2, 1));
}

#[test]
fn dezone_keeps_occupants_and_income_until_the_delayed_removal() {
    let mut game = flat_game();
    let house = game.map.content().building_for_zone(ZoneType::Residential).expect("house").id();
    game.map.set_zone(10, 18, ZoneType::Residential);
    game.map.set_building(10, 18, Some(house));
    game.map.set_household(10, 18, Household::new(2, 2, 1));
    game.touch();

    assert!(game.dezone(CellRect::new(10, 18, 1, 1)).success);
    let removal = game.map.zone_removal_at(10, 18).expect("scheduled");
    assert!((14.0..=21.0).contains(&removal.remove_at_day));
    assert_eq!(
        game.stats().residential,
        ZoneCount { zoned: 0, filled: 0, served: 0, awaiting_removal: 1 }
    );
    assert_eq!((game.stats().population, game.stats().weekly_income), (5, 10));

    while game.elapsed_days() < removal.remove_at_day {
        game.advance_day();
    }
    assert!(game.map.building_at(10, 18).is_none());
    assert!(game.map.household_at(10, 18).is_empty());
    assert_eq!(game.stats().population, 0);
}

#[test]
fn restoring_original_zone_cancels_removal_but_other_zones_are_blocked() {
    let mut game = flat_game();
    fill_homes(&mut game, 1, 5);
    let area = CellRect::new(0, 21, 1, 1);
    assert!(game.dezone(area).success);
    assert!(!game.designate(area, ZoneType::Industrial).success);
    assert!(game.map.zone_removal_at(0, 21).is_some());
    assert!(game.designate(area, ZoneType::Residential).success);
    assert!(game.map.zone_removal_at(0, 21).is_none());
    for _ in 0..4 {
        game.advance_week();
    }
    assert!(game.map.building_at(0, 21).is_some());
}

#[test]
fn zoning_demolition_and_failed_actions_preserve_expected_state() {
    let mut game = flat_game();
    let money = game.money;
    let designated = game.designate(CellRect::new(5, 5, 3, 3), ZoneType::Industrial);
    assert_eq!((designated.success, designated.cells, game.money), (true, 9, money));
    assert_eq!(game.stats().industrial.zoned, 9);
    assert!(game.demolish(CellRect::new(5, 5, 3, 3)).success);
    assert_eq!(game.stats().industrial.zoned, 0);
    assert!(!game.demolish(CellRect::new(5, 5, 3, 3)).success);
    assert_eq!(game.money, money);
}

#[test]
fn milestones_are_inclusive_and_reports_record_actual_growth() {
    let mut game = flat_game();
    fill_homes(&mut game, 20, 5);
    assert_eq!(game.highest_milestone, 100);
    assert_eq!(game.next_milestone(), Some(500));

    zone_row(&mut game, 21, 30, 20, ZoneType::Residential);
    let report = game.advance_week();
    assert_eq!(report.week, 1);
    assert_eq!(game.last_report, Some(report));
    assert_eq!(report.new_households, game.weekly_cap(3, 20));
    assert!(report.income > 0);
}

#[test]
fn growth_diagnostics_report_unlock_capacity_access_and_pause() {
    let mut game = flat_game();
    zone_row(&mut game, 19, 0, 2, ZoneType::Commercial);
    fill_homes(&mut game, 9, 2);
    assert_eq!(GrowthDiagnostic::for_cell(&game, 1, 19).status, GrowthStatus::NeedsHomes);
    fill_homes(&mut game, 10, 2);
    assert_eq!(GrowthDiagnostic::for_cell(&game, 1, 19).status, GrowthStatus::Ready);
    let shop = game.map.content().building_for_zone(ZoneType::Commercial).expect("shop").id();
    game.map.set_building(0, 19, Some(shop));
    fill_homes(&mut game, 20, 2);
    let diagnostic = GrowthDiagnostic::for_cell(&game, 1, 19);
    assert_eq!(diagnostic.status, GrowthStatus::CapacityReached);
    assert!(diagnostic.message.contains("at 21 occupied homes"));

    game.map.set_terrain(1, 18, game.map.content().terrains.find("Water").expect("water").id());
    game.map.set_terrain(2, 19, game.map.content().terrains.find("Water").expect("water").id());
    game.paused = true;
    game.touch();
    let paused = GrowthDiagnostic::for_zone(&game, ZoneType::Commercial);
    assert!(paused.paused);
}

#[test]
fn money_format_is_invariant_and_handles_i32_min() {
    assert_eq!(format_money(15_000), "$15,000");
    assert_eq!(format_money(-15_000), "-$15,000");
    assert_eq!(format_money(i32::MIN), "-$2,147,483,648");
}
