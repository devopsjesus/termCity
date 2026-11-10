using System.Globalization;
using TermCity.Core.Simulation;
using TermCity.Core.Rendering;

namespace TermCity.GodotApp;

public enum DiagnosticRenderQuality { Reference, Native8x, Native4x }

public sealed record GodotOptions(GameConfig Config, bool SmokeTest, string? CapturePath)
{
    public bool Load { get; init; }
    public string? LoadPath { get; init; }
    public bool Help { get; init; }
    public bool DumpMap { get; init; }
    public bool ReducedMotion { get; init; }
    public bool DebugStarter { get; init; }
    public bool DebugSettlement { get; init; }
    public bool DebugSiege { get; init; }
    public bool SettlementSmokeTest { get; init; }
    public bool WorldScaleSmokeTest { get; init; }
    public bool WallNetworkSmokeTest { get; init; }
    public bool InterfaceSmokeTest { get; init; }
    public bool GarrisonSmokeTest { get; init; }
    public bool FootpathArtworkSmokeTest { get; init; }
    public bool DepthComparison { get; init; }
    public bool DepthComparisonSmokeTest { get; init; }
    public bool TerrainComparison { get; init; }
    public TerrainStudyTreatment? TerrainPerformanceTreatment { get; init; }
    public bool DepthPerformanceComparison { get; init; }
    public bool DepthPerformanceBaseline { get; init; }
    public bool BuildingSmokeTest { get; init; }
    public bool MilestoneSmokeTest { get; init; }
    public bool WoodlotSmokeTest { get; init; }
    public bool PlacementSmokeTest { get; init; }
    public bool PerspectiveSmokeTest { get; init; }
    public bool PerspectivePerformanceTest { get; init; }
    public bool CameraPerformanceTest { get; init; }
    public int CameraProfileSeconds { get; init; } = 45;
    public bool CameraQuitTest { get; init; }
    public bool WholeMapCacheTest { get; init; }
    public bool MinimapJumpTest { get; init; }
    public bool MinimapTraversalTest { get; init; }
    public bool HaloScrollTest { get; init; }
    public bool RecentRegionTest { get; init; }
    public bool OverviewLod { get; init; }
    public string? MediumCheckpointsPath { get; init; }
    public double? BenchmarkZoom { get; init; }
    public GameSpeed BenchmarkSpeed { get; init; } = GameSpeed.Medium;
    public bool PersistentResidencyTest { get; init; }
    public bool PersistentResidency { get; init; }
    public CacheExperiment CacheExperiment { get; init; }
    public int FramesPerSecond { get; init; } = 60;
    public DiagnosticRenderQuality RenderQuality { get; init; }

