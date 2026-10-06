use std::io;
use std::path::{Path, PathBuf};
use std::time::Duration;

use crossterm::event::{
    Event, KeyCode, KeyEvent, KeyEventKind, KeyEventState, KeyModifiers, MouseButton, MouseEvent,
    MouseEventKind,
};
use ratatui::style::Color;
use ratatui::{Terminal, backend::TestBackend, buffer::Buffer, layout::Rect};
use termcity_app::{
    App, CliOptions, DEFAULT_FPS, HELP, MapSize, RedrawRequest, Scheduler, SchedulerConfig,
    dump_map, parse_args, render, run_cli, screen_layout,
};
use termcity_core::{
    CellRenderer, GameSession, GameSpeed, MessageKind, Pos, SessionFileSystem, ZoneType,
};
use termcity_test_support::{buffer_text, changed_cells, flat_game, memory_session};

fn key(code: KeyCode, modifiers: KeyModifiers) -> Event {
    Event::Key(KeyEvent { code, modifiers, kind: KeyEventKind::Press, state: KeyEventState::NONE })
}

fn mouse(kind: MouseEventKind, column: u16, row: u16, modifiers: KeyModifiers) -> Event {
    Event::Mouse(MouseEvent { kind, column, row, modifiers })
}

fn rendered(app: &App<termcity_test_support::MemoryFileSystem>, width: u16, height: u16) -> Buffer {
    let backend = TestBackend::new(width, height);
    let mut terminal = Terminal::new(backend).expect("test terminal");
    terminal.draw(|frame| render(frame, app)).expect("render");
    terminal.backend().buffer().clone()
}

fn minimap_marker(buffer: &Buffer) -> Rect {
    let minimap = screen_layout(buffer.area).minimap.inner(ratatui::layout::Margin::new(1, 1));
    let mut highlighted = Vec::new();
    for y in minimap.y..minimap.bottom() {
        for x in minimap.x..minimap.right() {
            if buffer[(x, y)].bg == Color::White {
                highlighted.push((x, y));
            }
        }
    }
    let left = highlighted.iter().map(|&(x, _)| x).min().expect("marker has width");
    let top = highlighted.iter().map(|&(_, y)| y).min().expect("marker has height");
    let right = highlighted.iter().map(|&(x, _)| x).max().expect("marker has width");
    let bottom = highlighted.iter().map(|&(_, y)| y).max().expect("marker has height");
    let marker = Rect::new(left, top, right - left + 1, bottom - top + 1);
    assert_eq!(highlighted.len(), usize::from(marker.width) * usize::from(marker.height));
    marker
}

#[test]
fn minimap_marker_keeps_its_size_when_scrolling_at_every_zoom() {
    for (terminal_width, terminal_height, map_width, map_height) in
        [(100, 30, 160, 96), (81, 25, 83, 29), (140, 40, 640, 384), (220, 60, 80, 24)]
    {
        let mut app =
            App::new(memory_session(map_width, map_height, 17), terminal_width, terminal_height);
        let draw = screen_layout(Rect::new(0, 0, terminal_width, terminal_height))
            .minimap
            .inner(ratatui::layout::Margin::new(1, 1));
        for zoom in -2..=1 {
            app.session.set_zoom(zoom, None);
            app.session.scroll_camera(-map_width, -map_height);
            let initial = minimap_marker(&rendered(&app, terminal_width, terminal_height));
            assert_eq!((initial.x, initial.y), (draw.x, draw.y));
            assert_eq!(
                i32::from(initial.width),
                (app.session.visible_cells_x().min(map_width) * i32::from(draw.width) + map_width
                    - 1)
                    / map_width
            );
            assert_eq!(
                i32::from(initial.height),
                (app.session.visible_cells_y().min(map_height) * i32::from(draw.height)
                    + map_height
                    - 1)
                    / map_height
            );
            for offset in [1, 2, 3, 5, 9, 17, 33, 67, map_width.max(map_height)] {
                app.session.scroll_camera(offset, offset);
                let marker = minimap_marker(&rendered(&app, terminal_width, terminal_height));
                assert_eq!(
                    (marker.width, marker.height),
                    (initial.width, initial.height),
                    "marker changed size at zoom {zoom}, camera ({}, {})",
                    app.session.camera_x,
                    app.session.camera_y
                );
                assert!(marker.x >= draw.x && marker.right() <= draw.right());
                assert!(marker.y >= draw.y && marker.bottom() <= draw.bottom());
            }
            if zoom >= 0 {
                let end = minimap_marker(&rendered(&app, terminal_width, terminal_height));
                assert_eq!((end.right(), end.bottom()), (draw.right(), draw.bottom()));
            }
            app.session.scroll_camera(-map_width, -map_height);
            assert_eq!(minimap_marker(&rendered(&app, terminal_width, terminal_height)), initial);
        }
    }
}

