#![allow(
    clippy::cast_possible_truncation,
    clippy::cast_possible_wrap,
    clippy::cast_precision_loss,
    clippy::cast_sign_loss
)]

use std::collections::{HashMap, HashSet};

use crate::{
    GameContent, GameMap, GameRandom, HighwayGenerator, MapError, PerlinNoise, Pos, RegisteredType,
    ScatterFeatureGenerator, TerrainGeneratorKind,
};

const REFERENCE_AREA: f64 = 160.0 * 96.0;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HillGenerator {
    pub coverage: f64,
    pub frequency: f64,
}

impl HillGenerator {
    #[must_use]
    pub const fn new() -> Self {
        Self { coverage: 0.17, frequency: 0.055 }
    }

    pub fn generate(&self, map: &mut GameMap, terrain_id: u8, seed: i32) {
        let noise = PerlinNoise::new(GameRandom::for_stage(seed, "hills"));
        let mut values = Vec::with_capacity(map.terrain_layer().len());

        for y in 0..map.height() {
            for x in 0..map.width() {
                values.push(noise.fractal(
                    f64::from(x) * self.frequency * 0.5,
                    f64::from(y) * self.frequency,
                    3,
                    0.5,
                ));
            }
        }

        let threshold = PerlinNoise::threshold_for_coverage(&values, self.coverage);
        for y in 0..map.height() {
            for x in 0..map.width() {
                if values[map.index(x, y)] >= threshold {
                    map.set_terrain(x, y, terrain_id);
                }
            }
        }
    }
}

impl Default for HillGenerator {
    fn default() -> Self {
        Self::new()
    }
}

impl ScatterFeatureGenerator {
    /// # Panics
    ///
    /// Panics if `feature_id` is not registered by the map.
    pub fn generate(&self, map: &mut GameMap, feature_id: u8, seed: i32) {
        let feature_name = map
            .content()
            .features
            .get_by_id(feature_id)
            .expect("feature id is registered")
            .name
            .clone();
        let mut random = GameRandom::for_stage(seed, &format!("feature:{feature_name}"));
        let cluster = (self.cluster_coverage > 0.0).then(|| {
            let noise =
                PerlinNoise::new(GameRandom::for_stage(seed, &format!("cluster:{feature_name}")));
            let mut values = Vec::with_capacity(map.terrain_layer().len());
            for y in 0..map.height() {
                for x in 0..map.width() {
                    values.push(noise.fractal(
                        f64::from(x) * self.cluster_frequency * 0.5,
                        f64::from(y) * self.cluster_frequency,
                        3,
                        0.5,
                    ));
                }
            }
            let threshold = PerlinNoise::threshold_for_coverage(&values, self.cluster_coverage);
            (values, threshold)
        });

        for y in 0..map.height() {
            for x in 0..map.width() {
                let terrain = map.terrain_at(x, y);
                if !terrain.allows_features || map.has_road(x, y) || map.feature_at(x, y).is_some()
                {
                    continue;
                }

                let in_cluster = cluster
                    .as_ref()
                    .is_some_and(|(values, threshold)| values[map.index(x, y)] >= *threshold);
                let density = if in_cluster { self.cluster_density } else { self.base_density };
                if random.chance(density * terrain.density_for(&feature_name)) {
                    map.set_feature(x, y, Some(feature_id));
                }
            }
        }
    }
}

/// Runs the map stages implemented by the Rust core.
///
/// Terrain and features are separate entry points so a road stage can be inserted between them.
pub struct MapGenerator;

impl MapGenerator {
    /// Creates a map and runs terrain, existing roads, then natural features.
    ///
    /// # Errors
    ///
    /// Returns [`MapError`] when the dimensions cannot form a valid map.
    pub fn generate(
        width: i32,
        height: i32,
        seed: i32,
        content: GameContent,
    ) -> Result<GameMap, MapError> {
        let mut map = GameMap::new(width, height, content)?;
        Self::generate_terrain(&mut map, seed);
        HighwayGenerator::generate(&mut map, seed);
        Self::generate_features(&mut map, seed);
        Ok(map)
    }

