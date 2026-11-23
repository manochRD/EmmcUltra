using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmmcUltra.Services;

public sealed class GptHeaderInfo
{
    public string Name { get; init; } = "";
    public bool SignatureOk { get; set; }
    public bool LayoutOk { get; set; }
    public bool HeaderCrcOk { get; set; }
    public bool ArrayCrcOk { get; set; }
    public uint HeaderSize { get; set; }
    public ulong MyLba { get; set; }
    public ulong AltLba { get; set; }
    public ulong FirstUsable { get; set; }
    public ulong LastUsable { get; set; }
    public Guid DiskGuid { get; set; }
    public ulong ArrayLba { get; set; }
    public uint NumEntries { get; set; }
    public uint EntrySize { get; set; }
    public uint ArrayCrc { get; set; }
    public int UsedEntries { get; set; }
    public byte[]? Header { get; set; }
    public byte[]? Array { get; set; }
    public List<string> Problems { get; } = new();

    public int ArrayBytes => (int)(NumEntries * EntrySize);
    public int ArraySectors => (ArrayBytes + 511) / 512;

    public bool IsUsable => SignatureOk && LayoutOk && HeaderCrcOk && ArrayCrcOk && Array != null;
    public bool IsValid => IsUsable && Problems.Count == 0;
}

public sealed record GptEntry(int Index, string Name, ulong First, ulong Last);
public sealed record GptImages(byte[] Main, byte[] Backup, List<string> Changes);

public static class GptTool
{
    public const int SS = 512;

    public static GptHeaderInfo ParseHeader(string name, byte[] sec)
    {
        var g = new GptHeaderInfo { Name = name };
        if (sec.Length < SS || Encoding.ASCII.GetString(sec, 0, 8) != "EFI PART")
        { g.Problems.Add("ไม่พบ signature 'EFI PART'"); return g; }

        g.SignatureOk = true;
        g.Header = sec.AsSpan(0, SS).ToArray();
        g.HeaderSize = R32(sec, 12);
        g.MyLba = R64(sec, 24);
        g.AltLba = R64(sec, 32);
        g.FirstUsable = R64(sec, 40);
        g.LastUsable = R64(sec, 48);
        g.DiskGuid = new Guid(sec.AsSpan(56, 16));
        g.ArrayLba = R64(sec, 72);
        g.NumEntries = R32(sec, 80);
        g.EntrySize = R32(sec, 84);
        g.ArrayCrc = R32(sec, 88);

        if (g.HeaderSize < 92 || g.HeaderSize > SS)
        { g.Problems.Add($"HeaderSize={g.HeaderSize} ผิดปกติ"); return g; }

        var tmp = sec.AsSpan(0, (int)g.HeaderSize).ToArray();
        tmp.AsSpan(16, 4).Clear();
        g.HeaderCrcOk = Crc32.Compute(tmp) == R32(sec, 16);
        if (!g.HeaderCrcOk) g.Problems.Add("Header CRC ไม่ถูกต้อง");

        if (g.EntrySize < 128 || g.EntrySize % 8 != 0 || g.NumEntries == 0 || g.NumEntries > 1024
            || (long)g.NumEntries * g.EntrySize > 128 * 1024)
        { g.Problems.Add($"ขนาด partition array ผิดปกติ ({g.NumEntries} × {g.EntrySize})"); return g; }

        g.LayoutOk = true;
        return g;
    }

    public static void AttachArray(GptHeaderInfo g, ReadOnlySpan<byte> data)
    {
        g.Array = data[..g.ArrayBytes].ToArray();
        g.ArrayCrcOk = Crc32.Compute(g.Array) == g.ArrayCrc;
        if (!g.ArrayCrcOk) g.Problems.Add("Partition Array CRC ไม่ถูกต้อง");
        g.UsedEntries = Entries(g).Count;
    }

