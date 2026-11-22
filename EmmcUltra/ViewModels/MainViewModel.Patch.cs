using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmcUltra.Models;
using EmmcUltra.Services;

namespace EmmcUltra.ViewModels;   // ⚠️ ใช้ namespace เดียวกับ MainViewModel

public partial class MainViewModel
{
    private List<PatchEntry> _patches = new();
    [ObservableProperty] public partial string? PatchPath { get; set; }
    [ObservableProperty] public partial string? PatchSummary { get; set; }

    private bool _rawAutoPatch = true;
    public bool RawAutoPatch { get => _rawAutoPatch; set => SetProperty(ref _rawAutoPatch, value); }

    // หา patch ที่คู่กับ rawprogram อัตโนมัติ: rawprogram0.xml -> patch0.xml
    private void TryAutoLoadPatch(string rawXmlPath)
    {
        string dir = Path.GetDirectoryName(rawXmlPath)!;
        string name = Path.GetFileName(rawXmlPath);
        string cand = Path.Combine(dir, Regex.Replace(name, "rawprogram", "patch", RegexOptions.IgnoreCase));

        if (!cand.Equals(rawXmlPath, StringComparison.OrdinalIgnoreCase) && File.Exists(cand)) LoadPatchFile(cand);
        else if (File.Exists(Path.Combine(dir, "patch0.xml"))) LoadPatchFile(Path.Combine(dir, "patch0.xml"));
        else { _patches = new(); PatchPath = null; PatchSummary = "ไม่พบ patch0.xml"; }
    }

    private void LoadPatchFile(string path)
    {
        try
        {
            _patches = PatchParser.Parse(path);
            PatchPath = path;
            int disk = _patches.Count(p => p.IsDisk);
            PatchSummary = $"{Path.GetFileName(path)}: {disk} DISK patch (ข้าม file patch {_patches.Count - disk})";
            RawLog($"PATCH: โหลด {PatchSummary}");
        }
        catch (Exception ex) { RawLog($"PATCH: โหลดไม่สำเร็จ — {ex.Message}"); }
    }

    [RelayCommand]
    private async Task LoadPatchAsync()
    {
        string? path = await PickOpenFile!();      // ⚠️ ADAPT ตาม signature ของคุณ
        if (!string.IsNullOrEmpty(path)) LoadPatchFile(path);
    }

    [RelayCommand]
    private async Task ApplyPatchAsync()
    {
        if (_patches.Count == 0) { RawLog("PATCH: ยังไม่ได้โหลด patch0.xml"); return; }
        //if (Confirm != null && !await Confirm(
        //        "จะแก้ GPT บน eMMC ตาม patch0.xml\n" +
        //        "ควรทำหลังเขียน gpt_main0.bin และ gpt_backup0.bin แล้วเท่านั้น\nดำเนินการต่อ?"))
        //    return;
        if (Confirm != null && !await Confirm("ยืนยันการทำงาน", "คุณต้องการเขียนข้อมูลลง eMMC หรือไม่?")) return;


        _rawCts = new CancellationTokenSource();
        try { await ApplyPatchCoreAsync(_rawCts.Token); }
        finally { _rawCts = null; }
    }

    private async Task<bool> ApplyPatchCoreAsync(CancellationToken ct)
    {
        try
        {
            RawLog("PATCH: เริ่ม apply patch...");
            var patcher = new GptPatcher(ReadPp, WritePp, DiskSectorsOf);
            var (applied, skipped) = await patcher.ApplyAsync(_patches, RawLog, ct);
            RawLog($"PATCH: เขียนแล้ว {applied} รายการ (ข้าม file patch {skipped})");
            return await VerifyGptCoreAsync(ct);
        }
        catch (OperationCanceledException)
        {
            RawLog("PATCH: ยกเลิกก่อนเขียน — GPT บน eMMC ยังไม่ถูกแก้");
            return false;
        }
        catch (Exception ex)
        {
            RawLog($"PATCH: ล้มเหลว — {ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private async Task VerifyGptAsync() => await VerifyGptCoreAsync(CancellationToken.None);

    private async Task<bool> VerifyGptCoreAsync(CancellationToken ct)
    {
        var pps = _patches.Where(p => p.IsDisk).Select(p => p.PhysicalPartition).Distinct().DefaultIfEmpty(0);
        bool allOk = true;

        foreach (int pp in pps)
        {
            var errs = await GptVerifier.CheckAsync(ReadPp, pp, DiskSectorsOf(pp), ct);
            if (errs.Count == 0)
                RawLog($"GPT VERIFY: ✔ pp{pp} Primary + Backup ถูกต้อง (CRC, AlternateLBA, ช่วง LBA)");
            else
            {
                allOk = false;
                foreach (var e in errs) RawLog($"GPT VERIFY: ✘ pp{pp} {e}");
            }
        }
        return allOk;
    }

    private Task<byte[]> ReadPp(int pp, ulong lba, uint n, CancellationToken ct)
        => ReadSectorsAsync(PartOf(pp), lba, n, ct);

    private Task WritePp(int pp, ulong lba, ReadOnlyMemory<byte> d, CancellationToken ct)
        => WriteSectorsAsync(PartOf(pp), lba, d, ct);

    private static HwPart PartOf(int pp) => pp switch
    {
        1 => HwPart.Boot1,
        2 => HwPart.Boot2,
        _ => HwPart.User
    };
}