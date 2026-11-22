using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using EmmcUltra.Models;

namespace EmmcUltra.Services;

public static class RawProgramParser
{
    /// diskSectorsOf(pp) = จำนวนเซกเตอร์ทั้งหมดของ physical partition นั้น (ใช้กับ NUM_DISK_SECTORS)
    public static List<RawProgramEntry> Parse(string xmlPath, Func<int, ulong> diskSectorsOf)
    {
        string dir = Path.GetDirectoryName(xmlPath)!;
        var doc = XDocument.Load(xmlPath);
        var list = new List<RawProgramEntry>();

        foreach (var x in doc.Descendants("program"))
        {
            string A(string n) => (string?)x.Attribute(n) ?? "";

            var e = new RawProgramEntry
            {
                Label = A("label"),
                FileName = A("filename").Trim(),
                PhysicalPartition = ParseInt(A("physical_partition_number")),
                StartSectorExpr = A("start_sector"),
                NumPartitionSectors = ParseU64(A("num_partition_sectors")),
                FileSectorOffset = ParseU64(A("file_sector_offset")),
                Sparse = A("sparse").Equals("true", StringComparison.OrdinalIgnoreCase),
                SectorSize = ParseInt(A("SECTOR_SIZE_IN_BYTES"), 512),
            };

            e.StartSector = EvalSector(e.StartSectorExpr, diskSectorsOf(e.PhysicalPartition));

            if (e.HasFile)
            {
                string p = Path.Combine(dir, e.FileName);
                if (File.Exists(p)) { e.FullPath = p; e.FileSize = new FileInfo(p).Length; }
            }

            e.IsSelected = e.FileFound && !e.Sparse;
            e.Status = !e.HasFile ? "ไม่มีไฟล์ (ข้าม)"
                     : !e.FileFound ? "ไม่พบไฟล์"
                     : e.Sparse ? "SPARSE (ยังไม่รองรับ)"
                     : "พร้อม";
            list.Add(e);
        }
        return list;
    }

    // รองรับ "40", "0x5000", "NUM_DISK_SECTORS-33."
    public static ulong EvalSector(string expr, ulong diskSectors)
    {
        string s = expr.Trim().TrimEnd('.').Replace(" ", "");
        const string K = "NUM_DISK_SECTORS";
        if (s.StartsWith(K, StringComparison.OrdinalIgnoreCase))
        {
            if (diskSectors == 0)
                throw new InvalidOperationException("ต้อง Identify eMMC ก่อน (ต้องรู้ NUM_DISK_SECTORS)");
            string rest = s[K.Length..];
            if (rest.Length == 0) return diskSectors;
            ulong n = ParseU64(rest[1..]);
            return rest[0] == '-' ? diskSectors - n : diskSectors + n;
        }
        return ParseU64(s);
    }

    static ulong ParseU64(string s)
    {
        s = s.Trim().TrimEnd('.');
        if (s.Length == 0) return 0;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(s[2..], NumberStyles.HexNumber)
            : ulong.Parse(s, CultureInfo.InvariantCulture);
    }

    static int ParseInt(string s, int def = 0) => int.TryParse(s, out var v) ? v : def;
}