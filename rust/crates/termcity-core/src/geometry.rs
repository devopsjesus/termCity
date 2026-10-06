use std::fmt;

/// A map cell coordinate.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct Pos {
    pub x: i32,
    pub y: i32,
}

impl Pos {
    #[must_use]
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }

    #[must_use]
    pub const fn offset(self, dx: i32, dy: i32) -> Self {
        Self::new(self.x + dx, self.y + dy)
    }
}

/// An inclusive rectangle of map cells.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct CellRect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl CellRect {
    #[must_use]
    pub const fn new(x: i32, y: i32, width: i32, height: i32) -> Self {
        Self { x, y, width, height }
    }

    #[must_use]
    pub const fn right(self) -> i32 {
        self.x + self.width - 1
    }

    #[must_use]
    pub const fn bottom(self) -> i32 {
        self.y + self.height - 1
    }

    #[must_use]
    pub const fn area(self) -> i32 {
        self.width * self.height
    }

    #[must_use]
    pub fn from_corners(a: Pos, b: Pos) -> Self {
        Self::new(a.x.min(b.x), a.y.min(b.y), (a.x - b.x).abs() + 1, (a.y - b.y).abs() + 1)
    }

    #[must_use]
    pub const fn single(pos: Pos) -> Self {
        Self::new(pos.x, pos.y, 1, 1)
    }

    #[must_use]
    pub const fn contains_xy(self, x: i32, y: i32) -> bool {
        x >= self.x && x <= self.right() && y >= self.y && y <= self.bottom()
    }

    #[must_use]
    pub const fn contains(self, pos: Pos) -> bool {
        self.contains_xy(pos.x, pos.y)
    }

    #[must_use]
    pub const fn intersects(self, other: Self) -> bool {
        self.width > 0
            && self.height > 0
            && other.width > 0
            && other.height > 0
            && self.x <= other.right()
            && self.right() >= other.x
            && self.y <= other.bottom()
            && self.bottom() >= other.y
    }

    #[must_use]
    pub fn cells(self) -> CellRectCells {
        CellRectCells::new(self)
    }
}

impl fmt::Display for CellRect {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.area() == 1 {
            write!(formatter, "({},{})", self.x, self.y)
        } else {
            write!(formatter, "({},{}) {}x{}", self.x, self.y, self.width, self.height)
        }
    }
}

/// Row-major iterator over the cells in a [`CellRect`].
#[derive(Clone, Debug)]
pub struct CellRectCells {
    rect: CellRect,
    next: Option<Pos>,
}

impl CellRectCells {
    const fn new(rect: CellRect) -> Self {
        let next =
            if rect.width > 0 && rect.height > 0 { Some(Pos::new(rect.x, rect.y)) } else { None };
        Self { rect, next }
    }
}

impl Iterator for CellRectCells {
    type Item = Pos;

    fn next(&mut self) -> Option<Self::Item> {
        let current = self.next?;
        self.next = if current.x < self.rect.right() {
            Some(Pos::new(current.x + 1, current.y))
        } else if current.y < self.rect.bottom() {
            Some(Pos::new(self.rect.x, current.y + 1))
        } else {
            None
        };
        Some(current)
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        let remaining = self.next.map_or(0, |next| {
            let rows_after = i64::from(self.rect.bottom() - next.y);
            let cells_in_later_rows = rows_after * i64::from(self.rect.width);
            let cells_in_current_row = i64::from(self.rect.right() - next.x + 1);
            usize::try_from(cells_in_later_rows + cells_in_current_row).unwrap_or(usize::MAX)
        });
        (remaining, Some(remaining))
    }
}

impl ExactSizeIterator for CellRectCells {}

/// A 24-bit color, independent of any UI representation.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct Rgb {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Rgb {
    #[must_use]
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }

    #[must_use]
    pub const fn hex(rgb: u32) -> Self {
        let [_, red, green, blue] = rgb.to_be_bytes();
        Self::new(red, green, blue)
    }

    #[must_use]
    pub fn blend(a: Self, b: Self, t: f64) -> Self {
        Self::new(
            blend_component(a.r, b.r, t),
            blend_component(a.g, b.g, t),
            blend_component(a.b, b.b, t),
        )
    }

    #[must_use]
    pub fn scale(self, factor: f64) -> Self {
        Self::new(
            scale_component(self.r, factor),
            scale_component(self.g, factor),
            scale_component(self.b, factor),
        )
    }
}

#[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
fn blend_component(a: u8, b: u8, t: f64) -> u8 {
    (f64::from(a) + (f64::from(b) - f64::from(a)) * t).round_ties_even() as u8
}

#[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
fn scale_component(value: u8, factor: f64) -> u8 {
    (f64::from(value) * factor).clamp(0.0, 255.0) as u8
}
