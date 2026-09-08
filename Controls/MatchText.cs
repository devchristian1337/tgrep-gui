using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TgrepGui.Core;

namespace TgrepGui.Controls;

public sealed class MatchText : ContentControl
{
    private static readonly FontFamily CodeFont = new("Cascadia Code, Consolas");
    private static readonly SolidColorBrush HighlightBackground = new(Colors.PaleGoldenrod);
    private static readonly SolidColorBrush HighlightForeground = new(Colors.Black);

    public SearchMatch? Match { get => (SearchMatch?)GetValue(MatchProperty); set => SetValue(MatchProperty, value); }
    public static readonly DependencyProperty MatchProperty = DependencyProperty.Register(nameof(Match), typeof(SearchMatch),
        typeof(MatchText), new PropertyMetadata(null, OnMatchChanged));
    private static void OnMatchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MatchText)sender;
        if (args.NewValue is not SearchMatch match)
        {
            control.Content = null;
            return;
        }
        var block = control.Content as RichTextBlock ?? new RichTextBlock
        {
            IsTextSelectionEnabled = true, FontFamily = CodeFont, FontSize = 13, TextWrapping = TextWrapping.NoWrap
        };
        block.Blocks.Clear();
        block.TextHighlighters.Clear();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = match.Text });
        block.Blocks.Add(paragraph);
        var highlight = new TextHighlighter { Background = HighlightBackground, Foreground = HighlightForeground };
        foreach (var span in match.Highlights) highlight.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        block.TextHighlighters.Add(highlight);
        control.Content = block;
    }
}