    public static List<GptEntry> Entries(GptHeaderInfo g)
    {
        var list = new List<GptEntry>();
        if (g.Array == null) return list;
        for (int i = 0; i < g.NumEntries; i++)
        {
            int o = i * (int)g.EntrySize;
            if (g.Array.AsSpan(o, 16).IndexOfAnyExcept((byte)0) < 0) continue;
            string n = Encoding.Unicode.GetString(g.Array, o + 56, 72).TrimEnd('\0');
            list.Add(new GptEntry(i + 1, n, R64(g.Array, o + 32), R64(g.Array, o + 40)));
        }
        return list;
    }

    public static void CheckOnDisk(GptHeaderInfo g, bool isPrimary, ulong disk)
    {
        ulong expMy = isPrimary ? 1 : disk - 1, expAlt = isPrimary ? disk - 1 : 1;
        if (g.MyLba != expMy) g.Problems.Add($"MyLBA=0x{g.MyLba:X} ควรเป็น 0x{expMy:X}");
        if (g.AltLba != expAlt) g.Problems.Add($"AlternateLBA=0x{g.AltLba:X} ควรเป็น 0x{expAlt:X}");
        if (g.LastUsable >= disk) g.Problems.Add($"LastUsableLBA=0x{g.LastUsable:X} เกินขนาด eMMC");
        g.Problems.AddRange(CheckEntries(Entries(g), g.FirstUsable, g.LastUsable));
    }

    //private static IEnumerable<string> CheckEntries(List<GptEntry> es, ulong firstUse, ulong lastUse)
    //{
    //    GptEntry? prev = null;
    //    foreach (var e in es.OrderBy(e => e.First))
    //    {
    //        if (e.First > e.Last)
    //            yield return $"#{e.Index} '{e.Name}' First > Last";
    //        else if (e.First < firstUse || e.Last > lastUse)
    //            yield return $"#{e.Index} '{e.Name}' 0x{e.First:X}-0x{e.Last:X} อยู่นอกช่วงที่ใช้ได้";
    //        if (prev != null && e.First <= prev.Last)
    //            yield return $"#{e.Index} '{e.Name}' ทับกับ #{prev.Index} '{prev.Name}'";
    //        prev = e;
    //    }
    //}

    private static IEnumerable<string> CheckEntries(List<GptEntry> es, ulong firstUse, ulong lastUse)
    {
        GptEntry? prev = null;
        var maxEntry = es.Count > 0 ? es.MaxBy(e => e.First) : null;

        foreach (var e in es.OrderBy(e => e.First))
        {
            bool isLastEntry = (maxEntry != null && e.Index == maxEntry.Index);

            if (e.First > e.Last && !isLastEntry)
            {
                yield return $"#{e.Index} '{e.Name}' First > Last";
            }
            else if (!isLastEntry && (e.First < firstUse || e.Last > lastUse))
            {
                yield return $"#{e.Index} '{e.Name}' 0x{e.First:X}-0x{e.Last:X} อยู่นอกช่วงที่ใช้ได้";
            }
            else if (isLastEntry && e.First > lastUse)
            {
                yield return $"#{e.Index} '{e.Name}' เริ่มต้นอยู่นอกช่วงดิสก์";
            }

            if (prev != null && e.First <= prev.Last)
            {
                yield return $"#{e.Index} '{e.Name}' ทับกับ #{prev.Index} '{prev.Name}'";
            }

            prev = e;
        }
    }


    public static async Task<GptHeaderInfo> ReadFromDiskAsync(
        Func<ulong, uint, CancellationToken, Task<byte[]>> read, bool isPrimary, ulong disk, CancellationToken ct)
    {
        ulong hdrLba = isPrimary ? 1 : disk - 1;
        var g = ParseHeader(isPrimary ? "PRIMARY" : "BACKUP", await read(hdrLba, 1, ct));
        if (!g.LayoutOk) return g;

        if (g.ArrayLba < 2 || g.ArrayLba + (ulong)g.ArraySectors > disk)
        { g.Problems.Add($"PartitionEntryLBA=0x{g.ArrayLba:X} อยู่นอก eMMC"); return g; }

        AttachArray(g, await read(g.ArrayLba, (uint)g.ArraySectors, ct));
        CheckOnDisk(g, isPrimary, disk);
        return g;
    }

