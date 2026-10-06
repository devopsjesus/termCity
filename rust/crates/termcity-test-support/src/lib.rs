use std::cell::RefCell;
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::rc::Rc;

use ratatui::buffer::Buffer;
use termcity_core::{
    CityGame, GameConfig, GameContent, GameMap, GameRandom, GameSession, PerlinNoise,
    SessionFileSystem,
};

/// Creates the same seeded noise fixture used by core compatibility tests.
#[must_use]
pub fn seeded_noise(seed: i32) -> PerlinNoise {
    PerlinNoise::new(GameRandom::from_i32_seed(seed))
}

/// Creates a deterministic, generation-free game for UI and session tests.
///
/// # Panics
///
/// Panics when `width` and `height` are not valid core map dimensions.
#[must_use]
pub fn flat_game(width: i32, height: i32, seed: i32) -> CityGame {
    let config = GameConfig { map_width: width, map_height: height, seed, ..GameConfig::default() };
    let map =
        GameMap::new(width, height, GameContent::default()).expect("valid fixture dimensions");
    CityGame::from_map(config, map)
}

/// Creates a deterministic session backed by an in-memory file system.
#[must_use]
pub fn memory_session(width: i32, height: i32, seed: i32) -> GameSession<MemoryFileSystem> {
    GameSession::with_file_system(
        flat_game(width, height, seed),
        PathBuf::from("saves/quicksave-rust.json"),
        MemoryFileSystem::default(),
        false,
    )
}

/// A clonable deterministic file system for save/load tests.
#[derive(Clone, Default)]
pub struct MemoryFileSystem(Rc<RefCell<HashMap<PathBuf, String>>>);

impl MemoryFileSystem {
    #[must_use]
    pub fn contents(&self, path: impl AsRef<Path>) -> Option<String> {
        self.0.borrow().get(path.as_ref()).cloned()
    }

    pub fn insert(&self, path: impl Into<PathBuf>, contents: impl Into<String>) {
        self.0.borrow_mut().insert(path.into(), contents.into());
    }
}

impl SessionFileSystem for MemoryFileSystem {
    fn read_to_string(&self, path: &Path) -> io::Result<String> {
        self.0
            .borrow()
            .get(path)
            .cloned()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "fixture file not found"))
    }

    fn write_atomic(&mut self, path: &Path, contents: &str) -> io::Result<()> {
        self.0.borrow_mut().insert(path.to_owned(), contents.to_owned());
        Ok(())
    }

    fn copy(&mut self, source: &Path, destination: &Path) -> io::Result<()> {
        let contents = self.read_to_string(source)?;
        self.0.borrow_mut().insert(destination.to_owned(), contents);
        Ok(())
    }

    fn exists(&self, path: &Path) -> bool {
        self.0.borrow().contains_key(path)
    }
}

/// Converts a test-backend buffer into stable row-oriented text.
#[must_use]
pub fn buffer_text(buffer: &Buffer) -> String {
    buffer
        .content
        .chunks(usize::from(buffer.area.width))
        .map(|row| row.iter().map(ratatui::buffer::Cell::symbol).collect::<String>())
        .collect::<Vec<_>>()
        .join("\n")
}

/// Counts cells changed between two same-sized rendered frames.
#[must_use]
pub fn changed_cells(before: &Buffer, after: &Buffer) -> usize {
    if before.area != after.area {
        return after.content.len();
    }
    before.content.iter().zip(&after.content).filter(|(left, right)| left != right).count()
}
