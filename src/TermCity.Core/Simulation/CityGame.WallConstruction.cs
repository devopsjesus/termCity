using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed record WallRunPlan(Vector2 Start, Vector2 End, IReadOnlyList<(Vector2 A, Vector2 B)> Spans,
    double Length, int Cost, string? Error, bool Gate = false)
{
    public IReadOnlyList<Vector2> Towers { get; init; } = [];
    public bool Valid => Error is null && Length > WallNetwork.Tolerance;
}

public readonly record struct WallRunTimings(double Validation, double Mutation, double Notification, double Territory);

public sealed partial class CityGame
{
    public WallRunTimings LastWallRunTimings { get; private set; }
    public Vector2 SnapWallEndpoint(Vector2 start, Vector2 pointer)
    {
        var target = SnapWallPoint(pointer);
        if (Settlement?.Walls.Any(w => w.IsTower &&
            Vector2.DistanceSquared(w.Center, target) < WallNetwork.Tolerance * WallNetwork.Tolerance &&
            Vector2.DistanceSquared(w.Center, start) > WallNetwork.Tolerance * WallNetwork.Tolerance) == true)
            return target;
        var delta = pointer - start;
        if (delta.LengthSquared() < WallNetwork.Tolerance * WallNetwork.Tolerance) return start;
        float angle = MathF.Atan2(delta.Y, delta.X);
        float best = float.PositiveInfinity;
        Vector2 direction = Vector2.UnitX;
        for (int quadrant = 0; quadrant < 4; quadrant++)
            foreach (int degrees in new[] { 0, 30, 45, 60 })
            {
                float candidate = (quadrant * 90 + degrees) * MathF.PI / 180;
                float difference = Math.Abs(MathF.IEEERemainder(candidate - angle, MathF.Tau));
                if (difference >= best) continue;
                best = difference;
                direction = new(MathF.Cos(candidate), MathF.Sin(candidate));
            }
        return start + direction * Math.Max(0, Vector2.Dot(delta, direction));
    }

    public Vector2 SnapWallPoint(Vector2 point)
    {
        if (Settlement is not { } state) return point;
        var tower = state.Walls.Where(w => w.IsTower && Vector2.Distance(w.Center, point) <= WorldScale.TowerWidth / 2 + .8f)
            .MinBy(w => Vector2.DistanceSquared(w.Center, point));
        if (tower is not null) return tower.Center;
        Vector2 closest = point;
        float distance = .7f;
        foreach (var span in state.NetworkSpans())
        {
            var delta = span.B - span.A;
            var candidate = span.A + delta * Math.Clamp(Vector2.Dot(point - span.A, delta) / delta.LengthSquared(), 0, 1);
            float reach = Vector2.Distance(candidate, point);
            if (reach >= distance) continue;
            closest = candidate;
            distance = reach;
        }
        return closest;
    }

    public IReadOnlyList<IReadOnlyList<Vector2>> PreviewWallEnclosures(WallRunPlan plan)
    {
        if (!plan.Valid || Settlement is not { } state) return [];
        var existing = state.NetworkSpans().ToArray();
        int nextId = existing.Select(s => s.Id).DefaultIfEmpty().Max() + 1;
        var additions = plan.Spans.Select((span, i) => new WallNetworkSpan(nextId + i, span.A, span.B, plan.Gate));
        var roots = state.Walls.Where(w => w.BelongsToFoundingWall).Select(w => w.Id).ToHashSet();
        var rootSpans = existing.Where(s => roots.Contains(s.Id / 4)).Select(s => s.Id).ToHashSet();
        var proposed = new WallNetwork(existing.Concat(additions),
            new(state.Bounds.X + state.Bounds.Width / 2f, state.Bounds.Y + state.Bounds.Height / 2f), rootSpans);
        return proposed.Regions.Where(region => region.ConnectedToRoot && region.Polygon.Select((a, i) =>
        {
            var b = region.Polygon[(i + 1) % region.Polygon.Count];
            var delta = b - a;
            return (a + b) / 2 + Vector2.Normalize(new Vector2(-delta.Y, delta.X)) * .01f;
        }).Any(sample => !state.WallNetwork.Contains(sample))).Select(region => region.Polygon).ToArray();
    }

