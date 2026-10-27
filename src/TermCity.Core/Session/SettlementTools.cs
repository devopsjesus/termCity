using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private Pos? _settlementAnchor;
    public BaileyGateSide SettlementGateSide { get; set; } = BaileyGateSide.Right;

    public void BeginSettlementPlacement()
    {
        if (Game.Settlement is not null)
        {
            SetMessage("A bailey is already founded. Use Garrison to mount defenses, repair or upgrade its walls.", MessageKind.Error);
            return;
        }
        var hall = Game.Map.Content.Buildings.FirstOrDefault(b => b.Name == "Motte and Bailey");
        if (hall is null)
        {
            SetMessage("Motte and Bailey is not registered in this world's building catalog.", MessageKind.Error);
            return;
        }
        if (BuildingPurchaseError(hall) is { } error)
        {
            SetMessage(error, MessageKind.Error);
            return;
        }
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        HasCursor = true;
        Anchor = null;
        Selection = null;
        _settlementAnchor = null;
        RefreshSettlementPlacement();
    }

    private void MoveSettlementPlacement(Pos position)
    {
        Cursor = Clamp(position);
        RefreshSettlementPlacement();
        FollowCursor();
    }

    private void RefreshSettlementPlacement()
    {
        var bounds = CellRect.FromCorners(_settlementAnchor ?? Cursor, Cursor);
        var walls = bounds.Cells().Where(p =>
            p.X == bounds.X || p.X == bounds.Right || p.Y == bounds.Y || p.Y == bounds.Bottom).ToHashSet();
        bool valid = _settlementAnchor is not null && Game.SettlementPlacementError(bounds) is null;
        Preview = new(PlacementKind.Settlement, bounds, Game.QuoteSettlement(bounds), "Motte and Bailey enclosure",
            Cells: walls, ValidCells: valid ? walls : []);
        Changed?.Invoke();
    }

    public string? WallDefensePurchaseError(WallDefenseKind kind)
    {
        var definition = WallDefenses.Get(kind);
        if (Game.Settlement is null) return "Found a Motte and Bailey before mounting defenses.";
        if (Math.Max(Game.HighestPopulation, Game.HighestMilestone) < definition.RequiredPopulation)
            return $"{definition.Name} unlocks at {definition.RequiredPopulation:N0} souls.";
        return Game.Money < definition.Cost ? $"{definition.Name} costs {Fmt.Money(definition.Cost)}." : null;
    }

    public ActionResult UpgradeWalls() => Execute(Game.UpgradeSettlementWalls);
    public ActionResult RepairWalls() => Execute(Game.RepairSettlementWalls);

    public void PreviewWallDefense(WallDefenseKind kind)
    {
        if (WallDefensePurchaseError(kind) is { } error)
        {
            SetMessage(error, MessageKind.Error);
            return;
        }
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        HasCursor = true;
        Anchor = null;
        Selection = null;
        RefreshWallDefensePlacement(kind);
    }

    private void MoveWallDefensePlacement(Pos position)
    {
        Cursor = Clamp(position);
        RefreshWallDefensePlacement(Preview!.Defense);
        FollowCursor();
    }

    private void RefreshWallDefensePlacement(WallDefenseKind kind)
    {
        var definition = WallDefenses.Get(kind);
        var cells = new HashSet<Pos> { Cursor };
        bool valid = Game.WallDefensePlacementError(Cursor, kind) is null;
        Preview = new(PlacementKind.WallDefense, CellRect.Single(Cursor),
            new(valid ? 1 : 0, definition.Cost, valid ? 0 : 1), definition.Name,
            Cells: cells, ValidCells: valid ? cells : [], Defense: kind);
        Changed?.Invoke();
    }

    public string? WallStatus(Pos cell)
    {
        var wall = Game.Settlement?.Walls.FirstOrDefault(w => w.Cell == cell);
        if (wall is null) return null;
        string name = wall.IsGate ? "Gate" : wall.IsTower ? "Arrow tower" : $"Wall ({wall.SectionLength} cells)";
        return $"{name}: {wall.Health:0}/{wall.MaxHealth:0} health; {Game.Settlement!.MaterialFor(wall)}" +
            (Game.Settlement.UpgradeTargetMaterial is { } target ? $"; upgrading to {target}: {Game.Settlement.OverallUpgradeProgress:P0}" : "") +
            (wall.Defense == WallDefenseKind.None ? "" : $"; {WallDefenses.Get(wall.Defense).Name}");
    }

    public void ShowGarrisonMenu()
    {
        var choices = new List<SessionChoice>
        {
            new("Motte and Bailey enclosure", () =>
            {
                BeginSettlementPlacement();
                if (SettlementToolActive) ClosePrompt();
            }, ["Found", "Two corners; automatic walls, towers, gate, homes and jobs"]),
        };
        foreach (var kind in Enum.GetValues<WallDefenseKind>().Where(k => k != WallDefenseKind.None))
        {
            var definition = WallDefenses.Get(kind);
            bool unlocked = Math.Max(Game.HighestMilestone, Game.HighestPopulation) >= definition.RequiredPopulation;
            choices.Add(new(definition.Name, () =>
            {
                PreviewWallDefense(kind);
                if (WallDefenseToolActive) ClosePrompt();
            }, [Fmt.Money(definition.Cost), unlocked
                ? $"Damage {definition.Damage}; splash {definition.SplashRadius}; range {definition.MinimumRange}-{definition.Range}"
                : $"LOCKED: {definition.RequiredPopulation:N0} souls"],
                ChoiceHelpContent.WallDefense(kind)));
        }
        choices.Add(new("Upgrade all walls", () => UpgradeWalls(),
            ["Upgrade", "Wood -> stone (250) -> iron-reinforced stone (1,000 souls)"]));
        choices.Add(new("Repair all walls", () => RepairWalls(),
            ["Repair", "Restore damaged walls, towers and gate for gold"]));
        foreach (var building in Game.Map.Content.Buildings.Where(b =>
            b.PlayerPlaceable && b.Service == ServiceKind.Defence && b.Name != "Motte and Bailey"))
            choices.Add(new(building.Name, () =>
            {
                PreviewBuilding(building);
                if (BuildingToolActive) ClosePrompt();
            }, [Fmt.Money(building.Cost), $"Lord's seat; unlock {CityProgression.RequiredPopulation(building):N0} souls"]));
        choices.Add(new("Back", CancelPrompt, ["", "Return"]));
        var settlement = Game.Settlement;
        string status = settlement is null ? "Found a rectangular bailey on open land. No roads or manual zoning are required."
            : $"{settlement.Material} walls; {settlement.WallSections.Count(w => w.Health > 0)}/{settlement.WallSections.Count()} sections intact. " +
                (settlement.UpgradeTargetMaterial is { } target ? $"Upgrading to {target}: {settlement.OverallUpgradeProgress:P0}. " : "") +
                $"Wave {settlement.WaveNumber}; {settlement.Goblins.Count} goblins; next wave in {settlement.NextWaveSeconds:0}s.";
        ShowPrompt("Garrison", status + "\nTowers fire arrows automatically. Goblin attacks begin at Hamlet (100 souls).",
            choices, columns: ["COMPONENT", "COST / ACTION", "CAPABILITY"]);
    }

    public void ShowWallStatus(Pos cell)
    {
        ShowPrompt("Wall inspection", WallStatus(cell) ?? "No wall at this cell.",
            [new("Garrison", ShowGarrisonMenu), new("Back", CancelPrompt)]);
    }
}