    public const string Usage = """
        TermCity Godot - a desktop city builder
        User options follow Godot's -- separator:
          --seed <n>        Signed 32-bit map seed (default: random)
          --size <size>     small, medium, large, TOP, SF, LA, SD, CHI, STL, or WIDTHxHEIGHT
          --load [file]     Load a city (default: Godot quick-save)
          --fps <n>         Maximum rendered frames per second, 5-60 (default: 60)
          --render-quality <Reference|Native8x|Native4x> Camera diagnostic only; defaults unchanged
          --dump-map        Print the map and exit
          --reduced-motion  Turn all visual effects off (the V key cannot turn them back on)
          --debug-starter   Debug only: well, woodlot and 18-20 full home plots near the spur; start unpaused
          --debug-settlement Debug only: 64x64 pre-drawn bailey; camera focused inside; start unpaused
          --debug-siege     Debug only: 64x64, 100-settler bailey, guard/archer garrisons, goblin wave and siege controls
          -h, --help        Show this help
          --smoke-test     Run engine integration checks and exit
          --building-smoke-test Verify building sprites at every zoom and exit
          --milestone-smoke-test Verify reached-only unlocks and narrow milestone dialogs
          --woodlot-smoke-test Verify fenced firewood yard, picking and model previews
          --placement-smoke-test Verify map controls and clicked placement while building
          --perspective-smoke-test Verify tilted 3D camera, rotation and picking
          --perspective-performance-test Check zoom, pan and selection performance
          --camera-performance-test Profile live camera input through simulation ticks; supports --debug-siege
          --camera-quit-test Measure quit with queued saved-combat work (requires --load)
          --camera-profile-seconds <n> Saved combat sampling duration, 5-300 seconds (default: 45)
          --whole-map-cache-test Verify progressive CPU retention, warm jumps and memory pressure
          --minimap-jump-test Verify frozen transitions, warm native reuse and destination memory
          --minimap-traversal-test Profile 24 sustained minimap jumps; supports --debug-siege
          --halo-scroll-test Compare fullscreen local scrolling at normal/full overview zoom
          --recent-region-test Measure fullscreen two-screen out-and-back reuse at both zooms; use TOP
          --generate-medium-checkpoints <dir> Generate bounded ordinary-rule growth saves; requires --size medium
          --benchmark-zoom <normal|overview> Isolate one zoom in region/minimap diagnostics
          --benchmark-speed <Slow|Medium|Fast> Starting speed in minimap or saved-combat diagnostics (default: Medium)
          --persistent-residency-test Verify native residency, edit/pressure lifecycle and camera-only reuse
          --persistent-residency Enable opt-in persistent native terrain/building pages (Debug only)
          --overview-lod     Debug only: opt-in simplified models at maximum zoom-out in a normal game
          --cache-experiment <name> Tuned, PersistentTerrain, PersistentScene, AdaptiveUploads, LargerBudgets, RecentRegion, RecentRegionLod, RecentRegionPagedLod, RecentRegionPagedLodLarge, LargerHalo, TerrainDrawPages, OverviewLod; requires a renderer diagnostic
          --settlement-smoke-test Verify enclosure tools, wall picking, garrison and combat rendering
          --wall-network-smoke-test Verify freeform walls, gates, picking and connected territory
          --interface-smoke-test Verify sidebar typography, debug controls and continuous friendly movement
          --garrison-smoke-test Verify tower patrols, first-Orc staging, troop commands and combat
          --footpath-art-smoke-test Verify dirt/doorway artwork; TOP checks live native path publication
          --world-scale-smoke-test Verify real-scale structures, trolls, picking and mixed-swarm performance
          --depth-comparison Open the isolated building/wall depth comparison (Debug only)
          --depth-comparison-smoke-test Verify and capture all comparison subjects (Debug only)
          --terrain-comparison Compare terrain lighting, edge relief and hill heights (Debug only)
          --terrain-comparison-smoke-test Validate and capture terrain candidates (Debug only)
          --terrain-performance-treatment <name> Current, Lighting, Balanced, DoubleHills, TripleHills; requires performance smoke
          --depth-performance-comparison Explicitly use the default combined performance treatment (Debug only)
          --depth-performance-baseline Use original art with --perspective-performance-test (Debug only)
          --capture <path> Save a PNG during a graphical smoke run
        """;

