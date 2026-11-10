using TermCity.Core.Session;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal bool WholeMapWarmingEnabled { get; private set; } = true;
    internal bool RecentGpuResidencyEnabled { get; private set; } = true;
    internal bool AtomicMinimapPresentationEnabled { get; private set; }
    internal bool GroundTextureCachingEnabled { get; private set; } = true;
    internal bool RoadRenderingEnabled { get; private set; } = true;
    private bool _navigationCooldownEnabled = true;
    private int _objectMaterialPageSpan = 8;
    internal bool TerrainFirstPresentation { get; private set; }
    private bool _usefulResidencyOnly = true, _lazyOffscreenTextures;
    private bool _pressureDrivenTerrain = true;
    private readonly HashSet<TermCity.Core.Util.Pos> _previousDestination = [];
    private bool _usefulResidencyDirty = true;
    private long _lastMinimapNavigation;
    internal bool NavigationCoolingDown => _navigationCooldownEnabled &&
        System.Diagnostics.Stopwatch.GetElapsedTime(_lastMinimapNavigation).TotalSeconds < 1;
    internal double GpuRetirementMilliseconds { get; private set; }
    internal double CpuRetirementMilliseconds { get; private set; }

    internal void ConfigureCacheExperiment(CacheExperiment experiment)
    {
        bool persistent = experiment is CacheExperiment.PersistentTerrain or CacheExperiment.PersistentScene;
        bool persistentObjects = experiment is CacheExperiment.PersistentScene or CacheExperiment.Tuned;
        _pressureDrivenTerrain = persistent || experiment == CacheExperiment.Tuned;
        if (persistent || _persistentResidency)
        {
            CpuCacheBudgetBytes = 256L * 1024 * 1024;
            GpuCacheBudgetBytes = 128L * 1024 * 1024;
        }
        if (persistent) experiment = CacheExperiment.Tuned;
        if (_persistentResidency != persistent || _persistentObjectResidency != persistentObjects)
        {
            ClearObjectStreaming();
            foreach (var key in _roadChunks.Keys.Concat(_groundMaterials.Keys).Distinct().ToArray()) RetireGpuGround(key);
            DisposeGroundDrawPages();
            _persistentResidency = persistent;
            _persistentObjectResidency = persistentObjects;
            ResetPersistentResidency();
            InvalidateStaticScene(discardBuildingCache: false);
        }
        _adaptiveObjectUploads = experiment == CacheExperiment.AdaptiveUploads;
        if (_adaptiveObjectUploads) experiment = CacheExperiment.Tuned;
        bool largePages = experiment == CacheExperiment.RecentRegionPagedLodLarge;
        bool stablePages = persistent || largePages || experiment == CacheExperiment.RecentRegionPagedLod;
        bool reserve = !persistent && (stablePages || experiment is CacheExperiment.RecentRegion or CacheExperiment.RecentRegionLod);
        bool largeBudgets = reserve || experiment == CacheExperiment.LargerBudgets;
        bool reserveLod = !persistent && (stablePages || experiment == CacheExperiment.RecentRegionLod);
        if (largeBudgets) experiment = CacheExperiment.Tuned;
        if (_recentRegionEnabled && !reserve) RestoreNativeVisibility();
        _recentRegionEnabled = reserve;
        _recentRouteBounds = null;
        if (largeBudgets)
        {
            CpuCacheBudgetBytes = (largePages ? 768L : 512L) * 1024 * 1024;
            GpuCacheBudgetBytes = (largePages ? 512L : 384L) * 1024 * 1024;
        }
        _overviewLodEnabled = experiment == CacheExperiment.OverviewLod;
        _overviewLodEnabled |= reserveLod;
        if (_overviewLodEnabled) experiment = CacheExperiment.Tuned;
        bool terrainPages = stablePages || experiment == CacheExperiment.TerrainDrawPages;
        if (terrainPages) experiment = CacheExperiment.Tuned;
        bool largerHalo = experiment == CacheExperiment.LargerHalo;
        if (largerHalo) experiment = CacheExperiment.Tuned;
        TerrainHaloEnterChunks = largerHalo ? 2 : 1;
        TerrainHaloExitChunks = largerHalo ? 3 : 2;
        _terrainHalo.Clear();
        _terrainHaloWindow = null;
        bool roadsEnabled = experiment != CacheExperiment.RoadsOff;
        WholeMapWarmingEnabled = experiment != CacheExperiment.NoWarming;
        RecentGpuResidencyEnabled = experiment != CacheExperiment.NoResidency;
        AtomicMinimapPresentationEnabled = experiment is not (CacheExperiment.NoFrameCopy or CacheExperiment.Tuned or CacheExperiment.RoadsOff or CacheExperiment.NoTerrainPriority);
        GroundTextureCachingEnabled = experiment != CacheExperiment.NoTexturePackets;
        _navigationCooldownEnabled = experiment is CacheExperiment.Tuned or CacheExperiment.RoadsOff or CacheExperiment.NoTerrainPriority;
        _objectMaterialPageSpan = experiment == CacheExperiment.SmallPages ? 2 : persistentObjects ? 8 : 4;
        TerrainFirstPresentation = experiment == CacheExperiment.TerrainFirst;
        _usefulResidencyOnly = experiment is CacheExperiment.UsefulResidency or CacheExperiment.Tuned or CacheExperiment.RoadsOff or CacheExperiment.NoTerrainPriority;
        if (persistentObjects) _usefulResidencyOnly = false;
        TerrainPriorityEnabled = experiment != CacheExperiment.NoTerrainPriority;
        if (!TerrainPriorityEnabled) ClearTerrainRetention();
        _lazyOffscreenTextures = experiment == CacheExperiment.LazyTextures;
        if (experiment == CacheExperiment.SmallBudgets)
        {
            CpuCacheBudgetBytes = 192L * 1024 * 1024;
            GpuCacheBudgetBytes = 96L * 1024 * 1024;
        }
        if (!AtomicMinimapPresentationEnabled) CancelMinimapJump();
        if (RoadRenderingEnabled != roadsEnabled)
        {
            CancelGroundPreparation();
            _cameraPathWork?.Task.GetAwaiter().GetResult();
            _cameraPathWork = _cameraPathsIncompatible = null;
            _cameraPathsReady = null;
            RoadRenderingEnabled = roadsEnabled;
            _groundRoadRevision = -1;
            ClearObjectStreaming();
            InvalidateStaticScene(discardBuildingCache: true);
        }
        if (experiment == CacheExperiment.SmallPages)
        {
            ClearObjectStreaming();
            InvalidateStaticScene(discardBuildingCache: false);
        }
        _stableGroundPages = stablePages;
        int groundPageSpan = largePages || persistent ? 4 : 2;
        if (_groundPageSpan != groundPageSpan)
        {
            foreach (var key in _roadChunks.Keys.ToArray()) RetireGpuGround(key);
            DisposeGroundDrawPages();
            _groundPageSpan = groundPageSpan;
        }
        ConfigureGroundDrawPages(terrainPages);
    }

    internal void ValidateDiagnosticRoadsOff()
    {
        if (RoadRenderingEnabled) throw new InvalidOperationException("Road-off validation requires the road-off experiment.");
        if (_roadLayout is null || _roadLayout.Segments.Count != 0 ||
            _preparedGround.Values.Any(chunk => chunk.Roads is not null || chunk.RoadBorderTriangles != 0) ||
            _roadChunks.Values.Any(chunk => chunk.Roads is not null || chunk.RoadBorderTriangles != 0) ||
            BridgeSegmentsDrawn != 0)
            throw new InvalidOperationException("Road-off mode prepared or published road surfaces or road bridges.");
    }

    internal void ValidateRoadRenderingRestored(bool hasRoads)
    {
        if (!RoadRenderingEnabled || _roadLayout is null || hasRoads && _roadLayout.Segments.Count == 0)
            throw new InvalidOperationException("Restoring the tuned renderer did not restore logical road geometry.");
    }

    private void RecordMinimapNavigation()
    {
        RememberTerrainDestination();
        _groundBatchContext = null;
        _previousDestination.Clear();
        _previousDestination.UnionWith(_wantedObjectChunks);
        _usefulResidencyDirty = true;
        _lastMinimapNavigation = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_cameraGroundWork is { } work) work.Cancellation.Cancel();
    }

    private bool UsefulNativeRegion(TermCity.Core.Util.Pos key) =>
        _wantedObjectChunks.Contains(key) || _previousDestination.Contains(key) || _recentPopulatedDestination.Contains(key) ||
        InRecentRegion(key);
}
