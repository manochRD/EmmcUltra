using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmcUltra.Models;
using EmmcUltra.Services;

namespace EmmcUltra.ViewModels;   // ⚠️ ใช้ namespace เดียวกับ MainViewModel

public partial class MainViewModel
{
    [ObservableProperty] public partial string? GptPrimaryStatus { get; set; }
    [ObservableProperty] public partial string? GptBackupStatus { get; set; }

    private bool _gptWriteBoth = true;
    public bool GptWriteBoth { get => _gptWriteBoth; set => SetProperty(ref _gptWriteBoth, value); }

    private bool _gptExpandLast = true;
    public bool GptExpandLast { get => _gptExpandLast; set => SetProperty(ref _gptExpandLast, value); }

    private const uint GptIoChunk = 32;          // ≤ buffer firmware (อ่าน 33 sectors ได้อยู่แล้วตาม log)

    // ================= COMMANDS =================
    [RelayCommand]
    private Task GptDiagnoseAsync() => RunGptAsync(ct => GptDiagnoseCoreAsync(ct));

    //[RelayCommand]
    //private Task GptDumpMainAsync() => RunGptAsync(async ct =>
    //{
    //    string? path = await PickSaveFile!("gpt_main0.bin");          // ⚠️ ADAPT signature
    //    if (string.IsNullOrEmpty(path)) return;
    //    var data = await GptReadAsync(0, 34, ct);
    //    await File.WriteAllBytesAsync(path, data, ct);
    //    var g = GptTool.LoadMainImage(data, "gpt_main0.bin");
    //    RawLog($"GPT: บันทึก {Path.GetFileName(path)} (LBA 0–33, {data.Length:N0} B) — " +
    //           (g.IsUsable ? $"✔ ข้อมูลถูกต้อง {g.UsedEntries} partitions" : "⚠ ข้อมูลเสีย: " + string.Join(", ", g.Problems)));
    //});
    [RelayCommand]
    private Task GptDumpMainAsync() => RunGptAsync(async ct =>
    {
        // พ่นข้อความไปที่หน้าต่าง Output ของ Visual Studio ทันที
        System.Diagnostics.Debug.WriteLine(">>> DEBUG: ปุ่ม Main ทำงานแล้ว!");

        string? path = await PickSaveFile!("gpt_main0.bin");
        if (string.IsNullOrEmpty(path))
        {
            System.Diagnostics.Debug.WriteLine(">>> DEBUG: ผู้ใช้ยกเลิกการเลือกไฟล์");
            return;
        }

        System.Diagnostics.Debug.WriteLine($">>> DEBUG: เลือกไฟล์สำเร็จ: {path}");

        var data = await GptReadAsync(0, 34, ct);
        System.Diagnostics.Debug.WriteLine($">>> DEBUG: อ่านข้อมูลสำเร็จ ได้ขนาด {data.Length} ไบต์");

        await File.WriteAllBytesAsync(path, data, ct);
        var g = GptTool.LoadMainImage(data, "gpt_main0.bin");

        RawLog($"GPT: บันทึก {Path.GetFileName(path)} (LBA 0-33, {data.Length:N0} B) - " +
               (g.IsUsable ? $"✔ ข้อมูลถูกต้อง {g.UsedEntries} partitions" : $"⚠ ข้อมูลเสีย: " + string.Join(", ", g.Problems)));
    });
    //[RelayCommand]
    //private Task GptDumpBackupAsync() => RunGptAsync(async ct =>
    //{
    //    ulong disk = GptRequireDisk(); if (disk == 0) return;
    //    string ? path = await PickSaveFile!("gpt_backup0.bin");        // ⚠️ ADAPT signature
    //    if (string.IsNullOrEmpty(path)) return;
    //    var data = await GptReadAsync(disk - 33, 34, ct);
    //    await File.WriteAllBytesAsync(path, data, ct);
    //    var g = GptTool.LoadBackupImage(data, "gpt_backup0.bin");
    //    RawLog($"GPT: บันทึก {Path.GetFileName(path)} (LBA 0x{disk - 33:X}–0x{disk - 1:X}, {data.Length:N0} B) — " +
    //           (g.IsUsable ? $"✔ ข้อมูลถูกต้อง {g.UsedEntries} partitions" : "⚠ ข้อมูลเสีย: " + string.Join(", ", g.Problems)));
    //});
    [RelayCommand]
    private Task GptDumpBackupAsync() => RunGptAsync(async ct =>
    {
        System.Diagnostics.Debug.WriteLine(">>> DEBUG: ปุ่ม Backup ทำงานแล้ว!");

        if (_extCsd == null || _extCsd.Length < 512)
        {
            _extCsd = await Task.Run(() => { _br.SelectPart(HwPart.User); return _br.ReadExtCsd(); }, ct);
        }

        ulong disk = GptRequireDisk();
        System.Diagnostics.Debug.WriteLine($">>> DEBUG: DiskSectorsOf = {disk}");

        if (disk == 0) return;

        // ลองถอยหลังจากขนาดดิสก์จริงเผื่อไว้สัก 34 เซกเตอร์ หรือใช้สูตรมาตรฐาน LBA ท้ายสุด
        // ปกติ LBA สุดท้ายคือ disk - 1 ดังนั้น Backup จะอยู่ระหว่าง (disk - 34) ถึง (disk - 1)
        ulong targetBackupLba = disk - 34;
        System.Diagnostics.Debug.WriteLine($">>> DEBUG: เปลี่ยนเป้าหมายอ่าน Backup ที่ LBA: {targetBackupLba} จำนวน 34 เซกเตอร์");

        var data = await GptReadAsync(targetBackupLba, 34, ct);

        string? path = await PickSaveFile!("gpt_backup0.bin");
        if (string.IsNullOrEmpty(path)) return;

        await File.WriteAllBytesAsync(path, data, ct);
        var g = GptTool.LoadBackupImage(data, "gpt_backup0.bin");

        RawLog($"GPT: บันทึก {Path.GetFileName(path)} (LBA {targetBackupLba}-{disk - 1}, {data.Length:N0} B) - " +
               (g.IsUsable ? $"✔ ข้อมูลถูกต้อง {g.UsedEntries} partitions" : $"⚠ ข้อมูลเสีย: " + string.Join(", ", g.Problems)));
    });

