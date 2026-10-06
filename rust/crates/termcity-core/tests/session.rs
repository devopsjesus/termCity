use std::cell::RefCell;
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::rc::Rc;

use termcity_core::{
    ActionOutcome, BuildingType, CellRect, ChangeCategory, CityGame, EdgeScroller, FeedbackEvent,
    GameConfig, GameContent, GameMap, GameSession, Household, MessageKind, PanelSection,
    PlacementKind, Pos, PromptKind, RegisteredType, Rgb, SaveGameStore, SessionAction,
    SessionEvent, SessionFileSystem, ZoneType,
};

fn game(width: i32, height: i32) -> CityGame {
    let config =
        GameConfig { map_width: width, map_height: height, seed: 17, ..GameConfig::default() };
    let map = GameMap::new(width, height, GameContent::default()).expect("valid test map");
    CityGame::from_map(config, map)
}

fn session() -> GameSession<MemoryFileSystem> {
    GameSession::with_file_system(
        game(160, 96),
        PathBuf::from("saves/quicksave.json"),
        MemoryFileSystem::default(),
        false,
    )
}

#[derive(Clone, Default)]
struct MemoryFileSystem(Rc<RefCell<MemoryFiles>>);

#[derive(Default)]
struct MemoryFiles {
    files: HashMap<PathBuf, String>,
    fail_write: Option<PathBuf>,
    fail_copy: Option<PathBuf>,
}

impl MemoryFileSystem {
    fn contents(&self, path: impl AsRef<Path>) -> Option<String> {
        self.0.borrow().files.get(path.as_ref()).cloned()
    }

    fn fail_copy_to(&self, path: impl Into<PathBuf>) {
        self.0.borrow_mut().fail_copy = Some(path.into());
    }

    fn clear_failures(&self) {
        let mut state = self.0.borrow_mut();
        state.fail_copy = None;
        state.fail_write = None;
    }
}

impl SessionFileSystem for MemoryFileSystem {
    fn read_to_string(&self, path: &Path) -> io::Result<String> {
        self.0
            .borrow()
            .files
            .get(path)
            .cloned()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "missing"))
    }

    fn write_atomic(&mut self, path: &Path, contents: &str) -> io::Result<()> {
        let mut state = self.0.borrow_mut();
        if state.fail_write.as_deref() == Some(path) {
            return Err(io::Error::new(io::ErrorKind::PermissionDenied, "blocked"));
        }
        state.files.insert(path.to_owned(), contents.to_owned());
        Ok(())
    }

    fn copy(&mut self, source: &Path, destination: &Path) -> io::Result<()> {
        let mut state = self.0.borrow_mut();
        if state.fail_copy.as_deref() == Some(destination) {
            return Err(io::Error::new(io::ErrorKind::PermissionDenied, "blocked"));
        }
        let contents = state
            .files
            .get(source)
            .cloned()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "missing"))?;
        state.files.insert(destination.to_owned(), contents);
        Ok(())
    }

    fn exists(&self, path: &Path) -> bool {
        self.0.borrow().files.contains_key(path)
    }
}

#[test]
fn zoom_levels_focus_and_coordinate_conversion_match_the_session_contract() {
    let mut session = session();
    session.set_viewport(80, 24);
    session.center_on(Pos::new(80, 48));
    let focus = session.screen_to_map(30, 10);
    session.zoom_by(1, Some((30, 10)));
    assert_eq!(session.screen_to_map(30, 10), focus);
    assert_eq!(
        (session.span_x(), session.visible_cells_x(), session.zoom_label()),
        (2, 40, "2x".to_owned())
    );

    session.set_zoom(-1, Some((30, 10)));
    assert_eq!((session.stride(), session.zoom_label()), (2, "0.5x".to_owned()));
    session.set_zoom(-2, None);
    assert_eq!((session.stride(), session.zoom_label()), (4, "0.25x".to_owned()));
    session.set_zoom(-20, None);
    assert_eq!(session.zoom_level, GameSession::<MemoryFileSystem>::MIN_ZOOM);
    assert_eq!(session.camera_x % 4, 0);
    assert_eq!(session.camera_y % 4, 0);
}

