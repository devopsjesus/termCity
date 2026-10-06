using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Views;
using TermCity.App;
using TermCity.Core.Session;

namespace TermCity.Tests;

/// <summary>
/// Runs the real Terminal.Gui application headlessly (ANSI driver, 120x30) and drives it by injecting
/// keyboard and mouse input from a script, so the UI wiring can be tested without a terminal.
/// </summary>
internal sealed class UiHarness
{
    // Direct delivery skips the terminal-input pipeline, whose queued, asynchronous processing made tests flaky.
    private static readonly InputInjectionOptions Direct = new() { Mode = InputInjectionMode.Direct };
    private static readonly InputInjectionOptions Pipeline = new() { Mode = InputInjectionMode.Pipeline, AutoProcess = false };

    private readonly IApplication _app;
    private readonly IInputInjector _injector;

    private UiHarness(IApplication app, GameSession session)
    {
        _app = app;
        Session = session;
        _injector = app.GetInputInjector();
    }

    public GameSession Session { get; }

    public IApplication App => _app;

    public Terminal.Gui.Views.Window Window { get; private set; } = null!;

    public static async Task Run(GameSession session, Func<UiHarness, Task> script)
    {
        await Task.Run(() =>
        {
            using IApplication app = Application.Create();
            app.Init(DriverRegistry.Names.ANSI);
            using var window = new GameApp(session).Attach(app);
            var harness = new UiHarness(app, session) { Window = window };

            var worker = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300);
                    await script(harness).WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    app.Invoke(() => app.RequestStop());
                }
            });

            app.Run(window);
            worker.GetAwaiter().GetResult();
        });
    }

    /// <summary>Runs an action on the application's thread and completes when it has finished.</summary>
    private Task OnUi(Action action)
    {
        var done = new TaskCompletionSource();
        _app.Invoke(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    public async Task Press(Key key)
    {
        await OnUi(() => _injector.InjectKey(key, Direct));
        await Task.Delay(40);
    }

    public async Task Mouse(MouseFlags flags, int x, int y)
    {
        await OnUi(() => _injector.InjectMouse(new Mouse { Flags = flags, ScreenPosition = new Point(x, y) }, Direct));
        await Task.Delay(40);
    }

    public Task QueueMouse(MouseFlags flags, int x, int y) => OnUi(() =>
        _injector.InjectMouse(new Mouse { Flags = flags, ScreenPosition = new Point(x, y) }, Pipeline));

    public Task Resize(int columns, int rows) => OnUi(() =>
    {
        _app.Driver!.SetScreenSize(columns, rows);
        // Headless console polling can restore the driver's default size; constrain the actual UI viewport as well.
        Window.Width = columns;
        Window.Height = rows;
        Window.SetNeedsLayout();
    });

    public Task Paste(string text) => OnUi(() => _app.RaisePasteEvent(text));
    /// <summary>
    /// Every cell's text and colours exactly as the driver holds them, without forcing a redraw first, so tests can
    /// tell whether partial redraws left the screen the same as a full one.
    /// </summary>
    public async Task<string> Cells()
    {
        var text = new System.Text.StringBuilder();
        await OnUi(() =>
        {
            var driver = _app.Driver!;
            var cells = driver.Contents!;
            for (int y = 0; y < driver.Rows; y++)
            {
                for (int x = 0; x < driver.Cols; x++)
                {
                    var cell = cells[y, x];
                    text.Append(cell.Grapheme).Append(cell.Attribute).Append('|');
                }

                text.Append('\n');
            }
        });
        return text.ToString();
    }

    /// <summary>Plain-text snapshot of the terminal contents, after drawing anything that is still pending.</summary>
    public async Task<string> Screen()
    {
        string screen = string.Empty;
        await OnUi(() =>
        {
            _app.LayoutAndDraw(true);
            screen = _app.Driver!.ToString() ?? string.Empty;
        });
        return screen;
    }

    /// <summary>The screen once it contains the text (it can take a moment to catch up on a busy machine).</summary>
    public async Task<string> ScreenWith(string text, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string screen = await Screen();
        while (!screen.Contains(text) && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
            screen = await Screen();
        }

        return screen;
    }
}