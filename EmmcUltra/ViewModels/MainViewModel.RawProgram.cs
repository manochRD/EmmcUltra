using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmcUltra.Models;
using EmmcUltra.Services;

namespace EmmcUltra.ViewModels;   // ⚠️ ใช้ namespace เดียวกับ MainViewModel เดิม

public partial class MainViewModel
{
    public ObservableCollection<RawProgramEntry> RawEntries { get; } = new();
    [ObservableProperty] public partial string? RawXmlPath { get; set; }
    [ObservableProperty] public partial string? RawSummary { get; set; }
    [ObservableProperty] public partial bool RawVerify { get; set; }

    private const int RawChunkSectors = 64;      // 32 KB ต่อคำสั่ง ต้องไม่เกิน buffer ของ firmware
    private CancellationTokenSource? _rawCts;

    // ---------- LOAD XML ----------
    [RelayCommand]
    private async Task LoadRawProgramAsync()
    {
        string? path = await PickOpenFile!();          // ⚠️ ADAPT ตาม signature ของคุณ
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var list = RawProgramParser.Parse(path, DiskSectorsOf);
            RawEntries.Clear();
            foreach (var e in list) RawEntries.Add(e);
            RawXmlPath = path;
            UpdateRawSummary();
            RawLog($"RAWPROGRAM: โหลด {list.Count} รายการจาก {Path.GetFileName(path)}");
        }
        catch (Exception ex) { RawLog($"RAWPROGRAM: โหลดไม่สำเร็จ — {ex.Message}"); TryAutoLoadPatch(path); }
    }

    [RelayCommand]
    private void RawToggleAll()
    {
        bool v = RawEntries.Any(e => e.FileFound && !e.Sparse && !e.IsSelected);
        foreach (var e in RawEntries.Where(e => e.FileFound && !e.Sparse)) e.IsSelected = v;
        UpdateRawSummary();
    }

    private void UpdateRawSummary()
    {
        var sel = RawEntries.Where(e => e.IsSelected).ToList();
        ulong bytes = sel.Aggregate(0UL, (s, e) => s + BytesToWrite(e));
        RawSummary = $"{sel.Count}/{RawEntries.Count} รายการ • {Fmt.Human(bytes)}";
    }

    // ---------- FLASH ----------
    [RelayCommand]
    private async Task FlashRawProgramAsync()
    {
        var jobs = RawEntries.Where(e => e.IsSelected && e.FileFound && !e.Sparse)
                             .OrderBy(e => e.PhysicalPartition)   // ลดการสลับพาร์ทิชัน
                             .ThenBy(e => e.StartSector)
                             .ToList();
        if (jobs.Count == 0) { RawLog("RAWPROGRAM: ไม่มีรายการที่เลือก"); return; }

        // ตรวจขนาดก่อนเริ่ม
        foreach (var e in jobs)
        {
            ulong cap = e.NumPartitionSectors * (ulong)e.SectorSize;
            if (cap > 0 && BytesToWrite(e) > cap)
            {
                e.Status = "FAIL: ไฟล์ใหญ่กว่าพาร์ทิชัน";
                RawLog($"RAWPROGRAM: {e.Label} ไฟล์ {Fmt.Human(BytesToWrite(e))} > พาร์ทิชัน {Fmt.Human(cap)} — ยกเลิกทั้งหมด");
                return;
            }

            ulong disk = DiskSectorsOf(e.PhysicalPartition);
            ulong endLba = e.StartSector + BytesToWrite(e) / (ulong)e.SectorSize;
            if (disk > 0 && endLba > disk)
            {
                e.Status = "FAIL: เกินขนาด eMMC";
                RawLog($"RAWPROGRAM: {e.Label} สิ้นสุดที่ LBA 0x{endLba:X} > ขนาดดิสก์ 0x{disk:X} — eMMC เล็กเกินไป");
                return;
            }
        }


        ulong total = jobs.Aggregate(0UL, (s, e) => s + BytesToWrite(e));
        //if (Confirm != null &&
        //    !await Confirm($"เขียน {jobs.Count} รายการ รวม {Fmt.Human(total)} ลง eMMC?\nข้อมูลเดิมจะถูกเขียนทับ"))  // ⚠️ ADAPT
        //    return;
        if (Confirm != null && !await Confirm("ยืนยันการเขียนข้อมูล", $"เขียน {jobs.Count} รายการ รวม {Fmt.Human(total)} ลง eMMC?\nข้อมูลเดิมจะถูกเขียนทับ"))
            return;


        _rawCts = new CancellationTokenSource();
        var ct = _rawCts.Token;
        var buf = new byte[RawChunkSectors * 512];
        var sw = Stopwatch.StartNew();
        ulong done = 0; int ok = 0, fail = 0;

        RawLog($"RAWPROGRAM: เริ่มเขียน {jobs.Count} รายการ ({Fmt.Human(total)}) verify={RawVerify}");

        foreach (var e in jobs)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                ulong ss = (ulong)e.SectorSize;
                ulong left = BytesToWrite(e);
                ulong lba = e.StartSector;
                e.Status = "กำลังเขียน...";

                await using var fs = new FileStream(e.FullPath!, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read, 1 << 20, useAsync: true);
                fs.Position = (long)(e.FileSectorOffset * ss);

                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int chunk = (int)Math.Min(left, (ulong)buf.Length);
                    Array.Clear(buf, 0, chunk);                       // padding เป็น 0 ที่เซกเตอร์สุดท้าย
                    await ReadUpToAsync(fs, buf, chunk, ct);

                    await WriteSectorsAsync(e.Part, lba, buf.AsMemory(0, chunk), ct);

                    if (RawVerify)
                    {
                        var rb = await ReadSectorsAsync(e.Part, lba, (uint)(chunk / (int)ss), ct);
                        if (!rb.AsSpan(0, chunk).SequenceEqual(buf.AsSpan(0, chunk)))
                            throw new IOException($"Verify ไม่ตรงที่ LBA 0x{lba:X}");
                    }

                    lba += (ulong)chunk / ss;
                    left -= (ulong)chunk;
                    done += (ulong)chunk;

                    double mbps = done / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    ProgressValue = done * 100.0 / total;
                    ProgressText = $"{e.Label}  {Fmt.Human(done)}/{Fmt.Human(total)}  {mbps:F2} MB/s";
                }

                e.Status = RawVerify ? "OK (verified)" : "OK";
                ok++;
                RawLog($"RAWPROGRAM: ✔ {e.Label} @ {e.PartLabel} 0x{e.StartSector:X}");
            }
            catch (OperationCanceledException)
            {
                e.Status = "ยกเลิก";
                RawLog("RAWPROGRAM: ผู้ใช้ยกเลิก");
                break;
            }
            catch (Exception ex)
            {
                e.Status = "FAIL: " + ex.Message;
                fail++;
                RawLog($"RAWPROGRAM: ✘ {e.Label} — {ex.Message}");
            }
        }

        RawLog($"RAWPROGRAM: เสร็จ OK={ok} FAIL={fail} เวลา {sw.Elapsed:mm\\:ss}");

        if (!ct.IsCancellationRequested && fail == 0 && RawAutoPatch && _patches.Count > 0)
            await ApplyPatchCoreAsync(ct);
        else if (fail > 0 && _patches.Count > 0)
            RawLog("PATCH: ข้ามเพราะมีรายการเขียนไม่สำเร็จ — แก้ปัญหาแล้วกด APPLY PATCH เอง");

        _rawCts = null;
    }

    private static ulong BytesToWrite(RawProgramEntry e)
    {
        ulong ss = (ulong)e.SectorSize;
        ulong off = e.FileSectorOffset * ss;
        ulong len = (ulong)e.FileSize > off ? (ulong)e.FileSize - off : 0;
        return (len + ss - 1) / ss * ss;                 // ปัดขึ้นให้เต็มเซกเตอร์
    }

    private static async Task ReadUpToAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) break;                            // EOF ส่วนที่เหลือเป็น 0
            got += n;
        }
    }

    // ================== ⚠️ ADAPT: ผูกกับโค้ดที่มีอยู่แล้ว ==================
    // ใช้ฟังก์ชันเดียวกับที่ WriteAtLbaCommand / PeekCommand เรียกอยู่ตอนนี้
    private async Task WriteSectorsAsync(HwPart part, ulong lba, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await Task.Run(() => _br.Write(lba, data.Span), ct);

    }
    private async Task<byte[]> ReadSectorsAsync(HwPart part, ulong lba, uint count, CancellationToken ct)
    {
        return await Task.Run(() => _br.Read(lba, (int)count), ct);
    }


    // USER = SEC_COUNT จาก EXT_CSD[212..215] (little-endian), BOOT = BOOT_SIZE_MULT[226] * 256
    // ⚠️ ADAPT: _extCsd = byte[512] ที่ได้จาก READ EXT_CSD / Identify ของคุณ
    private ulong DiskSectorsOf(int pp) => _extCsd is { Length: >= 512 } e
        ? pp switch
        {
            0 => BitConverter.ToUInt32(e, 212),   // SEC_COUNT [215:212]
            1 or 2 => (ulong)e[226] * 256,             // BOOT_SIZE_MULT × 128KB / 512
            _ => 0
        }
        : 0;
    
    private void RawLog(string s) { /* เรียก AppLog ของคุณ เช่น AppLog.Current.Log(s) */ }
}