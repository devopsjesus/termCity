using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed class DebugSettlementSiteUnavailableException(string message) : InvalidOperationException(message);

public static partial class SettlementDebugSetup
{
    public static (CityGame Game, Pos Target, int RegeneratedMaps) Create(GameConfig config, bool siege = false,
        BaileyGateSide gateSide = BaileyGateSide.Right, int? wallWidth = null, int? wallHeight = null,
        GameContent? content = null, DateTime? startingDate = null, CancellationToken cancellationToken = default) =>
        Create(config, settings => CityGame.New(settings, content, startingDate), siege, gateSide,
            wallWidth, wallHeight, cancellationToken);

    internal static (CityGame Game, Pos Target, int RegeneratedMaps) Create(GameConfig config,
        Func<GameConfig, CityGame> generate, bool siege = false, BaileyGateSide gateSide = BaileyGateSide.Right,
        int? wallWidth = null, int? wallHeight = null, CancellationToken cancellationToken = default)
    {
        if (!CityGame.DebugToolsAvailable) throw new InvalidOperationException("Settlement presets require a Debug build.");
        if (config.Scenario != CityScenario.Random)
            throw new InvalidOperationException("Debug settlement regeneration requires a random map.");
        if (!Enum.IsDefined(gateSide)) throw new ArgumentOutOfRangeException(nameof(gateSide));
        if (wallWidth is null && wallHeight is null) wallWidth = wallHeight = DefaultWallSize;
        int regenerated = 0;
        bool feasible = false;
        int seed = config.Seed;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = generate(config with { Seed = seed });
            cancellationToken.ThrowIfCancellationRequested();
            if (EnclosureSizeError(game, wallWidth, wallHeight) is { } sizeError)
                throw new InvalidOperationException(sizeError);
            double cheapest = game.Map.Content.Terrains.Where(terrain => terrain.Buildable)
                .Select(terrain => terrain.BuildCostModifier).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (!double.IsFinite(cheapest))
                throw new InvalidOperationException("Debug settlements require registered buildable terrain.");
            double minimum = (2L * wallWidth!.Value + 2L * wallHeight!.Value - 4) * 20 +
                new[] { "Motte and Bailey", "Town Well", "Woodlot" }.Sum(name =>
                    Math.Round(game.Map.Content.Buildings.Get(name).Cost * cheapest));
            if (game.Money < minimum)
                throw new InvalidOperationException($"The debug enclosure needs at least {minimum:N0} gold for its walls and starter buildings.");
            try
            {
                var target = Apply(game, siege, gateSide, wallWidth, wallHeight);
                cancellationToken.ThrowIfCancellationRequested();
                return (game, target, regenerated);
            }
            catch (DebugSettlementSiteUnavailableException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!feasible)
                {
                    VerifyPresetFeasible(config, game.Map.Content, siege, gateSide, wallWidth.Value, wallHeight.Value);
                    feasible = true;
                }
                regenerated++;
                seed = unchecked(seed + 1);
            }
        }
    }

    private static void VerifyPresetFeasible(GameConfig config, GameContent content, bool siege,
        BaileyGateSide gateSide, int width, int height)
    {
        var probe = CityGame.New(config, content);
        foreach (int index in probe.Map.RoadCells.ToArray())
            probe.Map.SetRoad(index % probe.Map.Width, index / probe.Map.Width, false);
        var area = new CellRect(0, 0, Math.Min(probe.Map.Width, width + 18), Math.Min(probe.Map.Height, height + 18));
        foreach (var cell in area.Cells())
        {
            probe.Map.ClearCell(cell.X, cell.Y);
            probe.Map.SetTerrain(cell.X, cell.Y, content.Terrains.Base);
        }
        probe.Touch();
        try
        {
            Apply(probe, siege, gateSide, width, height);
        }
        catch (DebugSettlementSiteUnavailableException error)
        {
            throw new InvalidOperationException("The requested debug preset cannot fit even on clear land. " +
                "Choose larger walls or a map with sufficient room and gold.", error);
        }
    }
}
