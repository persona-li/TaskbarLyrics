using TaskbarLyrics.App.Config;
namespace TaskbarLyrics.App.Services;

public enum TaskbarLyricSide { Unknown, Left, Right, Hidden, Manual }
public readonly record struct TaskbarInterval(int Left, int Right)
{
    public int Width => Math.Max(0, Right - Left);
}
public readonly record struct TaskbarRegionChoice(TaskbarLyricSide Side, int Left, int Width)
{
    public bool Visible => Width > 0 && Side != TaskbarLyricSide.Hidden;
}
public static class TaskbarRegionLayout
{
    // Font size is in WPF DIPs. Convert to physical pixels exactly once, like the renderer.
    public static OverlayConfig Resolve(OverlayConfig saved, double fontSize, double dpiScale, int taskbarWidth)
    {
        if (!saved.AutoFit) return saved;
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        int Px(double dip) => (int)Math.Ceiling(dip * scale);
        var minimum = Px(Math.Max(120, fontSize * 7));
        return new OverlayConfig
        {
            AutoFit = true,
            LeftMarginPx = Px(8), RightSafetyMarginPx = Px(20),
            MinWidthPx = minimum,
            MaxWidthPx = Math.Max(minimum, Math.Min(Px(900), (int)(taskbarWidth * .4))),
            WidthRatio = saved.WidthRatio
        };
    }

    public static TaskbarInterval LargestGap(int left, int right, IEnumerable<TaskbarInterval> occupied, int leftInset, int rightInset)
    {
        var best = new TaskbarInterval(left, left);
        var cursor = left;
        foreach (var item in occupied.Where(x => x.Right > left && x.Left < right).OrderBy(x => x.Left))
        {
            Consider(cursor, Math.Clamp(item.Left, left, right));
            cursor = Math.Max(cursor, Math.Min(right, item.Right));
        }
        Consider(cursor, right);
        return best;
        void Consider(int start, int end)
        {
            start = Math.Min(end, start + Math.Max(0, leftInset));
            end = Math.Max(start, end - Math.Max(0, rightInset));
            if (end - start > best.Width) best = new(start, end);
        }
    }
    public static TaskbarRegionChoice Select(TaskbarInterval left, TaskbarInterval right, OverlayConfig config, TaskbarLyricSide previous, int hysteresis)
    {
        var minimum = Math.Max(80, config.MinWidthPx);
        var returnThreshold = minimum + (previous == TaskbarLyricSide.Right ? Math.Max(0, hysteresis) : 0);
        if (left.Width >= returnThreshold) return Place(left, TaskbarLyricSide.Left);
        if (right.Width >= minimum) return Place(right, TaskbarLyricSide.Right);
        // If the right side disappeared, use a fitting left side without waiting for hysteresis.
        if (left.Width >= minimum) return Place(left, TaskbarLyricSide.Left);
        return new(TaskbarLyricSide.Hidden, left.Left, 0);
        TaskbarRegionChoice Place(TaskbarInterval region, TaskbarLyricSide side)
        {
            var width = Math.Min(region.Width, Math.Max(minimum, config.MaxWidthPx));
            return new(side, side == TaskbarLyricSide.Right ? region.Right - width : region.Left, width);
        }
    }
}
