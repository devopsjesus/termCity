using System.Collections;
using System.Reflection;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class TowerDefenseSmoke
{
    internal static async Task Run(Main host, string? capturePath)
    {
        VerifyTerraceClipping();
        var session = host.Session;
        var original = session.Game;
        double zoom = session.ZoomLevel;
        var center = new Pos(session.CameraX + session.ViewWidth * session.Stride / 2,
            session.CameraY + session.ViewHeight * session.Stride / 2);
        var view = host.Map.Perspective!;
        float azimuth = view.Azimuth;
        double worstActor = 0;
        var game = SaveGameStore.Deserialize(SaveGameStore.Serialize(original));
        Replace(session, game);
        try
        {
            var siege = game.Settlement!;
            var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom);
            foreach (var wall in siege.Walls) Field(wall, "ArrowCooldown", 100d);
            session.SetZoom(0);
            host.Map.RefreshCells();
            session.CenterOn(tower.Cell);
            view.Rotate(325 - view.Azimuth);
            foreach (var kind in new[] { WallDefenseKind.OilCauldron, WallDefenseKind.Rocks })
            {
                var traces = (IList)typeof(SettlementState).GetField("ProjectileList",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
                traces.Clear();
                Require(game.DebugStartSiegeWave(1).Success, "The defense fixture must use a real attackable wave.");
                var enemy = siege.Enemies.Single();
                var drop = DropAttackLayout.Footprints(tower, kind)[0];
                var target = drop.Center - drop.Forward * .25f;
                Set(enemy, nameof(enemy.X), (double)target.X);
                Set(enemy, nameof(enemy.Y), (double)target.Y);
                Set(enemy, nameof(enemy.Health), 1000d);
                Set(enemy, nameof(enemy.MaxHealth), 1000d);
                Field(enemy, "Route", new List<Pos> { tower.Cell });
                Field(enemy, "RouteIndex", 0);
                Field(enemy, "MotionInitialized", false);
                Set(tower, nameof(WallComponent.Defense), kind);
                Field(tower, "DefenseCooldown", 0d);
                foreach (var wall in siege.Walls) Field(wall, "ArrowCooldown", 100d);
                Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
                game.Speed = GameSpeed.Medium;
                Advance(game, .1);
                await Frames(host);
                Require(siege.Projectiles.Any(shot => shot.Kind == kind),
                    $"A real {kind} attack must launch without changing its targeting rules.");
                var shot = siege.Projectiles.First(shot => shot.Kind == kind);
                Require(enemy.Health == enemy.MaxHealth,
                    "Dropped weapons must not damage enemies before the oil or stone reaches the ground.");
                session.SelectCell(tower.Cell);
                await Frames(host);
                if (kind == WallDefenseKind.OilCauldron)
                {
                    view.Tilt();
                    view.Sync(session, host.Map.Size);
                    await Frames(host);
                    await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".cauldron-access.png"));
                    view.Tilt();
                    view.Tilt();
                    view.Sync(session, host.Map.Size);
                    await Frames(host);
                }
                int geometry = view.GeometryBuilds, fortifications = view.SiegeGeometryBuilds;
                Advance(game, kind == WallDefenseKind.OilCauldron ? .8 : 1.2);
                await Frames(host);
                if (kind == WallDefenseKind.OilCauldron)
                {
                    Require(view.OilFlowSegmentsDrawn == 16 && view.OilSheetTriangles >= 128,
                        "A tipped cauldron must pour a connected folded fluid mesh, not a straight cuboid beam.");
                    var mount = TowerDefenseArtwork.Stations(tower)[TowerDefenseArtwork.NearestStation(tower,
                        new((float)shot.EndX, (float)shot.EndY))];
                    var lip = TowerDefenseArtwork.Lip(mount, TowerDefenseArtwork.Rise(tower, game.Map), 1.05f);
                    var point = new Vector3(tower.Cell.X + .5f + lip.X,
                        TerrainSprite.Elevation(game.Map, tower.Cell.X, tower.Cell.Y) +
                            WallDamageLayout.ActorWalkHeight(tower, game.Map) + lip.Y,
                        tower.Cell.Y + .5f + lip.Z);
                    Require(host.Map.TryPickBuilding(view.ScreenPoint(point), out var picked) && picked == tower.Cell,
                        "The tipped cauldron's visible overhang must remain directly pickable.");
                }
                else
                    Require(view.DroppedStonesDrawn == 3,
                        $"Stone releases must stagger through the gap: visible={view.DroppedStonesDrawn}, " +
                        $"elapsed={shot.Progress * shot.FlightSeconds:F2}, animated={view.SiegeAnimationEnabled}.");
                Require(view.SelectionVisualFootprint is { } frame && view.SelectedObjectBounds is { } bounds &&
                    frame.Encloses(new Rect2(bounds.Position.X, bounds.Position.Z, bounds.Size.X, bounds.Size.Z)),
                    "Moving cauldrons must remain inside their actual selection footprint.");
                await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".{kind}-release.png"));
                int actors = view.SiegeActorBuilds;
                await Frames(host);
                Require(view.SiegeActorBuilds == actors, "Pausing must freeze cauldron motion and falling effects.");
                if (kind == WallDefenseKind.OilCauldron)
                {
                    Advance(game, Math.Max(0, shot.ContactSeconds + .02 - shot.Elapsed));
                    await Frames(host);
                    Require(view.DefenseSplashesDrawn > 0 && view.LargestDefenseSplashRadius < DropAttackLayout.Size(kind) / 2,
                        "Oil must begin as a small pool instead of popping into a full square footprint.");
                    await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".oil-pool-early.png"));
                }
                float landing = TowerDefenseArtwork.WindupSeconds +
                    TowerDefenseArtwork.FallSeconds(shot.DropHeight) + .18f;
                Advance(game, Math.Max(0, landing - shot.Progress * shot.FlightSeconds));
                await Frames(host);
                Require(view.DefenseSplashesDrawn > 0 &&
                    Math.Abs(view.LargestDefenseSplashRadius - DropAttackLayout.Size(kind) / 2) < .001,
                    "Oil and stone impacts must match their small station footprints.");
                Require(enemy.Health == enemy.MaxHealth - WallDefenses.Get(kind).Damage,
                    "Ground contact must apply exactly one strike, without extra pour damage.");
                Require(view.GeometryBuilds == geometry && view.SiegeGeometryBuilds == fortifications,
                    $"Defense motion must remain cached: kind={kind}, city={geometry}->{view.GeometryBuilds}, " +
                    $"fortifications={fortifications}->{view.SiegeGeometryBuilds}.");
                worstActor = Math.Max(worstActor, view.SiegeActorBuildMilliseconds);
                await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".{kind}-impact.png"));
                if (kind == WallDefenseKind.OilCauldron)
                {
                    Advance(game, .25);
                    await Frames(host);
                    Require(view.OilSheetVisible && view.OilSheetTriangles > 0 &&
                        enemy.Health == enemy.MaxHealth - WallDefenses.Get(kind).Damage,
                        "The fluid sheet must continue moving after contact without applying repeated pour damage.");
                    await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".oil-flow-later.png"));
                }
                var effects = host.Map.Effects ?? throw new InvalidOperationException("The defense fixture requires effect settings.");
                bool reduced = effects.Settings.ReducedMotion;
                effects.Settings.ReducedMotion = true;
                await Frames(host);
                Require(view.OilFlowSegmentsDrawn == 0 && view.DroppedStonesDrawn == 0 && !view.OilSheetVisible,
                    "Reduced motion must show static impact guides instead of moving streams or stones.");
                effects.Settings.ReducedMotion = reduced;
                host.Map.RefreshCells();
                view.Sync(session, host.Map.Size);
                await Frames(host);
            }
            await VerifyHeavyImpacts(host, game, capturePath);
            await VerifyOrphanedOil(host, game, capturePath);
        }
        finally
        {
            Replace(session, original);
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            session.CenterOn(center);
            view.Rotate(azimuth - view.Azimuth);
            session.ClearSelection();
            await Frames(host);
        }
        var cache = Descendants(host).OfType<RenderedModelPreviewCache>().Single();
        var panel = new TerminalFrame { Position = new(90, 140), Size = new(380, 220) };
        var previews = new HBoxContainer();
        panel.AddChild(previews);
        foreach (var kind in new[] { WallDefenseKind.OilCauldron, WallDefenseKind.Rocks })
            previews.AddChild(new RenderedModelPreview
            {
                Cache = cache, Subject = new ModelPreviewSubject(original.Map.Content, Defense: kind),
                CustomMinimumSize = new(170, 170),
            });
        host.AddChild(panel);
        try
        {
            await Frames(host);
            await GodotSmoke.VerifyModelPreviews(host, panel);
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".defense-thumbnails.png"));
        }
        finally
        {
            host.RemoveChild(panel);
            panel.QueueFree();
            await Frames(host);
        }
        Require(worstActor < 12, $"Defense actor updates took {worstActor:F1}ms; budget is 12ms.");
        GD.Print($"TERMCITY_TOWER_DEFENSES_OK stations=exterior steps=4 oil=connected stones=gravity " +
            $"splash=ground pause=frozen picking=overhang geometry=cached actor-worst={worstActor:F1}ms");
    }

    private static void VerifyTerraceClipping()
    {
        Vector2[] boundary = [new(.25f, .25f), new(1.75f, .25f), new(1.75f, .75f), new(.25f, .75f)];
        Vector3[] faces = [
            new(0, 0, 0), new(1, 0, 1), new(1, 0, 0),
            new(0, 0, 0), new(0, 0, 1), new(1, 0, 1),
            new(1, 2, 0), new(2, 2, 1), new(2, 2, 0),
            new(1, 2, 0), new(1, 2, 1), new(2, 2, 1),
            new(1, 0, 0), new(1, 2, 0), new(1, 2, 1),
        ];
        var vertices = PerspectiveCity.ClipGroundOverlay(boundary, faces);
        Require(vertices.Count > 0 && vertices.Count % 3 == 0, "Terrace overlays need complete clipped triangles.");
        float area = 0;
        bool low = false, high = false;
        for (int i = 0; i < vertices.Count; i += 3)
        {
            var a = vertices[i]; var b = vertices[i + 1]; var c = vertices[i + 2];
            Require(Math.Abs(a.Y - b.Y) < .00001f && Math.Abs(a.Y - c.Y) < .00001f,
                "A damage-guide triangle must not span a terrace riser.");
            Require(Math.Abs(a.Y - .035f) < .00001f || Math.Abs(a.Y - 2.035f) < .00001f,
                "Each guide layer must sit just above the actual terrain top, not its cliff face.");
            low |= a.Y < 1;
            high |= a.Y > 1;
            area += (b - a).Cross(c - a).Length() / 2;
        }
        Require(low && high && Math.Abs(area - .75f) < .0001f,
            "Clipping must preserve the exact damage area across both terrace levels without gaps or duplicate triangles.");
        GD.Print("TERMCITY_DAMAGE_TERRACES_OK layers=2 area=exact risers=excluded vertices=surface+0.035");
    }

    private static async Task VerifyHeavyImpacts(Main host, CityGame game, string? capture)
    {
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom &&
            w.Cell.X > siege.Bounds.X + 8 && w.Cell.X < siege.Bounds.Right - 8);
        foreach (var wall in siege.Walls) Set(wall, nameof(WallComponent.Defense), WallDefenseKind.None);
        foreach (var kind in new[] { WallDefenseKind.Ballista, WallDefenseKind.Trebuchet })
        {
            Require(game.DebugStartSiegeWave(1).Success, "Heavy-weapon validation needs a real siege target.");
            var enemy = siege.Enemies.Single();
            float distance = kind == WallDefenseKind.Ballista ? 8 : 14;
            Set(enemy, nameof(enemy.X), (double)tower.Center.X);
            Set(enemy, nameof(enemy.Y), (double)tower.Center.Y + distance);
            Set(enemy, nameof(enemy.Health), 2000d);
            Set(enemy, nameof(enemy.MaxHealth), 2000d);
            Field(enemy, "MotionInitialized", false);
            Field(enemy, "Route", new List<Pos> { tower.Cell });
            Field(enemy, "RouteIndex", 0);
            Set(tower, nameof(WallComponent.Defense), kind);
            Field(tower, "DefenseCooldown", 0d);
            foreach (var wall in siege.Walls) Field(wall, "ArrowCooldown", 100d);
            Advance(game, .1);
            var shot = siege.Projectiles.FirstOrDefault(p => p.Kind == kind);
            Require(shot is not null && enemy.Health == 2000, $"{kind} must release without instant damage.");
            void ContactClock(double seconds)
            {
                typeof(SettlementState).GetMethod("AdvanceCommittedAttacks", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(siege, [game, seconds]);
                Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
            }
            host.Session.SetZoom(-1);
            host.Map.RefreshCells();
            host.Session.CenterOn(tower.Cell);
            host.Session.SelectCell(tower.Cell);
            ContactClock(shot!.Windup + shot.TravelSeconds * .4);
            await Frames(host);
            Require(!shot.Applied, "The heavy projectile must remain live during its clear flight.");
            Require(view.WeaponRangeVisible && view.WeaponRangeTriangles > 0 &&
                view.WeaponRangeOuter == WallDefenses.Get(kind).Range &&
                view.WeaponRangeInner == WallDefenses.Get(kind).MinimumRange,
                "Selecting a firing weapon must retain its ground-range guide without a target-connecting line.");
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, $".{kind}-flight.png"));
            host.Map.SetPerspective(false);
            await Frames(host);
            Require(!shot.Applied && host.Session.ActiveWallDefenseGuide?.Kind == kind,
                "Overhead view must retain the live projectile and selected weapon's ground-range guide.");
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, $".{kind}-flight-overhead.png"));
            host.Map.SetPerspective(true);
            await Frames(host);
            ContactClock(Math.Max(0, shot.ContactSeconds - shot.Elapsed) + .05);
            await Frames(host);
            Require(shot.Applied && enemy.Health < 2000, "The heavy weapon must damage its target at physical impact.");
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, $".{kind}-contact.png"));
        }
        GD.Print("TERMCITY_HEAVY_IMPACTS_OK weapons=ballista,trebuchet damage=contact aim=animated trajectories=snapshotted");
    }

    private static async Task VerifyOrphanedOil(Main host, CityGame game, string? capture)
    {
        var siege = game.Settlement!;
        foreach (var wall in siege.Walls) Set(wall, nameof(WallComponent.Defense), WallDefenseKind.None);
        Require(game.DebugStartSiegeWave(1).Success, "The orphaned-oil fixture requires one real enemy.");
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom);
        var footprint = DropAttackLayout.Footprints(tower, WallDefenseKind.OilCauldron)[0];
        var point = footprint.Center - footprint.Forward * .25f;
        var enemy = siege.Enemies.Single();
        Set(enemy, nameof(enemy.X), (double)point.X); Set(enemy, nameof(enemy.Y), (double)point.Y);
        Set(enemy, nameof(enemy.Health), 1000d); Set(enemy, nameof(enemy.MaxHealth), 1000d);
        Field(enemy, "MotionInitialized", false); Field(enemy, "Route", new List<Pos> { tower.Cell });
        Field(enemy, "RouteIndex", 0);
        foreach (var wall in siege.Walls) Field(wall, "ArrowCooldown", 100d);
        Set(tower, nameof(WallComponent.Defense), WallDefenseKind.OilCauldron);
        Field(tower, "DefenseCooldown", 0d);
        Advance(game, .1);
        var shot = siege.Projectiles.Single(p => p.Kind == WallDefenseKind.OilCauldron);
        Require(game.RemoveWallSection(tower.Cell, tower.Id).Success, "The source tower must actually be removed.");
        Require(siege.Projectiles.Contains(shot), "Removing a tower must not cancel released oil.");
        typeof(SettlementState).GetMethod("AdvanceCommittedAttacks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(siege, [game, shot.ContactSeconds + .05]);
        Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        host.Session.SetZoom(0);
        host.Map.RefreshCells();
        host.Session.CenterOn(tower.Cell);
        await Frames(host);
        Require(shot.Applied && enemy.Health == 820 && host.Map.Perspective!.DefenseSplashesDrawn > 0,
            "Orphaned oil must land, damage once and render its saved footprint without resurrecting its source.");
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".oil-source-loss.png"));
        GD.Print("TERMCITY_OIL_SOURCE_LOSS_OK tower=removed impact=retained damage=once render=snapshot");
    }

    private static void Advance(CityGame game, double seconds)
    {
        while (seconds > .00001)
        {
            double step = Math.Min(.05, seconds);
            game.Paused = false;
            typeof(CityGame).GetMethod("AdvanceSiege", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(game, [step]);
            game.Paused = true;
            seconds -= step;
        }
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);

    private static void Field(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Replace(GameSession session, CityGame game) =>
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [game, true]);

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