    [RelayCommand]
    private Task GptWriteMainFileAsync() => RunGptAsync(async ct =>
    {
        string? path = await PickOpenFile!();                   // ⚠️ ADAPT signature
        if (string.IsNullOrEmpty(path)) return;
        var src = GptTool.LoadMainImage(await File.ReadAllBytesAsync(path, ct), Path.GetFileName(path));
        await GptWriteFromSourceAsync(src, Path.GetFileName(path), writeMain: true, writeBackup: GptWriteBoth, ct);
    });

    [RelayCommand]
    private Task GptWriteBackupFileAsync() => RunGptAsync(async ct =>
    {
        string? path = await PickOpenFile!();                   // ⚠️ ADAPT signature
        if (string.IsNullOrEmpty(path)) return;
        var src = GptTool.LoadBackupImage(await File.ReadAllBytesAsync(path, ct), Path.GetFileName(path));
        await GptWriteFromSourceAsync(src, Path.GetFileName(path), writeMain: GptWriteBoth, writeBackup: true, ct);
    });

    [RelayCommand]
    private Task GptRebuildFromPrimaryAsync() => RunGptAsync(async ct =>
    {
        var r = await GptDiagnoseCoreAsync(ct); if (r == null) return;
        await GptWriteFromSourceAsync(r.Value.pri, "PRIMARY บน eMMC", true, true, ct);
    });

    [RelayCommand]
    private Task GptRebuildFromBackupAsync() => RunGptAsync(async ct =>
    {
        var r = await GptDiagnoseCoreAsync(ct); if (r == null) return;
        await GptWriteFromSourceAsync(r.Value.bak, "BACKUP บน eMMC", true, true, ct);
    });

