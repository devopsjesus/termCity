using System.Runtime.InteropServices;

namespace TermCity.App;

/// <summary>
/// On Windows, asks for a 1 ms system timer resolution for as long as it is alive. The default is 15.6 ms, which makes
/// every sleep in the application loop last a multiple of that and caps the loop at about 32 iterations a second.
/// Does nothing on other platforms, where sleeps are already precise.
/// </summary>
internal sealed class FineTimer : IDisposable
{
    private readonly bool _active;

    private FineTimer(bool active) => _active = active;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    public static FineTimer Begin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FineTimer(false);
        }

        try
        {
            return new FineTimer(TimeBeginPeriod(1) == 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new FineTimer(false);
        }
    }

    public void Dispose()
    {
        if (_active)
        {
            TimeEndPeriod(1);
        }
    }
}
