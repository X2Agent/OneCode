namespace OneCode.App.Tui;

/// <summary>How <see cref="OverlayHost"/> sizes a <see cref="CenteredOverlay"/>.</summary>
public enum OverlayLayoutMode
{
    /// <summary>Centered dialog; preferred size clamped to a fraction of the host.</summary>
    Dialog,

    /// <summary>Near-fullscreen; fills the host minus a 1-cell margin.</summary>
    Fill,
}
