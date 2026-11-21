using EmmcUltra.Models;
using EmmcUltra.Services;
using EmmcUltra.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;


namespace EmmcUltra;

public sealed partial class MainWindow : Window
{
    public MainViewModel VM { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "eMMC//ULTRA Suite — STM32H723 Bridge";
        AppWindow.Resize(new SizeInt32(1600, 920));

        VM.Confirm = ConfirmAsync;
        VM.PickFolder = PickFolderAsync;
        VM.PickOpenFile = PickOpenAsync;
        VM.PickSaveFile = PickSaveAsync;
        VM.RequestShowHex += () => LeftPivot.SelectedIndex = 1;

        AppLog.Current.Flushed += OnLogFlushed;
        Closed += (_, _) => AppLog.Current.CloseFile();

        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.Tick += (_, _) => VM.StatMem = $"Memory Used: {Environment.WorkingSet:N0}";
        t.Start();

        VM.RefreshPortsCommand.Execute(null);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.Tag as string == "log") { LeftPivot.SelectedIndex = 3; return; }
        if (int.TryParse(b.Tag as string, out var i)) RightPivot.SelectedIndex = i;
    }

    private async void PartList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PartitionEntry pe) return;
        var path = await PickOpenAsync();
        if (path is null) return;
        pe.FilePath = path;
        pe.IsSelected = true;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var d = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "ยืนยัน",
            CloseButtonText = "ยกเลิก",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    private T Init<T>(T picker) where T : class
    {
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return picker;
    }

    private async Task<string?> PickFolderAsync()
    {
        var p = Init(new FolderPicker());
        p.FileTypeFilter.Add("*");
        return (await p.PickSingleFolderAsync())?.Path;
    }

    private async Task<string?> PickOpenAsync()
    {
        var p = Init(new FileOpenPicker());
        p.FileTypeFilter.Add("*");
        return (await p.PickSingleFileAsync())?.Path;
    }

    private async Task<string?> PickSaveAsync(string suggestedName)
    {
        var p = Init(new FileSavePicker());
        p.FileTypeChoices.Add("Binary image", new List<string> { ".bin" });
        p.SuggestedFileName = suggestedName;
        return (await p.PickSaveFileAsync())?.Path;
    }

    private void OnLogFlushed()
    {
        // เช็คว่าผู้ใช้เปิด Auto-scroll อยู่ไหม และในลิสต์มีข้อมูลหรือยัง
        if (!VM.LogAutoScroll || LogList.Items.Count == 0) return;

        // ใช้ DispatcherQueue เพื่อให้แน่ใจว่าสั่งเลื่อนหลังจาก UI วาดข้อมูลเสร็จแล้ว
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var lastItem = LogList.Items[LogList.Items.Count - 1];
                LogList.ScrollIntoView(lastItem);
            }
            catch
            {
                // ป้องกันกรณีที่ collection เปลี่ยนแปลงกระทันหันระหว่างกำลังเลื่อน
            }
        });
    }
}