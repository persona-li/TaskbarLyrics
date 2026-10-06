using TaskbarLyrics.Windows;
namespace TaskbarLyrics.App.Services;
public static class OverlayPlacementMotion
{
    public const int ExitMs = 140;
    public const int EnterMs = 240;
    public const double Travel = 72;
    public static (double Offset, double Opacity) SideFrame(TaskbarLyricSide destination, bool entering, double progress, double travel = Travel)
    {
        var t = Math.Clamp(progress, 0, 1);
        var direction = destination == TaskbarLyricSide.Right ? 1 : -1;
        return entering
            ? (direction * travel * Math.Pow(1 - t, 3), 1 - Math.Pow(1 - t, 2))
            : (-direction * travel * t * t, 1 - t);
    }
    public static TaskbarInterval VisibleBand(OverlayTargetRect frame, TaskbarInterval safe)
    {
        var left = Math.Max(frame.Left, safe.Left);
        return new(left, Math.Max(left, Math.Min(frame.Right, safe.Right)));
    }
}
