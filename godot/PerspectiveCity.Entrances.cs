using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly List<(Pos Owner, Vector3 Threshold)> _sceneEntrances = [];
    private List<(Pos Owner, Vector3 Threshold)> _entranceThresholds => _objectCapture?.Entrances ?? _sceneEntrances;
    internal IReadOnlyList<(Pos Owner, Vector3 Threshold)> RenderedEntranceThresholds =>
        _sceneEntrances.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.Entrances)).ToArray();

    private void DrawEntranceApproaches(GameMap map, CellRect area, BuildingType type, Pos anchor, int? rotation)
    {
        foreach (var entrance in BuildingEntranceLayout.ForBuilding(map, area, type, rotation))
        {
            if (!_drawingPreview) _entranceThresholds.Add((anchor,
                new(entrance.Threshold.X, entrance.Threshold.Y, entrance.Threshold.Z)));
            var points = entrance.Approach.Distinct().ToList();
            if (points.Count < 2) continue;
            var threshold = new NVector2(entrance.Threshold.X, entrance.Threshold.Z);
            float ground = RenderedGroundHeight(map, threshold.X, threshold.Y);
            float rise = Math.Max(0, entrance.Threshold.Y - ground);
            int steps = rise > .05f ? Math.Max(1, (int)Math.Ceiling(rise / WorldScale.Feet(.7f))) : 0;
            float lastLength = NVector2.Distance(points[^2], threshold);
            float run = steps == 0 ? 0 : Math.Min(lastLength * .7f, steps * .18f);
            var foot = threshold + entrance.Direction * run;
            points[^1] = foot;
            var source = new List<FootpathReliefTriangle>();
            var segments = new List<(NVector2 A, NVector2 B, float WA, float WB)>();
            int color = _drawingPreview ? PreviewColor(FootpathArtwork.FreshColor) : FootpathArtwork.FreshColor;
            float width = Math.Min(FootpathLayout.WidthForWear(12), Math.Max(.14f, entrance.Width));
            for (int i = 1; i < points.Count; i++)
            {
                float length = NVector2.Distance(points[i - 1], points[i]);
                if (length < .00001f) continue;
                var direction = (points[i] - points[i - 1]) / length;
                var across = new NVector2(-direction.Y, direction.X);
                bool garden = points.Count > 2 && i == 1;
                float startWidth = garden ? Math.Min(width, HouseYardLayout.WalkwayWidth) : width;
                float endWidth = i == points.Count - 1 && entrance.Kind is BuildingAccessKind.Door or BuildingAccessKind.Gate
                    ? Math.Max(.14f, entrance.Width) : startWidth;
                segments.Add((points[i - 1], points[i], startWidth, endWidth));
                int count = Math.Max(1, (int)Math.Ceiling(length / .25f));
                NVector3 Ground(NVector2 point) => new(point.X, RenderedGroundHeight(map, point.X, point.Y), point.Y);
                for (int step = 0; step < count; step++)
                {
                    float a = step / (float)count, b = (step + 1) / (float)count;
                    var first = NVector2.Lerp(points[i - 1], points[i], a);
                    var last = NVector2.Lerp(points[i - 1], points[i], b);
                    float firstWidth = (startWidth + (endWidth - startWidth) * a) / 2;
                    float lastWidth = (startWidth + (endWidth - startWidth) * b) / 2;
                    var p = Ground(first - across * firstWidth); var q = Ground(first + across * firstWidth);
                    var r = Ground(last + across * lastWidth); var s = Ground(last - across * lastWidth);
                    source.Add(new(p, q, r, color)); source.Add(new(p, r, s, color));
                }
            }
            float Distance(NVector2 point)
            {
                float inside = 0;
                for (int index = 0; index < segments.Count; index++)
                {
                    var segment = segments[index];
                    var delta = segment.B - segment.A;
                    float t = Math.Clamp(NVector2.Dot(point - segment.A, delta) / delta.LengthSquared(), 0, 1);
                    float half = (segment.WA + (segment.WB - segment.WA) * t) / 2;
                    float lateral = half - NVector2.Distance(point, segment.A + delta * t);
                    float end = Math.Min(index == 0 ? NVector2.Distance(segment.A, segment.B) * t : float.PositiveInfinity,
                        index == segments.Count - 1 ? NVector2.Distance(segment.A, segment.B) * (1 - t) : float.PositiveInfinity);
                    inside = Math.Max(inside, Math.Min(lateral, end));
                }
                return Math.Max(0, inside);
            }
            var faces = FootpathReliefMesh.Build(source, Distance);
            foreach (var face in faces)
                if (EmitDirtTriangle(face) && !_drawingPreview) DoorApproachTrianglesDrawn++;
            if (!_drawingPreview && faces.Count > 0)
            {
                var vertices = faces.SelectMany(face => new[] { face.A, face.B, face.C }).ToArray();
                var minimum = vertices.Aggregate(NVector3.Min);
                var maximum = vertices.Aggregate(NVector3.Max);
                var bounds = new Aabb(new(minimum.X, minimum.Y - .001f, minimum.Z),
                    new(maximum.X - minimum.X, Math.Max(.002f, maximum.Y - minimum.Y), maximum.Z - minimum.Z));
                _boxes.Add((bounds, anchor, color));
                _pickBoxes.Add((bounds, anchor, color));
                _structureHighlightBoxes.Add((new(Basis.Identity.Scaled(bounds.Size), bounds.GetCenter()), anchor));
            }
            if (steps == 0 || run <= .001f) continue;
            float bottom = RenderedGroundHeight(map, foot.X, foot.Y);
            float stepWidth = Math.Max(.14f, entrance.Width);
            for (int step = 0; step < steps; step++)
            {
                var at = NVector2.Lerp(foot, threshold, (step + .5f) / steps);
                float top = bottom + Math.Max(0, entrance.Threshold.Y - bottom) * (step + 1) / steps;
                float baseLevel = Math.Min(bottom, RenderedGroundHeight(map, at.X, at.Y));
                float tread = run / steps + .002f;
                Box(new(at.X, (top + baseLevel) / 2, at.Y),
                    entrance.Direction.X == 0 ? new(stepWidth, Math.Max(.002f, top - baseLevel), tread) :
                        new(tread, Math.Max(.002f, top - baseLevel), stepWidth),
                    HouseAppearance.StoneColor, anchor);
            }
        }
    }
}
