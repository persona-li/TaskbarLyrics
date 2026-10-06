using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace TaskbarLyrics.App.Controls;

// Reserve six positions, placing the visible circles at both content edges.
// The larger hover/click targets may extend into the popup's existing padding.
public sealed class RecentColorPanel : Panel
{
    public const int Capacity = 6;
    public const double CircleSize = 28;
    private const double TargetSize = 38;

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(TargetSize, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width)
            ? Capacity * TargetSize : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var step = Math.Max(0, finalSize.Width - CircleSize) / (Capacity - 1);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(i * step - (TargetSize - CircleSize) / 2,
                0, TargetSize, finalSize.Height));
        return finalSize;
    }
}
