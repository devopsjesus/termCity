/// Picks a stable variant index for a cell coordinate.
///
/// # Panics
///
/// Panics when `count` is zero.
#[must_use]
pub fn pick_cell_variant(x: i32, y: i32, count: usize) -> usize {
    assert!(count > 0, "variant count must be positive");

    let unsigned_x = u32::from_ne_bytes(x.to_ne_bytes());
    let unsigned_y = u32::from_ne_bytes(y.to_ne_bytes());
    let mut hash =
        unsigned_x.wrapping_mul(374_761_393).wrapping_add(unsigned_y.wrapping_mul(668_265_263));
    hash = (hash ^ (hash >> 13)).wrapping_mul(1_274_126_177);
    hash ^= hash >> 16;
    hash = hash.wrapping_mul(2_246_822_519);
    hash ^= hash >> 15;

    usize::try_from(u64::from(hash) % u64::try_from(count).expect("usize must fit in u64"))
        .expect("result is less than count")
}
