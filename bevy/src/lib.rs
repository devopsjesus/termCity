#![allow(clippy::cast_precision_loss, clippy::cast_possible_truncation)]

use termcity_core::{
    CellRect, CellVisual, CityStats, GameSession, MapRenderer, Pos, TerrainGeneratorKind,
    pick_cell_variant,
};

pub const CELL_WIDTH: f32 = 12.0;
pub const CELL_HEIGHT: f32 = 22.0;
pub const HEADER_HEIGHT: f32 = 70.0;
pub const FOOTER_HEIGHT: f32 = 64.0;
pub const HILL_BEAT_SECONDS: f32 = 60.0 / 80.0;
pub const HILL_CYCLE_SECONDS: f32 = HILL_BEAT_SECONDS * 4.0;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Grid {
    pub columns: i32,
    pub rows: i32,
    pub window_width: f32,
    pub window_height: f32,
}

impl Grid {
    #[must_use]
    pub fn new(width: f32, height: f32) -> Self {
        Self {
            columns: (width / CELL_WIDTH).floor().max(0.0) as i32,
            rows: ((height - HEADER_HEIGHT - FOOTER_HEIGHT) / CELL_HEIGHT).floor().max(0.0) as i32,
            window_width: width,
            window_height: height,
        }
    }

    #[must_use]
    pub fn hit_test(self, x: f32, y: f32) -> Option<(i32, i32)> {
        if !x.is_finite() || !y.is_finite() || x < 0.0 || y < HEADER_HEIGHT {
            return None;
        }
        let column = (x / CELL_WIDTH).floor() as i32;
        let row = ((y - HEADER_HEIGHT) / CELL_HEIGHT).floor() as i32;
        (column < self.columns && row < self.rows).then_some((column, row))
    }

