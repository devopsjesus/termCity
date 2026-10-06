use termcity_core::{CellRect, GameRandom, PerlinNoise, Pos, Rgb, pick_cell_variant};

fn assert_near(actual: f64, expected: f64) {
    assert!((actual - expected).abs() <= 1.0e-14, "expected {expected:.17}, got {actual:.17}");
}

#[test]
fn positions_and_rectangles_match_inclusive_csharp_geometry() {
    let start = Pos::new(4, -2);
    assert_eq!(start.offset(-3, 5), Pos::new(1, 3));

    let rect = CellRect::from_corners(Pos::new(3, 4), Pos::new(1, 2));
    assert_eq!(rect, CellRect::new(1, 2, 3, 3));
    assert_eq!(rect.right(), 3);
    assert_eq!(rect.bottom(), 4);
    assert_eq!(rect.area(), 9);
    assert!(rect.contains(Pos::new(2, 3)));
    assert!(!rect.contains_xy(4, 3));
    assert!(rect.intersects(CellRect::new(3, 4, 2, 2)));
    assert!(rect.intersects(CellRect::single(Pos::new(2, 3))));
    assert!(!rect.intersects(CellRect::new(4, 2, 2, 3)));
    assert!(!rect.intersects(CellRect::new(1, 5, 3, 1)));
    assert!(!rect.intersects(CellRect::new(1, 2, 0, 3)));
    assert!(!CellRect::new(1, 2, 3, 0).intersects(rect));
    assert_eq!(
        rect.cells().collect::<Vec<_>>(),
        vec![
            Pos::new(1, 2),
            Pos::new(2, 2),
            Pos::new(3, 2),
            Pos::new(1, 3),
            Pos::new(2, 3),
            Pos::new(3, 3),
            Pos::new(1, 4),
            Pos::new(2, 4),
            Pos::new(3, 4),
        ]
    );
    assert_eq!(CellRect::single(start).to_string(), "(4,-2)");
    assert_eq!(rect.to_string(), "(1,2) 3x3");
}

#[test]
fn rgb_math_matches_csharp_rounding_and_clamping() {
    assert_eq!(Rgb::hex(0x12_34_56), Rgb::new(0x12, 0x34, 0x56));
    assert_eq!(Rgb::blend(Rgb::new(0, 1, 2), Rgb::new(1, 2, 3), 0.5), Rgb::new(0, 2, 2));
    assert_eq!(Rgb::new(101, 200, 255).scale(0.5), Rgb::new(50, 100, 127));
    assert_eq!(Rgb::new(100, 200, 250).scale(2.0), Rgb::new(200, 255, 255));
}

#[test]
fn cell_hash_matches_csharp_vectors() {
    assert_eq!(pick_cell_variant(0, 0, 7), 0);
    assert_eq!(pick_cell_variant(1, 2, 7), 1);
    assert_eq!(pick_cell_variant(-1, 2, 7), 6);
    assert_eq!(pick_cell_variant(12_345, -6_789, 19), 15);
}

#[test]
fn splitmix64_sequence_and_state_match_csharp() {
    let mut random = GameRandom::new(0);
    assert_eq!(
        [
            random.next_u64(),
            random.next_u64(),
            random.next_u64(),
            random.next_u64(),
            random.next_u64(),
        ],
        [
            16_294_208_416_658_607_535,
            7_960_286_522_194_355_700,
            487_617_019_471_545_679,
            17_909_611_376_780_542_444,
            1_961_750_202_426_094_747,
        ]
    );
    assert_eq!(random.state(), 1_663_341_875_487_337_577);

    random.set_state(0);
    assert_eq!(random.next(10), 5);
    random.set_state(0);
    assert_near(random.next_f64(), 0.883_310_808_213_642_6);
}

#[test]
fn stage_streams_match_csharp_utf16_hashing() {
    assert_eq!(GameRandom::for_stage(123, "hills").state(), 0x0cdc_908b_b5b2_412c);
    assert_eq!(GameRandom::for_stage(-7, "water").state(), 0xf80c_e54e_0613_251d);
    assert_eq!(GameRandom::for_stage(1, "x😀").state(), 0xaec8_35a2_ecf0_985f);
}

#[test]
fn weighted_selection_is_deterministic() {
    let mut random = GameRandom::new(0);
    assert_eq!(random.weighted(&[1, 3, 6]), 2);
    assert_eq!(random.weighted(&[1, 3, 6]), 0);
    assert_eq!(random.weighted(&[1, 3, 6]), 2);
}

#[test]
fn perlin_noise_matches_csharp_seeded_samples() {
    let noise = PerlinNoise::new(GameRandom::from_i32_seed(42));
    for (x, y, expected_noise, expected_fractal) in [
        (0.0, 0.0, 0.0, 0.5),
        (0.25, 0.75, -0.371_428_184_509_277_34, 0.545_399_090_140_206_5),
        (-1.2, 3.4, -0.492_647_247_323_136, 0.388_207_878_799_945_2),
        (12.345, -67.89, 0.511_464_068_620_818_3, 0.788_593_760_428_757_4),
    ] {
        assert_near(noise.noise(x, y), expected_noise);
        assert_near(noise.fractal(x, y, 3, 0.5), expected_fractal);
    }
}

#[test]
fn coverage_threshold_matches_csharp_indexing() {
    assert_eq!(PerlinNoise::threshold_for_coverage(&[0.9, 0.1, 0.5, 0.7, 0.3], 0.4), 0.7);
}