#[test]
fn movement_selection_drag_and_block_snapping_are_ui_independent() {
    let mut session = session();
    session.set_zoom(-2, None);
    let start = session.cursor;
    session.move_cursor(1, 0, false);
    assert_eq!(session.cursor.x, start.x + 4);
    session.begin_drag(Pos::new(20, 20));
    session.update_drag(Pos::new(29, 25));
    session.end_selection();
    assert_eq!(session.selection, Some(CellRect::new(20, 20, 12, 8)));
    session.select_cell(Pos::new(23, 22));
    assert_eq!(session.selection, Some(CellRect::new(20, 20, 4, 4)));

    session.clear_selection();
    session.move_cursor(1, 0, true);
    session.move_cursor(0, 1, true);
    assert_eq!(session.selection.expect("selection").area(), 64);
    session.move_cursor(1, 0, false);
    assert!(session.selection.is_none());
}

#[test]
fn camera_scrolling_panning_and_typed_changes_are_separate() {
    let mut session = session();
    session.set_viewport(80, 24);
    session.center_on(Pos::new(80, 48));
    session.drain_events();
    let anchor = session.screen_to_map(30, 10);
    session.pan_camera(anchor, 40, 12);
    assert_eq!(session.screen_to_map(40, 12), anchor);
    session.place_cursor(Pos::new(session.camera_x + 1, session.camera_y + 1));
    let events = session.drain_events();
    assert!(events.contains(&SessionEvent::Changed(ChangeCategory::Camera)));
    assert!(events.contains(&SessionEvent::Changed(ChangeCategory::Selection)));

    session.scroll_camera(-10_000, -10_000);
    assert_eq!((session.camera_x, session.camera_y), (0, 0));
    session.scroll_camera(10_000, 10_000);
    assert_eq!((session.camera_x, session.camera_y), (80, 72));
}

#[test]
fn edge_scroller_accelerates_carries_fractions_and_resets() {
    assert_eq!(EdgeScroller::zone(0, 10, 90, 28).depth_x, 3);
    assert_eq!(EdgeScroller::zone(2, 10, 90, 28).depth_x, 1);
    assert_eq!(EdgeScroller::zone(40, 27, 90, 28).depth_y, 2);
    assert_eq!(EdgeScroller::zone(3, 1, 7, 4).dx, 0);

    let mut scroller = EdgeScroller::default();
    let mut total = 0;
    for _ in 0..25 {
        total += scroller.step(2, 10, 90, 28, 0.04).0;
    }
    assert!((-12..=-11).contains(&total));
    assert_eq!(scroller.step(40, 10, 90, 28, 1.0), (0, 0));
    assert_eq!(scroller.step(2, 10, 90, 28, 0.01).0, 0);
    let inner = EdgeScroller::default().step(2, 10, 90, 28, 1.0).0;
    let outer = EdgeScroller::default().step(0, 10, 90, 28, 1.0).0;
    assert!(outer < inner);
}

#[test]
fn previews_quote_blocked_cells_and_failed_confirmation_stays_open() {
    let mut session = session();
    let water = session.game.map.content().terrains.find("Water").expect("water").id();
    session.game.map.set_terrain(11, 18, water);
    assert!(
        session.game.designate(CellRect::single(Pos::new(12, 18)), ZoneType::Residential).success
    );
    session.notify_game_changed();
    session.begin_drag(Pos::new(10, 18));
    session.update_drag(Pos::new(13, 18));
    session.end_selection();
    session.preview_road(None);
    let preview = session.preview.as_ref().expect("preview");
    assert_eq!((preview.quote.cells, preview.quote.cost, preview.quote.skipped), (2, 1_000, 2));
    assert!(!preview.is_valid(&session.game, 11, 18));
    session.game.money = 1;
    assert!(!session.confirm_preview().expect("confirmation result").success);
    assert!(session.preview.is_some());
    assert_eq!(session.message_kind, MessageKind::Error);
}

