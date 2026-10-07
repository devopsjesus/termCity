#![allow(
    clippy::cast_precision_loss,
    clippy::cast_possible_truncation,
    clippy::too_many_arguments,
    clippy::type_complexity,
    clippy::needless_pass_by_value,
    clippy::struct_excessive_bools
)]

mod font;
mod options;
mod smoke;

use std::error::Error;
use std::path::PathBuf;
use std::time::Instant;

use bevy::input::mouse::{MouseScrollUnit, MouseWheel};
use bevy::prelude::*;
use bevy::text::FontSize;
use bevy::window::{PresentMode, PrimaryWindow, WindowCloseRequested, WindowResolution};
use bevy::winit::WinitSettings;
use font::{AtlasData, FONT_BYTES, GlyphAtlas};
use options::Options;
use termcity_bevy::{
    CELL_HEIGHT, CELL_WIDTH, FOOTER_HEIGHT, Grid, HILL_CYCLE_SECONDS, MapCache, hill_offset,
    tree_offset,
};
use termcity_core::{
    CellRect, CityGame, GameSession, GameSpeed, MessageKind, PlacementPreview, Pos, Rgb,
    SaveGameStore, SessionAction, SessionError, SessionEvent, ZoneType, format_money,
};

#[derive(Resource)]
struct Host {
    session: GameSession,
    grid: Grid,
    cache: MapCache,
    drag: bool,
    pan: Option<Pos>,
    animation_seconds: f32,
    focused: bool,
    last_overlay: Option<(Pos, Option<CellRect>, Option<PlacementPreview>)>,
    pool_size: usize,
    update_started: Instant,
    update_ms: f64,
    movement_seconds: f64,
}

impl Host {
    fn new(game: CityGame, save_path: PathBuf) -> Self {
        let mut session = GameSession::new(game, save_path);
        session.set_message(
            "Welcome to TermCity Bevy! F6: guide; P: resume; F5: save.",
            MessageKind::Info,
        );
        Self {
            session,
            grid: Grid::new(0.0, 0.0),
            cache: MapCache::default(),
            drag: false,
            pan: None,
            animation_seconds: 0.0,
            focused: false,
            last_overlay: None,
            pool_size: 0,
            update_started: Instant::now(),
            update_ms: 0.0,
            movement_seconds: 0.0,
        }
    }

    fn result<T>(&mut self, result: Result<T, SessionError>) {
        if let Err(error) = result {
            error!("{error}");
            self.session.set_message(error.to_string(), MessageKind::Error);
        }
    }

    fn dispatch(&mut self, action: SessionAction) {
        let result = self.session.dispatch(action);
        self.result(result);
    }
}

#[derive(Component)]
struct Tile {
    index: usize,
    glyph: bool,
}

#[derive(Component)]
enum UiSlot {
    Header,
    Footer,
    Modal,
}

fn main() -> Result<(), Box<dyn Error>> {
    let Some(options) = Options::parse(std::env::args().skip(1))? else {
        println!(
            "TermCity Bevy: animated 2D Rust frontend\n\
             --seed INTEGER --size small|medium|large|WIDTHxHEIGHT\n\
             --smoke [--frames 300..3600] [--screenshot PATH.png]\n\
             --no-vsync: measure uncapped rendering performance\n\
             --font-license: print the bundled font license\n\
             Arrows: cursor; Shift+arrows: select; WASD/right-drag: pan; wheel/+/-: zoom\n\
             P: pause; 1/2/3: speed; R/C/I: zone; U: dezone; B: road preview; T: road line\n\
             Enter: confirm; Esc: cancel; F5: save; F6: guide; Q/window close: guarded quit"
        );
        return Ok(());
    };
    if options.license {
        println!("{}", font::FONT_LICENSE);
        return Ok(());
    }
    let mut game = CityGame::new(options.config)?;
    game.paused = true;
    let atlas = AtlasData::new(game.map.content())?;
    let save_path = if options.smoke {
        let directory =
            std::env::temp_dir().join(format!("termcity-bevy-smoke-{}", std::process::id()));
        std::fs::create_dir(&directory)?;
        directory.join("quicksave-bevy.json")
    } else {
        isolated_save_path()?
    };
    let smoke =
        smoke::Smoke::new(options.smoke, options.frames, options.screenshot, options.no_vsync);
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "TermCity - Bevy".into(),
                    resolution: WindowResolution::new(1200, 720),
                    present_mode: if options.no_vsync {
                        PresentMode::AutoNoVsync
                    } else {
                        PresentMode::AutoVsync
                    },
                    ..default()
                }),
                close_when_requested: false,
                ..default()
            })
            .set(ImagePlugin::default_nearest()),
    )
    .insert_resource(WinitSettings::game())
    .insert_resource(ClearColor(Color::srgb_u8(16, 19, 24)))
    .insert_resource(Host::new(game, save_path))
    .insert_resource(smoke);
    let atlas = app.world_mut().resource_scope(|world, mut images: Mut<Assets<Image>>| {
        atlas.into_assets(&mut images, &mut world.resource_mut::<Assets<TextureAtlasLayout>>())
    });
    app.insert_resource(atlas).add_systems(Startup, setup).add_systems(
        Update,
        (
            begin_frame,
            smoke::drive,
            input,
            update_session,
            refresh_scene,
            animate,
            update_ui,
            smoke::verify,
            end_frame,
        )
            .chain(),
    );
    let exit = app.run();
    if exit.is_error() {
        return Err("Bevy frontend failed; see the diagnostic output".into());
    }
    Ok(())
}

