namespace TermCity.Core.Session;

public sealed partial class GameSession
{
    /// <summary>Whether vacant zoning and service-range guides are visible, independently of map tint.</summary>
    public bool AreaOverlaysVisible { get; private set; }

    public void SetAreaOverlays(bool visible)
    {
        if (AreaOverlaysVisible == visible) return;
        AreaOverlaysVisible = visible;
        Changed?.Invoke();
    }

    public void ToggleAreaOverlays() => SetAreaOverlays(!AreaOverlaysVisible);
}