#[test]
fn straight_road_line_uses_the_dominant_axis_and_identifies_gaps() {
    let mut session = session();
    let water = session.game.map.content().terrains.find("Water").expect("water").id();
    session.game.map.set_terrain(11, 18, water);
    session.notify_game_changed();
    session.place_cursor(Pos::new(10, 18));
    session.begin_road_line(None);
    session.update_drag(Pos::new(15, 19));
    assert_eq!(session.preview.as_ref().expect("line").area, CellRect::new(10, 18, 6, 1));
    assert!(session.preview.as_ref().expect("line").name.contains("gaps"));
    session.update_drag(Pos::new(11, 13));
    assert_eq!(session.preview.as_ref().expect("line").area, CellRect::new(10, 13, 1, 6));
}

#[test]
fn confirmation_creates_one_level_undo_and_preview_freezes_simulation() {
    let mut session = session();
    session.place_cursor(Pos::new(10, 18));
    let before = SaveGameStore::serialize(&session.game).expect("serialize");
    session.preview_road(None);
    session.update(0.5).expect("update");
    assert_eq!(SaveGameStore::serialize(&session.game).expect("serialize"), before);
    assert!(session.confirm_preview().expect("confirm").success);
    assert!(session.can_undo());
    session.request_undo();
    assert_eq!(session.prompt.as_ref().expect("prompt").kind, PromptKind::Undo);
    session.select_prompt(0).expect("undo");
    session.game.paused = false;
    assert_eq!(SaveGameStore::serialize(&session.game).expect("serialize"), before);
    assert!(!session.can_undo());
}

#[test]
fn undo_expires_after_seven_running_days_but_is_available_while_paused() {
    let mut session = session();
    session.place_cursor(Pos::new(10, 18));
    assert!(session.build_road(None).expect("road").success);
    for _ in 0..7 {
        session.game.advance_day();
    }
    assert!(session.can_undo());
    session.game.update(0.1);
    assert!(!session.can_undo());
    session.request_undo();
    assert!(session.prompt.is_none());
    session.game.paused = true;
    assert!(session.can_undo());
}

#[test]
fn demolition_and_building_previews_dispatch_to_existing_simulation() {
    let config = GameConfig { map_width: 160, map_height: 96, seed: 17, ..GameConfig::default() };
    let mut content = GameContent::default();
    let building = content
        .buildings
        .register(BuildingType::new(
            "Clinic",
            vec!["X"],
            Rgb::hex(0xff_ff_ff),
            ZoneType::None,
            1_000,
            true,
            "Clinic",
        ))
        .expect("register building")
        .clone();
    let map = GameMap::new(160, 96, content).expect("map");
    let mut session = GameSession::with_file_system(
        CityGame::from_map(config, map),
        PathBuf::from("saves/quicksave.json"),
        MemoryFileSystem::default(),
        false,
    );
    session.place_cursor(Pos::new(30, 30));
    session.preview_building(building);
    assert_eq!(session.preview.as_ref().expect("building").kind, PlacementKind::Building);
    assert!(session.confirm_preview().expect("building placement").success);
    session.place_cursor(Pos::new(30, 30));
    session.preview_demolish();
    assert_eq!(session.preview.as_ref().expect("demolition").quote.cells, 1);
    assert!(session.confirm_preview().expect("demolition").success);
}

#[test]
fn save_load_failures_are_explicit_and_success_pauses_loaded_game() {
    let mut session = session();
    session.place_cursor(Pos::new(10, 18));
    assert!(session.zone(ZoneType::Commercial).expect("zone").success);
    session.quick_save().expect("save");
    session.place_cursor(Pos::new(10, 18));
    assert!(session.demolish().expect("demolish").success);
    session.quick_load().expect("load");
    assert_eq!(session.game.stats().commercial.zoned, 1);
    assert!(session.game.paused);
    assert!(!session.can_undo());

    let missing = Path::new("missing.json");
    assert!(session.load_from(missing).is_err());
    assert_eq!(session.message_kind, MessageKind::Error);
    assert!(session.message.contains("Load failed"));
}

