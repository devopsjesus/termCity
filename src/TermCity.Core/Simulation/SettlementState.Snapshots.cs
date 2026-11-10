namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    internal bool CanSnapshot => !_changingWalls;

    private SettlementState(CityGame game, SettlementState source, bool presentation = false)
    {
        Bounds = source.Bounds;
        GateSide = source.GateSide;
        Walls = WallList.AsReadOnly();
        Enemies = EnemyList.AsReadOnly();
        Projectiles = ProjectileList.AsReadOnly();
        if (presentation) CopyCombatState(game, source);
        else CopySnapshot(game, source);
    }

    internal SettlementState ForkForSimulation(CityGame game) => new(game, this);
    internal SettlementState ForkForPresentation(CityGame game) => new(game, this, presentation: true);

    internal void CopyCombatState(CityGame game, SettlementState source)
    {
        if (Bounds != source.Bounds || GateSide != source.GateSide)
            throw new ArgumentException("Settlement snapshots must preserve founding bounds and gate side.", nameof(source));
        if (source._changingWalls)
            throw new InvalidOperationException("A settlement cannot be captured during a wall transaction.");
        var walls = WallList.ToDictionary(wall => wall.Id);
        WallList.Clear();
        foreach (var original in source.WallList)
        {
            if (walls.TryGetValue(original.Id, out var wall)) wall.CopySnapshot(original);
            else walls[original.Id] = wall = original.SnapshotCopy(this);
            WallList.Add(wall);
        }
        foreach (var original in source.WallList)
            walls[original.Id].SharedSection = original.SharedSection is { } section ? walls[section.Id] : null;
        _wallAt.Clear();
        foreach (var (cell, wall) in source._wallAt) _wallAt.Add(cell, walls[wall.Id]);
        var enemies = EnemyList.ToDictionary(enemy => enemy.Id);
        EnemyList.Clear();
        foreach (var original in source.EnemyList)
        {
            if (enemies.TryGetValue(original.Id, out var enemy)) enemy.CopySnapshot(original);
            else enemy = original.SnapshotCopy();
            EnemyList.Add(enemy);
        }
        var defenders = DefenderList.ToDictionary(unit => unit.Id);
        DefenderList.Clear();
        foreach (var original in source.DefenderList)
        {
            if (defenders.TryGetValue(original.Id, out var unit)) unit.CopySnapshot(original);
            else unit = original.SnapshotCopy();
            DefenderList.Add(unit);
        }
        var projectiles = ProjectileList.Where(shot => shot.Id > 0).ToDictionary(shot => shot.Id);
        ProjectileList.Clear();
        foreach (var original in source.ProjectileList)
        {
            if (original.Id > 0 && projectiles.TryGetValue(original.Id, out var shot))
            {
                shot.CopySnapshot(original);
                ProjectileList.Add(shot);
            }
            else ProjectileList.Add(original.SnapshotCopy());
        }
        _deaths.Clear();
        _deaths.AddRange(source._deaths.Select(death => death with { }));
        RosterTimers.Clear();
        foreach (var pair in source.RosterTimers) RosterTimers.Add(pair.Key, pair.Value);
        if (EstablishedLand.Count != source.EstablishedLand.Count || _topologyRevision != source._topologyRevision)
        {
            EstablishedLand.Clear();
            EstablishedLand.UnionWith(source.EstablishedLand);
        }
        Accumulator = source.Accumulator;
        NextEnemyId = source.NextEnemyId;
        NextDefenderId = source.NextDefenderId;
        NextAttackId = source.NextAttackId;
        NextWallId = source.NextWallId;
        Material = source.Material;
        WaveNumber = source.WaveNumber;
        NextWaveSeconds = source.NextWaveSeconds;
        Revision = source.Revision;
        OrcsIntroduced = source.OrcsIntroduced;
        PlannedOrcWave = source.PlannedOrcWave;
        PlannedOrcSide = source.PlannedOrcSide;
        FirstAssaultSide = source.FirstAssaultSide;
        FirstAssaultLastOrcId = source.FirstAssaultLastOrcId;
        TrollsIntroduced = source.TrollsIntroduced;
        PlannedTrollWave = source.PlannedTrollWave;
        RepairTarget = source.RepairTarget;
        RepairTargetId = source.RepairTargetId;
        RepairPhase = source.RepairPhase;
        UpgradeTargetMaterial = source.UpgradeTargetMaterial;
        UpgradedSections = source.UpgradedSections;
        UpgradeSectionSeconds = source.UpgradeSectionSeconds;
        UpgradeOrder = source.UpgradeOrder is null ? null : new(source.UpgradeOrder);
        _upgradeSectionIndices = source._upgradeSectionIndices is null ? null : new(source._upgradeSectionIndices);
        _patrolSignature = source._patrolSignature;
        _patrolRetrySeconds = source._patrolRetrySeconds;
        _patrolStagingSide = source._patrolStagingSide;
        _patrolNavigationVersion = source._patrolNavigationVersion;
        ArcherReachabilityBuilds = source.ArcherReachabilityBuilds;
        _orcApproachMapVersion = source._orcApproachMapVersion;
        _orcApproachTopologyRevision = source._orcApproachTopologyRevision;
        _orcApproachWave = source._orcApproachWave;
        if (_topologyRevision != source._topologyRevision || _defenderPathTowerState != source._defenderPathTowerState)
        {
            _wallSpatial = null;
            _groundLadders.Clear();
            _walkwayLadders.Clear();
            _walkLinks.Clear();
            _reserved.Clear();
            _wallExteriorSides.Clear();
            _attackFragments.Clear();
            _defenderPathCells.Clear();
            _defenderPathEdges.Clear();
            _defenderInteriorCells.Clear();
            _archerRangePosts.Clear();
            _archerReachability.Clear();
            _defenderPathNavigation = -1;
            _garrisonNavigationVersion = -1;
        }
        _network = source._network;
        _artworkNetwork = source._artworkNetwork;
        _topologyRevision = source._topologyRevision;
        _onlyOriginalWalls = source._onlyOriginalWalls;
        _defenderGame = game;
        _defenderPathTowerState = source._defenderPathTowerState;
        AttachedMap = game.Map;
    }

    internal void CopySnapshot(CityGame game, SettlementState source)
    {
        _archerReachability.Clear();
        CopyCombatState(game, source);
        EstablishedLand.Clear();
        EstablishedLand.UnionWith(source.EstablishedLand);
        var walls = WallList.ToDictionary(wall => wall.Id);
        _network = source._network;
        _artworkNetwork = source._artworkNetwork;
        _topologyRevision = source._topologyRevision;
        _territoryRevision = source._territoryRevision;
        _territoryMapVersion = source._territoryMapVersion;
        _eligibleLand.Clear();
        _eligibleLand.UnionWith(source._eligibleLand);
        _eligibleArea = source._eligibleArea;
        _growthBounds = source._growthBounds;
        _onlyOriginalWalls = source._onlyOriginalWalls;
        Replace(_wallExteriorSides, source._wallExteriorSides);
        Replace(_groundLadders, source._groundLadders);
        _walkwayLadders.Clear();
        foreach (var (id, ladders) in source._walkwayLadders)
            _walkwayLadders.Add(id, ladders.Select(ladder =>
                ladder with { Walkway = walls[ladder.Walkway.Id] }).ToArray());
        Replace(_walkLinks, source._walkLinks);
        Replace(_reserved, source._reserved);
        Replace(_wallDistances, source._wallDistances);
        Replace(_attackFragments, source._attackFragments);
        _wallSpatial = source._wallSpatial?.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(wall => walls[wall.Id]).ToList());
        _defenderPathCells.Clear();
        foreach (var (cell, value) in source._defenderPathCells)
            _defenderPathCells.Add(cell, (value.Ground, value.Support is { } support ? walls[support.Id] : null));
        _defenderPathNavigation = source._defenderPathNavigation;
        _defenderPathTowerState = source._defenderPathTowerState;
        Replace(_defenderPathEdges, source._defenderPathEdges);
        Replace(_defenderInteriorCells, source._defenderInteriorCells);
        Replace(_defenderWalkHeights, source._defenderWalkHeights);
        _archerRangePosts.Clear();
        _garrisonGround = source._garrisonGround is null ? null : new(source._garrisonGround);
        _garrisonNavigationVersion = source._garrisonNavigationVersion;
        _defenderGame = game;
        AttachedMap = game.Map;
        _movementOrder.Clear();

        static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> values)
            where TKey : notnull
        {
            target.Clear();
            foreach (var pair in values) target.Add(pair.Key, pair.Value);
        }
    }
}
