using CommunityToolkit.Mvvm.ComponentModel;

namespace EmmcUltra.Models;

public partial class RawProgramEntry : ObservableObject
{
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    public string Label { get; init; } = "";
    public string FileName { get; init; } = "";
    public int PhysicalPartition { get; init; }
    public string StartSectorExpr { get; init; } = "";
    public ulong StartSector { get; set; }
    public ulong NumPartitionSectors { get; init; }
    public ulong FileSectorOffset { get; init; }
    public bool Sparse { get; init; }
    public int SectorSize { get; init; } = 512;
    public string? FullPath { get; set; }
    public long FileSize { get; set; }

    public bool HasFile => FileName.Length > 0;
    public bool FileFound => FullPath != null;

    // ⚠️ ปรับชื่อ enum ให้ตรงกับ HwPart ของคุณ
    public HwPart Part => PhysicalPartition switch
    {
        1 => HwPart.Boot1,
        2 => HwPart.Boot2,
        _ => HwPart.User
    };

    public string PartLabel => Part.ToString().ToUpper();
    public string StartHex => $"0x{StartSector:X}";
    public string SizeText => Fmt.Human(NumPartitionSectors * (ulong)SectorSize);
    public string FileText => !HasFile ? "—"
        : FileFound ? $"{FileName}  ({Fmt.Human((ulong)FileSize)})"
        : $"{FileName}  (ไม่พบไฟล์)";
}