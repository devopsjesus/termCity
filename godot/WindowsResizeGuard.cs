using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;

namespace TermCity.GodotApp;

internal sealed class WindowsResizeGuard : IDisposable
{
    private const uint EnterSizeMove = 0x0231, ExitSizeMove = 0x0232, Timer = 0x0113, Destroy = 0x0082;
    private const nuint SubclassId = 0x54435247, MoveRedrawTimer = 1;
    private readonly nint _window;
    private readonly SubclassProcedure _procedure;
    private bool _sizing, _installed;
    public int SuppressedTicks { get; private set; }
    public bool Sizing => _sizing;
    public long ExitTimestamp { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint parameter, nint data, nuint id, nuint reference);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint reference);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint parameter, nint data);

    public WindowsResizeGuard(Window window)
    {
        _window = (nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId());
        _procedure = OnMessage;
        if (_window == 0 || !SetWindowSubclass(_window, _procedure, SubclassId, 0))
            throw new InvalidOperationException("Could not install the Windows resize guard.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        _installed = true;
    }

    private nint OnMessage(nint window, uint message, nuint parameter, nint data, nuint id, nuint reference)
    {
        if (message == EnterSizeMove) _sizing = true;
        // Godot 4.7's timer 1 runs Main::iteration inside the native sizing loop. Let Windows
        // finish that loop before the game draws/updates again; activation timer 2 is unaffected.
        if (_sizing && message == Timer && parameter == MoveRedrawTimer)
        {
            SuppressedTicks++;
            return 0;
        }
        if (message == ExitSizeMove)
        {
            _sizing = false;
            ExitTimestamp = Stopwatch.GetTimestamp();
        }
        if (message == Destroy)
        {
            if (_installed && !RemoveWindowSubclass(_window, _procedure, SubclassId))
                GD.PushError($"Could not remove the Windows resize guard during window destruction: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            _installed = false;
            _sizing = false;
        }
        return DefSubclassProc(window, message, parameter, data);
    }

    public void Dispose()
    {
        if (!_installed) return;
        if (!RemoveWindowSubclass(_window, _procedure, SubclassId))
            throw new InvalidOperationException("Could not remove the Windows resize guard.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        _installed = false;
        _sizing = false;
    }
}
