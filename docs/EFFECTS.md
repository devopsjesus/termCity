# Terminal effects

The map is a grid of terminal glyphs. The effect system makes it feel alive: demolished buildings shrink away as if
sucked into a point, new ones grow in, fires flicker and collapse into smoke, floods wash across the map, earthquakes
shake the screen, and cars, people, birds and factory smoke roam the city. Everything is drawn with glyphs from the
bundled DejaVu Sans Mono font; there are no image assets and no new packages.

## Layers

| Layer | Where | Knows about |
|---|---|---|
| Effect engine | `src/TermCity.Core/Effects/` | Nothing but numbers, glyph strings and `Rgb`. No Godot types. |
| Director | `EffectDirector` | `CityGame` / `GameSession` events, to spawn effects from changes |
| Planner | `godot/EffectDraw.cs` | Turns effect output into positioned glyph draws. Godot-free, unit tested. |
| Renderer | `godot/TerminalMap.cs` | Applies the plans with `DrawSetTransform` + `DrawString` |
| Wiring | `godot/Main.cs` | `V` key, Esc menu entry, persisted level, redraw throttling |

The simulation never references effects. The director observes it and spawns effects afterwards.

## Core model

`EffectSystem` owns a clock-driven timeline. `Update(dt)` advances time (clamped to 0.25 s per step so a stall cannot
fast-forward effects), runs scheduled callbacks, advances each `Effect`, and collects what they want drawn into an
`EffectSink`. Renderers query the result:

- `CellAt(x, y)` / `ActiveCells()`: per-cell `CellEffect` modifiers (replacement glyph, scale, offset in cell units
  with +Y down, foreground tint, background tint, alpha, and an `Overlay` flag).
- `Sprites`: free-floating glyphs at fractional map coordinates (a cell centre is `x + 0.5`).
- `Shake`: a whole-map translation in cell units.

`CellEffect.Overlay` matters: without it the glyph replaces the cell's own glyph (used when a building shrinks or
grows in place); with it the cell keeps its glyph and a ghost is drawn on top (used for flames, smoke, coins).

Effects are pure functions of their age, so they are deterministic. Randomness comes from `EffectRandom`, a stateless
hash keyed on the seed, so the same seed and the same sequence of updates give identical frames.

## Settings and budgets

`EffectSettings`: `Enabled`, `ReducedMotion` (accessibility, forces everything off and clears running effects),
`Intensity` (0 to 1, scales particle counts and ambient density), and `Level` (Off, Low = 0.5, High = 1). `Active` is
the single gate everything checks, so queries return nothing the moment effects are off, even before the next update.

Hard caps keep a frame cheap whatever happens: 1500 cell modifiers, 400 sprites, 48 ambient sprites, 48 live effects.
Over-budget output is dropped and counted (`DroppedLastFrame`, `Evicted`, `Rejected`). When the effect cap is hit the
oldest effect of equal or lower priority is evicted; a low-priority newcomer is rejected rather than evicting
something more important (fires outrank coins). Scheduled callbacks are bounded too.

## The director

`EffectDirector.Attach(session)` subscribes to the session and each frame `Update(dt)`:

1. diffs a `MapSnapshot` of the visible area against the previous one to find demolished, built, zoned and
   road-stroked cells, clustering them into groups (at most 600 cells each);
2. reads game events (fires, floods, earthquakes, outbreaks, abandonment, milestones, the weekly tax report);
3. keeps one `AmbientLife` effect running over the visible region.

Safeguards: more than 1500 changed cells at once (loading a save, a new city) is treated as a resync and produces no
effects; nothing is spawned while zoomed out (`Stride != 1`), because one glyph then represents many cells; and the
director updates after the session, so a change is detected before the frame that draws it (no flicker).

## Adding an effect

1. Subclass `Effect` in `src/TermCity.Core/Effects/`. Provide `Duration` (a persistent or ambient effect may override
   `IsPersistent` / `IsAmbient`), optionally `Priority`, and implement `Contribute(EffectSink sink)`.
2. In `Contribute`, compute everything from `LocalTime` (use `Easing` helpers) and write to the sink: `sink.AddCell(...)` for
   cell modifiers, `sink.AddSprite(...)` for particles, `sink.AddShake(...)` for screen shake. Stay inside the sink budgets;
   use `Settings.Scaled(n)` for particle counts so intensity and reduced motion apply.
3. Use only glyphs from `EffectGlyphs`. A test (`EffectGlyphFontTests`) parses the bundled font's cmap and fails if a
   glyph is missing from DejaVu Sans Mono, so a missing-glyph box cannot reach the screen.
4. Spawn it from `EffectDirector` where the triggering change or event is observed, via `_system.Spawn(...)`.
5. Add tests: a lifecycle test (it finishes), a determinism test, and a bounds test (scale and alpha stay in range).

## Rendering and its limits

`TerminalMap._Draw` runs a background pass (tinted backgrounds), a glyph pass, then overlay ghosts and sprites, with
the shake applied as a translation to everything. A transformed glyph is drawn by setting a canvas transform centred on
the cell and drawing the font string at the local origin, so scaling is about the cell centre.

Limits of per-glyph scaling:

- It scales the vector glyph uniformly, so wide glyphs stay wide: there is no independent horizontal or vertical
  squash, and no rotation is used.
- Glyphs below a scale of 0.05 or an alpha of 0.02 are skipped rather than drawn.
- Scales above about 1.3 spill into neighbouring cells (they overlap, nothing clips them), so effects stay below that.
- Small scales are still rasterised by the font renderer at the scaled size, so at some zoom levels they look slightly
  softer than native text.
- Effects are not drawn when zoomed out, and only the visible cells are ever processed.

## Performance notes

- No per-glyph nodes: everything is custom drawing in one `_Draw`, as before.
- With nothing running (`NeedsRedraw` false) the map does not redraw at all.
- One-shot effects and shake request a redraw every frame; ambient life alone redraws at 15 fps.
- Budgets bound the draw count; `TerminalMap.EffectGlyphsDrawn` exposes it for diagnostics.
- Ambient actors are spawned and retired based on the view, and the density follows `Intensity`.

## Controls

`V` cycles High, Low, Off (also under Esc > Effects) and the choice is saved in `display.cfg`. Start with
`--reduced-motion` to force effects off for the whole session; the toggle then only explains that.

## Verification status

Covered by unit tests (core effects, budgets, director, glyph coverage, `EffectDraw` planning, option parsing) and the
headless Godot smoke test (director wiring, spawn on build/demolish, no spawn while off, no replay on re-enable, menu
entry). The smoke test could not verify the look of the effects: that needs a human running the game with a window,
and the timing, softness of scaled glyphs, and ambient density should be judged by eye.

## Ideas not implemented

- Sound cues tied to effects (a suck-away whoosh, crackling fire).
- Weather (rain, snow, lightning) and a day/night tint driven by the calendar.
- Traffic that follows actual road congestion, trains and boats along rail and water.
- Rotation and non-uniform scaling for sprites (needs per-glyph transforms with a shear).
- Effects at zoomed-out levels (aggregate shimmer for a whole district).
- A user-adjustable intensity slider rather than three levels.
