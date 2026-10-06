using System.Diagnostics;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermCity.App.Views;
using TermCity.Core.Session;

namespace TermCity.App;

/// <summary>Builds the Terminal.Gui user interface around a <see cref="GameSession"/> and runs the game loop.</summary>
internal sealed class GameApp
{
    private const int PanelWidth = 34;

    // One title row plus thirteen rows of half-block pixels: twenty-six pixels tall, which a landscape map twenty-eight pixels wide
    // (the panel less a margin of three columns each side) needs once stretched a little taller.
    private const int MinimapHeight = 14;

    private readonly GameSession _session;
    private IApplication? _app;
    private MapView? _map;
    private PopoverMenu? _menu;

    /// <summary>The default and the limits for scrolling redraws per second.</summary>
    public const int DefaultFramesPerSecond = 30, MinFramesPerSecond = 5, MaxFramesPerSecond = 60;

    public GameApp(GameSession session, int framesPerSecond = DefaultFramesPerSecond)
    {
        _session = session;
        _frameMs = 1000 / Math.Clamp(framesPerSecond, MinFramesPerSecond, MaxFramesPerSecond);
    }

    public void Run()
    {
        // The loop runs 25 times a second by default, which makes every key and click wait up to 40 ms. Windows also
        // rounds every sleep up to its 15.6 ms clock tick unless asked for finer ticks, which turned a requested 20 ms
        // pause into 31 ms and the loop into about 32 iterations a second (measured: it drew only 19 frames a second
        // during a drag). Asking for 1 ms ticks and 60 iterations a second gives a smooth drag.
        Application.MaximumIterationsPerSecond = 60;
        using var fineTimer = FineTimer.Begin();

        using IApplication app = Application.Create();
        app.Init();
        using var window = Attach(app);
        app.Run(window);
    }

    // Scrolling redraws are limited to this many milliseconds apart. A scroll step rewrites most of the map, about 18 KB
    // of colour codes, so 60 redraws a second is over a megabyte a second. A terminal that cannot render that fast
    // (or the layers between it and the game, such as an editor's built-in terminal) falls further and further behind,
    // so a drag starts smooth and then everything, clicks included, arrives late. Thirty a second is half the load
    // and still looks smooth; --fps changes it.
    private readonly long _frameMs;

    // Moving the cursor redraws only the cells it touches, which is cheap, so it is never held back for long.
    private const long CursorFrameMs = 15;

    // A gap this long between ticks of the application loop means it was stalled, not just busy.
    private const long StallMs = 400;

    private Window? _window;
    private HudView? _hud;
    private MinimapView? _minimap;
    private MessageBarView? _bottom;
    private InteractionView? _interaction;
    private PlacementConfirmationView? _confirmation;
    private bool _messageShown;
    private long _lastFlush;
    [Flags]
    private enum Pending
    {
        None = 0,

        /// <summary>The camera moved: redraw the map and the minimap.</summary>
        Camera = 1,

        /// <summary>The cursor or selection changed: redraw the map and the status line.</summary>
        Selection = 2,

        /// <summary>Anything else: redraw the whole window.</summary>
        Everything = 4,
    }

    private Pending _pending;

