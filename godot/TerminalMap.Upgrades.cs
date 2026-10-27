using Godot;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class TerminalMap
{
    private void DrawWallUpgradeOverlay(SettlementState settlement, WallComponent wall, Vector2 center,
        Vector2 size, bool horizontal)
    {
        double progress = settlement.ProgressFor(wall);
        if (PerspectiveEnabled || wall.Health <= 0 || progress <= 0 || progress >= 1) return;
        var laidSize = horizontal ? new Vector2(size.X * (float)progress, size.Y) :
            new Vector2(size.X, size.Y * (float)progress);
        var color = settlement.UpgradeTargetMaterial == WallMaterial.Stone ?
            new Color("#a4a599") : new Color("#586b71");
        DrawRect(new(center - size / 2, laidSize), color);
    }
}