    public static GodotOptions Parse(string[] arguments)
    {
        var config = new GameConfig { Seed = Random.Shared.Next() };
        bool smokeTest = false;
        bool buildingSmokeTest = false;
        bool milestoneSmokeTest = false;
        bool woodlotSmokeTest = false;
        bool placementSmokeTest = false;
        bool perspectiveSmokeTest = false;
        bool perspectivePerformanceTest = false;
        bool cameraPerformanceTest = false;
        bool wholeMapCacheTest = false;
        bool minimapJumpTest = false;
        bool minimapTraversalTest = false;
        bool haloScrollTest = false;
        bool recentRegionTest = false;
        bool overviewLod = false;
        var cacheExperiment = CacheExperiment.Current;
        bool cacheExperimentSpecified = false;
        string? capturePath = null;
        bool load = false, help = false, dump = false, reducedMotion = false;
        bool debugStarter = false;
        bool debugSettlement = false, debugSiege = false, settlementSmokeTest = false, worldScaleSmokeTest = false;
        bool wallNetworkSmokeTest = false;
        bool interfaceSmokeTest = false;
        bool garrisonSmokeTest = false;
        bool footpathArtworkSmokeTest = false;
        bool depthComparison = false, depthComparisonSmokeTest = false;
        bool terrainComparison = false;
        TerrainStudyTreatment? terrainPerformanceTreatment = null;
        bool depthPerformanceComparison = false;
        bool depthPerformanceBaseline = false;
        string? loadPath = null;
        string? mediumCheckpointsPath = null;
        double? benchmarkZoom = null;
        GameSpeed benchmarkSpeed = GameSpeed.Medium;
        bool benchmarkSpeedSpecified = false;
        bool persistentResidencyTest = false;
        bool persistentResidency = false;
        int fps = 60;
        int? cameraProfileSeconds = null;
        bool cameraQuitTest = false;
        DiagnosticRenderQuality renderQuality = DiagnosticRenderQuality.Reference;
        bool qualitySpecified = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (argument == "--render-quality")
            {
                qualitySpecified = true;
                if (++i >= arguments.Length || !Enum.TryParse(arguments[i], true, out renderQuality) ||
                    !Enum.IsDefined(renderQuality))
                    throw new ArgumentException("--render-quality must be Reference, Native8x or Native4x.");
                continue;
            }
            if (argument == "--persistent-residency")
            {
                persistentResidency = true;
                continue;
            }
            if (argument == "--persistent-residency-test")
            {
                persistentResidencyTest = true;
                smokeTest = true;
                continue;
            }
            if (argument is "--benchmark-zoom" or "--benchmark-speed")
            {
                if (++i >= arguments.Length) throw new ArgumentException($"{argument} requires a value.");
                string benchmarkValue = arguments[i].ToLowerInvariant();
                if (argument == "--benchmark-zoom")
                    benchmarkZoom = benchmarkValue switch
                    {
                        "normal" => 0,
                        "overview" => TermCity.Core.Session.GameSession.MinZoom,
                        _ => throw new ArgumentException("--benchmark-zoom must be normal or overview."),
                    };
                else
                {
                    benchmarkSpeedSpecified = true;
                    benchmarkSpeed = benchmarkValue switch
                    {
                        "slow" => GameSpeed.Slow, "medium" => GameSpeed.Medium, "fast" => GameSpeed.Fast,
                        _ => throw new ArgumentException("--benchmark-speed must be Slow, Medium or Fast."),
                    };
                }
                continue;
            }
            if (argument == "--generate-medium-checkpoints")
            {
                if (++i >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--generate-medium-checkpoints requires an output directory.");
                mediumCheckpointsPath = arguments[i];
                smokeTest = true;
                continue;
            }
            if (argument == "--recent-region-test")
            {
                smokeTest = true; recentRegionTest = true; continue;
            }
            if (argument == "--overview-lod")
            {
                overviewLod = true;
                continue;
            }
            if (argument == "--halo-scroll-test")
            {
                smokeTest = true;
                haloScrollTest = true;
                continue;
            }
            if (argument == "--minimap-traversal-test")
            {
                smokeTest = true;
                minimapTraversalTest = true;
                continue;
            }
            if (argument == "--cache-experiment")
            {
                cacheExperimentSpecified = true;
                if (++i >= arguments.Length || !Enum.TryParse(arguments[i], ignoreCase: true, out cacheExperiment) ||
                    !Enum.IsDefined(cacheExperiment))
                    throw new ArgumentException("--cache-experiment requires a supported experiment name.");
                continue;
            }
            if (argument == "--minimap-jump-test")
            {
                smokeTest = true;
                minimapJumpTest = true;
                continue;
            }
            if (argument == "--whole-map-cache-test")
            {
                smokeTest = true;
                wholeMapCacheTest = true;
                continue;
            }
            if (argument == "--camera-performance-test")
            {
                smokeTest = true;
                cameraPerformanceTest = true;
                continue;
            }
            if (argument == "--camera-quit-test")
            {
                smokeTest = cameraPerformanceTest = cameraQuitTest = true;
                continue;
            }
            if (argument == "--camera-profile-seconds")
            {
                if (++i >= arguments.Length || !int.TryParse(arguments[i], out int seconds) || seconds is < 5 or > 300)
                    throw new ArgumentException("--camera-profile-seconds must be from 5 to 300.");
                cameraProfileSeconds = seconds;
                continue;
            }
            if (argument is "-h" or "--help")
            {
                help = true;
                continue;
            }
            if (argument == "--dump-map")
            {
                dump = true;
                continue;
            }
            if (argument is "--depth-comparison" or "--depth-comparison-smoke-test")
            {
                depthComparison = true;
                depthComparisonSmokeTest |= argument == "--depth-comparison-smoke-test";
                continue;
            }
            if (argument is "--terrain-comparison" or "--terrain-comparison-smoke-test")
            {
                terrainComparison = true;
                depthComparison = true;
                depthComparisonSmokeTest |= argument == "--terrain-comparison-smoke-test";
                continue;
            }
            if (argument == "--depth-performance-comparison")
            {
                depthPerformanceComparison = true;
                continue;
            }
            if (argument == "--depth-performance-baseline")
            {
                depthPerformanceBaseline = true;
                continue;
            }
            if (argument == "--reduced-motion")
            {
                reducedMotion = true;
                continue;
            }
            if (argument == "--debug-starter")
            {
                debugStarter = true;
                continue;
            }
            if (argument is "--debug-settlement" or "--debug-siege")
            {
                debugSettlement |= argument == "--debug-settlement";
                debugSiege |= argument == "--debug-siege";
                continue;
            }
            if (argument == "--settlement-smoke-test")
            {
                smokeTest = true;
                settlementSmokeTest = true;
                continue;
            }
            if (argument == "--interface-smoke-test")
            {
                smokeTest = true;
                interfaceSmokeTest = true;
                continue;
            }
            if (argument == "--garrison-smoke-test")
            {
                smokeTest = true;
                garrisonSmokeTest = true;
                continue;
            }
            if (argument == "--footpath-art-smoke-test")
            {
                smokeTest = true;
                footpathArtworkSmokeTest = true;
                continue;
            }
            if (argument == "--world-scale-smoke-test")
            {
                smokeTest = true;
                worldScaleSmokeTest = true;
                continue;
            }
            if (argument == "--wall-network-smoke-test")
            {
                smokeTest = true;
                wallNetworkSmokeTest = true;
                continue;
            }
            if (argument == "--load")
            {
                load = true;
                if (i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    loadPath = arguments[++i];
                }
                continue;
            }
            if (argument == "--smoke-test")
            {
                smokeTest = true;
                continue;
            }
            if (argument == "--building-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                continue;
            }
            if (argument == "--woodlot-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                woodlotSmokeTest = true;
                continue;
            }
            if (argument == "--placement-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                placementSmokeTest = true;
                continue;
            }
            if (argument == "--milestone-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                milestoneSmokeTest = true;
                continue;
            }
            if (argument == "--perspective-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                perspectiveSmokeTest = true;
                continue;
            }
            if (argument == "--perspective-performance-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                perspectivePerformanceTest = true;
                continue;
            }

            if (argument is not ("--seed" or "--size" or "--capture" or "--fps" or "--terrain-performance-treatment"))
            {
                throw new ArgumentException($"Unknown Godot option '{argument}'.");
            }

            if (++i >= arguments.Length)
            {
                throw new ArgumentException($"Missing value for {argument}.");
            }

            string value = arguments[i];
            switch (argument)
            {
                case "--terrain-performance-treatment":
                    if (!Enum.TryParse<TerrainStudyTreatment>(value, true, out var treatment) || !Enum.IsDefined(treatment))
                        throw new ArgumentException("Choose Current, Lighting, Balanced, DoubleHills or TripleHills for terrain performance.");
                    terrainPerformanceTreatment = treatment;
                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                    {
                        throw new ArgumentException("--seed must be a signed 32-bit integer.");
                    }
                    config = config with { Seed = seed };
                    break;
                case "--size":
                    if (!MapSize.TryParse(value, out var size, out string error))
                    {
                        throw new ArgumentException(error);
                    }
                    config = config with { MapWidth = size.Width, MapHeight = size.Height, Scenario = size.Scenario };
                    break;
                case "--capture":
                    capturePath = value;
                    break;
                case "--fps":
                    if (!int.TryParse(value, out fps) || fps is < 5 or > 60)
                    {
                        throw new ArgumentException("--fps must be from 5 to 60.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown Godot option '{argument}'.");
            }
        }
        if (depthComparison && !help)
        {
            if (!CityGame.DebugToolsAvailable)
                throw new ArgumentException("Depth comparison requires a Debug build.");
            if (load || dump || smokeTest || debugStarter || debugSettlement || debugSiege ||
                config.Scenario != CityScenario.Random)
                throw new ArgumentException("Depth comparison requires a separate run, not a live city or other debug/smoke mode.");
        }
        if (wholeMapCacheTest && (load || dump || buildingSmokeTest || perspectiveSmokeTest ||
            perspectivePerformanceTest || cameraPerformanceTest || settlementSmokeTest || worldScaleSmokeTest ||
            interfaceSmokeTest || garrisonSmokeTest || footpathArtworkSmokeTest || wallNetworkSmokeTest ||
            depthComparison || debugStarter || debugSettlement || debugSiege))
            throw new ArgumentException("--whole-map-cache-test requires a separate new-city cache run.");
        if (cacheExperimentSpecified && !minimapTraversalTest && !haloScrollTest && !recentRegionTest && !persistentResidencyTest)
            throw new ArgumentException("--cache-experiment requires --minimap-traversal-test or --halo-scroll-test.");
        if ((recentRegionTest || minimapTraversalTest || mediumCheckpointsPath is not null) && load && string.IsNullOrWhiteSpace(loadPath))
            throw new ArgumentException("Snapshot benchmarks require an explicit --load path, not the player's quick-save.");
        if (benchmarkZoom is not null && !recentRegionTest && !minimapTraversalTest ||
            benchmarkSpeedSpecified && !minimapTraversalTest && !(cameraPerformanceTest && load))
            throw new ArgumentException("Benchmark zoom/speed controls require the appropriate region/minimap diagnostic.");
        if (persistentResidencyTest && (load || dump || capturePath is not null || mediumCheckpointsPath is not null ||
            config.Scenario != CityScenario.Random || buildingSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest ||
            cameraPerformanceTest || wholeMapCacheTest || minimapJumpTest || minimapTraversalTest || haloScrollTest ||
            recentRegionTest || settlementSmokeTest || worldScaleSmokeTest || interfaceSmokeTest || garrisonSmokeTest ||
            footpathArtworkSmokeTest || wallNetworkSmokeTest || depthComparison || debugStarter || debugSettlement || debugSiege ||
            cacheExperiment is not (CacheExperiment.Current or CacheExperiment.PersistentTerrain or CacheExperiment.PersistentScene)))
            throw new ArgumentException("Persistent residency correctness requires a separate new-city diagnostic.");
        if (persistentResidency && !help && (!CityGame.DebugToolsAvailable || smokeTest || overviewLod))
            throw new ArgumentException("--persistent-residency requires a separate Debug game without overview LOD; diagnostics use PersistentScene.");
        if (mediumCheckpointsPath is not null && (dump || capturePath is not null ||
            config.Scenario != CityScenario.Random || config.MapWidth != 480 || config.MapHeight != 480 || !config.FullRules ||
            buildingSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest || cameraPerformanceTest ||
            wholeMapCacheTest || minimapJumpTest || minimapTraversalTest || haloScrollTest || recentRegionTest ||
            settlementSmokeTest || worldScaleSmokeTest || interfaceSmokeTest || garrisonSmokeTest || footpathArtworkSmokeTest ||
            wallNetworkSmokeTest || depthComparison || debugStarter || debugSettlement || debugSiege))
            throw new ArgumentException("Medium checkpoint generation requires a separate full-rules --size medium run.");
        if (recentRegionTest && (dump || buildingSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest ||
            cameraPerformanceTest || wholeMapCacheTest || minimapJumpTest || minimapTraversalTest || haloScrollTest ||
            settlementSmokeTest || worldScaleSmokeTest || interfaceSmokeTest || garrisonSmokeTest || footpathArtworkSmokeTest ||
            wallNetworkSmokeTest || depthComparison || debugStarter || debugSettlement || debugSiege))
            throw new ArgumentException("--recent-region-test requires a separate scenario performance run.");
        if (overviewLod && !help && (!CityGame.DebugToolsAvailable || smokeTest))
            throw new ArgumentException("--overview-lod requires a separate Debug game; diagnostics use --cache-experiment OverviewLod.");
        if (haloScrollTest && (load || dump || buildingSmokeTest || perspectiveSmokeTest ||
            perspectivePerformanceTest || cameraPerformanceTest || wholeMapCacheTest || minimapJumpTest ||
            minimapTraversalTest || settlementSmokeTest || worldScaleSmokeTest || interfaceSmokeTest ||
            garrisonSmokeTest || footpathArtworkSmokeTest || wallNetworkSmokeTest || depthComparison ||
            debugStarter || debugSettlement || debugSiege ||
            cacheExperiment is not (CacheExperiment.Current or CacheExperiment.Tuned or CacheExperiment.LargerHalo or CacheExperiment.TerrainDrawPages or CacheExperiment.OverviewLod)))
            throw new ArgumentException("--halo-scroll-test requires a separate run with Tuned or LargerHalo.");
        if (minimapTraversalTest && (dump || buildingSmokeTest || perspectiveSmokeTest ||
            perspectivePerformanceTest || cameraPerformanceTest || wholeMapCacheTest || minimapJumpTest ||
            settlementSmokeTest || worldScaleSmokeTest || interfaceSmokeTest || garrisonSmokeTest ||
            footpathArtworkSmokeTest || wallNetworkSmokeTest || depthComparison || debugStarter || debugSettlement))
            throw new ArgumentException("--minimap-traversal-test requires a separate new-city traversal run; --debug-siege is optional.");
        if (minimapJumpTest && (load || dump || buildingSmokeTest || perspectiveSmokeTest ||
            perspectivePerformanceTest || cameraPerformanceTest || wholeMapCacheTest || settlementSmokeTest ||
            worldScaleSmokeTest || interfaceSmokeTest || garrisonSmokeTest || footpathArtworkSmokeTest ||
            wallNetworkSmokeTest || depthComparison || debugStarter || debugSettlement || debugSiege))
            throw new ArgumentException("--minimap-jump-test requires a separate new-city jump run.");
        if (depthPerformanceComparison && !help &&
            (!CityGame.DebugToolsAvailable || !perspectivePerformanceTest || depthComparison))
            throw new ArgumentException("--depth-performance-comparison requires a separate Debug --perspective-performance-test run.");
        if (depthPerformanceBaseline && !help &&
            (!CityGame.DebugToolsAvailable || !perspectivePerformanceTest || depthComparison || depthPerformanceComparison))
            throw new ArgumentException("--depth-performance-baseline requires a separate Debug --perspective-performance-test run without --depth-performance-comparison.");
        if (terrainPerformanceTreatment is not null && !help &&
            (!CityGame.DebugToolsAvailable || !perspectivePerformanceTest || load || depthComparison ||
                depthPerformanceBaseline || depthPerformanceComparison))
            throw new ArgumentException("Terrain performance treatments require a separate Debug performance smoke, not a loaded city or other comparison.");
        if (capturePath is not null && !smokeTest && !depthComparison)
        {
            throw new ArgumentException("--capture requires --smoke-test.");
        }
        if (milestoneSmokeTest && (load || config.Scenario != CityScenario.Random ||
            woodlotSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--milestone-smoke-test requires a new random city and cannot be combined with other model smoke modes.");
        if (cameraPerformanceTest && (buildingSmokeTest || milestoneSmokeTest || woodlotSmokeTest ||
            placementSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest || settlementSmokeTest ||
            worldScaleSmokeTest || interfaceSmokeTest || garrisonSmokeTest || footpathArtworkSmokeTest ||
            wallNetworkSmokeTest || depthComparison || depthComparisonSmokeTest || terrainComparison || debugStarter || dump))
            throw new ArgumentException("--camera-performance-test requires a separate camera smoke run; --debug-siege is optional.");
        if (settlementSmokeTest && (load || config.Scenario != CityScenario.Random ||
            buildingSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--settlement-smoke-test requires a new random city and cannot be combined with other smoke modes.");
        if (worldScaleSmokeTest && (load || config.Scenario != CityScenario.Random || settlementSmokeTest ||
            buildingSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--world-scale-smoke-test requires a new random city and a separate smoke run.");
        if (interfaceSmokeTest && (!CityGame.DebugToolsAvailable || load || config.Scenario != CityScenario.Random ||
            settlementSmokeTest || worldScaleSmokeTest || wallNetworkSmokeTest || buildingSmokeTest ||
            milestoneSmokeTest || woodlotSmokeTest || placementSmokeTest || perspectiveSmokeTest ||
            perspectivePerformanceTest))
            throw new ArgumentException("--interface-smoke-test requires a separate Debug new random-city smoke run.");
        if (garrisonSmokeTest && (!CityGame.DebugToolsAvailable || load || config.Scenario != CityScenario.Random ||
            settlementSmokeTest || worldScaleSmokeTest || wallNetworkSmokeTest || interfaceSmokeTest ||
            footpathArtworkSmokeTest || buildingSmokeTest || milestoneSmokeTest || woodlotSmokeTest ||
            placementSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest ||
            depthComparison || depthComparisonSmokeTest || terrainComparison))
            throw new ArgumentException("--garrison-smoke-test requires a separate Debug new random-city smoke run.");
        if (footpathArtworkSmokeTest && (load || config.Scenario is not (CityScenario.Random or CityScenario.Top) ||
            settlementSmokeTest || worldScaleSmokeTest || wallNetworkSmokeTest || interfaceSmokeTest || buildingSmokeTest ||
            milestoneSmokeTest || woodlotSmokeTest || placementSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--footpath-art-smoke-test requires a separate new random-city or TOP smoke run.");
        if (wallNetworkSmokeTest && (load || config.Scenario != CityScenario.Random || settlementSmokeTest ||
            worldScaleSmokeTest || buildingSmokeTest || milestoneSmokeTest || woodlotSmokeTest ||
            placementSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--wall-network-smoke-test requires a separate new random-city smoke run.");
        if ((debugStarter || debugSettlement || debugSiege) && !help)
        {
            if ((debugStarter ? 1 : 0) + (debugSettlement ? 1 : 0) + (debugSiege ? 1 : 0) > 1)
                throw new ArgumentException("Choose only one Debug starter configuration.");
            if (!CityGame.DebugToolsAvailable)
                throw new ArgumentException("Debug starter configurations require a Debug build.");
            if (load || config.Scenario != CityScenario.Random)
                throw new ArgumentException("Debug starter configurations require a new random city, not a saved city or populated scenario.");
            if (smokeTest && !cameraPerformanceTest && !(minimapTraversalTest && debugSiege) || dump)
                throw new ArgumentException("Debug starter configurations cannot be combined with smoke tests or --dump-map.");
        }
        if (qualitySpecified && (!cameraPerformanceTest || !CityGame.DebugToolsAvailable))
            throw new ArgumentException("--render-quality requires a separate Debug camera-performance diagnostic.");
        if (cameraProfileSeconds is not null && (!cameraPerformanceTest || !load))
            throw new ArgumentException("--camera-profile-seconds requires a loaded-save camera-performance diagnostic.");
        if (cameraQuitTest && !load)
            throw new ArgumentException("--camera-quit-test requires an explicit saved-combat checkpoint.");
        return new(config, smokeTest, capturePath)
        {
            Load = load, LoadPath = loadPath, Help = help, DumpMap = dump, FramesPerSecond = fps, ReducedMotion = reducedMotion,
            RenderQuality = renderQuality,
            BuildingSmokeTest = buildingSmokeTest,
            MilestoneSmokeTest = milestoneSmokeTest,
            WoodlotSmokeTest = woodlotSmokeTest,
            PlacementSmokeTest = placementSmokeTest,
            PerspectiveSmokeTest = perspectiveSmokeTest,
            PerspectivePerformanceTest = perspectivePerformanceTest,
            CameraPerformanceTest = cameraPerformanceTest,
            CameraProfileSeconds = cameraProfileSeconds ?? 45,
            CameraQuitTest = cameraQuitTest,
            WholeMapCacheTest = wholeMapCacheTest,
            MinimapJumpTest = minimapJumpTest,
            MinimapTraversalTest = minimapTraversalTest,
            HaloScrollTest = haloScrollTest,
            RecentRegionTest = recentRegionTest,
            OverviewLod = overviewLod,
            MediumCheckpointsPath = mediumCheckpointsPath,
            BenchmarkZoom = benchmarkZoom, BenchmarkSpeed = benchmarkSpeed,
            PersistentResidencyTest = persistentResidencyTest,
            PersistentResidency = persistentResidency,
            CacheExperiment = cacheExperiment,
            DebugStarter = debugStarter,
            DebugSettlement = debugSettlement, DebugSiege = debugSiege, SettlementSmokeTest = settlementSmokeTest,
            WorldScaleSmokeTest = worldScaleSmokeTest,
            WallNetworkSmokeTest = wallNetworkSmokeTest,
            InterfaceSmokeTest = interfaceSmokeTest,
            GarrisonSmokeTest = garrisonSmokeTest,
            FootpathArtworkSmokeTest = footpathArtworkSmokeTest,
            DepthComparison = depthComparison, DepthComparisonSmokeTest = depthComparisonSmokeTest,
            TerrainComparison = terrainComparison,
            TerrainPerformanceTreatment = terrainPerformanceTreatment,
            DepthPerformanceComparison = depthPerformanceComparison,
            DepthPerformanceBaseline = depthPerformanceBaseline,
        };
    }
}
