using System.Diagnostics;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class SiegeAnimationSmoke
{
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
        Require(session.DebugStartWave(SettlementState.MaximumGoblinSwarm).Success, session.Message);
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
        var goblin = siege.Goblins.OrderByDescending(unit =>
            (unit.X - siege.Bounds.X - siege.Bounds.Width / 2.0) * Mathf.Sin(azimuth) +
            (unit.Y - siege.Bounds.Y - siege.Bounds.Height / 2.0) * Mathf.Cos(azimuth)).First();
        var previous = SiegeMotion.Goblin(siege, goblin);
        int revision = siege.Revision, actors = view.SiegeActorBuilds, geometry = view.GeometryBuilds;
        game.Paused = false;
        game.Update(0.04);
        game.Paused = true;
        view.Sync(session, host.Map.Size);
        var current = SiegeMotion.Goblin(siege, goblin);
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
        Require(SiegeMotion.Goblin(siege, goblin).Stride != current.Stride, "Walking poses must alternate leg/arm steps.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".walk-b.png"));
        session.SetZoom(zoom);
        host.Map.RefreshCells();
        session.PlaceCursor(oldCursor);
        session.CenterOn(center);
        await Frames(host);
        GD.Print("TERMCITY_SIEGE_WALK_OK interpolation=sub-tick gait=animated facing=world-space");
    }

    internal static async Task VerifyRepairCrew(Main host, string? capturePath)
    {
        var game = host.Session.Game;
        game.Paused = false;
        game.Update(0.4);
        game.Paused = true;
        await Frames(host);
        var view = host.Map.Perspective!;
        var siege = game.Settlement!;
        Require(siege.RepairingWall is not null && view.RepairCrewDrawn,
            "Damaged walls must show working civilian crews after the wave ends.");
        var crew = siege.RepairingWall!.Value;
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
        void SetFixture(object value, string property, object data) =>
            value.GetType().GetProperty(property)!.SetValue(value, data);
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
        GD.Print("TERMCITY_REPAIR_CREW_OK visible=true animated=true buckets-before-hammers=true city-rebuilds=0");
    }

    internal static async Task VerifyActiveSwarmBudget(Main host)
    {
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
        if (DisplayServer.GetName() != "headless")
            Require(p90 < 60, $"Active-swarm frame p90 was {p90:0.0}ms; budget is60ms.");
        GD.Print($"TERMCITY_SIEGE_ANIMATION_PERFORMANCE goblins={game.Settlement!.Goblins.Count} actor-worst={worstActor:0.0}ms " +
            $"simulation-worst={worstSimulation:0.0}ms frame-p90={p90:0.0}ms city-rebuilds=0");
    }

    private static void VerifyCrowdSpacing(SettlementState state)
    {
        var poses = state.Goblins.Select(goblin => SiegeMotion.Goblin(state, goblin)).ToArray();
        for (int first = 0; first < poses.Length; first++)
            for (int last = first + 1; last < poses.Length; last++)
                Require(System.Numerics.Vector2.Distance(poses[first].Position, poses[last].Position) + 0.0001 >=
                    state.Goblins[first].CollisionRadius + state.Goblins[last].CollisionRadius,
                    "Rendered goblin bodies must not overlap between combat ticks.");
    }

    internal static async Task VerifyWallAttack(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var state = game.Settlement!;
        Goblin? attacker = null;
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
            attacker = state.Goblins.FirstOrDefault(goblin => SiegeMotion.Goblin(state, goblin).Attack < -0.8f);
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
            strike = state.Goblins.Contains(unit) && SiegeMotion.Goblin(state, unit).Attack > 0.8f;
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