fn isolated_save_path() -> Result<PathBuf, Box<dyn Error>> {
    let terminal_path = SaveGameStore::default_path()?;
    let terminal_directory = terminal_path.parent().ok_or("terminal save path has no directory")?;
    Ok(terminal_directory.with_file_name("TermCityBevy").join("quicksave-bevy.json"))
}

fn setup(mut commands: Commands, mut fonts: ResMut<Assets<Font>>) {
    commands.spawn(Camera2d);
    let font = fonts.add(Font::from_bytes(FONT_BYTES.to_vec()));
    for (slot, node) in [
        (
            UiSlot::Header,
            Node {
                position_type: PositionType::Absolute,
                top: px(8),
                left: px(10),
                right: px(10),
                height: px(56),
                overflow: Overflow::clip(),
                ..default()
            },
        ),
        (
            UiSlot::Footer,
            Node {
                position_type: PositionType::Absolute,
                bottom: px(4),
                left: px(10),
                right: px(10),
                height: px(FOOTER_HEIGHT - 8.0),
                overflow: Overflow::clip(),
                ..default()
            },
        ),
        (
            UiSlot::Modal,
            Node {
                position_type: PositionType::Absolute,
                top: percent(12),
                left: percent(8),
                width: percent(84),
                max_height: percent(82),
                padding: UiRect::all(px(20)),
                border: UiRect::all(px(2)),
                display: Display::None,
                overflow: Overflow::clip(),
                ..default()
            },
        ),
    ] {
        let modal = matches!(slot, UiSlot::Modal);
        commands.spawn((
            Text::default(),
            TextFont { font: font.clone().into(), font_size: FontSize::Px(16.0), ..default() },
            TextColor(Color::srgb_u8(230, 235, 240)),
            node,
            BackgroundColor(if modal { Color::srgb_u8(25, 30, 40) } else { Color::NONE }),
            GlobalZIndex(if modal { 10 } else { 1 }),
            slot,
        ));
    }
}

fn begin_frame(mut host: ResMut<Host>) {
    host.update_started = Instant::now();
}

