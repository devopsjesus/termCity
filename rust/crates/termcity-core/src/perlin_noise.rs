use crate::GameRandom;

/// Seeded 2D gradient noise used for terrain and feature clustering.
#[derive(Clone, Debug)]
pub struct PerlinNoise {
    permutation: [usize; 512],
}

impl PerlinNoise {
    #[must_use]
    #[allow(clippy::cast_possible_truncation, clippy::cast_possible_wrap, clippy::cast_sign_loss)]
    pub fn new(mut random: GameRandom) -> Self {
        let mut shuffled = [0_usize; 256];
        for (index, value) in shuffled.iter_mut().enumerate() {
            *value = index;
        }

        for index in (1..256).rev() {
            let selected = random.next((index + 1) as i32) as usize;
            shuffled.swap(index, selected);
        }

        let mut permutation = [0_usize; 512];
        for (index, value) in permutation.iter_mut().enumerate() {
            *value = shuffled[index & 255];
        }

        Self { permutation }
    }

    /// Returns single-octave noise, roughly in `-1.0..=1.0`.
    #[must_use]
    #[allow(clippy::approx_constant, clippy::cast_possible_truncation, clippy::cast_sign_loss)]
    pub fn noise(&self, x: f64, y: f64) -> f64 {
        let x_floor = x.floor();
        let y_floor = y.floor();
        let xi = (x_floor as i32 & 255) as usize;
        let yi = (y_floor as i32 & 255) as usize;
        let xf = x - x_floor;
        let yf = y - y_floor;
        let u = fade(xf);
        let v = fade(yf);

        let aa = self.permutation[self.permutation[xi] + yi];
        let ab = self.permutation[self.permutation[xi] + yi + 1];
        let ba = self.permutation[self.permutation[xi + 1] + yi];
        let bb = self.permutation[self.permutation[xi + 1] + yi + 1];

        let x1 = lerp(gradient(aa, xf, yf), gradient(ba, xf - 1.0, yf), u);
        let x2 = lerp(gradient(ab, xf, yf - 1.0), gradient(bb, xf - 1.0, yf - 1.0), u);
        lerp(x1, x2, v) * 1.4142
    }

    /// Returns multi-octave noise normalized to roughly `0.0..=1.0`.
    #[must_use]
    #[allow(clippy::manual_midpoint)]
    pub fn fractal(&self, x: f64, y: f64, octaves: usize, persistence: f64) -> f64 {
        let mut sum = 0.0;
        let mut amplitude = 1.0;
        let mut frequency = 1.0;
        let mut maximum = 0.0;

        for _ in 0..octaves {
            sum += self.noise(x * frequency, y * frequency) * amplitude;
            maximum += amplitude;
            amplitude *= persistence;
            frequency *= 2.0;
        }

        ((sum / maximum + 1.0) / 2.0).clamp(0.0, 1.0)
    }

    /// Returns the sample threshold above which approximately `coverage` values lie.
    ///
    /// # Panics
    ///
    /// Panics when `values` is empty or contains `NaN`.
    #[must_use]
    #[allow(clippy::cast_possible_truncation, clippy::cast_precision_loss, clippy::cast_sign_loss)]
    pub fn threshold_for_coverage(values: &[f64], coverage: f64) -> f64 {
        assert!(!values.is_empty(), "values must not be empty");
        assert!(values.iter().all(|value| !value.is_nan()), "values must not contain NaN");
        let mut sorted = values.to_vec();
        sorted.sort_by(f64::total_cmp);

        let index = ((1.0 - coverage) * sorted.len() as f64) as usize;
        sorted[index.min(sorted.len() - 1)]
    }
}

fn fade(value: f64) -> f64 {
    value * value * value * (value * (value * 6.0 - 15.0) + 10.0)
}

fn lerp(a: f64, b: f64, amount: f64) -> f64 {
    a + amount * (b - a)
}

fn gradient(hash: usize, x: f64, y: f64) -> f64 {
    match hash & 3 {
        0 => x + y,
        1 => -x + y,
        2 => x - y,
        _ => -x - y,
    }
}
