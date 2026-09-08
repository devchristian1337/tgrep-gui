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
    private static readonly SolidColorBrush LightBackground = new(Colors.PaleGoldenrod);
    private static readonly SolidColorBrush DarkBackground = new(Colors.Goldenrod);
    private static readonly SolidColorBrush HighlightForeground = new(Colors.Black);

    public MatchText()
    {
        ActualThemeChanged += (_, _) => { if (Match is { } match) Render(this, match); };
    }

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
        Render(control, match);
    }

    private static void Render(MatchText control, SearchMatch match)
    {
        var block = control.Content as RichTextBlock ?? new RichTextBlock
        {
            IsTextSelectionEnabled = true, FontFamily = CodeFont, FontSize = 13, TextWrapping = TextWrapping.NoWrap
        };
        block.Blocks.Clear();
        block.TextHighlighters.Clear();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = match.Text });
        block.Blocks.Add(paragraph);
        bool dark = control.ActualTheme == ElementTheme.Dark
            || (control.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var highlight = new TextHighlighter
        {
            Background = dark ? DarkBackground : LightBackground,
            Foreground = HighlightForeground
        };
        foreach (var span in match.Highlights) highlight.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        block.TextHighlighters.Add(highlight);
        control.Content = block;
    }
}
