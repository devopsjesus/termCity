using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermCity.Core.Session;

namespace TermCity.App.Views;

internal sealed class InteractionView : PanelView
{
    private readonly GameSession _session;
    private readonly Action _restoreFocus;
    private readonly List<(Rectangle Rect, int Index)> _buttons = [];
    private SessionPrompt? _drawnPrompt;
    private int _selected;

    public InteractionView(GameSession session, Action restoreFocus)
    {
        _session = session;
        _restoreFocus = restoreFocus;
        Visible = false;
        CanFocus = true;
    }

    public void Synchronize()
    {
        var prompt = _session.Prompt;
        if (ReferenceEquals(prompt, _drawnPrompt))
        {
            SetNeedsDraw();
            return;
        }

        _drawnPrompt = prompt;
        _selected = 0;
        Visible = prompt is not null;
        if (Visible)
        {
            SetFocus();
        }
        else
        {
            _restoreFocus();
        }

        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_session.Prompt is not { } prompt)
        {
            return true;
        }

        ClearPanel(Colors.PanelBackground);
        _buttons.Clear();
        int width = Math.Max(1, Math.Min(76, Viewport.Width - 4));
        int left = Math.Max(0, (Viewport.Width - width) / 2);
        int row = 1;
        DrawText(left, row++, prompt.Title, Colors.Heading, Colors.PanelBackground);
        row++;
        foreach (string paragraph in prompt.Text.Split('\n'))
        {
            foreach (string line in Wrap(paragraph, width))
            {
                DrawText(left, row++, line, Colors.PanelText, Colors.PanelBackground);
            }
        }

        if (prompt.Input is { } input)
        {
            int available = Math.Max(1, width - 2);
            string visibleInput = input.Length > available ? input[^available..] : input;
            DrawText(left, row++, "> " + visibleInput, Colors.Accent, Colors.PanelBackground);
        }

        row++;
        for (int i = 0; i < prompt.Choices.Count; i++)
        {
            string text = (i == _selected ? "> " : "  ") + $"{i + 1}. {prompt.Choices[i].Label}";
            DrawText(left, row, text, i == _selected ? Colors.Accent : Colors.PanelText, Colors.PanelBackground);
            _buttons.Add((new Rectangle(left, row++, width, 1), i));
        }

        DrawText(left, row++, "Up/Down selects; Enter confirms; Esc cancels.", Colors.PanelDim, Colors.PanelBackground);
        if (_session.MessageKind == MessageKind.Error && _session.MessageVisible)
        {
            foreach (string line in Wrap(_session.Message, width))
            {
                DrawText(left, row++, line, Colors.Bad, Colors.PanelBackground);
            }
        }

        return true;
    }

    protected override bool OnPaste(string text)
    {
        if (_session.Prompt is not { Input: not null } prompt)
        {
            return false;
        }

        prompt.Input += text.Replace("\r", "").Replace("\n", "");
        SetNeedsDraw();
        return true;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        while (text.Length > width)
        {
            int split = text.LastIndexOf(' ', width - 1, width);
            if (split <= 0)
            {
                split = width;
            }

            yield return text[..split];
            text = text[split..].TrimStart();
        }

        yield return text;
    }

    protected override bool OnKeyDown(Key key)
    {
        if (_session.Prompt is not { } prompt)
        {
            return false;
        }

        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.A && prompt.Input is not null)
        {
            prompt.Input = "";
            SetNeedsDraw();
            return true;
        }

        switch (key.KeyCode)
        {
            case KeyCode.Esc: _session.ClosePrompt(); return true;
            case KeyCode.CursorUp: _selected = Math.Max(0, _selected - 1); break;
            case KeyCode.CursorDown: _selected = Math.Min(prompt.Choices.Count - 1, _selected + 1); break;
            case KeyCode.Enter: _session.SelectPrompt(_selected); return true;
            case KeyCode.Backspace when prompt.Input is { Length: > 0 }:
                prompt.Input = prompt.Input[..^1];
                break;
            default:
                if (!key.IsCtrl && !key.IsAlt)
                {
                    int value = key.AsRune.Value;
                    if (prompt.Input is not null && value >= 32 && value != 127)
                    {
                        prompt.Input += key.AsRune.ToString();
                    }
                    else if (prompt.Input is null && value is >= '1' and <= '9')
                    {
                        _session.SelectPrompt(value - '1');
                        return true;
                    }
                }

                break;
        }

        SetNeedsDraw();
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) && mouse.Position is { } p)
        {
            foreach (var (rect, index) in _buttons)
            {
                if (rect.Contains(p))
                {
                    _session.SelectPrompt(index);
                    break;
                }
            }
        }

        return true;
    }
}
