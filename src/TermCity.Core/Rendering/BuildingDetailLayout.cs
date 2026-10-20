namespace TermCity.Core.Rendering;

public readonly record struct FacadeBays(float Span, int Count)
{
    public float Spacing => Span / Count;
    public float Center(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        return -Span / 2 + (index + 0.5f) * Spacing;
    }
    public float Post(int index)
    {
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        return -Span / 2 + index * Spacing;
    }
}

public static class BuildingDetailLayout
{
    public const float WindowWidth = 0.18f;
    public const float BeamWidth = 0.045f;
    public const float TowerWidth = 0.6f;
    public const float TowerCapWidth = 0.66f;
    public const float MerlonWidth = 0.16f;
    public const float CottageBeamWidth = 0.032f;
    public const float CottageDoorWidth = 0.15f;
    public const float CottageWindowWidth = 0.10f;
    public const float HalfTimberPostWidth = 0.10f;
    public const float HalfTimberBeamHeight = 0.08f;

    public static FacadeBays HalfTimberFacade(float width)
    {
        if (!float.IsFinite(width) || width < 0.4f) throw new ArgumentOutOfRangeException(nameof(width));
        float span = width - HalfTimberPostWidth;
        int count = Math.Max(1, (int)Math.Floor(span / 0.30f));
        if (count % 2 == 0) count--;
        return new(span, count);
    }

    public static FacadeBays CottageFacade(float width)
    {
        if (!float.IsFinite(width) || width < 0.4f) throw new ArgumentOutOfRangeException(nameof(width));
        float span = width - 0.08f;
        int count = width < 0.76f ? 1 : Math.Clamp((int)Math.Floor(span / 0.32f), 3, 3);
        if (count % 2 == 0) count--;
        return new(span, count);
    }

    public static FacadeBays Facade(float width)
    {
        if (!float.IsFinite(width) || width < 0.4f) throw new ArgumentOutOfRangeException(nameof(width));
        float span = width - 0.16f;
        int count = Math.Max(1, (int)Math.Floor(span / 0.44f));
        // Odd bay counts leave a central doorway and mirror the glazing on either side.
        if (count % 2 == 0) count--;
        return new(span, count);
    }

    public static float[] Centers(float span, float detailWidth, float pitch)
    {
        if (!float.IsFinite(span) || span < 0) throw new ArgumentOutOfRangeException(nameof(span));
        if (!float.IsFinite(detailWidth) || detailWidth <= 0) throw new ArgumentOutOfRangeException(nameof(detailWidth));
        if (!float.IsFinite(pitch) || pitch < detailWidth) throw new ArgumentOutOfRangeException(nameof(pitch));
        if (span < detailWidth) return [];
        int count = 1 + (int)Math.Floor((span - detailWidth) / pitch + 0.00001f);
        var centers = new float[count];
        for (int i = 0; i < count; i++) centers[i] = (i - (count - 1) / 2f) * pitch;
        return centers;
    }
}