    public WallRunPlan PlanWallRun(Vector2 start, Vector2 end, bool gate = false)
    {
        var empty = Array.Empty<(Vector2 A, Vector2 B)>();
        WallRunPlan Fail(string message) => new(start, end, empty, 0, 0, message, gate);
        if (Settlement is not { } state) return Fail("Found a bailey before extending its walls.");
        if (gate && state.UpgradeTargetMaterial is not null)
            return Fail("Finish the purchased material upgrade before cutting a new gate into the walls.");
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(end.X) || !float.IsFinite(end.Y))
            return Fail("Choose finite wall endpoints.");
        float length = Vector2.Distance(start, end);
        if (length < .05f) return Fail("Choose two distinct wall endpoints.");
        if (new[] { start, end }.Any(p => p.X < 2 || p.Y < 2 || p.X > Map.Width - 2 || p.Y > Map.Height - 2))
            return Fail("Keep walls and their future upgrades inside the map clearance.");
        var material = state.UpgradeTargetMaterial ?? state.Material;
        var candidate = new WallComponent { Start = start, End = end, IsGate = gate, BuiltMaterial = material };
        int left = Math.Max(0, (int)MathF.Floor(Math.Min(start.X, end.X) - 3));
        int top = Math.Max(0, (int)MathF.Floor(Math.Min(start.Y, end.Y) - 3));
        int right = Math.Min(Map.Width - 1, (int)MathF.Ceiling(Math.Max(start.X, end.X) + 3));
        int bottom = Math.Min(Map.Height - 1, (int)MathF.Ceiling(Math.Max(start.Y, end.Y) + 3));
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                if (!WallDamageLayout.ContainsFootprint(candidate, x + .5, y + .5, .7)) continue;
                if (Map.Footbridges.Any(bridge => bridge.Cells().Contains(new Pos(x, y))))
                    return Fail("A civilian footbridge crosses this wall corridor. Keep its passage clear.");
                if (!Map.TerrainAt(x, y).Buildable && (gate ||
                    Map.TerrainAt(x, y).Name != Terrain.DefaultTerrains.WaterName))
                    return Fail("Gates need dry land; ordinary walls can cross water with an iron grate.");
                if (Map.BuildingAt(x, y) is not null) return Fail("A building occupies the wall corridor.");
                if (!gate && (Map.HasRoad(x, y) || Network.RoadOverlapsCell(Map.Index(x, y))))
                    return Fail("A road crosses this run. Place an explicit gate first, then join its ends.");
            }
        foreach (var area in Map.BuildingFootprints.Values)
            if (area.Cells().Any(p => WallDamageLayout.ContainsFootprint(candidate, p.X + .5, p.Y + .5, 2)))
                return Fail("Keep the wall's future upgrade and repair corridor clear of buildings.");
        if (state.Enemies.Any(enemy => enemy.Health > 0 &&
            WallDamageLayout.ContainsFootprint(candidate, enemy.X, enemy.Y, enemy.CollisionRadius)) ||
            state.Defenders.Any(unit => unit.Health > 0 &&
                WallDamageLayout.ContainsFootprint(candidate, unit.X, unit.Y, WorldScale.HumanRadius)))
            return Fail("A moving unit occupies this wall footprint. Wait for it to clear before building.");
        if (gate && state.Walls.Any(w => (w.IsTower || w.IsGate || w.Defense != WallDefenseKind.None) &&
            World.WallNetwork.PointSegmentDistance(w.Center, start, end) < WallDamageLayout.Width(w) / 2 + .75))
            return Fail("The gate needs room away from towers, mounted weapons and other gates.");
        var uncovered = gate ? new[] { (Start: 0f, End: 1f) } :
            World.WallNetwork.UncoveredIntervals(start, end, state.NetworkSpans()).ToArray();
        var spans = uncovered.Select(interval => (A: Vector2.Lerp(start, end, interval.Start),
            B: Vector2.Lerp(start, end, interval.End))).ToArray();
        double newLength = spans.Sum(span => (double)Vector2.Distance(span.A, span.B));
        if (newLength <= WallNetwork.Tolerance) return Fail("This wall run is already built. Repair damaged sections instead.");
        int rate = material switch { WallMaterial.Wood => 20, WallMaterial.Stone => 80, _ => 200 };
        int cost = checked((int)Math.Ceiling(newLength * rate - .00001));
        var towerSites = gate ? [] : JunctionTowerSites(state, state.NetworkSpans().Concat(
            spans.Select((span, i) => new WallNetworkSpan((state.NextWallId + i) * 4, span.A, span.B))).ToArray(), spans);
        foreach (var point in towerSites)
        {
            var cell = new Pos((int)point.X, (int)point.Y);
            if (new CellRect(cell.X - 2, cell.Y - 2, 5, 5).Cells().Any(p =>
                !Map.InBounds(p) || !Map.TerrainAt(p.X, p.Y).Buildable &&
                    Map.TerrainAt(p.X, p.Y).Name != Terrain.DefaultTerrains.WaterName || Map.BuildingAt(p.X, p.Y) is not null ||
                Map.HasRoad(p.X, p.Y) || Network.RoadOverlapsCell(Map.Index(p.X, p.Y))))
                return Fail("The corner or junction tower needs a clear five-cell footprint. Move the junction.");
        }
        return new WallRunPlan(start, end, spans, newLength, cost, Money < cost ? $"This run costs {cost:N0} gold." : null, gate)
            { Towers = towerSites };
    }

    public WallRunPlan PlanGate(Vector2 center, float angle)
    {
        var tangent = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        float width = WorldScale.GateWidth;
        var road = Network.RoadAtCellCenter((int)center.X, (int)center.Y);
        if (road is not null && road.Count >= 2)
        {
            int nearest = Enumerable.Range(0, road.Count - 1).MinBy(i =>
                WallNetwork.PointSegmentDistance(center,
                    new((float)road.Xs[i] / RoadCurves.CellWidth, (float)road.Ys[i] / RoadCurves.CellHeight),
                    new((float)road.Xs[i + 1] / RoadCurves.CellWidth, (float)road.Ys[i + 1] / RoadCurves.CellHeight)));
            var roadDirection = Vector2.Normalize(new Vector2(
                (float)(road.Xs[nearest + 1] - road.Xs[nearest]) / RoadCurves.CellWidth,
                (float)(road.Ys[nearest + 1] - road.Ys[nearest]) / RoadCurves.CellHeight));
            float crossing = Math.Abs(tangent.X * roadDirection.Y - tangent.Y * roadDirection.X);
            if (crossing < .2f)
                return new(center, center, [], 0, 0, "Turn the gate across the road, not along it.", true);
            width = Math.Max(width, (road.Type.WidthCells + .4f) / crossing + .8f);
        }
        return PlanWallRun(center - tangent * width / 2, center + tangent * width / 2, gate: true);
    }

    public float GateAngleAt(Vector2 center)
    {
        var spans = Settlement?.NetworkSpans().ToArray() ?? [];
        if (spans.Length > 0)
        {
            var span = spans.MinBy(s => WallNetwork.PointSegmentDistance(center, s.A, s.B));
            if (WallNetwork.PointSegmentDistance(center, span.A, span.B) <= .75f)
                return MathF.Atan2(span.B.Y - span.A.Y, span.B.X - span.A.X);
        }
        var road = Network.RoadAtCellCenter((int)center.X, (int)center.Y);
        if (road is null || road.Count < 2) return 0;
        int segment = Enumerable.Range(0, road.Count - 1).MinBy(i =>
            WallNetwork.PointSegmentDistance(center,
                new((float)road.Xs[i] / RoadCurves.CellWidth, (float)road.Ys[i] / RoadCurves.CellHeight),
                new((float)road.Xs[i + 1] / RoadCurves.CellWidth, (float)road.Ys[i + 1] / RoadCurves.CellHeight)));
        return MathF.Atan2((float)(road.Ys[segment + 1] - road.Ys[segment]) / RoadCurves.CellHeight,
            (float)(road.Xs[segment + 1] - road.Xs[segment]) / RoadCurves.CellWidth) + MathF.PI / 2;
    }

    public ActionResult BuildWallRun(Vector2 start, Vector2 end, bool gate = false)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var plan = PlanWallRun(start, end, gate);
        double validation = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LastWallRunTimings = new(validation, 0, 0, 0);
        if (!plan.Valid) return ActionResult.Fail(plan.Error ?? "There is no new wall to build.");
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = Settlement!;
        var material = state.UpgradeTargetMaterial ?? state.Material;
        bool featuresChanged = false;
        void ClearFeature(int x, int y)
        {
            if (Map.FeatureAt(x, y) is null) return;
            Map.SetFeature(x, y, null);
            featuresChanged = true;
        }
        state.ChangeWalls(() =>
        {
            if (gate) OpenGateSpan(state, start, end);
            foreach (var span in plan.Spans)
            {
                int count = gate ? 1 : Math.Max(1, (int)Math.Ceiling(Vector2.Distance(span.A, span.B)));
                for (int i = 0; i < count; i++)
                    AddSpan(state, Vector2.Lerp(span.A, span.B, i / (float)count),
                        Vector2.Lerp(span.A, span.B, (i + 1) / (float)count), material, gate);
            }
            foreach (var point in plan.Towers)
            {
                var cell = new Pos((int)point.X, (int)point.Y);
                state.AddWall(new WallComponent { Id = state.NextWallId, Cell = cell, SectionBounds = CellRect.Single(cell),
                    Start = point - Vector2.UnitX * .05f, End = point + Vector2.UnitX * .05f,
                    IsTower = true, JunctionTower = true, BuiltMaterial = material,
                    MaxHealth = SettlementState.MaterialHealth(material), Health = SettlementState.MaterialHealth(material) });
            }
        });
        foreach (var span in plan.Spans)
        {
            var footprint = WallDamageLayout.Preview(span.A, span.B, material, gate);
            var half = WallDamageLayout.HalfSize(footprint);
            for (int y = Math.Max(0, (int)MathF.Floor(footprint.Center.Y - half.Y - .7f));
                y <= Math.Min(Map.Height - 1, (int)MathF.Ceiling(footprint.Center.Y + half.Y + .7f)); y++)
                for (int x = Math.Max(0, (int)MathF.Floor(footprint.Center.X - half.X - .7f));
                    x <= Math.Min(Map.Width - 1, (int)MathF.Ceiling(footprint.Center.X + half.X + .7f)); x++)
                    if (WallDamageLayout.ContainsFootprint(footprint, x + .5, y + .5, .7))
                        ClearFeature(x, y);
        }
        foreach (var point in plan.Towers)
        {
            var cell = new Pos((int)point.X, (int)point.Y);
            foreach (var p in new CellRect(cell.X - 2, cell.Y - 2, 5, 5).Cells()) ClearFeature(p.X, p.Y);
        }
        Money -= plan.Cost;
        Map.InvalidateTravel();
        double mutation = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        Notify(roadsChanged: false, mapChanged: featuresChanged, servicesChanged: true);
        double notification = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        state.RefreshTerritory(this);
        double territory = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        SiegeChanged?.Invoke();
        LastWallRunTimings = new(validation, mutation, notification, territory);
        return ActionResult.Ok($"Built {(gate ? "gate" : "wall run")} for {plan.Cost:N0} gold.", plan.Cost);
    }

    private IReadOnlyList<Vector2> JunctionTowerSites(SettlementState state, WallNetworkSpan[] spans,
        IReadOnlyList<(Vector2 A, Vector2 B)> additions)
    {
        var result = new List<Vector2>();
        var nodes = new List<Vector2>();
        foreach (var span in additions)
        {
            nodes.Add(span.A); nodes.Add(span.B);
        }
        foreach (var added in additions)
            foreach (var existing in spans)
            {
                var a = added; var b = existing; var u = a.B - a.A; var v = b.B - b.A;
                float cross = u.X * v.Y - u.Y * v.X;
                if (Math.Abs(cross) < .00001f) continue;
                var delta = b.A - a.A;
                float t = (delta.X * v.Y - delta.Y * v.X) / cross;
                float s = (delta.X * u.Y - delta.Y * u.X) / cross;
                if (t >= 0 && t <= 1 && s >= 0 && s <= 1) nodes.Add(a.A + u * t);
            }
        foreach (var point in nodes.Distinct())
        {
            if (!WorldScale.HasDryTowerFootprint(Map, point)) continue;
            if (state.Walls.Any(w => w.IsTower && Vector2.Distance(w.Center, point) < WorldScale.TowerWidth + .1f ||
                w.IsGate && Vector2.Distance(w.Center, point) < (WorldScale.GateWidth + WorldScale.TowerWidth) / 2)) continue;
            if (result.Any(other => Vector2.Distance(other, point) < WorldScale.TowerWidth + .1f)) continue;
            var directions = spans.Where(s => WallNetwork.PointSegmentDistance(point, s.A, s.B) < .001f)
                .SelectMany(s => new[] { s.A - point, s.B - point }).Where(d => d.LengthSquared() > .0001f)
                .Select(Vector2.Normalize).ToArray();
            if (directions.Length < 2 || directions.All(d => Math.Abs(Vector2.Dot(d, directions[0])) > .999f)) continue;
            result.Add(point);
        }
        return result;
    }

    private static void AddSpan(SettlementState state, Vector2 a, Vector2 b, WallMaterial material,
        bool gate = false, double fraction = 1, bool original = false)
    {
        double maximum = SettlementState.MaterialHealth(material) * Vector2.Distance(a, b);
        var center = (a + b) / 2;
        var cell = new Pos((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y));
        state.AddWall(new WallComponent { Id = state.NextWallId,
            Cell = cell, SectionBounds = CellRect.Single(cell), Start = a, End = b,
            BuiltMaterial = material, IsGate = gate, BelongsToFoundingWall = original,
            MaxHealth = maximum, Health = maximum * fraction });
    }

    private static void OpenGateSpan(SettlementState state, Vector2 a, Vector2 b)
    {
        foreach (var wall in state.Walls.Where(w => !w.IsGate && !w.IsGateWing).ToArray())
        {
            var start = wall.Start ?? wall.Center - wall.Tangent * .5f;
            var end = wall.End ?? wall.Center + wall.Tangent * .5f;
            var remaining = WallNetwork.UncoveredIntervals(start, end, [new WallNetworkSpan(1, a, b)]);
            if (remaining.Count == 1 && remaining[0] == (0f, 1f)) continue;
            double fraction = wall.Health / wall.MaxHealth;
            var material = state.MaterialFor(wall);
            state.DetachHealthSection(wall);
            state.RemoveWall(wall);
            foreach (var interval in remaining)
                AddSpan(state, Vector2.Lerp(start, end, interval.Start), Vector2.Lerp(start, end, interval.End),
                    material, fraction: fraction, original: wall.BelongsToFoundingWall);
        }
    }

    public ActionResult RemoveWallSection(Pos cell, int? wallId = null)
    {
        var state = Settlement;
        var wall = wallId is { } id ? state?.Walls.FirstOrDefault(w => w.Id == id && w.Cell == cell) : state?.WallAt(cell);
        if (state is null || wall is null)
            return ActionResult.Fail("Select an existing wall section to remove.");
        if (state.UpgradeTargetMaterial is not null)
            return ActionResult.Fail("Finish the purchased material upgrade before removing its wall sections.");
        var members = state.Walls.Where(w => w.SectionId == wall.SectionId).ToArray();
        foreach (var member in members) state.RemoveWall(member);
        state.ClearRepairCrew();
        Map.InvalidateTravel();
        Notify(roadsChanged: true);
        state.RefreshTerritory(this);
        SiegeChanged?.Invoke();
        return ActionResult.Ok("Removed the selected wall section; existing residents remain.");
    }

    public static IEnumerable<Pos> WallCells(Vector2 a, Vector2 b)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(a, b) * 4));
        var seen = new HashSet<Pos>();
        for (int i = 0; i <= steps; i++)
        {
            var point = Vector2.Lerp(a, b, i / (float)steps);
            var p = new Pos((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));
            if (seen.Add(p)) yield return p;
        }
    }
}
