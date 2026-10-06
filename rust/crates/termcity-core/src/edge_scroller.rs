#![allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]

/// Direction and depth of a pointer inside the edge-scroll bands.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct EdgeZone {
    pub dx: i32,
    pub depth_x: usize,
    pub dy: i32,
    pub depth_y: usize,
}

/// Converts a pointer held near a viewport edge into accelerated whole-cell movement.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct EdgeScroller {
    carry_x: f64,
    carry_y: f64,
}

impl EdgeScroller {
    pub const HORIZONTAL_ZONE: i32 = 3;
    pub const VERTICAL_ZONE: i32 = 2;
    const HORIZONTAL_SPEED: [f64; 4] = [0.0, 12.0, 28.0, 48.0];
    const VERTICAL_SPEED: [f64; 3] = [0.0, 6.0, 12.0];

    #[must_use]
    pub const fn zone(x: i32, y: i32, width: i32, height: i32) -> EdgeZone {
        let mut zone = EdgeZone { dx: 0, depth_x: 0, dy: 0, depth_y: 0 };
        if width >= 2 * Self::HORIZONTAL_ZONE + 2 {
            if x < Self::HORIZONTAL_ZONE {
                zone.dx = -1;
                zone.depth_x = (Self::HORIZONTAL_ZONE - if x < 0 { 0 } else { x }) as usize;
            } else if x >= width - Self::HORIZONTAL_ZONE {
                zone.dx = 1;
                zone.depth_x = (x - (width - Self::HORIZONTAL_ZONE - 1)) as usize;
            }
            if zone.depth_x > Self::HORIZONTAL_ZONE as usize {
                zone.depth_x = Self::HORIZONTAL_ZONE as usize;
            }
        }
        if height >= 2 * Self::VERTICAL_ZONE + 2 {
            if y < Self::VERTICAL_ZONE {
                zone.dy = -1;
                zone.depth_y = (Self::VERTICAL_ZONE - if y < 0 { 0 } else { y }) as usize;
            } else if y >= height - Self::VERTICAL_ZONE {
                zone.dy = 1;
                zone.depth_y = (y - (height - Self::VERTICAL_ZONE - 1)) as usize;
            }
            if zone.depth_y > Self::VERTICAL_ZONE as usize {
                zone.depth_y = Self::VERTICAL_ZONE as usize;
            }
        }
        zone
    }

    #[must_use]
    pub fn step(
        &mut self,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        elapsed_seconds: f64,
    ) -> (i32, i32) {
        let zone = Self::zone(x, y, width, height);
        self.carry_x = if zone.dx == 0 {
            0.0
        } else {
            self.carry_x
                + f64::from(zone.dx) * Self::HORIZONTAL_SPEED[zone.depth_x] * elapsed_seconds
        };
        self.carry_y = if zone.dy == 0 {
            0.0
        } else {
            self.carry_y + f64::from(zone.dy) * Self::VERTICAL_SPEED[zone.depth_y] * elapsed_seconds
        };
        let step_x = self.carry_x.trunc() as i32;
        let step_y = self.carry_y.trunc() as i32;
        self.carry_x -= f64::from(step_x);
        self.carry_y -= f64::from(step_y);
        (step_x, step_y)
    }

    pub const fn reset(&mut self) {
        self.carry_x = 0.0;
        self.carry_y = 0.0;
    }
}
