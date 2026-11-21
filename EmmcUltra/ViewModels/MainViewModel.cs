using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmcUltra.Models;
using EmmcUltra.Services;
using Microsoft.UI.Dispatching;

namespace EmmcUltra.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SerialBridge _br = new();
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<PartitionEntry> Partitions { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    // MainWindow เป็นคนกำหนดให้ (dialog / file picker)
    public Func<string, string, Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(false);
    public Func<Task<string?>> PickFolder { get; set; } = () => Task.FromResult<string?>(null);
    public Func<Task<string?>> PickOpenFile { get; set; } = () => Task.FromResult<string?>(null);
    public Func<string, Task<string?>> PickSaveFile { get; set; } = _ => Task.FromResult<string?>(null);
    public event Action? RequestShowHex;

    // ---- Connection ----
    public MainViewModel()
    {
        PortIndex = -1;
        ConnText = "OFFLINE";
        ClkDiv = 4;
        RawStart = "0";
        RawCount = "1";
        HexView = "";
        ExtCsdHex = "";

        ProductName = Manufacturer = Capacity = SpecVersion = MfgDate = "—";
        BootInfo = BootSize = RpmbSize = LifeA = LifeB = PreEol = "—";

        ProgressText = "0%";
        StatAddress = "Address: 0x0";
        StatSize = "Size: 0";
        StatSpeed = "Transfer Speed: 0 KB/s";
        StatEta = "Estimated Time Left: 00:00:00";
        StatMem = "Memory Used: 0";
    }

    // ---- Connection ----
    [ObservableProperty] public partial int PortIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    public partial bool IsConnected { get; set; }
    public string ConnectButtonText => IsConnected ? "DISCONNECT" : "CONNECT";

    [ObservableProperty] public partial string ConnText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double ClkDiv { get; set; }
    [ObservableProperty] public partial int BusIndex { get; set; }

    // ---- Raw ----
    [ObservableProperty] public partial int RawPartIndex { get; set; }
    [ObservableProperty] public partial string RawStart { get; set; }
    [ObservableProperty] public partial string RawCount { get; set; }
    [ObservableProperty] public partial string HexView { get; set; }
    [ObservableProperty] public partial string ExtCsdHex { get; set; }

    // ---- Info ----
    [ObservableProperty] public partial string ProductName { get; set; }
    [ObservableProperty] public partial string Manufacturer { get; set; }
    [ObservableProperty] public partial string Capacity { get; set; }
    [ObservableProperty] public partial string SpecVersion { get; set; }
    [ObservableProperty] public partial string MfgDate { get; set; }
    [ObservableProperty] public partial string BootInfo { get; set; }
    [ObservableProperty] public partial string BootSize { get; set; }
    [ObservableProperty] public partial string RpmbSize { get; set; }
    [ObservableProperty] public partial string LifeA { get; set; }
    [ObservableProperty] public partial string LifeB { get; set; }
    [ObservableProperty] public partial string PreEol { get; set; }

    // ---- Progress / Status ----
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; }
    [ObservableProperty] public partial string StatAddress { get; set; }
    [ObservableProperty] public partial string StatSize { get; set; }
    [ObservableProperty] public partial string StatSpeed { get; set; }
    [ObservableProperty] public partial string StatEta { get; set; }
    [ObservableProperty] public partial string StatMem { get; set; }

    // ================= helpers =================
    private void Ui(Action a) { if (_ui.HasThreadAccess) a(); else _ui.TryEnqueue(() => a()); }

    public void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Ui(() => { Logs.Add(line); if (Logs.Count > 2000) Logs.RemoveAt(0); });
    }

    private async Task<bool> RunAsync(string title, Action<CancellationToken> work)
    {
        if (IsBusy) { Log("!! มีงานอื่นกำลังทำงานอยู่"); return false; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => work(_cts.Token));   // งาน I/O ทั้งหมดอยู่นอก UI thread
            return true;
        }
        catch (OperationCanceledException) { Log($"{title}: ยกเลิกแล้ว"); }
        catch (Exception ex) { Log($"!! {title} FAILED: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            var c = _cts; _cts = null; c?.Dispose();
            IsBusy = false;
        }
        return false;
    }

    private void OnProgress(DumpProgress p)
    {
        ProgressValue = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
        ProgressText = $"{ProgressValue:0}%  ·  {Fmt.Human((ulong)p.Done)} / {Fmt.Human((ulong)p.Total)}";
        StatAddress = $"Address: 0x{p.Lba:X8}";
        StatSize = $"Size: {Fmt.Human((ulong)p.Total)}";
        StatSpeed = $"Transfer Speed: {p.BytesPerSec / 1024:0.0} KB/s";
        StatEta = $"Estimated Time Left: {p.Eta:hh\\:mm\\:ss}";
    }

    private Progress<DumpProgress> NewProgress() => new(OnProgress);

    // ================= Connection =================
    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in SerialPort.GetPortNames().OrderBy(x => x.Length).ThenBy(x => x)) Ports.Add(p);
        if (Ports.Count > 0 && PortIndex < 0) PortIndex = 0;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            _br.Close(); IsConnected = false; ConnText = "OFFLINE"; Log("Disconnected");
            return;
        }
        if (PortIndex < 0 || PortIndex >= Ports.Count) { Log("!! เลือก COM Port ก่อน"); return; }

        string port = Ports[PortIndex];
        if (!await RunAsync("Connect", _ => _br.Open(port))) return;
        IsConnected = true;
        ConnText = $"ONLINE · {port}";
        Log($"Connected {port}");
        await IdentifyAsync();
    }

    [RelayCommand]
    private async Task ApplyClockAsync()
    {
        int div = (int)ClkDiv;
        int bus = BusIndex switch { 1 => 4, 2 => 8, _ => 1 };
        if (await RunAsync("Apply CLK", _ => _br.SetClock(div, bus)))
            Log($"CLKDIV={div}, BUS={bus}-bit");
    }

    [RelayCommand]
    private async Task IdentifyAsync()
    {
        byte[] cid = [], ext = [];
        if (!await RunAsync("Identify", _ => { cid = _br.ReadCid(); ext = _br.ReadExtCsd(); })) return;

        EmmcInfo i;
        try { i = EmmcInfo.Decode(cid, ext); }
        catch (Exception ex) { Log($"!! Decode: {ex.Message}"); return; }

        ProductName = i.Product; Manufacturer = i.Manufacturer; Capacity = i.Capacity;
        SpecVersion = i.Spec; MfgDate = i.MfgDate; BootInfo = i.BootPartition;
        BootSize = Fmt.Size(i.BootBytes); RpmbSize = Fmt.Size(i.RpmbBytes);
        LifeA = i.LifeA; LifeB = i.LifeB; PreEol = i.PreEol;
        ExtCsdHex = Fmt.Hex(ext, 0);

        foreach (var p in Partitions.Where(x => x.Part != HwPart.User).ToList()) Partitions.Remove(p);
        ulong bs = i.BootBytes / 512;
        if (bs > 0)
        {
            Partitions.Insert(0, new PartitionEntry { Part = HwPart.Boot1, Name = "BOOT1", StartLba = 0, EndLba = bs - 1, Type = "HW Boot" });
            Partitions.Insert(1, new PartitionEntry { Part = HwPart.Boot2, Name = "BOOT2", StartLba = 0, EndLba = bs - 1, Type = "HW Boot" });
        }
        Log($"Identify OK: {i.Product} | {i.Manufacturer} | {i.Capacity}");
    }

    // ================= GPT =================
    [RelayCommand]
    private async Task ReadGptAsync()
    {
        byte[] d = [];
        if (!await RunAsync("Read GPT", _ => { _br.SelectPart(HwPart.User); d = _br.Read(1, 33); })) return;

        List<PartitionEntry> list;
        try { list = GptParser.Parse(d); }
        catch (Exception ex) { Log($"!! GPT: {ex.Message}"); return; }

        foreach (var p in Partitions.Where(x => x.Part == HwPart.User).ToList()) Partitions.Remove(p);
        foreach (var p in list) Partitions.Add(p);
        Log($"GPT: พบ {list.Count} พาร์ทิชัน");
    }

    [RelayCommand]
    private async Task ForceWriteGptAsync()
    {
        var f = await PickOpenFile();
        if (f is null) return;
        if (!await Confirm("FORCE WRITE GPT",
            $"จะเขียน {Path.GetFileName(f)} ลง USER ตั้งแต่ LBA 0\nพาร์ทิชันเดิมจะถูกแทนที่ทั้งหมด ต้องการดำเนินการต่อหรือไม่?")) return;

        var prog = NewProgress();
        if (await RunAsync("Force Write GPT", ct => DumpEngine.WriteFromFile(_br, HwPart.User, 0, f, prog, ct)))
        {
            Log("Force Write GPT สำเร็จ");
            await ReadGptAsync();
        }
    }

    // ================= Partition dump / write =================
    [RelayCommand] private void ToggleSelectAll()
    {
        bool v = !Partitions.All(p => p.IsSelected);
        foreach (var p in Partitions) p.IsSelected = v;
    }

    [RelayCommand] private Task DumpSelectedAsync() =>
        DumpAsync(Partitions.Where(p => p.IsSelected).ToList());

    [RelayCommand] private Task DumpAllAsync() =>
        DumpAsync(Partitions.Where(p => p.Part == HwPart.User).ToList());

    [RelayCommand] private Task DumpExUserdataAsync() =>
        DumpAsync(Partitions.Where(p => p.Part == HwPart.User &&
            !p.Name.Equals("userdata", StringComparison.OrdinalIgnoreCase)).ToList());

    [RelayCommand] private Task BackupBootAsync() =>
        DumpAsync(Partitions.Where(p => p.Part is HwPart.Boot1 or HwPart.Boot2).ToList());

    private async Task DumpAsync(List<PartitionEntry> parts)
    {
        if (parts.Count == 0) { Log("!! ไม่มีพาร์ทิชันให้ดัมพ์ (กด READ GPT / IDENTIFY ก่อน)"); return; }
        var folder = await PickFolder();
        if (folder is null) return;

        var prog = NewProgress();
        var sw = Stopwatch.StartNew();
        bool ok = await RunAsync("Dump", ct =>
        {
            foreach (var p in parts)
            {
                ct.ThrowIfCancellationRequested();
                Ui(() => p.Status = "Reading...");
                Log($">> {p.PartLabel}/{p.Name} ({Fmt.Human(p.Sectors * 512)})");
                var path = Path.Combine(folder, $"{p.PartLabel}_{p.SafeName}_0x{p.StartLba:X8}.bin");
                try
                {
                    DumpEngine.ReadToFile(_br, p.Part, p.StartLba, p.Sectors, path, prog, ct);
                    Ui(() => p.Status = "Read OK");
                }
                catch { Ui(() => p.Status = "Failed"); throw; }
            }
        });
        if (ok) Log($"Dump เสร็จ {parts.Count} พาร์ทิชัน ใช้เวลา {sw.Elapsed:hh\\:mm\\:ss}");
    }

    [RelayCommand]
    private async Task WriteSelectedAsync()
    {
        var parts = Partitions.Where(p => p.IsSelected && p.FilePath is not null).ToList();
        if (parts.Count == 0) { Log("!! เลือกพาร์ทิชันแล้วดับเบิลคลิกช่อง Filename เพื่อเลือกไฟล์ก่อน"); return; }

        foreach (var p in parts)
            if ((ulong)new FileInfo(p.FilePath!).Length > p.Sectors * 512)
            { Log($"!! {p.Name}: ไฟล์ใหญ่กว่าพาร์ทิชัน"); return; }

        if (!await Confirm("ยืนยันการเขียน",
            $"จะเขียนทับ {parts.Count} พาร์ทิชัน:\n{string.Join(", ", parts.Select(p => p.Name))}\n\nข้อมูลเดิมจะหายถาวร")) return;

        var prog = NewProgress();
        await RunAsync("Write", ct =>
        {
            foreach (var p in parts)
            {
                ct.ThrowIfCancellationRequested();
                Ui(() => p.Status = "Writing...");
                try
                {
                    DumpEngine.WriteFromFile(_br, p.Part, p.StartLba, p.FilePath!, prog, ct);
                    Ui(() => p.Status = "Write OK");
                    Log($"Write OK: {p.Name}");
                }
                catch { Ui(() => p.Status = "Failed"); throw; }
            }
        });
    }

    // ================= Raw sector =================
    private bool TryRaw(out HwPart part, out ulong lba, out ulong count)
    {
        part = (HwPart)RawPartIndex;
        bool ok = Fmt.TryParseNum(RawStart, out lba) & Fmt.TryParseNum(RawCount, out count);
        if (!ok || count == 0) { Log("!! START LBA / COUNT ไม่ถูกต้อง (ใช้เลขฐาน 10 หรือ 0x...)"); return false; }
        return true;
    }

    [RelayCommand]
    private async Task PeekAsync()
    {
        if (!TryRaw(out var part, out var lba, out var cnt)) return;
        int n = (int)Math.Min(cnt, 8UL);
        byte[] d = [];
        if (await RunAsync("Peek", _ => { _br.SelectPart(part); d = _br.Read(lba, n); }))
        {
            HexView = Fmt.Hex(d, lba * 512);
            RequestShowHex?.Invoke();
        }
    }

    [RelayCommand]
    private async Task ReadRangeAsync()
    {
        if (!TryRaw(out var part, out var lba, out var cnt)) return;
        var path = await PickSaveFile($"{part}_0x{lba:X8}_{cnt}.bin");
        if (path is null) return;
        var prog = NewProgress();
        if (await RunAsync("Read Range", ct => DumpEngine.ReadToFile(_br, part, lba, cnt, path, prog, ct)))
            Log($"Read Range OK → {path}");
    }

    [RelayCommand]
    private async Task WriteAtLbaAsync()
    {
        if (!TryRaw(out var part, out var lba, out _)) return;
        var f = await PickOpenFile();
        if (f is null) return;
        if (!await Confirm("WRITE FILE @ LBA", $"เขียน {Path.GetFileName(f)} ลง {part} ตั้งแต่ LBA 0x{lba:X}?")) return;
        var prog = NewProgress();
        if (await RunAsync("Write @ LBA", ct => DumpEngine.WriteFromFile(_br, part, lba, f, prog, ct)))
            Log("Write @ LBA OK");
    }

    [RelayCommand]
    private void Abort()
    {
        try { _cts?.Cancel(); Log("Abort requested"); } catch (ObjectDisposedException) { }
    }
}