// ================== สวิตช์ DEBUG LOG ==================
// เปิด : ให้บรรทัดด้านล่างทำงาน
// ปิด  : ใส่ // หน้าบรรทัด  →  //#define DEBUG_LOG_ENABLE
#define DEBUG_LOG_ENABLE
// =====================================================

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace EmmcUltra.Services;

public enum LogKind { Info, Ok, Warn, Error, Debug }

public sealed class LogLine
{
    public LogLine(string text, LogKind kind) { Text = text; Kind = kind; }
    public string Text { get; }
    public LogKind Kind { get; }

    private static SolidColorBrush? s_info, s_ok, s_warn, s_err, s_dbg;

    // Brush ถูกสร้างตอน UI ดึงค่า (อยู่บน UI thread เสมอ)
    public Brush Brush => Kind switch
    {
        LogKind.Ok => s_ok ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0x39, 0xFF, 0x88)),
        LogKind.Warn => s_warn ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0xFC, 0xEE, 0x0A)),
        LogKind.Error => s_err ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0xFF, 0x4D, 0x6D)),
        LogKind.Debug => s_dbg ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0xB0, 0x7C, 0xFF)),
        _ => s_info ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0xC8, 0xD2, 0xE6)),
    };
}

public sealed class AppLog
{
#if DEBUG_LOG_ENABLE
    public const bool DebugEnabled = true;
#else
    public const bool DebugEnabled = false;
#endif
    public const int MaxPacketBytes = 64;    // แสดง hex ของแต่ละแพ็กเก็ตไม่เกินกี่ไบต์
    public const int MaxLines = 5000;        // จำนวนบรรทัดสูงสุดบนหน้าจอ
    private const int MaxPending = 20000;    // กันหน่วยความจำบวมตอน dump ใหญ่ ๆ

    public static AppLog Current { get; } = new();

    public ObservableCollection<LogLine> Lines { get; } = new();
    public event Action? Flushed;

    private readonly ConcurrentQueue<LogLine> _pending = new();
    private readonly object _fileLock = new();
    private StreamWriter? _file;
    private DispatcherQueueTimer? _timer;
    private int _dropped;

    private AppLog() { }

    /// เรียกครั้งเดียวจาก UI thread: อัปเดตหน้าจอเป็นชุด ๆ ทุก 100 ms (ไม่ให้ UI กระตุก)
    public void Attach(DispatcherQueue dq)
    {
        if (_timer != null) return;
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    // ---------- API ----------
    public void Info(string m) => Write(LogKind.Info, m);
    public void Ok(string m) => Write(LogKind.Ok, m);
    public void Warn(string m) => Write(LogKind.Warn, m);
    public void Error(string m) => Write(LogKind.Error, m);
    public void Dbg(string m) { if (DebugEnabled) Write(LogKind.Debug, m); }

    /// ข้อมูลที่ GUI ส่งไป STM32 (ระดับไบต์)
    public void Tx(string label, ReadOnlySpan<byte> data) { if (DebugEnabled) Packet("TX >>", label, data); }
    /// ข้อมูลที่ GUI ได้รับจาก STM32 (ระดับไบต์)
    public void Rx(string label, ReadOnlySpan<byte> data) { if (DebugEnabled) Packet("RX <<", label, data); }

    private void Packet(string dir, string label, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        sb.Append(dir).Append(' ').Append(label).Append(" (").Append(data.Length).Append(" bytes)");
        int n = Math.Min(data.Length, MaxPacketBytes);
        for (int off = 0; off < n; off += 16)
        {
            int len = Math.Min(16, n - off);
            sb.Append('\n').Append("    ").Append(off.ToString("X4")).Append(": ");
            for (int i = 0; i < 16; i++)
                sb.Append(i < len ? data[off + i].ToString("X2") + " " : "   ");
            sb.Append(" |");
            for (int i = 0; i < len; i++)
            {
                byte b = data[off + i];
                sb.Append((b >= 0x20 && b < 0x7F) ? (char)b : '.');
            }
            sb.Append('|');
        }
        if (data.Length > n) sb.Append("\n    ... +").Append(data.Length - n).Append(" bytes (ไม่แสดง)");
        Write(LogKind.Debug, sb.ToString());
    }

    public void Write(LogKind kind, string msg)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss:fff");
        string tag = kind == LogKind.Debug ? "[DBUG]" : "[LOG] ";
        foreach (var raw in msg.Split('\n'))
        {
            string text = $"{stamp} {tag} {raw.TrimEnd('\r')}";
            lock (_fileLock) { _file?.WriteLine(text); }        // ไฟล์ได้ครบทุกบรรทัดเสมอ
            if (_pending.Count < MaxPending) _pending.Enqueue(new LogLine(text, kind));
            else Interlocked.Increment(ref _dropped);
        }
    }

    private void Flush()
    {
        lock (_fileLock) { _file?.Flush(); }
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (_pending.IsEmpty && dropped == 0) return;

        if (dropped > 0)
            Lines.Add(new LogLine($"{DateTime.Now:HH:mm:ss:fff} [LOG]  ... ข้ามการแสดง {dropped} บรรทัด (log เร็วเกินไป — ไฟล์ log ยังบันทึกครบ)", LogKind.Warn));

        int n = 0;
        while (n++ < 1000 && _pending.TryDequeue(out var l)) Lines.Add(l);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        Flushed?.Invoke();
    }

    public void Clear() => Lines.Clear();

    // ---------- Log file ----------
    public bool IsFileEnabled { get { lock (_fileLock) { return _file != null; } } }

    public void OpenFile(string path)
    {
        lock (_fileLock)
        {
            _file?.Dispose();
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var fs = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _file = new StreamWriter(fs, new UTF8Encoding(true));
        }
    }

    public void CloseFile()
    {
        lock (_fileLock) { _file?.Flush(); _file?.Dispose(); _file = null; }
    }
}