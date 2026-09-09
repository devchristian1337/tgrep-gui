using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TgrepGui.Core;
using TgrepGui.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace TgrepGui;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public string DownloadHint => RuntimeInformation.OSArchitecture == Architecture.Arm64
        ? "Choose tgrep-v*-aarch64-pc-windows-msvc.zip, extract it, and select tgrep.exe in Settings."
        : "Choose tgrep-v*-x86_64-pc-windows-msvc.zip, extract it, and select tgrep.exe in Settings.";
    private bool initialized, shuttingDown, canClose;
    private CancellationTokenSource? copyLogFeedback;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new();
        Root.DataContext = ViewModel;
        ViewModel.ThemeChanged += ApplyTheme;
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += async (_, args) =>
        {
            if (canClose) return;
            args.Cancel = true;
            if (shuttingDown) return;
            shuttingDown = true; Root.IsHitTestVisible = false;
            try { await ViewModel.ShutdownAsync(); canClose = true; Close(); }
            catch (Exception ex) { shuttingDown = false; Root.IsHitTestVisible = true; ViewModel.ReportError(ex); }
        };
        Root.Loaded += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            ApplyWindowSize();
            await ViewModel.InitializeAsync();
            Navigation.SelectedItem = SearchNav;
            QueryBox.Focus(FocusState.Programmatic);
        };
    }

    private void ApplyWindowSize()
    {
        const int width = 1500, height = 920;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int w = Math.Min(width, work.Width), h = Math.Min(height, work.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.PreferredMinimumWidth = 960;
            overlapped.PreferredMinimumHeight = 720;
        }
    }

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = Root.RequestedTheme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode
        };
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (SettingsFrame == null || SearchPage == null || ViewModel == null) return;
        bool settings = args.IsSettingsSelected;
        if (settings && ViewModel.IsBusy) { Navigation.SelectedItem = SearchNav; return; }
        SearchPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsFrame.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (settings) SettingsFrame.Content = new Views.SettingsPage(ViewModel, this);
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    public async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }
    private async void BrowseFolder_Click(object sender, RoutedEventArgs args)
    {
        try { if (await PickFolderAsync() is { } path) ViewModel.SetFolder(path); }
        catch (Exception ex) { ViewModel.ReportError(ex); }
    }
    private void Folder_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        => ViewModel.SetFolder(args.SelectedItem.ToString() ?? "");
    private void Folder_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen) ViewModel.SetFolder(chosen);
        else ViewModel.RequestPrepare();
    }
    private void Folder_LostFocus(object sender, RoutedEventArgs args) => ViewModel.RequestPrepare();
    private void Query_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && ViewModel.SearchCommand.CanExecute(null))
        { ViewModel.SearchCommand.Execute(null); args.Handled = true; }
    }
    private void FocusQuery_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { Navigation.SelectedItem = SearchNav; QueryBox.Focus(FocusState.Programmatic); QueryBox.SelectAll(); args.Handled = true; }
    private void Cancel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { ViewModel.CancelCommand.Execute(null); args.Handled = true; }
    private void OpenLine_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { OpenSelected(); args.Handled = true; }
    private void Match_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element != null)
        {
            if (element is FrameworkElement { DataContext: SearchMatch match }) { Open(match.FullPath, match.LineNumber); return; }
            element = VisualTreeHelper.GetParent(element);
        }
        OpenSelected();
    }
    private void OpenSelected_Click(object sender, RoutedEventArgs args) => OpenSelected();
    private void OpenSelected()
    {
        if (MatchList.SelectedItem is SearchMatch match) Open(match.FullPath, match.LineNumber);
        else if (ViewModel.SelectedFile is { } file) Open(file.FullPath, file.Lines.FirstOrDefault()?.LineNumber ?? 1);
    }
    private void Open(string file, long line)
    {
        try { EditorLauncher.Open(file, line, ViewModel.Settings); }
        catch (Exception ex) { ViewModel.ReportError(ex); }
    }
    private void FileOpen_Click(object sender, RoutedEventArgs args)
    { if (((MenuFlyoutItem)sender).Tag is string path) Open(path, 1); }
    private void FileCopy_Click(object sender, RoutedEventArgs args)
    { if (((MenuFlyoutItem)sender).Tag is string path) Copy(path); }
    private void FileFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (((MenuFlyoutItem)sender).Tag is not string path) return;
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); Process.Start(info);
        }
        catch (Exception ex) { ViewModel.ReportError(ex); }
    }
    private void CopyMatches_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { CopyMatches(); args.Handled = true; }
    private void CopyMatches_Click(object sender, RoutedEventArgs args) => CopyMatches();
    private void CopyMatches()
    {
        string? selection = SelectedTextIn(MatchList);
        Copy(selection ?? string.Join(Environment.NewLine, MatchList.SelectedItems.OfType<SearchMatch>()
            .OrderBy(x => x.LineNumber).Select(x => $"{x.FullPath}:{x.LineNumber}: {x.Text}")));
    }
    private static string? SelectedTextIn(DependencyObject parent)
    {
        if (parent is RichTextBlock { SelectedText.Length: > 0 } block) return block.SelectedText;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (SelectedTextIn(VisualTreeHelper.GetChild(parent, i)) is { } text) return text;
        return null;
    }
    private void LogFlyout_Opening(object sender, object args)
    {
        // Keep the header inside the popup viewport; only the list owns scrolling.
        LogPanel.Width = Math.Max(1, Math.Min(620, Root.ActualWidth - 64));
        LogPanel.Height = Math.Max(1, Math.Min(360, Root.ActualHeight - 96));
        CopyLogButton.Content = "Copy log";
    }
    private async void CopyLog_Click(object sender, RoutedEventArgs args)
    {
        if (!Copy(string.Join(Environment.NewLine, ViewModel.Logs))) return;
        await ShowCopyLogFeedbackAsync("Copied");
    }
    private async Task ShowCopyLogFeedbackAsync(string text)
    {
        copyLogFeedback?.Cancel();
        copyLogFeedback = new CancellationTokenSource();
        var token = copyLogFeedback.Token;
        CopyLogButton.Content = text;
        try
        {
            await Task.Delay(1600, token);
            CopyLogButton.Content = "Copy log";
        }
        catch (TaskCanceledException) { }
    }
    private bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); Clipboard.Flush();
            return true;
        }
        catch (Exception ex) { ViewModel.ReportError(ex); return false; }
    }
}
