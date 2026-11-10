using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private bool _overviewLodEnabled, _overviewLodActive;
    private readonly Dictionary<(Pos Key, bool Overview), ObjectChunk> _alternateLodChunks = [];
    private bool UseOverviewLod => _overviewLodActive && !_drawingPreview;
    internal int OverviewBuildingsDrawn { get; private set; }
    internal int OverviewTreesDrawn { get; private set; }
    internal bool OverviewLodActive => UseOverviewLod;
    internal bool OverviewLodEnabled { get => _overviewLodEnabled; init => _overviewLodEnabled = value; }

    internal static async Task VerifyOverviewLodTransitions(Main host, CacheExperiment experiment = CacheExperiment.OverviewLod)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 80, MapHeight = 48, Seed = 42 });
        game.Paused = true;
        var map = game.Map;
        var anchors = new[] { new Pos(16, 12), new Pos(28, 12), new Pos(16, 24), new Pos(28, 24) };
        var cottage = map.Content.Buildings.Get("Cottage");
        for (int index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            var area = new CellRect(anchor.X, anchor.Y, 6, 6);
            foreach (var cell in area.Cells())
            {
                map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get("Meadow"));
                map.SetFeature(cell.X, cell.Y, null);
                map.SetRoad(cell.X, cell.Y, false);
            }
            map.SetBuildingFootprint(cottage, area);
            map.BuildingRotations[map.Index(anchor.X, anchor.Y)] = index;
        }
        game.Touch();
        var session = new GameSession(game, showGuide: false);
        session.SetCameraEdgePadding(true);
        session.CenterOn(new(24, 20));
        var probe = new PerspectiveCity();
        host.AddChild(probe);
        try
        {
            probe.ConfigureCacheExperiment(experiment);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            await probe.AwaitCachePreparation();
            probe.ValidateGroundDrawPages();
            var detailed = probe.BuildingGeometry;
            var tokens = anchors.ToDictionary(anchor => anchor, probe.CachedObjectToken);
            string saved = SaveGameStore.Serialize(game);
            session.SetZoom(GameSession.MinZoom);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            if (!probe.OverviewLodActive || probe.BuildingGeometry.Count >= detailed.Count)
                throw new InvalidOperationException("Overview fixture did not generate fewer building parts directly.");
            probe.ValidateOverviewGeometry(session);
            probe.ValidateObjectBuffers();
            probe.ValidateGroundDrawPages();
            session.SetZoom(-1);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            if (probe.OverviewLodActive) throw new InvalidOperationException("Intermediate zoom incorrectly uses overview LOD.");
            session.SetZoom(0);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            probe.ValidateObjectBuffers();
            probe.ValidateGroundDrawPages();
            if (!detailed.SequenceEqual(probe.BuildingGeometry) ||
                anchors.Any(anchor => !ReferenceEquals(tokens[anchor], probe.CachedObjectToken(anchor))) ||
                saved != SaveGameStore.Serialize(game))
                throw new InvalidOperationException("Overview transitions failed exact detailed-cache reuse or save preservation.");
            GD.Print("TERMCITY_OVERVIEW_LOD_TRANSITIONS_OK rotations=4 intermediate=detailed return=exact-cache save=unchanged");
        }
        finally
        {
            host.RemoveChild(probe);
            probe.Free();
        }
    }
    internal void ValidateOverviewGeometry(GameSession session)
    {
        if (_overviewLodActive != (_overviewLodEnabled && session.ZoomLevel == GameSession.MinZoom))
            throw new InvalidOperationException("Overview LOD activated outside its discrete maximum zoom-out level.");
        if (!_overviewLodActive) return;
        int owners = 0;
        int roofHits = 0;
        foreach (var chunk in VisibleObjectData.Where(chunk => _requiredObjectChunks.Contains(chunk.Key)))
        {
            if (!chunk.Overview) throw new InvalidOperationException("A detailed chunk was published in the overview LOD scene.");
            foreach (var box in chunk.Geometry.Boxes)
            {
                var type = session.Game.Map.BuildingAt(box.Anchor.X, box.Anchor.Y);
                if (type is null || type.IsService || type.Zone is not
                    (ZoneType.Residential or ZoneType.Commercial or ZoneType.Industrial)) continue;
                if (!chunk.Geometry.Structures.TryGetValue(box.Anchor, out var picking) || !picking.Boxes.Contains(box.Bounds))
                    throw new InvalidOperationException("An overview building body, roof course or overhang is not represented by picking.");
                owners++;
            }
            foreach (var group in chunk.Geometry.Boxes.GroupBy(box => box.Anchor))
            {
                if (roofHits >= 16) break;
                var roof = group.OrderByDescending(box => box.Bounds.End.Y).First().Bounds;
                var point = ScreenPoint(new Vector3(roof.GetCenter().X, roof.End.Y, roof.GetCenter().Z));
                if (new Rect2(Vector2.Zero, DisplaySize).HasPoint(point) &&
                    PickBuilding(point, out var picked) && picked == group.Key) roofHits++;
            }
        }
        if (owners > 0 && roofHits == 0)
            throw new InvalidOperationException("Overview LOD picking did not select any directly visible building roofs.");
        ValidateDetailedOverviewPreview(session);
        GD.Print($"TERMCITY_OVERVIEW_LOD_GEOMETRY_OK picked-volumes={owners} roof-hits={roofHits} " +
            $"roof-overhangs=complete previews=detailed zoom={session.ZoomLevel}");
    }

    private void ValidateDetailedOverviewPreview(GameSession session)
    {
        var map = session.Game.Map;
        var subject = VisibleObjectData.SelectMany(chunk => chunk.Geometry.Structures.Keys)
            .FirstOrDefault(owner => map.BuildingAt(owner.X, owner.Y)?.Zone == ZoneType.Residential, new Pos(-1, -1));
        if (!map.InBounds(subject)) return;
        if (_objectCapture is not null || _drawingPreview)
            throw new InvalidOperationException("Preview verification cannot interrupt an active artwork capture.");
        var preview = new ObjectGeometry();
        int buildings = BuildingsDrawn, previews = PreviewBuildingsDrawn, lodBuildings = OverviewBuildingsDrawn;
        _objectCapture = preview;
        _drawingPreview = true;
        try
        {
            Building(map, map.BuildingFootprintAt(subject.X, subject.Y), map.BuildingAt(subject.X, subject.Y)!);
            if (UseOverviewLod || OverviewBuildingsDrawn != lodBuildings || preview.Instances.Count == 0)
                throw new InvalidOperationException("Construction preview used simplified overview artwork.");
        }
        finally
        {
            _drawingPreview = false;
            _objectCapture = null;
            BuildingsDrawn = buildings;
            PreviewBuildingsDrawn = previews;
        }
        GD.Print($"TERMCITY_OVERVIEW_PREVIEW_OK instances={preview.Instances.Values.Sum(parts => parts.Count)} detail=full");
    }
    private void RetainAlternateLod(ObjectChunk chunk)
    {
        if (chunk.Overview == _overviewLodActive)
        {
            DisposeObjectGeometry(chunk);
            return;
        }
        var key = (chunk.Key, chunk.Overview);
        if (_alternateLodChunks.Remove(key, out var previous) && previous != chunk)
            DisposeObjectGeometry(previous);
        _alternateLodChunks[key] = chunk;
    }

    private void UpdateOverviewLod(GameSession session)
    {
        bool active = _overviewLodEnabled && session.ZoomLevel == GameSession.MinZoom;
        if (_overviewLodActive == active) return;
        _overviewLodActive = active;
        InvalidateStaticScene(discardBuildingCache: false);
    }

    private void OverviewHouse(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        var look = HouseAppearance.At(type, anchor.X, anchor.Y);
        foreach (var part in look.BaseParts(area.Width, area.Height, type.Level))
            Box(center + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor);
        var blocks = look.BlocksOnPlot(area.Width, area.Height, type.Level);
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var origin = center + new Vector3(block.X, 0, block.Z);
            Box(origin + Vector3.Up * block.Height / 2, new(block.Width, block.Height, block.Depth),
                look.PlasterColor, anchor);
            for (int step = 0; step < block.RoofSteps; step++)
            {
                var course = block.RoofCourse(step);
                float relief = DepthStudyArtwork.UsesRelief(DepthTreatment) ? DepthStudyArtwork.RoofReliefScale : 1;
                Box(origin + new Vector3(course.Center.X, course.Center.Y, course.Center.Z),
                    new(course.Size.X * relief, course.Size.Y, course.Size.Z * relief),
                    step == block.RoofSteps - 1 ? look.RoofCapColor :
                        step % 2 == 0 ? look.RoofBlockColor : look.RoofBlockShadeColor, anchor);
            }
            if (index == 0 && look.Chimney)
                Box(origin + new Vector3(-block.Width * .25f, block.Height + block.RoofRise - .04f, -block.Depth * .15f),
                    new(.12f, .24f, .12f), HouseAppearance.StoneColor, anchor);
        }
        OverviewBuildingsDrawn++;
    }

    private void OverviewWorkplace(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        var look = WorkplaceAppearance.At(type, anchor.X, anchor.Y, area.Width, area.Height);
        Box(center + Vector3.Up * .025f, new(look.Width + .06f, .05f, look.Depth + .10f),
            HouseAppearance.StoneColor, anchor);
        var body = center + new Vector3(0, 0, -.06f);
        if (look.Form == WorkplaceForm.Stall)
        {
            var back = body + new Vector3(0, 0, -look.Depth * .3f);
            Box(back + Vector3.Up * .17f, new(look.Width * .8f, .34f, look.Depth * .3f), look.WallColor, anchor);
            Gable(back, look.Width * .8f, look.Depth * .3f, .34f, .13f);
            Box(center + new Vector3(0, .39f, look.Depth * .12f),
                new(look.Width, .04f, look.Depth * .6f), look.CanvasColor, anchor);
            Box(center + new Vector3(0, .17f, look.Depth * .3f),
                new(look.Width * .82f, .18f, .12f), HouseAppearance.TimberColor, anchor);
        }
        else
        {
            float depth = Math.Max(.45f, look.Depth * .72f);
            Box(body + Vector3.Up * look.Height / 2, new(look.Width, look.Height, depth), look.WallColor, anchor);
            Gable(body, look.Width, depth, look.Height, look.RoofRise);
            if (look.Form is WorkplaceForm.Smithy or WorkplaceForm.Forge)
                Box(body + new Vector3(-look.Width * .25f, look.Height + look.RoofRise, -depth * .15f),
                    new(.14f, .28f, .14f), HouseAppearance.StoneColor, anchor);
            if (look.Form == WorkplaceForm.Mill)
            {
                float radius = area.Area > 1 ? .21f : .17f;
                var wheel = body + new Vector3(look.Width / 2 + .055f, radius + .05f, .02f);
                Box(wheel, new(.13f, .055f, radius * 1.7f), HouseAppearance.TimberColor, anchor);
                Box(wheel, new(.13f, radius * 1.7f, .055f), HouseAppearance.TimberColor, anchor);
                for (int spoke = 0; spoke < 8; spoke++)
                {
                    float angle = spoke * Mathf.Tau / 8;
                    Box(wheel + new Vector3(0, Mathf.Sin(angle) * radius, Mathf.Cos(angle) * radius),
                        new(.12f, .08f, .08f), 0x8b7252, anchor);
                }
            }
        }
        OverviewBuildingsDrawn++;

        void Gable(Vector3 at, float width, float depth, float height, float rise)
        {
            var transform = _buildingTransform;
            if (look.CrosswiseRoof)
            {
                var basis = new Basis(Vector3.Back, Vector3.Up, Vector3.Left);
                _buildingTransform *= new Transform3D(basis, at - basis * at);
            }
            try
            {
                Roof(at + Vector3.Up * height, (look.CrosswiseRoof ? depth : width) + .18f,
                    (look.CrosswiseRoof ? width : depth) + .18f, rise, look.RoofColor, anchor, look.WallColor);
            }
            finally { _buildingTransform = transform; }
        }
    }
}
