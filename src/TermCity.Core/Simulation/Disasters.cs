using TermCity.Core.Buildings;
using TermCity.Core.Terrain;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>Fires, outbreaks, floods and quakes. Each is rare, scaled by the city's profile, and blunted by the service meant to stop it.</summary>
internal static class Disasters
{
    public static void RunWeek(CityGame game, WeekTally tally, int[] occupied)
    {
        if (occupied.Length == 0)
        {
            return;
        }

        Fires(game, tally, occupied);
        Outbreak(game, tally);
        Flood(game, tally, occupied);
        Quake(game, tally, occupied);
    }

    private static void Fires(CityGame game, WeekTally tally, int[] occupied)
    {
        double rate = game.Config.FireIgnitionPerBuildingWeek * game.Profile.FireRisk;
        double expected = occupied.Length * rate;
        int fires = Poisson(game, expected);
        for (int n = 0; n < fires; n++)
        {
            int start = occupied[game.Rng.Next(occupied.Length)];
            if (game.Map.BuildingLayer[start] == 0)
            {
                continue;
            }

            // A fire station nearby often catches a blaze before it starts; it only protects what it can reach.
            if (game.Rng.Chance(0.65 * game.Services.Coverage(ServiceKind.Fire, start) / 100))
            {
                continue;
            }

            Ignite(game, tally, start, "A fire broke out");
        }
    }

    /// <summary>Burns a cell and spreads to neighbours; fire cover shrinks the spread.</summary>
    internal static void Ignite(CityGame game, WeekTally tally, int start, string headline)
    {
        var map = game.Map;
        var burned = new List<int>();
        var frontier = new Queue<int>();
        var seen = new HashSet<int> { start };
        frontier.Enqueue(start);
        int deaths = 0;
        while (frontier.Count > 0 && burned.Count < 60)
        {
            int i = frontier.Dequeue();
            double cover = game.Services.Coverage(ServiceKind.Fire, i) / 100;
            deaths += Burn(game, i, cover);
            burned.Add(i);
            double spread = 0.62 * (1 - 0.92 * cover);
            int x = i % map.Width, y = i / map.Width;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                int nx = x + dx, ny = y + dy;
                if (!map.InBounds(nx, ny))
                {
                    continue;
                }

                int ni = map.Index(nx, ny);
                if (map.BuildingLayer[ni] != 0 && seen.Add(ni) && game.Rng.Chance(spread))
                {
                    frontier.Enqueue(ni);
                }
            }
        }

