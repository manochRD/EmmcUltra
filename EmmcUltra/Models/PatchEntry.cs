using System;

namespace EmmcUltra.Models;

public sealed class PatchEntry
{
    public int SectorSize { get; init; } = 512;
    public ulong ByteOffset { get; init; }
    public string FileName { get; init; } = "";
    public int PhysicalPartition { get; init; }
    public int SizeInBytes { get; init; }
    public string StartSectorExpr { get; init; } = "";
    public string ValueExpr { get; init; } = "";
    public string What { get; init; } = "";

    public bool IsDisk => FileName.Equals("DISK", StringComparison.OrdinalIgnoreCase);
}