    // ================= CORE =================
    private async Task<(GptHeaderInfo pri, GptHeaderInfo bak)?> GptDiagnoseCoreAsync(CancellationToken ct)
    {
        ulong disk = GptRequireDisk(); if (disk == 0) return null;
        RawLog($"GPT: ตรวจ GPT — eMMC USER = {disk:N0} sectors ({Fmt.Human(disk * 512)})");

        var pri = await GptTool.ReadFromDiskAsync(GptReadAsync, true, disk, ct);
        var bak = await GptTool.ReadFromDiskAsync(GptReadAsync, false, disk, ct);



        GptPrimaryStatus = GptTool.Describe(pri);
        GptBackupStatus = GptTool.Describe(bak);

        foreach (var g in new[] { pri, bak })
        {
            RawLog($"GPT: {g.Name} → " + (g.IsValid ? "✔ ปกติ" : g.IsUsable ? "⚠ CRC ถูก แต่ตำแหน่งไม่ตรง" : "✘ เสีย")
                   + $" ({g.UsedEntries} partitions)");
            foreach (var p in g.Problems) RawLog($"GPT:     - {p}");
        }

        if (pri.IsValid && bak.IsValid)
            RawLog(pri.Array!.AsSpan().SequenceEqual(bak.Array)
                ? "GPT: ✔ Primary และ Backup ปกติ และตรงกัน"
                : "GPT: ⚠ ตารางใน Primary กับ Backup ไม่เหมือนกัน (ระบบใช้ Primary) → ถ้าต้องการให้ตรงกัน กด REBUILD จาก PRIMARY");
        else if (pri.IsUsable)
            RawLog("GPT: แนะนำ → REBUILD จาก PRIMARY");
        else if (bak.IsUsable)
            RawLog("GPT: แนะนำ → REBUILD จาก BACKUP (Primary เสีย แต่ Backup ยังดี)");
        else
            RawLog("GPT: ✘ เสียทั้ง 2 ชุด → ต้องเขียนจากไฟล์ gpt_main0.bin ของรุ่นเดียวกัน หรือไฟล์ที่ dump ไว้ก่อนเสีย");

        return (pri, bak);
    }

    private async Task GptWriteFromSourceAsync(GptHeaderInfo src, string srcName,
                                               bool writeMain, bool writeBackup, CancellationToken ct)
    {
        ulong disk = GptRequireDisk(); if (disk == 0) return;
        if (!src.IsUsable)
        {
            RawLog($"GPT: {srcName} ใช้เป็นต้นแบบไม่ได้ — {string.Join(", ", src.Problems)}");
            return;
        }

        GptImages img;
        try { img = GptTool.Build(src, disk, GptExpandLast); }
        //try { img = GptTool.Build(src, disk, true); }
        catch (Exception ex) { RawLog($"GPT: สร้าง GPT ไม่ได้ — {ex.Message}"); return; }

        var es = GptTool.Entries(src).OrderBy(e => e.First).ToList();
        RawLog($"GPT: ต้นทาง {srcName} — {es.Count} partitions:");
        foreach (var e in es)
            RawLog($"GPT:   {e.Index,3}  {e.Name,-20} 0x{e.First:X} – 0x{e.Last:X}  ({Fmt.Human((e.Last - e.First + 1) * 512)})");
        foreach (var c in img.Changes) RawLog("GPT: ปรับ → " + c);

        ulong bakLba = disk - (ulong)(img.Backup.Length / 512);
        string msg = $"ต้นทาง: {srcName}\n{es.Count} partitions (สุดท้าย: {es.Last().Name})\n\n" +
                     (writeMain ? $"• Primary → LBA 0 – {img.Main.Length / 512 - 1}\n" : "") +
                     (writeBackup ? $"• Backup  → LBA 0x{bakLba:X} – 0x{disk - 1:X}\n" : "") +
                     (img.Changes.Count > 0 ? "\nการปรับ:\n- " + string.Join("\n- ", img.Changes) + "\n" : "") +
                     "\nจะสำรองข้อมูลเดิมไว้ก่อนเขียน ดำเนินการต่อหรือไม่?";
        //if (Confirm != null && !await Confirm(msg)) return;            // ⚠️ ADAPT signature
        if (Confirm != null && !await Confirm("ยืนยันการทำงาน", "คุณต้องการเขียนข้อมูลลง eMMC หรือไม่?")) return;

        await GptSafetyBackupAsync(disk, ct);

        // ส่วนเขียน GPT มีขนาดเล็ก (~17 KB) จึงไม่ยกเลิกกลางคัน เพื่อไม่ให้ header ถูกเขียนค้างครึ่งเดียว
        if (writeMain)
        {
            await GptWriteAsync(0, img.Main);
            RawLog($"GPT: ✔ เขียน Primary (LBA 0–{img.Main.Length / 512 - 1}) + readback ตรง");
        }
        if (writeBackup)
        {
            await GptWriteAsync(bakLba, img.Backup);
            RawLog($"GPT: ✔ เขียน Backup (LBA 0x{bakLba:X}–0x{disk - 1:X}) + readback ตรง");
        }

        await GptDiagnoseCoreAsync(CancellationToken.None);
        RawLog("GPT: เสร็จ → ไปแท็บ PARTITION กด READ GPT เพื่อตรวจรายการ partition");
    }