#[test]
fn autosaves_rotate_three_slots_without_overwriting_the_quicksave() {
    let mut session = session();
    session.quick_save().expect("quick save");
    let manual = session.file_system().contents(&session.save_path).expect("manual save");
    for money in 1..=4 {
        session.game.money = money;
        session.notify_game_changed();
        session.autosave().expect("autosave");
    }
    for (slot, money) in [(1, 4), (2, 3), (3, 2)] {
        let path = session.autosave_path(slot).expect("path");
        let json = session.file_system().contents(path).expect("autosave contents");
        assert_eq!(SaveGameStore::deserialize(&json).expect("autosave game").money, money);
    }
    assert_eq!(session.file_system().contents(&session.save_path), Some(manual));
}

#[test]
fn autosave_rotation_failure_preserves_newest_good_save_and_can_retry() {
    let mut session = session();
    session.autosave().expect("first");
    session.game.money = 42;
    session.notify_game_changed();
    session.autosave().expect("second");
    let newest_path = session.autosave_path(1).expect("newest path");
    let newest = session.file_system().contents(&newest_path).expect("newest");
    let third = session.autosave_path(3).expect("third path");
    session.file_system().fail_copy_to(third);
    session.game.money = 43;
    session.notify_game_changed();
    assert!(session.autosave().is_err());
    assert_eq!(session.file_system().contents(&newest_path), Some(newest));
    assert!(session.message.contains("Autosave failed"));
    session.file_system().clear_failures();
    session.autosave().expect("retry");
    let retried = session.file_system().contents(newest_path).expect("retried");
    assert_eq!(SaveGameStore::deserialize(&retried).expect("game").money, 43);
}

#[test]
fn elapsed_time_drives_autosave_and_message_expiry_without_hidden_clocks() {
    let mut session = session();
    session.game.paused = true;
    session.set_message("hello", MessageKind::Info);
    session.update(4.999).expect("update");
    assert!(session.message_visible());
    session.update(0.002).expect("update");
    assert!(!session.message_visible());
    assert!(session.file_system().contents(session.autosave_path(1).expect("path")).is_none());
    session.update(55.0).expect("autosave update");
    assert!(session.file_system().contents(session.autosave_path(1).expect("path")).is_some());
    session.update(60.0).expect("unchanged update");
    assert!(session.file_system().contents(session.autosave_path(2).expect("path")).is_none());
}

#[test]
fn progress_guard_supports_cancel_save_and_discard_with_typed_events() {
    let mut session = session();
    session.request_quit().expect("request quit");
    let prompt = session.prompt.as_ref().expect("quit prompt");
    assert_eq!(prompt.kind, PromptKind::ProgressGuard);
    assert_eq!(prompt.choices[0].label, "Save and quit");
    session.select_prompt(2).expect("cancel");
    assert!(!session.drain_events().contains(&SessionEvent::QuitRequested));

    session.request_quit().expect("request quit");
    session.select_prompt(0).expect("save and quit");
    assert!(session.drain_events().contains(&SessionEvent::QuitRequested));
    assert!(!session.has_unsaved_changes());

    session.game.advance_day();
    session.notify_game_changed();
    session.request_quit().expect("request quit");
    session.select_prompt(1).expect("discard");
    assert!(session.drain_events().contains(&SessionEvent::QuitRequested));
}

#[test]
fn restart_and_load_are_guarded_and_reset_undo() {
    let mut session = session();
    session.quick_save().expect("save");
    session.place_cursor(Pos::new(10, 18));
    assert!(session.build_road(None).expect("road").success);
    let seed = session.game.config.seed;
    session.request_new_city(true, 999).expect("request restart");
    session.select_prompt(2).expect("cancel restart");
    assert!(session.game.map.has_road(10, 18));
    session.request_new_city(true, 999).expect("request restart");
    session.select_prompt(1).expect("discard restart");
    assert_eq!(session.game.config.seed, seed);
    assert!(session.game.paused);
    assert!(!session.can_undo());
    assert!(session.guide_visible);
    assert_eq!(session.prompt.as_ref().expect("guide").kind, PromptKind::Guide);
}