    /// Creates a map and runs terrain followed by natural features. Roads are intentionally omitted.
    ///
    /// # Errors
    ///
    /// Returns [`MapError`] when the dimensions cannot form a valid map.
    pub fn generate_terrain_and_features(
        width: i32,
        height: i32,
        seed: i32,
        content: GameContent,
    ) -> Result<GameMap, MapError> {
        let mut map = GameMap::new(width, height, content)?;
        Self::generate_terrain(&mut map, seed);
        Self::generate_features(&mut map, seed);
        Ok(map)
    }

    pub fn generate_terrain(map: &mut GameMap, seed: i32) {
        let mut generators: Vec<_> = map
            .content()
            .terrains
            .iter()
            .filter_map(|terrain| terrain.generator.map(|kind| (terrain.id(), kind)))
            .collect();
        generators.sort_by_key(|(id, kind)| (terrain_order(*kind), *id));

        for (terrain_id, generator) in generators {
            match generator {
                TerrainGeneratorKind::Hills => {
                    HillGenerator::default().generate(map, terrain_id, seed);
                }
                TerrainGeneratorKind::Water => {
                    let _ = WaterGenerator::default().generate(map, terrain_id, seed);
                }
            }
        }
    }

    pub fn generate_features(map: &mut GameMap, seed: i32) {
        let mut generators: Vec<_> = map
            .content()
            .features
            .iter()
            .filter_map(|feature| feature.generator.map(|generator| (feature.id(), generator)))
            .collect();
        generators.sort_by_key(|(id, generator)| (generator.order, *id));

        for (feature_id, generator) in generators {
            generator.generate(map, feature_id, seed);
        }
    }
}

