using System.Diagnostics;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class SiegeAnimationSmoke
{
    internal static async Task VerifyProjectileFlight(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var projectiles = (List<SiegeProjectile>)typeof(SettlementState).GetField("ProjectileList", flags)!.GetValue(siege)!;
        var accumulator = typeof(SettlementState).GetField("Accumulator", flags)!;
        var savedShots = projectiles.ToArray();
        object savedAccumulator = accumulator.GetValue(siege)!;
        bool paused = game.Paused;
        int zoom = session.ZoomLevel;
        float azimuth = view.Azimuth;
        var center = new Pos(session.CameraX + session.VisibleCellsX / 2, session.CameraY + session.VisibleCellsY / 2);
        int revision = siege.Revision;
        void Set(object value, string property, object data) =>
            value.GetType().GetProperty(property)!.SetValue(value, data);
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom);
        var start = view.WallTop(tower) + Vector3.Up;
        session.SetZoom(0);
        view.Rotate(325 - view.Azimuth);
        session.CenterOn(tower.Cell);
        host.Map.RefreshCells();
        game.Paused = true;
        projectiles.Clear();
        foreach (var kind in new[] { WallDefenseKind.None, WallDefenseKind.Ballista, WallDefenseKind.Trebuchet })
        {
            var shot = new SiegeProjectile();
            Set(shot, nameof(shot.Id), 1000000);
            Set(shot, nameof(shot.Kind), kind);
            Set(shot, nameof(shot.Windup), .05d);
            Set(shot, nameof(shot.TravelSeconds), .12d);
            Set(shot, nameof(shot.StartX), (double)start.X);
            Set(shot, nameof(shot.StartY), (double)start.Z);
            Set(shot, nameof(shot.EndX), (double)start.X + 4);
            Set(shot, nameof(shot.EndY), (double)start.Z);
            Set(shot, nameof(shot.WorldStartHeight), start.Y);
            Set(shot, nameof(shot.WorldEndHeight), start.Y - 1);
            Set(shot, nameof(shot.ArcHeight), kind == WallDefenseKind.Trebuchet ? 3f : 0f);
            projectiles.Add(shot);
            foreach (bool perspective in new[] { true, false })
            {
                host.Map.SetPerspective(perspective);
                host.Map.RefreshCells();
                session.CenterOn(tower.Cell);
                Set(shot, nameof(shot.Elapsed), 0d);
                accumulator.SetValue(siege, .025d);
                Set(siege, nameof(siege.Revision), ++revision);
                await Frames(host);
                int geometry = view.GeometryBuilds, walls = view.SiegeGeometryBuilds;
                Require(perspective ? view.RenderedProjectilePosition(shot.Id) is null :
                    !host.Map.RenderedProjectilePoints.ContainsKey(shot.Id), "Windup must hide the projectile.");
                accumulator.SetValue(siege, .075d);
                await Frames(host);
                var first3D = view.RenderedProjectilePosition(shot.Id);
                var first2D = host.Map.RenderedProjectilePoints.GetValueOrDefault(shot.Id);
                Require(perspective ? first3D is not null : host.Map.RenderedProjectilePoints.ContainsKey(shot.Id),
                    "Sub-tick release must use the same visual clock as flight.");
                accumulator.SetValue(siege, .095d);
                await Frames(host);
                Require(perspective ? view.RenderedProjectilePosition(shot.Id) != first3D :
                    host.Map.RenderedProjectilePoints[shot.Id] != first2D,
                    $"Rendered committed projectiles must move within an unchanged combat tick: kind={kind} " +
                    $"perspective={perspective} fraction={siege.InterpolationFraction} " +
                    $"first3D={first3D} current3D={view.RenderedProjectilePosition(shot.Id)} " +
                    $"first2D={first2D} current2D={host.Map.RenderedProjectilePoints.GetValueOrDefault(shot.Id)}.");
                var frozen3D = view.RenderedProjectilePosition(shot.Id);
                var frozen2D = host.Map.RenderedProjectilePoints.GetValueOrDefault(shot.Id);
                game.Update(.5);
                await Frames(host);
                Require(perspective ? view.RenderedProjectilePosition(shot.Id) == frozen3D :
                    host.Map.RenderedProjectilePoints[shot.Id] == frozen2D, "Paused projectiles must remain frozen.");
                await Capture(host, capturePath is null ? null :
                    Path.ChangeExtension(capturePath, $".flight-{kind}-{perspective}.png"));
                Set(shot, nameof(shot.Elapsed), shot.ContactSeconds);
                accumulator.SetValue(siege, 0d);
                await Frames(host);
                if (perspective)
                    Require(view.RenderedProjectilePosition(shot.Id) is { } endpoint &&
                        endpoint.DistanceTo(new((float)shot.EndX, shot.WorldEndHeight, (float)shot.EndY)) < .001f,
                        "Perspective flight must clamp to contact without overshooting.");
                else Require(!host.Map.RenderedProjectilePoints.ContainsKey(shot.Id),
                    "Overhead flight must retire at its existing endpoint boundary.");
                Set(shot, nameof(shot.Applied), true);
                Set(siege, nameof(siege.Revision), ++revision);
                await Frames(host);
                Require(perspective ? view.RenderedProjectilePosition(shot.Id) is null :
                    !host.Map.RenderedProjectilePoints.ContainsKey(shot.Id), "Applied shots must not resume flight.");
                Require(view.GeometryBuilds == geometry && view.SiegeGeometryBuilds == walls,
                    "Projectile interpolation must not rebuild static city or wall geometry.");
                Set(shot, nameof(shot.Applied), false);
            }
            projectiles.Clear();
        }
        projectiles.AddRange(savedShots);
        accumulator.SetValue(siege, savedAccumulator);
        Set(siege, nameof(siege.Revision), ++revision);
        host.Map.SetPerspective(true);
        view.Rotate(azimuth - view.Azimuth);
        session.SetZoom(zoom);
        session.CenterOn(center);
        host.Map.RefreshCells();
        game.Paused = paused;
        await Frames(host);
        GD.Print("TERMCITY_PROJECTILE_FLIGHT_OK kinds=3 views=2 sub-tick=rendered windup=smooth pause=frozen contact=clamped");
    }

    internal static async Task VerifySurfaceScrolling(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        int zoom = session.ZoomLevel;
        float azimuth = view.Azimuth;
        var fixture = game.Map.BuildingFootprints.Values.First(area =>
            game.Map.BuildingAt(area.X, area.Y)?.Zone == ZoneType.Residential);
        var terrain = game.Map.TerrainAt(fixture.X, fixture.Y);
        game.Map.SetTerrain(fixture.X, fixture.Y, game.Map.Content.Terrains.Get("Hill"));
        game.Touch();
        session.SetZoom(0);
        session.CenterOn(new(fixture.X + fixture.Width / 2, fixture.Y + fixture.Height / 2));
        host.Map.RefreshCells();
        await Frames(host);
        float floor = fixture.Cells().Max(p => TerrainSprite.Elevation(game.Map, p.X, p.Y));
        int supports = view.BuildingVolumes(new(fixture.X, fixture.Y), HouseAppearance.StoneColor)
            .Count(box => box.Position.Y < floor - 0.05f && box.End.Y >= floor - 0.0001f);
        Require(supports > 0, "Surface checks must exercise actual split-level stone foundations.");
        int homes = 0;
        foreach (var area in game.Map.BuildingFootprints.Values.Where(area =>
            game.Map.BuildingAt(area.X, area.Y)?.Zone == ZoneType.Residential))
        {
            var boxes = view.BuildingVolumes(new(area.X, area.Y), HouseAppearance.StoneColor);
            if (boxes.Count == 0) continue;
            homes++;
            var timber = view.BuildingVolumes(new(area.X, area.Y), HouseAppearance.FrameColor);
            foreach (var stone in boxes)
                Require(!timber.Any(beam => Math.Abs(beam.End.Y - stone.End.Y) < .0001f &&
                    Math.Min(beam.End.X, stone.End.X) - Math.Max(beam.Position.X, stone.Position.X) > .0001f &&
                    Math.Min(beam.End.Z, stone.End.Z) - Math.Max(beam.Position.Z, stone.Position.Z) > .0001f),
                    "Stone plinths must not share exposed top faces with dark timber beams.");
            for (int a = 0; a < boxes.Count; a++)
                for (int b = a + 1; b < boxes.Count; b++)
                {
                    var overlap = boxes[a].Intersection(boxes[b]).Size;
                    Require(overlap.X < 0.0001f || overlap.Y < 0.0001f || overlap.Z < 0.0001f,
                        "House plinths, steps and hillside supports must not overlap textured stone faces.");
                }
        }
        Require(homes > 0, "Surface scrolling checks must include rendered house foundations.");
        Require(view.StablePixelMaterials, "Opaque textures must use linear mipmapped anisotropic filtering.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".foundation.png"));
        game.Map.SetTerrain(fixture.X, fixture.Y, terrain);
        game.Touch();
        host.Map.RefreshCells();
        var gate = siege.Walls.Single(w => w.IsGate);
        session.SetZoom(0);
        session.CenterOn(gate.Cell);
        await Frames(host);
        Require(WallDamageLayout.Fragments(gate).Count(part => part.Kind == WallFragmentKind.DoorBrace) == 18,
            "The intact gate must have visible raised crossbars on both faces.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".gate-braces.png"));
        var corners = siege.Walls.Where(w => w.IsTower &&
            (w.Cell.X == siege.Bounds.X || w.Cell.X == siege.Bounds.Right) &&
            (w.Cell.Y == siege.Bounds.Y || w.Cell.Y == siege.Bounds.Bottom)).ToArray();
        Require(corners.Length == 4, "Surface scrolling checks must exercise all four actual corner towers.");
        foreach (var tower in corners)
        {
            session.SetZoom(0);
            view.Rotate(azimuth - view.Azimuth);
            session.CenterOn(tower.Cell);
            await Frames(host);
            for (int rotation = 0; rotation < 4; rotation++)
            {
                if (rotation > 0) view.Rotate(90);
                view.ResetCameraOffset();
                await Frames(host);
                int builds = view.GeometryBuilds, walls = view.SiegeGeometryBuilds;
                foreach (float offset in new[] { -0.3f, 0f, 0.3f })
                {
                    view.SetCameraOffset(session, new(offset, offset / 2));
                    await Frames(host);
                    var deck = view.WallTop(tower) - Vector3.Up * WorldScale.Feet(3);
                    var screen = view.ScreenPoint(deck);
                    bool hit = host.Map.TryPickBuilding(screen, out var picked);
                    if (rotation == 0)
                        await Capture(host, capturePath is null ? null :
                            Path.ChangeExtension(capturePath,
                                $".corner-{tower.Cell.X}-{tower.Cell.Y}-pan-{offset + 0.3f:F1}.png"));
                    Require(hit && picked == tower.Cell,
                        $"The tower deck must remain pickable during scrolling: rotation={rotation}, offset={offset}, " +
                        $"screen={screen}, viewport={host.Map.Size}, picked={picked}, tower={tower.Cell}.");
                    Require(view.GeometryBuilds == builds && view.SiegeGeometryBuilds == walls,
                        "Fractional scrolling must reuse cached city and fortification geometry.");
                }
            }
        }
        view.ResetCameraOffset();
        view.Rotate(azimuth - view.Azimuth);
        await VerifyTextureStability(host);
        session.SetZoom(zoom);
        session.CenterOn(new(siege.Bounds.X + siege.Bounds.Width / 2, siege.Bounds.Y + siege.Bounds.Height / 2));
        await Frames(host);
        Console.WriteLine($"TERMCITY_STRUCTURE_SURFACES_OK houses={homes} supports={supports} corners=4 rotations=4 fractional-pans=48 geometry=cached");
    }

    private static async Task VerifyTextureStability(Main host)
    {
        if (DisplayServer.GetName() == "headless") return;
        var view = host.Map.Perspective!;
        var session = host.Session;
        session.SetZoom(-1);
        host.Map.RefreshCells();
        if (session.Game.Settlement is { } settlement)
            session.CenterOn(new(settlement.Bounds.X + settlement.Bounds.Width / 2,
                settlement.Bounds.Y + settlement.Bounds.Height / 2));
        view.ResetCameraOffset();
        await Frames(host);
        var materials = view.PixelMaterials.ToArray();
        var frames = new List<(byte[] Pixels, Vector2 Shift)>[2] { [], [] };
        int width = 0, height = 0;
        try
        {
            for (int mode = 0; mode < 2; mode++)
            {
                foreach (var material in materials)
                    material.TextureFilter = mode == 0 ? BaseMaterial3D.TextureFilterEnum.Nearest :
                        BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
                view.ResetCameraOffset();
                await Frames(host);
                var origin = view.ScreenPoint(Vector3.Zero);
                foreach (float offset in new[] { 0f, .02f, .04f, .06f, .08f })
                {
                    view.SetCameraOffset(session, new(offset, offset / 2));
                    await Frames(host);
                    using var image = view.CaptureDisplayImage();
                    image.Convert(Image.Format.Rgba8);
                    width = image.GetWidth();
                    height = image.GetHeight();
                    var shift = (view.ScreenPoint(Vector3.Zero) - origin) *
                        new Vector2(width / host.Map.Size.X, height / host.Map.Size.Y);
                    frames[mode].Add((image.GetData(), shift));
                }
            }
        }
        finally
        {
            foreach (var material in materials)
                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
            view.ResetCameraOffset();
        }
        var mask = new List<(int X, int Y)>();
        for (int y = 2; y < height - 2; y += 2)
            for (int x = 2; x < width - 2; x += 2)
            {
                int index = (y * width + x) * 4;
                if (Enumerable.Range(0, 3).Sum(channel => Math.Abs(
                    frames[0][0].Pixels[index + channel] - frames[1][0].Pixels[index + channel])) >= 12)
                    mask.Add((x, y));
            }
        Require(mask.Count >= 200, $"Scroll-flicker analysis must measure real minified textured surfaces: sampled={mask.Count}, " +
            $"camera={session.CameraX},{session.CameraY}, view={view.RenderedView}.");
        double Residual(int mode)
        {
            double error = 0;
            foreach (var frame in frames[mode].Skip(1))
                foreach (var point in mask)
                {
                    float x = point.X + frame.Shift.X, y = point.Y + frame.Shift.Y;
                    int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
                    float fx = x - left, fy = y - top;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double Sample(int px, int py) => frame.Pixels[(py * width + px) * 4 + channel];
                        double sampled = (Sample(left, top) * (1 - fx) + Sample(left + 1, top) * fx) * (1 - fy) +
                            (Sample(left, top + 1) * (1 - fx) + Sample(left + 1, top + 1) * fx) * fy;
                        error += Math.Abs(sampled - frames[mode][0].Pixels[(point.Y * width + point.X) * 4 + channel]);
                    }
                }
            return error / (mask.Count * (frames[mode].Count - 1) * 3);
        }
        double nearest = Residual(0), filtered = Residual(1);
        GD.Print($"TERMCITY_SCROLL_FLICKER pixels={mask.Count} nearest-error={nearest:F3} filtered-error={filtered:F3}");
        Require(filtered <= nearest * .75,
            "Mipmapped pixel materials must reduce motion-compensated texture flicker by at least 25 percent.");
    }

    internal static async Task VerifyWallDamage(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        Require(session.DebugClearGoblins().Success, session.Message);
        game.Paused = true;
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        float originalAzimuth = view.Azimuth;
        var sections = siege.WallSections.Where(w => !w.IsTower && !w.IsGate)
            .OrderByDescending(w => w.Cell.Y == siege.Bounds.Bottom).ThenBy(w => w.Cell.X).Take(7).ToArray();
        int ExpectedBurning() => siege.Walls.Count(w => w.Health / w.MaxHealth < WallDamageLayout.FireThreshold);
        double[] fractions = [1, 0.75, 0.5, 0.33, 0.329, 0.1, 0];
        void SetFixture(object value, string property, object data) =>
            value.GetType().GetProperty(property)!.SetValue(value, data);
        for (int i = 0; i < sections.Length; i++)
            SetFixture(sections[i], nameof(WallComponent.Health), sections[i].MaxHealth * fractions[i]);
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom);
        var gate = siege.Walls.Single(w => w.IsGate);
        SetFixture(tower, nameof(WallComponent.Health), tower.MaxHealth * 0.2);
        SetFixture(gate, nameof(WallComponent.Health), gate.MaxHealth * 0.3);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        session.PlaceCursor(sections[3].Cell);
        session.SetZoom(1);
        host.Map.RefreshCells();
        view.Rotate(45 - view.Azimuth);
        session.CenterOn(session.Cursor);
        await Frames(host);
        Require(view.BurningWallsDrawn == ExpectedBurning(),
            "Only sections strictly below 33 percent, including breached rubble, must burn.");
        Require(host.Map.WallHealthBarsDrawn == siege.WallSections.Count(w => w.Health < w.MaxHealth ||
            session.HasCursor && w.SectionBounds.Contains(session.Cursor)),
            "Each multi-cell wall section must draw only one shared health bar.");
        var fragment = WallDamageLayout.Fragments(sections[2]).Where(part => part.Kind == WallFragmentKind.Body)
            .MaxBy(part => part.Size.Y);
        var piece = new Vector3(sections[2].Cell.X + 0.5f + fragment.Center.X,
            TerrainSprite.Elevation(game.Map, sections[2].Cell.X, sections[2].Cell.Y) + fragment.Center.Y,
            sections[2].Cell.Y + 0.5f + fragment.Center.Z);
        Require(host.Map.TryPickBuilding(view.ScreenPoint(piece), out var picked) && picked == sections[2].Cell,
            "The remaining fragments of a damaged wall must remain directly pickable.");
        int damagedBuilds = view.SiegeGeometryBuilds;
        SetFixture(sections[6], nameof(WallComponent.Health), 0.01d);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        await Frames(host);
        Require(view.SiegeGeometryBuilds > damagedBuilds && view.BurningWallsDrawn == ExpectedBurning(),
            "Rebuilding a breach must restore standing fragments even within the same health band.");
        damagedBuilds = view.SiegeGeometryBuilds;
        SetFixture(sections[3], nameof(WallComponent.Health), sections[3].MaxHealth * 0.329);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        await Frames(host);
        Require(view.BurningWallsDrawn == ExpectedBurning() && view.SiegeGeometryBuilds > damagedBuilds,
            "Crossing the exact fire threshold must invalidate walls even within the same damage band.");
        SetFixture(sections[3], nameof(WallComponent.Health), sections[3].MaxHealth * 0.33);
        SetFixture(sections[6], nameof(WallComponent.Health), 0d);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        await Frames(host);
        Require(view.BurningWallsDrawn == ExpectedBurning(), "Repairs to exactly 33 percent must immediately extinguish flames.");
        int geometry = view.GeometryBuilds, actors = view.SiegeActorBuilds;
        await Frames(host);
        Require(view.SiegeActorBuilds == actors, "Paused flames must remain frozen.");
        foreach (var material in Enum.GetValues<WallMaterial>())
        {
            SetFixture(siege, nameof(SettlementState.Material), material);
            SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
            await Frames(host);
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".damage-{material}.png"));
        }
        game.Paused = false;
        game.Update(0.04);
        game.Paused = true;
        await Frames(host);
        Require(view.SiegeActorBuilds > actors && view.GeometryBuilds == geometry && view.BurningWallsDrawn == ExpectedBurning(),
            "Flames must animate without attackers and without rebuilding the city.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".flames-b.png"));
        bool animation = view.SiegeAnimationEnabled;
        view.SiegeAnimationEnabled = false;
        view.Sync(session, host.Map.Size);
        Require(view.BurningWallsDrawn == ExpectedBurning(), "Reduced motion must retain visible static flames.");
        view.SiegeAnimationEnabled = animation;
        host.Map.SetPerspective(false);
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".damage-overhead.png"));
        host.Map.SetPerspective(true);
        await Frames(host);
        Require(session.DebugRepairBuildings().Success, session.Message);
        await Frames(host);
        Require(view.BurningWallsDrawn == 0 && siege.Walls.All(w => WallDamageLayout.DamageBand(w) == 0) &&
            view.GeometryBuilds == geometry, "Full repairs must restore silhouettes and extinguish flames without city rebuilds.");
        SetFixture(siege, nameof(SettlementState.Material), WallMaterial.Wood);
        foreach (var wall in siege.Walls) SetFixture(wall, nameof(WallComponent.Health), wall.MaxHealth * 0.3);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        Require(session.DebugStartWave(SettlementState.MaximumEnemySwarm).Success, session.Message);
        await Frames(host);
        Require(view.BurningWallsDrawn == siege.Walls.Count, "Every critically damaged section must retain its fire effect.");
        await VerifyActiveSwarmBudget(host);
        view.Rotate(originalAzimuth - view.Azimuth);
        GD.Print("TERMCITY_WALL_DAMAGE_OK threshold=strict-33% materials=3 breaches=burning pause=frozen reduced-motion=static repair=restored city-rebuilds=0");
    }

    internal static async Task VerifyWalking(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        float azimuth = Mathf.DegToRad(view.Azimuth);
        var goblin = siege.Enemies.OrderByDescending(unit =>
            (unit.X - siege.Bounds.X - siege.Bounds.Width / 2.0) * Mathf.Sin(azimuth) +
            (unit.Y - siege.Bounds.Y - siege.Bounds.Height / 2.0) * Mathf.Cos(azimuth)).First();
        var previous = SiegeMotion.Enemy(siege, goblin);
        int revision = siege.Revision, actors = view.SiegeActorBuilds, geometry = view.GeometryBuilds;
        game.Paused = false;
        game.Update(0.04);
        game.Paused = true;
        view.Sync(session, host.Map.Size);
        var current = SiegeMotion.Enemy(siege, goblin);
        Require(siege.Revision == revision && current.Position != previous.Position &&
            view.SiegeActorBuilds > actors && view.GeometryBuilds == geometry,
            "Actors must move smoothly between fixed combat ticks without rebuilding the city.");
        var center = new Pos(session.CameraX + session.VisibleCellsX / 2, session.CameraY + session.VisibleCellsY / 2);
        int zoom = session.ZoomLevel;
        var oldCursor = session.Cursor;
        session.PlaceCursor(new((int)goblin.X, (int)goblin.Y));
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(new((int)goblin.X, (int)goblin.Y));
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".walk-a.png"));
        game.Paused = false;
        game.Update(0.04);
        game.Paused = true;
        await Frames(host);
        Require(SiegeMotion.Enemy(siege, goblin).Stride != current.Stride, "Walking poses must alternate leg/arm steps.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".walk-b.png"));
        session.SetZoom(zoom);
        host.Map.RefreshCells();
        session.PlaceCursor(oldCursor);
        session.CenterOn(center);
        await Frames(host);
        GD.Print("TERMCITY_SIEGE_WALK_OK interpolation=sub-tick gait=animated facing=world-space");
    }

    internal static async Task VerifyRepairCrew(Main host, string? capturePath, SiegeEnemy visibilityAttacker)
    {
        var game = host.Session.Game;
        var speed = game.Speed;
        game.Speed = GameSpeed.Medium;
        var view = host.Map.Perspective!;
        var siege = game.Settlement!;
        void SetFixture(object value, string property, object data) =>
            value.GetType().GetProperty(property)!.SetValue(value, data);
        Require(host.Session.DebugRepairBuildings().Success, host.Session.Message);
        var thresholdWall = siege.WallSections.First(w => !w.IsGate && !w.IsTower);
        var visibilityEnemies = (System.Collections.IList)typeof(SettlementState).GetField("EnemyList",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(siege)!;
        visibilityEnemies.Add(visibilityAttacker);
        SetFixture(thresholdWall, nameof(WallComponent.Health), thresholdWall.MaxHealth * .2);
        double combatHealth = thresholdWall.Health;
        game.Speed = GameSpeed.Medium;
        game.Paused = false;
        game.Update(.1);
        game.Paused = true;
        await Frames(host);
        Require(thresholdWall.Health > combatHealth && !view.RepairCrewDrawn &&
            !view.FirefightingCrewDrawn && view.RepairWaterSegmentsDrawn == 0 && !view.UpgradeCrewDrawn,
            "Civilian work visuals must remain hidden while any attacker remains, without pausing actual repairs.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".repairs-hidden-combat.png"));
        Require(host.Session.DebugClearGoblins().Success, host.Session.Message);
        await Frames(host);
        Require(view.RepairCrewDrawn, "Repair crews must become visible after the last enemy is removed.");
        Require(host.Session.DebugRepairBuildings().Success, host.Session.Message);
        SetFixture(thresholdWall, nameof(WallComponent.Health), thresholdWall.MaxHealth * .95);
        game.Paused = false;
        game.Update(0.1);
        game.Paused = true;
        await Frames(host);
        Require(siege.RepairingWall == thresholdWall.Cell && !view.RepairCrewDrawn &&
            WallDamageLayout.DamageBand(thresholdWall) == 0,
            $"Minor hits must repair invisibly: target={siege.RepairingWall}, expected={thresholdWall.Cell}, " +
            $"health={thresholdWall.Health / thresholdWall.MaxHealth:P2}, visible={view.RepairCrewDrawn}.");
        foreach (var (fraction, damage, crewVisible) in new[]
            { (.85, true, false), (.8, true, true), (.99, false, true), (1d, false, false), (.8, true, true) })
        {
            SetFixture(thresholdWall, nameof(WallComponent.Health), thresholdWall.MaxHealth * fraction);
            SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
            await Frames(host);
            Require((WallDamageLayout.DamageBand(thresholdWall) > 0) == damage &&
                view.RepairCrewDrawn == crewVisible,
                $"Wall visuals must honor the 85/80/100 percent thresholds at {fraction:P0}.");
            if (fraction == .85 || fraction == .99)
                await Capture(host, capturePath is null ? null :
                    Path.ChangeExtension(capturePath, $".repair-threshold-{fraction * 100:0}.png"));
        }
        GD.Print("TERMCITY_REPAIR_THRESHOLDS_OK damage=85% crew-start=80% crew-stop=100% mechanics=unchanged");
        GD.Print("TERMCITY_REPAIR_COMBAT_VISIBILITY_OK enemies=hidden post-battle=visible health-recovery=unchanged");
        var crew = siege.VisibleRepairingWall!.Value;
        int zoom = host.Session.ZoomLevel;
        var oldCursor = host.Session.Cursor;
        var center = new Pos(host.Session.CameraX + host.Session.VisibleCellsX / 2,
            host.Session.CameraY + host.Session.VisibleCellsY / 2);
        host.Session.PlaceCursor(crew);
        host.Session.SetZoom(1);
        host.Map.RefreshCells();
        host.Session.CenterOn(crew);
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".repairs-a.png"));
        int geometry = view.GeometryBuilds, actorBuilds = view.SiegeActorBuilds;
        game.Paused = false;
        game.Update(0.06);
        game.Paused = true;
        await Frames(host);
        Require(view.SiegeActorBuilds > actorBuilds && view.GeometryBuilds == geometry,
            "Repair animation must update without rebuilding terrain or the city.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".repairs-b.png"));
        var wall = siege.Walls.Single(w => w.Cell == crew);
        double health = wall.Health;
        SetFixture(wall, nameof(WallComponent.Health), wall.MaxHealth * 0.2);
        typeof(SettlementState).GetField("RepairPhase",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(siege, 0.5d);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        await Frames(host);
        Require(view.FirefightingCrewDrawn && view.RepairWaterSegmentsDrawn == 12,
            "The tipped bucket must throw a connected twelve-segment water sheet, not dotted particles.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".buckets-a.png"));
        game.Paused = false;
        game.Update(0.2);
        game.Paused = true;
        await Frames(host);
        Require(view.FirefightingCrewDrawn && view.GeometryBuilds == geometry,
            "Bucket pouring and water must animate without rebuilding the city.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".buckets-b.png"));
        SetFixture(wall, nameof(WallComponent.Health), wall.MaxHealth * WallDamageLayout.FireThreshold);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        await Frames(host);
        Require(view.RepairCrewDrawn && !view.FirefightingCrewDrawn,
            "After the fire is out, the same crew must switch to structural repair tools.");
        SetFixture(wall, nameof(WallComponent.Health), health);
        SetFixture(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        host.Session.SetZoom(zoom);
        host.Map.RefreshCells();
        host.Session.PlaceCursor(oldCursor);
        host.Session.CenterOn(center);
        await Frames(host);
        game.Speed = speed;
        GD.Print("TERMCITY_REPAIR_CREW_OK visible=true animated=true buckets-before-hammers=true city-rebuilds=0");
    }

    internal static async Task VerifyActiveSwarmBudget(Main host)
    {
        if (DisplayServer.GetName() != "headless")
        {
            host.GetWindow().GrabFocus();
            await Frames(host);
        }
        var game = host.Session.Game;
        var view = host.Map.Perspective!;
        int geometry = view.GeometryBuilds, actorBuilds = view.SiegeActorBuilds;
        double worstActor = 0;
        double worstSimulation = 0;
        var frames = new List<double>();
        for (int n = 0; n < 24; n++)
        {
            var watch = Stopwatch.StartNew();
            game.Paused = false;
            game.Update(0.03);
            game.Paused = true;
            worstSimulation = Math.Max(worstSimulation, watch.Elapsed.TotalMilliseconds);
            VerifyCrowdSpacing(game.Settlement!);
            view.Sync(host.Session, host.Map.Size);
            worstActor = Math.Max(worstActor, view.SiegeActorBuildMilliseconds);
            host.Map.QueueRedraw();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (n >= 4) frames.Add(watch.Elapsed.TotalMilliseconds);
        }
        frames.Sort();
        double p90 = frames[(int)(frames.Count * 0.9)];
        Require(view.SiegeActorBuilds - actorBuilds >= 20 && view.GeometryBuilds == geometry,
            "Animated swarm frames must reuse city geometry and update more often than combat ticks.");
        Require(worstActor < 12, $"Swarm actor update took {worstActor:0.0}ms; budget is12ms.");
        GD.Print($"TERMCITY_SIEGE_ANIMATION_PERFORMANCE goblins={game.Settlement!.Enemies.Count} actor-worst={worstActor:0.0}ms " +
            $"simulation-worst={worstSimulation:0.0}ms frame-p90={p90:0.0}ms city-rebuilds=0");
        if (DisplayServer.GetName() != "headless" && p90 >= 60)
            throw new PerformanceBudgetExceededException($"Active-swarm frame p90 was {p90:0.0}ms; budget is60ms; " +
                $"actor-worst={worstActor:0.0}ms, simulation-worst={worstSimulation:0.0}ms.");
    }

    private static void VerifyCrowdSpacing(SettlementState state)
    {
        var poses = state.Enemies.Select(goblin => SiegeMotion.Enemy(state, goblin)).ToArray();
        for (int first = 0; first < poses.Length; first++)
            for (int last = first + 1; last < poses.Length; last++)
                Require(System.Numerics.Vector2.Distance(poses[first].Position, poses[last].Position) + 0.0001 >=
                    state.Enemies[first].CollisionRadius + state.Enemies[last].CollisionRadius,
                    "Rendered goblin bodies must not overlap between combat ticks.");
    }

    internal static async Task VerifyWallAttack(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var state = game.Settlement!;
        SiegeEnemy? attacker = null;
        double worstSimulation = 0;
        for (int step = 0; step < 160 && attacker is null; step++)
        {
            int mapVersion = game.MapVersion, cityBuilds = host.Map.Perspective!.GeometryBuilds;
            var watch = Stopwatch.StartNew();
            game.Paused = false;
            game.Update(0.2);
            game.Paused = true;
            worstSimulation = Math.Max(worstSimulation, watch.Elapsed.TotalMilliseconds);
            if (watch.Elapsed.TotalMilliseconds > 100)
                GD.Print($"TERMCITY_SIEGE_SLOW_UPDATE step={step} elapsed={watch.Elapsed.TotalMilliseconds:0.0}ms " +
                    $"map-changed={game.MapVersion != mapVersion} city-rebuilds={host.Map.Perspective.GeometryBuilds - cityBuilds}");
            attacker = state.Enemies.FirstOrDefault(goblin => SiegeMotion.Enemy(state, goblin).Attack < -0.8f);
        }
        Require(attacker is not null, "A goblin reaching a wall must visibly wind up a melee attack.");
        var unit = attacker!;
        var view = host.Map.Perspective!;
        float azimuth = view.Azimuth;
        int zoom = session.ZoomLevel;
        var cursor = session.Cursor;
        var center = new Pos(session.CameraX + session.VisibleCellsX / 2, session.CameraY + session.VisibleCellsY / 2);
        float outside = Mathf.RadToDeg(MathF.Atan2((float)(unit.X - state.Bounds.X - state.Bounds.Width / 2.0),
            (float)(unit.Y - state.Bounds.Y - state.Bounds.Height / 2.0)));
        view.Rotate(outside + 45 - view.Azimuth);
        session.PlaceCursor(new((int)unit.X, (int)unit.Y));
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(session.Cursor);
        session.ClosePrompt();
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".attack-windup.png"));
        bool strike = false;
        for (int step = 0; step < 35 && !strike; step++)
        {
            game.Paused = false;
            game.Update(0.04);
            game.Paused = true;
            strike = state.Enemies.Contains(unit) && SiegeMotion.Enemy(state, unit).Attack > 0.8f;
        }
        Require(strike, "The same melee attacker must follow its wind-up with a visible forward strike.");
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".attack-strike.png"));
        view.Rotate(azimuth - view.Azimuth);
        session.SetZoom(zoom);
        host.Map.RefreshCells();
        session.PlaceCursor(cursor);
        session.CenterOn(center);
        session.ClosePrompt();
        await Frames(host);
        GD.Print($"TERMCITY_GOBLIN_CROWD_ATTACK_OK spacing=body-radius movement=swept queueing=true " +
            $"attack=windup-strike-recoil congested-simulation-worst={worstSimulation:0.0}ms");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
