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
        var basis = WallBasis(wall);
        foreach (var block in WallUpgradeArtwork.Blocks(settlement, wall))
        {
            var size = new Vector3(block.Size.X, block.Size.Y, block.Size.Z);
            var center = origin + basis * new Vector3(block.Center.X, block.Center.Y, block.Center.Z);
            var transform = SiegeBox(wall.IsOriginal ? batches : _extensionWallBatches, center, size, block.Color, orientation: basis);
            RecordWallTop(transform, block.Color, !wall.IsOriginal);
            _wallHighlightBoxes[wall.Id].Add(transform);
            var worldSize = basis.X.Abs() * size.X + basis.Y.Abs() * size.Y + basis.Z.Abs() * size.Z;
            _siegePickTransforms[_siegePickBoxes.Count] = transform;
            _siegePickWallIds[_siegePickBoxes.Count] = wall.Id;
            _siegePickBoxes.Add((new Aabb(center - worldSize / 2, worldSize), wall.Cell, block.Color));
        }
    }
}
