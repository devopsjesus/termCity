#![forbid(unsafe_code)]

use std::fmt;
use std::io::{self, IsTerminal, Write};
use std::panic;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crossterm::{
    cursor::Show,
    event::{
        self, DisableMouseCapture, EnableMouseCapture, Event as CrosstermEvent, KeyCode, KeyEvent,
        KeyEventKind, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
    },
    execute,
    terminal::{EnterAlternateScreen, LeaveAlternateScreen, disable_raw_mode, enable_raw_mode},
};
use ratatui::{
    Frame, Terminal,
    backend::CrosstermBackend,
    layout::{Alignment, Constraint, Layout, Rect},
    style::{Color, Modifier, Style},
    text::{Line, Span},
    widgets::{Block, Clear, Paragraph, Wrap},
};
use termcity_core::{
    CellInspector, CellRect, ChangeCategory, CityGame, CityStats, Demand, DiskFileSystem,
    EdgeScroller, GameConfig, GameSession, GameSpeed, MapRenderer, MessageKind, PanelSection,
    PlacementKind, Pos, Rgb, SaveGameStore, SessionError, SessionEvent, SessionFileSystem,
    ZoneType, format_money,
};

pub const MIN_TERMINAL_WIDTH: u16 = 80;
pub const MIN_TERMINAL_HEIGHT: u16 = 24;
pub const SIDE_PANEL_WIDTH: u16 = 34;
pub const MIN_FPS: u16 = 5;
pub const MAX_FPS: u16 = 60;
pub const DEFAULT_FPS: u16 = 30;
pub const HELP: &str = "\
TermCity Rust - a terminal city builder

Usage: termcity-rs [options]

  --seed <n>        Generate from a signed 32-bit seed (default: random)
  --load [file]     Load a save (default: Rust quick-save file)
  --size <size>     small (160x96), medium (320x192), large (640x384),
                    or WIDTHxHEIGHT (80x24 through 640x384)
  --fps <n>         Camera redraws per second, 5 to 60 (default: 30)
  --dump-map        Print the map through CellRenderer and exit
  -h, --help, /?    Show this help

Play: Arrows move; Ctrl+Arrows jump; Shift extends selection; R/C/I zone;
U dezone; D/Delete demolish; B road preview; T road line; Enter confirms/menu;
P/Space pause; 1/2/3 speed; +/-/0 zoom; F1 help; F5 save; F6 guide;
F7 weekly report; F8 growth report; F9 load; F10 menu; F12 diagnostics;
Ctrl+Z undo; E edge scroll; Ctrl+Q quit. Mouse supports select, pan, wheel,
Ctrl+wheel zoom, right-click context actions, minimap and zoom controls.";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MapSize {
    pub width: i32,
    pub height: i32,
}

impl Default for MapSize {
    fn default() -> Self {
        Self { width: 160, height: 96 }
    }
}