        tally.Deaths += deaths;
        string size = burned.Count == 1 ? "one building" : $"{burned.Count} buildings";
        string toll = deaths > 0 ? $" {deaths} died." : string.Empty;
        game.Report(new CityEvent(game.Week, EventKind.Fire, $"{headline} and destroyed {size}.{toll}", burned));
    }

    private static int Burn(CityGame game, int i, double cover)
    {
        var map = game.Map;
        int dead = 0;
        var h = map.HouseholdLayer[i];
        if (!h.IsEmpty)
        {
            int risk = (int)Math.Round(h.Total * 0.10 * (1 - 0.9 * cover));
            dead = Math.Min(h.Total, risk);
        }

        var p = map.PosOf(i);
        map.SetBuilding(p.X, p.Y, null);
        map.SetHousehold(p.X, p.Y, default);
        return dead;
    }

    private static void Outbreak(CityGame game, WeekTally tally)
    {
        var ind = game.Indicators;
        var stats = game.Stats;
        if (game.OutbreakWeeksLeft > 0)
        {
            game.OutbreakWeeksLeft--;
            if (game.OutbreakWeeksLeft == 0)
            {
                game.Report(new CityEvent(game.Week, EventKind.Outbreak, "The outbreak has run its course.", [], Bad: false));
            }

            return;
        }

        if (stats.Population < game.Config.OutbreakMinPopulation)
        {
            return;
        }

        double density = stats.Residential.Filled == 0 ? 1 : Math.Min(2, stats.Population / (stats.Residential.Filled * 5.0));
        double chance = game.Config.OutbreakChancePerWeek * game.Profile.OutbreakRisk * density *
            (1 - 0.75 * ind.CoverageOf(ServiceKind.Health) / 100) * (1 + ind.Pollution / 100);
        if (game.Rng.Chance(chance))
        {
            game.OutbreakWeeksLeft = game.Rng.Next(4, 9);
            game.Report(new CityEvent(game.Week, EventKind.Outbreak,
                "An outbreak of disease is spreading. Clinics and hospitals save lives.", []));
        }
    }

    private static void Flood(CityGame game, WeekTally tally, int[] occupied)
    {
        // Spring melt: floods only come in a window of weeks.
        int week = game.WeekOfYear;
        if (week < 8 || week > 22)
        {
            return;
        }

        if (!game.Rng.Chance(game.Config.FloodChancePerWeek * game.Profile.FloodRisk))
        {
            return;
        }

        var map = game.Map;
        var hill = map.Content.Terrains.Get(DefaultTerrains.HillName);
        // Flood a random shoreline: every low, flat cell within reach of the water that has a building.
        var shore = new List<int>();
        foreach (int i in occupied)
        {
            if (map.BuildingLayer[i] != 0 && map.NearWater(i % map.Width, i / map.Width, 2) &&
                map.TerrainLayer[i] != hill.Id)
            {
                shore.Add(i);
            }
        }

        if (shore.Count == 0)
        {
            return;
        }

        int centre = shore[game.Rng.Next(shore.Count)];
        int cx = centre % map.Width, cy = centre / map.Width;
        var hit = new List<int>();
        foreach (int i in shore)
        {
            int dx = i % map.Width - cx, dy = i / map.Width - cy;
            if (dx * dx + dy * dy <= 16 * 16 && game.Rng.Chance(0.35))
            {
                var p = map.PosOf(i);
                map.SetBuilding(p.X, p.Y, null);
                tally.MovedOut += map.HouseholdLayer[i].Total;
                map.SetHousehold(p.X, p.Y, default);
                hit.Add(i);
            }
        }

        if (hit.Count > 0)
        {
            game.Report(new CityEvent(game.Week, EventKind.Flood,
                $"The river is over its banks: {hit.Count} waterside buildings are lost.", hit));
        }
    }

    private static void Quake(CityGame game, WeekTally tally, int[] occupied)
    {
        double perWeek = game.Profile.QuakeRisk / (10.0 * game.Config.WeeksPerYear);
        if (perWeek <= 0 || !game.Rng.Chance(perWeek))
        {
            return;
        }

        var map = game.Map;
        double magnitude = 0.4 + 0.6 * game.Rng.NextDouble();
        int centre = occupied[game.Rng.Next(occupied.Length)];
        int cx = centre % map.Width, cy = centre / map.Width;
        int radius = (int)(14 + 40 * magnitude);
        var lost = new List<int>();
        int deaths = 0;
        foreach (int i in occupied)
        {
            int dx = i % map.Width - cx, dy = i / map.Width - cy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > radius)
            {
                continue;
            }

            if (map.BuildingLayer[i] == 0)
            {
                continue;
            }

            var building = map.Content.Buildings[map.BuildingLayer[i]];
            double risk = magnitude * (1 - d / radius) * (0.12 + 0.05 * building.Level);
            if (game.Rng.Chance(risk))
            {
                deaths += (int)(map.HouseholdLayer[i].Total * 0.02 * magnitude);
                var p = map.PosOf(i);
                map.SetBuilding(p.X, p.Y, null);
                map.SetHousehold(p.X, p.Y, default);
                lost.Add(i);
            }
        }

        tally.Deaths += deaths;
        game.Report(new CityEvent(game.Week, EventKind.Earthquake,
            $"A magnitude {5 + magnitude * 2.5:0.0} earthquake destroyed {lost.Count} buildings.", lost));
        // Broken gas mains: a few of the ruins burn.
        for (int n = 0; n < lost.Count / 25 && lost.Count > 0; n++)
        {
            int spark = occupied[game.Rng.Next(occupied.Length)];
            if (map.BuildingLayer[spark] != 0)
            {
                Ignite(game, tally, spark, "Gas mains ruptured and a fire started");
            }
        }
    }

    private static int Poisson(CityGame game, double mean)
    {
        // Knuth's method is plenty for the small means used here.
        double limit = Math.Exp(-Math.Min(mean, 30));
        int k = 0;
        double p = 1;
        do
        {
            k++;
            p *= game.Rng.NextDouble();
        }
        while (p > limit && k < 60);
        return k - 1;
    }
}
