using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TgrepGui.Core;

namespace TgrepGui.Controls;

public sealed class MatchText : ContentControl
{
    public SearchMatch? Match { get => (SearchMatch?)GetValue(MatchProperty); set => SetValue(MatchProperty, value); }
    public static readonly DependencyProperty MatchProperty = DependencyProperty.Register(nameof(Match), typeof(SearchMatch),
        typeof(MatchText), new PropertyMetadata(null, OnMatchChanged));
    private static void OnMatchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MatchText)sender;
        var block = new RichTextBlock { IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 13, TextWrapping = TextWrapping.NoWrap };
        if (args.NewValue is SearchMatch match)
        {
            var paragraph = new Paragraph(); paragraph.Inlines.Add(new Run { Text = match.Text }); block.Blocks.Add(paragraph);
            var highlight = new TextHighlighter { Background = new SolidColorBrush(Colors.PaleGoldenrod), Foreground = new SolidColorBrush(Colors.Black) };
            foreach (var span in match.Highlights) highlight.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
            block.TextHighlighters.Add(highlight);
        }
        control.Content = block;
    }
}
