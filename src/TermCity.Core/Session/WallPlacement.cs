using System.Numerics;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private Vector2? _wallAnchor;
    private float _gateAngle;
    private bool _gateAngleChosen;
    private PlacementKind _wallKind;
    private int? _pickedWallId;
    private CityGame? _pickedWallGame;
    private CityGame? _wallPreviewGame;
    private int _wallPreviewRevision, _wallPreviewMapVersion, _wallPreviewMoney;
    public double LastWallPreviewMilliseconds { get; private set; }
    public WallComponent? WallPlacementStartTower => Preview?.Kind == PlacementKind.WallRun && _wallAnchor is { } anchor
        ? Game.Settlement?.Walls.FirstOrDefault(wall => wall.IsTower &&
            Vector2.DistanceSquared(wall.Center, anchor) <= World.WallNetwork.Tolerance * World.WallNetwork.Tolerance)
        : null;
    public WallComponent? SelectedWallComponent => ReferenceEquals(_pickedWallGame, Game) && _pickedWallId is { } id &&
        Game.Settlement?.Walls.FirstOrDefault(w => w.Id == id && w.Cell == Cursor) is { } picked ? picked :
        Game.Settlement?.Walls.FirstOrDefault(w => w.Cell == Cursor && (w.IsOriginal ||
            Rendering.WallDamageLayout.ContainsFootprint(w, Cursor.X + .5, Cursor.Y + .5)));

    public void RememberWallPick(int? wallId)
    {
        _pickedWallId = wallId;
        _pickedWallGame = Game;
    }
    public string WallPlacementHint => _wallKind switch
    {
        PlacementKind.Gate => $"Gate: {Preview?.WallPlan?.Cost:N0}g; [ / ] or R rotates; click or Enter builds; Esc finishes.",
        PlacementKind.RemoveWall => "Choose a wall section, then click or Enter to review its removal.",
        _ when _wallAnchor is null => "Wall: click a start or existing wall; choose an endpoint; Esc finishes.",
        _ => $"Wall: {Preview?.WallPlan?.Length:0.0} cells, {Preview?.WallPlan?.Cost:N0}g; snaps 0/30/45/60/90; click or Enter builds; Esc finishes." +
            (Preview?.WallEnclosures?.Count > 0 ? " Gold: newly enclosed land; gate access is also required." : ""),
    };

    public void BeginWallPlacement(PlacementKind kind = PlacementKind.WallRun)
    {
        if (kind is not (PlacementKind.WallRun or PlacementKind.Gate or PlacementKind.RemoveWall))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (Game.Settlement is null)
        {
            SetMessage("Found a bailey before editing its walls.", MessageKind.Error);
            return;
        }
        _previewMenu = CaptureMenu() ?? _selectedMenu;
        _wallKind = kind;
        _wallAnchor = null;
        _gateAngle = 0;
        _gateAngleChosen = false;
        HasCursor = true;
        Anchor = null;
        Selection = null;
        RefreshWallPlacement();
    }

    public void RotateGatePlacement(int steps)
    {
        if (Preview?.Kind != PlacementKind.Gate) return;
        _gateAngleChosen = true;
        _gateAngle += steps * MathF.PI / 12;
        RefreshWallPlacement();
    }

    private void MoveWallPlacement(Pos position)
    {
        var cursor = Clamp(position);
        bool changed = Cursor != cursor || !ReferenceEquals(_wallPreviewGame, Game) ||
            _wallPreviewRevision != Game.Settlement!.Revision || _wallPreviewMapVersion != Game.MapVersion ||
            _wallPreviewMoney != Game.Money;
        Cursor = cursor;
        if (changed) RefreshWallPlacement();
        FollowCursor();
    }

    private void RefreshWallPlacement()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var point = Game.SnapWallPoint(new(Cursor.X + .5f, Cursor.Y + .5f));
        if (_wallKind == PlacementKind.WallRun && _wallAnchor is { } anchor)
            point = Game.SnapWallEndpoint(anchor, new(Cursor.X + .5f, Cursor.Y + .5f));
        if (_wallKind == PlacementKind.Gate && !_gateAngleChosen) _gateAngle = Game.GateAngleAt(point);
        WallRunPlan? plan = _wallKind switch
        {
            PlacementKind.Gate => Game.PlanGate(point, _gateAngle),
            PlacementKind.WallRun => Game.PlanWallRun(_wallAnchor ?? point, point),
            _ => null,
        };
        var cells = plan is null ? new HashSet<Pos> { Cursor } :
            CityGame.WallCells(plan.Start, plan.End).ToHashSet();
        var area = cells.Aggregate(CellRect.Single(Cursor), (bounds, p) => bounds.Union(CellRect.Single(p)));
        bool valid = plan?.Valid ?? Game.Settlement!.Walls.Any(w => w.Cell == Cursor);
        Preview = new(_wallKind, area, new(valid ? cells.Count : 0, plan?.Cost ?? 0, valid ? 0 : cells.Count),
            _wallKind == PlacementKind.Gate ? "Gate" : _wallKind == PlacementKind.RemoveWall ? "Remove wall" : "Wall run",
            Cells: cells, ValidCells: valid ? cells : [], WallPlan: plan,
            WallEnclosures: plan is { Valid: true } ? Game.PreviewWallEnclosures(plan) : []);
        _wallPreviewGame = Game;
        _wallPreviewRevision = Game.Settlement!.Revision;
        _wallPreviewMapVersion = Game.MapVersion;
        _wallPreviewMoney = Game.Money;
        Changed?.Invoke();
        LastWallPreviewMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
