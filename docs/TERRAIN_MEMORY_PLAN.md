# Terrain memory reduction

## Direction

Terminal conventions apply to the UI only. Perspective map artwork and resource
layouts must use world-space proportions, not terminal glyph dimensions.
Keep original block-built artwork, terrain topology, road geometry and picking.

## Implementation sequence

1. Measure the existing medium terrain allocation and capture normal/overview
   artwork before changes.
2. Replace 192x352 chunk color textures with directly generated 128x128 RGBA
   textures: eight square texels per logical cell, regular broad color patches,
   deterministic world-coordinate variation. Do not resize terminal sprites.
3. Remove 12x22 terrain shader sampling assumptions. Derive cell coordinates and
   patch grouping from world space; retain river direction, phase and shoreline
   data independently of color-texture dimensions.
4. Verify exact texture sizes/byte totals, whole-cell categories, deterministic
   adjacent chunks, save preservation and native terrain/road geometry. Inspect
   captures, rotation, zoom, picking and previews in graphical integration.
5. Repeat the same full-display medium navigation workload and report bytes,
   uploads, frame/readiness latency and any remaining failures. Keep UI, cache
   targets, native panel resolution and antialiasing unchanged.

The color-texture tier should shrink from approximately 309 MiB to 75 MiB across
900 medium chunks including mipmaps (about 76% less). Meshes, flow/shore images,
CPU metadata and driver allocations are additional costs, not part of that figure.

Shared terrain material atlases/compact category maps and indexed meshes were
subsequently implemented as the next measured step. The existing terminal-oriented renderer may remain for
intentional overhead/legacy UI compatibility; it must not supply perspective
terrain artwork or constrain future world rendering.

## Implemented

Perspective texture generation now uses `WorldTerrainTexture`, independent of
the terminal sprite rasterizer. Every color chunk is 128x128, with four-texel
square patches and palette variation indexed in world coordinates. Shader
category, patch and river-cell sampling no longer assumes 12x22 pixels.
Terrain geometry, float flow/shore data and the intentional UI remain unchanged.
The project guidance records terminal styling as UI-only for future work.

The initial square-texture step removed approximately 76% of color-texture bytes.
The follow-up replaces those full-color images with a shared ten-color palette,
16x16 RGBA per-cell category/custom-color data and 32x32 byte variation data.
Tests reconstruct every original square-raster texel exactly, including category
boundaries and partial-map edges. No world-resolution or geometry LOD was added.

Terrain/road/bank meshes now share exact matching vertices through index buffers,
retaining separate UV, color and hard-normal seams. Indices are counted in CPU/GPU
accounting, rebased when pages combine surfaces, and traversed by native triangle
inspection. Logical picking triangles remain unchanged. An indexed representation
is selected only when smaller than the original packet.

The medium color/category/variation tier is now 1,843,240 bytes including the
single shared palette, versus 78,643,200 bytes for the square color texture tier.
Uncompressed float river/shore data and geometry remain separately accounted.
