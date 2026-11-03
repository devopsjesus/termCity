using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.GodotApp;

internal enum DepthTreatment { Baseline, Lighting, Relief, Combined }

internal static class DepthStudyArtwork
{
    internal const DepthTreatment DefaultTreatment = DepthTreatment.Combined;
    internal const float RoofReliefScale = 1.09f;
    internal const float ReliefWallCoreInset = .11f;
    internal static bool UsesLighting(DepthTreatment treatment) =>
        treatment is DepthTreatment.Lighting or DepthTreatment.Combined;
    internal static bool UsesRelief(DepthTreatment treatment) =>
        treatment is DepthTreatment.Relief or DepthTreatment.Combined;

    internal static IReadOnlyList<HouseModelPart> HouseParts(HouseAppearance appearance, HouseBlock block, bool entrance,
        bool includeRoof = true)
    {
        var parts = new List<HouseModelPart>();
        void Add(Vector3 center, Vector3 size, int color, bool roof = false) =>
            parts.Add(new(center, size, color, roof));
        Add(new(0, block.Height / 2, 0), new(block.Width - ReliefWallCoreInset * 2,
            block.Height, block.Depth - ReliefWallCoreInset * 2),
            appearance.PlasterColor);
        foreach (var part in appearance.Parts(block, includeRoof))
        {
            if (!part.Roof && part.Color == appearance.PlasterColor) continue;
            if (part.Roof)
                Add(part.Center * new Vector3(RoofReliefScale, 1, RoofReliefScale),
                    part.Size * new Vector3(RoofReliefScale, 1, RoofReliefScale), part.Color, true);
            else Add(part.Center, part.Size, part.Color);
        }
        void Face(bool alongX, int side)
        {
            float span = alongX ? block.Width : block.Depth;
            var bays = BuildingDetailLayout.HalfTimberFacade(span);
            Vector3 Position(float along, float y, float outward) => alongX
                ? new(along, y, side * (block.Depth / 2 + outward))
                : new(side * (block.Width / 2 + outward), y, along);
            Vector3 Size(float across, float tall, float thick) => alongX
                ? new(across, tall, thick) : new(thick, tall, across);
            void Panel(float x, float y, float w, float h) =>
                Add(Position(x, y, -.04f), Size(w, h, .05f), appearance.PlasterColor);
            for (int floor = 0; floor < block.Stories; floor++)
            {
                var panel = block.WallPanel(floor);
                for (int bay = 0; bay < bays.Count; bay++)
                {
                    float x = bays.Center(bay), width = bays.Spacing - BuildingDetailLayout.HalfTimberPostWidth;
                    bool door = entrance && alongX && side == 1 && floor == 0 && bay == bays.Count / 2;
                    bool window = !(alongX && side == -1 || !alongX && bay != bays.Count / 2);
                    if (!door && !window)
                    {
                        Panel(x, panel.Bottom + panel.Height / 2, width, panel.Height);
                        continue;
                    }
                    float openingWidth = door ? .18f : .14f;
                    float openingHeight = door ? Math.Min(.30f, panel.Height) : Math.Min(.14f, panel.Height);
                    float bottom = door ? panel.Bottom : panel.Bottom + (panel.Height - openingHeight) / 2;
                    float top = bottom + openingHeight, middle = (bottom + top) / 2;
                    float shoulder = (width - openingWidth) / 2;
                    foreach (int edge in new[] { -1, 1 })
                        Panel(x + edge * (openingWidth + shoulder) / 2,
                            panel.Bottom + panel.Height / 2, shoulder, panel.Height);
                    if (bottom > panel.Bottom)
                        Panel(x, (panel.Bottom + bottom) / 2, openingWidth, bottom - panel.Bottom);
                    if (top < panel.Bottom + panel.Height)
                        Panel(x, (top + panel.Bottom + panel.Height) / 2, openingWidth, panel.Bottom + panel.Height - top);
                    // The pane is behind a real opening; four reveals bridge it to the outer wall.
                    Add(Position(x, middle, -.0875f), Size(openingWidth, openingHeight, .015f),
                        door ? 0x514637 : 0x35494b);
                    foreach (int edge in new[] { -1, 1 })
                        Add(Position(x + edge * (openingWidth / 2 - .01f), middle, -.0475f),
                            Size(.02f, openingHeight, .065f), HouseAppearance.FrameColor);
                    foreach (float y in new[] { bottom + .01f, top - .01f })
                        Add(Position(x, y, -.0475f), Size(openingWidth - .04f, .02f, .065f),
                            HouseAppearance.FrameColor);
                    Add(Position(x, bottom - .012f, .045f), Size(openingWidth + .05f, .024f, .12f),
                        HouseAppearance.SillColor);
                }
            }
            Add(Position(0, block.Height - .025f, .035f), Size(span, .05f, .07f),
                HouseAppearance.FrameColor);
        }
        Face(true, -1); Face(true, 1); Face(false, -1); Face(false, 1);
        return parts;
    }

    internal static IEnumerable<WallFragment> WallFragments(WallComponent wall, bool relief, GameMap? map = null)
    {
        foreach (var part in map is null ? WallDamageLayout.Fragments(wall) : WallDamageLayout.Fragments(wall, map))
            yield return relief && part.Kind == WallFragmentKind.Trim
                ? part with { Size = part.Size + new Vector3(.16f, 0, .16f) } : part;
        if (!relief || wall.IsGate || wall.IsGateWing || wall.Health != wall.MaxHealth ||
            wall.Material == WallMaterial.Wood) yield break;
        float width = WallDamageLayout.Width(wall), depth = WallDamageLayout.Depth(wall);
        if (!wall.IsTower && width < 4) yield break;
        foreach (int side in wall.IsTower ? new[] { -1, 1 } : new[] { (int)WallDamageLayout.ExteriorSide(wall) })
            foreach (int end in new[] { -1, 1 })
            {
                float x = end * (width / 2 - .45f);
                float height = WallDamageLayout.NominalHeight(wall) * .58f;
                yield return new(new(x, .18f, side * (depth / 2 + .23f)),
                    new(.64f, .36f, .46f), WallFragmentKind.Body);
                yield return new(new(x, .36f + height / 2, side * (depth / 2 + .14f)),
                    new(.42f, height, .28f), WallFragmentKind.Body);
            }
    }
}
