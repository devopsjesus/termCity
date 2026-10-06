/// Small deterministic `SplitMix64` pseudo-random number generator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct GameRandom {
    state: u64,
}

impl GameRandom {
    #[must_use]
    pub const fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    #[must_use]
    pub const fn from_i32_seed(seed: i32) -> Self {
        let unsigned_seed = u32::from_ne_bytes(seed.to_ne_bytes());
        Self::new(
            (unsigned_seed as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15).wrapping_add(0x0123_4567),
        )
    }

    #[must_use]
    pub const fn state(self) -> u64 {
        self.state
    }

    pub const fn set_state(&mut self, state: u64) {
        self.state = state;
    }

    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut value = self.state;
        value = (value ^ (value >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        value = (value ^ (value >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        value ^ (value >> 31)
    }

    /// Returns a value in `0..max_exclusive`.
    ///
    /// # Panics
    ///
    /// Panics when `max_exclusive` is not positive.
    pub fn next(&mut self, max_exclusive: i32) -> i32 {
        assert!(max_exclusive > 0, "upper bound must be positive");
        let value = self.next_u64() % u64::from(max_exclusive.unsigned_abs());
        i32::try_from(value).expect("value is below the i32 upper bound")
    }

    /// Returns a value in `min_inclusive..max_exclusive`.
    ///
    /// # Panics
    ///
    /// Panics when the range is empty or its width exceeds [`i32::MAX`].
    pub fn next_range(&mut self, min_inclusive: i32, max_exclusive: i32) -> i32 {
        let width = max_exclusive.checked_sub(min_inclusive).expect("range width must fit in i32");
        min_inclusive + self.next(width)
    }

    #[allow(clippy::cast_precision_loss)]
    pub fn next_f64(&mut self) -> f64 {
        ((self.next_u64() >> 11) as f64) * (1.0 / ((1_u64 << 53) as f64))
    }

    pub fn chance(&mut self, probability: f64) -> bool {
        self.next_f64() < probability
    }

    /// Picks an index according to the supplied relative weights.
    ///
    /// # Panics
    ///
    /// Panics when the weights do not sum to a positive value or their sum overflows an `i32`.
    pub fn weighted(&mut self, weights: &[i32]) -> usize {
        let total = weights
            .iter()
            .copied()
            .try_fold(0_i32, i32::checked_add)
            .expect("weight total must fit in i32");
        let mut roll = self.next(total);

        for (index, weight) in weights.iter().copied().enumerate() {
            roll -= weight;
            if roll < 0 {
                return index;
            }
        }

        weights.len() - 1
    }

    /// Creates an independent stream derived from a seed and stable stage name.
    #[must_use]
    pub fn for_stage(seed: i32, stage: &str) -> Self {
        let mut hash = 14_695_981_039_346_656_037_u64;
        for code_unit in stage.encode_utf16() {
            hash = (hash ^ u64::from(code_unit)).wrapping_mul(1_099_511_628_211);
        }

        let unsigned_seed = u32::from_ne_bytes(seed.to_ne_bytes());
        Self::new(hash ^ u64::from(unsigned_seed).wrapping_mul(0x9E37_79B9_7F4A_7C15))
    }
}
