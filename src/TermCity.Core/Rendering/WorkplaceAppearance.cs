using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public enum WorkplaceForm { Stall, Shop, MarketHall, Smithy, Carpenter, Cooper, Mill, Forge, Armory, SupplyShop }

public readonly record struct WorkplaceAppearance(WorkplaceForm Form, float Width, float Depth,
    float Height, float RoofRise, int WallColor, int RoofColor, int CanvasColor, bool CrosswiseRoof)
{
    public static WorkplaceAppearance At(BuildingType type, int x, int y, int plotWidth, int plotDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plotWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plotDepth);
        if (type.Zone is not (ZoneType.Commercial or ZoneType.Industrial))
            throw new ArgumentException("A workplace requires a commercial or industrial zone.", nameof(type));
        int variant = CellHash.Pick(x + 47, y - 13, 3);
        var palette = HouseAppearance.At(x + 3, y + 11, type.Level);
        var form = type.Name switch
        {
            "Blacksmith" => WorkplaceForm.Smithy,
            "Woodsmith" => WorkplaceForm.Carpenter,
            "Armory" => WorkplaceForm.Armory,
            "Supply Shop" => WorkplaceForm.SupplyShop,
            "Market" => WorkplaceForm.Stall,
            _ => type.Zone == ZoneType.Commercial
            ? type.Level switch { 1 => WorkplaceForm.Stall, 2 => WorkplaceForm.Shop, _ => WorkplaceForm.MarketHall }
            : type.Name == "Mill" || type.Level == 2 ? WorkplaceForm.Mill
            : type.Level >= 3 ? WorkplaceForm.Forge
            : variant switch { 0 => WorkplaceForm.Smithy, 1 => WorkplaceForm.Carpenter, _ => WorkplaceForm.Cooper },
        };
        float height = form switch
        {
            WorkplaceForm.Stall => 0.38f, WorkplaceForm.Shop => 0.48f,
            WorkplaceForm.MarketHall => 0.56f, WorkplaceForm.Mill => 0.5f,
            WorkplaceForm.Forge => 0.48f, _ => 0.4f,
        };
        return new(form, plotWidth * (variant == 0 ? 0.66f : 0.74f),
            plotDepth * (variant == 1 ? 0.62f : 0.7f), height,
            form == WorkplaceForm.Stall ? 0.08f : variant == 2 ? 0.27f : 0.22f,
            form is WorkplaceForm.Smithy or WorkplaceForm.Forge ? HouseAppearance.StoneColor : palette.WallColor,
            palette.RoofColor, variant switch { 0 => 0x63818a, 1 => 0x996454, _ => 0x78805b },
            variant == 1);
    }
}
