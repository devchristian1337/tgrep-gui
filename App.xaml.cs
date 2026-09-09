using Microsoft.UI.Xaml;

namespace TgrepGui;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;
    public App()
    {
        UnhandledException += (_, e) =>
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tgrep-gui");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O} {e.Message}\n{e.Exception}\n");
            }
            catch { }
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Main = new MainWindow();
        Main.Activate();
    }
}
