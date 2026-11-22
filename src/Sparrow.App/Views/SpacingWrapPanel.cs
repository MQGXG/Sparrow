using Avalonia;
using Avalonia.Controls;

namespace Sparrow.App.Views;

/// <summary>
/// 带间距的换行面板：属性栏按「整组」折行，组之间保留 Spacing，行之间保留 LineSpacing。
/// Avalonia 自带的 WrapPanel 没有间距，这里只做属性栏需要的最小实现。
/// </summary>
public sealed class SpacingWrapPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SpacingWrapPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<SpacingWrapPanel, double>(nameof(LineSpacing));

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width);

        double lineWidth = 0;
        double lineHeight = 0;
        double maxWidth = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            child.Measure(new Size(limit, double.PositiveInfinity));
            var desired = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + Spacing + desired.Width > limit)
            {
                maxWidth = Math.Max(maxWidth, lineWidth);
                totalHeight += lineHeight + LineSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += (lineWidth > 0 ? Spacing : 0) + desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        maxWidth = Math.Max(maxWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(
            double.IsInfinity(limit) ? maxWidth : Math.Min(maxWidth, limit),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double lineHeight = 0;

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            var size = child.DesiredSize;

            if (x > 0 && x + Spacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + LineSpacing;
                lineHeight = 0;
            }

            if (x > 0)
                x += Spacing;

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