    public static GptHeaderInfo LoadMainImage(byte[] f, string name)
    {
        if (f.Length < 3 * SS || f.Length % SS != 0) return Bad(name, $"ขนาดไฟล์ {f.Length} B ไม่ถูกต้อง");
        var g = ParseHeader(name, f.AsSpan(SS, SS).ToArray());
        if (!g.LayoutOk) return g;

        long off = (long)g.ArrayLba * SS;
        if (off < 2 * SS || off + g.ArrayBytes > f.Length)
        { g.Problems.Add("ไฟล์มี partition array ไม่ครบ"); return g; }

        AttachArray(g, f.AsSpan((int)off, g.ArrayBytes));
        return g;
    }

    public static GptHeaderInfo LoadBackupImage(byte[] f, string name)
    {
        if (f.Length < 2 * SS || f.Length % SS != 0) return Bad(name, $"ขนาดไฟล์ {f.Length} B ไม่ถูกต้อง");
        int secs = f.Length / SS;
        var g = ParseHeader(name, f.AsSpan((secs - 1) * SS, SS).ToArray());
        if (!g.LayoutOk) return g;

        long fileStart = (long)g.MyLba - (secs - 1);
        long off = ((long)g.ArrayLba - fileStart) * SS;
        if (off < 0 || off + g.ArrayBytes > (long)(secs - 1) * SS)
        { g.Problems.Add("ไฟล์มี partition array ไม่ครบ / ตำแหน่งไม่สัมพันธ์กับ header"); return g; }

        AttachArray(g, f.AsSpan((int)off, g.ArrayBytes));
        return g;
    }

    public static GptImages Build(GptHeaderInfo src, ulong disk, bool expandLast)
    {
        if (!src.IsUsable) throw new InvalidOperationException("GPT ต้นทางเสีย ใช้เป็นต้นแบบไม่ได้");

        int arrSec = src.ArraySectors;
        ulong newLast = disk - 2 - (ulong)arrSec;
        var changes = new List<string>();

        if (src.FirstUsable < 2 + (ulong)arrSec)
            throw new InvalidDataException($"FirstUsableLBA=0x{src.FirstUsable:X} ทับกับ partition array");

        var es = Entries(src);
        if (es.Count == 0) throw new InvalidDataException("ตาราง partition ว่าง");
        var bad = CheckEntries(es, src.FirstUsable, ulong.MaxValue).ToList();
        if (bad.Count > 0) throw new InvalidDataException("ตาราง partition ผิด: " + string.Join(", ", bad));

        var arr = new byte[arrSec * SS];
        src.Array!.CopyTo(arr, 0);

        // 1. ค้นหาพาร์ทิชัน 'userdata' โดยเฉพาะ เพื่อนำมาขยาย (ถ้าไม่พบ ค่อย fallback ไปตัวท้ายสุด)
        var targetE = es.FirstOrDefault(e => e.Name.Equals("userdata", StringComparison.OrdinalIgnoreCase))
                      ?? es.MaxBy(e => e.Last)!;

        if (targetE.Last > newLast)
        {
            throw new InvalidOperationException(
                $"'{targetE.Name}' จบที่ 0x{targetE.Last:X} แต่ eMMC นี้ใช้ได้ถึง 0x{newLast:X} – eMMC เล็กกว่าต้นทาง");
        }

        // 2. ถ้าเปิดใช้งาน expandLast ให้ทำการขยายพาร์ทิชันเป้าหมายให้ชนขอบ eMMC ใหม่
        if (expandLast && targetE.Last < newLast)
        {
            // คำนวณหาจุดเริ่มต้น (Start LBA) ของพาร์ทิชันนี้ให้อยู่ต่อจากพาร์ทิชันก่อนหน้า
            var prevE = es.Where(e => e.Index != targetE.Index).MaxBy(e => e.Last);
            ulong newStart = prevE != null ? prevE.Last + 1 : targetE.First;
            if (newStart >= newLast) newStart = targetE.First;

            int entryOffset = (targetE.Index - 1) * (int)src.EntrySize;

            // บันทึก Start LBA และ Last LBA ใหม่ลงในตาราง Entry
            W64(arr, entryOffset + 32, newStart);
            W64(arr, entryOffset + 40, newLast);

            string msg = $"DEBUG: ขยาย '{targetE.Name}' สำเร็จ Start:0x{newStart:X} -> End:0x{newLast:X}";
            changes.Add(msg);
            System.Diagnostics.Debug.WriteLine(msg);
        }

        ulong srcDisk = Math.Max(src.MyLba, src.AltLba) + 1;
        if (srcDisk != disk)
        {
            changes.Add($"ขนาดต้นทาง ≈ {srcDisk:N0} sectors ≠ eMMC นี้ {disk:N0} sectors → ปรับตำแหน่ง Backup ใหม่");
        }
        if (src.LastUsable != newLast)
        {
            changes.Add($"LastUsableLBA 0x{src.LastUsable:X} → 0x{newLast:X}");
        }

        uint crc = Crc32.Compute(arr.AsSpan(0, src.ArrayBytes));
        ulong bakArr = disk - 1 - (ulong)arrSec;

        var main = new byte[(2 + arrSec) * SS];
        ProtectiveMbr(disk).CopyTo(main, 0);
        MakeHeader(src, 1, disk - 1, newLast, 2, crc).CopyTo(main, SS);
        arr.CopyTo(main, 2 * SS);

        var bak = new byte[(arrSec + 1) * SS];
        arr.CopyTo(bak, 0);
        MakeHeader(src, disk - 1, 1, newLast, bakArr, crc).CopyTo(bak, arrSec * SS);

        return new GptImages(main, bak, changes);
    }