const fn terrain_order(kind: TerrainGeneratorKind) -> i32 {
    match kind {
        TerrainGeneratorKind::Hills => 10,
        TerrainGeneratorKind::Water => 20,
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MapSide {
    North,
    East,
    South,
    West,
}

impl MapSide {
    fn random(random: &mut GameRandom) -> Self {
        match random.next(4) {
            0 => Self::North,
            1 => Self::East,
            2 => Self::South,
            _ => Self::West,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WaterKind {
    Sea,
    EdgeLake,
    CornerLake,
    InlandLake,
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum WaterMapType {
    SeaEdge,
    Bay,
    LargeLake,
    RiverConfluence,
    InlandLakes,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WaterBody {
    pub id: i16,
    pub kind: WaterKind,
    pub side: Option<MapSide>,
    pub span: i32,
    pub depth: i32,
    pub fed: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WaterRiver {
    pub id: i16,
    pub target_body_id: i16,
    pub sources: i32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WaterPlan {
    pub map_type: WaterMapType,
    pub bodies: Vec<WaterBody>,
    pub rivers: Vec<WaterRiver>,
    ids: Vec<i16>,
    width: i32,
    height: i32,
}

impl WaterPlan {
    pub const FIRST_RIVER_ID: i16 = 100;

    #[must_use]
    /// # Panics
    ///
    /// Panics if the coordinate is outside the generated map.
    pub fn id_at(&self, x: i32, y: i32) -> i16 {
        assert!(
            x >= 0 && y >= 0 && x < self.width && y < self.height,
            "water coordinate ({x},{y}) is out of bounds"
        );
        self.ids[usize::try_from(y * self.width + x).expect("water coordinate is non-negative")]
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct WaterGenerator {
    pub extra_lakes: Option<i32>,
}

impl WaterGenerator {
    pub const MAX_EDGE_DEPTH: i32 = 10;
    pub const MAX_SEA_SHARE_OF_EDGE: f64 = 0.30;

    #[must_use]
    pub fn generate(&self, map: &mut GameMap, terrain_id: u8, seed: i32) -> WaterPlan {
        Self::build(map, terrain_id, seed, self.extra_lakes)
    }

    #[must_use]
    #[allow(clippy::too_many_lines)]
    pub fn build(
        map: &mut GameMap,
        terrain_id: u8,
        seed: i32,
        extra_lakes: Option<i32>,
    ) -> WaterPlan {
        let mut random = GameRandom::for_stage(seed, "water");
        let mut ids = vec![0_i16; map.terrain_layer().len()];
        let mut bodies = Vec::new();
        let mut rivers = Vec::new();
        let mut targets = HashMap::new();
        let mut edge_water = [0_i32; 4];

        let mut map_type = choose_type(&mut random);
        let mut primary = place_primary(
            map,
            terrain_id,
            &mut random,
            &mut ids,
            &mut edge_water,
            map_type,
            &mut bodies,
            &mut targets,
        );
        if primary.is_none() {
            map_type = WaterMapType::LargeLake;
            primary = try_place_with_retries(
                map,
                terrain_id,
                &mut random,
                &mut ids,
                &mut edge_water,
                WaterKind::InlandLake,
                &mut bodies,
                &mut targets,
                false,
            );
        }

        let scale = (f64::from(map.width()) * f64::from(map.height()) / REFERENCE_AREA).sqrt();
        let extras = extra_lakes.unwrap_or_else(|| {
            (scale.mul_add(0.8, -0.5) + random.next_f64()).round().clamp(0.0, 6.0) as i32
        });
        for _ in 0..extras.max(0) {
            try_place_with_retries(
                map,
                terrain_id,
                &mut random,
                &mut ids,
                &mut edge_water,
                WaterKind::InlandLake,
                &mut bodies,
                &mut targets,
                false,
            );
        }

        let primary_id = primary.as_ref().map(|body| body.id);
        for (body_index, body_entry) in bodies.iter_mut().enumerate() {
            let body = body_entry.clone();
            let is_primary = primary_id == Some(body.id);
            let mut sources =
                if is_primary && map_type == WaterMapType::RiverConfluence { 2 } else { 1 };
            let fed_chance = if sources == 2 {
                1.0
            } else if is_primary && map_type == WaterMapType::LargeLake {
                0.95
            } else if body.kind == WaterKind::Sea {
                0.92
            } else {
                0.75
            };
            if !random.chance(fed_chance) {
                continue;
            }

            let river_id = WaterPlan::FIRST_RIVER_ID + rivers.len() as i16;
            let target = targets[&body.id];
            let mut carved = try_carve_river(
                map,
                terrain_id,
                GameRandom::for_stage(seed, &format!("river:{body_index}")),
                &mut ids,
                river_id,
                &body,
                target,
                sources,
            );
            if !carved && sources == 2 {
                sources = 1;
                map_type = if body.kind == WaterKind::Sea {
                    WaterMapType::SeaEdge
                } else {
                    WaterMapType::InlandLakes
                };
                carved = try_carve_river(
                    map,
                    terrain_id,
                    GameRandom::for_stage(seed, &format!("river:{body_index}:single")),
                    &mut ids,
                    river_id,
                    &body,
                    target,
                    1,
                );
            }

            if carved {
                rivers.push(WaterRiver { id: river_id, target_body_id: body.id, sources });
                body_entry.fed = true;

                if is_primary && map_type == WaterMapType::LargeLake && random.chance(0.4) {
                    let second = WaterPlan::FIRST_RIVER_ID + rivers.len() as i16;
                    if try_carve_river(
                        map,
                        terrain_id,
                        GameRandom::for_stage(seed, &format!("river:{body_index}:2")),
                        &mut ids,
                        second,
                        &body,
                        target,
                        1,
                    ) {
                        rivers.push(WaterRiver { id: second, target_body_id: body.id, sources: 1 });
                    }
                }
            }
        }

        WaterPlan { map_type, bodies, rivers, ids, width: map.width(), height: map.height() }
    }
}

fn choose_type(random: &mut GameRandom) -> WaterMapType {
    let roll = random.next_f64();
    if roll < 0.28 {
        WaterMapType::SeaEdge
    } else if roll < 0.50 {
        WaterMapType::Bay
    } else if roll < 0.72 {
        WaterMapType::LargeLake
    } else if roll < 0.92 {
        WaterMapType::RiverConfluence
    } else {
        WaterMapType::InlandLakes
    }
}

#[allow(clippy::too_many_arguments)]
fn place_primary(
    map: &mut GameMap,
    terrain_id: u8,
    random: &mut GameRandom,
    ids: &mut [i16],
    edge_water: &mut [i32; 4],
    map_type: WaterMapType,
    bodies: &mut Vec<WaterBody>,
    targets: &mut HashMap<i16, Pos>,
) -> Option<WaterBody> {
    let kind = match map_type {
        WaterMapType::SeaEdge => WaterKind::Sea,
        WaterMapType::Bay => {
            if random.chance(0.5) {
                WaterKind::EdgeLake
            } else {
                WaterKind::CornerLake
            }
        }
        WaterMapType::LargeLake | WaterMapType::InlandLakes => WaterKind::InlandLake,
        WaterMapType::RiverConfluence => {
            if random.chance(0.5) {
                WaterKind::Sea
            } else {
                WaterKind::InlandLake
            }
        }
    };
    try_place_with_retries(
        map,
        terrain_id,
        random,
        ids,
        edge_water,
        kind,
        bodies,
        targets,
        map_type == WaterMapType::LargeLake,
    )
}

#[allow(clippy::too_many_arguments)]
fn try_place_with_retries(
    map: &mut GameMap,
    terrain_id: u8,
    random: &mut GameRandom,
    ids: &mut [i16],
    edge_water: &mut [i32; 4],
    kind: WaterKind,
    bodies: &mut Vec<WaterBody>,
    targets: &mut HashMap<i16, Pos>,
    large: bool,
) -> Option<WaterBody> {
    for _ in 0..40 {
        let id = i16::try_from(bodies.len() + 1).expect("water body count is bounded");
        if let Some((body, target)) =
            try_place(map, terrain_id, random, ids, edge_water, id, kind, large)
        {
            targets.insert(id, target);
            bodies.push(body.clone());
            return Some(body);
        }
    }
    None
}

#[allow(clippy::too_many_arguments)]
fn try_place(
    map: &mut GameMap,
    terrain_id: u8,
    random: &mut GameRandom,
    ids: &mut [i16],
    edge_water: &mut [i32; 4],
    id: i16,
    kind: WaterKind,
    large: bool,
) -> Option<(WaterBody, Pos)> {
    match kind {
        WaterKind::Sea => try_place_sea(map, terrain_id, random, ids, edge_water, id),
        WaterKind::EdgeLake => {
            try_place_edge_lake(map, terrain_id, random, ids, edge_water, id, false)
        }
        WaterKind::CornerLake => {
            try_place_edge_lake(map, terrain_id, random, ids, edge_water, id, true)
        }
        WaterKind::InlandLake => try_place_inland_lake(map, terrain_id, random, ids, id, large),
    }
}

fn try_place_sea(
    map: &mut GameMap,
    terrain_id: u8,
    random: &mut GameRandom,
    ids: &mut [i16],
    edge_water: &mut [i32; 4],
    id: i16,
) -> Option<(WaterBody, Pos)> {
    let side = MapSide::random(random);
    let along_x = matches!(side, MapSide::North | MapSide::South);
    let edge = if along_x { map.width() } else { map.height() };
    let far = if along_x { map.height() } else { map.width() } - 1;
    let span = ((f64::from(edge)
        * (0.12 + (WaterGenerator::MAX_SEA_SHARE_OF_EDGE - 0.12) * random.next_f64()))
        as i32)
        .max(6);
    let depth = random.next_range(5, WaterGenerator::MAX_EDGE_DEPTH + 1);
    let low = span / 2 + 1;
    let high = edge - span / 2 - 1;
    if high <= low {
        return None;
    }

    let center = random.next_range(low, high);
    let noise = perlin_from(random);
    let offset = random.next_f64() * 100.0;
    let mut cells = Vec::new();
    for along in center - span / 2..=center + span / 2 {
        let t = f64::from(along - center) / (f64::from(span) / 2.0);
        let ripple = noise.fractal(f64::from(along) * 0.08, offset, 2, 0.5);
        let profile = (1.0 - t * t).max(0.0).sqrt() * (0.85 + 0.25 * (ripple * 2.0 - 1.0));
        let reach = (f64::from(depth) * profile).round().clamp(0.0, f64::from(depth)) as i32;
        for distance in 0..reach {
            let inward = if matches!(side, MapSide::North | MapSide::West) {
                distance
            } else {
                far - distance
            };
            cells.push(if along_x { Pos::new(along, inward) } else { Pos::new(inward, along) });
        }
    }

    if cells.len() < 10 || !is_clear(map, ids, &cells, 6) || !fits_edges(map, &cells, edge_water) {
        return None;
    }
    commit(map, terrain_id, ids, id, cells.iter().copied());
    add_edges(map, &cells, edge_water);

    let middle = (depth / 2).max(1);
    let target = if along_x {
        Pos::new(center, if side == MapSide::North { middle } else { map.height() - 1 - middle })
    } else {
        Pos::new(if side == MapSide::West { middle } else { map.width() - 1 - middle }, center)
    };
    Some((
        WaterBody { id, kind: WaterKind::Sea, side: Some(side), span, depth, fed: false },
        target,
    ))
}

#[allow(clippy::too_many_arguments)]
fn try_place_edge_lake(
    map: &mut GameMap,
    terrain_id: u8,
    random: &mut GameRandom,
    ids: &mut [i16],
    edge_water: &mut [i32; 4],
    id: i16,
    corner: bool,
) -> Option<(WaterBody, Pos)> {
    let side = MapSide::random(random);
    let along_x = matches!(side, MapSide::North | MapSide::South);
    let (mut radius_x, mut radius_y) = if along_x {
        let radius_y = random.next_range(3, 8);
        ((2.0 * f64::from(radius_y) * (0.9 + 0.4 * random.next_f64())).round() as i32, radius_y)
    } else {
        let radius_x = random.next_range(5, 9);
        (
            radius_x,
            (f64::from(radius_x) / (2.0 * (0.9 + 0.4 * random.next_f64()))).round().max(2.0) as i32,
        )
    };
    if corner {
        radius_x = radius_x.min(8);
        radius_y = radius_y.min(7);
    }
    let along_limit = (WaterGenerator::MAX_SEA_SHARE_OF_EDGE
        * f64::from(if along_x { map.width() } else { map.height() })
        / 2.0) as i32;
    if along_x {
        radius_x = radius_x.min(along_limit.max(4));
    } else {
        radius_y = radius_y.min(along_limit.max(2));
    }

    let center = if corner {
        Pos::new(
            if random.chance(0.5) { 0 } else { map.width() - 1 },
            if random.chance(0.5) { 0 } else { map.height() - 1 },
        )
    } else {
        let low = if along_x { radius_x } else { radius_y } + 2;
        let high = if along_x { map.width() } else { map.height() } - low;
        if high <= low {
            return None;
        }
        let along = random.next_range(low, high);
        if along_x {
            Pos::new(along, if side == MapSide::North { 0 } else { map.height() - 1 })
        } else {
            Pos::new(if side == MapSide::West { 0 } else { map.width() - 1 }, along)
        }
    };

    let cells = lake_cells(map, random, center, radius_x, radius_y);
    if cells.len() < 12 || !is_clear(map, ids, &cells, 6) || !fits_edges(map, &cells, edge_water) {
        return None;
    }
    commit(map, terrain_id, ids, id, cells.iter().copied());
    add_edges(map, &cells, edge_water);
    let target = Pos::new(
        (center.x
            + if center.x == 0 {
                radius_x / 3
            } else if center.x == map.width() - 1 {
                -radius_x / 3
            } else {
                0
            })
        .clamp(0, map.width() - 1),
        (center.y
            + if center.y == 0 {
                radius_y / 3
            } else if center.y == map.height() - 1 {
                -radius_y / 3
            } else {
                0
            })
        .clamp(0, map.height() - 1),
    );
    Some((
        WaterBody {
            id,
            kind: if corner { WaterKind::CornerLake } else { WaterKind::EdgeLake },
            side: Some(side),
            span: if along_x { 2 * radius_x } else { 2 * radius_y },
            depth: if along_x { radius_y } else { radius_x },
            fed: false,
        },
        target,
    ))
}

fn try_place_inland_lake(
    map: &mut GameMap,
    terrain_id: u8,
    random: &mut GameRandom,
    ids: &mut [i16],
    id: i16,
    large: bool,
) -> Option<(WaterBody, Pos)> {
    let max_radius = ((f64::from(map.width()) / 2.0).min(f64::from(map.height())) / 10.0) as i32;
    let max_radius = max_radius.clamp(4, 14);
    let radius_y = if large {
        random.next_range(max_radius, max_radius * 3 / 2 + 1)
    } else {
        random.next_range(3, max_radius + 1)
    };
    let radius_x = (2.0 * f64::from(radius_y) * (0.9 + 0.4 * random.next_f64())).round() as i32;
    let margin_x = radius_x + WaterGenerator::MAX_EDGE_DEPTH + 2;
    let margin_y = radius_y + WaterGenerator::MAX_EDGE_DEPTH / 2 + 2;
    if map.width() <= 2 * margin_x || map.height() <= 2 * margin_y {
        return None;
    }

    let center = Pos::new(
        random.next_range(margin_x, map.width() - margin_x),
        random.next_range(margin_y, map.height() - margin_y),
    );
    let cells = lake_cells(map, random, center, radius_x, radius_y);
    if !is_clear(map, ids, &cells, 6) {
        return None;
    }
    commit(map, terrain_id, ids, id, cells.iter().copied());
    Some((
        WaterBody {
            id,
            kind: WaterKind::InlandLake,
            side: None,
            span: 2 * radius_x,
            depth: 2 * radius_y,
            fed: false,
        },
        center,
    ))
}

fn lake_cells(
    map: &GameMap,
    random: &mut GameRandom,
    center: Pos,
    radius_x: i32,
    radius_y: i32,
) -> Vec<Pos> {
    let noise = perlin_from(random);
    let offset = random.next_f64() * 100.0;
    let mut cells = Vec::new();
    for y in center.y - radius_y - 2..=center.y + radius_y + 2 {
        for x in center.x - radius_x - 3..=center.x + radius_x + 3 {
            if !map.in_bounds(x, y) {
                continue;
            }
            let dx = f64::from(x - center.x) / f64::from(radius_x);
            let dy = f64::from(y - center.y) / f64::from(radius_y);
            let shore = 1.0
                + 0.2
                    * noise.noise(
                        f64::from(x).mul_add(0.12, offset),
                        f64::from(y).mul_add(0.24, offset),
                    );
            if dx.hypot(dy) <= shore {
                cells.push(Pos::new(x, y));
            }
        }
    }
    cells
}

fn edge_counts(map: &GameMap, cells: &[Pos]) -> [i32; 4] {
    let mut counts = [0; 4];
    for cell in cells {
        counts[0] += i32::from(cell.y == 0);
        counts[1] += i32::from(cell.x == map.width() - 1);
        counts[2] += i32::from(cell.y == map.height() - 1);
        counts[3] += i32::from(cell.x == 0);
    }
    counts
}

fn fits_edges(map: &GameMap, cells: &[Pos], edge_water: &[i32; 4]) -> bool {
    let counts = edge_counts(map, cells);
    (0..4).all(|side| {
        let length = if matches!(side, 0 | 2) { map.width() } else { map.height() };
        f64::from(edge_water[side] + counts[side])
            <= WaterGenerator::MAX_SEA_SHARE_OF_EDGE * f64::from(length)
    })
}

fn add_edges(map: &GameMap, cells: &[Pos], edge_water: &mut [i32; 4]) {
    let counts = edge_counts(map, cells);
    for side in 0..4 {
        edge_water[side] += counts[side];
    }
}

fn is_clear(map: &GameMap, ids: &[i16], cells: &[Pos], margin: i32) -> bool {
    cells.iter().all(|cell| {
        (-margin / 2..=margin / 2).all(|dy| {
            (-margin..=margin).all(|dx| {
                let position = cell.offset(dx, dy);
                !map.contains(position) || ids[map.index(position.x, position.y)] == 0
            })
        })
    })
}

fn commit(
    map: &mut GameMap,
    terrain_id: u8,
    ids: &mut [i16],
    id: i16,
    cells: impl IntoIterator<Item = Pos>,
) {
    for cell in cells {
        map.set_terrain(cell.x, cell.y, terrain_id);
        ids[map.index(cell.x, cell.y)] = id;
    }
}

#[derive(Clone, Debug)]
struct Segment {
    cells: HashSet<Pos>,
    centers: Vec<(Pos, f64)>,
}

#[allow(clippy::too_many_arguments)]
fn try_carve_river(
    map: &mut GameMap,
    terrain_id: u8,
    mut random: GameRandom,
    ids: &mut [i16],
    river_id: i16,
    body: &WaterBody,
    target: Pos,
    sources: i32,
) -> bool {
    let minimum = 0.3 * f64::from(map.width()).min(2.0 * f64::from(map.height()));
    for _ in 0..16 {
        let mut segments = Vec::new();
        if sources == 1 {
            let source = random_edge_point(map, &mut random, body);
            if distance(source, target) < minimum {
                continue;
            }
            segments.push(make_segment(map, &mut random, source, target));
        } else {
            let first = random_edge_point(map, &mut random, body);
            let second = random_edge_point(map, &mut random, body);
            if distance(first, second) < minimum {
                continue;
            }
            let middle_x = f64::from(first.x + second.x) / 2.0;
            let middle_y = f64::from(first.y + second.y) / 2.0;
            let fraction = 0.45 + 0.2 * random.next_f64();
            let join = Pos::new(
                (middle_x + f64::from(target.x).mul_add(fraction, -middle_x * fraction)).round()
                    as i32,
                (middle_y + f64::from(target.y).mul_add(fraction, -middle_y * fraction)).round()
                    as i32,
            );
            if !map.contains(join)
                || distance(join, target) < 0.25 * minimum
                || distance(first, join) < 0.4 * minimum
                || distance(second, join) < 0.4 * minimum
            {
                continue;
            }
            segments.push(make_segment(map, &mut random, first, join));
            segments.push(make_segment(map, &mut random, second, join));
            segments.push(make_segment(map, &mut random, join, target));
        }

        if segments.iter().all(|segment| is_river_clear(map, ids, segment, river_id, body.id)) {
            for segment in segments {
                commit(map, terrain_id, ids, river_id, segment.cells);
            }
            return true;
        }
    }
    false
}

fn distance(first: Pos, second: Pos) -> f64 {
    let dx = f64::from(first.x - second.x);
    let dy = 2.0 * f64::from(first.y - second.y);
    dx.hypot(dy)
}

fn random_edge_point(map: &GameMap, random: &mut GameRandom, body: &WaterBody) -> Pos {
    for _ in 0..20 {
        let side = MapSide::random(random);
        if body.side == Some(side) && body.kind != WaterKind::InlandLake {
            continue;
        }
        return match side {
            MapSide::North => Pos::new(edge_coordinate(random, map.width()), 0),
            MapSide::South => Pos::new(edge_coordinate(random, map.width()), map.height() - 1),
            MapSide::West => Pos::new(0, edge_coordinate(random, map.height())),
            MapSide::East => Pos::new(map.width() - 1, edge_coordinate(random, map.height())),
        };
    }
    Pos::new(0, 0)
}

fn edge_coordinate(random: &mut GameRandom, length: i32) -> i32 {
    let margin = 6.min((length - 2) / 2).max(0);
    let high = length - margin;
    if high > margin { random.next_range(margin, high) } else { length / 2 }
}

fn make_segment(map: &GameMap, random: &mut GameRandom, from: Pos, to: Pos) -> Segment {
    let length = distance(from, to);
    let dx = f64::from(to.x - from.x);
    let dy = 2.0 * f64::from(to.y - from.y);
    let noise = perlin_from(random);
    let offset = random.next_f64() * 100.0;
    let amplitude = (0.12 * length).min(20.0);
    let perpendicular_x = if length > 0.0 { -dy / length } else { 0.0 };
    let perpendicular_y = if length > 0.0 { dx / length } else { 0.0 };
    let steps = length.ceil().max(2.0) as i32;
    let mut cells = HashSet::new();
    let mut centers = Vec::new();

    for step in 0..=steps {
        let progress = f64::from(step) / f64::from(steps);
        let taper = 1.0_f64.min((progress * 8.0).min((1.0 - progress) * 5.0));
        let sway = amplitude
            * taper
            * (noise.fractal(progress * length * 0.018, offset, 2, 0.5) * 2.0 - 1.0)
            * 2.0;
        let visual_x = f64::from(from.x) + dx * progress + perpendicular_x * sway;
        let visual_y = 2.0 * f64::from(from.y) + dy * progress + perpendicular_y * sway;
        let center = Pos::new(visual_x.round() as i32, (visual_y / 2.0).round() as i32);
        centers.push((center, progress));

        let radius_y = 1.0
            + if noise.noise(progress * length * 0.05, offset + 50.0) > 0.2 { 0.5 } else { 0.0 };
        let radius_x = 2.0 * radius_y;
        for y in (f64::from(center.y) - radius_y).floor() as i32
            ..=(f64::from(center.y) + radius_y).ceil() as i32
        {
            for x in (f64::from(center.x) - radius_x).floor() as i32
                ..=(f64::from(center.x) + radius_x).ceil() as i32
            {
                let ellipse_x = f64::from(x - center.x) / radius_x;
                let ellipse_y = f64::from(y - center.y) / radius_y;
                if ellipse_x * ellipse_x + ellipse_y * ellipse_y <= 1.05 && map.in_bounds(x, y) {
                    cells.insert(Pos::new(x, y));
                }
            }
        }
    }
    Segment { cells, centers }
}

fn is_river_clear(
    map: &GameMap,
    ids: &[i16],
    segment: &Segment,
    river_id: i16,
    target_id: i16,
) -> bool {
    if segment.cells.iter().any(|cell| {
        let existing = ids[map.index(cell.x, cell.y)];
        existing != 0 && existing != target_id && existing != river_id
    }) {
        return false;
    }

    for (center, progress) in &segment.centers {
        let edge_distance =
            center.x.min(map.width() - 1 - center.x).min(center.y.min(map.height() - 1 - center.y));
        if *progress > 0.12 && *progress < 0.85 && edge_distance < 3 {
            return false;
        }
        for dy in -3..=3 {
            for dx in -6..=6 {
                let nearby = center.offset(dx, dy);
                if !map.contains(nearby) {
                    continue;
                }
                let existing = ids[map.index(nearby.x, nearby.y)];
                if existing != 0
                    && existing != river_id
                    && !(existing == target_id && *progress > 0.7)
                {
                    return false;
                }
            }
        }
    }
    true
}

fn perlin_from(random: &mut GameRandom) -> PerlinNoise {
    let noise = PerlinNoise::new(*random);
    for _ in 1..256 {
        random.next_u64();
    }
    noise
}