#[allow(clippy::too_many_lines)]
fn input(
    mut host: ResMut<Host>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    mut wheel: MessageReader<MouseWheel>,
    mut close: MessageReader<WindowCloseRequested>,
    windows: Query<&Window, With<PrimaryWindow>>,
    time: Res<Time<Real>>,
) {
    let Ok(window) = windows.single() else {
        return;
    };
    if close.read().next().is_some() {
        host.session.end_selection();
        host.drag = false;
        host.pan = None;
        if !host
            .session
            .prompt
            .as_ref()
            .is_some_and(|prompt| prompt.kind == termcity_core::PromptKind::ProgressGuard)
        {
            host.session.close_prompt();
            let result = host.session.request_quit();
            host.result(result);
        }
    }
    if !window.focused {
        wheel.clear();
        return;
    }
    if let Some(prompt) = &host.session.prompt {
        let count = prompt.choices.len();
        for (index, key) in [
            KeyCode::Digit1,
            KeyCode::Digit2,
            KeyCode::Digit3,
            KeyCode::Digit4,
            KeyCode::Digit5,
            KeyCode::Digit6,
            KeyCode::Digit7,
            KeyCode::Digit8,
            KeyCode::Digit9,
        ]
        .into_iter()
        .enumerate()
        {
            if index < count && keys.just_pressed(key) {
                host.dispatch(SessionAction::SelectPrompt(index));
                break;
            }
        }
        if keys.just_pressed(KeyCode::Escape) {
            host.dispatch(SessionAction::ClosePrompt);
        } else if count == 1 && keys.just_pressed(KeyCode::Enter) {
            host.dispatch(SessionAction::SelectPrompt(0));
        }
        wheel.clear();
        if host.drag {
            host.session.end_selection();
        }
        host.drag = false;
        host.pan = None;
        return;
    }
    let shift = keys.pressed(KeyCode::ShiftLeft) || keys.pressed(KeyCode::ShiftRight);
    host.movement_seconds += time.delta_secs_f64();
    let repeat = host.movement_seconds >= 0.08;
    if repeat {
        host.movement_seconds = 0.0;
    }
    for (key, dx, dy) in [
        (KeyCode::ArrowLeft, -1, 0),
        (KeyCode::ArrowRight, 1, 0),
        (KeyCode::ArrowUp, 0, -1),
        (KeyCode::ArrowDown, 0, 1),
    ] {
        if keys.just_pressed(key) || (repeat && keys.pressed(key)) {
            host.dispatch(SessionAction::MoveCursor { dx, dy, extend: shift });
        }
    }
    for (key, dx, dy) in [
        (KeyCode::KeyA, -3, 0),
        (KeyCode::KeyD, 3, 0),
        (KeyCode::KeyW, 0, -3),
        (KeyCode::KeyS, 0, 3),
    ] {
        if keys.just_pressed(key) || (repeat && keys.pressed(key)) {
            host.dispatch(SessionAction::ScrollCharacters { dx, dy });
        }
    }
    let hit = window
        .cursor_position()
        .and_then(|pos| host.grid.hit_test(pos.x, pos.y))
        .filter(|&(x, y)| host.session.game.map.contains(host.session.screen_to_map(x, y)));
    for event in wheel.read() {
        if let Some(focus) = hit {
            let amount = match event.unit {
                MouseScrollUnit::Line => event.y,
                MouseScrollUnit::Pixel => event.y / 40.0,
            };
            if amount.abs() >= 0.25 {
                host.session.zoom_by(if amount > 0.0 { 1 } else { -1 }, Some(focus));
            }
        }
    }
    for (key, delta) in [
        (KeyCode::Equal, 1),
        (KeyCode::NumpadAdd, 1),
        (KeyCode::Minus, -1),
        (KeyCode::NumpadSubtract, -1),
    ] {
        if keys.just_pressed(key) {
            host.session.zoom_by(delta, hit);
        }
    }
    if let Some((x, y)) = hit {
        let position = host.session.screen_to_map(x, y);
        if mouse.just_pressed(MouseButton::Left) {
            host.session.begin_drag(position);
            host.drag = true;
        } else if mouse.pressed(MouseButton::Left) && host.drag && position != host.session.cursor {
            host.session.update_drag(position);
        }
        if mouse.just_pressed(MouseButton::Right) {
            host.pan = Some(position);
        } else if mouse.pressed(MouseButton::Right)
            && let Some(anchor) = host.pan
        {
            host.session.pan_camera(anchor, x, y);
        }
    }
    if mouse.just_released(MouseButton::Left) {
        host.session.end_selection();
        host.drag = false;
    }
    if mouse.just_released(MouseButton::Right) {
        host.pan = None;
    }
    for (key, action) in [
        (KeyCode::KeyP, SessionAction::TogglePause),
        (KeyCode::Digit1, SessionAction::SetSpeed(GameSpeed::Slow)),
        (KeyCode::Digit2, SessionAction::SetSpeed(GameSpeed::Medium)),
        (KeyCode::Digit3, SessionAction::SetSpeed(GameSpeed::Fast)),
        (KeyCode::KeyR, SessionAction::Zone(ZoneType::Residential)),
        (KeyCode::KeyC, SessionAction::Zone(ZoneType::Commercial)),
        (KeyCode::KeyI, SessionAction::Zone(ZoneType::Industrial)),
        (KeyCode::KeyU, SessionAction::Dezone),
        (KeyCode::KeyB, SessionAction::PreviewRoad(None)),
        (KeyCode::KeyT, SessionAction::BeginRoadLine(None)),
        (KeyCode::Enter, SessionAction::ConfirmPreview),
        (KeyCode::Escape, SessionAction::ClearSelection),
    ] {
        let preview_allows_action = host.session.preview.is_none()
            || !matches!(&action, SessionAction::Zone(_) | SessionAction::Dezone);
        if keys.just_pressed(key) && preview_allows_action {
            host.dispatch(action);
        }
    }
    if keys.just_pressed(KeyCode::F5) {
        let result = host.session.quick_save();
        host.result(result);
    }
    if keys.just_pressed(KeyCode::F6) {
        host.session.show_guide();
    }
    if keys.just_pressed(KeyCode::KeyQ) {
        let result = host.session.request_quit();
        host.result(result);
    }
}