    /// <summary>Creates the main window and starts the game clock on the application's main loop.</summary>
    internal Window Attach(IApplication app)
    {
        _app = app;
        var window = Build();
        _window = window;

        // Anything but a camera move may change the side panels too; camera moves only need the map and minimap.
        _session.Changed += () => Request(Pending.Everything);
        _session.Changed += () => _interaction?.Synchronize();
        _session.Changed += () => _confirmation?.Synchronize();
        _session.CameraChanged += () => Request(Pending.Camera);
        _session.CameraChanged += () => _confirmation?.Synchronize();
        _session.SelectionChanged += () => Request(Pending.Selection);
        _session.QuitRequested += () => app.RequestStop();

        var clock = Stopwatch.StartNew();
        double last = 0;
        int progressStep = -1;
        // Optional timing trace (see FrameTrace): the length of each main loop iteration and of each draw.
        var trace = FrameTrace.FromEnvironment();
        if (trace is not null)
        {
            var iterationClock = Stopwatch.StartNew();
            double lastIteration = 0;
            app.Iteration += (_, _) =>
            {
                double now = iterationClock.Elapsed.TotalMilliseconds;
                trace.Record("iteration-gap", now - lastIteration);
                lastIteration = now;
            };

            var drawClock = Stopwatch.StartNew();
            double drawStart = 0;
            app.LayoutAndDrawComplete += (_, _) =>
            {
                double now = drawClock.Elapsed.TotalMilliseconds;
                trace.Record("draw-complete-after", now - drawStart);
                drawStart = now;
            };
        }

        if (trace is not null)
        {
            _session.CameraChanged += () => trace.Record("camera-moved", 0);
            _session.SelectionChanged += () => trace.Record("selection-changed", 0);
        }

        long lastLoopTick = Environment.TickCount64;
        long lastDebugRedraw = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(8), () =>
        {
            long tick = Environment.TickCount64;
            long gap = tick - lastLoopTick;
            lastLoopTick = tick;
            _session.RecordLoopGap(gap);
            if (gap > StallMs)
            {
                _map?.SuppressHover();
            }

            if (_session.InputDebug && tick - lastDebugRedraw > 250)
            {
                lastDebugRedraw = tick;
                _bottom?.SetNeedsDraw();
            }

            _map?.EdgeScrollTick();
            Flush();

            // A message gives way to cell details after a few seconds.
            if (_session.MessageVisible != _messageShown)
            {
                _messageShown = _session.MessageVisible;
                _bottom?.SetNeedsDraw();
            }

            double now = clock.Elapsed.TotalSeconds;
            if (now - last >= 0.1)
            {
                _session.Update(now - last);
                last = now;

                // Between days the only thing that changes is the week bar in the top line.
                int step = _session.Game.Day;
                if (step != progressStep)
                {
                    progressStep = step;
                    _hud?.SetNeedsDraw();
                }
            }

            return true;
        });

        return window;
    }

    private void Request(Pending what)
    {
        _pending |= what;
        Flush();
    }

    private void Flush()
    {
        if (_pending == Pending.None)
        {
            return;
        }

        // The loop ticks every 8 ms, so a little slack stops an interval of exactly one frame from missing by a hair and waiting a whole extra tick.
        long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        if (now - _lastFlush < (_pending == Pending.Selection ? CursorFrameMs : _frameMs) - 2)
        {
            return;
        }

        if (_pending.HasFlag(Pending.Everything))
        {
            _window?.SetNeedsDraw();
        }
        else
        {
            bool selectionOnly = _pending == Pending.Selection;
            if (selectionOnly)
            {
                _map?.InvalidateSelection();
            }
            else
            {
                _map?.SetNeedsDraw();
            }

            if (_pending.HasFlag(Pending.Camera))
            {
                _minimap?.SetNeedsDraw();
            }

            if (_pending.HasFlag(Pending.Selection))
            {
                _bottom?.SetNeedsDraw();
            }
        }

        _pending = Pending.None;
        _lastFlush = now;
    }
    private Window Build()
    {
        var window = new Window { BorderStyle = LineStyle.None, Width = Dim.Fill(), Height = Dim.Fill() };

        var hud = _hud = new HudView(_session) { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        var bottom = _bottom = new MessageBarView(_session) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };

        var help = new HelpView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        _map = new MapView(_session, help, () => _menu?.Visible == true || _session.Prompt is not null ||
            (_session.Preview is not null && !_session.RoadToolActive), ShowContextMenu, _session.RequestQuit)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(PanelWidth),
            Height = Dim.Fill(1),
        };

        var minimap = _minimap = new MinimapView(_session)
        {
            X = Pos.AnchorEnd(PanelWidth),
            Y = 1,
            Width = PanelWidth,
            Height = MinimapHeight,
        };

        var zoom = new ZoomBarView(_session)
        {
            X = Pos.AnchorEnd(PanelWidth),
            Y = 1 + MinimapHeight,
            Width = PanelWidth,
            Height = 1,
        };

        var info = new InfoPanelView(_session)
        {
            X = Pos.AnchorEnd(PanelWidth),
            Y = 2 + MinimapHeight,
            Width = PanelWidth,
            Height = Dim.Fill(1),
        };

        _interaction = new InteractionView(_session, () => _map.SetFocus())
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
        };
        _confirmation = new PlacementConfirmationView(_session, _map);
        window.Add(hud, _map, minimap, zoom, info, bottom, _confirmation, help, _interaction);
        _map.SetFocus();
        _interaction.Synchronize();
        _confirmation.Synchronize();
        return window;
    }

    private void ShowContextMenu(Point screen)
    {
        if (_app?.Popovers is not { } popovers)
        {
            return;
        }

        if (_menu is not null)
        {
            popovers.DeRegister(_menu);
            _menu.Dispose();
        }

        _menu = ContextMenuBuilder.Build(_session);
        popovers.Register(_menu);
        _menu.MakeVisible(screen);
    }
}
