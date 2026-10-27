using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private void DrawWallUpgrade(Dictionary<int, List<Transform3D>> batches, SettlementState settlement,
        WallComponent wall)
    {
        var top = WallTop(wall);
        var origin = new Vector3(top.X, top.Y - WallHeight(wall), top.Z);
        bool horizontal = wall.Cell.Y == settlement.Bounds.Y || wall.Cell.Y == settlement.Bounds.Bottom;
        var basis = horizontal ? Basis.Identity : new Basis(Vector3.Up, Mathf.Pi / 2);
        foreach (var block in WallUpgradeArtwork.Blocks(settlement, wall))
        {
            var size = new Vector3(block.Size.X, block.Size.Y, block.Size.Z);
            var center = origin + basis * new Vector3(block.Center.X, block.Center.Y, block.Center.Z);
            var transform = SiegeBox(batches, center, size, block.Color, orientation: basis);
            _wallHighlightBoxes[wall.Cell].Add(transform);
            var worldSize = basis.X.Abs() * size.X + basis.Y.Abs() * size.Y + basis.Z.Abs() * size.Z;
            _siegePickBoxes.Add((new Aabb(center - worldSize / 2, worldSize), wall.Cell, block.Color));
        }
    }
}