fn update_session(
    mut host: ResMut<Host>,
    time: Res<Time<Real>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut exit: MessageWriter<AppExit>,
) {
    let Ok(window) = windows.single() else {
        return;
    };
    let focused = window.focused;
    if focused && host.focused {
        let elapsed = time.delta_secs_f64().min(0.5);
        host.animation_seconds = (host.animation_seconds + elapsed as f32) % HILL_CYCLE_SECONDS;
        let result = host.session.update(elapsed);
        host.result(result);
    } else if host.focused && !focused {
        host.session.end_selection();
        host.drag = false;
        host.pan = None;
    }
    host.focused = focused;
    for event in host.session.drain_events() {
        match event {
            SessionEvent::QuitRequested => {
                exit.write(AppExit::Success);
            }
            SessionEvent::Changed(_) | SessionEvent::Feedback(_) => {}
        }
    }
}

fn refresh_scene(
    mut commands: Commands,
    mut host: ResMut<Host>,
    atlas: Res<GlyphAtlas>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut tiles: Query<(Entity, &Tile, &mut Sprite, &mut Transform, &mut Visibility)>,
) {
    let Ok(window) = windows.single() else {
        return;
    };
    let grid = Grid::new(window.width(), window.height());
    let layout_changed = host.grid != grid;
    if layout_changed {
        host.grid = grid;
        if grid.columns > 0 && grid.rows > 0 {
            host.session.set_viewport(grid.columns, grid.rows);
        }
        host.cache.invalidate();
    }
    let host = &mut *host;
    let base_changed = host.cache.refresh(&host.session, grid);
    let overlay_changed = host.last_overlay.as_ref().is_none_or(|(cursor, selection, preview)| {
        *cursor != host.session.cursor
            || *selection != host.session.selection
            || *preview != host.session.preview
    });
    let size = host.cache.cells.len();
    if host.pool_size != size {
        for (entity, ..) in &mut tiles {
            commands.entity(entity).despawn();
        }
        for index in 0..size {
            for glyph in [false, true] {
                let (sprite, transform, visibility) = tile_appearance(host, &atlas, index, glyph);
                commands.spawn((Tile { index, glyph }, sprite, transform, visibility));
            }
        }
        host.pool_size = size;
    } else if base_changed || overlay_changed || layout_changed {
        for (_, tile, mut sprite, mut transform, mut visibility) in &mut tiles {
            let (new_sprite, new_transform, new_visibility) =
                tile_appearance(host, &atlas, tile.index, tile.glyph);
            *sprite = new_sprite;
            *transform = new_transform;
            *visibility = new_visibility;
        }
    }
    if overlay_changed {
        host.last_overlay =
            Some((host.session.cursor, host.session.selection, host.session.preview.clone()));
    }
}

fn color(rgb: Rgb) -> Color {
    Color::srgb_u8(rgb.r, rgb.g, rgb.b)
}

