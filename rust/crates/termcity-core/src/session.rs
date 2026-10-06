#![allow(clippy::missing_errors_doc, clippy::struct_excessive_bools)]

use std::collections::HashSet;
use std::error::Error;
use std::fmt;
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use atomic_write_file::AtomicWriteFile;

use crate::{
    ActionResult, BuildingType, CellRect, CityGame, GameConfig, GameSpeed, GrowthDiagnostic,
    MILESTONES, MapError, PersistenceError, Pos, Quote, RoadType, SaveGameStore, ZoneType,
    format_money,
};

const SCROLL_MARGIN: i32 = 1;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MessageKind {
    Info,
    Success,
    Error,
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum PanelSection {
    Demand,
    City,
    Zones,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChangeCategory {
    Full,
    Camera,
    Selection,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum FeedbackEvent {
    PopulationMilestone(i32),
    ZonesUnlocked,
    WeeklyReport { week: i32 },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SessionEvent {
    Changed(ChangeCategory),
    Feedback(FeedbackEvent),
    QuitRequested,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PlacementKind {
    Road,
    Building,
    Demolish,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PlacementPreview {
    pub kind: PlacementKind,
    pub area: CellRect,
    pub quote: Quote,
    pub name: String,
    pub road: Option<RoadType>,
    pub building: Option<BuildingType>,
}

impl PlacementPreview {
    #[must_use]
    pub fn is_valid(&self, game: &CityGame, x: i32, y: i32) -> bool {
        match self.kind {
            PlacementKind::Road => game.can_place_road(x, y, self.road.as_ref()),
            PlacementKind::Building => game.can_build_on(x, y),
            PlacementKind::Demolish => {
                game.map.in_bounds(x, y)
                    && (game.map.has_road(x, y)
                        || game.map.zone_at(x, y) != ZoneType::None
                        || game.map.building_at(x, y).is_some()
                        || game.map.feature_at(x, y).is_some())
            }
        }
    }

    #[must_use]
    pub fn summary(&self) -> String {
        format!(
            "{}: {} valid, {} blocked, {}",
            self.name,
            self.quote.cells,
            self.quote.skipped,
            format_money(self.quote.cost)
        )
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PromptKind {
    Undo,
    ProgressGuard,
    SessionMenu,
    LoadMenu,
    LoadFile,
    Guide,
    Report,
    GrowthReport,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PromptAction {
    Close,
    ConfirmUndo,
    SaveAndContinue,
    ContinueWithoutSave,
    OpenLoadMenu,
    OpenLoadFile,
    LoadPromptInput,
    QuickSave,
    RequestUndo,
    RequestLoad(PathBuf),
    RequestNewCity { restart: bool, new_seed: i32 },
    ShowReport,
    ShowGuide,
    DismissGuide,
    RequestQuit,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PromptChoice {
    pub label: String,
    pub action: PromptAction,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SessionPrompt {
    pub kind: PromptKind,
    pub title: String,
    pub text: String,
    pub choices: Vec<PromptChoice>,
    pub input: Option<String>,
    pub footer: Option<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub enum PendingIntent {
    Quit,
    NewCity { config: GameConfig },
    Load { path: PathBuf },
}

#[derive(Clone, Debug, PartialEq)]
pub enum SessionAction {
    MoveCursor { dx: i32, dy: i32, extend: bool },
    JumpCursor { dx: i32, dy: i32, extend: bool },
    ScrollCamera { dx: i32, dy: i32 },
    ScrollCharacters { dx: i32, dy: i32 },
    SetZoom { level: i32, focus: Option<(i32, i32)> },
    TogglePause,
    SetSpeed(GameSpeed),
    ToggleSelection,
    ClearSelection,
    Zone(ZoneType),
    Dezone,
    BuildRoad(Option<RoadType>),
    Demolish,
    PlaceBuilding(BuildingType),
    PreviewRoad(Option<RoadType>),
    PreviewBuilding(BuildingType),
    PreviewDemolish,
    BeginRoadLine(Option<RoadType>),
    ConfirmPreview,
    CancelPreview,
    RequestUndo,
    SelectPrompt(usize),
    ClosePrompt,
}

#[derive(Clone, Debug, PartialEq)]
pub enum ActionOutcome {
    None,
    GameAction(ActionResult),
}

#[derive(Debug)]
pub enum SessionError {
    InvalidElapsed(f64),
    InvalidAutosaveSlot(usize),
    InvalidLoadPath,
    InvalidPromptChoice(usize),
    Persistence(PersistenceError),
    Io { operation: &'static str, path: PathBuf, source: io::Error },
    NewGame(MapError),
}

impl fmt::Display for SessionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidElapsed(value) => write!(formatter, "invalid elapsed time: {value}"),
            Self::InvalidAutosaveSlot(slot) => write!(formatter, "invalid autosave slot: {slot}"),
            Self::InvalidLoadPath => formatter.write_str("enter a save-file path"),
            Self::InvalidPromptChoice(index) => write!(formatter, "invalid prompt choice: {index}"),
            Self::Persistence(error) => error.fmt(formatter),
            Self::Io { operation, path, source } => {
                write!(formatter, "failed to {operation} '{}': {source}", path.display())
            }
            Self::NewGame(error) => error.fmt(formatter),
        }
    }
}

impl Error for SessionError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Persistence(error) => Some(error),
            Self::Io { source, .. } => Some(source),
            Self::NewGame(error) => Some(error),
            Self::InvalidElapsed(_)
            | Self::InvalidAutosaveSlot(_)
            | Self::InvalidLoadPath
            | Self::InvalidPromptChoice(_) => None,
        }
    }
}

impl From<PersistenceError> for SessionError {
    fn from(value: PersistenceError) -> Self {
        Self::Persistence(value)
    }
}

impl From<MapError> for SessionError {
    fn from(value: MapError) -> Self {
        Self::NewGame(value)
    }
}

pub trait SessionFileSystem {
    fn read_to_string(&self, path: &Path) -> io::Result<String>;
    fn write_atomic(&mut self, path: &Path, contents: &str) -> io::Result<()>;
    fn copy(&mut self, source: &Path, destination: &Path) -> io::Result<()>;
    fn exists(&self, path: &Path) -> bool;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct DiskFileSystem;

impl SessionFileSystem for DiskFileSystem {
    fn read_to_string(&self, path: &Path) -> io::Result<String> {
        fs::read_to_string(path)
    }

    fn write_atomic(&mut self, path: &Path, contents: &str) -> io::Result<()> {
        if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
            fs::create_dir_all(parent)?;
        }
        let mut file = AtomicWriteFile::options().open(path)?;
        file.write_all(contents.as_bytes())?;
        file.commit()
    }

    fn copy(&mut self, source: &Path, destination: &Path) -> io::Result<()> {
        fs::copy(source, destination).map(|_| ())
    }

    fn exists(&self, path: &Path) -> bool {
        path.exists()
    }
}

pub struct GameSession<F = DiskFileSystem> {
    pub game: CityGame,
    pub save_path: PathBuf,
    file_system: F,
    pub cursor: Pos,
    pub selection: Option<CellRect>,
    pub anchor: Option<Pos>,
    anchor_from_shift: bool,
    pub camera_x: i32,
    pub camera_y: i32,
    pub view_width: i32,
    pub view_height: i32,
    pub zoom_level: i32,
    pub message: String,
    pub message_kind: MessageKind,
    elapsed_ui_seconds: f64,
    message_at: f64,
    collapsed: HashSet<PanelSection>,
    pub edge_scroll_enabled: bool,
    pub input_debug: bool,
    pub loop_gap_ms: u64,
    pub loop_worst_gap_ms: u64,
    pub preview: Option<PlacementPreview>,
    pub road_tool_active: bool,
    road_anchor: Pos,
    line_road: Option<RoadType>,
    pub prompt: Option<SessionPrompt>,
    pending_intent: Option<PendingIntent>,
    pub guide_visible: bool,
    undo_snapshot: Option<String>,
    undo_at: f64,
    revision: u64,
    saved_revision: Option<u64>,
    saved_at: f64,
    autosave_revision: Option<u64>,
    autosave_at: f64,
    autosave_elapsed: f64,
    observed_homes: i32,
    observed_week: i32,
    observed_milestone: i32,
    events: Vec<SessionEvent>,
}

impl GameSession<DiskFileSystem> {
    pub fn new(game: CityGame, save_path: impl Into<PathBuf>) -> Self {
        Self::with_file_system(game, save_path, DiskFileSystem, false)
    }

    /// # Errors
    ///
    /// Returns an error if the platform default save path is unavailable.
    pub fn with_default_path(game: CityGame) -> Result<Self, SessionError> {
        Ok(Self::new(game, SaveGameStore::default_path()?))
    }
}

impl<F: SessionFileSystem> GameSession<F> {
    pub const MIN_ZOOM: i32 = -2;
    pub const MAX_ZOOM: i32 = 1;
    pub const MESSAGE_DURATION_SECONDS: f64 = 5.0;
    pub const AUTOSAVE_INTERVAL_SECONDS: f64 = 60.0;
    pub const AUTOSAVE_SLOTS: usize = 3;

    pub fn with_file_system(
        game: CityGame,
        save_path: impl Into<PathBuf>,
        file_system: F,
        show_guide: bool,
    ) -> Self {
        let cursor = Pos::new(game.map.width() / 2, game.map.height() / 2);
        let stats = game.stats();
        let mut session = Self {
            observed_homes: stats.residential.occupied(),
            observed_week: game.week,
            observed_milestone: game.highest_milestone,
            guide_visible: show_guide && !game.guide_dismissed,
            game,
            save_path: save_path.into(),
            file_system,
            cursor,
            selection: None,
            anchor: None,
            anchor_from_shift: false,
            camera_x: 0,
            camera_y: 0,
            view_width: 80,
            view_height: 24,
            zoom_level: 0,
            message: "Welcome to TermCity! Press F1 for help.".to_owned(),
            message_kind: MessageKind::Info,
            elapsed_ui_seconds: 0.0,
            message_at: 0.0,
            collapsed: HashSet::new(),
            edge_scroll_enabled: false,
            input_debug: false,
            loop_gap_ms: 0,
            loop_worst_gap_ms: 0,
            preview: None,
            road_tool_active: false,
            road_anchor: cursor,
            line_road: None,
            prompt: None,
            pending_intent: None,
            undo_snapshot: None,
            undo_at: 0.0,
            revision: 0,
            saved_revision: None,
            saved_at: 0.0,
            autosave_revision: None,
            autosave_at: 0.0,
            autosave_elapsed: 0.0,
            events: Vec::new(),
        };
        session.center_on(cursor);
        session.events.clear();
        if show_guide {
            session.game.paused = true;
            session.show_guide();
        }
        session
    }

    #[must_use]
    pub const fn stride(&self) -> i32 {
        if self.zoom_level < 0 { 1 << -self.zoom_level } else { 1 }
    }

    #[must_use]
    pub const fn span_x(&self) -> i32 {
        if self.zoom_level > 0 { 2 } else { 1 }
    }

    #[must_use]
    pub const fn visible_cells_x(&self) -> i32 {
        if self.zoom_level < 0 {
            self.view_width * self.stride()
        } else {
            (self.view_width + self.span_x() - 1) / self.span_x()
        }
    }

    #[must_use]
    pub const fn visible_cells_y(&self) -> i32 {
        if self.zoom_level < 0 { self.view_height * self.stride() } else { self.view_height }
    }

    #[must_use]
    pub fn zoom_label(&self) -> String {
        match self.zoom_level {
            -2 => "0.25x".to_owned(),
            -1 => "0.5x".to_owned(),
            1 => "2x".to_owned(),
            _ => "1x".to_owned(),
        }
    }

    #[must_use]
    pub const fn view_rect(&self) -> CellRect {
        CellRect::new(self.camera_x, self.camera_y, self.visible_cells_x(), self.visible_cells_y())
    }

    #[must_use]
    pub fn active_area(&self) -> CellRect {
        self.selection.unwrap_or_else(|| self.block_at(self.cursor))
    }

    #[must_use]
    pub fn block_at(&self, position: Pos) -> CellRect {
        let stride = self.stride();
        if stride == 1 {
            return CellRect::single(position);
        }
        let x = position.x / stride * stride;
        let y = position.y / stride * stride;
        CellRect::new(
            x,
            y,
            stride.min(self.game.map.width() - x),
            stride.min(self.game.map.height() - y),
        )
    }

    #[must_use]
    pub fn message_visible(&self) -> bool {
        !self.message.is_empty()
            && self.elapsed_ui_seconds - self.message_at < Self::MESSAGE_DURATION_SECONDS
    }

    #[must_use]
    pub fn has_unsaved_changes(&self) -> bool {
        self.saved_revision != Some(self.revision)
            || self.saved_at.to_bits() != self.game.elapsed_days().to_bits()
    }

    #[must_use]
    pub fn can_undo(&self) -> bool {
        self.undo_snapshot.is_some()
            && (self.game.paused || self.game.elapsed_days() - self.undo_at <= 7.0)
    }

    #[must_use]
    pub fn next_milestone(&self) -> Option<i32> {
        MILESTONES.iter().copied().find(|milestone| *milestone > self.game.highest_milestone)
    }

    #[must_use]
    pub fn is_collapsed(&self, section: PanelSection) -> bool {
        self.collapsed.contains(&section)
    }

    pub fn drain_events(&mut self) -> Vec<SessionEvent> {
        std::mem::take(&mut self.events)
    }

    #[must_use]
    pub const fn file_system(&self) -> &F {
        &self.file_system
    }

    pub const fn file_system_mut(&mut self) -> &mut F {
        &mut self.file_system
    }

    /// Records mutations made directly through [`GameSession::game`].
    pub fn notify_game_changed(&mut self) {
        self.mark_game_changed();
    }

    pub fn set_message(&mut self, message: impl Into<String>, kind: MessageKind) {
        self.message = message.into();
        self.message_kind = kind;
        self.message_at = self.elapsed_ui_seconds;
        self.changed(ChangeCategory::Full);
    }

    pub fn record_loop_gap(&mut self, gap_ms: u64) {
        self.loop_gap_ms = gap_ms;
        self.loop_worst_gap_ms = self.loop_worst_gap_ms.max(gap_ms);
    }

    pub fn toggle_input_debug(&mut self) {
        self.input_debug = !self.input_debug;
        self.loop_worst_gap_ms = 0;
        let message = if self.input_debug {
            "Input debug on: the last mouse and key events are shown here."
        } else {
            "Input debug off."
        };
        self.set_message(message, MessageKind::Info);
    }

    pub fn toggle_section(&mut self, section: PanelSection) {
        if !self.collapsed.remove(&section) {
            self.collapsed.insert(section);
        }
        self.changed(ChangeCategory::Full);
    }

    pub fn set_viewport(&mut self, width: i32, height: i32) {
        if width <= 0 || height <= 0 || (width == self.view_width && height == self.view_height) {
            return;
        }
        self.view_width = width;
        self.view_height = height;
        self.follow_cursor();
    }

    pub fn zoom_by(&mut self, delta: i32, focus: Option<(i32, i32)>) {
        let level = (self.zoom_level + delta).clamp(Self::MIN_ZOOM, Self::MAX_ZOOM);
        if level == self.zoom_level {
            self.set_message(
                if delta > 0 {
                    "Already zoomed in as far as it goes."
                } else {
                    "Already zoomed out as far as it goes."
                },
                MessageKind::Info,
            );
            return;
        }
        self.set_zoom(level, focus);
    }

    pub fn set_zoom(&mut self, level: i32, focus: Option<(i32, i32)>) {
        let (view_x, view_y) = focus.unwrap_or((self.view_width / 2, self.view_height / 2));
        let map_focus = self.screen_to_map(view_x, view_y);
        self.zoom_level = level.clamp(Self::MIN_ZOOM, Self::MAX_ZOOM);
        self.camera_x = map_focus.x - self.view_offset_x(view_x);
        self.camera_y = map_focus.y - self.view_offset_y(view_y);
        self.clamp_camera();
        self.changed(ChangeCategory::Camera);
    }

    pub fn scroll_camera(&mut self, dx: i32, dy: i32) {
        let before = (self.camera_x, self.camera_y);
        self.camera_x = self.camera_x.saturating_add(dx);
        self.camera_y = self.camera_y.saturating_add(dy);
        self.clamp_camera();
        if before != (self.camera_x, self.camera_y) {
            self.changed(ChangeCategory::Camera);
        }
    }

    pub fn scroll_characters(&mut self, dx: i32, dy: i32) {
        if self.zoom_level < 0 {
            self.scroll_camera(dx * self.stride(), dy * self.stride());
        } else {
            let x = dx.signum() * ((dx.abs() + self.span_x() - 1) / self.span_x());
            self.scroll_camera(x, dy);
        }
    }

    pub fn toggle_edge_scroll(&mut self) {
        self.edge_scroll_enabled = !self.edge_scroll_enabled;
        self.set_message(
            if self.edge_scroll_enabled { "Edge scrolling on." } else { "Edge scrolling off." },
            MessageKind::Info,
        );
    }

    pub fn pan_camera(&mut self, anchor: Pos, view_x: i32, view_y: i32) {
        let before = (self.camera_x, self.camera_y);
        self.camera_x = anchor.x - self.view_offset_x(view_x);
        self.camera_y = anchor.y - self.view_offset_y(view_y);
        self.clamp_camera();
        if before != (self.camera_x, self.camera_y) {
            self.changed(ChangeCategory::Camera);
        }
    }

    pub fn place_cursor(&mut self, position: Pos) {
        self.cursor = self.clamp_position(position);
        self.anchor = None;
        self.selection = None;
        self.changed(ChangeCategory::Selection);
    }

    pub fn center_on(&mut self, position: Pos) {
        self.camera_x = position.x - self.visible_cells_x() / 2;
        self.camera_y = position.y - self.visible_cells_y() / 2;
        self.clamp_camera();
        self.changed(ChangeCategory::Camera);
    }

    #[must_use]
    pub fn screen_to_map(&self, view_x: i32, view_y: i32) -> Pos {
        Pos::new(
            self.camera_x + self.view_offset_x(view_x),
            self.camera_y + self.view_offset_y(view_y),
        )
    }

    pub fn move_cursor(&mut self, dx: i32, dy: i32, extend: bool) {
        self.move_cursor_cells(dx * self.stride(), dy * self.stride(), extend);
    }

    pub fn jump_cursor(&mut self, dx: i32, dy: i32, extend: bool) {
        self.move_cursor_cells(
            dx * (self.visible_cells_x() - 1).max(1),
            dy * (self.visible_cells_y() - 1).max(1),
            extend,
        );
    }

    pub fn select_cell(&mut self, position: Pos) {
        self.cursor = self.clamp_position(position);
        self.anchor = None;
        self.selection = Some(self.block_at(self.cursor));
        self.follow_cursor();
    }

    pub fn begin_drag(&mut self, position: Pos) {
        self.cursor = self.clamp_position(position);
        if self.road_tool_active {
            self.road_anchor = self.cursor;
            self.refresh_road_line();
            return;
        }
        self.anchor = Some(self.cursor);
        self.anchor_from_shift = false;
        self.selection = Some(self.block_at(self.cursor));
        self.changed(ChangeCategory::Selection);
    }

    pub fn update_drag(&mut self, position: Pos) {
        self.cursor = self.clamp_position(position);
        if self.road_tool_active {
            self.refresh_road_line();
        } else if let Some(anchor) = self.anchor {
            self.selection = Some(self.make_selection(anchor, self.cursor));
            self.changed(ChangeCategory::Selection);
        }
    }

    pub fn end_selection(&mut self) {
        self.anchor = None;
        self.changed(ChangeCategory::Selection);
    }

    pub fn toggle_selection_mode(&mut self) {
        if self.anchor.is_none() {
            self.anchor = Some(self.cursor);
            self.anchor_from_shift = false;
            self.selection = Some(self.block_at(self.cursor));
            self.set_message(
                "Selecting: move with the arrow keys, S to finish, Enter for the menu.",
                MessageKind::Info,
            );
        } else {
            self.anchor = None;
            let message = self.selection.map_or_else(String::new, |selection| {
                format!(
                    "Selected {}x{} ({} cells).",
                    selection.width,
                    selection.height,
                    selection.area()
                )
            });
            self.set_message(message, MessageKind::Info);
        }
    }

    pub fn clear_selection(&mut self) {
        self.cancel_preview();
        self.anchor = None;
        self.selection = None;
        self.changed(ChangeCategory::Selection);
    }

    pub fn prepare_context_menu_at(&mut self, position: Pos) {
        if self.selection.is_some_and(|selection| selection.contains(position)) {
            self.cursor = self.clamp_position(position);
            self.anchor = None;
            self.changed(ChangeCategory::Selection);
        } else {
            self.select_cell(position);
        }
    }

    pub fn zone(&mut self, zone: ZoneType) -> Result<ActionResult, SessionError> {
        self.execute(|game, area| game.designate(area, zone))
    }

    pub fn dezone(&mut self) -> Result<ActionResult, SessionError> {
        self.execute(CityGame::dezone)
    }

    pub fn build_road(&mut self, road: Option<&RoadType>) -> Result<ActionResult, SessionError> {
        self.execute(|game, area| game.build_road(area, road))
    }

    pub fn demolish(&mut self) -> Result<ActionResult, SessionError> {
        self.execute(CityGame::demolish)
    }

    pub fn place_building(
        &mut self,
        building: &BuildingType,
    ) -> Result<ActionResult, SessionError> {
        self.execute(|game, area| game.place_building(building, area))
    }

    pub fn toggle_pause(&mut self) {
        self.game.paused = !self.game.paused;
        self.mark_game_changed();
        self.set_message(if self.game.paused { "Paused." } else { "Resumed." }, MessageKind::Info);
    }

    pub fn set_speed(&mut self, speed: GameSpeed) {
        self.game.speed = speed;
        self.game.paused = false;
        self.mark_game_changed();
        self.set_message(format!("Speed: {speed:?}."), MessageKind::Info);
    }

    pub fn preview_road(&mut self, road: Option<RoadType>) {
        let road = road.unwrap_or_else(|| self.game.default_road().clone());
        let area = self.active_area();
        self.preview = Some(PlacementPreview {
            kind: PlacementKind::Road,
            area,
            quote: self.game.quote_road(area, Some(&road)),
            name: road.name.clone(),
            road: Some(road),
            building: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn preview_building(&mut self, building: BuildingType) {
        let area = self.active_area();
        self.preview = Some(PlacementPreview {
            kind: PlacementKind::Building,
            area,
            quote: self.game.quote_building(&building, area),
            name: building.name.clone(),
            road: None,
            building: Some(building),
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn preview_demolish(&mut self) {
        let area = self.active_area();
        let mut preview = PlacementPreview {
            kind: PlacementKind::Demolish,
            area,
            quote: Quote::default(),
            name: "Demolish".to_owned(),
            road: None,
            building: None,
        };
        preview.quote.cells = i32::try_from(
            area.cells()
                .filter(|position| preview.is_valid(&self.game, position.x, position.y))
                .count(),
        )
        .unwrap_or(i32::MAX);
        preview.quote.skipped = area.area() - preview.quote.cells;
        self.preview = Some(preview);
        self.changed(ChangeCategory::Full);
    }

    pub fn begin_road_line(&mut self, road: Option<RoadType>) {
        self.road_tool_active = true;
        self.line_road = Some(road.unwrap_or_else(|| self.game.default_road().clone()));
        self.road_anchor = self.cursor;
        self.anchor = None;
        self.selection = None;
        self.refresh_road_line();
    }

    pub fn confirm_preview(&mut self) -> Result<ActionResult, SessionError> {
        let Some(preview) = self.preview.clone() else {
            let result = ActionResult {
                success: false,
                message: "There is no placement to confirm.".to_owned(),
                cost: 0,
                cells: 0,
            };
            self.complete(&result);
            return Ok(result);
        };
        let result = match preview.kind {
            PlacementKind::Road => {
                self.execute(|game, _| game.build_road(preview.area, preview.road.as_ref()))?
            }
            PlacementKind::Building => {
                if let Some(building) = preview.building {
                    self.execute(|game, _| game.place_building(&building, preview.area))?
                } else {
                    ActionResult {
                        success: false,
                        message: "No building type selected.".to_owned(),
                        cost: 0,
                        cells: 0,
                    }
                }
            }
            PlacementKind::Demolish => self.execute(|game, _| game.demolish(preview.area))?,
        };
        if result.success {
            self.cancel_preview();
        }
        Ok(result)
    }

    pub fn cancel_preview(&mut self) {
        if self.preview.is_none() && !self.road_tool_active {
            return;
        }
        self.preview = None;
        self.road_tool_active = false;
        self.line_road = None;
        self.changed(ChangeCategory::Full);
    }

    pub fn request_undo(&mut self) {
        if !self.can_undo() {
            let message = if self.undo_snapshot.is_none() {
                "Nothing to undo."
            } else {
                "Undo expired: more than 7 game days have passed. Pause to allow undo."
            };
            self.set_message(message, MessageKind::Error);
            return;
        }
        self.game.paused = true;
        self.cancel_preview();
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::Undo,
            title: "Undo last action".to_owned(),
            text: "Undo restores the entire city to immediately before the last successful action, including money, residents and time. The game will remain paused.".to_owned(),
            choices: vec![
                PromptChoice { label: "Undo".to_owned(), action: PromptAction::ConfirmUndo },
                PromptChoice { label: "Cancel".to_owned(), action: PromptAction::Close },
            ],
            input: None,
            footer: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn undo(&mut self) -> Result<(), SessionError> {
        let Some(snapshot) = self.undo_snapshot.take() else {
            self.set_message("Nothing to undo.", MessageKind::Error);
            return Ok(());
        };
        let content = self.game.map.content().clone();
        let restored = SaveGameStore::deserialize_with_content(&snapshot, content)?;
        self.replace_game(restored);
        self.game.paused = true;
        self.set_message("Last action undone; city restored and paused.", MessageKind::Success);
        Ok(())
    }

    pub fn update(&mut self, elapsed_seconds: f64) -> Result<(), SessionError> {
        if elapsed_seconds < 0.0 || !elapsed_seconds.is_finite() {
            return Err(SessionError::InvalidElapsed(elapsed_seconds));
        }
        self.elapsed_ui_seconds += elapsed_seconds;
        if self.prompt.is_none() && self.preview.is_none() {
            let before = self.game.elapsed_days();
            let version = self.game.map_version();
            self.game.update(elapsed_seconds);
            if before.to_bits() != self.game.elapsed_days().to_bits()
                || version != self.game.map_version()
            {
                self.mark_game_changed();
            }
        }
        self.autosave_elapsed += elapsed_seconds;
        if self.autosave_elapsed >= Self::AUTOSAVE_INTERVAL_SECONDS {
            self.autosave_elapsed = 0.0;
            if self.autosave_revision != Some(self.revision)
                || self.autosave_at.to_bits() != self.game.elapsed_days().to_bits()
            {
                self.autosave()?;
            }
        }
        Ok(())
    }

    pub fn quick_save(&mut self) -> Result<(), SessionError> {
        let serialized = SaveGameStore::serialize(&self.game)?;
        let path = self.save_path.clone();
        if let Err(source) = self.file_system.write_atomic(&path, &serialized) {
            let error = SessionError::Io { operation: "write save", path, source };
            self.set_message(format!("Save failed: {error}"), MessageKind::Error);
            return Err(error);
        }
        self.saved_revision = Some(self.revision);
        self.saved_at = self.game.elapsed_days();
        self.set_message(
            format!("Game saved to {}", self.save_path.display()),
            MessageKind::Success,
        );
        Ok(())
    }

    pub fn quick_load(&mut self) -> Result<(), SessionError> {
        let path = self.save_path.clone();
        self.load_from(&path)
    }

    pub fn load_from(&mut self, path: &Path) -> Result<(), SessionError> {
        let json = match self.file_system.read_to_string(path) {
            Ok(json) => json,
            Err(source) => {
                let error =
                    SessionError::Io { operation: "read save", path: path.to_owned(), source };
                self.set_message(format!("Load failed: {error}"), MessageKind::Error);
                return Err(error);
            }
        };
        let content = self.game.map.content().clone();
        let loaded = match SaveGameStore::deserialize_with_content(&json, content) {
            Ok(game) => game,
            Err(error) => {
                let error = SessionError::Persistence(error);
                self.set_message(format!("Load failed: {error}"), MessageKind::Error);
                return Err(error);
            }
        };
        self.replace_game(loaded);
        self.game.paused = true;
        self.saved_revision = Some(self.revision);
        self.saved_at = self.game.elapsed_days();
        self.set_message(
            format!("Loaded {}. The game is paused; press P to resume.", path.display()),
            MessageKind::Success,
        );
        Ok(())
    }

    pub fn autosave_path(&self, slot: usize) -> Result<PathBuf, SessionError> {
        if !(1..=Self::AUTOSAVE_SLOTS).contains(&slot) {
            return Err(SessionError::InvalidAutosaveSlot(slot));
        }
        let parent = self.save_path.parent().unwrap_or_else(|| Path::new(""));
        let stem =
            self.save_path.file_stem().and_then(|value| value.to_str()).unwrap_or("quicksave");
        Ok(parent.join(format!("{stem}.autosave{slot}.json")))
    }

    pub fn autosave(&mut self) -> Result<(), SessionError> {
        let serialized = SaveGameStore::serialize(&self.game)?;
        let pending = self.autosave_path(1)?.with_extension("json.pending");
        if let Err(error) = self.autosave_inner(&pending, &serialized) {
            self.set_message(format!("Autosave failed: {error}"), MessageKind::Error);
            return Err(error);
        }
        self.autosave_revision = Some(self.revision);
        self.autosave_at = self.game.elapsed_days();
        Ok(())
    }

    fn autosave_inner(&mut self, pending: &Path, serialized: &str) -> Result<(), SessionError> {
        self.file_system.write_atomic(pending, serialized).map_err(|source| SessionError::Io {
            operation: "write pending autosave",
            path: pending.to_owned(),
            source,
        })?;
        for slot in (2..=Self::AUTOSAVE_SLOTS).rev() {
            let source_path = self.autosave_path(slot - 1)?;
            if self.file_system.exists(&source_path) {
                let destination = self.autosave_path(slot)?;
                self.file_system.copy(&source_path, &destination).map_err(|source| {
                    SessionError::Io { operation: "rotate autosave", path: destination, source }
                })?;
            }
        }
        let newest = self.autosave_path(1)?;
        self.file_system.write_atomic(&newest, serialized).map_err(|source| SessionError::Io {
            operation: "promote autosave",
            path: newest,
            source,
        })
    }

    pub fn request_quit(&mut self) -> Result<(), SessionError> {
        self.guard_progress("Quit", PendingIntent::Quit)
    }

    pub fn request_new_city(&mut self, restart: bool, new_seed: i32) -> Result<(), SessionError> {
        let mut config = self.game.config.clone();
        if !restart {
            config.seed = new_seed;
        }
        self.guard_progress(
            if restart { "Restart this seed" } else { "New city" },
            PendingIntent::NewCity { config },
        )
    }

    pub fn request_load(&mut self, path: impl Into<PathBuf>) -> Result<(), SessionError> {
        let path = path.into();
        if path.as_os_str().is_empty() {
            self.set_message("Load failed: enter a save-file path.", MessageKind::Error);
            return Err(SessionError::InvalidLoadPath);
        }
        self.guard_progress("Load city", PendingIntent::Load { path })
    }

    pub fn show_session_menu(&mut self, new_seed: i32) {
        self.cancel_preview();
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::SessionMenu,
            title: "City menu".to_owned(),
            text: format!(
                "Seed {} | {}x{}",
                self.game.config.seed,
                self.game.map.width(),
                self.game.map.height()
            ),
            choices: vec![
                PromptChoice { label: "Back to city".to_owned(), action: PromptAction::Close },
                PromptChoice {
                    label: "Save quick-save".to_owned(),
                    action: PromptAction::QuickSave,
                },
                PromptChoice { label: "Load city".to_owned(), action: PromptAction::OpenLoadMenu },
                PromptChoice {
                    label: "New city (same map size)".to_owned(),
                    action: PromptAction::RequestNewCity { restart: false, new_seed },
                },
                PromptChoice {
                    label: "Restart this seed".to_owned(),
                    action: PromptAction::RequestNewCity { restart: true, new_seed },
                },
                PromptChoice {
                    label: "Undo last action".to_owned(),
                    action: PromptAction::RequestUndo,
                },
                PromptChoice {
                    label: "Weekly report / milestones".to_owned(),
                    action: PromptAction::ShowReport,
                },
                PromptChoice {
                    label: "First-city guide".to_owned(),
                    action: PromptAction::ShowGuide,
                },
                PromptChoice { label: "Quit".to_owned(), action: PromptAction::RequestQuit },
            ],
            input: None,
            footer: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn show_load_menu(&mut self) -> Result<(), SessionError> {
        let mut choices = vec![PromptChoice {
            label: "Quick-save".to_owned(),
            action: PromptAction::RequestLoad(self.save_path.clone()),
        }];
        for slot in 1..=Self::AUTOSAVE_SLOTS {
            choices.push(PromptChoice {
                label: format!("Autosave {slot} ({})", if slot == 1 { "newest" } else { "backup" }),
                action: PromptAction::RequestLoad(self.autosave_path(slot)?),
            });
        }
        choices.push(PromptChoice {
            label: "Enter a file path".to_owned(),
            action: PromptAction::OpenLoadFile,
        });
        choices.push(PromptChoice { label: "Cancel".to_owned(), action: PromptAction::Close });
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::LoadMenu,
            title: "Load city".to_owned(),
            text: "Choose a quick-save, autosave, or file path.".to_owned(),
            choices,
            input: None,
            footer: None,
        });
        self.changed(ChangeCategory::Full);
        Ok(())
    }

    pub fn show_load_file_prompt(&mut self) {
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::LoadFile,
            title: "Load file".to_owned(),
            text: "Enter a save-file path. Ctrl+A clears the field; Enter selects the highlighted button.".to_owned(),
            choices: vec![
                PromptChoice {
                    label: "Load".to_owned(),
                    action: PromptAction::LoadPromptInput,
                },
                PromptChoice { label: "Cancel".to_owned(), action: PromptAction::Close },
            ],
            input: Some(self.save_path.display().to_string()),
            footer: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn set_prompt_input(&mut self, input: impl Into<String>) {
        if let Some(prompt) = &mut self.prompt {
            prompt.input = Some(input.into());
            self.changed(ChangeCategory::Full);
        }
    }

    pub fn show_guide(&mut self) {
        self.guide_visible = true;
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::Guide,
            title: "Your first city".to_owned(),
            text: format!(
                "1. Connect a street to the highways (T draws a line).\n2. Zone nearby homes with R.\n3. Press P to resume. At {} occupied homes, shops and factories unlock.",
                self.game.config.min_residential_cells
            ),
            choices: vec![
                PromptChoice {
                    label: "Start building / keep guide".to_owned(),
                    action: PromptAction::Close,
                },
                PromptChoice {
                    label: "Dismiss guide".to_owned(),
                    action: PromptAction::DismissGuide,
                },
            ],
            input: None,
            footer: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn dismiss_guide(&mut self) {
        self.guide_visible = false;
        self.game.guide_dismissed = true;
        self.mark_game_changed();
        self.close_prompt();
    }

    pub fn show_report(&mut self) {
        let report = self.game.last_report.map_or_else(
            || "No completed week yet.".to_owned(),
            |report| {
                format!(
                    "Week {}: income {}\nNew homes {}, shops {}, factories {}",
                    report.week,
                    format_money(report.income),
                    report.new_households,
                    report.new_commercial,
                    report.new_industrial
                )
            },
        );
        let next = self.next_milestone().map_or_else(
            || "All population milestones reached.".to_owned(),
            |value| format!("Next milestone: {value} people"),
        );
        let stats = self.game.stats();
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::Report,
            title: "Weekly report and milestones".to_owned(),
            text: format!(
                "{report}\nPopulation: {}\nHighest milestone: {}\n{next}",
                stats.population, self.game.highest_milestone
            ),
            choices: vec![PromptChoice { label: "Close".to_owned(), action: PromptAction::Close }],
            input: None,
            footer: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn show_growth_report(&mut self) {
        let mut lines = Vec::new();
        for zone in [ZoneType::Residential, ZoneType::Commercial, ZoneType::Industrial] {
            let diagnostic = GrowthDiagnostic::for_zone(&self.game, zone);
            let name = zone.info().map_or("Unknown", |info| info.name);
            lines.push(format!("{name}: {} road-served vacancies", diagnostic.eligible_vacancies));
            lines.push(diagnostic.message);
            let leaving = self.game.stats().for_zone(zone).awaiting_removal;
            if leaving > 0 {
                lines.push(format!("{leaving} unzoned building(s) awaiting removal."));
            }
        }
        if self.game.paused {
            lines.push("Paused - press P after closing this report to resume.".to_owned());
        }
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::GrowthReport,
            title: "Growth and road access".to_owned(),
            text: lines.join("\n"),
            choices: vec![PromptChoice { label: "Close".to_owned(), action: PromptAction::Close }],
            input: None,
            footer: None,
        });
        self.changed(ChangeCategory::Full);
    }

    pub fn close_prompt(&mut self) {
        self.prompt = None;
        self.pending_intent = None;
        self.changed(ChangeCategory::Full);
    }

    pub fn select_prompt(&mut self, index: usize) -> Result<(), SessionError> {
        let action = self
            .prompt
            .as_ref()
            .and_then(|prompt| prompt.choices.get(index))
            .map(|choice| choice.action.clone())
            .ok_or(SessionError::InvalidPromptChoice(index))?;
        self.run_prompt_action(action)
    }

    pub fn dispatch(&mut self, action: SessionAction) -> Result<ActionOutcome, SessionError> {
        let result = match action {
            SessionAction::MoveCursor { dx, dy, extend } => {
                self.move_cursor(dx, dy, extend);
                None
            }
            SessionAction::JumpCursor { dx, dy, extend } => {
                self.jump_cursor(dx, dy, extend);
                None
            }
            SessionAction::ScrollCamera { dx, dy } => {
                self.scroll_camera(dx, dy);
                None
            }
            SessionAction::ScrollCharacters { dx, dy } => {
                self.scroll_characters(dx, dy);
                None
            }
            SessionAction::SetZoom { level, focus } => {
                self.set_zoom(level, focus);
                None
            }
            SessionAction::TogglePause => {
                self.toggle_pause();
                None
            }
            SessionAction::SetSpeed(speed) => {
                self.set_speed(speed);
                None
            }
            SessionAction::ToggleSelection => {
                self.toggle_selection_mode();
                None
            }
            SessionAction::ClearSelection => {
                self.clear_selection();
                None
            }
            SessionAction::Zone(zone) => Some(self.zone(zone)?),
            SessionAction::Dezone => Some(self.dezone()?),
            SessionAction::BuildRoad(road) => Some(self.build_road(road.as_ref())?),
            SessionAction::Demolish => Some(self.demolish()?),
            SessionAction::PlaceBuilding(building) => Some(self.place_building(&building)?),
            SessionAction::PreviewRoad(road) => {
                self.preview_road(road);
                None
            }
            SessionAction::PreviewBuilding(building) => {
                self.preview_building(building);
                None
            }
            SessionAction::PreviewDemolish => {
                self.preview_demolish();
                None
            }
            SessionAction::BeginRoadLine(road) => {
                self.begin_road_line(road);
                None
            }
            SessionAction::ConfirmPreview => Some(self.confirm_preview()?),
            SessionAction::CancelPreview => {
                self.cancel_preview();
                None
            }
            SessionAction::RequestUndo => {
                self.request_undo();
                None
            }
            SessionAction::SelectPrompt(index) => {
                self.select_prompt(index)?;
                None
            }
            SessionAction::ClosePrompt => {
                self.close_prompt();
                None
            }
        };
        Ok(result.map_or(ActionOutcome::None, ActionOutcome::GameAction))
    }

    fn execute(
        &mut self,
        action: impl FnOnce(&mut CityGame, CellRect) -> ActionResult,
    ) -> Result<ActionResult, SessionError> {
        let snapshot = SaveGameStore::serialize(&self.game)?;
        let at = self.game.elapsed_days();
        let area = self.active_area();
        let result = action(&mut self.game, area);
        if result.success {
            self.undo_snapshot = Some(snapshot);
            self.undo_at = at;
            self.anchor = None;
            self.selection = None;
            self.mark_game_changed();
        }
        self.complete(&result);
        Ok(result)
    }

    fn complete(&mut self, result: &ActionResult) {
        self.set_message(
            result.message.clone(),
            if result.success { MessageKind::Success } else { MessageKind::Error },
        );
    }

    fn move_cursor_cells(&mut self, dx: i32, dy: i32, extend: bool) {
        if self.road_tool_active {
            self.cursor = self.clamp_position(self.cursor.offset(dx, dy));
            self.refresh_road_line();
            self.follow_cursor();
            return;
        }
        if extend && self.anchor.is_none() {
            self.anchor = Some(self.cursor);
            self.anchor_from_shift = true;
        } else if !extend && self.anchor.is_some() && self.anchor_from_shift {
            self.anchor = None;
            self.selection = None;
        } else if !extend && self.anchor.is_none() {
            self.selection = None;
        }
        self.cursor = self.clamp_position(self.cursor.offset(dx, dy));
        if let Some(anchor) = self.anchor {
            self.selection = Some(self.make_selection(anchor, self.cursor));
        }
        self.follow_cursor();
    }

    fn make_selection(&self, first: Pos, second: Pos) -> CellRect {
        if self.stride() == 1 {
            return CellRect::from_corners(first, second);
        }
        let first = self.block_at(first);
        let second = self.block_at(second);
        let left = first.x.min(second.x);
        let top = first.y.min(second.y);
        let right = first.right().max(second.right());
        let bottom = first.bottom().max(second.bottom());
        CellRect::new(left, top, right - left + 1, bottom - top + 1)
    }

    fn refresh_road_line(&mut self) {
        let end = if (self.cursor.x - self.road_anchor.x).abs()
            >= (self.cursor.y - self.road_anchor.y).abs()
        {
            Pos::new(self.cursor.x, self.road_anchor.y)
        } else {
            Pos::new(self.road_anchor.x, self.cursor.y)
        };
        let area = CellRect::from_corners(self.road_anchor, end);
        let road = self.line_road.clone().unwrap_or_else(|| self.game.default_road().clone());
        let has_gap = area.cells().any(|position| {
            !self.game.can_place_road(position.x, position.y, Some(&road))
                && !self.game.map.has_road(position.x, position.y)
        });
        self.preview = Some(PlacementPreview {
            kind: PlacementKind::Road,
            area,
            quote: self.game.quote_road(area, Some(&road)),
            name: format!("{} line{}", road.name, if has_gap { " (gaps)" } else { "" }),
            road: Some(road),
            building: None,
        });
        self.changed(ChangeCategory::Full);
    }

    fn follow_cursor(&mut self) {
        let before = (self.camera_x, self.camera_y);
        let visible_x = self.visible_cells_x();
        let visible_y = self.visible_cells_y();
        let margin_x = (SCROLL_MARGIN * self.stride()).min(((visible_x - 1) / 2).max(0));
        let margin_y = (SCROLL_MARGIN * self.stride()).min(((visible_y - 1) / 2).max(0));
        if self.cursor.x < self.camera_x + margin_x {
            self.camera_x = self.cursor.x - margin_x;
        } else if self.cursor.x > self.camera_x + visible_x - 1 - margin_x {
            self.camera_x = self.cursor.x - (visible_x - 1 - margin_x);
        }
        if self.cursor.y < self.camera_y + margin_y {
            self.camera_y = self.cursor.y - margin_y;
        } else if self.cursor.y > self.camera_y + visible_y - 1 - margin_y {
            self.camera_y = self.cursor.y - (visible_y - 1 - margin_y);
        }
        self.clamp_camera();
        if before != (self.camera_x, self.camera_y) {
            self.changed(ChangeCategory::Camera);
        }
        self.changed(ChangeCategory::Selection);
    }

    fn clamp_camera(&mut self) {
        self.camera_x =
            self.camera_x.clamp(0, (self.game.map.width() - self.visible_cells_x()).max(0));
        self.camera_y =
            self.camera_y.clamp(0, (self.game.map.height() - self.visible_cells_y()).max(0));
        self.camera_x -= self.camera_x % self.stride();
        self.camera_y -= self.camera_y % self.stride();
    }

    fn clamp_position(&self, position: Pos) -> Pos {
        Pos::new(
            position.x.clamp(0, self.game.map.width() - 1),
            position.y.clamp(0, self.game.map.height() - 1),
        )
    }

    const fn view_offset_x(&self, view_x: i32) -> i32 {
        if self.zoom_level < 0 { view_x * self.stride() } else { view_x / self.span_x() }
    }

    const fn view_offset_y(&self, view_y: i32) -> i32 {
        if self.zoom_level < 0 { view_y * self.stride() } else { view_y }
    }

    fn changed(&mut self, category: ChangeCategory) {
        self.events.push(SessionEvent::Changed(category));
    }

    fn mark_game_changed(&mut self) {
        self.revision = self.revision.wrapping_add(1);
        self.update_feedback();
        self.changed(ChangeCategory::Full);
    }

    fn update_feedback(&mut self) {
        let stats = self.game.stats();
        let homes = stats.residential.occupied();
        let milestone = self.game.highest_milestone;
        if milestone > self.observed_milestone {
            self.events.push(SessionEvent::Feedback(FeedbackEvent::PopulationMilestone(milestone)));
            self.set_message(
                format!("Population milestone: {milestone}! F7 shows your progress."),
                MessageKind::Success,
            );
        } else if self.observed_homes < self.game.config.min_residential_cells
            && homes >= self.game.config.min_residential_cells
        {
            self.events.push(SessionEvent::Feedback(FeedbackEvent::ZonesUnlocked));
            self.set_message(
                format!(
                    "{} occupied homes: shops and factories unlocked. Zone with C and I.",
                    self.game.config.min_residential_cells
                ),
                MessageKind::Success,
            );
        } else if self.observed_week != self.game.week
            && self.game.last_report.is_some()
            && !self.message_visible()
        {
            let report = self.game.last_report.expect("checked above");
            self.events
                .push(SessionEvent::Feedback(FeedbackEvent::WeeklyReport { week: report.week }));
            self.set_message(
                format!(
                    "Week {}: +{} homes, +{} shops, +{} factories, {} tax. F7 report.",
                    report.week,
                    report.new_households,
                    report.new_commercial,
                    report.new_industrial,
                    format_money(report.income)
                ),
                MessageKind::Info,
            );
        }
        self.observed_homes = homes;
        self.observed_week = self.game.week;
        self.observed_milestone = milestone;
    }

    fn replace_game(&mut self, game: CityGame) {
        self.cancel_preview();
        self.prompt = None;
        self.pending_intent = None;
        self.undo_snapshot = None;
        self.game = game;
        let stats = self.game.stats();
        self.observed_homes = stats.residential.occupied();
        self.observed_week = self.game.week;
        self.observed_milestone = self.game.highest_milestone;
        self.guide_visible = !self.game.guide_dismissed;
        self.autosave_elapsed = 0.0;
        self.anchor = None;
        self.selection = None;
        self.cursor = self.clamp_position(self.cursor);
        self.clamp_camera();
        self.follow_cursor();
        self.mark_game_changed();
    }

    fn guard_progress(&mut self, title: &str, intent: PendingIntent) -> Result<(), SessionError> {
        if !self.has_unsaved_changes() {
            self.pending_intent = Some(intent);
            return self.continue_pending();
        }
        let quitting = matches!(intent, PendingIntent::Quit);
        self.pending_intent = Some(intent);
        self.prompt = Some(SessionPrompt {
            kind: PromptKind::ProgressGuard,
            title: title.to_owned(),
            text: if quitting {
                "Save your city before quitting? Autosaves are separate from your quick-save."
            } else {
                "Save your city before continuing? Autosaves are separate from your quick-save."
            }
            .to_owned(),
            choices: vec![
                PromptChoice {
                    label: if quitting { "Save and quit" } else { "Save and continue" }.to_owned(),
                    action: PromptAction::SaveAndContinue,
                },
                PromptChoice {
                    label: if quitting { "Quit without saving" } else { "Continue without saving" }
                        .to_owned(),
                    action: PromptAction::ContinueWithoutSave,
                },
                PromptChoice { label: "Cancel".to_owned(), action: PromptAction::Close },
            ],
            input: None,
            footer: Some(format!("Quick-save file: {}", self.save_path.display())),
        });
        self.changed(ChangeCategory::Full);
        Ok(())
    }

    fn run_prompt_action(&mut self, action: PromptAction) -> Result<(), SessionError> {
        match action {
            PromptAction::Close => self.close_prompt(),
            PromptAction::ConfirmUndo => self.undo()?,
            PromptAction::SaveAndContinue => {
                self.quick_save()?;
                self.continue_pending()?;
            }
            PromptAction::ContinueWithoutSave => self.continue_pending()?,
            PromptAction::QuickSave => {
                self.quick_save()?;
                self.close_prompt();
            }
            PromptAction::RequestUndo => self.request_undo(),
            PromptAction::RequestLoad(path) => self.request_load(path)?,
            PromptAction::RequestNewCity { restart, new_seed } => {
                self.request_new_city(restart, new_seed)?;
            }
            PromptAction::ShowReport => self.show_report(),
            PromptAction::ShowGuide => self.show_guide(),
            PromptAction::DismissGuide => self.dismiss_guide(),
            PromptAction::RequestQuit => self.request_quit()?,
            PromptAction::OpenLoadMenu => self.show_load_menu()?,
            PromptAction::OpenLoadFile => self.show_load_file_prompt(),
            PromptAction::LoadPromptInput => {
                let input = self
                    .prompt
                    .as_ref()
                    .and_then(|prompt| prompt.input.clone())
                    .unwrap_or_default();
                self.request_load(input.trim().trim_matches('"'))?;
            }
        }
        Ok(())
    }

    fn continue_pending(&mut self) -> Result<(), SessionError> {
        let intent = self.pending_intent.take();
        self.prompt = None;
        match intent {
            Some(PendingIntent::Quit) => self.events.push(SessionEvent::QuitRequested),
            Some(PendingIntent::Load { path }) => self.load_from(&path)?,
            Some(PendingIntent::NewCity { config }) => {
                let game = CityGame::new(config)?;
                self.replace_game(game);
                self.game.paused = true;
                self.guide_visible = true;
                self.show_guide();
            }
            None => {}
        }
        Ok(())
    }
}
