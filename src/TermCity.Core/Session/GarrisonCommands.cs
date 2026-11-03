using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    private int? _selectedEnemyId;
    private CityGame? _enemySelectionGame;
    public SiegeEnemy? SelectedEnemy
    {
        get
        {
            if (!ReferenceEquals(_enemySelectionGame, Game))
            {
                _selectedEnemyId = null;
                _enemySelectionGame = Game;
            }
            var enemy = Game.Settlement?.Enemies.FirstOrDefault(e => e.Id == _selectedEnemyId && e.Health > 0);
            if (enemy is null) _selectedEnemyId = null;
            return enemy;
        }
    }

    public string? SelectedEnemySummary => SelectedEnemy is { } enemy
        ? $"({(int)enemy.X},{(int)enemy.Y}) | {enemy.Definition.Name} : {enemy.Health:0}/{enemy.MaxHealth:0} health"
        : null;

    private void ClearEnemySelection() => _selectedEnemyId = null;

    public void SelectEnemy(int id)
    {
        if (Game.Settlement?.Enemies.FirstOrDefault(e => e.Id == id && e.Health > 0) is null)
        {
            SetMessage("That attacker is no longer present.", MessageKind.Error);
            return;
        }
        ClearSelection();
        _selectedDefenders.Clear();
        HasCursor = false;
        _selectedEnemyId = id;
        _enemySelectionGame = Game;
        SelectionChanged?.Invoke();
    }

    private readonly HashSet<int> _selectedDefenders = [];
    private CityGame? _defenderSelectionGame;
    public IReadOnlyCollection<int> SelectedDefenderIds
    {
        get
        {
            if (!ReferenceEquals(_defenderSelectionGame, Game))
            {
                _selectedDefenders.Clear();
                _defenderSelectionGame = Game;
            }
            _selectedDefenders.RemoveWhere(id => Game.Settlement?.Defenders.Any(d => d.Id == id && d.Health > 0) != true);
            return _selectedDefenders;
        }
    }

    public void SelectDefender(int id, bool additive = false)
    {
        _ = SelectedDefenderIds;
        if (Game.Settlement?.Defenders.FirstOrDefault(d => d.Id == id && d.Health > 0) is not { } unit)
        {
            SetMessage("That defender is no longer available.", MessageKind.Error);
            return;
        }
        if (!additive) _selectedDefenders.Clear();
        _selectedDefenders.Add(id);
        ClearSelection();
        SetMessage($"{_selectedDefenders.Count} defender(s) selected: {unit.Kind}, {unit.Health:0}/{unit.MaxHealth:0} HP. " +
            $"{CombatProfiles.Fighter(unit.Kind).Damage:0} damage / {CombatProfiles.Fighter(unit.Kind).Interval:0.#}s; " +
            (unit.Kind == DefenderKind.Archer ? "9-cell maximum reach; clear shot required. " : "Melee contact; reach and cover checked at strike. ") +
            "Right-click ground to move or an enemy to attack; Esc clears selection.");
    }

    public void ClearDefenderSelection()
    {
        _selectedDefenders.Clear();
        SetMessage("Defender selection cleared.");
    }

    public ActionResult CommandSelectedDefenders(Pos? destination = null, int? enemyId = null)
    {
        var ids = SelectedDefenderIds.ToArray();
        return Game.Settlement is { } siege ? Execute(() => siege.CommandDefenders(Game, ids, destination, enemyId)) :
            Complete(ActionResult.Fail("There is no garrison in this city."));
    }
}