fn tile_appearance(
    host: &Host,
    atlas: &GlyphAtlas,
    index: usize,
    glyph: bool,
) -> (Sprite, Transform, Visibility) {
    let columns = usize::try_from(host.grid.columns).expect("nonnegative viewport");
    let column = i32::try_from(index % columns).expect("viewport column");
    let row = i32::try_from(index / columns).expect("viewport row");
    let (x, y) = host.grid.center(column, row);
    let transform = Transform::from_xyz(x, y, if glyph { 1.0 } else { 0.0 });
    let Some(cell) = host.cache.cells[index] else {
        return (Sprite::default(), transform, Visibility::Hidden);
    };
    let sprite = if glyph {
        let character = cell.visual.glyph.chars().next().expect("validated map glyph");
        let mut sprite = Sprite::from_atlas_image(
            atlas.image.clone(),
            TextureAtlas { layout: atlas.layout.clone(), index: atlas.indices[&character] },
        );
        sprite.color = color(cell.visual.foreground);
        sprite.custom_size = Some(Vec2::new(CELL_WIDTH, CELL_HEIGHT));
        sprite
    } else {
        let mut background = color(cell.visual.background);
        if host.session.selection.is_some_and(|selection| selection.intersects(cell.area)) {
            background = Color::srgb_u8(50, 80, 120);
        }
        if let Some(preview) = &host.session.preview
            && preview.area.intersects(cell.area)
        {
            background = if preview.is_valid(&host.session.game, cell.area.x, cell.area.y) {
                Color::srgb_u8(35, 100, 65)
            } else {
                Color::srgb_u8(120, 35, 40)
            };
        }
        if cell.area.contains(host.session.cursor) {
            background = Color::srgb_u8(90, 120, 160);
        }
        Sprite::from_color(background, Vec2::new(CELL_WIDTH, CELL_HEIGHT))
    };
    (sprite, transform, Visibility::Visible)
}

fn animate(host: Res<Host>, mut tiles: Query<(&Tile, &mut Transform)>) {
    if !host.focused {
        return;
    }
    let columns = usize::try_from(host.grid.columns).expect("nonnegative viewport");
    for (tile, mut transform) in &mut tiles {
        if !tile.glyph {
            continue;
        }
        let Some(cell) = host.cache.cells[tile.index] else {
            continue;
        };
        if !cell.animated_hill && !cell.animated_tree {
            continue;
        }
        let (x, y) = host.grid.center(
            i32::try_from(tile.index % columns).expect("viewport column"),
            i32::try_from(tile.index / columns).expect("viewport row"),
        );
        let stride = host.session.stride();
        let position = Pos::new(cell.area.x / stride, cell.area.y / stride);
        let (dx, dy) = if cell.animated_hill {
            hill_offset(host.animation_seconds, position)
        } else {
            tree_offset(host.animation_seconds, position)
        };
        let translation = Vec3::new(x + dx, y + dy, transform.translation.z);
        if transform.translation != translation {
            transform.translation = translation;
        }
    }
}

fn update_ui(host: Res<Host>, mut text: Query<(&UiSlot, &mut Text, &mut Node)>) {
    for (slot, mut text, mut node) in &mut text {
        let value = match slot {
            UiSlot::Header => format!(
                "TERMCITY / BEVY   {}   Week {} Day {}   {:?} {}   Zoom {}\n\
                 Population {}   Roads {}/{} connected   Income {}/week",
                format_money(host.session.game.money),
                host.session.game.week,
                host.session.game.day,
                host.session.game.speed,
                if host.session.game.paused { "PAUSED" } else { "RUNNING" },
                host.session.zoom_label(),
                host.cache.stats.population,
                host.cache.stats.connected_road_cells,
                host.cache.stats.road_cells,
                format_money(host.cache.stats.weekly_income),
            ),
            UiSlot::Footer => format!(
                "{}\nArrows/Shift: cursor/select | WASD/right-drag: pan | Wheel +/-: zoom | P: pause | 1/2/3: speed\n\
                 R/C/I: zone | U: dezone | B/T: road | Enter/Esc: confirm/cancel | F5: save | F6: guide | Q: quit",
                host.session.preview.as_ref().map_or_else(
                    || if host.session.message_visible() {
                        host.session.message.clone()
                    } else {
                        format!("Cursor ({},{})", host.session.cursor.x, host.session.cursor.y)
                    },
                    termcity_core::PlacementPreview::summary,
                ),
            ),
            UiSlot::Modal => {
                if let Some(prompt) = &host.session.prompt {
                    node.display = Display::Flex;
                    let choices = prompt
                        .choices
                        .iter()
                        .enumerate()
                        .map(|(index, choice)| format!("{}: {}", index + 1, choice.label))
                        .collect::<Vec<_>>()
                        .join("\n");
                    let error = if host.session.message_kind == MessageKind::Error
                        && host.session.message_visible()
                    {
                        format!("\n\nERROR: {}", host.session.message)
                    } else {
                        String::new()
                    };
                    format!(
                        "{}\n\n{}\n\n{}{error}\n\nEsc: cancel / close",
                        prompt.title, prompt.text, choices
                    )
                } else {
                    node.display = Display::None;
                    String::new()
                }
            }
        };
        if text.0 != value {
            text.0 = value;
        }
    }
}

