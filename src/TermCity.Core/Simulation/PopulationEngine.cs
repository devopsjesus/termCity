using TermCity.Core.Buildings;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>Counts of what happened during the current week, reported when it ends.</summary>
internal sealed class WeekTally
{
    public int Births, Deaths, MovedIn, MovedOut, Events;

    public void Reset() => Births = Deaths = MovedIn = MovedOut = Events = 0;
}

/// <summary>
/// The weekly life of a full-rules city, applied to every home in a stable order so a seed always plays the same:
/// people are born, grow up, grow old and die; the unhappy and unemployed leave; empty homes and failing businesses are
/// abandoned; well-served land redevelops taller and neglected land falls back; and then disasters strike.
/// (Arrivals happen through the week, in <see cref="CityGame"/>.)
/// </summary>
internal static class PopulationEngine
{
    private const double ChildMortality = 0.0006 / 52;
    private const double AdultMortality = 0.003 / 52;
    private const double SeniorMortality = 0.065 / 52;
    private const double BirthsPerAdultWeek = 0.0006;
    private const double ChildrenGrowUpPerWeek = 1.0 / 936;
    private const double AdultsRetirePerWeek = 1.0 / 2444;

    private static readonly int[] UpgradePopulation = [0, 1_500, 9_000];
    private static readonly double[] UpgradeLandValue = [0, 38, 60];
    private static readonly double[] DowngradeLandValue = [0, 22, 40];

    public static void RunWeek(CityGame game, WeekTally tally)
    {
        var map = game.Map;
        var homes = Cells(map, ZoneType.Residential);
        Demographics(game, tally, homes);
        Emigration(game, tally, homes);
        Abandonment(game, homes);
        Closures(game);
        Density(game);

        var occupied = new List<int>();
        foreach (var zone in Zones.Placeable)
        {
            occupied.AddRange(Cells(map, zone));
        }

        occupied.Sort();
        Disasters.RunWeek(game, tally, [.. occupied]);
    }

    private static int[] Cells(GameMap map, ZoneType zone)
    {
        var cells = new List<int>();
        foreach (int i in map.ZoneCells(zone))
        {
            if (map.BuildingLayer[i] != 0)
            {
                cells.Add(i);
            }
        }

        cells.Sort();
        return [.. cells];
    }

    private static void Demographics(CityGame game, WeekTally tally, int[] homes)
    {
        var map = game.Map;
        var services = game.Services;
        double fertility = 0.7 + 0.6 * game.Indicators.Happiness / 100;
        bool outbreak = game.OutbreakWeeksLeft > 0;

        foreach (int i in homes)
        {
            var h = map.HouseholdLayer[i];
            if (h.IsEmpty)
            {
                continue;
            }

            double cover = services.Coverage(ServiceKind.Health, i) / 100;
            double mortality = 1.6 - 0.8 * cover;
            if (outbreak)
            {
                mortality *= 1 + 3 * (1 - 0.7 * cover);
            }

            int deadS = Math.Min(h.Seniors, game.StochasticRound(h.Seniors * SeniorMortality * mortality));
            int deadA = Math.Min(h.Adults, game.StochasticRound(h.Adults * AdultMortality * mortality));
            int deadC = Math.Min(h.Children, game.StochasticRound(h.Children * ChildMortality * mortality));
            int grown = Math.Min(h.Children - deadC, game.StochasticRound(h.Children * ChildrenGrowUpPerWeek));
            int retired = Math.Min(h.Adults - deadA, game.StochasticRound(h.Adults * AdultsRetirePerWeek));

            int capacity = map.Content.Buildings[map.BuildingLayer[i]].Capacity;
            int room = Math.Max(0, capacity - h.Total);
            int born = Math.Min(room, game.StochasticRound(h.Adults * BirthsPerAdultWeek * fertility));

            int adults = h.Adults - deadA + grown - retired;
            int children = h.Children - deadC - grown + born;
            int seniors = h.Seniors - deadS + retired;
            tally.Births += born;
            tally.Deaths += deadS + deadA + deadC;
            map.HouseholdLayer[i] = new Household((byte)Math.Clamp(adults, 0, 255), (byte)Math.Clamp(children, 0, 255), (byte)Math.Clamp(seniors, 0, 255));
        }
    }

    private static void Emigration(CityGame game, WeekTally tally, int[] homes)
    {
        var map = game.Map;
        var rng = game.Rng;
        var ind = game.Indicators;
        double rate = Math.Clamp((42 - ind.Happiness) / 42, 0, 1) * 0.015 + Math.Max(0, ind.Unemployment - 0.12) * 0.04;
        int leaving = game.StochasticRound(game.Stats.Population * rate);
        int guard = leaving + homes.Length;
        while (leaving > 0 && homes.Length > 0 && guard-- > 0)
        {
            int a = homes[rng.Next(homes.Length)], b = homes[rng.Next(homes.Length)];
            if (map.HouseholdLayer[a].IsEmpty)
            {
                a = b;
            }

            if (map.HouseholdLayer[b].IsEmpty)
            {
                b = a;
            }

            if (map.HouseholdLayer[a].IsEmpty)
            {
                continue;
            }

            int cell = CityAnalysis.CellHappiness(game, a) <= CityAnalysis.CellHappiness(game, b) ? a : b;
            var h = map.HouseholdLayer[cell];
            int n = Math.Min(h.Total, 1 + rng.Next(3));
            map.HouseholdLayer[cell] = h.Without(n, rng);
            tally.MovedOut += n;
            leaving -= n;
        }
    }

