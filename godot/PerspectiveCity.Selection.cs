using Godot;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly List<(Transform3D Transform, Pos Anchor)> _sceneHighlights = [];
    private List<(Transform3D Transform, Pos Anchor)> _structureHighlightBoxes =>
        _objectCapture?.Highlights ?? _sceneHighlights;
    private readonly Dictionary<int, List<Transform3D>> _wallHighlightBoxes = [];
    private int? _highlightWallId;
    private readonly StandardMaterial3D _selectionHighlightMaterial = new()
    {
        AlbedoColor = new Color(1, 0.94f, 0.68f, 0.19f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Grow = true,
        GrowAmount = 0.006f,
        RenderPriority = 1,
    };
    private readonly MultiMeshInstance3D _selectionHighlightBoxes = new();
    private readonly MeshInstance3D _selectionHighlightRoof = new();
    private int _highlightGeometryVersion = -1;
    private bool _highlightIsWall;
    internal Pos? HighlightedStructure { get; private set; }
    internal int HighlightedBoxCount => _selectionHighlightBoxes.Multimesh?.InstanceCount ?? 0;
    internal int HighlightedRoofTriangleCount { get; private set; }
    internal int SelectionHighlightBuilds { get; private set; }
    internal Aabb? SelectedObjectBounds { get; private set; }
    internal Rect2? SelectionVisualFootprint { get; private set; }
    private bool _selectedMountedDefense;
    private int _selectedDefensePose, _highlightDefensePose;
    private IReadOnlyList<Transform3D> _selectedDefenseTransforms = [];

    private bool SyncWallSelectionBounds(GameSession session, WallComponent? wall)
    {
        var transforms = new List<Transform3D>();
        bool mounted = false;
        if (wall is not null)
        {
            var siege = session.Game.Settlement!;
            var owner = session.WallDefenseToolActive || wall.Defense != WallDefenseKind.None ? wall :
                siege.Walls.First(member => member.Id == wall.SectionId);
            var kind = session.WallDefenseToolActive ? session.Preview!.Defense : owner.Defense;
            if (kind != WallDefenseKind.None && (owner.Health > 0 || session.WallDefenseToolActive))
            {
                var batches = new Dictionary<int, List<Transform3D>>();
                DrawMountedDefense(batches, MountedDefenseOrigin(owner, kind), kind, owner, animate: !session.WallDefenseToolActive,
                    facing: session.WallDefenseToolActive ? session.Preview!.Rotation == 2 ? -1 : 1 : null);
                foreach (var batch in batches.Values) transforms.AddRange(batch);
                mounted = true;
            }
            else
                foreach (var member in siege.Walls.Where(member => member.SectionId == owner.SectionId))
                    if (_wallHighlightBoxes.TryGetValue(member.Id, out var boxes)) transforms.AddRange(boxes);
        }
        Aabb? bounds = null;
        foreach (var transform in transforms)
        {
            var size = transform.Basis.X.Abs() + transform.Basis.Y.Abs() + transform.Basis.Z.Abs();
            var box = new Aabb(transform.Origin - size / 2, size);
            bounds = bounds is { } previous ? previous.Merge(box) : box;
        }
        bool changed = SelectedObjectBounds != bounds || _selectedMountedDefense != mounted;
        SelectedObjectBounds = bounds;
        _selectedMountedDefense = mounted;
        _selectedDefenseTransforms = mounted ? transforms : [];
        var pose = new HashCode();
        foreach (var transform in _selectedDefenseTransforms) pose.Add(transform);
        _selectedDefensePose = pose.ToHashCode();
        return changed;
    }

    private void OutlineSelectedObject(GameSession session, CellRect logicalArea, int color)
    {
        if (SelectedObjectBounds is not { } bounds)
        {
            Outline(session.Game.Map, logicalArea, color, _selectedWallTop);
            return;
        }
        float padding = Math.Max(0.05f, _indicatorThickness);
        float width = Math.Max(bounds.Size.X, _selectedMountedDefense ? 1 : logicalArea.Width);
        float depth = Math.Max(bounds.Size.Z, _selectedMountedDefense ? 1 : logicalArea.Height);
        var center = bounds.GetCenter();
        var footprint = new Rect2(center.X - width / 2 - padding, center.Z - depth / 2 - padding,
            width + padding * 2, depth + padding * 2);
        SelectionVisualFootprint = footprint;
        float height = _selectedMountedDefense ? bounds.Position.Y + Math.Max(0.02f, _indicatorThickness / 2 + 0.005f) :
            bounds.End.Y + _indicatorThickness + 0.025f;
        Outline(session.Game.Map, footprint, color, height);
    }

    private void InitializeSelectionHighlight()
    {
        _selectionHighlightBoxes.Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _cube,
        };
        _selectionHighlightBoxes.MaterialOverride = _selectionHighlightMaterial;
        _selectionHighlightRoof.MaterialOverride = _selectionHighlightMaterial;
        _selectionHighlightBoxes.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _selectionHighlightRoof.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _viewport.AddChild(_selectionHighlightBoxes);
        _viewport.AddChild(_selectionHighlightRoof);
    }

    private void SyncSelectionHighlight(GameSession session)
    {
        bool wall = _selection is not null && _selectedWallTop is not null;
        Pos? selected = session.FixedPlacementToolActive || session.Selection is null || _selection is null ? null :
            wall ? session.SelectedWallComponent?.SectionAnchor : _selectedPropertyAnchor;
        int? wallId = wall ? session.SelectedWallComponent?.SectionId : null;
        int geometry = wall ? SiegeGeometryBuilds : GeometryBuilds;
        if (selected == HighlightedStructure && wallId == _highlightWallId && wall == _highlightIsWall && geometry == _highlightGeometryVersion &&
            _selectedDefensePose == _highlightDefensePose) return;
        HighlightedStructure = selected;
        _highlightWallId = wallId;
        _highlightIsWall = wall;
        _highlightGeometryVersion = geometry;
        _highlightDefensePose = _selectedDefensePose;
        SelectionHighlightBuilds++;
        var transforms = new List<Transform3D>();
        if (selected is { } anchor)
        {
            if (wall)
            {
                if (_selectedMountedDefense) transforms.AddRange(_selectedDefenseTransforms);
                else
                    foreach (var member in session.Game.Settlement!.Walls.Where(w => w.SectionId == wallId))
                        if (_wallHighlightBoxes.TryGetValue(member.Id, out var boxes)) transforms.AddRange(boxes);
            }
            else
                foreach (var box in SceneHighlights)
                    if (box.Anchor == anchor) transforms.Add(box.Transform);
        }
        var mesh = _selectionHighlightBoxes.Multimesh;
        if (mesh.InstanceCount != transforms.Count) mesh.InstanceCount = transforms.Count;
        for (int i = 0; i < transforms.Count; i++) mesh.SetInstanceTransform(i, transforms[i]);
        _selectionHighlightRoof.Mesh?.Dispose();
        _selectionHighlightRoof.Mesh = null;
        HighlightedRoofTriangleCount = 0;
        if (selected is not { } roofAnchor || wall) return;
        using var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var face in SceneRoofFaces)
        {
            if (face.Anchor != roofAnchor) continue;
            surface.SetNormal((face.B - face.A).Cross(face.C - face.A).Normalized());
            surface.AddVertex(face.A); surface.AddVertex(face.B); surface.AddVertex(face.C);
            HighlightedRoofTriangleCount++;
        }
        if (HighlightedRoofTriangleCount > 0) _selectionHighlightRoof.Mesh = surface.Commit();
    }

    private void DisposeSelectionHighlight()
    {
        _selectionHighlightBoxes.Multimesh.Dispose();
        _selectionHighlightRoof.Mesh?.Dispose();
        _selectionHighlightMaterial.Dispose();
    }
}
