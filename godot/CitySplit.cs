using Godot;

namespace TermCity.GodotApp;

public partial class CitySplit : HSplitContainer
{
    public const int MinimumSidebar = 280, MaximumSidebar = 480, DefaultSidebar = 280;
    public float SidebarWidth => GetChild<Control>(1).Size.X;
    public const int Divider = TerminalFrame.Inset;

    public override void _Ready()
    {
        DraggerVisibility = DraggerVisibilityEnum.Hidden;
        MouseDefaultCursorShape = CursorShape.Hsize;
        AddThemeConstantOverride("separation", Divider);
        SplitOffsets = [-DefaultSidebar];
        Resized += UpdateBounds;
        UpdateBounds();
    }

    public void SetSidebarWidth(float width)
    {
        if (!float.IsFinite(width)) throw new ArgumentOutOfRangeException(nameof(width));
        int maximum = Math.Max(MinimumSidebar, Math.Min(MaximumSidebar, (int)Size.X - 320 - Divider));
        int target = (int)Math.Round(Math.Clamp(width, MinimumSidebar, maximum));
        SplitOffsets = [-target];
    }

    private void UpdateBounds()
    {
        GetChild<Control>(0).CustomMinimumSize = new Vector2(
            Math.Max(96, Math.Min(320, Size.X - MinimumSidebar - Divider)), 0);
        GetChild<Control>(1).CustomMinimumSize = new Vector2(MinimumSidebar, 0);
        GetChild<Control>(0).CustomMinimumSize = new Vector2(
            Math.Max(GetChild<Control>(0).CustomMinimumSize.X, Size.X - MaximumSidebar - Divider), 0);
    }
}
