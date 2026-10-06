use crate::{GameRandom, Rgb};

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
#[repr(u8)]
pub enum ZoneType {
    #[default]
    None = 0,
    Residential = 1,
    Commercial = 2,
    Industrial = 3,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ZoneInfo {
    pub zone_type: ZoneType,
    pub name: &'static str,
    pub letter: char,
    pub empty_glyph: &'static str,
    pub foreground: Rgb,
    pub background: Rgb,
    pub weekly_value: i32,
}

pub const RESIDENTIAL_ZONE: ZoneInfo = ZoneInfo {
    zone_type: ZoneType::Residential,
    name: "Residential",
    letter: 'R',
    empty_glyph: "░",
    foreground: Rgb::hex(0x58_d0_68),
    background: Rgb::hex(0x1f_4a_28),
    weekly_value: 200,
};

pub const COMMERCIAL_ZONE: ZoneInfo = ZoneInfo {
    zone_type: ZoneType::Commercial,
    name: "Commercial",
    letter: 'C',
    empty_glyph: "░",
    foreground: Rgb::hex(0x58_a6_ff),
    background: Rgb::hex(0x1b_3a_66),
    weekly_value: 350,
};

pub const INDUSTRIAL_ZONE: ZoneInfo = ZoneInfo {
    zone_type: ZoneType::Industrial,
    name: "Industrial",
    letter: 'I',
    empty_glyph: "░",
    foreground: Rgb::hex(0xf2_c9_4c),
    background: Rgb::hex(0x57_48_1a),
    weekly_value: 500,
};

pub const PLACEABLE_ZONES: [ZoneType; 3] =
    [ZoneType::Residential, ZoneType::Commercial, ZoneType::Industrial];

impl ZoneType {
    #[must_use]
    pub const fn info(self) -> Option<&'static ZoneInfo> {
        match self {
            Self::Residential => Some(&RESIDENTIAL_ZONE),
            Self::Commercial => Some(&COMMERCIAL_ZONE),
            Self::Industrial => Some(&INDUSTRIAL_ZONE),
            Self::None => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct Household {
    pub adults: u8,
    pub children: u8,
    pub seniors: u8,
}

impl Household {
    const ADULT_WEIGHTS: [i32; 4] = [0, 20, 60, 20];
    const CHILD_WEIGHTS: [i32; 5] = [15, 20, 30, 20, 15];
    const SENIOR_WEIGHTS: [i32; 3] = [65, 15, 20];

    #[must_use]
    pub const fn new(adults: u8, children: u8, seniors: u8) -> Self {
        Self { adults, children, seniors }
    }

    #[must_use]
    pub const fn total(self) -> u16 {
        self.adults as u16 + self.children as u16 + self.seniors as u16
    }

    #[must_use]
    pub const fn is_empty(self) -> bool {
        self.total() == 0
    }

    /// Generates a household from the built-in population weights.
    ///
    /// # Panics
    ///
    /// Panics only if the fixed weight tables are changed so their selected indexes no longer fit in a byte.
    pub fn random(random: &mut GameRandom) -> Self {
        Self::new(
            u8::try_from(random.weighted(&Self::ADULT_WEIGHTS))
                .expect("adult count fits in a byte"),
            u8::try_from(random.weighted(&Self::CHILD_WEIGHTS))
                .expect("child count fits in a byte"),
            u8::try_from(random.weighted(&Self::SENIOR_WEIGHTS))
                .expect("senior count fits in a byte"),
        )
    }
}
