using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TgrepGui.ViewModels;

namespace TgrepGui.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MainViewModel model;
    private readonly MainWindow window;
    public SettingsPage(MainViewModel model, MainWindow window)
    {
        InitializeComponent(); this.model = model; this.window = window;
        DataContext = model;
        var settings = model.Settings;
        TgrepPathBox.Text = settings.TgrepPath; IndexPathBox.Text = settings.IndexPath;
        AutoUpdateEngineBox.IsChecked = settings.AutoUpdateEngine;
        EditorPathBox.Text = settings.EditorPath; EditorArgsBox.Text = settings.EditorArguments;
        IgnoreCaseBox.IsChecked = settings.IgnoreCase; LiteralBox.IsChecked = settings.Literal;
        ThemeBox.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        SettingsLocation.Text = model.SettingsPath;
    }
    private async void BrowseTgrep_Click(object sender, RoutedEventArgs args) => await PickAsync(TgrepPathBox, false);
    private async void BrowseEditor_Click(object sender, RoutedEventArgs args) => await PickAsync(EditorPathBox, false);
    private async void BrowseIndex_Click(object sender, RoutedEventArgs args) => await PickAsync(IndexPathBox, true);
    private async Task PickAsync(TextBox target, bool folder)
    {
        try { if (await (folder ? window.PickFolderAsync() : window.PickExecutableAsync()) is { } path) target.Text = path; }
        catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
    }
    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        SaveButton.IsEnabled = false;
        try
        {
            await model.SaveSettingsAsync(model.Settings with
            {
                TgrepPath = TgrepPathBox.Text.Trim().Trim('"'), IndexPath = IndexPathBox.Text.Trim().Trim('"'),
                AutoUpdateEngine = AutoUpdateEngineBox.IsChecked == true,
                EditorPath = EditorPathBox.Text.Trim().Trim('"'), EditorArguments = EditorArgsBox.Text,
                Theme = ((ComboBoxItem)ThemeBox.SelectedItem).Tag.ToString()!,
                IgnoreCase = IgnoreCaseBox.IsChecked == true, Literal = LiteralBox.IsChecked == true
            });
            ShowMessage("Settings saved. Engine changes apply to the next search.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
        finally { SaveButton.IsEnabled = true; }
    }
    private void ShowMessage(string text, InfoBarSeverity severity)
    { SaveInfo.Message = text; SaveInfo.Severity = severity; SaveInfo.IsOpen = true; }
}
