using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;

namespace TermCity.GodotApp;

internal static class WindowsResizeSmoke
{
    private const uint EnterSizeMove = 0x0231, Sizing = 0x0214, ExitSizeMove = 0x0232, Paint = 0x000F;
    private const uint BottomRight = 8, NoZOrder = 0x0004, NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nuint parameter, nint data);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendSizingMessage(nint window, uint message, nuint parameter, ref WindowRect rect);

    public static async Task<double> Run(Window window)
    {
        nint handle = (nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId());
        if (handle == 0) throw new InvalidOperationException("Windows resize check has no native window handle.");

        // Run the native messages off the scene thread so its event loop can service them normally.
        return await Task.Run(() =>
        {
            if (!GetWindowRect(handle, out var original)) throw new Win32Exception(Marshal.GetLastWin32Error());
            double worst = 0;
            try
            {
                SendMessage(handle, EnterSizeMove, 0, 0);
                for (int step = 0; step < 16; step++)
                {
                    var bounds = original;
                    bounds.Right = bounds.Left + 800 + step * 50;
                    bounds.Bottom = bounds.Top + 600 + step * 20;
                    var timer = Stopwatch.StartNew();
                    SendSizingMessage(handle, Sizing, BottomRight, ref bounds);
                    SetBounds(handle, bounds);
                    SendMessage(handle, Paint, 0, 0);
                    worst = Math.Max(worst, timer.Elapsed.TotalMilliseconds);
                    Thread.Sleep(33);
                }
            }
            finally
            {
                SendMessage(handle, ExitSizeMove, 0, 0);
                SetBounds(handle, original);
            }
            return worst;
        });
    }

    private static void SetBounds(nint handle, WindowRect bounds)
    {
        if (!SetWindowPos(handle, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            NoZOrder | NoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
