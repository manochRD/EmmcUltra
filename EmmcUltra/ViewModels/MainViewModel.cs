using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmcUltra.Models;
using EmmcUltra.Services;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace EmmcUltra.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int ConnectTimeoutMs = 4000;   // รอ STM32 ตอบตอน CONNECT
    private const int PeekMaxSectors = 8;        // PEEK แสดงสูงสุดกี่ sector

    private readonly SerialBridge _br = new();
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly AppLog _log = AppLog.Current;
    private CancellationTokenSource? _cts;
    private volatile bool _userCancel;
    private byte[] _lastCid = [], _lastExt = [];

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<PartitionEntry> Partitions { get; } = new();
    public ObservableCollection<InfoRow> InfoRows { get; } = new();
    public ObservableCollection<LogLine> LogLines => _log.Lines;
    public string DebugBadge => AppLog.DebugEnabled ? "DEBUG LOG: ON" : "DEBUG LOG: OFF";

    // MainWindow เป็นคนกำหนดให้ (dialog / file picker)
    public Func<string, string, Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(false);
    public Func<Task<string?>> PickFolder { get; set; } = () => Task.FromResult<string?>(null);
    public Func<Task<string?>> PickOpenFile { get; set; } = () => Task.FromResult<string?>(null);
    public Func<string, Task<string?>> PickSaveFile { get; set; } = _ => Task.FromResult<string?>(null);
    public Func<string, Task<string?>> PickLogFile { get; set; } = _ => Task.FromResult<string?>(null);
    public event Action? RequestShowHex;

    public MainViewModel()
    {
        _log.Attach(_ui);

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

        LogAutoScroll = true;
        LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                   "EmmcUltra", $"emmc_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        _log.Info("EmmcUltra เริ่มทำงาน");
        _log.Info($"Debug log: {(AppLog.DebugEnabled ? "เปิด" : "ปิด")} (สลับได้ที่ #define DEBUG_LOG_ENABLE ใน AppLog.cs)");
    }

    // ---- Connection ----
    [ObservableProperty] public partial int PortIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    public partial bool IsConnecting { get; set; }

    public string ConnectButtonText => IsConnected ? "DISCONNECT" : IsConnecting ? "CANCEL" : "CONNECT";

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

    // ---- Advanced log ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseLogFileCommand))]
    public partial bool LogToFile { get; set; }
    [ObservableProperty] public partial string LogFilePath { get; set; }
    [ObservableProperty] public partial bool LogAutoScroll { get; set; }

    partial void OnLogToFileChanged(bool value) => ApplyLogFile();
    partial void OnLogFilePathChanged(string value) { if (LogToFile) ApplyLogFile(); }

    // ================= helpers =================
    private void Ui(Action a) { if (_ui.HasThreadAccess) a(); else _ui.TryEnqueue(() => a()); }

    /// เข้ากันได้กับโค้ดเดิม: ข้อความขึ้นต้นด้วย "!!" = error
    public void Log(string msg)
    {
        if (msg.StartsWith("!!")) _log.Error(msg); else _log.Info(msg);
    }

    private async Task<bool> RunAsync(string title, Action<CancellationToken> work)
    {
        if (IsBusy) { _log.Warn("!! มีงานอื่นกำลังทำงานอยู่ — รอให้เสร็จหรือกด ABORT"); return false; }
        if (!IsConnected) { _log.Error($"!! {title}: ยังไม่ได้เชื่อมต่อ — เลือก COM Port แล้วกด CONNECT ก่อน"); return false; }

        IsBusy = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var sw = Stopwatch.StartNew();
        _log.Dbg($"BEGIN {title}");
        try
        {
            await Task.Run(() => work(cts.Token));     // งาน I/O ทั้งหมดอยู่นอก UI thread
            _log.Dbg($"END {title} ({sw.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (OperationCanceledException) { _log.Warn($"{title}: ยกเลิกแล้ว"); }
        catch (TimeoutException ex) { _log.Error($"!! {title}: อุปกรณ์ไม่ตอบสนอง (timeout) — {ex.Message}"); }
        catch (Exception ex) { _log.Error($"!! {title} FAILED: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            _cts = null; cts.Dispose();
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

    private static void SetClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    // ================= Connection =================
    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in SerialPort.GetPortNames().OrderBy(x => x.Length).ThenBy(x => x)) Ports.Add(p);
        if (Ports.Count > 0 && PortIndex < 0) PortIndex = 0;
        _log.Info(Ports.Count == 0 ? "ไม่พบ COM Port" : $"พบ COM Port {Ports.Count} พอร์ต: {string.Join(", ", Ports)}");
    }

    // AllowConcurrentExecutions = true → ปุ่มกดได้ตลอด (CANCEL / DISCONNECT ได้แม้กำลังเชื่อมต่อ)
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting)
        {
            await DisconnectAsync(IsConnecting ? "ผู้ใช้กด CANCEL" : "ผู้ใช้กด DISCONNECT");
            return;
        }
        if (PortIndex < 0 || PortIndex >= Ports.Count) { _log.Error("!! เลือก COM Port ก่อน"); return; }

        string port = Ports[PortIndex];
        _userCancel = false;
        IsConnecting = true;
        IsBusy = true;
        ConnText = $"CONNECTING · {port}";
        _log.Info($"CONNECT: กำลังเปิดพอร์ต {port} (timeout {ConnectTimeoutMs / 1000.0:0.#} วินาที)");

        byte[] cid = [], ext = [];
        var sw = Stopwatch.StartNew();
        var work = Task.Run(() =>
        {
            _br.Open(port);
            _log.Info($"เปิดพอร์ต {port} สำเร็จ — ทดสอบการตอบสนองของ STM32 โดยอ่าน CID");
            cid = _br.ReadCid();
            _log.Info($"ได้รับ CID {cid.Length} ไบต์ — อ่าน EXT_CSD ต่อ");
            ext = _br.ReadExtCsd();
            _log.Info($"ได้รับ EXT_CSD {ext.Length} ไบต์");
        });

        try
        {
            var first = await Task.WhenAny(work, Task.Delay(ConnectTimeoutMs));
            if (first != work)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);   // กัน unobserved exception
                throw new TimeoutException($"STM32 ไม่ตอบภายใน {ConnectTimeoutMs / 1000.0:0.#} วินาที");
            }
            await work;
        }
        catch (Exception ex)
        {
            await ClosePortSafeAsync();
            IsConnecting = false; IsConnected = false; IsBusy = false;
            ConnText = "OFFLINE";
            if (_userCancel) _log.Warn("ยกเลิกการเชื่อมต่อแล้ว");
            else
            {
                _log.Error($"!! เชื่อมต่อ {port} ไม่สำเร็จ: {ex.GetType().Name}: {ex.Message}");
                _log.Error("!! พอร์ตนี้อาจไม่ใช่ STM32 CDC, ถูกโปรแกรมอื่นใช้อยู่ หรือเฟิร์มแวร์ไม่ตอบ — ลองเลือกพอร์ตอื่น");
            }
            return;
        }

        IsConnecting = false; IsBusy = false; IsConnected = true;
        ConnText = $"ONLINE · {port}";
        _log.Ok($"Connected {port} ({sw.ElapsedMilliseconds} ms)");
        ApplyIdentify(cid, ext);
    }

    private async Task DisconnectAsync(string reason)
    {
        _userCancel = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        await ClosePortSafeAsync();                 // ปิดพอร์ต → Read ที่ค้างอยู่จะหลุดทันที
        IsConnected = false; IsConnecting = false;
        ConnText = "OFFLINE";
        _log.Info($"Disconnected ({reason})");
    }

    private async Task ClosePortSafeAsync()
    {
        var t = Task.Run(() =>
        {
            try { _br.Close(); }
            catch (Exception ex) { _log.Warn($"Close: {ex.Message}"); }
        });
        if (await Task.WhenAny(t, Task.Delay(2000)) != t)
            _log.Warn("!! ปิดพอร์ตไม่ทันใน 2 วินาที (ไดรเวอร์ค้าง) — ถอดสาย USB แล้วเสียบใหม่");
    }

    [RelayCommand]
    private async Task ApplyClockAsync()
    {
        int div = (int)ClkDiv;
        int bus = BusIndex switch { 1 => 4, 2 => 8, _ => 1 };
        _log.Info($"APPLY CLK: ตั้ง CLKDIV={div}, BUS WIDTH={bus}-bit");
        if (await RunAsync("Apply CLK", _ => _br.SetClock(div, bus)))
            _log.Ok($"CLKDIV={div}, BUS={bus}-bit ตั้งค่าสำเร็จ");
    }

    [RelayCommand]
    private async Task IdentifyAsync()
    {
        _log.Info("IDENTIFY: อ่าน CID (CMD2/CMD10) + EXT_CSD (CMD8)");
        byte[] cid = [], ext = [];
        if (!await RunAsync("Identify", _ => { cid = _br.ReadCid(); ext = _br.ReadExtCsd(); })) return;
        ApplyIdentify(cid, ext);
    }

    private void ApplyIdentify(byte[] cid, byte[] ext)
    {
        _lastCid = cid; _lastExt = ext;

        EmmcInfo i;
        try { i = EmmcInfo.Decode(cid, ext); }
        catch (Exception ex) { _log.Error($"!! Decode: {ex.Message}"); return; }

        ProductName = i.Product; Manufacturer = i.Manufacturer; Capacity = i.Capacity;
        SpecVersion = i.Spec; MfgDate = i.MfgDate; BootInfo = i.BootPartition;
        BootSize = Fmt.Size(i.BootBytes); RpmbSize = Fmt.Size(i.RpmbBytes);
        LifeA = i.LifeA; LifeB = i.LifeB; PreEol = i.PreEol;
        ExtCsdHex = HexDump.Format(ext, 0, false);

        _log.Ok($"IDENTIFY OK: {i.Product} | {i.Manufacturer} | {i.Capacity} | {i.Spec}");
        RebuildInfoRows(logAll: true);

        foreach (var p in Partitions.Where(x => x.Part != HwPart.User).ToList()) Partitions.Remove(p);
        ulong bs = i.BootBytes / 512;
        if (bs > 0)
        {
            Partitions.Insert(0, new PartitionEntry { Part = HwPart.Boot1, Name = "BOOT1", StartLba = 0, EndLba = bs - 1, Type = "HW Boot" });
            Partitions.Insert(1, new PartitionEntry { Part = HwPart.Boot2, Name = "BOOT2", StartLba = 0, EndLba = bs - 1, Type = "HW Boot" });
            _log.Info($"เพิ่มพาร์ทิชันฮาร์ดแวร์ BOOT1/BOOT2: {Fmt.Size(i.BootBytes)} ต่อพาร์ทิชัน ({bs:N0} sectors)");
        }
        else _log.Warn("BOOT_SIZE_MULT = 0 → อุปกรณ์นี้ไม่มีพาร์ทิชัน BOOT");
    }

    private void RebuildInfoRows(bool logAll)
    {
        var rows = EmmcDecoder.Decode(_lastCid, _lastExt);
        InfoRows.Clear();
        foreach (var r in rows) InfoRows.Add(r);
        if (!logAll) return;

        _log.Info($"ถอดรหัส CID + EXT_CSD ตาม JESD84-B51 ได้ {rows.Count} รายการ:");
        foreach (var r in rows)
            _log.Info($"  {r.Group,-9} {r.Field,-34} {r.Byte,-10} {r.Raw,-14} {r.Value}");
    }

    [RelayCommand]
    private void CopyInfo()
    {
        if (InfoRows.Count == 0) { _log.Warn("ยังไม่มีข้อมูล eMMC — กด CONNECT / IDENTIFY ก่อน"); return; }
        var sb = new StringBuilder();
        sb.AppendLine($"eMMC REPORT  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var r in InfoRows)
            sb.AppendLine($"{r.Group,-9} {r.Field,-34} {r.Byte,-10} {r.Raw,-24} {r.Value}");
        SetClipboard(sb.ToString());
        _log.Info($"คัดลอกรายงาน eMMC ({InfoRows.Count} รายการ) ไปยังคลิปบอร์ดแล้ว");
    }

    // ================= GPT =================
    [RelayCommand]
    private async Task ReadGptAsync()
    {
        _log.Info("READ GPT: เลือก USER area → อ่าน LBA 1..33 (GPT header + partition entries)");
        byte[] d = [];
        if (!await RunAsync("Read GPT", _ => { _br.SelectPart(HwPart.User); d = _br.Read(1, 33); })) return;

        string sig = new string(d.Take(8).Select(b => (b >= 0x20 && b < 0x7F) ? (char)b : '.').ToArray());
        _log.Info($"ได้รับ {d.Length} ไบต์, signature = \"{sig}\" (ต้องเป็น \"EFI PART\")");

        List<PartitionEntry> list;
        try { list = GptParser.Parse(d); }
        catch (Exception ex) { _log.Error($"!! GPT: {ex.Message}"); return; }

        foreach (var p in Partitions.Where(x => x.Part == HwPart.User).ToList()) Partitions.Remove(p);
        foreach (var p in list) Partitions.Add(p);

        _log.Ok($"GPT: พบ {list.Count} พาร์ทิชัน");
        _log.Info($"  {"#",3}  {"NAME",-20} {"START LBA",12} {"END LBA",12} {"SECTORS",14} {"SIZE",11}  TYPE");
        int n = 0;
        ulong total = 0;
        foreach (var p in list)
        {
            total += p.Sectors * 512;
            _log.Info($"  {++n,3}  {p.Name,-20} {"0x" + p.StartLba.ToString("X"),12} {"0x" + p.EndLba.ToString("X"),12} " +
                      $"{p.Sectors,14:N0} {Fmt.Human(p.Sectors * 512),11}  {p.Type}");
        }
        _log.Info($"รวมพื้นที่ที่ใช้โดยพาร์ทิชัน: {Fmt.Human(total)}");
    }

    [RelayCommand]
    private async Task ForceWriteGptAsync()
    {
        var f = await PickOpenFile();
        if (f is null) { _log.Info("FORCE WRITE GPT: ผู้ใช้ยกเลิก"); return; }
        if (!await Confirm("FORCE WRITE GPT",
            $"จะเขียน {Path.GetFileName(f)} ลง USER ตั้งแต่ LBA 0\nพาร์ทิชันเดิมจะถูกแทนที่ทั้งหมด ต้องการดำเนินการต่อหรือไม่?"))
        { _log.Info("FORCE WRITE GPT: ผู้ใช้ไม่ยืนยัน"); return; }

        _log.Warn($"FORCE WRITE GPT: เขียน {f} ({Fmt.Human((ulong)new FileInfo(f).Length)}) → USER @ LBA 0");
        var prog = NewProgress();
        if (await RunAsync("Force Write GPT", ct => DumpEngine.WriteFromFile(_br, HwPart.User, 0, f, prog, ct)))
        {
            _log.Ok("Force Write GPT สำเร็จ — อ่าน GPT ใหม่เพื่อตรวจสอบ");
            await ReadGptAsync();
        }
    }

    // ================= Partition dump / write =================
    [RelayCommand]
    private void ToggleSelectAll()
    {
        bool v = !Partitions.All(p => p.IsSelected);
        foreach (var p in Partitions) p.IsSelected = v;
    }

    [RelayCommand]
    private Task DumpSelectedAsync() =>
        DumpAsync(Partitions.Where(p => p.IsSelected).ToList());

    [RelayCommand]
    private Task DumpAllAsync() =>
        DumpAsync(Partitions.Where(p => p.Part == HwPart.User).ToList());

    [RelayCommand]
    private Task DumpExUserdataAsync() =>
        DumpAsync(Partitions.Where(p => p.Part == HwPart.User &&
            !p.Name.Equals("userdata", StringComparison.OrdinalIgnoreCase)).ToList());

    [RelayCommand]
    private Task BackupBootAsync() =>
        DumpAsync(Partitions.Where(p => p.Part is HwPart.Boot1 or HwPart.Boot2).ToList());

    private async Task DumpAsync(List<PartitionEntry> parts)
    {
        if (parts.Count == 0) { _log.Error("!! ไม่มีพาร์ทิชันให้ดัมพ์ (กด READ GPT / IDENTIFY ก่อน)"); return; }
        var folder = await PickFolder();
        if (folder is null) { _log.Info("DUMP: ผู้ใช้ยกเลิกการเลือกโฟลเดอร์"); return; }

        ulong total = 0;
        foreach (var p in parts) total += p.Sectors * 512;
        _log.Info($"DUMP: {parts.Count} พาร์ทิชัน รวม {Fmt.Human(total)} → {folder}");

        var prog = NewProgress();
        var sw = Stopwatch.StartNew();
        bool ok = await RunAsync("Dump", ct =>
        {
            int idx = 0;
            foreach (var p in parts)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                Ui(() => p.Status = "Reading...");
                var path = Path.Combine(folder, $"{p.PartLabel}_{p.SafeName}_0x{p.StartLba:X8}.bin");
                _log.Info($"[{idx}/{parts.Count}] อ่าน {p.PartLabel}/{p.Name}: LBA 0x{p.StartLba:X} – 0x{p.EndLba:X} " +
                          $"({p.Sectors:N0} sectors, {Fmt.Human(p.Sectors * 512)})");
                var psw = Stopwatch.StartNew();
                try
                {
                    DumpEngine.ReadToFile(_br, p.Part, p.StartLba, p.Sectors, path, prog, ct);
                    double sec = Math.Max(psw.Elapsed.TotalSeconds, 0.001);
                    Ui(() => p.Status = "Read OK");
                    _log.Ok($"[{idx}/{parts.Count}] {p.Name} OK — {psw.Elapsed:hh\\:mm\\:ss}, " +
                            $"{p.Sectors * 512 / 1024.0 / sec:0.0} KB/s → {Path.GetFileName(path)}");
                }
                catch
                {
                    Ui(() => p.Status = "Failed");
                    _log.Error($"!! [{idx}/{parts.Count}] {p.Name} FAILED");
                    throw;
                }
            }
        });
        if (ok) _log.Ok($"DUMP เสร็จ {parts.Count} พาร์ทิชัน ({Fmt.Human(total)}) ใช้เวลา {sw.Elapsed:hh\\:mm\\:ss}");
    }

    [RelayCommand]
    private async Task WriteSelectedAsync()
    {
        var parts = Partitions.Where(p => p.IsSelected && p.FilePath is not null).ToList();
        if (parts.Count == 0) { _log.Error("!! เลือกพาร์ทิชันแล้วดับเบิลคลิกช่อง Filename เพื่อเลือกไฟล์ก่อน"); return; }

        foreach (var p in parts)
        {
            ulong fileLen = (ulong)new FileInfo(p.FilePath!).Length;
            _log.Info($"ตรวจขนาด {p.Name}: ไฟล์ {Fmt.Human(fileLen)} / พาร์ทิชัน {Fmt.Human(p.Sectors * 512)}");
            if (fileLen > p.Sectors * 512) { _log.Error($"!! {p.Name}: ไฟล์ใหญ่กว่าพาร์ทิชัน — ยกเลิก"); return; }
        }

        if (!await Confirm("ยืนยันการเขียน",
            $"จะเขียนทับ {parts.Count} พาร์ทิชัน:\n{string.Join(", ", parts.Select(p => p.Name))}\n\nข้อมูลเดิมจะหายถาวร"))
        { _log.Info("WRITE: ผู้ใช้ไม่ยืนยัน"); return; }

        var prog = NewProgress();
        await RunAsync("Write", ct =>
        {
            foreach (var p in parts)
            {
                ct.ThrowIfCancellationRequested();
                Ui(() => p.Status = "Writing...");
                _log.Warn($"WRITE {p.PartLabel}/{p.Name} @ LBA 0x{p.StartLba:X} ← {Path.GetFileName(p.FilePath!)}");
                var psw = Stopwatch.StartNew();
                try
                {
                    DumpEngine.WriteFromFile(_br, p.Part, p.StartLba, p.FilePath!, prog, ct);
                    Ui(() => p.Status = "Write OK");
                    _log.Ok($"Write OK: {p.Name} ({psw.Elapsed:hh\\:mm\\:ss})");
                }
                catch
                {
                    Ui(() => p.Status = "Failed");
                    _log.Error($"!! Write FAILED: {p.Name}");
                    throw;
                }
            }
        });
    }

    // ================= Raw sector / HEX =================
    private bool TryRaw(out HwPart part, out ulong lba, out ulong count)
    {
        part = (HwPart)RawPartIndex;
        bool ok = Fmt.TryParseNum(RawStart, out lba) & Fmt.TryParseNum(RawCount, out count);
        if (!ok || count == 0) { _log.Error("!! START LBA / COUNT ไม่ถูกต้อง (ใช้เลขฐาน 10 หรือ 0x...)"); return false; }
        return true;
    }

    [RelayCommand]
    private async Task PeekAsync()
    {
        if (!TryRaw(out var part, out var lba, out var cnt)) return;
        int n = (int)Math.Min(cnt, (ulong)PeekMaxSectors);
        if (cnt > (ulong)PeekMaxSectors)
            _log.Warn($"PEEK แสดงได้สูงสุด {PeekMaxSectors} sectors — ตัดเหลือ {n} (ใช้ READ RANGE ถ้าต้องการมากกว่านี้)");

        _log.Info($"PEEK: เลือก {part} → อ่าน LBA 0x{lba:X} ({lba}) จำนวน {n} sector(s)");
        byte[] d = [];
        if (!await RunAsync("Peek", _ => { _br.SelectPart(part); d = _br.Read(lba, n); })) return;

        if (d.Length == 0) { _log.Warn($"!! PEEK: ไม่ได้รับข้อมูลจาก {part} LBA 0x{lba:X}"); return; }
        HexView = HexDump.Format(d, lba * 512);
        _log.Ok($"PEEK OK: ได้รับ {d.Length} ไบต์ จาก {part}");
        RequestShowHex?.Invoke();
    }

    [RelayCommand]
    private async Task ReadExtCsdAsync()
    {
        _log.Info("READ EXT_CSD: ส่ง CMD8 (SEND_EXT_CSD) อ่าน 512 ไบต์");
        byte[] ext = [];
        if (!await RunAsync("Read EXT_CSD", _ => { ext = _br.ReadExtCsd(); })) return;

        if (ext.Length == 0) { _log.Warn("!! READ EXT_CSD: ไม่ได้รับข้อมูล"); return; }
        _lastExt = ext;
        HexView = HexDump.Format(ext, 0, false);
        ExtCsdHex = HexView;
        _log.Ok($"READ EXT_CSD OK: {ext.Length} ไบต์ (EXT_CSD_REV={(ext.Length > 192 ? ext[192] : 0)})");
        if (_lastCid.Length > 0) RebuildInfoRows(logAll: false);
        RequestShowHex?.Invoke();
    }

    [RelayCommand]
    private async Task ReadRangeAsync()
    {
        if (!TryRaw(out var part, out var lba, out var cnt)) return;
        var path = await PickSaveFile($"{part}_0x{lba:X8}_{cnt}.bin");
        if (path is null) { _log.Info("READ RANGE: ผู้ใช้ยกเลิก"); return; }

        _log.Info($"READ RANGE: {part} LBA 0x{lba:X} จำนวน {cnt:N0} sectors ({Fmt.Human(cnt * 512)}) → {path}");
        var prog = NewProgress();
        var sw = Stopwatch.StartNew();
        if (await RunAsync("Read Range", ct => DumpEngine.ReadToFile(_br, part, lba, cnt, path, prog, ct)))
            _log.Ok($"Read Range OK ({sw.Elapsed:hh\\:mm\\:ss}) → {path}");
    }

    [RelayCommand]
    private async Task WriteAtLbaAsync()
    {
        if (!TryRaw(out var part, out var lba, out _)) return;
        var f = await PickOpenFile();
        if (f is null) { _log.Info("WRITE @ LBA: ผู้ใช้ยกเลิก"); return; }
        if (!await Confirm("WRITE FILE @ LBA", $"เขียน {Path.GetFileName(f)} ลง {part} ตั้งแต่ LBA 0x{lba:X}?"))
        { _log.Info("WRITE @ LBA: ผู้ใช้ไม่ยืนยัน"); return; }

        _log.Warn($"WRITE @ LBA: {f} ({Fmt.Human((ulong)new FileInfo(f).Length)}) → {part} @ 0x{lba:X}");
        var prog = NewProgress();
        if (await RunAsync("Write @ LBA", ct => DumpEngine.WriteFromFile(_br, part, lba, f, prog, ct)))
            _log.Ok("Write @ LBA OK");
    }

    [RelayCommand]
    private void Abort()
    {
        try { _cts?.Cancel(); _log.Warn("Abort requested — รอให้คำสั่งปัจจุบันจบ"); }
        catch (ObjectDisposedException) { }
    }

    // ================= Advanced log =================
    private void ApplyLogFile()
    {
        if (!LogToFile)
        {
            if (_log.IsFileEnabled) { _log.Info("หยุดบันทึก log ลงไฟล์"); _log.CloseFile(); }
            return;
        }
        try
        {
            _log.OpenFile(LogFilePath);
            _log.Info($"เริ่มบันทึก log ต่อท้ายไฟล์: {LogFilePath}");
        }
        catch (Exception ex)
        {
            _log.Error($"!! เปิดไฟล์ log ไม่ได้: {ex.Message}");
            LogToFile = false;
        }
    }

    [RelayCommand(CanExecute = nameof(LogToFile))]
    private async Task BrowseLogFileAsync()
    {
        var p = await PickLogFile(Path.GetFileName(LogFilePath));
        if (p is not null) LogFilePath = p;
    }

    [RelayCommand] private void ClearLog() => _log.Clear();

    [RelayCommand]
    private void CopyLog()
    {
        SetClipboard(string.Join(Environment.NewLine, _log.Lines.Select(l => l.Text)));
        _log.Info($"คัดลอก log {_log.Lines.Count} บรรทัดไปยังคลิปบอร์ดแล้ว");
    }
}