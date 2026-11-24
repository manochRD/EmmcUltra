using Microsoft.UI.Xaml;

namespace EmmcUltra;

public partial class App : Application
{
    // ประกาศตัวแปรเก็บหน้าต่างหลักไว้ใช้ทั่วโปรเจกต์
    public static Window? MainRootWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();

        // เก็บอินสแตนซ์ไว้ใช้งาน
        MainRootWindow = window;

        window.Activate();
    }
}