#[test]
fn cli_parses_all_options_and_signed_seed() {
    let parsed = parse_args(
        [
            "--seed",
            "-2147483648",
            "--size",
            "80X24",
            "--fps",
            "60",
            "--load",
            "city.json",
            "--dump-map",
        ]
        .map(str::to_owned),
    )
    .expect("valid CLI");
    assert_eq!(parsed.seed, Some(i32::MIN));
    assert_eq!(parsed.size, MapSize { width: 80, height: 24 });
    assert_eq!(parsed.fps, 60);
    assert_eq!(parsed.load, Some(Some(std::path::PathBuf::from("city.json"))));
    assert!(parsed.dump_map);

    let defaults = parse_args(Vec::<String>::new()).expect("defaults");
    assert_eq!(defaults.size, MapSize::default());
    assert_eq!(defaults.fps, DEFAULT_FPS);
}

#[test]
fn cli_rejects_invalid_arguments_with_exit_two() {
    for arguments in [
        vec!["--seed", "2147483648"],
        vec!["--size", "79x24"],
        vec!["--size", "640x385"],
        vec!["--fps", "4"],
        vec!["--fps", "61"],
        vec!["--wat"],
    ] {
        let mut output = Vec::new();
        let mut error = Vec::new();
        let code =
            run_cli(arguments.into_iter().map(str::to_owned), &mut output, &mut error, false);
        assert_eq!(code, 2);
        assert!(String::from_utf8(error).expect("UTF-8").contains("Usage:"));
    }
}

#[test]
fn help_and_dump_work_without_a_terminal() {
    let mut output = Vec::new();
    let mut error = Vec::new();
    assert_eq!(run_cli(["--help"].map(str::to_owned), &mut output, &mut error, false), 0);
    assert_eq!(String::from_utf8(output).expect("UTF-8").trim(), HELP);
    assert_eq!(error, Vec::<u8>::new());

    let mut output = Vec::new();
    let mut error = Vec::new();
    assert_eq!(
        run_cli(
            ["--seed", "1", "--size", "80x24", "--dump-map"].map(str::to_owned),
            &mut output,
            &mut error,
            false,
        ),
        0
    );
    let text = String::from_utf8(output).expect("UTF-8");
    assert!(text.starts_with("Seed 1, 80x24\n"));
    assert_eq!(text.lines().count(), 25);
    assert_eq!(error, Vec::<u8>::new());
}

#[test]
fn interactive_play_requires_both_terminal_streams() {
    let mut output = Vec::new();
    let mut error = Vec::new();
    let code = run_cli(
        ["--seed", "4", "--size", "80x24"].map(str::to_owned),
        &mut output,
        &mut error,
        false,
    );
    assert_eq!(code, 1);
    assert!(String::from_utf8(error).expect("UTF-8").contains("interactive terminal"));
}

#[test]
fn dump_uses_cell_renderer_for_every_cell() {
    let game = flat_game(80, 24, 7);
    let mut output = Vec::new();
    dump_map(&game, &mut output).expect("dump");
    let text = String::from_utf8(output).expect("UTF-8");
    let first_row = text.lines().nth(1).expect("first map row");
    let expected =
        (0..game.map.width()).map(|x| CellRenderer::render(&game, x, 0).glyph).collect::<String>();
    assert_eq!(first_row, expected);
}

#[test]
fn layout_render_and_all_zoom_levels_use_real_map() {
    let session = memory_session(160, 96, 17);
    let mut app = App::new(session, 100, 30);
    let layout = screen_layout(Rect::new(0, 0, 100, 30));
    assert_eq!(layout.panel.width, 34);
    assert_eq!(layout.map.width, 66);
    let base = rendered(&app, 100, 30);
    let text = buffer_text(&base);
    assert!(text.contains("TERMCITY-RS"));
    assert!(text.contains("MINIMAP"));
    assert!(text.contains("DEMAND"));
    assert!(text.contains("Welcome to TermCity"));

    for level in -2..=1 {
        app.session.set_zoom(level, None);
        app.session.drain_events();
        let buffer = rendered(&app, 100, 30);
        assert!(buffer.content.iter().any(|cell| cell.symbol() == "·"));
        assert!(buffer_text(&buffer).contains(&app.session.zoom_label()));
    }
}

