using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;

namespace TermCity.GodotApp;

internal static class WindowsResizeSmoke
{
    private const uint EnterSizeMove = 0x0231, Sizing = 0x0214, ExitSizeMove = 0x0232, Paint = 0x000F;
    private const uint BottomRight = 8, NoZOrder = 0x0004, NoActivate = 0x0010, AsyncWindowPos = 0x4000;
    private const uint SystemCommand = 0x0112, SizeBottomRight = 0xF008, CancelMode = 0x001F;
    private const uint InMoveSize = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public WindowRect CaretRect;
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

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint parameter, nint data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, System.Text.StringBuilder name,
        uint length, out uint required);

    public static bool IsIsolatedDesktop()
    {
        const string prefix = "TermCityResize_";
        string value = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value[prefix.Length..], "N", out _)) return false;
        nint input = OpenInputDesktop(0, false, 0x0001);
        if (input == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return value != DesktopName(input); }
        finally
        {
            if (!CloseDesktop(input)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string DesktopName(nint desktop)
    {
        var name = new System.Text.StringBuilder(256);
        if (desktop == 0 || !GetUserObjectInformation(desktop, 2, name, (uint)name.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }

    public static async Task<double> RunModal(Window window, Vector2I? finalSize = null)
    {
        if (!IsIsolatedDesktop())
            throw new InvalidOperationException("Native modal resize smoke requires a private TermCity diagnostic desktop.");
        nint handle = (nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId());
        if (handle == 0) throw new InvalidOperationException("Native modal resize check has no window handle.");
        var clientSize = window.Size;
        return await Task.Run(() =>
        {
            if (!GetWindowRect(handle, out var original)) throw new Win32Exception(Marshal.GetLastWin32Error());
            bool changed = false;
            double worst = 0;
            try
            {
                Post(handle, SystemCommand, SizeBottomRight);
                WaitForSizing(handle, true);
                for (int step = 0; step < 256; step++)
                {
                    var requested = SweepBounds(original, step);
                    var timer = Stopwatch.StartNew();
                    SendSizingMessage(handle, Sizing, BottomRight, ref requested);
                    SetBounds(handle, requested);
                    WaitForBounds(handle, requested);
                    worst = Math.Max(worst, timer.Elapsed.TotalMilliseconds);
                    Thread.Sleep(8);
                    if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    changed |= bounds.Right - bounds.Left != original.Right - original.Left ||
                        bounds.Bottom - bounds.Top != original.Bottom - original.Top;
                    if (!NativeSizing(handle))
                        throw new InvalidOperationException("Native sizing ended before the reversing-resize test finished.");
                }
            }
            finally
            {
                Post(handle, CancelMode, 0);
                WaitForSizing(handle, false);
                if (ReadGuiInfo(handle).Capture != 0)
                    throw new InvalidOperationException("Native sizing retained mouse capture after cancellation.");
                var final = original;
                if (finalSize is { } size)
                {
                    final.Right += size.X - clientSize.X;
                    final.Bottom += size.Y - clientSize.Y;
                }
                SetBounds(handle, final);
                WaitForBounds(handle, final);
            }
            if (!changed) throw new InvalidOperationException("Native modal sizing did not change the window dimensions.");
            return worst;
        });
    }

    private static bool NativeSizing(nint handle)
    {
        var info = ReadGuiInfo(handle);
        return (info.Flags & InMoveSize) != 0 && info.MoveSize == handle;
    }

    private static GuiThreadInfo ReadGuiInfo(nint handle)
    {
        uint thread = GetWindowThreadProcessId(handle, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    private static void WaitForSizing(nint handle, bool sizing)
    {
        var timer = Stopwatch.StartNew();
        while (NativeSizing(handle) != sizing)
        {
            if (timer.Elapsed.TotalSeconds >= 5)
                throw new InvalidOperationException($"Native sizing did not {(sizing ? "start" : "finish")} within five seconds.");
            Thread.Sleep(10);
        }
    }

    private static void WaitForBounds(nint handle, WindowRect expected)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (bounds.Left == expected.Left && bounds.Top == expected.Top &&
                bounds.Right == expected.Right && bounds.Bottom == expected.Bottom) return;
            if (timer.Elapsed.TotalSeconds >= 5)
                throw new InvalidOperationException("Native sizing did not apply the requested window bounds within five seconds.");
            Thread.Sleep(2);
        }
    }

    private static void Post(nint handle, uint message, nuint parameter, nint data = 0)
    {
        if (!PostMessage(handle, message, parameter, data))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

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
                for (int step = 0; step < 256; step++)
                {
                    var bounds = SweepBounds(original, step);
                    var timer = Stopwatch.StartNew();
                    SendSizingMessage(handle, Sizing, BottomRight, ref bounds);
                    SetBounds(handle, bounds);
                    SendMessage(handle, Paint, 0, 0);
                    worst = Math.Max(worst, timer.Elapsed.TotalMilliseconds);
                    Thread.Sleep(3);
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

    private static WindowRect SweepBounds(WindowRect bounds, int step)
    {
        int sweep = step % 32;
        int extent = sweep < 16 ? sweep : 31 - sweep;
        bounds.Right = bounds.Left + 720 + extent * 80;
        bounds.Bottom = bounds.Top + 520 + extent * 32;
        return bounds;
    }

    private static void SetBounds(nint handle, WindowRect bounds)
    {
        if (!SetWindowPos(handle, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            NoZOrder | NoActivate | AsyncWindowPos))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
