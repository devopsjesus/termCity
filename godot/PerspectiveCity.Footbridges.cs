using Godot;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly List<Vector3> _footbridgeFaces = [];
    internal int CivilianFootbridgesDrawn { get; private set; }

    private void DrawCivilianFootbridges(GameMap map)
    {
        _footbridgeFaces.Clear();
        CivilianFootbridgesDrawn = 0;
        if (map.Footbridges.Count == 0) return;
        using var cubeArrays = _cube.GetMeshArrays();
        var cubeVertices = cubeArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var cubeIndices = cubeArrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        foreach (var bridge in map.Footbridges)
        {
            var cells = bridge.Cells().ToArray();
            if (!cells.Any(p => _view.Contains(p))) continue;
            float height = bridge.DeckHeight(map);
            var direction = new Vector3(Math.Sign(bridge.End.X - bridge.Start.X), 0,
                Math.Sign(bridge.End.Y - bridge.Start.Y));
            var across = new Vector3(direction.Z, 0, -direction.X);
            var basis = new Basis(across, Vector3.Up, direction);
            int visible = bridge.Complete ? cells.Length :
                Math.Max(1, (int)Math.Ceiling(cells.Length * bridge.Progress / (double)SettlementFootbridge.ConstructionDays));
            for (int i = 0; i < visible; i++)
            {
                var p = cells[i];
                var at = new Vector3(p.X + .5f, height, p.Y + .5f);
                void Part(Vector3 offset, Vector3 size, int color)
                {
                    var transform = new Transform3D(basis * Basis.Identity.Scaled(size), at + basis * offset);
                    BatchBox(color, transform);
                    if (bridge.Complete)
                        foreach (int index in cubeIndices) _footbridgeFaces.Add(transform * cubeVertices[index]);
                }
                for (int plank = 0; plank < 5; plank++)
                    Part(new(0, -.055f, (plank - 2) * .2f), new(.82f, .11f, .185f), 0x967a56);
                foreach (int side in new[] { -1, 1 })
                {
                    Part(new(side * .30f, -.15f, 0), new(.12f, .18f, 1), 0x594a35);
                    Part(new(side * .38f, .38f, 0), new(.055f, .055f, 1), 0x79634b);
                    Part(new(side * .38f, .55f, 0), new(.055f, .055f, 1), 0x79634b);
                    Part(new(side * .38f, .25f, -.4f), new(.07f, .6f, .07f), 0x79634b);
                    Part(new(side * .38f, .575f, -.4f), new(.12f, .05f, .12f), 0x967a56);
                    if (i % 2 == 0)
                    {
                        float bottom = TermCity.Core.Rendering.WallWaterLayout.Surface(map, new(p.X + .5f, p.Y + .5f)) - .6f;
                        Part(new(side * .3f, -(height - bottom) / 2, 0), new(.11f, height - bottom, .11f), 0x594a35);
                    }
                }
            }
            if (!bridge.Complete)
            {
                var p = cells[Math.Max(0, visible - 1)];
                var at = new Vector3(p.X + .5f, height, p.Y + .5f);
                Box(at + Vector3.Up * .65f, new(.38f, .6f, .28f), 0x79634b, pick: false);
                Box(at + Vector3.Up * 1.1f, new(.27f, .27f, .27f), 0xc6bf9c, pick: false);
                foreach (int side in new[] { -1, 1 })
                    Box(at + across * (side * .11f) + Vector3.Up * .22f, new(.12f, .44f, .14f), 0x443d32, pick: false);
                Box(at + direction * .3f + Vector3.Up * .6f, new(.5f, .07f, .07f), 0xa4a599, pick: false);
            }
            CivilianFootbridgesDrawn++;
        }
    }
}
