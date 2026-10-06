using System.Diagnostics;

namespace TermCity.App;

/// <summary>
/// Optional timing trace for chasing performance problems. Set the environment variable TERMCITY_TRACE to a file path
/// and the game writes one line per slow event: the time of day, what it was, and how many milliseconds it took.
/// Nothing is recorded (and nothing costs anything) when the variable is not set.
/// </summary>
internal sealed class FrameTrace
{
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastFlush;

    /// <summary>The active trace, if tracing is on, so any part of the interface can add to it.</summary>
    public static FrameTrace? Current { get; private set; }

    private int _mouseEvents;

    /// <summary>Records the first few mouse events seen by the map, to show what the terminal really sends.</summary>
    public void RecordMouse(string description)
    {
        if (_mouseEvents++ < 60)
        {
            Record("mouse " + description, 0);
        }
    }

    private FrameTrace(string path) => _writer = new StreamWriter(path, append: false) { AutoFlush = false };

    public static FrameTrace? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable("TERMCITY_TRACE");
        return Current = string.IsNullOrWhiteSpace(path) ? null : new FrameTrace(path);
    }

    public void Record(string what, double milliseconds)
    {
        _writer.WriteLine($"{_clock.ElapsedMilliseconds},{what},{milliseconds:F1}");
        if (_clock.ElapsedMilliseconds - _lastFlush > 500)
        {
            _lastFlush = _clock.ElapsedMilliseconds;
            _writer.Flush();
        }
    }
}
