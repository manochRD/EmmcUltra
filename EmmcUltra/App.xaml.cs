using Microsoft.UI.Xaml;

namespace EmmcUltra;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window window = new MainWindow();
        window.Activate();
    }
}