#[test]
fn guide_reports_folded_panels_and_debug_switches_are_exposed_as_state() {
    let mut session = session();
    session.show_guide();
    assert!(session.guide_visible);
    session.select_prompt(1).expect("dismiss guide");
    assert!(session.game.guide_dismissed);
    session.show_report();
    assert!(session.prompt.as_ref().expect("report").text.contains("Next milestone: 100 people"));
    session.close_prompt();
    session.game.paused = true;
    session.show_growth_report();
    assert!(session.prompt.as_ref().expect("growth").text.contains("road-served vacancies"));
    assert!(session.prompt.as_ref().expect("growth").text.contains("Paused"));

    assert!(!session.is_collapsed(PanelSection::City));
    session.toggle_section(PanelSection::City);
    assert!(session.is_collapsed(PanelSection::City));
    session.toggle_edge_scroll();
    assert!(session.edge_scroll_enabled);
    session.record_loop_gap(3_100);
    session.toggle_input_debug();
    assert!(session.input_debug);
    assert_eq!(session.loop_worst_gap_ms, 0);
}

#[test]
fn milestone_unlock_and_week_feedback_are_typed() {
    let mut milestone_session = session();
    let home = milestone_session
        .game
        .map
        .content()
        .building_for_zone(ZoneType::Residential)
        .expect("home")
        .id();
    for x in 0..20 {
        milestone_session.game.map.set_zone(x, 21, ZoneType::Residential);
        milestone_session.game.map.set_building(x, 21, Some(home));
        milestone_session.game.map.set_household(
            x,
            21,
            Household { adults: 3, children: 2, seniors: 0 },
        );
    }
    milestone_session.game.touch();
    milestone_session.notify_game_changed();
    let events = milestone_session.drain_events();
    assert!(events.contains(&SessionEvent::Feedback(FeedbackEvent::PopulationMilestone(100))));
    assert!(milestone_session.message.contains("milestone: 100"));

    let mut unlock = session();
    for x in 0..10 {
        unlock.game.map.set_zone(x, 21, ZoneType::Residential);
        unlock.game.map.set_building(x, 21, Some(home));
        unlock.game.map.set_household(x, 21, Household { adults: 2, children: 0, seniors: 0 });
    }
    unlock.game.touch();
    unlock.notify_game_changed();
    assert!(unlock.drain_events().contains(&SessionEvent::Feedback(FeedbackEvent::ZonesUnlocked)));
}

#[test]
fn typed_action_dispatch_routes_controller_operations() {
    let mut session = session();
    let start = session.cursor;
    assert_eq!(
        session.dispatch(SessionAction::MoveCursor { dx: 1, dy: 0, extend: false }).expect("move"),
        ActionOutcome::None
    );
    assert_eq!(session.cursor, start.offset(1, 0));
    let outcome =
        session.dispatch(SessionAction::Zone(ZoneType::Residential)).expect("zone dispatch");
    assert!(matches!(outcome, ActionOutcome::GameAction(result) if result.success));
    session.dispatch(SessionAction::PreviewDemolish).expect("preview");
    assert_eq!(session.preview.as_ref().expect("preview").kind, PlacementKind::Demolish);
}

#[test]
fn menus_and_load_input_use_typed_prompt_actions() {
    let mut session = session();
    session.show_session_menu(1234);
    assert_eq!(session.prompt.as_ref().expect("menu").kind, PromptKind::SessionMenu);
    session.select_prompt(2).expect("load menu");
    assert_eq!(session.prompt.as_ref().expect("load menu").kind, PromptKind::LoadMenu);
    session.select_prompt(4).expect("load file");
    assert_eq!(session.prompt.as_ref().expect("load file").kind, PromptKind::LoadFile);
    session.set_prompt_input("");
    assert!(session.select_prompt(0).is_err());
    assert_eq!(session.message_kind, MessageKind::Error);
    assert!(session.message.contains("enter a save-file path"));
}
