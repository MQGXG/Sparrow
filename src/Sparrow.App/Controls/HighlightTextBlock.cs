using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sparrow.App.Controls;

/// <summary>
/// 支持关键词高亮的文本块。
///
/// 为什么不用 TextBox + Inlines 手工拼：搜索词变化时列表项是整体替换的，
/// 每个项只需要在构造时决定一次高亮片段；把逻辑收进控件，
/// XAML 里就还是「一行绑定」，不用在 VM 里塞 Run 列表。
///
/// 只设置 Inlines，不设置 Text —— Avalonia 的 TextBlock 在同时存在
/// Text 和 Inlines 时行为不明确，避开这个坑。
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<string?> TermProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Term));

    private static readonly IBrush FallbackHighlight = new SolidColorBrush(Color.Parse("#E5A44C"));

    /// <summary>要显示的完整文本。</summary>
    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>要高亮的关键词；空值表示不高亮。</summary>
    public string? Term
    {
        get => GetValue(TermProperty);
        set => SetValue(TermProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceTextProperty || change.Property == TermProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        Inlines ??= [];

        Inlines.Clear();

        var text = SourceText;
        if (string.IsNullOrEmpty(text))
            return;

        var term = Term?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            Inlines.Add(new Run(text));
            return;
        }

        var highlight = HighlightBrush();
        var index = 0;

        while (index < text.Length)
        {
            var hit = text.IndexOf(term, index, StringComparison.CurrentCultureIgnoreCase);

            if (hit < 0)
            {
                Inlines.Add(new Run(text[index..]));
                break;
            }

            if (hit > index)
                Inlines.Add(new Run(text[index..hit]));

            Inlines.Add(new Run(text.Substring(hit, term.Length))
            {
                Foreground = highlight,
                FontWeight = FontWeight.SemiBold
            });

            index = hit + term.Length;
        }
    }

    private IBrush HighlightBrush()
    {
        if (Application.Current is { } app &&
            app.TryFindResource("AccentBrush", app.ActualThemeVariant, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return FallbackHighlight;
    }
}