impl MapSize {
    fn parse(value: &str) -> Result<Self, CliError> {
        match value.to_ascii_lowercase().as_str() {
            "small" => Ok(Self::default()),
            "medium" => Ok(Self { width: 320, height: 192 }),
            "large" => Ok(Self { width: 640, height: 384 }),
            _ => {
                let (width, height) = value
                    .split_once(['x', 'X'])
                    .ok_or_else(|| CliError::new(format!("Invalid map size '{value}'.")))?;
                let width = width
                    .parse::<i32>()
                    .map_err(|_| CliError::new(format!("Invalid map width '{width}'.")))?;
                let height = height
                    .parse::<i32>()
                    .map_err(|_| CliError::new(format!("Invalid map height '{height}'.")))?;
                if !(80..=640).contains(&width) || !(24..=384).contains(&height) {
                    return Err(CliError::new(
                        "Map dimensions must be from 80x24 through 640x384.",
                    ));
                }
                Ok(Self { width, height })
            }
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CliOptions {
    pub seed: Option<i32>,
    pub size: MapSize,
    pub load: Option<Option<PathBuf>>,
    pub fps: u16,
    pub dump_map: bool,
    pub help: bool,
}

impl Default for CliOptions {
    fn default() -> Self {
        Self {
            seed: None,
            size: MapSize::default(),
            load: None,
            fps: DEFAULT_FPS,
            dump_map: false,
            help: false,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CliError(String);

impl CliError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

impl fmt::Display for CliError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl std::error::Error for CliError {}

/// Parses command-line arguments without reading process-global state.
///
/// # Errors
///
/// Returns a descriptive error for unknown options, missing values, malformed values, or values
/// outside the documented ranges.
pub fn parse_args(arguments: impl IntoIterator<Item = String>) -> Result<CliOptions, CliError> {
    let arguments = arguments.into_iter().collect::<Vec<_>>();
    let mut options = CliOptions::default();
    let mut index = 0;
    while index < arguments.len() {
        match arguments[index].as_str() {
            "-h" | "--help" | "/?" => options.help = true,
            "--dump-map" => options.dump_map = true,
            "--seed" => {
                index += 1;
                let value = arguments
                    .get(index)
                    .ok_or_else(|| CliError::new("Option '--seed' needs a value."))?;
                options.seed = Some(value.parse().map_err(|_| {
                    CliError::new(format!("--seed must be a signed 32-bit integer, not '{value}'."))
                })?);
            }
            "--size" => {
                index += 1;
                let value = arguments
                    .get(index)
                    .ok_or_else(|| CliError::new("Option '--size' needs a value."))?;
                options.size = MapSize::parse(value)?;
            }
            "--fps" => {
                index += 1;
                let value = arguments
                    .get(index)
                    .ok_or_else(|| CliError::new("Option '--fps' needs a value."))?;
                let fps = value.parse::<u16>().map_err(|_| {
                    CliError::new(format!("--fps must be an integer, not '{value}'."))
                })?;
                if !(MIN_FPS..=MAX_FPS).contains(&fps) {
                    return Err(CliError::new(format!(
                        "--fps must be from {MIN_FPS} to {MAX_FPS}."
                    )));
                }
                options.fps = fps;
            }
            "--load" => {
                let path = arguments.get(index + 1).filter(|next| !next.starts_with('-'));
                if path.is_some() {
                    index += 1;
                }
                options.load = Some(path.map(PathBuf::from));
            }
            unknown => return Err(CliError::new(format!("Unknown option '{unknown}'."))),
        }
        index += 1;
    }
    Ok(options)
}

fn generated_seed() -> i32 {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or(Duration::ZERO).as_nanos();
    let bytes = nanos.to_le_bytes();
    i32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])
}

/// Generates or loads the game session described by CLI options.
///
/// # Errors
///
/// Returns generation, default-path, save-read, or save-validation errors.
pub fn create_session(options: &CliOptions) -> Result<GameSession, String> {
    let config = GameConfig {
        map_width: options.size.width,
        map_height: options.size.height,
        seed: options.seed.unwrap_or_else(generated_seed),
        ..GameConfig::default()
    };
    let game = CityGame::new(config).map_err(|error| error.to_string())?;
    let save_path = SaveGameStore::default_path().map_err(|error| error.to_string())?;
    let mut session = GameSession::new(game, save_path);
    if let Some(path) = &options.load {
        let path = path.clone().unwrap_or_else(|| session.save_path.clone());
        session.load_from(&path).map_err(|error| error.to_string())?;
    } else if !options.dump_map {
        session.game.paused = true;
        session.show_guide();
    }
    Ok(session)
}

/// Writes a text map using the same cell renderer as the interactive UI.
///
/// # Errors
///
/// Returns an error when writing to the supplied output fails.
pub fn dump_map(game: &CityGame, output: &mut impl Write) -> io::Result<()> {
    writeln!(output, "Seed {}, {}x{}", game.config.seed, game.map.width(), game.map.height())?;
    let renderer = MapRenderer::new(game);
    for y in 0..game.map.height() {
        for x in 0..game.map.width() {
            write!(output, "{}", renderer.render(x, y).glyph)?;
        }
        writeln!(output)?;
    }
    Ok(())
}

/// Runs the CLI with injectable streams and terminal detection. Returns the documented exit class.
pub fn run_cli(
    arguments: impl IntoIterator<Item = String>,
    output: &mut impl Write,
    error: &mut impl Write,
    interactive_terminal: bool,
) -> u8 {
    let options = match parse_args(arguments) {
        Ok(options) => options,
        Err(parse_error) => {
            let _ = writeln!(error, "{parse_error}\n\n{HELP}");
            return 2;
        }
    };
    if options.help {
        let _ = writeln!(output, "{HELP}");
        return 0;
    }
    let session = match create_session(&options) {
        Ok(session) => session,
        Err(runtime_error) => {
            let _ = writeln!(error, "termcity-rs: {runtime_error}");
            return 1;
        }
    };
    if options.dump_map {
        return match dump_map(&session.game, output) {
            Ok(()) => 0,
            Err(runtime_error) => {
                let _ = writeln!(error, "termcity-rs: failed to write map: {runtime_error}");
                1
            }
        };
    }
    if !interactive_terminal {
        let _ = writeln!(error, "TermCity needs an interactive terminal on stdin and stdout.");
        return 1;
    }
    match run_interactive(session, options.fps) {
        Ok(()) => 0,
        Err(runtime_error) => {
            let _ = writeln!(error, "termcity-rs: {runtime_error}");
            1
        }
    }
}

pub fn run_process(arguments: impl IntoIterator<Item = String>) -> u8 {
    let interactive = io::stdin().is_terminal() && io::stdout().is_terminal();
    run_cli(arguments, &mut io::stdout(), &mut io::stderr(), interactive)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RedrawRequest {
    None,
    Cursor,
    Full,
}

impl RedrawRequest {
    fn merge(self, other: Self) -> Self {
        match (self, other) {
            (Self::Full, _) | (_, Self::Full) => Self::Full,
            (Self::Cursor, _) | (_, Self::Cursor) => Self::Cursor,
            _ => Self::None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PointerMode {
    None,
    Pan { anchor: Pos },
    Select,
    Minimap,
}

const CONTEXT_CHOICES: [&str; 9] = [
    "Zone homes (R)",
    "Zone shops (C)",
    "Zone industry (I)",
    "Dezone (U)",
    "Road preview (B)",
    "Road line (T)",
    "Demolish (D)",
    "Clear selection",
    "City menu (F10)",
];

pub struct App<F = DiskFileSystem> {
    pub session: GameSession<F>,
    pub help_visible: bool,
    pub should_quit: bool,
    pub selected_prompt: usize,
    pub context_visible: bool,
    terminal_size: (u16, u16),
    pointer: PointerMode,
    selected_context: usize,
    mouse_position: Option<(u16, u16)>,
    edge_scroller: EdgeScroller,
}

impl<F: SessionFileSystem> App<F> {
    #[must_use]
    pub fn new(session: GameSession<F>, width: u16, height: u16) -> Self {
        let mut app = Self {
            session,
            help_visible: false,
            should_quit: false,
            selected_prompt: 0,
            context_visible: false,
            terminal_size: (width, height),
            pointer: PointerMode::None,
            selected_context: 0,
            mouse_position: None,
            edge_scroller: EdgeScroller::default(),
        };
        app.resize(width, height);
        app.session.drain_events();
        app
    }

    pub fn resize(&mut self, width: u16, height: u16) {
        self.terminal_size = (width, height);
        let layout = screen_layout(Rect::new(0, 0, width, height));
        let map = inner(layout.map);
        self.session.set_viewport(i32::from(map.width), i32::from(map.height));
    }

    /// Advances simulation time and consumes typed session change events.
    ///
    /// # Errors
    ///
    /// Returns session persistence or elapsed-time errors.
    pub fn update(&mut self, elapsed: Duration) -> Result<RedrawRequest, SessionError> {
        let result = self.update_inner(elapsed);
        self.finish_session_operation(result)
    }

    fn update_inner(&mut self, elapsed: Duration) -> Result<RedrawRequest, SessionError> {
        self.session.update(elapsed.as_secs_f64())?;
        if (self.session.edge_scroll_enabled || self.pointer == PointerMode::Select)
            && self.session.prompt.is_none()
            && self.session.preview.is_none()
            && !self.help_visible
            && !self.context_visible
        {
            let map = inner(screen_layout(self.terminal_area()).map);
            if let Some((x, y)) = self.mouse_position {
                let (dx, dy) = self.edge_scroller.step(
                    i32::from(x) - i32::from(map.x),
                    i32::from(y) - i32::from(map.y),
                    i32::from(map.width),
                    i32::from(map.height),
                    elapsed.as_secs_f64(),
                );
                if dx != 0 || dy != 0 {
                    self.session.scroll_characters(dx, dy);
                    if self.pointer == PointerMode::Select {
                        let view_x = i32::from(x) - i32::from(map.x);
                        let view_y = i32::from(y) - i32::from(map.y);
                        let position = self.session.screen_to_map(view_x, view_y);
                        self.session.update_drag(position);
                    }
                }
            }
        } else {
            self.edge_scroller.reset();
        }
        Ok(self.consume_events())
    }

    /// Applies one terminal event while respecting active modal overlays.
    ///
    /// # Errors
    ///
    /// Returns errors from save, load, undo, generation, or dispatched game actions.
    pub fn handle_event(&mut self, event: &CrosstermEvent) -> Result<RedrawRequest, SessionError> {
        let result = self.handle_event_inner(event);
        self.finish_session_operation(result)
    }

    fn handle_event_inner(
        &mut self,
        event: &CrosstermEvent,
    ) -> Result<RedrawRequest, SessionError> {
        let result = match event {
            CrosstermEvent::Resize(width, height) => {
                self.resize(*width, *height);
                RedrawRequest::Full
            }
            CrosstermEvent::Key(key) if key.kind != KeyEventKind::Release => {
                self.handle_key(*key)?
            }
            CrosstermEvent::Mouse(mouse) => self.handle_mouse(*mouse)?,
            _ => RedrawRequest::None,
        };
        Ok(result.merge(self.consume_events()))
    }

    fn finish_session_operation(
        &mut self,
        result: Result<RedrawRequest, SessionError>,
    ) -> Result<RedrawRequest, SessionError> {
        match result {
            Ok(redraw) => Ok(redraw),
            Err(error)
                if !matches!(
                    error,
                    SessionError::InvalidElapsed(_) | SessionError::InvalidAutosaveSlot(_)
                ) =>
            {
                self.session.set_message(format!("Operation failed: {error}"), MessageKind::Error);
                Ok(RedrawRequest::Full.merge(self.consume_events()))
            }
            Err(error) => Err(error),
        }
    }

    fn consume_events(&mut self) -> RedrawRequest {
        self.session.drain_events().into_iter().fold(RedrawRequest::None, |redraw, event| {
            let event_redraw = match event {
                SessionEvent::Changed(ChangeCategory::Selection) => RedrawRequest::Cursor,
                SessionEvent::Changed(ChangeCategory::Camera | ChangeCategory::Full)
                | SessionEvent::Feedback(_) => RedrawRequest::Full,
                SessionEvent::QuitRequested => {
                    self.should_quit = true;
                    RedrawRequest::None
                }
            };
            redraw.merge(event_redraw)
        })
    }

    fn handle_key(&mut self, key: KeyEvent) -> Result<RedrawRequest, SessionError> {
        if self.help_visible {
            self.help_visible = false;
            return Ok(RedrawRequest::Full);
        }
        if self.context_visible {
            return self.handle_context_key(key);
        }
        if self.session.prompt.is_some() {
            return self.handle_prompt_key(key);
        }
        if self.session.preview.is_some() {
            return self.handle_preview_key(key);
        }

        let ctrl = key.modifiers.contains(KeyModifiers::CONTROL);
        let shift = key.modifiers.contains(KeyModifiers::SHIFT);
        let alt = key.modifiers.contains(KeyModifiers::ALT);
        match key.code {
            KeyCode::Up => self.move_key(0, -1, ctrl || alt, shift),
            KeyCode::Down => self.move_key(0, 1, ctrl || alt, shift),
            KeyCode::Left => self.move_key(-1, 0, ctrl || alt, shift),
            KeyCode::Right => self.move_key(1, 0, ctrl || alt, shift),
            KeyCode::PageUp => self.session.jump_cursor(0, -1, shift),
            KeyCode::PageDown => self.session.jump_cursor(0, 1, shift),
            KeyCode::Home => self.session.jump_cursor(-1, 0, shift),
            KeyCode::End => self.session.jump_cursor(1, 0, shift),
            KeyCode::Enter | KeyCode::Char('m' | 'M') => self.open_context(),
            KeyCode::Esc => self.session.clear_selection(),
            KeyCode::Char('s' | 'S') => self.session.toggle_selection_mode(),
            KeyCode::Char('r' | 'R') => {
                self.session.zone(ZoneType::Residential)?;
            }
            KeyCode::Char('c' | 'C') => {
                self.session.zone(ZoneType::Commercial)?;
            }
            KeyCode::Char('i' | 'I') => {
                self.session.zone(ZoneType::Industrial)?;
            }
            KeyCode::Char('u' | 'U') => {
                self.session.dezone()?;
            }
            KeyCode::Char('d' | 'D') | KeyCode::Delete => self.session.preview_demolish(),
            KeyCode::Char('b' | 'B') => self.session.preview_road(None),
            KeyCode::Char('t' | 'T') => self.session.begin_road_line(None),
            KeyCode::Char('p' | 'P' | ' ') => self.session.toggle_pause(),
            KeyCode::Char('1') => self.session.set_speed(GameSpeed::Slow),
            KeyCode::Char('2') => self.session.set_speed(GameSpeed::Medium),
            KeyCode::Char('3') => self.session.set_speed(GameSpeed::Fast),
            KeyCode::Char('+' | '=') => self.session.zoom_by(1, None),
            KeyCode::Char('-' | '_') => self.session.zoom_by(-1, None),
            KeyCode::Char('0') => self.session.set_zoom(0, None),
            KeyCode::Char('e' | 'E') => self.session.toggle_edge_scroll(),
            KeyCode::Char('z' | 'Z') if ctrl => self.session.request_undo(),
            KeyCode::Char('q' | 'Q') if ctrl => self.session.request_quit()?,
            KeyCode::F(1) => self.help_visible = true,
            KeyCode::F(2) => self.session.toggle_section(PanelSection::Demand),
            KeyCode::F(3) => self.session.toggle_section(PanelSection::City),
            KeyCode::F(4) => self.session.toggle_section(PanelSection::Zones),
            KeyCode::F(5) => self.session.quick_save()?,
            KeyCode::F(6) => {
                if self.session.guide_visible {
                    self.session.dismiss_guide();
                } else {
                    self.session.show_guide();
                }
            }
            KeyCode::F(7) => self.session.show_report(),
            KeyCode::F(8) => self.session.show_growth_report(),
            KeyCode::F(9) => {
                let path = self.session.save_path.clone();
                self.session.request_load(path)?;
            }
            KeyCode::F(10) => self.session.show_session_menu(generated_seed()),
            KeyCode::F(12) => self.session.toggle_input_debug(),
            _ => return Ok(RedrawRequest::None),
        }
        Ok(RedrawRequest::Full)
    }

    fn move_key(&mut self, dx: i32, dy: i32, jump: bool, extend: bool) {
        if jump {
            self.session.jump_cursor(dx, dy, extend);
        } else {
            self.session.move_cursor(dx, dy, extend);
        }
    }

    fn open_context(&mut self) {
        self.context_visible = true;
        self.selected_context = 0;
    }

    fn handle_context_key(&mut self, key: KeyEvent) -> Result<RedrawRequest, SessionError> {
        match key.code {
            KeyCode::Esc => self.context_visible = false,
            KeyCode::Up => self.selected_context = self.selected_context.saturating_sub(1),
            KeyCode::Down | KeyCode::Tab => {
                self.selected_context = (self.selected_context + 1).min(CONTEXT_CHOICES.len() - 1);
            }
            KeyCode::Enter => self.run_context_action()?,
            _ => return Ok(RedrawRequest::None),
        }
        Ok(RedrawRequest::Full)
    }

    fn run_context_action(&mut self) -> Result<(), SessionError> {
        self.context_visible = false;
        match self.selected_context {
            0 => {
                self.session.zone(ZoneType::Residential)?;
            }
            1 => {
                self.session.zone(ZoneType::Commercial)?;
            }
            2 => {
                self.session.zone(ZoneType::Industrial)?;
            }
            3 => {
                self.session.dezone()?;
            }
            4 => self.session.preview_road(None),
            5 => self.session.begin_road_line(None),
            6 => self.session.preview_demolish(),
            7 => self.session.clear_selection(),
            8 => self.session.show_session_menu(generated_seed()),
            _ => {}
        }
        Ok(())
    }

    fn handle_preview_key(&mut self, key: KeyEvent) -> Result<RedrawRequest, SessionError> {
        match key.code {
            KeyCode::Enter | KeyCode::Char('y' | 'Y') => {
                self.session.confirm_preview()?;
            }
            KeyCode::Esc | KeyCode::Char('n' | 'N') => self.session.cancel_preview(),
            KeyCode::Up => self.session.move_cursor(0, -1, false),
            KeyCode::Down => self.session.move_cursor(0, 1, false),
            KeyCode::Left => self.session.move_cursor(-1, 0, false),
            KeyCode::Right => self.session.move_cursor(1, 0, false),
            KeyCode::F(5) => self.session.quick_save()?,
            KeyCode::F(9) => {
                let path = self.session.save_path.clone();
                self.session.request_load(path)?;
            }
            KeyCode::F(10) => self.session.show_session_menu(generated_seed()),
            KeyCode::Char('z' | 'Z') if key.modifiers.contains(KeyModifiers::CONTROL) => {
                self.session.request_undo();
            }
            KeyCode::Char('q' | 'Q') if key.modifiers.contains(KeyModifiers::CONTROL) => {
                self.session.request_quit()?;
            }
            _ => return Ok(RedrawRequest::None),
        }
        Ok(RedrawRequest::Full)
    }

    fn handle_prompt_key(&mut self, key: KeyEvent) -> Result<RedrawRequest, SessionError> {
        let choice_count = self.session.prompt.as_ref().map_or(0, |prompt| prompt.choices.len());
        self.selected_prompt = self.selected_prompt.min(choice_count.saturating_sub(1));
        match key.code {
            KeyCode::Esc => {
                self.session.close_prompt();
                self.selected_prompt = 0;
            }
            KeyCode::Up => self.selected_prompt = self.selected_prompt.saturating_sub(1),
            KeyCode::Down | KeyCode::Tab => {
                self.selected_prompt =
                    (self.selected_prompt + 1).min(choice_count.saturating_sub(1));
            }
            KeyCode::Enter => {
                self.session.select_prompt(self.selected_prompt)?;
                self.selected_prompt = 0;
            }
            KeyCode::Backspace => {
                if let Some(mut input) =
                    self.session.prompt.as_ref().and_then(|prompt| prompt.input.clone())
                {
                    input.pop();
                    self.session.set_prompt_input(input);
                }
            }
            KeyCode::Char('a' | 'A') if key.modifiers.contains(KeyModifiers::CONTROL) => {
                if self.session.prompt.as_ref().is_some_and(|prompt| prompt.input.is_some()) {
                    self.session.set_prompt_input("");
                }
            }
            KeyCode::Char(character)
                if !key.modifiers.intersects(KeyModifiers::CONTROL | KeyModifiers::ALT) =>
            {
                if let Some(mut input) =
                    self.session.prompt.as_ref().and_then(|prompt| prompt.input.clone())
                {
                    input.push(character);
                    self.session.set_prompt_input(input);
                }
            }
            _ => return Ok(RedrawRequest::None),
        }
        Ok(RedrawRequest::Full)
    }

    fn handle_mouse(&mut self, mouse: MouseEvent) -> Result<RedrawRequest, SessionError> {
        self.mouse_position = Some((mouse.column, mouse.row));
        if self.help_visible {
            if matches!(mouse.kind, MouseEventKind::Down(MouseButton::Left | MouseButton::Right)) {
                self.help_visible = false;
                return Ok(RedrawRequest::Full);
            }
            return Ok(RedrawRequest::None);
        }
        if self.context_visible {
            return self.handle_context_mouse(mouse);
        }
        if self.session.prompt.is_some() || self.session.preview.is_some() {
            return self.handle_modal_mouse(mouse);
        }
        let layout = screen_layout(Rect::new(0, 0, self.terminal_size.0, self.terminal_size.1));
        let map = inner(layout.map);
        let point = (mouse.column, mouse.row);

        if self.pointer == PointerMode::Minimap {
            match mouse.kind {
                MouseEventKind::Drag(MouseButton::Left) => {
                    self.pan_from_minimap(mouse.column, mouse.row, layout.minimap);
                    return Ok(RedrawRequest::Full);
                }
                MouseEventKind::Up(MouseButton::Left) => {
                    self.pointer = PointerMode::None;
                    return Ok(RedrawRequest::None);
                }
                _ => {}
            }
        }
        if matches!(mouse.kind, MouseEventKind::Down(MouseButton::Left))
            && layout.minimap.contains(point.into())
        {
            self.pointer = PointerMode::Minimap;
            self.pan_from_minimap(mouse.column, mouse.row, layout.minimap);
            return Ok(RedrawRequest::Full);
        }
        if matches!(mouse.kind, MouseEventKind::Down(MouseButton::Left))
            && layout.zoom.contains(point.into())
        {
            let level = zoom_level_at(mouse.column, layout.zoom);
            self.session.set_zoom(level, None);
            return Ok(RedrawRequest::Full);
        }
        if !map.contains(point.into()) {
            return Ok(RedrawRequest::None);
        }
        let view_x = i32::from(mouse.column - map.x);
        let view_y = i32::from(mouse.row - map.y);
        let position = self.session.screen_to_map(view_x, view_y);
        let selecting = mouse
            .modifiers
            .intersects(KeyModifiers::SHIFT | KeyModifiers::CONTROL | KeyModifiers::ALT)
            || self.session.road_tool_active;
        match mouse.kind {
            MouseEventKind::Down(MouseButton::Right) => {
                self.session.prepare_context_menu_at(position);
                self.open_context();
            }
            MouseEventKind::Down(MouseButton::Left) if selecting => {
                self.pointer = PointerMode::Select;
                self.session.begin_drag(position);
            }
            MouseEventKind::Down(MouseButton::Left) => {
                self.pointer = PointerMode::Pan { anchor: position };
                self.session.select_cell(position);
            }
            MouseEventKind::Drag(MouseButton::Left) => match self.pointer {
                PointerMode::Pan { anchor } => self.session.pan_camera(anchor, view_x, view_y),
                PointerMode::Select => self.session.update_drag(position),
                PointerMode::Minimap | PointerMode::None => {}
            },
            MouseEventKind::Up(MouseButton::Left) => {
                if self.pointer == PointerMode::Select {
                    self.session.end_selection();
                }
                self.pointer = PointerMode::None;
            }
            MouseEventKind::ScrollUp | MouseEventKind::ScrollDown => {
                let delta: i32 =
                    if matches!(mouse.kind, MouseEventKind::ScrollUp) { -3 } else { 3 };
                if mouse.modifiers.contains(KeyModifiers::CONTROL) {
                    self.session.zoom_by(-delta.signum(), Some((view_x, view_y)));
                } else if mouse.modifiers.contains(KeyModifiers::ALT) {
                    self.session.scroll_characters(delta, 0);
                } else {
                    self.session.scroll_characters(0, delta);
                }
            }
            MouseEventKind::ScrollLeft => self.session.scroll_characters(-3, 0),
            MouseEventKind::ScrollRight => self.session.scroll_characters(3, 0),
            _ => return Ok(RedrawRequest::None),
        }
        Ok(RedrawRequest::Full)
    }

    fn handle_context_mouse(&mut self, mouse: MouseEvent) -> Result<RedrawRequest, SessionError> {
        if !matches!(mouse.kind, MouseEventKind::Down(MouseButton::Left)) {
            return Ok(RedrawRequest::None);
        }
        let popup = centered(
            self.terminal_area(),
            42,
            u16::try_from(CONTEXT_CHOICES.len()).unwrap_or(u16::MAX).saturating_add(2),
        );
        if !popup.contains((mouse.column, mouse.row).into()) {
            self.context_visible = false;
            return Ok(RedrawRequest::Full);
        }
        let index = usize::from(mouse.row.saturating_sub(popup.y + 1));
        if index < CONTEXT_CHOICES.len() {
            self.selected_context = index;
            self.run_context_action()?;
        }
        Ok(RedrawRequest::Full)
    }

    fn handle_modal_mouse(&mut self, mouse: MouseEvent) -> Result<RedrawRequest, SessionError> {
        if !matches!(mouse.kind, MouseEventKind::Down(MouseButton::Left)) {
            return Ok(RedrawRequest::None);
        }
        if self.session.preview.is_some() {
            let (popup, button_row) = confirmation_geometry(self.terminal_area());
            if mouse.row == button_row && popup.contains((mouse.column, mouse.row).into()) {
                if mouse.column < popup.x + popup.width / 2 {
                    self.session.confirm_preview()?;
                } else {
                    self.session.cancel_preview();
                }
            }
            return Ok(RedrawRequest::Full);
        }
        let Some(prompt) = &self.session.prompt else {
            return Ok(RedrawRequest::None);
        };
        let (popup, first_choice) = prompt_geometry(
            self.terminal_area(),
            &prompt.text,
            prompt.input.is_some(),
            prompt.choices.len(),
        );
        if popup.contains((mouse.column, mouse.row).into()) && mouse.row >= first_choice {
            let index = usize::from(mouse.row - first_choice);
            if index < prompt.choices.len() {
                self.selected_prompt = index;
                self.session.select_prompt(index)?;
                self.selected_prompt = 0;
            }
        }
        Ok(RedrawRequest::Full)
    }

    fn pan_from_minimap(&mut self, x: u16, y: u16, area: Rect) {
        let inner = inner(area);
        if inner.width == 0 || inner.height == 0 {
            return;
        }
        let map_x = i32::from(x.saturating_sub(inner.x)) * self.session.game.map.width()
            / i32::from(inner.width);
        let map_y = i32::from(y.saturating_sub(inner.y)) * self.session.game.map.height()
            / i32::from(inner.height);
        self.session.center_on(Pos::new(map_x, map_y));
    }

    const fn terminal_area(&self) -> Rect {
        Rect::new(0, 0, self.terminal_size.0, self.terminal_size.1)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScreenLayout {
    pub hud: Rect,
    pub map: Rect,
    pub panel: Rect,
    pub minimap: Rect,
    pub zoom: Rect,
    pub message: Rect,
}

#[must_use]
pub fn screen_layout(area: Rect) -> ScreenLayout {
    let rows = Layout::vertical([Constraint::Length(1), Constraint::Min(1), Constraint::Length(1)])
        .split(area);
    let columns = Layout::horizontal([Constraint::Min(1), Constraint::Length(SIDE_PANEL_WIDTH)])
        .split(rows[1]);
    let panel_rows =
        Layout::vertical([Constraint::Length(9), Constraint::Length(3), Constraint::Min(1)])
            .split(columns[1]);
    ScreenLayout {
        hud: rows[0],
        map: columns[0],
        panel: panel_rows[2],
        minimap: panel_rows[0],
        zoom: panel_rows[1],
        message: rows[2],
    }
}

pub fn render<F: SessionFileSystem>(frame: &mut Frame<'_>, app: &App<F>) {
    let area = frame.area();
    if area.width < MIN_TERMINAL_WIDTH || area.height < MIN_TERMINAL_HEIGHT {
        render_small(frame, area);
        return;
    }
    let layout = screen_layout(area);
    let renderer = MapRenderer::new(&app.session.game);
    let stats = renderer.stats();
    render_hud(frame, layout.hud, &app.session, stats);
    render_map(frame, layout.map, &app.session, &renderer);
    render_minimap(frame, layout.minimap, &app.session, &renderer);
    render_zoom(frame, layout.zoom, &app.session);
    render_panel(frame, layout.panel, &app.session, stats);
    render_message(frame, layout.message, &app.session);
    if app.help_visible {
        render_help(frame, area);
    } else if app.session.prompt.is_some() {
        render_prompt(frame, area, &app.session, app.selected_prompt);
    } else if app.session.preview.is_some() {
        render_confirmation(frame, area, &app.session);
    } else if app.context_visible {
        render_context(frame, area, app.selected_context);
    }
}

fn render_small(frame: &mut Frame<'_>, area: Rect) {
    frame.render_widget(
        Paragraph::new(format!(
            "TermCity needs at least {MIN_TERMINAL_WIDTH}x{MIN_TERMINAL_HEIGHT}\nCurrent: {}x{}",
            area.width, area.height
        ))
        .alignment(Alignment::Center)
        .style(Style::default().fg(Color::Yellow))
        .block(Block::bordered().title(" TERMINAL TOO SMALL ")),
        area,
    );
}

fn render_hud<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
    stats: CityStats,
) {
    let game = &session.game;
    let speed =
        if game.paused { "PAUSED".to_owned() } else { format!("{:?}", game.speed).to_uppercase() };
    let text = format!(
        " TERMCITY-RS  {}  Y{} W{:02} D{}  POP {}  SPEED {}  ZOOM {} ",
        format_money(game.money),
        game.year(),
        game.week_of_year(),
        game.day + 1,
        stats.population,
        speed,
        session.zoom_label()
    );
    frame.render_widget(
        Paragraph::new(text).style(
            Style::default()
                .fg(Color::Black)
                .bg(Color::Rgb(95, 225, 190))
                .add_modifier(Modifier::BOLD),
        ),
        area,
    );
}

fn render_map<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
    renderer: &MapRenderer<'_>,
) {
    let block = Block::bordered().title(" CITY MAP ");
    let map = block.inner(area);
    frame.render_widget(block, area);
    for sy in 0..map.height {
        for sx in 0..map.width {
            if session.zoom_level > 0 && sx % 2 == 1 {
                continue;
            }
            let view_x = i32::from(sx);
            let view_y = i32::from(sy);
            let position = session.screen_to_map(view_x, view_y);
            if !session.game.map.contains(position) {
                continue;
            }
            let visual = if session.zoom_level < 0 {
                renderer.sample(position.x, position.y, session.stride())
            } else {
                renderer.render(position.x, position.y)
            };
            let mut style = Style::default().fg(rgb(visual.foreground)).bg(rgb(visual.background));
            let block = session.block_at(position);
            if session.selection.is_some_and(|selection| selection.intersects(block)) {
                style = style.bg(Color::Rgb(78, 69, 120));
            }
            if let Some(preview) = &session.preview
                && preview.area.intersects(block)
            {
                let valid = preview.is_valid(&session.game, position.x, position.y);
                style =
                    style.bg(if valid { Color::Rgb(42, 120, 73) } else { Color::Rgb(140, 45, 55) });
            }
            if block.contains(session.cursor) {
                style = style
                    .fg(Color::Black)
                    .bg(Color::Rgb(255, 220, 90))
                    .add_modifier(Modifier::BOLD);
            }
            frame.buffer_mut().set_string(map.x + sx, map.y + sy, visual.glyph, style);
            if session.zoom_level > 0 && sx + 1 < map.width {
                frame.buffer_mut().set_string(map.x + sx + 1, map.y + sy, visual.glyph, style);
            }
        }
    }
}

fn render_minimap<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
    renderer: &MapRenderer<'_>,
) {
    let block = Block::bordered().title(" MINIMAP ");
    let draw = block.inner(area);
    frame.render_widget(block, area);
    let view = session.view_rect();
    let map_width = session.game.map.width();
    let map_height = session.game.map.height();
    let marker_width =
        (view.width.min(map_width) * i32::from(draw.width) + map_width - 1) / map_width;
    let marker_height =
        (view.height.min(map_height) * i32::from(draw.height) + map_height - 1) / map_height;
    let marker = CellRect::new(
        (view.x * i32::from(draw.width) / map_width).clamp(0, i32::from(draw.width) - marker_width),
        (view.y * i32::from(draw.height) / map_height)
            .clamp(0, i32::from(draw.height) - marker_height),
        marker_width,
        marker_height,
    );
    for y in 0..draw.height {
        for x in 0..draw.width {
            let map_x = i32::from(x) * session.game.map.width() / i32::from(draw.width.max(1));
            let map_y = i32::from(y) * session.game.map.height() / i32::from(draw.height.max(1));
            let map_right =
                i32::from(x + 1) * session.game.map.width() / i32::from(draw.width.max(1));
            let map_bottom =
                i32::from(y + 1) * session.game.map.height() / i32::from(draw.height.max(1));
            let visual = renderer.sample_rect(
                map_x,
                map_y,
                (map_right - map_x).max(1),
                (map_bottom - map_y).max(1),
            );
            let in_view = marker.contains_xy(i32::from(x), i32::from(y));
            let style = Style::default().fg(rgb(visual.foreground)).bg(if in_view {
                Color::White
            } else {
                rgb(visual.background)
            });
            frame.buffer_mut().set_string(draw.x + x, draw.y + y, visual.glyph, style);
        }
    }
}

fn render_zoom<F: SessionFileSystem>(frame: &mut Frame<'_>, area: Rect, session: &GameSession<F>) {
    let labels = ["0.25x", "0.5x", "1x", "2x"];
    let selected = usize::try_from(session.zoom_level + 2).unwrap_or_default();
    let spans = labels.into_iter().enumerate().flat_map(|(index, label)| {
        let style = if index == selected {
            Style::default().fg(Color::Black).bg(Color::Cyan).add_modifier(Modifier::BOLD)
        } else {
            Style::default().fg(Color::Gray)
        };
        [Span::raw(" "), Span::styled(label, style)]
    });
    frame.render_widget(
        Paragraph::new(Line::from(spans.collect::<Vec<_>>()))
            .block(Block::bordered().title(" ZOOM [-/+] ")),
        area,
    );
}

fn render_panel<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
    stats: CityStats,
) {
    let game = &session.game;
    let demand = Demand::compute(stats, &game.config);
    let collapsed = |section| if session.is_collapsed(section) { "▶" } else { "▼" };
    let mut lines = vec![Line::styled(
        format!("{} DEMAND (F2)", collapsed(PanelSection::Demand)),
        Style::default().fg(Color::Cyan).add_modifier(Modifier::BOLD),
    )];
    if !session.is_collapsed(PanelSection::Demand) {
        lines.extend([
            Line::from(demand_line("Homes", demand.residential, Color::Green)),
            Line::from(demand_line("Shops", demand.commercial, Color::Blue)),
            Line::from(demand_line("Industry", demand.industrial, Color::Yellow)),
        ]);
    }
    lines.push(Line::styled(
        format!("{} CITY (F3)", collapsed(PanelSection::City)),
        Style::default().fg(Color::Cyan).add_modifier(Modifier::BOLD),
    ));
    if !session.is_collapsed(PanelSection::City) {
        lines.extend([
            Line::raw(format!(" Population {:>10}", stats.population)),
            Line::raw(format!(" Households {:>10}", stats.households)),
            Line::raw(format!(" Roads {:>10}", stats.road_cells)),
            Line::raw(format!(" Weekly tax {:>10}", format_money(stats.weekly_income))),
        ]);
    }
    lines.push(Line::styled(
        format!("{} ZONES (F4)", collapsed(PanelSection::Zones)),
        Style::default().fg(Color::Cyan).add_modifier(Modifier::BOLD),
    ));
    if !session.is_collapsed(PanelSection::Zones) {
        lines.extend([
            zone_line("R Homes", stats.residential),
            zone_line("C Shops", stats.commercial),
            zone_line("I Industry", stats.industrial),
        ]);
    }
    if session.guide_visible {
        lines.extend([
            Line::raw(""),
            Line::styled("GUIDE", Style::default().fg(Color::LightYellow)),
            Line::raw("Connect road → zone R → resume P"),
        ]);
    }
    if session.input_debug {
        lines.extend([
            Line::raw(""),
            Line::raw(format!(
                "Loop {}ms worst {}ms",
                session.loop_gap_ms, session.loop_worst_gap_ms
            )),
        ]);
    }
    frame.render_widget(
        Paragraph::new(lines).wrap(Wrap { trim: true }).block(Block::bordered().title(" CITY ")),
        area,
    );
}

fn demand_line(label: &str, value: f64, color: Color) -> Vec<Span<'_>> {
    let bars = (1..=10).filter(|step| value.clamp(0.0, 1.0) >= f64::from(*step) / 10.0).count();
    vec![
        Span::raw(format!(" {label:<8} ")),
        Span::styled("█".repeat(bars), Style::default().fg(color)),
        Span::styled("·".repeat(10 - bars), Style::default().fg(Color::DarkGray)),
    ]
}

fn zone_line(label: &str, count: termcity_core::ZoneCount) -> Line<'static> {
    Line::raw(format!(" {label:<10} {:>4}/{:<4}", count.occupied(), count.zoned))
}

fn render_message<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
) {
    let inspection = CellInspector::summary(&session.game, session.cursor);
    let message = if session.message_visible() { &session.message } else { &inspection };
    let color = match session.message_kind {
        MessageKind::Info => Color::Gray,
        MessageKind::Success => Color::LightGreen,
        MessageKind::Error => Color::LightRed,
    };
    frame.render_widget(
        Paragraph::new(format!(" {message}")).style(Style::default().fg(color)),
        area,
    );
}

fn render_confirmation<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
) {
    let (popup, _) = confirmation_geometry(area);
    frame.render_widget(Clear, popup);
    let preview = session.preview.as_ref().expect("checked by caller");
    let kind = match preview.kind {
        PlacementKind::Road => "Build road",
        PlacementKind::Building => "Place building",
        PlacementKind::Demolish => "Demolish",
    };
    frame.render_widget(
        Paragraph::new(vec![
            Line::raw(preview.summary()),
            Line::raw(""),
            Line::styled(
                " [ YES: Enter/Y ]          [ NO: Esc/N ] ",
                Style::default().fg(Color::Black).bg(Color::Yellow),
            ),
        ])
        .alignment(Alignment::Center)
        .block(Block::bordered().title(format!(" {kind}? "))),
        popup,
    );
}

fn render_prompt<F: SessionFileSystem>(
    frame: &mut Frame<'_>,
    area: Rect,
    session: &GameSession<F>,
    selected: usize,
) {
    let prompt = session.prompt.as_ref().expect("checked by caller");
    let (popup, _) =
        prompt_geometry(area, &prompt.text, prompt.input.is_some(), prompt.choices.len());
    frame.render_widget(Clear, popup);
    let mut lines = prompt.text.lines().map(Line::raw).collect::<Vec<_>>();
    lines.push(Line::raw(""));
    if let Some(input) = &prompt.input {
        lines.push(Line::styled(
            format!("> {input}"),
            Style::default().fg(Color::White).bg(Color::DarkGray),
        ));
        lines.push(Line::raw(""));
    }
    for (index, choice) in prompt.choices.iter().enumerate() {
        let style = if index == selected {
            Style::default().fg(Color::Black).bg(Color::Cyan).add_modifier(Modifier::BOLD)
        } else {
            Style::default()
        };
        lines.push(Line::styled(
            format!(" {} {}", if index == selected { "▶" } else { " " }, choice.label),
            style,
        ));
    }
    if let Some(footer) = &prompt.footer {
        lines.push(Line::raw(""));
        lines.push(Line::styled(footer, Style::default().fg(Color::DarkGray)));
    }
    frame.render_widget(
        Paragraph::new(lines).block(Block::bordered().title(format!(" {} ", prompt.title))),
        popup,
    );
}

fn render_help(frame: &mut Frame<'_>, area: Rect) {
    let popup = centered(area, 76, 23);
    frame.render_widget(Clear, popup);
    let text = "\
Move cursor   Arrows (Ctrl/Alt/Page/Home/End: full-screen); click
Pan map       Left-drag; wheel; Alt+wheel horizontal; minimap; E edges
Zoom          + / - / 0; Ctrl+wheel; click zoom bar
Select        Shift/Ctrl/Alt+click-drag; Shift+Arrows; S, arrows, S
Area menu     Right-click or Enter/M (opens road preview)
Zone          R homes / C shops / I factories; U dezone
Road          B preview; T straight-line tool
Demolish      D/Delete preview (free; no refund)
Preview       Enter/Y confirms; Esc/N cancels
Undo          Ctrl+Z, full-city rollback with confirmation
Clock         Space/P pause; 1 slow, 2 medium, 3 fast
Panel         F2/F3/F4 fold demand/city/zones
Game          F10 menu; F5 save; F9 load; Ctrl+Q quit
Reports       F7 weekly/milestones; F8 growth explanations
Guide         F6 show/dismiss; F12 diagnostics

Dezoned buildings leave after a delay. Zones are free; roads enable growth.
Press any key or click to close.";
    frame.render_widget(
        Paragraph::new(text)
            .wrap(Wrap { trim: false })
            .block(Block::bordered().title(" TERMCITY HELP ")),
        popup,
    );
}

fn render_context(frame: &mut Frame<'_>, area: Rect, selected: usize) {
    let popup = centered(
        area,
        42,
        u16::try_from(CONTEXT_CHOICES.len()).unwrap_or(u16::MAX).saturating_add(2),
    );
    frame.render_widget(Clear, popup);
    let lines = CONTEXT_CHOICES
        .iter()
        .enumerate()
        .map(|(index, choice)| {
            let style = if index == selected {
                Style::default().fg(Color::Black).bg(Color::Cyan).add_modifier(Modifier::BOLD)
            } else {
                Style::default()
            };
            Line::styled(format!(" {} {choice}", if index == selected { "▶" } else { " " }), style)
        })
        .collect::<Vec<_>>();
    frame.render_widget(
        Paragraph::new(lines).block(Block::bordered().title(" AREA ACTIONS ")),
        popup,
    );
}

fn rgb(value: Rgb) -> Color {
    Color::Rgb(value.r, value.g, value.b)
}

fn inner(area: Rect) -> Rect {
    Block::bordered().inner(area)
}

fn centered(area: Rect, width: u16, height: u16) -> Rect {
    let width = width.min(area.width);
    let height = height.min(area.height);
    Rect::new(
        area.x + area.width.saturating_sub(width) / 2,
        area.y + area.height.saturating_sub(height) / 2,
        width,
        height,
    )
}

fn confirmation_geometry(area: Rect) -> (Rect, u16) {
    let popup = centered(area, 56, 7);
    (popup, popup.y.saturating_add(3))
}

fn prompt_geometry(area: Rect, text: &str, has_input: bool, choice_count: usize) -> (Rect, u16) {
    let text_rows = u16::try_from(text.lines().count()).unwrap_or(u16::MAX);
    let input_rows = if has_input { 2 } else { 0 };
    let content_rows = text_rows
        .saturating_add(1)
        .saturating_add(input_rows)
        .saturating_add(u16::try_from(choice_count).unwrap_or(u16::MAX));
    let height = content_rows.saturating_add(2);
    let popup = centered(area, 68, height);
    let first_choice =
        popup.y.saturating_add(1).saturating_add(text_rows).saturating_add(1 + input_rows);
    (popup, first_choice)
}

fn zoom_level_at(x: u16, area: Rect) -> i32 {
    let relative = x.saturating_sub(area.x + 1);
    let segment = area.width.saturating_sub(2).max(1) / 4;
    i32::from((relative / segment.max(1)).min(3)) - 2
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SchedulerConfig {
    poll_interval: Duration,
    simulation_interval: Duration,
    camera_interval: Duration,
    cursor_interval: Duration,
    stall_threshold: Duration,
}

impl SchedulerConfig {
    #[must_use]
    pub fn for_fps(fps: u16) -> Self {
        Self {
            poll_interval: Duration::from_millis(8),
            simulation_interval: Duration::from_millis(100),
            camera_interval: Duration::from_nanos(1_000_000_000 / u64::from(fps)),
            cursor_interval: Duration::from_millis(15),
            stall_threshold: Duration::from_millis(400),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScheduleDecision {
    pub update: Option<Duration>,
    pub redraw: RedrawRequest,
    pub timeout: Duration,
    pub stalled: bool,
}

pub struct Scheduler {
    config: SchedulerConfig,
    last: Duration,
    last_update: Duration,
    last_camera: Option<Duration>,
    last_cursor: Option<Duration>,
    pending: RedrawRequest,
}

impl Scheduler {
    #[must_use]
    pub fn new(config: SchedulerConfig, now: Duration) -> Self {
        Self {
            config,
            last: now,
            last_update: now,
            last_camera: None,
            last_cursor: None,
            pending: RedrawRequest::Full,
        }
    }

    pub fn request(&mut self, redraw: RedrawRequest) {
        self.pending = self.pending.merge(redraw);
    }

    pub fn advance(&mut self, now: Duration) -> ScheduleDecision {
        let stalled =
            now < self.last || now.saturating_sub(self.last) > self.config.stall_threshold;
        if stalled {
            self.last_update = now;
            self.last_camera = None;
            self.last_cursor = None;
        }
        self.last = now;
        let update = (now.saturating_sub(self.last_update) >= self.config.simulation_interval)
            .then(|| {
                let elapsed = now.saturating_sub(self.last_update);
                self.last_update = now;
                elapsed
            });
        let full_due = self.pending == RedrawRequest::Full
            && self
                .last_camera
                .is_none_or(|last| now.saturating_sub(last) >= self.config.camera_interval);
        let cursor_due = self.pending == RedrawRequest::Cursor
            && self
                .last_cursor
                .is_none_or(|last| now.saturating_sub(last) >= self.config.cursor_interval);
        let redraw = if full_due {
            self.last_camera = Some(now);
            self.last_cursor = Some(now);
            self.pending = RedrawRequest::None;
            RedrawRequest::Full
        } else if cursor_due {
            self.last_cursor = Some(now);
            self.pending = RedrawRequest::None;
            RedrawRequest::Cursor
        } else {
            RedrawRequest::None
        };
        let next_update = self.last_update.saturating_add(self.config.simulation_interval);
        let timeout = self.config.poll_interval.min(next_update.saturating_sub(now));
        ScheduleDecision { update, redraw, timeout, stalled }
    }
}

struct TerminalSession<W: Write> {
    terminal: Terminal<CrosstermBackend<W>>,
}

impl<W: Write> TerminalSession<W> {
    fn enter(mut writer: W) -> io::Result<Self> {
        enable_raw_mode()?;
        if let Err(error) = execute!(writer, EnterAlternateScreen, EnableMouseCapture) {
            restore_terminal();
            return Err(error);
        }
        match Terminal::new(CrosstermBackend::new(writer)) {
            Ok(terminal) => Ok(Self { terminal }),
            Err(error) => {
                restore_terminal();
                Err(error)
            }
        }
    }

    fn draw<F: SessionFileSystem>(&mut self, app: &App<F>) -> io::Result<()> {
        self.terminal.draw(|frame| render(frame, app))?;
        Ok(())
    }
}

impl<W: Write> Drop for TerminalSession<W> {
    fn drop(&mut self) {
        let _ = disable_raw_mode();
        let _ =
            execute!(self.terminal.backend_mut(), LeaveAlternateScreen, DisableMouseCapture, Show);
    }
}

type PanicHook = dyn Fn(&panic::PanicHookInfo<'_>) + Send + Sync + 'static;

struct ScopedPanicHook(Arc<PanicHook>);

impl ScopedPanicHook {
    fn install() -> Self {
        let previous = Arc::<PanicHook>::from(panic::take_hook());
        let delegated = Arc::clone(&previous);
        panic::set_hook(Box::new(move |info| {
            restore_terminal();
            delegated(info);
        }));
        Self(previous)
    }
}

impl Drop for ScopedPanicHook {
    fn drop(&mut self) {
        if !std::thread::panicking() {
            let previous = Arc::clone(&self.0);
            panic::set_hook(Box::new(move |info| previous(info)));
        }
    }
}

fn restore_terminal() {
    let _ = disable_raw_mode();
    let _ = execute!(io::stdout(), LeaveAlternateScreen, DisableMouseCapture, Show);
}

/// Runs the Crossterm/Ratatui event loop until the session requests exit.
///
/// # Errors
///
/// Returns terminal setup, input, drawing, or session errors after restoring terminal state.
pub fn run_interactive(session: GameSession, fps: u16) -> Result<(), String> {
    let _panic_hook = ScopedPanicHook::install();
    let mut terminal = TerminalSession::enter(io::stdout()).map_err(|error| error.to_string())?;
    let size = crossterm::terminal::size().map_err(|error| error.to_string())?;
    let mut app = App::new(session, size.0, size.1);
    let start = Instant::now();
    let mut scheduler = Scheduler::new(SchedulerConfig::for_fps(fps), Duration::ZERO);
    while !app.should_quit {
        let now = start.elapsed();
        let decision = scheduler.advance(now);
        if let Some(elapsed) = decision.update {
            if !decision.stalled {
                scheduler.request(app.update(elapsed).map_err(|error| error.to_string())?);
            }
            app.session.record_loop_gap(u64::try_from(elapsed.as_millis()).unwrap_or(u64::MAX));
        }
        if decision.redraw != RedrawRequest::None {
            terminal.draw(&app).map_err(|error| error.to_string())?;
        }
        if event::poll(decision.timeout).map_err(|error| error.to_string())? {
            let input = event::read().map_err(|error| error.to_string())?;
            scheduler.request(app.handle_event(&input).map_err(|error| error.to_string())?);
        }
    }
    Ok(())
}