    #[must_use]
    pub fn center(self, column: i32, row: i32) -> (f32, f32) {
        (
            (column as f32 + 0.5) * CELL_WIDTH - self.window_width / 2.0,
            self.window_height / 2.0 - HEADER_HEIGHT - (row as f32 + 0.5) * CELL_HEIGHT,
        )
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DisplayCell {
    pub area: CellRect,
    pub visual: CellVisual,
    pub animated_hill: bool,
    pub animated_tree: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Stamp {
    version: u64,
    seed: i32,
    camera: Pos,
    zoom: i32,
    columns: i32,
    rows: i32,
}

#[derive(Default)]
pub struct MapCache {
    stamp: Option<Stamp>,
    pub cells: Vec<Option<DisplayCell>>,
    pub stats: CityStats,
    pub rebuilds: u64,
}

impl MapCache {
    /// Refreshes the base appearance only when the map or view has changed.
    ///
    /// # Panics
    ///
    /// Panics if viewport dimensions cannot fit in memory.
    pub fn refresh(&mut self, session: &GameSession, grid: Grid) -> bool {
        let stamp = Stamp {
            version: session.game.map_version(),
            seed: session.game.config.seed,
            camera: Pos::new(session.camera_x, session.camera_y),
            zoom: session.zoom_level,
            columns: grid.columns,
            rows: grid.rows,
        };
        if self.stamp == Some(stamp) {
            return false;
        }
        self.cells.clear();
        let renderer = MapRenderer::new(&session.game);
        let tree = session.game.map.content().features.find("Tree");
        self.stats = renderer.stats();
        for row in 0..grid.rows {
            for column in 0..grid.columns {
                let pos = session.screen_to_map(column, row);
                if !session.game.map.contains(pos) {
                    self.cells.push(None);
                    continue;
                }
                let stride = session.stride();
                let area = CellRect::new(
                    pos.x,
                    pos.y,
                    stride.min(session.game.map.width() - pos.x),
                    stride.min(session.game.map.height() - pos.y),
                );
                let visual = renderer.sample_rect(pos.x, pos.y, area.width, area.height);
                let animate = should_animate(pos);
                let terrain = session.game.map.terrain_at(pos.x, pos.y);
                // At coarse zoom, the sampled glyph can come from elsewhere in the block.
                let hill_glyph = session.game.map.content().terrains.iter().any(|terrain| {
                    terrain.generator == Some(TerrainGeneratorKind::Hills)
                        && terrain.glyphs.contains(&visual.glyph)
                });
                self.cells.push(Some(DisplayCell {
                    area,
                    visual,
                    animated_hill: animate
                        && hill_glyph
                        && (stride > 1 || terrain.generator == Some(TerrainGeneratorKind::Hills)),
                    animated_tree: animate
                        && tree.is_some_and(|feature| feature.glyphs.contains(&visual.glyph)),
                }));
            }
        }
        self.stamp = Some(stamp);
        self.rebuilds += 1;
        true
    }

    pub fn invalidate(&mut self) {
        self.stamp = None;
    }
}

#[must_use]
pub fn should_animate(position: Pos) -> bool {
    // Salt the coordinates independently of glyph variation.
    pick_cell_variant(position.x ^ 0x51ed_270b, position.y ^ 0x2f6e_2b1d, 10) == 0
}

#[must_use]
pub fn hill_offset(seconds: f32, position: Pos) -> (f32, f32) {
    let beat = (seconds.rem_euclid(HILL_CYCLE_SECONDS) / HILL_BEAT_SECONDS).floor();
    let height = (1.0 - (beat - 2.0).abs()) * 1.5;
    let direction = if (position.x ^ position.y) & 1 == 0 { 1.0 } else { -1.0 };
    (0.0, height * direction)
}

#[must_use]
pub fn tree_offset(seconds: f32, position: Pos) -> (f32, f32) {
    let (_, height) = hill_offset(seconds, position);
    (-height, 0.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use termcity_core::{
        CityGame, GameConfig, RegisteredType, SaveGameStore, SessionAction, ZoneType,
    };

    fn session() -> GameSession {
        let mut game = CityGame::new(GameConfig { seed: 42, ..GameConfig::default() }).unwrap();
        game.paused = true;
        GameSession::new(game, "unused-bevy-test.json")
    }

    #[test]
    fn grid_excludes_hud_and_incomplete_margins() {
        let grid = Grid::new(1205.0, 725.0);
        assert_eq!((grid.columns, grid.rows), (100, 26));
        assert_eq!(grid.hit_test(0.0, HEADER_HEIGHT), Some((0, 0)));
        assert_eq!(grid.hit_test(1199.0, HEADER_HEIGHT + 571.0), Some((99, 25)));
        for (x, y) in [(1200.0, 100.0), (-1.0, 100.0), (0.0, 69.0), (0.0, 642.0), (f32::NAN, 100.0)]
        {
            assert_eq!(grid.hit_test(x, y), None);
        }
    }

    #[test]
    fn tiny_window_has_no_selectable_map() {
        assert_eq!(Grid::new(5.0, 80.0).hit_test(0.0, 70.0), None);
    }

    #[test]
    fn sprite_centers_round_trip_to_authoritative_cells_at_every_zoom() {
        let mut session = session();
        let grid = Grid::new(1200.0, 720.0);
        session.set_viewport(grid.columns, grid.rows);
        for zoom in GameSession::<termcity_core::DiskFileSystem>::MIN_ZOOM
            ..=GameSession::<termcity_core::DiskFileSystem>::MAX_ZOOM
        {
            session.set_zoom(zoom, None);
            for (column, row) in [(0, 0), (50, 12), (99, 25)] {
                let (x, y) = grid.center(column, row);
                let hit = grid
                    .hit_test(x + grid.window_width / 2.0, grid.window_height / 2.0 - y)
                    .unwrap();
                assert_eq!(hit, (column, row));
                assert_eq!(session.screen_to_map(hit.0, hit.1), session.screen_to_map(column, row));
            }
        }
    }

    #[test]
    fn animation_and_selection_do_not_rebuild_or_mutate_the_map() {
        let mut session = session();
        let grid = Grid::new(960.0, 640.0);
        session.set_viewport(grid.columns, grid.rows);
        let mut cache = MapCache::default();
        assert!(cache.refresh(&session, grid));
        let saved = SaveGameStore::serialize(&session.game).unwrap();
        let first = hill_offset(0.0, session.cursor);
        session.dispatch(SessionAction::MoveCursor { dx: 1, dy: 0, extend: true }).unwrap();
        session.update(f64::from(HILL_BEAT_SECONDS)).unwrap();
        assert_ne!(first, hill_offset(HILL_BEAT_SECONDS, session.cursor));
        assert!(!cache.refresh(&session, grid));
        assert_eq!(cache.rebuilds, 1);
        assert_eq!(saved, SaveGameStore::serialize(&session.game).unwrap());
    }

    #[test]
    fn hills_advance_at_eighty_beats_per_minute() {
        for (seconds, expected) in
            [(0.0, -1.5), (0.749, -1.5), (0.75, 0.0), (1.5, 1.5), (2.25, 0.0), (3.0, -1.5)]
        {
            assert!((hill_offset(seconds, Pos::new(4, 6)).1 - expected).abs() < f32::EPSILON);
        }
    }

    #[test]
    fn hills_step_up_up_down_down_without_sideways_movement() {
        let position = Pos::new(4, 6);
        for (beat, expected) in [-1.5, 0.0, 1.5, 0.0, -1.5].into_iter().enumerate() {
            let (x, y) = hill_offset(beat as f32 * HILL_BEAT_SECONDS, position);
            assert!(x.abs() < f32::EPSILON);
            assert!((y - expected).abs() < f32::EPSILON);
            let held =
                hill_offset(beat as f32 * HILL_BEAT_SECONDS + HILL_BEAT_SECONDS * 0.5, position);
            assert!((held.1 - expected).abs() < f32::EPSILON, "hold each beat until the next step");
        }
    }

    #[test]
    fn trees_step_left_left_right_right_on_the_same_eighty_bpm_beat() {
        let position = Pos::new(4, 6);
        for (beat, expected) in [1.5, 0.0, -1.5, 0.0, 1.5].into_iter().enumerate() {
            let seconds = beat as f32 * HILL_BEAT_SECONDS;
            let (x, y) = tree_offset(seconds, position);
            assert!((x - expected).abs() < f32::EPSILON);
            assert!(y.abs() < f32::EPSILON, "trees must not bob vertically");
            assert!((x + hill_offset(seconds, position).1).abs() < f32::EPSILON);
            assert!((tree_offset(seconds, position.offset(1, 0)).0 + x).abs() < f32::EPSILON);
            assert!(
                (tree_offset(seconds + HILL_BEAT_SECONDS * 0.5, position).0 - x).abs()
                    < f32::EPSILON
            );
        }
    }

    #[test]
    fn only_visible_tree_glyphs_are_marked_for_horizontal_animation() {
        let mut session = session();
        let grid = Grid::new(960.0, 640.0);
        session.set_viewport(grid.columns, grid.rows);
        let position = CellRect::new(40, 24, 78, 48)
            .cells()
            .find(|pos| session.game.can_build_on(pos.x, pos.y) && should_animate(*pos))
            .unwrap();
        let tree_id = session.game.map.content().features.find("Tree").unwrap().id();
        session.game.map.set_feature(position.x, position.y, Some(tree_id));
        session.center_on(position);
        session.place_cursor(position);
        let mut cache = MapCache::default();
        cache.refresh(&session, grid);
        let cell = cache.cells.iter().flatten().find(|cell| cell.area.contains(position)).unwrap();
        assert!(cell.animated_tree);
        assert!(!cell.animated_hill);
        session.scroll_camera(1, 0);
        assert!(cache.refresh(&session, grid));
        let cell = cache.cells.iter().flatten().find(|cell| cell.area.contains(position)).unwrap();
        assert!(cell.animated_tree, "scrolling must not change the chosen subset");
        assert!(session.build_road(None).unwrap().success);
        assert!(cache.refresh(&session, grid));
        let cell = cache.cells.iter().flatten().find(|cell| cell.area.contains(position)).unwrap();
        assert!(!cell.animated_tree, "a road replacing a tree must stop its animation");
        assert!(!cell.animated_hill);
    }

    #[test]
    fn animation_density_is_between_eight_and_twelve_percent_for_each_kind_at_every_zoom() {
        let mut game = CityGame::new(GameConfig {
            map_width: 640,
            map_height: 384,
            seed: 42,
            ..GameConfig::default()
        })
        .unwrap();
        game.paused = true;
        let mut session = GameSession::new(game, "unused-animation-density-test.json");
        let grid =
            Grid::new(640.0 * CELL_WIDTH, 384.0 * CELL_HEIGHT + HEADER_HEIGHT + FOOTER_HEIGHT);
        session.set_viewport(grid.columns, grid.rows);
        let hill_glyphs: Vec<_> = session
            .game
            .map
            .content()
            .terrains
            .iter()
            .filter(|terrain| terrain.generator == Some(TerrainGeneratorKind::Hills))
            .flat_map(|terrain| terrain.glyphs.iter().copied())
            .collect();
        let tree_glyphs = session.game.map.content().features.find("Tree").unwrap().glyphs.clone();
        let mut cache = MapCache::default();
        for zoom in -2..=1 {
            session.set_zoom(zoom, None);
            cache.refresh(&session, grid);
            let mut hills = (0, 0);
            let mut trees = (0, 0);
            for cell in cache.cells.iter().flatten() {
                if hill_glyphs.contains(&cell.visual.glyph) {
                    hills.0 += 1;
                    hills.1 += i32::from(cell.animated_hill);
                }
                if tree_glyphs.contains(&cell.visual.glyph) {
                    trees.0 += 1;
                    trees.1 += i32::from(cell.animated_tree);
                }
            }
            for (kind, (total, animated)) in [("hills", hills), ("trees", trees)] {
                assert!(total > 0);
                let percentage = 100.0 * f64::from(animated) / f64::from(total);
                assert!(
                    (8.0..=12.0).contains(&percentage),
                    "{kind} at zoom {zoom}: {percentage:.2}% animated"
                );
            }
        }
    }

    #[test]
    fn neighboring_tiles_bob_in_opposite_directions_with_a_stable_checkerboard() {
        for beat in 0..4 {
            let seconds = beat as f32 * HILL_BEAT_SECONDS;
            let (_, center) = hill_offset(seconds, Pos::new(4, 6));
            for position in [Pos::new(3, 6), Pos::new(5, 6), Pos::new(4, 5), Pos::new(4, 7)] {
                assert!((hill_offset(seconds, position).1 + center).abs() < f32::EPSILON);
            }
            assert!((hill_offset(seconds, Pos::new(5, 7)).1 - center).abs() < f32::EPSILON);
            assert!(
                (hill_offset(seconds + HILL_CYCLE_SECONDS, Pos::new(4, 6)).1 - center).abs()
                    < f32::EPSILON
            );
        }
    }

    #[test]
    fn map_camera_zoom_and_resize_invalidate_cache() {
        let mut session = session();
        let grid = Grid::new(960.0, 640.0);
        session.set_viewport(grid.columns, grid.rows);
        let mut cache = MapCache::default();
        cache.refresh(&session, grid);
        session.scroll_camera(1, 0);
        assert!(cache.refresh(&session, grid));
        session.set_zoom(-1, None);
        assert!(cache.refresh(&session, grid));
        let resized = Grid::new(800.0, 600.0);
        session.set_viewport(resized.columns, resized.rows);
        assert!(cache.refresh(&session, resized));
        let buildable = CellRect::new(0, 0, session.game.map.width(), session.game.map.height())
            .cells()
            .find(|pos| session.game.can_build_on(pos.x, pos.y))
            .unwrap();
        session.place_cursor(buildable);
        session.zone(ZoneType::Residential).unwrap();
        assert!(cache.refresh(&session, resized));
    }

    #[test]
    fn rendering_is_bounded_to_view_not_map_area() {
        let mut session = session();
        let grid = Grid::new(960.0, 640.0);
        session.set_viewport(grid.columns, grid.rows);
        let mut cache = MapCache::default();
        for zoom in -2..=1 {
            session.set_zoom(zoom, None);
            cache.refresh(&session, grid);
            assert_eq!(cache.cells.len(), usize::try_from(grid.columns * grid.rows).unwrap());
            for cell in cache.cells.iter().flatten() {
                assert!(session.game.map.contains(Pos::new(cell.area.right(), cell.area.bottom())));
            }
        }
    }
}
