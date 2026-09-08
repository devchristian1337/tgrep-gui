using Microsoft.UI.Xaml;

namespace TgrepGui;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Main = new MainWindow();
        Main.Activate();
    }
}
