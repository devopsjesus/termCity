use termcity_core::{
    COMMERCIAL_ZONE, GameContent, GameRandom, Household, INDUSTRIAL_ZONE, RESIDENTIAL_ZONE,
    RegisteredType, RegistryError, TypeRegistry, ZoneType,
};

#[test]
fn default_content_matches_registered_ids_and_names() {
    let content = GameContent::default();

    assert_eq!(content.terrains.len(), 3);
    assert_eq!(content.base_terrain().name(), "Grass");
    assert_eq!(content.base_terrain().id(), 0);
    assert_eq!(content.terrains.find("hIlL").expect("hill exists").id(), 1);
    assert_eq!(content.features.find("Tree").expect("tree exists").id(), 1);
    assert_eq!(content.buildings.find("Factory").expect("factory exists").id(), 3);
    assert_eq!(content.default_road().name(), "Street");
    assert_eq!(content.roads.find("Highway").expect("highway exists").id(), 3);
}

#[test]
fn registry_rejects_duplicate_names_case_insensitively() {
    let content = GameContent::default();
    let mut registry = TypeRegistry::new(0);
    let terrain = content.base_terrain().clone();
    registry.register(terrain.clone()).expect("first registration succeeds");
    let mut duplicate = terrain;
    duplicate.name = "gRaSs".into();
    assert_eq!(registry.register(duplicate), Err(RegistryError::DuplicateName("gRaSs".into())));
}

#[test]
fn default_content_matches_glyph_cost_and_zone_rules() {
    let content = GameContent::default();
    let hill = content.terrains.find("Hill").expect("hill exists");
    assert_eq!(hill.glyphs.len(), 8);
    assert_eq!(hill.build_cost_modifier, 1.5);
    assert_eq!(hill.density_for("Tree"), 0.5);
    assert_eq!(hill.density_for("Unknown"), 1.0);
    assert!(!content.terrains.find("Water").expect("water exists").buildable);

    assert_eq!(content.roads.find("Avenue").expect("avenue exists").cost_multiplier, 1.8);
    assert_eq!(content.roads.find("Highway").expect("highway exists").glyph_for(15), "╬");
    assert_eq!(
        content.building_for_zone(ZoneType::Commercial).expect("shop exists").name(),
        "Shop"
    );
}

#[test]
fn zones_and_households_match_csharp_rules() {
    assert_eq!(ZoneType::Residential.info(), Some(&RESIDENTIAL_ZONE));
    assert_eq!(ZoneType::Commercial.info(), Some(&COMMERCIAL_ZONE));
    assert_eq!(ZoneType::Industrial.info(), Some(&INDUSTRIAL_ZONE));
    assert_eq!(ZoneType::None.info(), None);
    assert_eq!(RESIDENTIAL_ZONE.weekly_value, 200);
    assert_eq!(COMMERCIAL_ZONE.weekly_value, 350);
    assert_eq!(INDUSTRIAL_ZONE.weekly_value, 500);

    let mut random = GameRandom::new(0);
    assert_eq!(Household::random(&mut random), Household::new(2, 0, 1));
    assert_eq!(Household::new(2, 3, 1).total(), 6);
    assert!(Household::default().is_empty());
}
