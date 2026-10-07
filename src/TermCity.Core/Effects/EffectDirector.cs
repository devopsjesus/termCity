using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Effects;

/// <summary>
/// The only link between the game and the effects. It watches a <see cref="CityGame"/> (events, map changes,
/// milestones, tax day) and spawns effects into an <see cref="EffectSystem"/>; the simulation never knows effects
/// exist and the effects never read the game except through this class.
/// <para>
/// Call <see cref="Update"/> once per frame instead of <see cref="EffectSystem.Update"/>: it polls the game, spawns
/// what changed and then advances the effect clock.
/// </para>
/// </summary>
public sealed class EffectDirector : IDisposable
{
    /// <summary>More changed cells than this in one step is a load, new game or wholesale rewrite: resync, do not animate.</summary>
    public const int MassChangeLimit = 1500;

    /// <summary>Most cells handed to a single effect.</summary>
    public const int MaxGroupCells = 600;

    private const int MaxClusters = 12;
    private const int ClusterReach = 2;
    private const int ViewMargin = 6;
    private const int MaxPendingEvents = 200;

    private readonly EffectSystem _system;
    private readonly List<CityEvent> _events = [];
    private readonly MapSnapshot _snapshot = new();
    private GameSession? _session;
    private CityGame? _game;
    private CellRect? _fixedView;
    private int _version = -1;
    private WeekReport? _report;
    private int _milestone;
    private AmbientLife? _ambient;
    private bool _disposed;

    public EffectDirector(EffectSystem system) => _system = system ?? throw new ArgumentNullException(nameof(system));

    public EffectSystem System => _system;

    /// <summary>Ambient life (cars, walkers, birds, smoke) on or off, independent of the master switch.</summary>
    public bool Ambient { get; set; } = true;

    public int SpawnedLastUpdate { get; private set; }

    /// <summary>Total effects spawned by this director since it was created.</summary>
    public int TotalSpawned { get; private set; }

    /// <summary>Times a wholesale map change was detected and resynced without animating.</summary>
    public int Resyncs { get; private set; }

    /// <summary>Watches a game directly. The view defaults to the whole map unless <paramref name="view"/> is given.</summary>
    public void Attach(CityGame game, CellRect? view = null)
    {
        _session = null;
        _fixedView = view;
        Bind(game);
    }

    /// <summary>Watches a session's current game (following new games and loads) and its visible area and zoom.</summary>
    public void Attach(GameSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _fixedView = null;
        Bind(session.Game);
    }