#[test]
fn movement_selection_zoning_speed_and_preview_keys_dispatch() {
    let session = memory_session(160, 96, 19);
    let mut app = App::new(session, 100, 30);
    let start = app.session.cursor;
    app.handle_event(&key(KeyCode::Right, KeyModifiers::NONE)).expect("move");
    assert_eq!(app.session.cursor, Pos::new(start.x + 1, start.y));
    app.handle_event(&key(KeyCode::Down, KeyModifiers::SHIFT)).expect("select");
    assert!(app.session.selection.is_some());
    app.handle_event(&key(KeyCode::Char('r'), KeyModifiers::NONE)).expect("zone");
    assert_eq!(
        app.session.game.map.zone_at(app.session.cursor.x, app.session.cursor.y),
        ZoneType::Residential
    );
    app.handle_event(&key(KeyCode::Char('1'), KeyModifiers::NONE)).expect("speed");
    assert_eq!(app.session.game.speed, GameSpeed::Slow);
    app.handle_event(&key(KeyCode::Char('b'), KeyModifiers::NONE)).expect("preview");
    assert!(app.session.preview.is_some());
    app.handle_event(&key(KeyCode::Esc, KeyModifiers::NONE)).expect("cancel");
    assert!(app.session.preview.is_none(), "{}", app.session.message);
}

#[test]
fn mouse_select_pan_wheel_zoom_and_right_click_are_supported() {
    let session = memory_session(160, 96, 23);
    let mut app = App::new(session, 100, 30);
    let layout = screen_layout(Rect::new(0, 0, 100, 30));
    let map = BlockRect::from(layout.map);
    let x = map.x + 4;
    let y = map.y + 4;
    app.handle_event(&mouse(MouseEventKind::Down(MouseButton::Left), x, y, KeyModifiers::SHIFT))
        .expect("select down");
    app.handle_event(&mouse(
        MouseEventKind::Drag(MouseButton::Left),
        x + 3,
        y + 2,
        KeyModifiers::SHIFT,
    ))
    .expect("select drag");
    app.handle_event(&mouse(
        MouseEventKind::Up(MouseButton::Left),
        x + 3,
        y + 2,
        KeyModifiers::SHIFT,
    ))
    .expect("select up");
    assert!(app.session.selection.expect("selection").area() > 1);

    let old_zoom = app.session.zoom_level;
    app.handle_event(&mouse(MouseEventKind::ScrollUp, x, y, KeyModifiers::CONTROL))
        .expect("wheel zoom");
    assert_eq!(app.session.zoom_level, old_zoom + 1);

    app.handle_event(&mouse(MouseEventKind::Down(MouseButton::Right), x, y, KeyModifiers::NONE))
        .expect("context");
    assert!(app.context_visible);
    assert!(buffer_text(&rendered(&app, 100, 30)).contains("AREA ACTIONS"));
    for _ in 0..4 {
        app.handle_event(&key(KeyCode::Down, KeyModifiers::NONE)).expect("choose road");
    }
    app.handle_event(&key(KeyCode::Enter, KeyModifiers::NONE)).expect("open road preview");
    assert!(app.session.preview.is_some());
}

#[test]
fn enabled_edge_scroll_uses_latest_pointer_position() {
    let session = memory_session(160, 96, 27);
    let mut app = App::new(session, 100, 30);
    app.session.center_on(Pos::new(80, 48));
    app.session.drain_events();
    app.handle_event(&key(KeyCode::Char('e'), KeyModifiers::NONE)).expect("enable edge scroll");
    let map = screen_layout(Rect::new(0, 0, 100, 30)).map;
    app.handle_event(&mouse(
        MouseEventKind::Moved,
        map.right().saturating_sub(2),
        map.y + map.height / 2,
        KeyModifiers::NONE,
    ))
    .expect("record pointer");
    let before = app.session.camera_x;
    app.update(Duration::from_secs(1)).expect("edge update");
    assert!(app.session.camera_x > before);
}

