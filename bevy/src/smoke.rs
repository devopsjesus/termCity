use std::path::PathBuf;

use bevy::prelude::*;
use bevy::render::view::screenshot::{Screenshot, ScreenshotCaptured};
use bevy::window::{PrimaryWindow, WindowCloseRequested};
use termcity_bevy::{CELL_HEIGHT, CELL_WIDTH, HEADER_HEIGHT, HILL_BEAT_SECONDS, should_animate};
use termcity_core::{
    CellRect, GameSession, Pos, PromptKind, SaveGameStore, TerrainGeneratorKind, ZoneType,
};

use crate::{Host, Tile};

#[derive(Resource)]
pub struct Smoke {
    pub enabled: bool,
    frame: u32,
    frames: u32,
    screenshot: Option<PathBuf>,
    screenshot_saved: bool,
    uncapped: bool,
    baseline: String,
    hill: Option<(usize, Vec3)>,
    tree: Option<(usize, Vec3)>,
    rebuilds: u64,
    animation_seconds: f32,
    elapsed_days: f64,
    target: Pos,
    camera: Pos,
    frame_ms: Vec<f64>,
    update_ms: Vec<f64>,
}

impl Smoke {
    pub fn new(enabled: bool, frames: u32, screenshot: Option<PathBuf>, uncapped: bool) -> Self {
        Self {
            enabled,
            frames,
            screenshot,
            screenshot_saved: false,
            uncapped,
            frame: 0,
            baseline: String::new(),
            hill: None,
            tree: None,
            rebuilds: 0,
            animation_seconds: 0.0,
            elapsed_days: 0.0,
            target: Pos::default(),
            camera: Pos::default(),
            frame_ms: Vec::new(),
            update_ms: Vec::new(),
        }
    }
}

pub fn drive(
    mut smoke: ResMut<Smoke>,
    mut host: ResMut<Host>,
    mut keys: ResMut<ButtonInput<KeyCode>>,
    mut mouse: ResMut<ButtonInput<MouseButton>>,
    mut windows: Query<(Entity, &mut Window), With<PrimaryWindow>>,
    mut close: MessageWriter<WindowCloseRequested>,
) {
    if !smoke.enabled {
        return;
    }
    smoke.frame += 1;
    keys.reset_all();
    mouse.clear();
    mouse.release_all();
    let (entity, mut window) = windows.single_mut().expect("native smoke window");
    window.focused = !(80..85).contains(&smoke.frame);
    match smoke.frame {
        30 => {
            let game = &host.session.game;
            let hill = CellRect::new(0, 0, game.map.width(), game.map.height())
                .cells()
                .find(|pos| {
                    game.map.terrain_at(pos.x, pos.y).generator == Some(TerrainGeneratorKind::Hills)
                        && game.map.feature_at(pos.x, pos.y).is_none()
                        && !game.map.has_road(pos.x, pos.y)
                        && should_animate(*pos)
                })
                .expect("generated map contains a bare hill");
            host.session.center_on(hill);
        }
        40 => {
            host.animation_seconds = 0.0;
            smoke.baseline =
                SaveGameStore::serialize(&host.session.game).expect("serialize smoke city");
            smoke.rebuilds = host.cache.rebuilds;
        }
        45 => host.animation_seconds = HILL_BEAT_SECONDS,
        50 => {
            let hit = (0..host.grid.rows)
                .flat_map(|row| (0..host.grid.columns).map(move |column| (column, row)))
                .find(|&(column, row)| {
                    let pos = host.session.screen_to_map(column, row);
                    host.session.game.can_build_on(pos.x, pos.y)
                })
                .expect("visible buildable cell");
            smoke.target = host.session.screen_to_map(hit.0, hit.1);
            window.set_cursor_position(Some(Vec2::new(
                (hit.0 as f32 + 0.5) * CELL_WIDTH,
                HEADER_HEIGHT + (hit.1 as f32 + 0.5) * CELL_HEIGHT,
            )));
            mouse.press(MouseButton::Left);
        }
        52 => keys.press(KeyCode::KeyR),
        53 => {
            keys.press(KeyCode::KeyU);
            keys.press(KeyCode::KeyB);
        }
        54 => keys.press(KeyCode::Enter),
        56 => keys.press(KeyCode::F5),
        58 => host.session.autosave().expect("native autosave"),
        60 | 61 => keys.press(KeyCode::Minus),
        62..=64 => keys.press(KeyCode::Equal),
        65 => keys.press(KeyCode::F6),
        66 | 102 | 107 => keys.press(KeyCode::Escape),
        67 => window.resolution.set(960.0, 640.0),
        72 => window.resolution.set(1200.0, 720.0),
        75 => {
            smoke.camera = Pos::new(host.session.camera_x, host.session.camera_y);
            keys.press(KeyCode::KeyD);
        }
        80 => {
            smoke.animation_seconds = host.animation_seconds;
            smoke.elapsed_days = host.session.game.elapsed_days();
        }
        87 | 95 => keys.press(KeyCode::KeyP),
        100 => keys.press(KeyCode::KeyQ),
        105 => {
            close.write(WindowCloseRequested { window: entity });
        }
        110 => window.resolution.set_scale_factor_override(Some(1.5)),
        114 => window.resolution.set_scale_factor_override(None),
        _ => {}
    }
    if smoke.frame > 120 {
        if smoke.frame.is_multiple_of(30) {
            keys.press(if smoke.frame.is_multiple_of(60) { KeyCode::KeyD } else { KeyCode::KeyA });
        }
        if smoke.frame % 100 == 10 {
            keys.press(KeyCode::Minus);
        } else if smoke.frame % 100 == 20 {
            keys.press(KeyCode::Equal);
        }
    }
}

