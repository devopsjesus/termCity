using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class TerminalMap
{
    internal int WallHealthBarsDrawn { get; private set; }
    internal Dictionary<int, Vector2> RenderedProjectilePoints { get; } = [];

    private Vector2 SiegeScreenPoint(double x, double y, float height = 0)
    {
        if (PerspectiveEnabled && Perspective is { } view)
        {
            int cx = Math.Clamp((int)x, 0, Session.Game.Map.Width - 1);
            int cy = Math.Clamp((int)y, 0, Session.Game.Map.Height - 1);
            return view.ScreenPoint(new((float)x, TerrainSprite.Elevation(Session.Game.Map, cx, cy) + height, (float)y));
        }
        return new((float)(x - Session.CameraX) * Grid.PixelWidth / Session.Stride,
            (float)(y - Session.CameraY) * Grid.PixelHeight / Session.Stride);
    }

    private void DrawSiegeOverlay()
    {
        WallHealthBarsDrawn = 0;
        RenderedProjectilePoints.Clear();
        var settlement = Session.Game.Settlement;
        float sx = Grid.PixelWidth / (float)Session.Stride, sy = Grid.PixelHeight / (float)Session.Stride;
        if (settlement is not null && Session.SelectedDefenderIds.Count == 1 &&
            settlement.Defenders.FirstOrDefault(d => Session.SelectedDefenderIds.Contains(d.Id)) is { } selectedFighter)
        {
            var profile = CombatProfiles.Fighter(selectedFighter.Kind);
            var target = settlement.Enemies.FirstOrDefault(e => e.Id == selectedFighter.TargetEnemyId && e.Health > 0);
            double radius = selectedFighter.Kind == DefenderKind.Soldier && target is not null
                ? SettlementState.MeleeDistance(target) : profile.Range;
            for (int step = 0; step < 48; step++)
            {
                double a = step * Math.Tau / 48, b = (step + 1) * Math.Tau / 48;
                double ax = selectedFighter.X + Math.Cos(a) * radius, ay = selectedFighter.Y + Math.Sin(a) * radius;
                double bx = selectedFighter.X + Math.Cos(b) * radius, by = selectedFighter.Y + Math.Sin(b) * radius;
                if (Session.Game.Map.InBounds((int)Math.Floor(ax), (int)Math.Floor(ay)) &&
                    Session.Game.Map.InBounds((int)Math.Floor(bx), (int)Math.Floor(by)))
                    DrawLine(SiegeScreenPoint(ax, ay), SiegeScreenPoint(bx, by), new Color(.56f, .78f, .83f, .4f), 1);
            }
            DrawString(CellFont, new Vector2(12, Size.Y - 12),
                selectedFighter.Kind == DefenderKind.Archer ? "MAXIMUM REACH - supported post and clear shot required" :
                    "MELEE CONTACT - reach, height and cover rechecked at strike",
                fontSize: FontSize, modulate: new Color("#8fc7d4"));
        }
        if (settlement is not null)
        {
            foreach (var wall in settlement.Walls)
            {
                if (wall.IsGateWing) continue;
                var center = SiegeScreenPoint(wall.Center.X, wall.Center.Y,
                    WallDamageLayout.TopHeight(wall) + 0.4f);
                if (!PerspectiveEnabled && !wall.IsOriginal) DrawOrientedWall(wall);
                if (!PerspectiveEnabled && wall.IsOriginal)
                {
                    bool horizontal = wall.Cell.Y == settlement.Bounds.Y || wall.Cell.Y == settlement.Bounds.Bottom;
                    var half = WallDamageLayout.HalfSize(wall);
                    var size = new Vector2(sx * half.X * 2, sy * half.Y * 2);
                    var color = settlement.MaterialFor(wall) == WallMaterial.Wood ? new Color("#98754b") : new Color("#a4a599");
                    bool selected = !Session.FixedPlacementToolActive && Session.Selection is not null && Session.HasCursor &&
                        Session.ActiveArea == Session.BlockAt(Session.Cursor) && wall.SectionBounds.Contains(Session.Cursor);
                    if (selected) color = color.Lerp(new Color(1, 0.94f, 0.68f), 0.19f);
                    int band = WallDamageLayout.DamageBand(wall);
                    if (band == 0) DrawRect(new(center - size / 2, size), color);
                    else
                        for (int part = 0; part < 4; part++)
                        {
                            float intact = Math.Max(0.2f, 1 - band * (part % 2 == 0 ? 0.1f : 0.06f));
                            var chipSize = horizontal ? new Vector2(size.X / 4, size.Y * intact) :
                                new Vector2(size.X * intact, size.Y / 4);
                            var offset = horizontal ? new Vector2((part + 0.5f) * size.X / 4 - size.X / 2, 0) :
                                new Vector2(0, (part + 0.5f) * size.Y / 4 - size.Y / 2);
                            DrawRect(new(center + offset - chipSize / 2, chipSize),
                                WallDamageLayout.IsBurning(wall)
                                    ? selected ? new Color("#554338").Lerp(new Color(1, 0.94f, 0.68f), 0.19f) : new Color("#554338")
                                    : color);
                        }
                    if (wall.IsGate) DrawRect(new(center - new Vector2(sx * 0.16f, sy * 0.3f),
                        new Vector2(sx * 0.32f, sy * 0.6f)), Colors.Black);
                    DrawWallUpgradeOverlay(settlement, wall, center, size, horizontal);
                    if (wall.Health > 0 && wall.Defense != WallDefenseKind.None)
                        DrawRect(new(center - new Vector2(2, 3), new Vector2(4, 6)), new Color("#e4d3a0"));
                    if (WallDamageLayout.IsBurning(wall))
                    {
                        double phase = Effects?.Settings.ReducedMotion == true ? 0 :
                            (settlement.Revision + settlement.InterpolationFraction) * 0.1;
                        var surfaces = WallDamageLayout.FlameSurfaces(wall);
                        for (int i = 0; i < surfaces.Count; i++)
                        {
                            var surface = surfaces[i];
                            var root = center + (horizontal
                                ? new Vector2(surface.Position.X * sx, surface.Position.Z * sy)
                                : new Vector2(surface.Position.Z * sx, -surface.Position.X * sy));
                            foreach (var part in WallDamageLayout.FlameParts(surface, phase,
                                wall.Cell.X * 13 + wall.Cell.Y * 7 + i * 31))
                            {
                                float height = part.Size.Y * 16;
                                float width = Math.Max(1, part.Size.X * 20);
                                var flameColor = Color.FromHtml($"#{part.Color:x6}");
                                DrawRect(new(root + new Vector2((part.Center.X - surface.Position.X) * 20 - width / 2,
                                    -(part.Center.Y - surface.Position.Y) * 16 - height / 2),
                                    new Vector2(width, height)), flameColor);
                            }
                        }
                    }
                }
                if (wall.Cell == wall.SectionAnchor &&
                    (wall.Health < wall.MaxHealth || Session.HasCursor && wall.SectionBounds.Contains(Session.Cursor)))
                {
                    var bounds = wall.SectionBounds;
                    var healthCenter = SiegeScreenPoint(wall.IsOriginal ? bounds.X + bounds.Width / 2.0 : wall.Center.X,
                        wall.IsOriginal ? bounds.Y + bounds.Height / 2.0 : wall.Center.Y,
                        WallDamageLayout.TopHeight(wall) + 0.4f);
                    DrawSiegeHealth(healthCenter - new Vector2(12, 8), wall.Health / wall.MaxHealth, 24);
                    WallHealthBarsDrawn++;
                }
            }
            if (!PerspectiveEnabled && settlement.UpgradingWall is { } upgradeCell)
            {
                var at = SiegeScreenPoint(upgradeCell.X + 0.5, upgradeCell.Y + 0.5);
                float swing = Effects?.Settings.ReducedMotion == true ? 0 :
                    MathF.Sin((float)settlement.UpgradeAnimationPhase * Mathf.Tau);
                DrawLine(at, at + new Vector2(3, -3 - swing * 2), new Color("#79634b"), 1);
                DrawRect(new(at + new Vector2(2, -4 - swing * 2), new Vector2(3, 2)), new Color("#a4a599"));
                DrawRect(new(at + new Vector2(4, 1), new Vector2(3, 2)), new Color("#a4a599"));
            }
            int? selectedEnemyId = Session.SelectedEnemy?.Id;
            if (!PerspectiveEnabled)
                foreach (var death in settlement.Deaths)
                {
                    float t = Effects?.Settings.ReducedMotion == true ? .85f : (float)(death.Seconds / death.Duration);
                    float collapse = Mathf.SmoothStep(0, 1, Math.Clamp(t * 1.7f, 0, 1));
                    var p = death.Position + death.Direction * (collapse * (death.Cause == SiegeDeathCause.Crushing ? .7f : .3f));
                    var at = SiegeScreenPoint(p.X, p.Y);
                    int rgb = death.Cause == SiegeDeathCause.Oil ? 0x493b29 : death.Friendly
                        ? death.Defender == DefenderKind.Archer ? 0x59777d : 0x647875
                        : death.Enemy == SiegeEnemyKind.Goblin ? 0x66834c : 0x746147;
                    var body = new Vector2(Math.Max(3, sx * death.Scale * .5f), Math.Max(3, sy * death.Scale * (.7f - collapse * .35f)));
                    DrawRect(new(at - body / 2, body), Color.FromHtml($"#{rgb:x6}"));
                    DrawRect(new(at + new Vector2(body.X / 2 - 1, -body.Y / 2), Vector2.One * Math.Max(2, sx * .15f)),
                        death.Friendly ? new Color("#c6bf9c") : new Color("#718c4e"));
                }
            foreach (var goblin in settlement.Enemies)
            {
                var pose = SiegeMotion.Enemy(settlement, goblin);
                if (Effects?.Settings.ReducedMotion == true) pose = pose with { Stride = 0, Bob = 0, Sway = 0, Attack = 0 };
                var center = SiegeScreenPoint(pose.Position.X, pose.Position.Y,
                    (float)(goblin.BodyScale * 1.35) + 0.3f);
                if (!PerspectiveEnabled)
                {
                    if (goblin.Definition.IsTroll)
                    {
                        var color = goblin.Kind switch
                        {
                            SiegeEnemyKind.TreeTroll => new Color("#42633b"),
                            SiegeEnemyKind.StoneTroll => new Color("#85877d"),
                            _ => new Color("#76604a"),
                        };
                        float radius = (float)goblin.CollisionRadius;
                        var size = new Vector2(radius * sx * 2, radius * sy * 2);
                        DrawRect(new(center - size / 2, size), color);
                        DrawRect(new(center - new Vector2(3, 2), new Vector2(2, 2)), new Color("#d4b465"));
                        DrawRect(new(center + new Vector2(1, -2), new Vector2(2, 2)), new Color("#d4b465"));
                        if (goblin.Health < goblin.MaxHealth || goblin.Id == selectedEnemyId)
                            DrawSiegeHealth(center - new Vector2(8, size.Y / 2 + 4), goblin.Health / goblin.MaxHealth, 16);
                        continue;
                    }
                    if (goblin.Kind == SiegeEnemyKind.Orc)
                    {
                        DrawRect(new(center - new Vector2(5, 6), new Vector2(10, 11)), new Color("#586b71"));
                        DrawRect(new(center - new Vector2(3, 5), new Vector2(6, 4)), new Color("#66834c"));
                        DrawRect(new(center + new Vector2(-4, -2), new Vector2(2, 2)), new Color("#d9d49b"));
                        DrawRect(new(center + new Vector2(2, -2), new Vector2(2, 2)), new Color("#d9d49b"));
                    }
                    var forward = new Vector2(pose.Forward.X * sx, pose.Forward.Y * sy).Normalized();
                    var right = new Vector2(-forward.Y, forward.X);
                    center += right * (pose.Sway * sx);
                    DrawLine(center - right * 1.5f, center - right * 1.5f - forward * (3 + pose.Stride * 2),
                        new Color("#443d32"), 2);
                    DrawLine(center + right * 1.5f, center + right * 1.5f - forward * (3 - pose.Stride * 2),
                        new Color("#443d32"), 2);
                    DrawRect(new(center - new Vector2(3, 3), new Vector2(6, 6)), new Color("#729953"));
                    DrawColoredPolygon([center + forward * 5, center + forward * 2 - right * 2,
                        center + forward * 2 + right * 2], new Color("#a5bb76"));
                    var hand = center + right * 3 - forward * pose.Stride * 2;
                    var tip = hand + forward * (pose.Attack < 0 ? pose.Attack * 4 : 4 + pose.Attack * 4);
                    DrawLine(hand, tip, new Color("#c9c7a4"), 1.5f);
                    DrawRect(new(tip - Vector2.One, Vector2.One * 2), new Color("#a4a599"));
                }
                if (goblin.Health < goblin.MaxHealth || goblin.Id == selectedEnemyId)
                    DrawSiegeHealth(center - new Vector2(8, 4), goblin.Health / goblin.MaxHealth, 16);
            }
            if (!PerspectiveEnabled)
            {
                foreach (var unit in settlement.Defenders)
                {
                    var center = SiegeScreenPoint(
                        unit.PreviousX + (unit.X - unit.PreviousX) * settlement.InterpolationFraction,
                        unit.PreviousY + (unit.Y - unit.PreviousY) * settlement.InterpolationFraction);
                    DrawRect(new(center - new Vector2(2, 3), new Vector2(4, 6)),
                        unit.Kind == DefenderKind.Archer ? new Color("#59777d") : new Color("#647875"));
                    DrawRect(new(center - new Vector2(2, 5), new Vector2(4, 2)), new Color("#c6bf9c"));
                    DrawLine(center + new Vector2(3, 2), center + new Vector2(3, -4),
                        unit.Kind == DefenderKind.Archer ? new Color("#a69666") : new Color("#c8c8d0"), 1.5f);
                    if (Session.SelectedDefenderIds.Contains(unit.Id))
                    {
                        DrawRect(new(center - new Vector2(4, 6), new Vector2(8, 10)), new Color("#8fc7d4"), false);
                        DrawSiegeHealth(center - new Vector2(5, 9), unit.Health / unit.MaxHealth, 10);
                    }
                }
                if (settlement.FirstOrcApproach is { } side)
                    foreach (var wall in settlement.Walls.Where(w => side switch
                    {
                        BaileyGateSide.Right => w.Cell.X == settlement.Bounds.Right,
                        BaileyGateSide.Left => w.Cell.X == settlement.Bounds.X,
                        BaileyGateSide.Bottom => w.Cell.Y == settlement.Bounds.Bottom,
                        _ => w.Cell.Y == settlement.Bounds.Y,
                    }))
                    {
                        var center = SiegeScreenPoint(wall.Cell.X + 0.5, wall.Cell.Y + 0.5);
                        DrawRect(new(center - new Vector2(sx, sy) / 2, new Vector2(sx, sy)), new Color("#ffcc50"), false);
                    }
            }
            if (!PerspectiveEnabled)
                foreach (var shot in settlement.Projectiles)
                {
                    if (shot.Id > 0 && shot.Kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks)
                    {
                        if (shot.Elapsed >= shot.ContactSeconds || Effects?.Settings.ReducedMotion == true)
                        {
                            var footprint = new DropFootprint(shot.FootprintCenter, shot.FootprintForward,
                                DropAttackLayout.Size(shot.Kind), shot.StationIndex);
                            Vector2[] bounds = [SiegeScreenPoint(0, 0), SiegeScreenPoint(Session.Game.Map.Width, 0),
                                SiegeScreenPoint(Session.Game.Map.Width, Session.Game.Map.Height), SiegeScreenPoint(0, Session.Game.Map.Height)];
                            var pools = shot.Kind == WallDefenseKind.OilCauldron ?
                                TowerDefenseArtwork.OilPool(footprint.Size, Effects?.Settings.ReducedMotion == true
                                    ? Math.Max(0, shot.Elapsed - shot.ContactSeconds) : shot.Elapsed - shot.ContactSeconds,
                                    Effects?.Settings.ReducedMotion != true)
                                : new[] { new OilPoolPatch(System.Numerics.Vector2.Zero, new(footprint.Size, footprint.Size)) };
                            foreach (var patch in pools)
                            {
                                var center = footprint.Center + footprint.Right * patch.Center.X + footprint.Forward * patch.Center.Y;
                                var halfX = footprint.Right * patch.Size.X / 2;
                                var halfY = footprint.Forward * patch.Size.Y / 2;
                                var polygon = new[] { center - halfX - halfY, center + halfX - halfY,
                                    center + halfX + halfY, center - halfX + halfY }
                                    .Select(p => SiegeScreenPoint(p.X, p.Y)).ToArray();
                                foreach (var clipped in Geometry2D.IntersectPolygons(polygon, bounds))
                                    DrawColoredPolygon(clipped, shot.Kind == WallDefenseKind.OilCauldron ?
                                        new Color("#68491f") : new Color("#8b806b"));
                            }
                        }
                        continue;
                    }
                    if (shot.Melee || shot.Id > 0 &&
                        (shot.Applied || SiegeMotion.ProjectileElapsed(settlement, shot) < shot.Windup)) continue;
                    var start = SiegeScreenPoint(shot.StartX, shot.StartY);
                    var end = SiegeScreenPoint(shot.EndX, shot.EndY);
                    float progress = shot.Id > 0 ? SiegeMotion.ProjectileFlightProgress(settlement, shot) :
                        SiegeMotion.ProjectileProgress(settlement, shot);
                    if (progress >= 1) continue;
                    var at = start.Lerp(end, progress);
                    if (shot.Id > 0) RenderedProjectilePoints.Add(shot.Id, at);
                    DrawLine(at - (end - start).Normalized() * 6, at, new Color("#eed69d"), 2);
                    if (shot.Kind is WallDefenseKind.None or WallDefenseKind.Ballista)
                    {
                        var direction = (end - start).Normalized();
                        var normal = new Vector2(-direction.Y, direction.X);
                        var tail = at - direction * 5;
                        DrawLine(tail - normal * 2 - direction, tail + normal * 2 - direction, new Color("#c6bf9c"), 1.5f);
                    }
                }
            if (!PerspectiveEnabled && settlement.VisibleRepairingWall is { } repair)
            {
                var inward = new Vector2(settlement.Bounds.X + settlement.Bounds.Width / 2f - repair.X - 0.5f,
                    settlement.Bounds.Y + settlement.Bounds.Height / 2f - repair.Y - 0.5f).Normalized();
                var center = SiegeScreenPoint(repair.X + 0.5 + inward.X, repair.Y + 0.5 + inward.Y);
                var wall = settlement.Walls.Single(w => w.Cell == repair);
                bool firefighting = RepairCrewArtwork.FightingFire(wall);
                float swing = Effects?.Settings.ReducedMotion == true ? 0 :
                    MathF.Sin((float)(settlement.RepairAnimationPhase + settlement.InterpolationFraction * 0.1) * Mathf.Tau);
                DrawRect(new(center - new Vector2(1, 2), new Vector2(2, 3)), new Color("#b49a70"));
                if (!firefighting)
                {
                    var hammer = center + new Vector2(3, -3 - swing * 2);
                    DrawLine(center, hammer, new Color("#79634b"), 1);
                    DrawRect(new(hammer - Vector2.One, new Vector2(2, 1.5f)), new Color("#c8c8d0"));
                }
                var carrier = center + new Vector2(6 + swing, 0);
                DrawRect(new(carrier - new Vector2(1, 2), new Vector2(2, 3)), new Color("#586b71"));
                if (firefighting)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var pose = RepairCrewArtwork.BucketPose(
                            settlement.RepairAnimationPhase + settlement.InterpolationFraction * 0.1, i,
                            Effects?.Settings.ReducedMotion != true);
                        var bucket = (i == 0 ? center : carrier) + new Vector2(2, -pose.Lift * 20);
                        DrawRect(new(bucket - new Vector2(1.5f, 1), new Vector2(3, 3)), new Color("#967a56"));
                        DrawRect(new(bucket - new Vector2(1, 1), new Vector2(2, 1)), new Color("#8fc7d4"));
                        if (pose.Pour >= 0)
                        {
                            var end = SiegeScreenPoint(repair.X + 0.5, repair.Y + 0.5);
                            foreach (var segment in RepairCrewArtwork.BucketWater(
                                System.Numerics.Vector3.Zero, new(end.X - bucket.X, 0, end.Y - bucket.Y), pose.Pour))
                            {
                                var a = bucket + new Vector2(segment.Start.X, segment.Start.Z - segment.Start.Y * 16);
                                var b = bucket + new Vector2(segment.End.X, segment.End.Z - segment.End.Y * 16);
                                DrawLine(a, b, new Color("#8fc7d4"), Math.Max(1.5f, segment.Width * 20));
                            }
                        }
                    }
                }
                else
                    DrawRect(new(carrier - new Vector2(2, 1), new Vector2(Math.Max(4, sx * RepairCrewArtwork.BoardLength), 1.5f)),
                        new Color("#c0a16c"));
            }
        }
        if (Session.Preview is { Kind: PlacementKind.Settlement, Cells: { } cells } preview && !PerspectiveEnabled)
            foreach (var cell in cells)
            {
                var center = SiegeScreenPoint(cell.X + 0.5, cell.Y + 0.5);
                DrawRect(new(center - new Vector2(sx, sy) / 2, new Vector2(sx, sy)),
                    preview.ValidCells?.Count > 0 ? new Color(0.4f, 0.8f, 0.55f, 0.8f) : new Color(0.9f, 0.4f, 0.4f, 0.8f));
            }
        if (!PerspectiveEnabled && Session.ActiveWallDefenseGuide is { } guide)
            DrawWeaponRange(guide.Cell, guide.Kind);
        if (PerspectiveEnabled && Session.ActiveWallDefenseGuide is { Kind: not (WallDefenseKind.OilCauldron or WallDefenseKind.Rocks) })
            DrawString(CellFont, new Vector2(12, Size.Y - 12), "MAXIMUM REACH - clear trajectory required",
                fontSize: FontSize, modulate: new Color("#e5cf79"));
        if (!PerspectiveEnabled && Session.Preview?.WallPlan is { } run && System.Numerics.Vector2.DistanceSquared(run.Start, run.End) >= .0025f)
        {
            var ghost = WallDamageLayout.Preview(run.Start, run.End,
                settlement?.UpgradeTargetMaterial ?? settlement?.Material ?? WallMaterial.Wood, run.Gate);
            if (settlement is not null)
                WallDamageLayout.OrientPreview(ghost, settlement, Session.Preview.WallEnclosures);
            DrawOrientedWall(ghost, run.Valid ? new Color(.4f, .8f, .55f, .8f) : new Color(.9f, .4f, .4f, .8f));
        }
        if (!PerspectiveEnabled && Session.Preview?.WallEnclosures is { } enclosed)
            foreach (var polygon in enclosed)
                DrawPolyline(polygon.Append(polygon[0]).Select(p => SiegeScreenPoint(p.X, p.Y)).ToArray(), new Color("#e5cf79"), 1.5f);
    }

    private void DrawOrientedWall(WallComponent wall, Color? preview = null)
    {
        var tangent = wall.Tangent;
        var normal = new System.Numerics.Vector2(-tangent.Y, tangent.X);
        var color = preview ?? (wall.Material == WallMaterial.Wood ? new Color("#98754b") : new Color("#a4a599"));
        if (preview is null && Session.Selection is not null && Session.SelectedWallComponent?.SectionId == wall.SectionId)
            color = color.Lerp(new Color(1, .94f, .68f), .19f);
        foreach (var part in WallDamageLayout.Fragments(wall, Session.Game.Map).OrderBy(p => p.Center.Y + p.Size.Y / 2))
        {
            var center = wall.Center + tangent * part.Center.X + normal * part.Center.Z;
            var points = new[] { new Vector2(-1, -1), new(1, -1), new(1, 1), new(-1, 1) }.Select(corner =>
            {
                var p = center + tangent * (corner.X * part.Size.X / 2) + normal * (corner.Y * part.Size.Z / 2);
                return SiegeScreenPoint(p.X, p.Y);
            }).ToArray();
            DrawColoredPolygon(points, preview is null && part.Kind == WallFragmentKind.Grate ? new Color("#343b3e") :
                preview is null && part.Kind == WallFragmentKind.Door ? new Color("#443d32") : color);
        }
    }

    private void DrawSiegeHealth(Vector2 position, double fraction, float width)
    {
        DrawRect(new(position - Vector2.One, new Vector2(width + 2, 5)), Colors.Black);
        DrawRect(new(position, new Vector2(width * (float)Math.Clamp(fraction, 0, 1), 3)),
            fraction < 0.35 ? new Color("#e47676") : new Color("#91bd68"));
    }

    private void DrawWeaponRange(Pos cell, WallDefenseKind kind)
    {
        if (kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks && Session.SelectedWallComponent is { } wall)
        {
            int facing = Session.WallDefenseToolActive ? Session.Preview!.Rotation == 2 ? -1 : 1 : wall.DefenseFacing;
            foreach (var polygon in Session.Game.Settlement!.DropCoverage(Session.Game, wall, kind, facing))
                DrawColoredPolygon(polygon.Select(p => SiegeScreenPoint(p.X, p.Y)).ToArray(), new Color(1, .79f, .3f, .3f));
            return;
        }
        var weapon = WallDefenses.Get(kind);
        var source = Session.SelectedWallComponent?.Center ?? new System.Numerics.Vector2(cell.X + .5f, cell.Y + .5f);
        Vector2 Point(double radius, int step) => SiegeScreenPoint(
            source.X + Math.Cos(step * Math.Tau / 64) * radius,
            source.Y + Math.Sin(step * Math.Tau / 64) * radius);
        foreach (double range in new[] { weapon.Range, weapon.MinimumRange }.Where(r => r > 0))
        {
            var points = Enumerable.Range(0, 65).Select(i => Point(range, i)).ToArray();
            DrawPolyline(points, new Color(1, 0.85f, 0.55f, 0.65f), 1.5f, antialiased: true);
        }
        DrawString(CellFont, new Vector2(12, Size.Y - 12), "MAXIMUM REACH - clear trajectory required",
            fontSize: FontSize, modulate: new Color("#e5cf79"));
    }
}