    public void Detach()
    {
        Unbind();
        _session = null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Unbind();
        }
    }

    /// <summary>Polls the game, spawns effects for what changed, and advances the effect system by <paramref name="dt"/> seconds.</summary>
    public void Update(double dt)
    {
        SpawnedLastUpdate = 0;
        if (_session is not null && !ReferenceEquals(_session.Game, _game))
        {
            Bind(_session.Game);
        }

        bool close = true;
        if (_game is not null)
        {
            var view = CurrentView(out close);
            _system.View = close ? view : null;
            Poll(view, close);
        }

        SyncAmbient(close);
        _system.Update(dt);
    }

    private void Bind(CityGame? game)
    {
        Unbind();
        _game = game;
        _events.Clear();
        _system.Clear();
        _ambient = null;
        if (game is null)
        {
            return;
        }

        game.EventOccurred += OnEvent;
        Resync();
    }

    private void Unbind()
    {
        if (_game is not null)
        {
            _game.EventOccurred -= OnEvent;
        }

        _game = null;
        _events.Clear();
    }

    private void OnEvent(CityEvent cityEvent)
    {
        _events.Add(cityEvent);
        if (_events.Count > MaxPendingEvents)
        {
            _events.RemoveAt(0);
        }
    }

    private void Resync()
    {
        var game = _game!;
        _snapshot.Capture(game.Map);
        _version = game.MapVersion;
        _report = game.LastReport;
        _milestone = game.HighestMilestone;
        _events.Clear();
    }

    /// <summary>The area worth animating, clipped to the map. <paramref name="close"/> is false when zoomed too far out to see detail.</summary>
    private CellRect CurrentView(out bool close)
    {
        var map = _game!.Map;
        var whole = new CellRect(0, 0, map.Width, map.Height);
        close = true;
        CellRect view = whole;
        if (_session is not null)
        {
            view = _session.ViewRect;
            close = _session.Stride == 1;
        }
        else if (_fixedView is { } fixedView)
        {
            view = fixedView;
        }

        int x0 = Math.Max(0, view.X), y0 = Math.Max(0, view.Y);
        int x1 = Math.Min(map.Width - 1, view.Right), y1 = Math.Min(map.Height - 1, view.Bottom);
        return x1 < x0 || y1 < y0 ? new CellRect(0, 0, 0, 0) : new CellRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void SyncAmbient(bool close)
    {
        bool wanted = _game is not null && Ambient && close && _system.Settings.Active;
        bool alive = _ambient is not null && _system.Effects.Contains(_ambient);
        if (wanted && !alive)
        {
            _ambient = new AmbientLife(new GameAmbientWorld(_game!));
            if (!_system.Spawn(_ambient))
            {
                _ambient = null;
            }
        }
        else if (!wanted && _ambient is not null)
        {
            _system.ClearAmbient();
            _ambient = null;
        }
    }

    // ---- Polling ----------------------------------------------------------------------------------------------

    private void Poll(CellRect view, bool close)
    {
        var game = _game!;
        var map = game.Map;
        bool animate = _system.Settings.Active && close;

        if (!_snapshot.Matches(map))
        {
            Resync();
            Resyncs++;
            return;
        }

        var causes = animate ? CausesFromEvents(map) : null;
        if (game.MapVersion != _version)
        {
            if (animate)
            {
                DiffAndSpawn(map, view, causes!);
            }

            _snapshot.Capture(map);
            _version = game.MapVersion;
        }

        if (animate)
        {
            SpawnFromEvents(map, view);
        }

        if (game.HighestMilestone > _milestone)
        {
            if (animate)
            {
                Celebrate(view);
            }

            _milestone = game.HighestMilestone;
        }

        if (!ReferenceEquals(game.LastReport, _report))
        {
            if (animate && game.LastReport is { Income: > 0 })
            {
                TaxDay(map, view);
            }

            _report = game.LastReport;
        }

        _events.Clear();
    }

    private Dictionary<int, EventKind> CausesFromEvents(GameMap map)
    {
        var causes = new Dictionary<int, EventKind>();
        foreach (var e in _events)
        {
            if (e.Kind is EventKind.Finance or EventKind.Milestone or EventKind.Outbreak)
            {
                continue;
            }

            foreach (int index in e.Cells)
            {
                if (index < 0 || index >= map.Width * map.Height)
                {
                    continue;
                }

                // A fire wins over the quake that caused it.
                if (!causes.TryGetValue(index, out var existing) || e.Kind == EventKind.Fire || existing != EventKind.Fire && e.Kind == EventKind.Earthquake)
                {
                    causes[index] = e.Kind;
                }
            }
        }

        return causes;
    }

    private void DiffAndSpawn(GameMap map, CellRect view, Dictionary<int, EventKind> causes)
    {
        int x0 = Math.Max(0, view.X - ViewMargin), y0 = Math.Max(0, view.Y - ViewMargin);
        int x1 = Math.Min(map.Width - 1, view.Right + ViewMargin), y1 = Math.Min(map.Height - 1, view.Bottom + ViewMargin);
        if (x1 < x0 || y1 < y0)
        {
            return;
        }

        var lost = new Dictionary<EventKind, List<Pos>>();
        var lostPlain = new List<Pos>();
        var grownBuildings = new List<Pos>();
        var upgraded = new List<Pos>();
        var roads = new List<Pos>();
        var zoned = new List<Pos>();
        int changed = 0;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var c = _snapshot.Compare(map, x, y);
                if (c == CellChange.None)
                {
                    continue;
                }

                if (++changed > MassChangeLimit)
                {
                    Resyncs++;
                    return;
                }

                var p = new Pos(x, y);
                int index = map.Index(x, y);
                if ((c & CellChange.Lost) != 0)
                {
                    if (causes.TryGetValue(index, out var kind))
                    {
                        if (!lost.TryGetValue(kind, out var list))
                        {
                            lost[kind] = list = [];
                        }

                        list.Add(p);
                    }
                    else
                    {
                        lostPlain.Add(p);
                    }
                }

                if ((c & CellChange.BuildingGained) != 0)
                {
                    grownBuildings.Add(p);
                }

                if ((c & CellChange.BuildingChanged) != 0 && causes.TryGetValue(index, out var k) && k == EventKind.Upgrade)
                {
                    upgraded.Add(p);
                }

                if ((c & CellChange.RoadGained) != 0)
                {
                    roads.Add(p);
                }

                if ((c & CellChange.Zoned) != 0 && (c & CellChange.BuildingGained) == 0)
                {
                    zoned.Add(p);
                }
            }
        }

        SpawnLostPlain(map, lostPlain);
        foreach (var (kind, cells) in lost)
        {
            SpawnLostCaused(map, kind, cells);
        }

        SpawnBuilt(map, grownBuildings, upgraded);
        SpawnRoads(map, roads);
        SpawnZoned(map, zoned);
    }

    private IReadOnlyList<GhostCell> Ghosts(GameMap map, IEnumerable<Pos> cells)
    {
        var ghosts = new List<GhostCell>();
        foreach (var p in cells.Take(MaxGroupCells))
        {
            var visual = _snapshot.Describe(map, p.X, p.Y);
            ghosts.Add(new GhostCell(p, visual.Glyph, visual.Foreground));
        }

        return ghosts;
    }

    private void SpawnLostPlain(GameMap map, List<Pos> cells)
    {
        if (cells.Count == 0)
        {
            return;
        }

        var hint = _session?.ActiveArea;
        foreach (var cluster in Cluster(cells))
        {
            (double X, double Y) centre = ClusterCentre(cluster);
            if (hint is { } area && cluster.All(area.Contains) && area.Area > 1)
            {
                centre = (area.X + area.Width / 2.0, area.Y + area.Height / 2.0);
            }

            Spawn(new DemolishEffect(Ghosts(map, cluster), centre, _system.NextSeed()));
        }
    }

    private void SpawnLostCaused(GameMap map, EventKind kind, List<Pos> cells)
    {
        var ghosts = Ghosts(map, cells);
        var capped = cells.Take(MaxGroupCells).ToList();
        switch (kind)
        {
            case EventKind.Fire:
                Spawn(new FireEffect(ghosts, _system.NextSeed()));
                Spawn(new CollapseEffect(capped, _system.NextSeed()), 2.8);
                break;
            case EventKind.Earthquake:
                Spawn(new FadeEffect([], EffectGlyphs.DustColor, 1.4, ghosts));
                Spawn(new CollapseEffect(capped, _system.NextSeed()), 0.3);
                break;
            case EventKind.Flood:
                Spawn(new FadeEffect([], EffectGlyphs.WaterColor, 2.0, ghosts));
                break;
            default:
                Spawn(new FadeEffect([], EffectGlyphs.AbandonColor, 2.2, ghosts));
                break;
        }
    }

    private void SpawnBuilt(GameMap map, List<Pos> grown, List<Pos> upgraded)
    {
        var placed = new List<TintedCell>();
        var natural = new List<TintedCell>();
        foreach (var p in grown.Take(MaxGroupCells))
        {
            var building = map.BuildingAt(p.X, p.Y);
            var cell = new TintedCell(p, building?.Foreground ?? EffectGlyphs.ScaffoldColor);
            (map.ZoneAt(p.X, p.Y) == ZoneType.None ? placed : natural).Add(cell);
        }

        foreach (var p in upgraded.Take(MaxGroupCells))
        {
            natural.Add(new TintedCell(p, EffectGlyphs.SparkleColor));
        }

        if (placed.Count > 0)
        {
            Spawn(new ConstructEffect(placed, ConstructStyle.BuildUp, _system.NextSeed()));
        }

        if (natural.Count > 0)
        {
            Spawn(new ConstructEffect(natural, ConstructStyle.Grow, _system.NextSeed()));
        }
    }

    private void SpawnRoads(GameMap map, List<Pos> roads)
    {
        if (roads.Count == 0)
        {
            return;
        }

        var cells = new List<RoadCell>();
        foreach (var p in RoadStrokeEffect.OrderPath(roads.Take(MaxGroupCells)))
        {
            // The glyph the cell showed before: terrain if it had nothing; fall back to the same background.
            cells.Add(new RoadCell(p, _snapshot.Describe(map, p.X, p.Y).Background));
        }

        Spawn(new RoadStrokeEffect(cells));
    }

    private void SpawnZoned(GameMap map, List<Pos> zoned)
    {
        if (zoned.Count == 0)
        {
            return;
        }

        var cells = zoned.Take(MaxGroupCells).Select(p => new TintedCell(p, Zones.Get(map.ZoneAt(p.X, p.Y)).Foreground)).ToList();
        var origin = _session?.ActiveArea is { } area && zoned.All(area.Contains)
            ? (area.X + area.Width / 2.0, area.Y + area.Height / 2.0)
            : ClusterCentre(zoned);
        Spawn(new ZoneRippleEffect(cells, origin));
    }

    private void SpawnFromEvents(GameMap map, CellRect view)
    {
        foreach (var e in _events)
        {
            switch (e.Kind)
            {
                case EventKind.Earthquake:
                    var hit = InView(map, e.Cells, view).Take(QuakeEffect.MaxJitterCells).ToList();
                    Spawn(new QuakeEffect(hit, _system.NextSeed(), Math.Min(2, 0.6 + e.Cells.Count / 60.0)));
                    break;
                case EventKind.Flood:
                    var wet = InView(map, e.Cells, view).Take(MaxGroupCells).ToList();
                    if (wet.Count > 0)
                    {
                        Spawn(new FloodEffect(wet, _system.NextSeed()));
                    }

                    break;
                case EventKind.Outbreak when e.Bad:
                    var sick = SampleZone(map, ZoneType.Residential, view, 60);
                    if (sick.Count > 0)
                    {
                        Spawn(new FadeEffect(sick, EffectGlyphs.OutbreakColor, 3.0));
                    }

                    break;
            }
        }
    }

    private static IEnumerable<Pos> InView(GameMap map, IReadOnlyList<int> cells, CellRect view)
    {
        foreach (int index in cells)
        {
            if (index < 0 || index >= map.Width * map.Height)
            {
                continue;
            }

            var p = map.PosOf(index);
            if (view.Contains(p))
            {
                yield return p;
            }
        }
    }

    private static List<Pos> SampleZone(GameMap map, ZoneType zone, CellRect view, int limit)
    {
        var all = new List<Pos>();
        foreach (int index in map.ZoneCells(zone))
        {
            var p = map.PosOf(index);
            if (view.Contains(p) && map.IsFilled(p.X, p.Y))
            {
                all.Add(p);
            }
        }

        if (all.Count <= limit)
        {
            return all;
        }

        double step = all.Count / (double)limit;
        return [.. Enumerable.Range(0, limit).Select(i => all[(int)(i * step)])];
    }

    private void Celebrate(CellRect view)
    {
        var origin = (view.X + view.Width / 2.0, view.Y + view.Height * 0.55);
        Spawn(new ConfettiEffect(origin, 70, _system.NextSeed()));
    }

    private void TaxDay(GameMap map, CellRect view)
    {
        var sources = new List<Pos>();
        foreach (var zone in new[] { ZoneType.Commercial, ZoneType.Residential, ZoneType.Industrial })
        {
            sources.AddRange(SampleZone(map, zone, view, 5));
        }

        if (sources.Count > 0)
        {
            Spawn(new CoinsEffect(sources, _system.NextSeed()));
        }
    }

    private void Spawn(Effect effect, double delay = 0)
    {
        if (_system.Spawn(effect, delay))
        {
            SpawnedLastUpdate++;
            TotalSpawned++;
        }
    }

    // ---- Clustering -------------------------------------------------------------------------------------------

    /// <summary>Groups cells that are within a couple of cells of each other, merging the smallest groups if there are too many.</summary>
    public static List<List<Pos>> Cluster(IReadOnlyCollection<Pos> cells)
    {
        var remaining = new HashSet<Pos>(cells);
        var clusters = new List<List<Pos>>();
        foreach (var start in cells)
        {
            if (!remaining.Remove(start))
            {
                continue;
            }

            var group = new List<Pos> { start };
            var queue = new Queue<Pos>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                for (int dy = -ClusterReach; dy <= ClusterReach; dy++)
                {
                    for (int dx = -ClusterReach; dx <= ClusterReach; dx++)
                    {
                        var n = new Pos(p.X + dx, p.Y + dy);
                        if (remaining.Remove(n))
                        {
                            group.Add(n);
                            queue.Enqueue(n);
                        }
                    }
                }
            }

            clusters.Add(group);
        }

        if (clusters.Count > MaxClusters)
        {
            clusters = [.. clusters.OrderByDescending(c => c.Count)];
            var rest = clusters.Skip(MaxClusters - 1).SelectMany(c => c).ToList();
            clusters = [.. clusters.Take(MaxClusters - 1), rest];
        }

        return clusters;
    }

    public static (double X, double Y) ClusterCentre(IReadOnlyCollection<Pos> cells)
    {
        int x0 = cells.Min(p => p.X), x1 = cells.Max(p => p.X), y0 = cells.Min(p => p.Y), y1 = cells.Max(p => p.Y);
        return ((x0 + x1 + 1) / 2.0, (y0 + y1 + 1) / 2.0);
    }

    private sealed class GameAmbientWorld(CityGame game) : IAmbientWorld
    {
        public AmbientKind Classify(int x, int y)
        {
            var map = game.Map;
            if (!map.InBounds(x, y))
            {
                return AmbientKind.None;
            }

            var kind = AmbientKind.None;
            if (map.HasRoad(x, y))
            {
                kind |= AmbientKind.Road;
            }

            if (map.BuildingAt(x, y) is not null)
            {
                switch (map.ZoneAt(x, y))
                {
                    case ZoneType.Residential:
                        kind |= AmbientKind.Home;
                        break;
                    case ZoneType.Industrial:
                        kind |= AmbientKind.Factory;
                        break;
                }
            }

            if (!map.TerrainAt(x, y).Buildable)
            {
                kind |= AmbientKind.Water;
            }

            return kind;
        }
    }
}