#[allow(clippy::too_many_lines)]
pub fn verify(
    mut commands: Commands,
    mut smoke: ResMut<Smoke>,
    host: Res<Host>,
    time: Res<Time<Real>>,
    tiles: Query<(&Tile, &Transform, &Visibility)>,
    cameras: Query<(&Camera, &GlobalTransform)>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut exit: MessageWriter<AppExit>,
) {
    if !smoke.enabled {
        return;
    }
    match smoke.frame {
        40 => {
            assert_eq!(tiles.iter().count(), host.cache.cells.len() * 2);
            assert!(host.grid.columns > 0 && host.grid.rows > 0);
            smoke.hill = tiles.iter().find_map(|(tile, transform, visibility)| {
                (tile.glyph
                    && *visibility != Visibility::Hidden
                    && host.cache.cells[tile.index].is_some_and(|cell| cell.animated_hill))
                .then_some((tile.index, transform.translation))
            });
            assert!(smoke.hill.is_some(), "a hill must be visibly animated");
            smoke.tree = tiles.iter().find_map(|(tile, transform, visibility)| {
                (tile.glyph
                    && *visibility != Visibility::Hidden
                    && host.cache.cells[tile.index].is_some_and(|cell| cell.animated_tree))
                .then_some((tile.index, transform.translation))
            });
            assert!(smoke.tree.is_some(), "a tree must be visibly animated");
        }
        45 => {
            assert_eq!(
                smoke.baseline,
                SaveGameStore::serialize(&host.session.game).unwrap(),
                "animation must not mutate gameplay"
            );
            assert_eq!(
                smoke.rebuilds, host.cache.rebuilds,
                "animation must not rebuild base appearance"
            );
            let (index, before) = smoke.hill.unwrap();
            let (_, transform, _) =
                tiles.iter().find(|(tile, _, _)| tile.glyph && tile.index == index).unwrap();
            assert_ne!(before, transform.translation, "paused gameplay must still animate hills");
            assert!(
                (before.x - transform.translation.x).abs() < f32::EPSILON,
                "bobbing must stay vertical"
            );
            let (index, before) = smoke.tree.unwrap();
            let (_, transform, _) =
                tiles.iter().find(|(tile, _, _)| tile.glyph && tile.index == index).unwrap();
            assert_ne!(before, transform.translation, "paused gameplay must still animate trees");
            assert!(
                (before.y - transform.translation.y).abs() < f32::EPSILON,
                "tree bounces must stay horizontal"
            );
        }
        52 => {
            assert_eq!(host.session.cursor, smoke.target, "native pointer hit testing");
            assert_eq!(
                host.session.game.map.zone_at(smoke.target.x, smoke.target.y),
                ZoneType::Residential
            );
        }
        53 => assert!(host.session.preview.is_some()),
        54 => {
            assert!(host.session.preview.is_none());
            assert!(host.session.game.map.has_road(smoke.target.x, smoke.target.y));
        }
        57 => {
            let json = std::fs::read_to_string(&host.session.save_path).expect("quick-save exists");
            let loaded = SaveGameStore::deserialize(&json).expect("quick-save round trip");
            assert_eq!(json, SaveGameStore::serialize(&loaded).unwrap());
        }
        59 => assert!(host.session.autosave_path(1).unwrap().exists()),
        61 => assert_eq!(host.session.zoom_level, -2),
        64 => assert_eq!(host.session.zoom_level, 1),
        65 => assert_eq!(host.session.prompt.as_ref().unwrap().kind, PromptKind::Guide),
        66 => assert!(host.session.prompt.is_none()),
        70 => assert_eq!((host.grid.columns, host.grid.rows), (80, 23)),
        76 => assert_ne!(smoke.camera, Pos::new(host.session.camera_x, host.session.camera_y)),
        84 | 85 => {
            assert_eq!(smoke.animation_seconds, host.animation_seconds);
            assert_eq!(smoke.elapsed_days, host.session.game.elapsed_days());
        }
        94 => {
            assert!(!host.session.game.paused);
            assert!(host.session.game.elapsed_days() > smoke.elapsed_days);
        }
        95 => assert!(host.session.game.paused),
        101 | 106 => {
            assert_eq!(host.session.prompt.as_ref().unwrap().kind, PromptKind::ProgressGuard);
        }
        103 | 108 => assert!(host.session.prompt.is_none(), "cancel keeps the window open"),
        112 | 118 => {
            let window = windows.single().unwrap();
            if smoke.frame == 112 {
                assert_eq!(window.scale_factor(), 1.5);
            }
            assert_eq!(host.grid, termcity_bevy::Grid::new(window.width(), window.height()));
            let (camera, transform) = cameras.single().unwrap();
            let screen = Vec2::new(CELL_WIDTH * 2.5, HEADER_HEIGHT + CELL_HEIGHT * 2.5);
            let world = camera.viewport_to_world_2d(transform, screen).expect("camera DPI mapping");
            let (x, y) = host.grid.center(2, 2);
            assert!(
                world.distance(Vec2::new(x, y)) < 0.01,
                "input and rendered cells must agree at either DPI"
            );
        }
        200 if smoke.screenshot.is_some() => {
            commands.spawn(Screenshot::primary_window()).observe(save_capture);
        }
        _ => {}
    }
    if smoke.frame > 120 {
        smoke.frame_ms.push(time.delta_secs_f64() * 1000.0);
        smoke.update_ms.push(host.update_ms);
    }
    if smoke.frame >= smoke.frames {
        smoke.frame_ms.sort_by(f64::total_cmp);
        smoke.update_ms.sort_by(f64::total_cmp);
        let count = smoke.frame_ms.len();
        let average = smoke.frame_ms.iter().sum::<f64>() / count as f64;
        let p95 = smoke.frame_ms[count * 95 / 100];
        let update_p95 = smoke.update_ms[count * 95 / 100];
        if p95 > 16.7 {
            warn!(
                "Measured frame p95 exceeds the 16.7 ms goal; presentation timing excludes other engine/GPU work"
            );
        }
        if smoke.screenshot.is_some() {
            assert!(smoke.screenshot_saved, "this run must capture and save the native screenshot");
        }
        let directory = host.session.save_path.parent().unwrap();
        std::fs::remove_file(&host.session.save_path).expect("remove owned smoke save");
        for slot in 1..=GameSession::<termcity_core::DiskFileSystem>::AUTOSAVE_SLOTS {
            let path = host.session.autosave_path(slot).unwrap();
            if path.exists() {
                std::fs::remove_file(path).expect("remove owned smoke autosave");
            }
        }
        let pending = host.session.autosave_path(1).unwrap().with_extension("json.pending");
        if pending.exists() {
            std::fs::remove_file(pending).expect("remove owned smoke recovery file");
        }
        std::fs::remove_dir(directory).expect("remove empty owned smoke directory");
        println!(
            "TERMCITY_BEVY_SMOKE_OK map={}x{} visible={} entities={} rebuilds={} frames={} \
             uncapped={} average_fps={:.1} frame_p95_ms={p95:.2} presentation_p95_ms={update_p95:.2}",
            host.session.game.map.width(),
            host.session.game.map.height(),
            host.cache.cells.len(),
            tiles.iter().count(),
            host.cache.rebuilds,
            count,
            smoke.uncapped,
            1000.0 / average,
        );
        exit.write(AppExit::Success);
    }
}

fn save_capture(capture: On<ScreenshotCaptured>, mut smoke: ResMut<Smoke>) {
    let path = smoke.screenshot.as_ref().expect("requested native screenshot");
    capture
        .image
        .clone()
        .try_into_dynamic()
        .expect("convert native screenshot")
        .save(path)
        .expect("save native screenshot");
    smoke.screenshot_saved = true;
}