#[test]
fn selection_drag_edge_scrolls_without_hover_toggle_and_extends_endpoint() {
    let session = memory_session(160, 96, 28);
    let mut app = App::new(session, 100, 30);
    app.session.center_on(Pos::new(80, 48));
    app.session.drain_events();
    assert!(!app.session.edge_scroll_enabled);
    let map = screen_layout(Rect::new(0, 0, 100, 30)).map;
    let row = map.y + map.height / 2;
    let start_x = map.right().saturating_sub(5);
    let edge_x = map.right().saturating_sub(2);
    app.handle_event(&mouse(
        MouseEventKind::Down(MouseButton::Left),
        start_x,
        row,
        KeyModifiers::SHIFT,
    ))
    .expect("start selection");
    app.handle_event(&mouse(
        MouseEventKind::Drag(MouseButton::Left),
        edge_x,
        row,
        KeyModifiers::SHIFT,
    ))
    .expect("drag selection");
    let before_camera = app.session.camera_x;
    let before_right = app.session.selection.expect("selection").right();

    app.update(Duration::from_secs(1)).expect("edge update");

    assert!(app.session.camera_x > before_camera);
    assert!(app.session.selection.expect("selection").right() > before_right);
}

struct BlockRect {
    x: u16,
    y: u16,
}

impl From<Rect> for BlockRect {
    fn from(value: Rect) -> Self {
        Self { x: value.x + 1, y: value.y + 1 }
    }
}

#[test]
fn help_prompt_confirmation_and_path_entry_block_background_actions() {
    let session = memory_session(160, 96, 29);
    let mut app = App::new(session, 100, 30);
    app.handle_event(&key(KeyCode::F(1), KeyModifiers::NONE)).expect("help");
    let cursor = app.session.cursor;
    app.handle_event(&key(KeyCode::Right, KeyModifiers::NONE)).expect("dismiss help");
    assert_eq!(app.session.cursor, cursor);
    assert!(!app.help_visible);

    app.handle_event(&key(KeyCode::F(10), KeyModifiers::NONE)).expect("menu");
    app.handle_event(&key(KeyCode::Right, KeyModifiers::NONE)).expect("modal blocks move");
    assert_eq!(app.session.cursor, cursor);
    assert!(buffer_text(&rendered(&app, 100, 30)).contains("City menu"));
    app.handle_event(&key(KeyCode::Esc, KeyModifiers::NONE)).expect("close menu");

    app.session.show_load_file_prompt();
    app.session.drain_events();
    app.handle_event(&key(KeyCode::Char('a'), KeyModifiers::CONTROL)).expect("clear path");
    app.handle_event(&key(KeyCode::Char('x'), KeyModifiers::NONE)).expect("type path");
    assert_eq!(app.session.prompt.as_ref().and_then(|prompt| prompt.input.as_deref()), Some("x"));
    app.handle_event(&key(KeyCode::Esc, KeyModifiers::NONE)).expect("close path");

    app.handle_event(&key(KeyCode::Char('d'), KeyModifiers::NONE)).expect("preview");
    let text = buffer_text(&rendered(&app, 100, 30));
    assert!(text.contains("Demolish?"));
    assert!(text.contains("YES"));
}

#[test]
fn modal_mouse_rows_match_rendered_confirmation_and_prompt_choices() {
    let session = memory_session(160, 96, 30);
    let mut app = App::new(session, 100, 30);
    app.handle_event(&key(KeyCode::Char('b'), KeyModifiers::NONE)).expect("preview");
    let confirmation_x = (100 - 56) / 2 + 10;
    let confirmation_y = (30 - 7) / 2 + 3;
    app.handle_event(&mouse(
        MouseEventKind::Down(MouseButton::Left),
        confirmation_x,
        confirmation_y,
        KeyModifiers::NONE,
    ))
    .expect("confirm click");
    assert!(app.session.preview.is_none(), "{}", app.session.message);

    app.handle_event(&key(KeyCode::F(10), KeyModifiers::NONE)).expect("menu");
    let prompt = app.session.prompt.as_ref().expect("prompt");
    let text_rows = u16::try_from(prompt.text.lines().count()).expect("small prompt");
    let content_rows = text_rows + 1 + u16::try_from(prompt.choices.len()).expect("small prompt");
    let popup_y = (30 - (content_rows + 2)) / 2;
    let first_choice_y = popup_y + 1 + text_rows + 1;
    app.handle_event(&mouse(
        MouseEventKind::Down(MouseButton::Left),
        20,
        first_choice_y,
        KeyModifiers::NONE,
    ))
    .expect("prompt click");
    assert!(app.session.prompt.is_none());
}

