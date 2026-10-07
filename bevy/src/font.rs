#![allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]

use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;

use ab_glyph::{Font, FontArc, PxScale, ScaleFont, point};
use bevy::asset::RenderAssetUsages;
use bevy::prelude::*;
use bevy::render::render_resource::{Extent3d, TextureDimension, TextureFormat};
use termcity_core::{GameContent, ZoneType};

pub const FONT_BYTES: &[u8] = include_bytes!("../assets/DejaVuSansMono.ttf");
pub const FONT_LICENSE: &str = include_str!("../assets/DejaVu-LICENSE.txt");
const TILE_WIDTH: u32 = 20;
const TILE_HEIGHT: u32 = 32;
const COLUMNS: u32 = 16;

pub struct AtlasData {
    pub pixels: Vec<u8>,
    pub rows: u32,
    pub indices: BTreeMap<char, usize>,
}

impl AtlasData {
    pub fn new(content: &GameContent) -> Result<Self, Box<dyn Error>> {
        let font = FontArc::try_from_vec(FONT_BYTES.to_vec())?;
        let mut glyphs = BTreeSet::from([' ']);
        for glyph in content
            .terrains
            .iter()
            .flat_map(|terrain| &terrain.glyphs)
            .chain(content.features.iter().flat_map(|feature| &feature.glyphs))
            .chain(content.buildings.iter().flat_map(|building| &building.glyphs))
            .chain(content.roads.iter().flat_map(|road| &road.glyphs))
            .copied()
            .chain(
                [ZoneType::Residential, ZoneType::Commercial, ZoneType::Industrial]
                    .into_iter()
                    .map(|zone| zone.info().expect("placeable zone").empty_glyph),
            )
        {
            let mut characters = glyph.chars();
            let character = characters.next().ok_or("empty map glyph")?;
            if characters.next().is_some() {
                return Err(format!("map glyph {glyph:?} is not a single character").into());
            }
            glyphs.insert(character);
        }
        let rows = u32::try_from(glyphs.len())?.div_ceil(COLUMNS);
        let width = COLUMNS * TILE_WIDTH;
        let mut pixels = vec![0; usize::try_from(width * rows * TILE_HEIGHT * 4)?];
        let mut indices = BTreeMap::new();
        let scaled = font.as_scaled(PxScale::from(24.0));
        for (index, character) in glyphs.into_iter().enumerate() {
            if font.glyph_id(character).0 == 0 {
                return Err(format!("bundled font is missing map glyph {character:?}").into());
            }
            indices.insert(character, index);
            let mut glyph = scaled.scaled_glyph(character);
            glyph.position = point((TILE_WIDTH as f32 - scaled.h_advance(glyph.id)) / 2.0, 25.0);
            let Some(outlined) = font.outline_glyph(glyph) else {
                if character == ' ' {
                    continue;
                }
                return Err(format!("map glyph {character:?} has no outline").into());
            };
            let bounds = outlined.px_bounds();
            if bounds.min.x < 0.0
                || bounds.min.y < 0.0
                || bounds.max.x > TILE_WIDTH as f32
                || bounds.max.y > TILE_HEIGHT as f32
            {
                return Err(format!("map glyph {character:?} exceeds its atlas cell").into());
            }
            let tile_x = u32::try_from(index)? % COLUMNS * TILE_WIDTH;
            let tile_y = u32::try_from(index)? / COLUMNS * TILE_HEIGHT;
            outlined.draw(|x, y, coverage| {
                let pixel_x = tile_x + x + bounds.min.x as u32;
                let pixel_y = tile_y + y + bounds.min.y as u32;
                let offset =
                    usize::try_from((pixel_y * width + pixel_x) * 4).expect("atlas offset");
                pixels[offset..offset + 4].copy_from_slice(&[
                    255,
                    255,
                    255,
                    (coverage * 255.0).round() as u8,
                ]);
            });
        }
        Ok(Self { pixels, rows, indices })
    }

    pub fn into_assets(
        self,
        images: &mut Assets<Image>,
        layouts: &mut Assets<TextureAtlasLayout>,
    ) -> GlyphAtlas {
        let image = Image::new(
            Extent3d {
                width: COLUMNS * TILE_WIDTH,
                height: self.rows * TILE_HEIGHT,
                depth_or_array_layers: 1,
            },
            TextureDimension::D2,
            self.pixels,
            TextureFormat::Rgba8UnormSrgb,
            RenderAssetUsages::default(),
        );
        GlyphAtlas {
            image: images.add(image),
            layout: layouts.add(TextureAtlasLayout::from_grid(
                UVec2::new(TILE_WIDTH, TILE_HEIGHT),
                COLUMNS,
                self.rows,
                None,
                None,
            )),
            indices: self.indices,
        }
    }
}

#[derive(Resource)]
pub struct GlyphAtlas {
    pub image: Handle<Image>,
    pub layout: Handle<TextureAtlasLayout>,
    pub indices: BTreeMap<char, usize>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn licensed_font_covers_every_registered_map_glyph() {
        assert!(FONT_LICENSE.contains("Permission"));
        let atlas = AtlasData::new(&GameContent::default()).unwrap();
        assert!(atlas.indices.len() > 30);
        assert!(atlas.pixels.iter().any(|byte| *byte != 0));
    }
}