    private static byte[] MakeHeader(GptHeaderInfo s, ulong my, ulong alt, ulong lastUse, ulong arrLba, uint arrCrc)
    {
        var h = (byte[])s.Header!.Clone();
        W64(h, 24, my); W64(h, 32, alt); W64(h, 48, lastUse); W64(h, 72, arrLba); W32(h, 88, arrCrc);
        W32(h, 16, 0);
        W32(h, 16, Crc32.Compute(h.AsSpan(0, (int)s.HeaderSize)));
        return h;
    }

    public static byte[] ProtectiveMbr(ulong disk)
    {
        var m = new byte[SS];
        const int p = 446;
        m[p + 2] = 0x02;
        m[p + 4] = 0xEE;
        m[p + 5] = m[p + 6] = m[p + 7] = 0xFF;
        W32(m, p + 8, 1);
        W32(m, p + 12, (uint)Math.Min(disk - 1, 0xFFFFFFFFUL));
        m[510] = 0x55; m[511] = 0xAA;
        return m;
    }

    public static string Describe(GptHeaderInfo g)
    {
        if (!g.SignatureOk) return "✘ ไม่พบ GPT header\n" + string.Join("\n", g.Problems);
        var sb = new StringBuilder();
        sb.AppendLine(g.IsValid ? "✔ VALID" : g.IsUsable ? "⚠ CRC ถูก แต่ตำแหน่ง/ช่วงไม่ตรง" : "✘ เสีย");
        sb.AppendLine($"Header CRC: {(g.HeaderCrcOk ? "OK" : "BAD")}   Array CRC: {(g.ArrayCrcOk ? "OK" : "BAD")}");
        sb.AppendLine($"MyLBA 0x{g.MyLba:X}   Alt 0x{g.AltLba:X}   Array @0x{g.ArrayLba:X}");
        sb.AppendLine($"Usable 0x{g.FirstUsable:X} – 0x{g.LastUsable:X}");
        sb.AppendLine($"Partitions: {g.UsedEntries} / {g.NumEntries}");
        foreach (var p in g.Problems.Take(6)) sb.AppendLine("• " + p);
        return sb.ToString().TrimEnd();
    }

    private static GptHeaderInfo Bad(string name, string msg)
    { var g = new GptHeaderInfo { Name = name }; g.Problems.Add(msg); return g; }

    private static uint R32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong R64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
}