    private async Task GptSafetyBackupAsync(ulong disk, CancellationToken ct)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                  "EmmcUltra", "GptSafety");
        Directory.CreateDirectory(dir);
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var main = await GptReadAsync(0, 34, ct);
        var bak = await GptReadAsync(disk - 33, 33, ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{ts}_before_gpt_main0.bin"), main, ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{ts}_before_gpt_backup0.bin"), bak, ct);
        RawLog($"GPT: สำรองของเดิมไว้ที่ {dir}\\{ts}_before_*.bin");
    }

    // ================= I/O HELPERS =================
    private async Task<byte[]> GptReadAsync(ulong lba, uint count, CancellationToken ct)
    {
        var res = new byte[count * 512];
        for (ulong done = 0; done < count;)
        {
            ct.ThrowIfCancellationRequested();

            long remaining = (long)count - (long)done;
            uint n = (uint)(remaining < GptIoChunk ? remaining : GptIoChunk);

            ulong targetLba = lba + done;
            RawLog($"GPT I/O: กำลังอ่าน LBA {targetLba} จำนวน {n} เซกเตอร์...");

            var readTask = Task.Run(() =>
            {
                _br.SelectPart(HwPart.User);
                // เปลี่ยนพารามิเตอร์ตัวแรกกลับเป็น ulong ตามที่ระบบเดิมต้องการ
                return _br.Read(targetLba, (int)n);
            }, ct);

            if (await Task.WhenAny(readTask, Task.Delay(5000, ct)) != readTask)
            {
                throw new TimeoutException($"อ่าน LBA {targetLba} ใช้เวลานานเกินไป (ฮาร์ดแวร์อาจไม่ตอบสนอง)");
            }

            byte[] d = await readTask;

            if (d == null || d.Length < (int)(n * 512))
                throw new IOException($"อ่าน LBA 0x{targetLba:X} ได้ไม่ครบ");

            Buffer.BlockCopy(d, 0, res, (int)(done * 512), (int)(n * 512));
            done += (ulong)n;
        }

        RawLog("GPT I/O: อ่านข้อมูลสำเร็จครบถ้วน");
        return res;
    }

    private async Task GptWriteAsync(ulong lba, byte[] data)
    {
        uint total = (uint)(data.Length / 512);
        for (uint done = 0; done < total;)
        {
            uint n = Math.Min(GptIoChunk, total - done);
            await WriteSectorsAsync(HwPart.User, lba + done,
                                    data.AsMemory((int)(done * 512), (int)(n * 512)), CancellationToken.None);
            done += n;
        }
        var rb = await GptReadAsync(lba, total, CancellationToken.None);
        if (!rb.AsSpan().SequenceEqual(data))
            throw new IOException($"Readback ไม่ตรงที่ LBA 0x{lba:X} — eMMC อาจถูก Write-protect หรือหมดอายุ (ดู PRE-EOL)");
    }

    private ulong GptRequireDisk()
    {
        // 👇 เพิ่มการเช็คและบังคับอ่านอัตโนมัติตรงนี้เลยครับ
        if (_extCsd == null || _extCsd.Length < 512)
        {
            try
            {
                _br.SelectPart(HwPart.User);
                _extCsd = _br.ReadExtCsd();
            }
            catch
            {
                // ป้องกันกรณีต่อฮาร์ดแวร์หลุด
            }
        }

        ulong d = DiskSectorsOf(0);
        System.Diagnostics.Debug.WriteLine(">>> DEBUG: DiskSectorsOf = {0}", d);

        if (d < 4096)
        {
            RawLog("GPT: ไม่ทราบขนาด eMMC → กด RE-IDENTIFY หรือ READ EXT_CSD ก่อน");
            return 0;
        }
        return d;
    }

    private async Task RunGptAsync(Func<CancellationToken, Task> work)
    {
        _rawCts = new CancellationTokenSource();
        try { await work(_rawCts.Token); }
        catch (OperationCanceledException) { RawLog("GPT: ยกเลิก"); }
        catch (Exception ex) { RawLog($"GPT: ผิดพลาด — {ex.Message}"); }
        finally { _rawCts = null; }
    }

    [RelayCommand]
    private Task GptForceWriteAsync() => RunGptAsync(async ct =>
    {
        System.Diagnostics.Debug.WriteLine(">>> DEBUG: ปุ่ม Force Write GPT ทำงานแล้ว!");

        // 1. เลือกไฟล์ GPT ที่ต้องการเขียน (ใช้ไฟล์ gpt_main0.bin ที่ปกติ)
        string? path = await PickOpenFile();
        if (string.IsNullOrEmpty(path)) return;

        byte[] gptData = await File.ReadAllBytesAsync(path, ct);
        if (gptData.Length < 17408)
        {
            RawLog("GPT Force Write: ขนาดไฟล์ GPT ไม่ถูกต้อง (ต้อง 17 KB ขึ้นไป)");
            return;
        }

        // 2. ดึงขนาดดิสก์จริง
        if (_extCsd == null || _extCsd.Length < 512)
        {
            _br.SelectPart(HwPart.User);
            _extCsd = _br.ReadExtCsd();
        }
        ulong disk = DiskSectorsOf(0);
        if (disk < 4096)
        {
            RawLog("GPT Force Write: ไม่สามารถอ่านขนาด eMMC ได้");
            return;
        }

        System.Diagnostics.Debug.WriteLine($">>> DEBUG: กำลัง Force Write Main GPT ที่ LBA 0");

        // 3. เขียน Main GPT ที่ LBA 0 (เนื่องจาก Write เป็น synchronous ให้ครอบ Task.Run หรือเรียกตรงๆ ได้เลยตามรูปแบบโปรเจกต์)
        _br.SelectPart(HwPart.User);
        await Task.Run(() => _br.Write(0, gptData), ct);

        System.Diagnostics.Debug.WriteLine($">>> DEBUG: กำลัง Force Write Backup GPT ที่ LBA {disk - 34}");

        // 4. เขียน Backup GPT ที่ท้ายดิสก์ (LBA disk - 34)
        await Task.Run(() => _br.Write(disk - 34, gptData), ct);

        RawLog("GPT Force Write: บังคับเขียนตาราง GPT ทั้ง Main และ Backup สำเร็จเรียบร้อย!");
    });
}