fn end_frame(mut host: ResMut<Host>) {
    host.update_ms = host.update_started.elapsed().as_secs_f64() * 1000.0;
}

#[cfg(test)]
mod tests {
    use super::*;

    fn input_app() -> App {
        let mut game = CityGame::new(termcity_core::GameConfig { seed: 42, ..default() }).unwrap();
        game.paused = true;
        let mut host = Host::new(game, PathBuf::new());
        host.grid = Grid::new(960.0, 640.0);
        host.session.set_viewport(host.grid.columns, host.grid.rows);
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(host)
            .init_resource::<ButtonInput<KeyCode>>()
            .init_resource::<ButtonInput<MouseButton>>()
            .add_message::<MouseWheel>()
            .add_message::<WindowCloseRequested>()
            .add_systems(Update, (input, update_session).chain());
        app.world_mut().spawn((Window { focused: true, ..default() }, PrimaryWindow));
        app
    }

    fn key(app: &mut App, key: KeyCode) {
        let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
        keys.reset_all();
        keys.press(key);
        app.update();
    }

    #[test]
    fn frontend_storage_does_not_collide_with_terminal_saves() {
        let path = isolated_save_path().unwrap();
        assert_ne!(path, SaveGameStore::default_path().unwrap());
        assert_eq!(path.file_name().unwrap(), "quicksave-bevy.json");
        assert_eq!(path.parent().unwrap().file_name().unwrap(), "TermCityBevy");
    }

    #[test]
    fn prompt_digits_do_not_leak_into_simulation_speed_controls() {
        let mut app = input_app();
        key(&mut app, KeyCode::KeyQ);
        assert!(app.world().resource::<Host>().session.prompt.is_some());
        key(&mut app, KeyCode::Digit3);
        let host = app.world().resource::<Host>();
        assert!(host.session.prompt.is_none());
        assert_eq!(host.session.game.speed, GameSpeed::Medium);
        assert!(host.session.game.paused);
        assert!(app.world().resource::<Messages<AppExit>>().is_empty());
    }

    #[test]
    fn road_preview_blocks_zoning_until_canceled() {
        let mut app = input_app();
        let pos = {
            let mut host = app.world_mut().resource_mut::<Host>();
            let pos =
                CellRect::new(0, 0, host.session.game.map.width(), host.session.game.map.height())
                    .cells()
                    .find(|pos| host.session.game.can_build_on(pos.x, pos.y))
                    .unwrap();
            host.session.place_cursor(pos);
            host.session.preview_road(None);
            pos
        };
        key(&mut app, KeyCode::KeyR);
        assert_eq!(
            app.world().resource::<Host>().session.game.map.zone_at(pos.x, pos.y),
            ZoneType::None
        );
        key(&mut app, KeyCode::Escape);
        key(&mut app, KeyCode::KeyR);
        assert_eq!(
            app.world().resource::<Host>().session.game.map.zone_at(pos.x, pos.y),
            ZoneType::Residential
        );
    }

    #[test]
    fn closing_a_window_with_a_guide_open_still_guards_unsaved_progress() {
        let mut app = input_app();
        key(&mut app, KeyCode::F6);
        let window = app
            .world_mut()
            .query_filtered::<Entity, With<PrimaryWindow>>()
            .single(app.world())
            .unwrap();
        app.world_mut()
            .resource_mut::<Messages<WindowCloseRequested>>()
            .write(WindowCloseRequested { window });
        app.world_mut().resource_mut::<ButtonInput<KeyCode>>().reset_all();
        app.update();
        assert_eq!(
            app.world().resource::<Host>().session.prompt.as_ref().unwrap().kind,
            termcity_core::PromptKind::ProgressGuard
        );
        assert!(app.world().resource::<Messages<AppExit>>().is_empty());
    }

    #[test]
    fn save_failure_is_visible_and_does_not_exit_the_guard() {
        let mut app = input_app();
        key(&mut app, KeyCode::KeyQ);
        key(&mut app, KeyCode::Digit1);
        let host = app.world().resource::<Host>();
        assert_eq!(host.session.message_kind, MessageKind::Error);
        assert_ne!(host.session.message, "");
        assert_eq!(
            host.session.prompt.as_ref().unwrap().kind,
            termcity_core::PromptKind::ProgressGuard
        );
        assert!(app.world().resource::<Messages<AppExit>>().is_empty());
    }
}
