using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private const int RiverSiteLimit = 24;
    private readonly List<Pos> _riverSiteCandidates = [];
    private readonly List<Pos> _riverSites = [];
    private MultiMesh? _riverInstances;
    private readonly StandardMaterial3D _riverMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true
    };
    internal IReadOnlyList<Pos> RiverActivitySites => _riverSites;
    internal int RiverFishDrawn { get; private set; }
    internal int RiverSplashesDrawn { get; private set; }

    private void InitializeRiverActivity()
    {
        _riverInstances = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = _cube,
            InstanceCount = RiverSiteLimit * 12,
            VisibleInstanceCount = 0
        };
        _viewport.AddChild(new MultiMeshInstance3D { Multimesh = _riverInstances, MaterialOverride = _riverMaterial });
    }

    private void CacheRiverActivitySites()
    {
        _riverSiteCandidates.Clear();
        if (_map is not { } map) return;
        var flow = TerrainSprite.Flow(map);
        for (int row = _view.Y / 8 * 8; row <= _view.Bottom; row += 8)
            for (int column = _view.X / 8 * 8; column <= _view.Right; column += 8)
            {
                Pos? best = null;
                int rank = int.MaxValue;
                for (int y = Math.Max(row, _view.Y); y < Math.Min(row + 8, _view.Bottom + 1); y++)
                    for (int x = Math.Max(column, _view.X); x < Math.Min(column + 8, _view.Right + 1); x++)
                    {
                        if (!flow.At(x, y).River || _roadLayout?.InCorridor(new(x + 0.5f, y + 0.5f)) == true) continue;
                        int value = CellHash.Pick(x, y, 10000);
                        if (value >= rank) continue;
                        best = new(x, y);
                        rank = value;
                    }
                if (best is { } site) _riverSiteCandidates.Add(site);
            }
    }

    private void SelectRiverActivitySites()
    {
        _riverSites.Clear();
        if (_map is not { } map) return;
        var flow = TerrainSprite.Flow(map);
        var visible = new Rect2(Vector2.Zero, DisplaySize);
        foreach (var site in _riverSiteCandidates.Select(site => (Site: site, Screen: ScreenPoint(
            new(site.X + 0.5f, flow.At(site.X, site.Y).Level, site.Y + 0.5f))))
            .Where(candidate => visible.HasPoint(candidate.Screen))
            .OrderBy(candidate => candidate.Screen.DistanceSquaredTo(DisplaySize / 2)).Take(RiverSiteLimit))
            _riverSites.Add(site.Site);
    }

    private void UpdateRiverActivity()
    {
        RiverFishDrawn = RiverSplashesDrawn = 0;
        if (_riverInstances is not { } instances || _map is not { } map) return;
        int count = 0;
        if (_waterAnimating)
            foreach (var site in _riverSites)
            {
                if (RiverActivityLayout.At(site, WaterFlowRenderClock) is not { } activity) continue;
                var current = TerrainSprite.Flow(map).At(site.X, site.Y);
                var direction = new Vector3(current.Direction.X, 0, current.Direction.Y);
                var center = new Vector3(activity.Position.X, current.Level + 0.025f, activity.Position.Y);
                float progress = activity.Progress;
                if (activity.Fish && progress < 0.65f)
                {
                    float leap = progress / 0.65f;
                    var position = center + direction * ((leap - 0.5f) * 0.32f) +
                        Vector3.Up * (4 * leap * (1 - leap) * 0.55f);
                    var basis = Basis.LookingAt(direction + Vector3.Up * ((0.5f - leap) * 2), Vector3.Up);
                    Cube(position, new(0.13f, 0.13f, 0.32f), new Color("#8d9d86"), basis);
                    Cube(position + basis.Z * 0.20f, new(0.19f, 0.05f, 0.10f), new Color("#647c74"), basis);
                    RiverFishDrawn++;
                }
                else
                {
                    float splash = activity.Fish ? (progress - 0.65f) / 0.35f : progress;
                    if (activity.Fish) center += direction * 0.16f;
                    float radius = 0.06f + splash * 0.25f;
                    for (int drop = 0; drop < 6; drop++)
                    {
                        float angle = drop * Mathf.Tau / 6;
                        var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                        float size = 0.075f * (1 - splash * 0.6f);
                        Cube(center + offset, new(size, 0.022f, size), new Color("#579ab0"), Basis.Identity);
                        if (splash < 0.65f && drop % 2 == 0)
                            Cube(center + offset * 0.7f + Vector3.Up * (Mathf.Sin(splash * Mathf.Pi) * 0.22f),
                                new(0.055f, 0.09f, 0.055f), new Color("#78afbc"), Basis.Identity);
                    }
                    RiverSplashesDrawn++;
                }
            }
        instances.VisibleInstanceCount = count;

        void Cube(Vector3 position, Vector3 size, Color color, Basis basis)
        {
            instances.SetInstanceTransform(count, new(basis * Basis.FromScale(size), position));
            instances.SetInstanceColor(count++, color);
        }
    }
}
