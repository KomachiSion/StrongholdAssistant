using System.Windows;

namespace StrongholdAssistant.Host;

public static class WorkbenchGeometry
{
    // 工作区放不下时，以放进工作区为先。
    public static Size MinimumSize(Rect work, double uiScale = 1) =>
        new(Math.Min(960 * uiScale, work.Width), Math.Min(640 * uiScale, work.Height));

    public static FreeWindowBounds Calculate(Rect work, double uiScale, FreeWindowBounds? saved = null)
    {
        var minimum = MinimumSize(work, uiScale);
        var width = Math.Clamp(saved?.Width ?? 1280 * uiScale, minimum.Width, work.Width);
        var height = Math.Clamp(saved?.Height ?? 800 * uiScale, minimum.Height, work.Height);
        var left = saved?.Left ?? work.Left + (work.Width - width) / 2;
        var top = saved?.Top ?? work.Top + (work.Height - height) / 2;
        return new(Math.Clamp(left, work.Left, work.Right - width),
            Math.Clamp(top, work.Top, work.Bottom - height), width, height);
    }
}
