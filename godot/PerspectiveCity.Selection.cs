using Godot;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly List<(Transform3D Transform, Pos Anchor)> _structureHighlightBoxes = [];
    private readonly Dictionary<Pos, List<Transform3D>> _wallHighlightBoxes = [];
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
            wall ? session.Game.Settlement?.Walls.First(w => w.Cell == session.Cursor).SectionAnchor : _selectedPropertyAnchor;
        int geometry = wall ? SiegeGeometryBuilds : GeometryBuilds;
        if (selected == HighlightedStructure && wall == _highlightIsWall && geometry == _highlightGeometryVersion) return;
        HighlightedStructure = selected;
        _highlightIsWall = wall;
        _highlightGeometryVersion = geometry;
        SelectionHighlightBuilds++;
        var transforms = new List<Transform3D>();
        if (selected is { } anchor)
        {
            if (wall)
            {
                foreach (var member in session.Game.Settlement!.Walls.Where(w => w.SectionAnchor == anchor))
                    if (_wallHighlightBoxes.TryGetValue(member.Cell, out var boxes)) transforms.AddRange(boxes);
            }
            else
                foreach (var box in _structureHighlightBoxes)
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
        foreach (var face in _roofFaces)
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
