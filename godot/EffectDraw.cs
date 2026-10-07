using TermCity.Core.Effects;
using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

/// <summary>One glyph to put on screen: where its centre goes (map-control pixels), how big, how opaque and which colour.</summary>
public readonly record struct GlyphDraw(string Glyph, Rgb Color, float Alpha, float CentreX, float CentreY, float Scale);

/// <summary>
/// The Godot-independent half of effect rendering: turns <see cref="CellEffect"/>s and <see cref="EffectSprite"/>s into
/// <see cref="GlyphDraw"/>s. <c>TerminalMap</c> only has to apply the result with <c>DrawSetTransform</c> + <c>DrawString</c>.
/// Keeping the maths here lets the unit tests cover it without the engine.
/// <para>
/// Per-glyph scaling is done by scaling the font draw about the glyph's cell centre, so it is a bitmap-free vector scale of
/// the same DejaVu glyph. Limits: tiny scales (below <see cref="MinScale"/>) are not drawn at all, and large scales make
/// glyphs wider than their cell (they simply overlap neighbours), so the effects keep scales at or below about 1.3.
/// </para>
/// </summary>
public static class EffectDraw
{
    /// <summary>Glyphs scaled below this are invisible anyway and are skipped.</summary>
    public const float MinScale = 0.05f;

    /// <summary>Glyphs more transparent than this are skipped.</summary>
    public const float MinAlpha = 0.02f;

    public static bool IsVisible(float scale, float alpha) => scale >= MinScale && alpha >= MinAlpha;

    /// <summary>Blends a cell colour toward an effect tint.</summary>
    public static Rgb Tinted(Rgb colour, Rgb? tint, float amount) =>
        tint is { } t && amount > 0 ? Rgb.Blend(colour, t, Math.Clamp(amount, 0f, 1f)) : colour;

    /// <summary>The background colour of a cell after its effect.</summary>
    public static Rgb Background(Rgb background, in CellEffect effect) =>
        Tinted(background, effect.BackgroundTint, effect.BackgroundAmount);

    /// <summary>Local draw origin for a centred glyph: the font baseline relative to the cell centre.</summary>
    public static (float X, float Y) GlyphOrigin(int cellWidth, int cellHeight, float baseline) =>
        (-cellWidth / 2f, baseline - cellHeight / 2f);

    /// <summary>
    /// Plans how one cell with an effect is drawn. <paramref name="main"/> replaces the cell's normal glyph (null when the
    /// glyph is hidden); <paramref name="overlay"/> is an extra ghost drawn above the whole map (null when none).
    /// </summary>
    public static void PlanCell(in CellEffect effect, CellVisual visual, int column, int row, int cellWidth, int cellHeight,
        (float X, float Y) shake, out GlyphDraw? main, out GlyphDraw? overlay)
    {
        float centreX = (column + 0.5f + effect.OffsetX) * cellWidth + shake.X;
        float centreY = (row + 0.5f + effect.OffsetY) * cellHeight + shake.Y;
        var colour = Tinted(visual.Foreground, effect.Tint, effect.TintAmount);
        var transformed = IsVisible(effect.Scale, effect.Alpha)
            ? new GlyphDraw(effect.Glyph ?? visual.Glyph, colour, effect.Alpha, centreX, centreY, effect.Scale)
            : (GlyphDraw?)null;
        if (effect.Overlay)
        {
            // The cell keeps its own glyph in place; the ghost floats above everything.
            main = new GlyphDraw(visual.Glyph, visual.Foreground, 1f,
                (column + 0.5f) * cellWidth + shake.X, (row + 0.5f) * cellHeight + shake.Y, 1f);
            overlay = transformed;
        }
        else
        {
            main = transformed;
            overlay = null;
        }
    }

    /// <summary>Plans a free-floating sprite; <paramref name="originX"/>/<paramref name="originY"/> is the map cell shown at the top-left.</summary>
    public static GlyphDraw? PlanSprite(in EffectSprite sprite, int originX, int originY, int cellWidth, int cellHeight,
        (float X, float Y) shake, int columns, int rows)
    {
        if (!IsVisible(sprite.Scale, sprite.Alpha))
        {
            return null;
        }

        float column = sprite.X - originX, row = sprite.Y - originY;
        if (column < -1 || row < -1 || column > columns + 1 || row > rows + 1)
        {
            return null;
        }

        return new GlyphDraw(sprite.Glyph, sprite.Color, sprite.Alpha, column * cellWidth + shake.X, row * cellHeight + shake.Y, sprite.Scale);
    }

    public static (float X, float Y) ShakePixels((float X, float Y) shakeCells, int cellWidth, int cellHeight) =>
        (shakeCells.X * cellWidth, shakeCells.Y * cellHeight);
}
