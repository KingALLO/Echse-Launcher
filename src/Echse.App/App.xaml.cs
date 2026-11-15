using Microsoft.UI.Xaml;

namespace Echse.App;

public partial class App : Application
{
    private MainWindow? window;
    private Mutex? instanceMutex;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(LocalServices.DataDirectory)));
        instanceMutex = new Mutex(true, @"Local\EchseLauncher-" + identity, out var firstInstance);
        if (!firstInstance)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            window = new MainWindow();
            window.Activate();
            window.ShowStartupMessage("Echse läuft bereits.",
                "Bitte verwende das bereits geöffnete Fenster. Eine zweite Instanz kann deine Bibliothek nicht verändern.");
            return;
        }
        window = new MainWindow();
        window.Closed += (_, _) =>
        {
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
            instanceMutex = null;
        };
        window.Activate();
        await window.InitializeAsync();
    }
}
