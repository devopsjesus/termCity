using System.Reflection;
using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class GarrisonSmoke
{
    public static async Task Run(Main host, string? capture)
    {
        var session = host.Session;
        var originalGame = session.Game;
        var game = CityGame.New(session.Game.Config);
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.ClearCell(x, y);
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Base);
            }
        game.Touch();
        SettlementDebugSetup.Apply(game, siege: true);
        Require(game.DebugRemoveGoblins().Success, "The garrison fixture needs a peaceful starter.");
        Set(game, nameof(game.HighestPopulation), 250);
        Set(game, nameof(game.HighestMilestone), 250);
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [game, true]);
        var siege = game.Settlement!;
        var gate = siege.Walls.Single(w => w.IsGate).Cell;
        foreach (string name in new[] { "Archer Barracks", "Guard Post" })
        {
            Require(game.Map.BuildingFootprints.Values.Count(area =>
                game.Map.BuildingAt(area.X, area.Y)?.Name == name) == 1,
                $"The siege preset must already contain exactly one {name}.");
        }
        game.Speed = GameSpeed.Medium;
        game.Paused = false;
        for (int n = 0; n < 300; n++) game.Update(.1);
        game.Paused = true;
        session.ClosePrompt();
        Require(siege.Defenders.Count == 8, "Two funded garrisons must recruit their finite four-person rosters.");
        Require(siege.FirstOrcApproach is not null, "The first Orc attack must show its planned wall sector.");
        host.Map.SetPerspective(true);
        session.SetZoom(0);
        var view = host.Map.Perspective!;
        view.Rotate(270 - view.Azimuth);
        var center = new Pos((int)siege.Defenders.Average(d => d.X), (int)siege.Defenders.Average(d => d.Y));
        session.PlaceCursor(center);
        host.Map.RefreshCells();
        session.CenterOn(center);
        await Frames(host);
        Require(view.DefendersDrawn == 8, "Every archer and soldier must have a rendered model.");
        Require(view.AccessLadderWallIds.SetEquals(siege.Walls.Where(WallDamageLayout.HasAccessLadder).Select(w => w.Id)) &&
            siege.Walls.Where(w => w.IsGate || w.IsGateWing).All(w => !view.AccessLadderWallIds.Contains(w.Id)),
            "Cached ladder geometry must belong only to intact towers, never gates or gate wings.");
        Require(siege.Walls.Where(WallDamageLayout.HasAccessLadder).All(tower =>
            siege.WallNetwork.ContainsAny(WallDamageLayout.LadderClimbPosition(tower))),
            "Ground-access ladder landings must stay inside the structural enclosure.");
        session.CenterOn(gate);
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".gate-no-ladder.png"));
        session.CenterOn(center);
        await Frames(host);
        int geometry = view.GeometryBuilds;
        var picked = new List<int>();
        for (int turn = 0; turn < 4 && picked.Count < 2; turn++)
            foreach (var unit in siege.Defenders.Where(d => !picked.Contains(d.Id)))
            {
                view.Rotate(90);
                session.CenterOn(new((int)unit.X, (int)unit.Y));
                await Frames(host);
                geometry = view.GeometryBuilds;
                var point = view.ScreenPoint(view.DefenderPosition(unit, siege.InterpolationFraction) +
                    Vector3.Up * (WorldScale.HumanHeight * 0.9f));
                if (!host.Map.TryPickSiegeActor(point, out bool friendly, out int id) || !friendly || id != unit.Id) continue;
                Click(host, point, MouseButton.Left, picked.Count > 0);
                view.Sync(session, host.Map.Size);
                Require(view.GeometryBuilds == geometry, "A troop click must not rebuild city geometry.");
                picked.Add(unit.Id);
                if (picked.Count == 2) break;
            }
        Require(picked.Count == 2 && session.SelectedDefenderIds.Count == 2,
            "Real clicks and Shift-clicks must select two frontmost friendly models.");
        view.Sync(session, host.Map.Size);
        Require(view.GeometryBuilds == geometry, "Troop selection must not rebuild city geometry.");
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".garrison-defenders.png"));
        var archer = siege.Defenders.Where(d => d.Kind == DefenderKind.Archer && d.Elevation > 1)
            .OrderByDescending(d => d.Elevation).FirstOrDefault();
        Require(archer is not null, "At least one archer must have reached a tower or walkway.");
        var azimuth = view.Azimuth;
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(new((int)archer!.X, (int)archer.Y));
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".garrison-walkway.png"));
        view.Rotate(90);
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".garrison-walkway-rotated.png"));
        view.Rotate(azimuth - view.Azimuth);
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(center);
        await Frames(host);
        var target = siege.Bounds.Cells().Where(p => game.CanBuildOn(p.X, p.Y) &&
                Math.Min(Math.Min(p.X - siege.Bounds.X, siege.Bounds.Right - p.X),
                    Math.Min(p.Y - siege.Bounds.Y, siege.Bounds.Bottom - p.Y)) >= 8 &&
                siege.Defenders.Where(d => picked.Contains(d.Id)).All(d => siege.DefenderCanReach(game, d, p)))
            .OrderBy(p => Math.Abs(p.X - center.X) + Math.Abs(p.Y - center.Y))
            .First(p =>
            {
                var point = view.ScreenPoint(new(p.X + .5f,
                    TermCity.Core.Rendering.TerrainSprite.Elevation(game.Map, p.X, p.Y), p.Y + .5f));
                return new Rect2(Vector2.Zero, host.Map.Size).HasPoint(point) &&
                    !host.Map.TryPickBuilding(point, out _);
            });
        Click(host, view.ScreenPoint(new(target.X + .5f,
            TermCity.Core.Rendering.TerrainSprite.Elevation(game.Map, target.X, target.Y), target.Y + .5f)), MouseButton.Right);
        Require(siege.Defenders.Where(d => picked.Contains(d.Id)).All(d => d.Order == DefenderOrder.Move),
            $"A real right-click must issue a temporary movement order: target={target}, message={session.Message}, " +
            $"prompt={session.Prompt?.Title}, picked={string.Join(",", picked)}, selected={string.Join(",", session.SelectedDefenderIds)}; " +
            string.Join("; ", siege.Defenders.Where(d => picked.Contains(d.Id))
                .Select(d => $"{d.Id}:{d.Kind}@{d.X:F1},{d.Y:F1} height={d.Elevation:F1} order={d.Order}")));
        session.ClearDefenderSelection();
        Set(siege, nameof(siege.NextWaveSeconds), 0d);
        game.Paused = false;
        game.Update(.1);
        game.Paused = true;
        Require(siege.Enemies.Any(g => g.Kind == SiegeEnemyKind.Orc) && siege.FirstOrcApproach is null,
            "The warned wave must contain Orcs and consume the one-time forecast.");
        var orc = siege.Enemies.First(g => g.Kind == SiegeEnemyKind.Orc);
        session.PlaceCursor(new((int)orc.X, (int)orc.Y));
        host.Map.RefreshCells();
        session.CenterOn(session.Cursor);
        view.Rotate(90);
        await Frames(host);
        bool orcPicked = false;
        foreach (float rotation in new[] { 0f, 90f, 90f, 90f })
        {
            view.Rotate(rotation);
            await Frames(host);
            var point = view.ScreenPoint(new((float)orc.X,
                TermCity.Core.Rendering.TerrainSprite.Elevation(game.Map, (int)orc.X, (int)orc.Y) +
                (float)orc.BodyScale * .65f, (float)orc.Y));
            if (!host.Map.TryPickSiegeActor(point, out bool friendly, out int id) || friendly || id != orc.Id) continue;
            orcPicked = true;
            session.SelectDefender(picked[0]);
            Click(host, point, MouseButton.Right);
            Require(siege.Defenders.Single(d => d.Id == picked[0]).Order == DefenderOrder.Attack,
                "Right-clicking a visible Orc must command the selected defender to attack it.");
            break;
        }
        Require(orcPicked, "The larger Orc model must be directly pickable in a camera orientation.");
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".first-orc.png"));
        for (int n = 0; n < 40; n++)
        {
            game.Paused = false; game.Update(.1); game.Paused = true;
            view.Sync(session, host.Map.Size);
            Require(view.SiegeActorBuildMilliseconds < 12, "Mixed enemy and defender actor updates must remain within 12ms.");
        }
        Require(game.DebugRemoveGoblins().Success, "The mixed-swarm performance fixture must clear prior attackers.");
        Set(siege, nameof(siege.WaveNumber), 99);
        Set(siege, nameof(siege.NextWaveSeconds), 0d);
        game.Paused = false;
        for (int n = 0; n < 3 && siege.Enemies.Count == 0; n++) game.Update(.1);
        game.Paused = true;
        Require(siege.Enemies.Count == SettlementState.MaximumEnemySwarm &&
            siege.Enemies.Any(g => g.Kind == SiegeEnemyKind.Orc),
            $"The performance fixture must contain a maximum-size mixed Orc/goblin swarm and the defenders: " +
            $"wave={siege.WaveNumber}, count={siege.Enemies.Count}, Orcs={siege.Enemies.Count(g => g.Kind == SiegeEnemyKind.Orc)}.");
        typeof(CityGame).GetField("_dayProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, 0d);
        view.Sync(session, host.Map.Size);
        await Frames(host);
        await SiegeAnimationSmoke.VerifyActiveSwarmBudget(host);
        host.Map.SetPerspective(false);
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".garrison-overhead.png"));
        await VerifyLadderTravel(host, game, capture);
        await VerifyAutomaticArcherResponse(host, game, capture);
        await VerifyArcherDistribution(host, game, capture);
        await ArcherBowSmoke.Run(host, game, capture);
        session.ClearDefenderSelection();
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [originalGame, true]);
        session.ClosePrompt();
        originalGame.Paused = true;
        host.Map.Invalidate(true);
        GD.Print("TERMCITY_GARRISON_OK defenders=8 selection=click+shift commands=move+attack orcs=distinct first-warning=once city-rebuilds=0");
    }

    private static async Task VerifyAutomaticArcherResponse(Main host, CityGame game, string? capture)
    {
        Require(game.DebugStartSiegeWave(1).Success, "Archer response needs one real siege attacker.");
        var siege = game.Settlement!;
        var enemy = siege.Enemies.Single();
        var unit = siege.Defenders.Single();
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom &&
            w.Cell.X > siege.Bounds.X + 4);
        var target = siege.Walls.Single(w => w.Cell == new Pos(siege.Bounds.Right, siege.Bounds.Y + 12));
        Require(!target.IsTower, "The response target must attack a wall sector without a tower.");
        Set(enemy, nameof(enemy.X), (double)target.Center.X + 1.1);
        Set(enemy, nameof(enemy.Y), (double)target.Center.Y);
        Set(enemy, nameof(enemy.Health), 96d);
        Set(enemy, nameof(enemy.MaxHealth), 96d);
        typeof(SiegeEnemy).GetField("Route", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(enemy, new List<Pos> { target.Cell });
        typeof(SiegeEnemy).GetField("RouteIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(enemy, 0);
        typeof(SiegeEnemy).GetField("MotionInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(enemy, false);
        Set(unit, nameof(unit.X), (double)tower.Center.X);
        Set(unit, nameof(unit.Y), (double)tower.Center.Y);
        Set(unit, nameof(unit.Elevation), WallDamageLayout.ActorWalkHeight(tower, game.Map));
        Set(unit, nameof(unit.Order), DefenderOrder.Automatic);
        double initial = Math.Sqrt(Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2));
        var advance = typeof(SettlementState).GetMethod("AdvanceDefenders", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var attacks = typeof(SettlementState).GetMethod("AdvanceCommittedAttacks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        host.Session.SelectDefender(unit.Id);
        bool captured = false;
        for (int step = 0; step < 1400 && enemy.Health > 0; step++)
        {
            game.Paused = false;
            advance.Invoke(siege, [game, .1]);
            attacks.Invoke(siege, [game, .1]);
            game.Paused = true;
            Set(siege, nameof(siege.Revision), siege.Revision + 1);
            if (captured || enemy.Health >= 96) continue;
            host.Session.CenterOn(new((int)unit.X, (int)unit.Y));
            host.Map.RefreshCells();
            await Frames(host);
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".archer-response.png"));
            captured = true;
        }
        Require(captured && enemy.Health <= 0 &&
            Math.Sqrt(Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2)) < initial - 2,
            "The automatic archer must leave its distant patrol, approach a usable post and kill the towerless-sector attacker.");
        GD.Print("TERMCITY_ARCHER_RESPONSE_OK threat=retained blocked-shots=reposition patrol=interrupted " +
            "movement=physical impact=arrow range=unchanged");
    }

    private static async Task VerifyArcherDistribution(Main host, CityGame game, string? capture)
    {
        Require(game.DebugStartSiegeWave(4).Success, "Archer distribution needs four real attackers.");
        var siege = game.Settlement!;
        var first = siege.Defenders.Single();
        var roster = (System.Collections.IList)typeof(SettlementState).GetField("DefenderList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
        var nextId = typeof(SettlementState).GetField("NextDefenderId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 1; i < 4; i++)
        {
            var unit = new SettlementDefender();
            int id = (int)nextId.GetValue(siege)!;
            Set(unit, nameof(unit.Id), id);
            nextId.SetValue(siege, id + 1);
            Set(unit, nameof(unit.Home), first.Home);
            Set(unit, nameof(unit.Kind), DefenderKind.Archer);
            roster.Add(unit);
        }
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.X == siege.Bounds.Right &&
            w.Cell.Y > siege.Bounds.Y + 4 && w.Cell.Y < siege.Bounds.Bottom - 4);
        for (int i = 0; i < 4; i++)
        {
            var unit = siege.Defenders[i];
            Set(unit, nameof(unit.X), (double)tower.Center.X + (i % 2 == 0 ? -.5 : .5));
            Set(unit, nameof(unit.Y), (double)tower.Center.Y + (i < 2 ? -.5 : .5));
            Set(unit, nameof(unit.Elevation), WallDamageLayout.ActorWalkHeight(tower, game.Map));
            Set(unit, nameof(unit.Order), DefenderOrder.Automatic);
            Set(unit, nameof(unit.AttackCooldown), 0d);
            var enemy = siege.Enemies[i];
            int y = tower.Cell.Y - 5 + i * 2;
            Set(enemy, nameof(enemy.X), siege.Bounds.Right + 1.6);
            Set(enemy, nameof(enemy.Y), y + .5);
            Set(enemy, nameof(enemy.Health), 96d);
            Set(enemy, nameof(enemy.MaxHealth), 96d);
            typeof(SiegeEnemy).GetField("Route", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(enemy, new List<Pos> { new(siege.Bounds.Right + 1, y), new(siege.Bounds.Right, y) });
            typeof(SiegeEnemy).GetField("RouteIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(enemy, 0);
            typeof(SiegeEnemy).GetField("MotionInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(enemy, false);
        }
        var advance = typeof(SettlementState).GetMethod("AdvanceDefenders", BindingFlags.Instance | BindingFlags.NonPublic)!;
        game.Paused = false;
        advance.Invoke(siege, [game, .1]);
        game.Paused = true;
        Set(siege, nameof(siege.Revision), siege.Revision + 1);
        Require(siege.Defenders.Select(unit => unit.TargetEnemyId).Distinct().Count() == 4,
            "Four automatic archers must split across four available enemies rather than focus-fire together.");
        host.Session.ClearDefenderSelection();
        host.Session.CenterOn(tower.Cell);
        host.Map.RefreshCells();
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".archers-distributed.png"));
        var enemies = (System.Collections.IList)typeof(SettlementState).GetField("EnemyList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
        var survivor = siege.Enemies[0];
        enemies.Clear();
        enemies.Add(survivor);
        game.Paused = false;
        advance.Invoke(siege, [game, .1]);
        game.Paused = true;
        Set(siege, nameof(siege.Revision), siege.Revision + 1);
        Require(siege.Defenders.All(unit => unit.TargetEnemyId == survivor.Id),
            "Automatic archers must converge when only one enemy remains.");
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".archers-converged.png"));
        GD.Print("TERMCITY_ARCHER_DISTRIBUTION_OK archers=4 targets=4 survivor=converged");
    }

    private static async Task VerifyLadderTravel(Main host, CityGame game, string? capture)
    {
        Require(game.DebugRemoveGoblins().Success, "Ladder validation needs a peaceful scene.");
        game.SetFunding(ServiceKind.Defence, 0);
        var siege = game.Settlement!;
        var unit = siege.Defenders.First(d => d.Kind == DefenderKind.Archer);
        var roster = (System.Collections.IList)typeof(SettlementState).GetField("DefenderList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
        roster.Clear();
        roster.Add(unit);
        var tower = siege.Walls.First(w => w.IsTower && w.IsOriginal && w.Cell.X == siege.Bounds.Right &&
            w.Cell.Y > siege.Bounds.Y + 4 && w.Cell.Y < siege.Bounds.Bottom - 4);
        var inward = WallDamageLayout.LadderInward(tower);
        var point = tower.Center + inward * 3;
        var ground = new Pos((int)point.X, (int)point.Y);
        Set(unit, nameof(unit.X), (double)tower.Center.X);
        Set(unit, nameof(unit.Y), (double)tower.Center.Y);
        Set(unit, nameof(unit.Elevation), WallDamageLayout.ActorWalkHeight(tower, game.Map));
        Set(unit, nameof(unit.Health), unit.MaxHealth);
        host.Session.ClearDefenderSelection();
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        var advance = typeof(SettlementState).GetMethod("AdvanceDefenders", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (bool down in new[] { true, false })
        {
            Require(siege.CommandDefenders(game, [unit.Id], down ? ground : tower.Cell, null).Success,
                "The real ladder route must accept both descent and ascent.");
            bool checkedClimb = false;
            float top = TerrainSprite.Elevation(game.Map, tower.Cell.X, tower.Cell.Y) +
                WallDamageLayout.ActorWalkHeight(tower, game.Map);
            float bottom = TerrainSprite.Elevation(game.Map, ground.X, ground.Y);
            for (int step = 0; step < 200 && unit.Order == DefenderOrder.Move; step++)
            {
                float before = TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y) + unit.Elevation;
                game.Paused = false;
                advance.Invoke(siege, [game, .1]);
                game.Paused = true;
                Set(siege, nameof(siege.Revision), siege.Revision + 1);
                float after = TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y) + unit.Elevation;
                if (Math.Abs(after - before) < .001f || after <= bottom + .1f || after >= top - .1f) continue;
                Require(System.Numerics.Vector2.Distance(new((float)unit.X, (float)unit.Y),
                    WallDamageLayout.LadderClimbPosition(tower)) < .01f,
                    "Vertical travel must stay on the exposed side of the rungs, not inside the tower.");
                if (checkedClimb || Math.Abs(after - (top + bottom) / 2) > .25f) continue;
                view.Sync(host.Session, host.Map.Size);
                await CaptureClimber(host, tower, unit, capture, down ? "descending" : "ascending");
                checkedClimb = true;
            }
            Require(checkedClimb && unit.Order == DefenderOrder.Automatic,
                $"The { (down ? "descending" : "ascending") } civilian must finish the ladder route.");
        }
        GD.Print("TERMCITY_LADDER_TRAVEL_OK ascent=exposed descent=exposed " +
            (capture is null ? "capture=not-requested" : "picking=body rotations=2 zooms=2"));
        var connection = WallDamageLayout.WalkwayLadders(tower).First();
        var lower = tower.Center + connection.Direction * 3;
        var lowerCell = new Pos((int)lower.X, (int)lower.Y);
        Require(siege.CommandDefenders(game, [unit.Id], lowerCell, null).Success,
            "Tower occupants must reach connected lower walkways through their side ladders.");
        bool checkedSide = false;
        for (int step = 0; step < 200 && unit.Order == DefenderOrder.Move; step++)
        {
            float before = unit.Elevation;
            game.Paused = false;
            advance.Invoke(siege, [game, .1]);
            game.Paused = true;
            Set(siege, nameof(siege.Revision), siege.Revision + 1);
            if (checkedSide || Math.Abs(before - unit.Elevation) < .001f) continue;
            view.Sync(host.Session, host.Map.Size);
            await CaptureClimber(host, tower, unit, capture, "walkway-side");
            checkedSide = true;
        }
        Require(checkedSide && unit.Order == DefenderOrder.Automatic,
            "The side ladder must complete the tower-to-walkway descent.");
    }

    private static async Task CaptureClimber(Main host, WallComponent tower, SettlementDefender unit,
        string? capture, string phase)
    {
        if (capture is null) return;
        var view = host.Map.Perspective!;
        var camera = (Camera3D)typeof(PerspectiveCity).GetField("_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var transform = camera.Transform;
        float span = camera.Size;
        var at = view.DefenderPosition(unit, host.Session.Game.Settlement!.InterpolationFraction);
        var candidates = WallDamageLayout.WalkwayLadders(tower).Select(ladder =>
            (Position: ladder.Position, Direction: ladder.Direction)).Append(
                (Position: WallDamageLayout.LadderPosition(tower), Direction: WallDamageLayout.LadderInward(tower)));
        var closest = candidates.MinBy(ladder => System.Numerics.Vector2.DistanceSquared(
            ladder.Position + ladder.Direction * WallDamageLayout.LadderClimberOffset, new(at.X, at.Z)));
        var inward = closest.Direction;
        var ladder = closest.Position;
        var target = new Vector3(ladder.X, at.Y + WorldScale.HumanHeight / 2, ladder.Y);
        for (int turn = 0; turn < 2; turn++)
        {
            float side = turn == 0 ? -.7f : .7f;
            camera.Position = target + new Vector3(inward.X + tower.Tangent.X * side, .55f,
                inward.Y + tower.Tangent.Y * side).Normalized() * 100;
            camera.LookAt(target, Vector3.Up);
            camera.Size = turn == 0 ? 6 : 8;
            camera.MakeCurrent();
            camera.ForceUpdateTransform();
            view.RequestRender();
            await Frames(host);
            view.RequestRender();
            var rendered = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            RenderingServer.ForceDraw(false);
            await rendered;
            var chest = view.ScreenPoint(at + Vector3.Up * WorldScale.HumanHeight * .6f);
            Require(view.PickSiegeActor(chest, out bool friendly, out int id) && friendly && id == unit.Id,
                "The visible climbing civilian must be picked in front of the ladder and wall.");
            using var image = view.CaptureDisplayImage();
            var error = image.SavePng(Path.ChangeExtension(capture, $".ladder-{phase}-{turn}.png"));
            if (error != Error.Ok) throw new IOException($"Ladder capture failed: {error}");
        }
        camera.Transform = transform;
        camera.Size = span;
        camera.ForceUpdateTransform();
        view.RequestRender();
    }

    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name)!.SetValue(value, data);
    private static void Click(Main host, Vector2 point, MouseButton button, bool shift = false)
    {
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = point, ButtonIndex = button, Pressed = true, ShiftPressed = shift });
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = point, ButtonIndex = button, Pressed = false, ShiftPressed = shift });
    }
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