    private static void Abandonment(CityGame game, int[] homes)
    {
        var map = game.Map;
        double happiness = game.Indicators.Happiness;
        if (happiness >= 40)
        {
            return;
        }

        double chance = (40 - happiness) / 40 * 0.15;
        var lost = new List<int>();
        foreach (int i in homes)
        {
            if (map.HouseholdLayer[i].IsEmpty && game.Rng.Chance(chance))
            {
                var p = map.PosOf(i);
                map.SetBuilding(p.X, p.Y, null);
                lost.Add(i);
            }
        }

        if (lost.Count > 0)
        {
            game.Report(new CityEvent(game.Week, EventKind.Abandonment,
                $"{lost.Count} empty homes were abandoned: people are leaving a town they do not like.", lost));
        }
    }

    private static void Closures(CityGame game)
    {
        var map = game.Map;
        double climate = game.Indicators.BusinessClimate;
        double strain = Math.Max(0, 0.45 - climate) * 0.04;
        var closed = new List<int>();
        foreach (var zone in new[] { ZoneType.Commercial, ZoneType.Industrial })
        {
            foreach (int i in Cells(map, zone))
            {
                double chance = strain + (CityAnalysis.Operating(game, i) ? 0 : 0.03);
                if (chance > 0 && game.Rng.Chance(chance))
                {
                    var p = map.PosOf(i);
                    map.SetBuilding(p.X, p.Y, null);
                    closed.Add(i);
                }
            }
        }

        if (closed.Count > 0)
        {
            game.Report(new CityEvent(game.Week, EventKind.Closure,
                $"{closed.Count} businesses closed: no fuel or water, cut off from the roads, or too costly to run.", closed));
        }
    }

    private static void Density(CityGame game)
    {
        var map = game.Map;
        var rng = game.Rng;
        var ind = game.Indicators;
        var services = game.Services;
        var buildings = map.Content.Buildings;
        double scale = Math.Sqrt(game.Profile.DensityAppetite);
        int population = game.Stats.Population;
        bool utilities = services.Power.Ratio >= 0.98 && services.Water.Ratio >= 0.98;
        var grown = new List<int>();
        int fallen = 0;

        foreach (var zone in Zones.Placeable)
        {
            foreach (int i in Cells(map, zone))
            {
                if (!rng.Chance(game.Config.DensityChangePerWeek))
                {
                    continue;
                }

                var type = buildings[map.BuildingLayer[i]];
                var next = buildings.Upgrade(type);
                double land = CityAnalysis.LandValue(game, i);
                int level = type.Level;

                if (next is not null && utilities && population >= UpgradePopulation[level] &&
                    land >= UpgradeLandValue[level] * scale && game.Network.AccessRank(i) >= level &&
                    DemandsUpgrade(game, ind, zone, i, type))
                {
                    var p = map.PosOf(i);
                    map.SetBuilding(p.X, p.Y, next);
                    if (zone == ZoneType.Residential)
                    {
                        map.HouseholdLayer[i] = map.HouseholdLayer[i].Scaled(0.6 * next.Capacity / type.Capacity);
                    }

                    grown.Add(i);
                }
                else if (level > 1 && land < DowngradeLandValue[level - 1] * scale)
                {
                    var lower = buildings.ForZone(zone, level - 1)!;
                    var p = map.PosOf(i);
                    map.SetBuilding(p.X, p.Y, lower);
                    if (zone == ZoneType.Residential)
                    {
                        var h = map.HouseholdLayer[i];
                        var shrunk = h.Total <= lower.Capacity ? h : h.Scaled((double)lower.Capacity / h.Total);
                        game.Tally.MovedOut += h.Total - shrunk.Total;
                        map.HouseholdLayer[i] = shrunk;
                    }

                    fallen++;
                }
            }
        }

        if (grown.Count > 0)
        {
            game.Report(new CityEvent(game.Week, EventKind.Upgrade,
                $"{grown.Count} buildings were redeveloped at higher density.", grown, Bad: false));
        }

        if (fallen > 0)
        {
            game.Report(new CityEvent(game.Week, EventKind.Upgrade,
                $"{fallen} buildings were replaced by smaller ones as the neighbourhoods declined.", []));
        }
    }

    private static bool DemandsUpgrade(CityGame game, CityIndicators ind, ZoneType zone, int i, BuildingType type)
    {
        if (!CityAnalysis.Operating(game, i))
        {
            return false;
        }

        if (zone == ZoneType.Residential)
        {
            var h = game.Map.HouseholdLayer[i];
            return h.Total >= 0.7 * type.Capacity && ind.Unemployment <= 0.10;
        }

        return ind.JobsFilled >= 0.9;
    }
}
