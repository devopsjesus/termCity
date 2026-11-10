namespace TermCity.Core.World;

public sealed partial class GameMap
{
    internal GameMap ForkForSimulation()
    {
        var snapshot = new GameMap(Width, Height, Content);
        snapshot.CopySnapshot(this);
        return snapshot;
    }

    internal void CopySnapshot(GameMap source)
    {
        if (ReferenceEquals(this, source)) return;
        if (Width != source.Width || Height != source.Height || !ReferenceEquals(Content, source.Content))
            throw new ArgumentException("Simulation snapshots must have matching dimensions and content.", nameof(source));
        Array.Copy(source.TerrainLayer, TerrainLayer, TerrainLayer.Length);
        Array.Copy(source.SeaLayer, SeaLayer, SeaLayer.Length);
        Array.Copy(source.RiverLayer, RiverLayer, RiverLayer.Length);
        Array.Copy(source.FeatureLayer, FeatureLayer, FeatureLayer.Length);
        Array.Copy(source.RoadLayer, RoadLayer, RoadLayer.Length);
        Array.Copy(source.RoadTypeLayer, RoadTypeLayer, RoadTypeLayer.Length);
        Array.Copy(source.PlayerRoadLayer, PlayerRoadLayer, PlayerRoadLayer.Length);
        Array.Copy(source.ZoneLayer, ZoneLayer, ZoneLayer.Length);
        Array.Copy(source.BuildingLayer, BuildingLayer, BuildingLayer.Length);
        Array.Copy(source.HouseholdLayer, HouseholdLayer, HouseholdLayer.Length);
        Array.Copy(source.PathLayer, PathLayer, PathLayer.Length);
        Array.Copy(source.PathLinkLayer, PathLinkLayer, PathLinkLayer.Length);
        Replace(BuildingEstablishedDays, source.BuildingEstablishedDays);
        Replace(TravelCache, source.TravelCache);
        Replace(ZoneRemovals, source.ZoneRemovals);
        Replace(_buildingFootprints, source._buildingFootprints);
        Replace(_buildingOwners, source._buildingOwners);
        Replace(_houseYardCells, source._houseYardCells);
        Replace(_houseYardOwners, source._houseYardOwners);
        Replace(BuildingRotations, source.BuildingRotations);
        _roadCells.Clear();
        _roadCells.UnionWith(source._roadCells);
        _serviceCells.Clear();
        _serviceCells.UnionWith(source._serviceCells);
        for (int zone = 0; zone < _zoneCells.Length; zone++)
        {
            _zoneCells[zone].Clear();
            _zoneCells[zone].UnionWith(source._zoneCells[zone]);
        }
        RoadAccessChanges.Clear();
        RoadAccessChanges.UnionWith(source.RoadAccessChanges);
        FootbridgeList.Clear();
        FootbridgeList.AddRange(source.FootbridgeList);
        SettlementBounds = source.SettlementBounds;
        NavigationVersion = source.NavigationVersion;
        FootpathVersion = source.FootpathVersion;
        TerrainVersion = source.TerrainVersion;
        ElevationProfile = source.ElevationProfile;
        TravelStepCosts = Clone(source.TravelStepCosts);
        PathRoutes = source.PathRoutes;
        FootpathRoadTargets = source.FootpathRoadTargets;
        FootpathStepKnown = Clone(source.FootpathStepKnown);
        FootpathStepAllowed = Clone(source.FootpathStepAllowed);
        FootpathStepTerrain = source.FootpathStepTerrain;
        TravelWallKnown = Clone(source.TravelWallKnown);
        TravelWallAllowed = Clone(source.TravelWallAllowed);
        TravelWallState = null;
        TravelWallTopology = source.TravelWallTopology;
        BuildingPaths = source.BuildingPaths;
        FootpathJourneyCache = source.FootpathJourneyCache;
        FootpathJourneyBuildings = source.FootpathJourneyBuildings;
        FootpathJourneyRoads = source.FootpathJourneyRoads;
        FootpathJourneyNavigation = source.FootpathJourneyNavigation;
        FootpathStrokeCache = source.FootpathStrokeCache;
        FootpathStrokeNavigation = source.FootpathStrokeNavigation;
        FootpathStrokeVersion = source.FootpathStrokeVersion;
        FootpathStrokeJourneys = source.FootpathStrokeJourneys;
        EntranceAccesses = source.EntranceAccesses is null ? null : new(source.EntranceAccesses);
        HillLevels = Clone(source.HillLevels);
        TerrainSamples = source.TerrainSamples;
        TerrainSamplesVersion = source.TerrainSamplesVersion;
        CoastalHillCaps = Clone(source.CoastalHillCaps);
        WaterDensities = Clone(source.WaterDensities);
        CoastDistances = Clone(source.CoastDistances);
        JunctionDelays = Clone(source.JunctionDelays);
        TravelRoadPaths = source.TravelRoadPaths;
        JunctionRoadLayout = source.JunctionRoadLayout?.ForkForMap(this);
        FootpathHeights = Clone(source.FootpathHeights);
        FootpathBridgeCells = Clone(source.FootpathBridgeCells);
        WaterFlow = source.WaterFlow?.ForkForMap(this);
        LastTravelRepairMilliseconds = source.LastTravelRepairMilliseconds;
        LastRepairedFields = source.LastRepairedFields;
        LastCompactedCells = source.LastCompactedCells;
        LastRepairTreeMilliseconds = source.LastRepairTreeMilliseconds;
        LastRepairScanMilliseconds = source.LastRepairScanMilliseconds;
        LastRepairFloodMilliseconds = source.LastRepairFloodMilliseconds;
        LastRepairFloodNodes = source.LastRepairFloodNodes;
        LastBuildingJourneyMilliseconds = source.LastBuildingJourneyMilliseconds;
        LastRoadJourneyMilliseconds = source.LastRoadJourneyMilliseconds;
        LastPathLinkMilliseconds = source.LastPathLinkMilliseconds;
        LastFootbridgeMilliseconds = source.LastFootbridgeMilliseconds;

        static T[]? Clone<T>(T[]? values) => values is null ? null : (T[])values.Clone();
        static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> values)
            where TKey : notnull
        {
            target.Clear();
            foreach (var pair in values) target.Add(pair.Key, pair.Value);
        }

    }

    internal void BindSnapshotFortifications(Simulation.SettlementState? state)
    {
        Fortifications = state;
        TravelWallState = state;
        if (state is null || TravelWallTopology != state.TopologyRevision)
        {
            TravelWallKnown = TravelWallAllowed = null;
            TravelWallTopology = state?.TopologyRevision ?? -1;
        }
    }
}
