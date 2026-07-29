using System;
using Avalonia.Controls;

namespace Pinscreen2.App;

/// <summary>
/// A sample of everything that, if it changes, invalidates where the clock
/// popup was placed: the display mode, our window, and the laid-out root.
///
/// Sampled on the clock timer rather than subscribed to via events. A monitor
/// wake raises these in bursts, and reacting to each one is what caused the
/// hangs that moved clock updates onto a timer in the first place. Sampling
/// collapses a burst into a single action and is idempotent.
///
/// Lives outside MainWindow so the decision rules can be tested directly.
/// </summary>
public readonly record struct DisplayGeometry(
    double ScreenWidth, double ScreenHeight, double Scaling,
    double ClientWidth, double ClientHeight,
    double RootWidth, double RootHeight,
    WindowState State)
{
    public bool HasScreen => ScreenWidth > 0 && ScreenHeight > 0 && Scaling > 0;

    /// <summary>
    /// The display mode itself changed, rather than just our window moving or
    /// resizing within an unchanged mode. This is the case that leaves a
    /// fullscreen window sized to the old mode.
    /// </summary>
    public bool ResolutionDiffers(DisplayGeometry other) =>
        Math.Abs(ScreenWidth - other.ScreenWidth) > 0.5 ||
        Math.Abs(ScreenHeight - other.ScreenHeight) > 0.5 ||
        Math.Abs(Scaling - other.Scaling) > 0.001;

    /// <summary>
    /// Whether a fullscreen window actually covers the display, in layout
    /// units. Windows can leave a window flagged fullscreen while still sized
    /// to the previous mode once the display comes back, and anchoring the
    /// popup to those stale bounds is exactly how the clock ends up off-centre.
    ///
    /// True when the screen is unknown: with nothing to compare against,
    /// forcing a window-state toggle would be a guess.
    /// </summary>
    public bool FillsScreen()
    {
        if (!HasScreen) return true;
        return Math.Abs(ClientWidth - ScreenWidth / Scaling) <= 2
            && Math.Abs(ClientHeight - ScreenHeight / Scaling) <= 2;
    }

    /// <summary>True when the window should be re-asserted before re-anchoring the popup.</summary>
    public bool NeedsFullScreenReassert(DisplayGeometry previous) =>
        ResolutionDiffers(previous) && State == WindowState.FullScreen && !FillsScreen();

    public override string ToString() =>
        $"{ScreenWidth:0}x{ScreenHeight:0}@{Scaling:0.##}|client {ClientWidth:0}x{ClientHeight:0}" +
        $"|root {RootWidth:0}x{RootHeight:0}|{State}";
}
