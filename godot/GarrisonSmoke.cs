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
    private static async Task VerifyContinuousMovement(Main host, SettlementState siege)
    {
        var game = host.Session.Game;
        var view = host.Map.Perspective!;
        var unit = siege.Defenders.First(defender => defender.Kind == DefenderKind.Soldier);
        var previous = (unit.PreviousX, unit.PreviousY, unit.PreviousElevation);
        var accumulator = typeof(SettlementState).GetField("Accumulator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var savedAccumulator = accumulator.GetValue(siege);
        var speed = game.Speed;
        bool paused = game.Paused;
        async Task WaitForDraw()
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        host.SetProcess(false);
        try
        {
            Require(siege.Enemies.Count == 0 && siege.Projectiles.Count == 0 && siege.VisibleRepairingWall is null,
                "Continuous friendly movement must be tested without attackers, arrows or repair crews waking the map.");
            Set(unit, nameof(unit.PreviousX), unit.X - .14);
            Set(unit, nameof(unit.PreviousY), unit.Y);
            Set(unit, nameof(unit.PreviousElevation), unit.Elevation);
            game.Paused = false;
            host.Map.AdvanceAnimation(0, true);
            await WaitForDraw();
            int geometry = view.GeometryBuilds;
            foreach (var testSpeed in Enum.GetValues<GameSpeed>())
            {
                game.Speed = testSpeed;
                host.Session.CombatPresentation.Reset();
                host.Session.AdvancePresentation(0, true);
                double interval = .1 * game.Config.SecondsPerWeek(testSpeed) / game.Config.MediumSecondsPerWeek;
                double previousFraction = 0;
                Vector3? last = null;
                foreach (double fraction in new[] { .1, .3, .5, .7, .9 })
                {
                    int draws = host.Map.DrawCount, builds = view.SiegeActorBuilds;
                    host.Map.AdvanceAnimation((fraction - previousFraction) * interval, true);
                    previousFraction = fraction;
                    await WaitForDraw();
                    Require(host.Map.DrawCount > draws && view.SiegeActorBuilds > builds,
                        $"Friendly actors must redraw between combat ticks at {testSpeed} speed.");
                    var rendered = view.RenderedDefenderPosition(unit.Id);
                    Require(rendered is not null, "The moving soldier must have an actual rendered body transform.");
                    if (last is { } before)
                        Require(Math.Abs(before.DistanceTo(rendered!.Value) - .028f) < .001f,
                            $"Rendered civilian movement must maintain an even cadence at {testSpeed} speed.");
                    last = rendered;
                }
            }
            Require(view.GeometryBuilds == geometry, "Continuous actor movement must not rebuild the city.");
            game.Paused = true;
            int pausedBuilds = view.SiegeActorBuilds;
            host.Map.AdvanceAnimation(0, true);
            await WaitForDraw();
            Require(view.SiegeActorBuilds == pausedBuilds, "Paused friendly movement must remain frozen.");
        }
        finally
        {
            Set(unit, nameof(unit.PreviousX), previous.PreviousX);
            Set(unit, nameof(unit.PreviousY), previous.PreviousY);
            Set(unit, nameof(unit.PreviousElevation), previous.PreviousElevation);
            accumulator.SetValue(siege, savedAccumulator);
            game.Speed = speed;
            game.Paused = paused;
            host.Session.CombatPresentation.Reset();
            host.SetProcess(true);
            host.Map.Invalidate(false);
            await Frames(host);
        }
        GD.Print("TERMCITY_FRIENDLY_CADENCE_OK speeds=slow+medium+fast subtick=rendered pause=frozen city=cached");
    }

    public static async Task Run(Main host, string? capture, bool movementOnly = false)
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
        await VerifyTowerPatrols(host, game, capture);
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
        var countdown = Descendants(host).OfType<Label>().Single(label => label.Name == "NextWaveCountdown");
        Require(countdown.Text.Length > 0 && countdown.Size.Y >= countdown.GetThemeFont("font").GetHeight(22) &&
            countdown.GetThemeFontSize("font_size") == 22,
            "The next-wave countdown must retain readable 22-point text and a visible full-height row.");
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
            $"Real clicks and Shift-clicks must select two frontmost friendly models. " +
            $"picked={string.Join(",", picked)} selected={string.Join(",", session.SelectedDefenderIds)} " +
            $"message={session.Message} prompt={session.Prompt?.Title}");
        view.Sync(session, host.Map.Size);
        Require(view.GeometryBuilds == geometry, "Troop selection must not rebuild city geometry.");
        await VerifyContinuousMovement(host, siege);
        await VerifyIdleArcherFacing(host, siege, capture);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".garrison-defenders.png"));
        if (movementOnly)
        {
            await VerifyTroopInput(host, game, capture);
            session.ClearDefenderSelection();
            typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, [originalGame, true]);
            session.ClosePrompt();
            originalGame.Paused = true;
            host.Map.Invalidate(true);
            return;
        }
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

    private static async Task VerifyIdleArcherFacing(Main host, SettlementState siege, string? capture)
    {
        var session = host.Session;
        var view = host.Map.Perspective!;
        var unit = siege.Defenders.First(defender => defender.Kind == DefenderKind.Archer);
        var saved = (unit.X, unit.Y, unit.PreviousX, unit.PreviousY, unit.Elevation,
            unit.PreviousElevation, unit.AttackCooldown, unit.TargetEnemyId);
        double zoom = session.ZoomLevel;
        float azimuth = view.Azimuth;
        var center = new Pos(session.CameraX + session.VisibleCellsX / 2, session.CameraY + session.VisibleCellsY / 2);
        var posts = siege.Walls.Where(wall => wall.IsTower).Take(4).Concat(new[]
        {
            siege.Walls.First(wall => !wall.IsTower && !wall.IsGate && wall.Cell.Y == siege.Bounds.Y && wall.Cell.X == siege.Bounds.X + 10),
            siege.Walls.First(wall => !wall.IsTower && !wall.IsGate && wall.Cell.Y == siege.Bounds.Bottom && wall.Cell.X == siege.Bounds.X + 10),
            siege.Walls.First(wall => !wall.IsTower && !wall.IsGate && wall.Cell.X == siege.Bounds.X && wall.Cell.Y == siege.Bounds.Y + 10),
            siege.Walls.First(wall => !wall.IsTower && !wall.IsGate && wall.Cell.X == siege.Bounds.Right && wall.Cell.Y == siege.Bounds.Y + 10),
        }).ToArray();
        var enemies = (List<SiegeEnemy>)typeof(SettlementState).GetField("EnemyList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
        SiegeEnemy? target = null;
        void Revise()
        {
            Set(siege, nameof(siege.Revision), siege.Revision + 1);
            session.CombatPresentation.Reset();
            session.AdvancePresentation(0, false);
        }
        void CenterArcher()
        {
            view.Sync(session, host.Map.Size);
            var body = view.DefenderPosition(unit, siege.InterpolationFraction) + Vector3.Up;
            var bodyPlane = view.GroundPlanePoint(view.ScreenPoint(body));
            var centerPlane = view.GroundPlanePoint(host.Map.Size / 2);
            view.SetCameraOffset(session, view.CameraOffset +
                new Vector2(bodyPlane.X - centerPlane.X, bodyPlane.Z - centerPlane.Z));
            view.Sync(session, host.Map.Size);
            Require(new Rect2(Vector2.Zero, host.Map.Size).HasPoint(view.ScreenPoint(body)),
                "Archer facing checks must render a visible archer, not an off-screen model.");
        }
        host.SetProcess(false);
        try
        {
            Set(unit, nameof(unit.TargetEnemyId), null);
            Set(unit, nameof(unit.AttackCooldown), 0d);
            foreach (var post in posts)
            {
                var normal = new System.Numerics.Vector2(-post.Tangent.Y, post.Tangent.X);
                var point = post.Center + normal * WallDamageLayout.WalkwayOffset(post);
                Set(unit, nameof(unit.X), (double)point.X); Set(unit, nameof(unit.Y), (double)point.Y);
                Set(unit, nameof(unit.PreviousX), (double)point.X); Set(unit, nameof(unit.PreviousY), (double)point.Y);
                float height = WallDamageLayout.ActorWalkHeight(post, session.Game.Map);
                Set(unit, nameof(unit.Elevation), height); Set(unit, nameof(unit.PreviousElevation), height);
                var outward = post.IsTower ? -WallDamageLayout.LadderInward(post) :
                    normal * WallDamageLayout.ExteriorSide(post);
                foreach (int level in new[] { 0, 1 })
                {
                    session.SetZoom(level);
                    host.Map.RefreshCells();
                    session.CenterOn(post.Cell);
                    await Frames(host);
                    for (int turn = 0; turn < 4; turn++)
                    {
                        view.Rotate(90);
                        Revise();
                        CenterArcher();
                        var actual = view.RenderedDefenderForward(unit.Id);
                        Require(actual is { } facing && facing.Dot(new(outward.X, 0, outward.Y)) > .999f,
                            $"Idle archers must face outside their actual post at every heading/zoom: post={post.Cell}, " +
                            $"tower={post.IsTower}, zoom={level}, outward={outward}, actual={actual}.");
                        Require(view.SiegeActorBuildMilliseconds < 12,
                            $"Idle outward-facing actor updates took {view.SiegeActorBuildMilliseconds:0.0}ms; budget is 12ms.");
                    }
                }
            }
            session.SetZoom(0);
            host.Map.RefreshCells();
            session.CenterOn(posts[^1].Cell);
            CenterArcher();
            await Frames(host);
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".archer-outward.png"));
            target = new SiegeEnemy();
            Set(target, nameof(target.Id), int.MaxValue);
            Set(target, nameof(target.X), unit.X + 4);
            Set(target, nameof(target.Y), unit.Y + 3);
            Set(target, nameof(target.Health), 96d); Set(target, nameof(target.MaxHealth), 96d);
            typeof(SiegeEnemy).GetField("Route", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(target, new List<Pos> { posts[^1].Cell });
            enemies.Add(target);
            Set(unit, nameof(unit.TargetEnemyId), target.Id);
            Revise();
            view.Sync(session, host.Map.Size);
            var enemyDirection = new Vector3(4, 0, 3).Normalized();
            Require(view.RenderedDefenderForward(unit.Id) is { } aim && aim.Dot(enemyDirection) > .999f,
                "An active enemy must override the idle outward heading.");
            Set(target, nameof(target.Health), 0d);
            Revise();
            view.Sync(session, host.Map.Size);
            var idle = SiegeMotion.IdleDefenderForward(siege, unit);
            Require(view.RenderedDefenderForward(unit.Id) is { } restored && restored.Dot(new(idle.X, 0, idle.Y)) > .999f,
                "A dead target must stop overriding the archer's outward idle heading.");
            Set(target, nameof(target.Health), 96d);
            Set(unit, nameof(unit.PreviousX), unit.X - .1);
            Revise();
            view.Sync(session, host.Map.Size);
            Require(view.RenderedDefenderForward(unit.Id) is { } walking && walking.Dot(Vector3.Right) > .999f,
                "Walking archers must still face their movement rather than an idle heading or enemy.");
        }
        finally
        {
            if (target is not null) enemies.Remove(target);
            Set(unit, nameof(unit.X), saved.X); Set(unit, nameof(unit.Y), saved.Y);
            Set(unit, nameof(unit.PreviousX), saved.PreviousX); Set(unit, nameof(unit.PreviousY), saved.PreviousY);
            Set(unit, nameof(unit.Elevation), saved.Elevation); Set(unit, nameof(unit.PreviousElevation), saved.PreviousElevation);
            Set(unit, nameof(unit.AttackCooldown), saved.AttackCooldown); Set(unit, nameof(unit.TargetEnemyId), saved.TargetEnemyId);
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            view.Rotate(azimuth - view.Azimuth);
            session.CenterOn(center);
            Revise();
            host.SetProcess(true);
            await Frames(host);
        }
        GD.Print("TERMCITY_ARCHER_FACING_OK posts=walls+towers headings=4 zooms=2 idle=outward target=preserved walking=preserved");
    }

    private static async Task VerifyTroopInput(Main host, CityGame game, string? capture)
    {
        var session = host.Session;
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        var guard = siege.Defenders.First(unit => unit.Kind == DefenderKind.Soldier);
        var archer = siege.Defenders.First(unit => unit.Kind == DefenderKind.Archer);
        var units = new[] { guard, archer }.Concat(siege.Defenders.Where(unit => unit.Id != guard.Id && unit.Id != archer.Id)).ToArray();
        var clear = siege.Bounds.Cells().First(p => p.X >= siege.Bounds.X + 12 && p.Y >= siege.Bounds.Y + 12 &&
            p.X + 12 <= siege.Bounds.Right - 12 && p.Y + 8 <= siege.Bounds.Bottom - 12 &&
            new CellRect(p.X, p.Y, 12, 8).Cells().All(cell => siege.ContainsInterior(cell) &&
                game.Map.TerrainAt(cell.X, cell.Y).Buildable &&
                game.Map.BuildingAt(cell.X, cell.Y)?.Zone is null or ZoneType.Residential or ZoneType.Commercial or ZoneType.Industrial &&
                !game.Map.HasRoad(cell.X, cell.Y) && !game.Network.RoadOverlapsCell(game.Map.Index(cell.X, cell.Y))));
        foreach (var cell in new CellRect(clear.X, clear.Y, 12, 8).Cells())
            game.Map.ClearCell(cell.X, cell.Y);
        game.Touch();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var (unit, index) in units.Select((unit, index) => (unit, index)))
        {
            double x = clear.X + 1.5 + index % 4 * 3, y = clear.Y + 1.5 + index / 4 * 4;
            Set(unit, nameof(unit.X), x); Set(unit, nameof(unit.Y), y);
            Set(unit, nameof(unit.PreviousX), x); Set(unit, nameof(unit.PreviousY), y);
            Set(unit, nameof(unit.Elevation), 0f); Set(unit, nameof(unit.PreviousElevation), 0f);
            Set(unit, nameof(unit.Order), DefenderOrder.Automatic);
            typeof(SettlementDefender).GetField("Path", fields)!.SetValue(unit, new List<Pos>());
            typeof(SettlementDefender).GetField("PathIndex", fields)!.SetValue(unit, 0);
            typeof(SettlementDefender).GetField("PathTarget", fields)!.SetValue(unit, null);
            Set(unit, nameof(unit.TargetEnemyId), null);
            Set(unit, nameof(unit.Destination), null);
        }
        Set(siege, nameof(siege.Revision), siege.Revision + 1);
        session.ClearDefenderSelection();
        session.ClearSelection();
        session.CenterOn(new(clear.X + 6, clear.Y + 4));
        await Frames(host);
        var guardPoint = host.Map.DefenderSelectionPoint(guard)!.Value;
        var archerPoint = host.Map.DefenderSelectionPoint(archer)!.Value;
        Require(host.Map.TryPickSiegeActor(guardPoint, out bool friendly, out int id) && friendly && id == guard.Id,
            $"The Shift-click fixture needs the actual frontmost guardsman model: clear={clear}, point={guardPoint}, " +
            $"map={host.Map.Size}, friendly={friendly}, id={id}, expected={guard.Id}.");
        Click(host, guardPoint, MouseButton.Left, shift: true);
        Require(session.SelectedDefenderIds.SequenceEqual([guard.Id]) && host.Map.DefenderSelectionPath.Count == 0,
            "Shift-click must add a defender and finish without leaving a drawing gesture.");
        Vector2[] circle = Enumerable.Range(0, 17).Select(i =>
            archerPoint + new Vector2(Mathf.Cos(i * Mathf.Tau / 16), Mathf.Sin(i * Mathf.Tau / 16)) * 18).ToArray();
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = circle[0], ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true });
        foreach (var point in circle.Skip(1))
            host._Input(new InputEventMouseMotion { Position = host.Map.GlobalPosition + point,
                ButtonMask = MouseButtonMask.Left, ShiftPressed = true });
        Require(host.Map.DefenderSelectionPath.Count >= 3, "Shift-drag must show a real freehand selection outline.");
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".troop-circle.png"));
        host._Input(new InputEventMouseButton { Position = host.Map.GlobalPosition + circle[^1],
            ButtonIndex = MouseButton.Left, Pressed = false });
        Require(session.SelectedDefenderIds.Order().SequenceEqual(new[] { guard.Id, archer.Id }.Order()) &&
            host.Map.DefenderSelectionPath.Count == 0,
            "Closing the circle must add its archer, preserve the Shift-clicked guard and exclude the other six troops.");
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = circle[0], ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true });
        foreach (var point in circle.Skip(1))
            host._Input(new InputEventMouseMotion { Position = host.Map.GlobalPosition + point, ButtonMask = MouseButtonMask.Left });
        host._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(session.SelectedDefenderIds.Count == 2 && host.Map.DefenderSelectionPath.Count == 0 && session.Prompt is null,
            "Escape must cancel the unfinished circle without clearing troops or opening a menu.");
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = circle[0], ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true });
        var outside = host.Map.GlobalPosition + new Vector2(host.Map.Size.X + 30, host.Map.Size.Y / 2);
        host._Input(new InputEventMouseMotion { Position = outside, ButtonMask = MouseButtonMask.Left });
        host._Input(new InputEventMouseButton { Position = outside, ButtonIndex = MouseButton.Left, Pressed = false });
        Require(host.Map.DefenderSelectionPath.Count == 0 && session.SelectedDefenderIds.Count == 2,
            "Releasing a short gesture outside the map must finish it without losing the existing selection.");
        var areaStart = view.ScreenPoint(new(clear.X + .5f,
            view.RenderedGroundHeight(game.Map, clear.X + .5f, clear.Y + 7.5f), clear.Y + 7.5f));
        var areaEnd = view.ScreenPoint(new(clear.X + 2.5f,
            view.RenderedGroundHeight(game.Map, clear.X + 2.5f, clear.Y + 7.5f), clear.Y + 7.5f));
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = areaStart, ButtonIndex = MouseButton.Left, Pressed = true, CtrlPressed = true });
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseMotion { Position = areaEnd, ButtonMask = MouseButtonMask.Left, CtrlPressed = true });
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = areaEnd, ButtonIndex = MouseButton.Left, Pressed = false, CtrlPressed = true });
        Require(session.Selection is { Area: > 1 } && host.Map.DefenderSelectionPath.Count == 0,
            "Ctrl-drag must still select map areas when living troops are present.");
        session.ClearSelection();
        session.SelectDefenders([guard.Id, archer.Id]);

        Require(game.DebugStartSiegeWave(1).Success, "Attack-click checks need a real spawned enemy.");
        var enemy = siege.Enemies.Single();
        session.CenterOn(new((int)enemy.X, (int)enemy.Y));
        Vector2 enemyPoint = default;
        bool picked = false;
        for (int turn = 0; turn < 4 && !picked; turn++)
        {
            view.Rotate(90);
            await Frames(host);
            enemyPoint = view.ScreenPoint(new((float)enemy.X,
                TerrainSprite.Elevation(game.Map, (int)enemy.X, (int)enemy.Y) + (float)enemy.BodyScale * .65f, (float)enemy.Y));
            picked = host.Map.TryPickSiegeActor(enemyPoint, out friendly, out id) && !friendly && id == enemy.Id;
        }
        Require(picked, "Attack orders must use a directly pickable enemy model.");
        Click(host, enemyPoint, MouseButton.Right);
        Require(session.Prompt is null && session.MessageKind == MessageKind.Success &&
            units.All(unit => unit.Id == guard.Id || unit.Id == archer.Id
                ? unit.Order == DefenderOrder.Attack && unit.TargetEnemyId == enemy.Id
                : unit.Order == DefenderOrder.Automatic),
            "Right-clicking an enemy must command only the selected mixed group, not open a context menu.");
        session.ClearDefenderSelection();
        Click(host, enemyPoint, MouseButton.Right);
        Require(session.Prompt is null && session.MessageKind == MessageKind.Success && session.SelectedDefenderIds.Count == 0 &&
            units.All(unit => unit.Order == DefenderOrder.Attack && unit.TargetEnemyId == enemy.Id),
            "Right-clicking an enemy with no troops selected must command every living defender.");
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".troops-attack.png"));
        GD.Print("TERMCITY_TROOP_INPUT_OK shift-click=additive circle=freehand+additive escape=cancel attack=selected-or-all");
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
        Set(siege.Enemies[0], nameof(SiegeEnemy.Kind), SiegeEnemyKind.StoneTroll);
        game.Paused = false;
        for (int tick = 0; tick < 10; tick++) advance.Invoke(siege, [game, .1]);
        game.Paused = true;
        Require(siege.Defenders.Count(unit => unit.TargetEnemyId == siege.Enemies[0].Id) >= 2 &&
            siege.Defenders.Any(unit => unit.TargetEnemyId != siege.Enemies[0].Id && unit.TargetEnemyId is not null),
            "Weighted threat coverage must prefer a troll without abandoning every lower-ranked threat.");
        Set(siege.Enemies[0], nameof(SiegeEnemy.Kind), SiegeEnemyKind.Goblin);
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
        GD.Print("TERMCITY_ARCHER_DISTRIBUTION_OK archers=4 targets=4 weighted-threats=covered survivor=converged");
    }

    private static async Task VerifyTowerPatrols(Main host, CityGame game, string? capture)
    {
        var state = game.Settlement!;
        var side = state.FirstOrcApproach!.Value;
        var advance = typeof(SettlementState).GetMethod("AdvanceDefenders", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plannedWave = typeof(SettlementState).GetField("PlannedOrcWave", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plannedSide = typeof(SettlementState).GetField("PlannedOrcSide", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? savedWave = plannedWave.GetValue(state), savedSide = plannedSide.GetValue(state);
        bool OnSide(WallComponent wall) => side switch
        {
            BaileyGateSide.Right => wall.Cell.X == state.Bounds.Right,
            BaileyGateSide.Left => wall.Cell.X == state.Bounds.X,
            BaileyGateSide.Bottom => wall.Cell.Y == state.Bounds.Bottom,
            _ => wall.Cell.Y == state.Bounds.Y,
        };
        void Settle()
        {
            game.Paused = false;
            for (int tick = 0; tick < 1800; tick++) advance.Invoke(state, [game, .1]);
            game.Paused = true;
            Set(state, nameof(state.Revision), state.Revision + 1);
        }
        Settle();
        foreach (var unit in state.Defenders)
        {
            int? postId = (int?)typeof(SettlementDefender).GetField("PatrolWallId",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(unit);
            Require(state.Walls.Any(w => w.Id == postId && OnSide(w)),
                "Both archers and guards must stage on the scout's announced wall.");
        }
        plannedSide.SetValue(state, null);
        plannedWave.SetValue(state, 0);
        Settle();
        var corners = state.Walls.Where(w => w.IsTower &&
            (w.Cell.X == state.Bounds.X || w.Cell.X == state.Bounds.Right) &&
            (w.Cell.Y == state.Bounds.Y || w.Cell.Y == state.Bounds.Bottom)).ToArray();
        Require(corners.All(corner => state.Defenders.Any(u => u.Kind == DefenderKind.Archer &&
            u.Elevation > .5f && WallDamageLayout.ContainsFootprint(corner, u.X, u.Y, -.1))),
            "Peaceful automatic archers must physically reach all four corner decks. " +
            string.Join("; ", state.Defenders.Select(u => $"{u.Id}:{u.Kind}@{u.X:F2},{u.Y:F2} z={u.Elevation:F2}")));
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        foreach (var corner in corners)
        {
            host.Session.CenterOn(corner.Cell);
            host.Map.RefreshCells();
            await Frames(host);
            var unit = state.Defenders.Single(u => u.Kind == DefenderKind.Archer &&
                WallDamageLayout.ContainsFootprint(corner, u.X, u.Y, -.1));
            Require(view.RenderedDefenderPosition(unit.Id) is not null,
                "Each assigned corner archer must have an actual rendered actor.");
            await Capture(host, capture is null ? null :
                Path.ChangeExtension(capture, $".patrol-corner-{corner.Id}.png"));
        }
        plannedSide.SetValue(state, savedSide);
        plannedWave.SetValue(state, savedWave);
        Settle();
        GD.Print("TERMCITY_TOWER_PATROL_OK staging=archers+guards corners=4 arrivals=physical actors=rendered");
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

    private static void Set(object value, string name, object? data) => value.GetType().GetProperty(name)!.SetValue(value, data);
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
