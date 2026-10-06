namespace TermCity.App;

internal static class PlatformKeys
{
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    public static string Control => IsMacOS ? "Control" : "Ctrl";

    public static string Alt => IsMacOS ? "Option" : "Alt";

    public static string Enter => IsMacOS ? "Return" : "Enter";

    public static string Escape => IsMacOS ? "Escape" : "Esc";

    public static string Delete => IsMacOS ? "Forward Delete" : "Delete";

    public static string FullScreenJump => IsMacOS ? "Fn+Arrow" : "Ctrl+Arrow";
}