#[test]
fn closing_a_large_prompt_resets_selection_for_a_small_report() {
    let session = memory_session(160, 96, 32);
    let mut app = App::new(session, 100, 30);
    app.handle_event(&key(KeyCode::F(10), KeyModifiers::NONE)).expect("menu");
    for _ in 0..8 {
        app.handle_event(&key(KeyCode::Down, KeyModifiers::NONE)).expect("move choice");
    }
    assert_eq!(app.selected_prompt, 8);
    app.handle_event(&key(KeyCode::Esc, KeyModifiers::NONE)).expect("close menu");
    assert_eq!(app.selected_prompt, 0);
    app.handle_event(&key(KeyCode::F(7), KeyModifiers::NONE)).expect("report");
    app.handle_event(&key(KeyCode::Enter, KeyModifiers::NONE)).expect("close report");
    assert!(app.session.prompt.is_none());
}

#[derive(Clone, Default)]
struct FailingFileSystem;

impl SessionFileSystem for FailingFileSystem {
    fn read_to_string(&self, _path: &Path) -> io::Result<String> {
        Err(io::Error::new(io::ErrorKind::NotFound, "missing"))
    }

    fn write_atomic(&mut self, _path: &Path, _contents: &str) -> io::Result<()> {
        Err(io::Error::new(io::ErrorKind::PermissionDenied, "read-only"))
    }

    fn copy(&mut self, _source: &Path, _destination: &Path) -> io::Result<()> {
        Err(io::Error::new(io::ErrorKind::PermissionDenied, "read-only"))
    }

    fn exists(&self, _path: &Path) -> bool {
        false
    }
}

#[test]
fn recoverable_save_errors_stay_in_the_game_and_show_feedback() {
    let session = GameSession::with_file_system(
        flat_game(160, 96, 33),
        PathBuf::from("read-only/quicksave-rust.json"),
        FailingFileSystem,
        false,
    );
    let mut app = App::new(session, 100, 30);

    let redraw = app
        .handle_event(&key(KeyCode::F(5), KeyModifiers::NONE))
        .expect("save failure is recoverable");

    assert_eq!(redraw, RedrawRequest::Full);
    assert_eq!(app.session.message_kind, MessageKind::Error);
    assert!(app.session.message.contains("Operation failed"));
    assert!(!app.should_quit);
}

#[test]
fn resize_warns_safely_and_recovers() {
    let session = memory_session(160, 96, 31);
    let mut app = App::new(session, 100, 30);
    app.handle_event(&Event::Resize(40, 10)).expect("small resize");
    assert!(buffer_text(&rendered(&app, 40, 10)).contains("TERMINAL TOO SMALL"));
    app.handle_event(&Event::Resize(100, 30)).expect("restore resize");
    assert!(buffer_text(&rendered(&app, 100, 30)).contains("CITY MAP"));
}

#[test]
fn sequential_diff_render_matches_fresh_full_render() {
    let session = memory_session(160, 96, 37);
    let mut app = App::new(session, 100, 30);
    let backend = TestBackend::new(100, 30);
    let mut terminal = Terminal::new(backend).expect("terminal");
    terminal.draw(|frame| render(frame, &app)).expect("initial render");
    let before = terminal.backend().buffer().clone();

    let redraw = app.handle_event(&key(KeyCode::Right, KeyModifiers::NONE)).expect("cursor event");
    assert_ne!(redraw, RedrawRequest::None);
    terminal.draw(|frame| render(frame, &app)).expect("diff render");
    let diff_result = terminal.backend().buffer().clone();
    let fresh = rendered(&app, 100, 30);
    assert_eq!(diff_result, fresh);
    assert!(changed_cells(&before, &diff_result) < 30);
}

#[test]
fn scheduler_separates_tick_camera_cursor_and_suppresses_stalls() {
    let mut scheduler = Scheduler::new(SchedulerConfig::for_fps(30), Duration::ZERO);
    assert_eq!(scheduler.advance(Duration::ZERO).redraw, RedrawRequest::Full);
    scheduler.request(RedrawRequest::Full);
    assert_eq!(scheduler.advance(Duration::from_millis(20)).redraw, RedrawRequest::None);
    assert_eq!(scheduler.advance(Duration::from_millis(34)).redraw, RedrawRequest::Full);
    scheduler.request(RedrawRequest::Cursor);
    assert_eq!(scheduler.advance(Duration::from_millis(49)).redraw, RedrawRequest::Cursor);
    assert!(scheduler.advance(Duration::from_millis(100)).update.is_some());
    let stalled = scheduler.advance(Duration::from_secs(2));
    assert!(stalled.stalled);
    assert!(stalled.update.is_none());
}

#[test]
fn cli_load_without_file_is_distinct_from_no_load() {
    let options = parse_args(["--load"].map(str::to_owned)).expect("load option");
    assert_eq!(options, CliOptions { load: Some(None), ..CliOptions::default() });
}
