using Godot;

namespace TermCity.GodotApp;

public partial class PauseDialog : TerminalFrame
{
    private static readonly string[] Facts =
    [
        "Medieval manuscripts were often written on parchment made from animal skins.",
        "Watermills powered more than grain grinding: some also helped full cloth and work metal.",
        "A medieval town's market rights could be granted by a royal charter.",
        "Before mechanical clocks became common, bells helped mark the hours of the day.",
        "Many medieval towns used walls and gates to control entry and collect tolls.",
        "The three-field system rotated winter crops, spring crops and fallow land.",
        "Guilds could regulate training, workmanship and trade within a town.",
        "Medieval builders used wooden scaffolding to construct great stone cathedrals.",
        "A manuscript's decorated initial could turn its first letter into a tiny work of art.",
        "Medieval fairs brought merchants together to trade goods from distant regions.",
        "Some medieval bridges carried shops and houses as well as traffic.",
        "Rushlights made from plant stems dipped in fat offered a cheaper alternative to candles.",
    ];

    private readonly Label _fact;
    private readonly Label _hint;
    private int _factIndex = -1;

    internal string Fact => _fact.Text;
    internal int FactChanges { get; private set; }

    public PauseDialog()
    {
        Name = "PauseDialog";
        JoinAdjacent = false;
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", 12);
        var title = new Label { Name = "PauseTitle", Text = "PAUSED",
            HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", new Color("#ffe066"));
        content.AddChild(Framed(title, "PauseHeader"));
        _fact = new Label { Name = "PauseFact", HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        content.AddChild(_fact);
        _hint = new Label { Name = "PauseHint", Text = "Press SPACEBAR to unpause",
            HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        content.AddChild(Framed(_hint, "PauseFooter"));
        AddChild(content);
    }

    internal void SetState(bool paused, bool show)
    {
        show &= paused;
        if (show && !Visible)
        {
            int next = Random.Shared.Next(Facts.Length - (_factIndex >= 0 ? 1 : 0));
            if (_factIndex >= 0 && next >= _factIndex) next++;
            _factIndex = next;
            _fact.Text = Facts[next];
            FactChanges++;
        }
        Visible = show;
    }

    private static TerminalFrame Framed(Label label, string name)
    {
        var frame = new TerminalFrame { Name = name, JoinAdjacent = false };
        frame.AddChild(label);
        return frame;
    }

    internal void Fit(Rect2 mapBounds, Vector2 windowSize)
    {
        float width = Math.Max(1, Math.Min(mapBounds.Size.X, windowSize.X - mapBounds.Position.X));
        float contentWidth = Math.Max(1, Math.Min(420, width - Inset * 2));
        _fact.CustomMinimumSize = new Vector2(contentWidth, 0);
        _fact.AddThemeFontSizeOverride("font_size", width < 260 ? 12 : 16);
        _hint.AddThemeFontSizeOverride("font_size", width < 260 ? 10 : 14);
        Size = GetCombinedMinimumSize();
        Position = new Vector2(
            Math.Clamp(mapBounds.GetCenter().X - Size.X / 2, mapBounds.Position.X,
                Math.Max(mapBounds.Position.X, windowSize.X - Size.X)),
            Math.Clamp(mapBounds.GetCenter().Y - Size.Y / 2, mapBounds.Position.Y,
                Math.Max(mapBounds.Position.Y, windowSize.Y - Size.Y)));
    }
}
