using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class TerminalMap
{
    internal int WallHealthBarsDrawn { get; private set; }

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
        var settlement = Session.Game.Settlement;
        float sx = Grid.PixelWidth / (float)Session.Stride, sy = Grid.PixelHeight / (float)Session.Stride;
        if (settlement is not null)
        {
            foreach (var wall in settlement.Walls)
            {
                var center = SiegeScreenPoint(wall.Cell.X + 0.5, wall.Cell.Y + 0.5,
                    WallDamageLayout.TopHeight(wall) + 0.4f);
                if (!PerspectiveEnabled)
                {
                    bool horizontal = wall.Cell.Y == settlement.Bounds.Y || wall.Cell.Y == settlement.Bounds.Bottom;
                    var size = wall.IsTower ? new Vector2(sx, sy) * 0.95f
                        : horizontal ? new Vector2(sx, sy * 0.4f) : new Vector2(sx * 0.4f, sy);
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
                    var healthCenter = SiegeScreenPoint(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0,
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
            foreach (var goblin in settlement.Goblins)
            {
                var pose = SiegeMotion.Goblin(settlement, goblin);
                if (Effects?.Settings.ReducedMotion == true) pose = pose with { Stride = 0, Bob = 0, Sway = 0, Attack = 0 };
                var center = SiegeScreenPoint(pose.Position.X, pose.Position.Y, 1.6f);
                if (!PerspectiveEnabled)
                {
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
                if (goblin.Health < goblin.MaxHealth) DrawSiegeHealth(center - new Vector2(8, 4), goblin.Health / goblin.MaxHealth, 16);
            }
            if (!PerspectiveEnabled)
                foreach (var shot in settlement.Projectiles)
                {
                    var start = SiegeScreenPoint(shot.StartX, shot.StartY);
                    var end = SiegeScreenPoint(shot.EndX, shot.EndY);
                    float progress = SiegeMotion.ProjectileProgress(settlement, shot);
                    if (progress >= 1) continue;
                    var at = start.Lerp(end, progress);
                    DrawLine(at - (end - start).Normalized() * 6, at, new Color("#eed69d"), 2);
                }
            if (!PerspectiveEnabled && settlement.RepairingWall is { } repair)
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
        if (Session.AreaOverlaysVisible && Session.Preview is { Kind: PlacementKind.WallDefense } defensePreview)
            DrawWeaponRange(Session.Cursor, defensePreview.Defense);
        else if (Session.AreaOverlaysVisible && Session.HasCursor && settlement?.Walls.FirstOrDefault(w => w.Cell == Session.Cursor) is { } selected &&
            selected.Defense != WallDefenseKind.None)
            DrawWeaponRange(selected.Cell, selected.Defense);
    }

    private void DrawSiegeHealth(Vector2 position, double fraction, float width)
    {
        DrawRect(new(position - Vector2.One, new Vector2(width + 2, 5)), Colors.Black);
        DrawRect(new(position, new Vector2(width * (float)Math.Clamp(fraction, 0, 1), 3)),
            fraction < 0.35 ? new Color("#e47676") : new Color("#91bd68"));
    }

    private void DrawWeaponRange(Pos cell, WallDefenseKind kind)
    {
        var weapon = WallDefenses.Get(kind);
        var center = SiegeScreenPoint(cell.X + 0.5, cell.Y + 0.5, 2);
        float scale = PerspectiveEnabled && Perspective is { } view
            ? Size.Y / view.CameraSpan : Grid.PixelWidth / (float)Session.Stride;
        foreach (double range in new[] { weapon.Range, weapon.MinimumRange }.Where(r => r > 0))
        {
            var points = Enumerable.Range(0, 65).Select(i => center +
                new Vector2(Mathf.Cos(i * Mathf.Tau / 64), Mathf.Sin(i * Mathf.Tau / 64)) * (float)range * scale).ToArray();
            DrawPolyline(points, new Color(1, 0.85f, 0.55f, 0.65f), 1.5f, antialiased: true);
        }
    }
}
