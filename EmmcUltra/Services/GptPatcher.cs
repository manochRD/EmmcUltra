using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmmcUltra.Models;

namespace EmmcUltra.Services;

public sealed class GptPatcher
{
    public delegate Task<byte[]> ReadFn(int pp, ulong lba, uint count, CancellationToken ct);
    public delegate Task WriteFn(int pp, ulong lba, ReadOnlyMemory<byte> data, CancellationToken ct);

    private const int SS = 512;
    private readonly ReadFn _read;
    private readonly WriteFn _write;
    private readonly Func<int, ulong> _diskSectors;

    private readonly Dictionary<(int pp, ulong lba), byte[]> _cache = new();
    private readonly SortedSet<(int pp, ulong lba)> _dirty = new();

    public GptPatcher(ReadFn read, WriteFn write, Func<int, ulong> diskSectors)
    {
        _read = read; _write = write; _diskSectors = diskSectors;
    }

    public async Task<(int applied, int skipped)> ApplyAsync(
        IReadOnlyList<PatchEntry> patches, Action<string> log, CancellationToken ct)
    {
        int applied = 0, skipped = 0;

        foreach (var p in patches)
        {
            ct.ThrowIfCancellationRequested();
            if (!p.IsDisk) { skipped++; continue; }
            if (p.SectorSize != SS) throw new NotSupportedException($"SECTOR_SIZE {p.SectorSize} ไม่รองรับ");
            if (p.SizeInBytes is < 1 or > 8) throw new InvalidDataException($"size_in_bytes={p.SizeInBytes} ไม่ถูกต้อง");

            ulong disk = _diskSectors(p.PhysicalPartition);
            ulong lba = RawProgramParser.EvalSector(p.StartSectorExpr, disk);
            ulong val = await EvalValueAsync(p, disk, ct);

            var bytes = new byte[p.SizeInBytes];                       // little-endian
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(val >> (8 * i));

            await PokeAsync(p.PhysicalPartition, lba, p.ByteOffset, bytes, ct);
            applied++;
            log($"PATCH: [pp{p.PhysicalPartition}] LBA 0x{lba:X} +{p.ByteOffset} ({p.SizeInBytes}B) = 0x{val:X}  — {p.What}");
        }

        ct.ThrowIfCancellationRequested();
        await FlushAsync(ct);                                          // เขียนลง eMMC ครั้งเดียวตอนจบ
        return (applied, skipped);
    }

    // value = "123" | "0x..." | "NUM_DISK_SECTORS-34." | "CRC32(start,len)"
    private async Task<ulong> EvalValueAsync(PatchEntry p, ulong disk, CancellationToken ct)
    {
        string v = p.ValueExpr.Replace(" ", "");
        if (v.StartsWith("CRC32(", StringComparison.OrdinalIgnoreCase) && v.EndsWith(')'))
        {
            string inner = v[6..^1];
            int comma = inner.LastIndexOf(',');
            if (comma < 0) throw new InvalidDataException($"CRC32 รูปแบบผิด: {p.ValueExpr}");

            ulong start = RawProgramParser.EvalSector(inner[..comma], disk);
            int len = (int)RawProgramParser.EvalSector(inner[(comma + 1)..], disk);
            var data = await PeekAsync(p.PhysicalPartition, start, len, ct);
            return Crc32.Compute(data);
        }
        return RawProgramParser.EvalSector(v, disk);
    }

    // โหลดเซกเตอร์ที่ยังไม่อยู่ใน cache (อ่านต่อเนื่องทีละสูงสุด 64 เซกเตอร์)
    private async Task EnsureAsync(int pp, ulong first, ulong count, CancellationToken ct)
    {
        ulong i = 0;
        while (i < count)
        {
            if (_cache.ContainsKey((pp, first + i))) { i++; continue; }

            ulong run = 0;
            while (i + run < count && run < 64 && !_cache.ContainsKey((pp, first + i + run))) run++;

            var data = await _read(pp, first + i, (uint)run, ct);
            if (data.Length < (int)run * SS)
                throw new IOException($"อ่าน LBA 0x{first + i:X} ได้ไม่ครบ");

            for (ulong k = 0; k < run; k++)
                _cache[(pp, first + i + k)] = data.AsSpan((int)k * SS, SS).ToArray();
            i += run;
        }
    }

    private async Task<byte[]> PeekAsync(int pp, ulong lba, int len, CancellationToken ct)
    {
        await EnsureAsync(pp, lba, (ulong)((len + SS - 1) / SS), ct);
        var r = new byte[len];
        for (int off = 0; off < len; off += SS)
            Buffer.BlockCopy(_cache[(pp, lba + (ulong)(off / SS))], 0, r, off, Math.Min(SS, len - off));
        return r;
    }

    private async Task PokeAsync(int pp, ulong lba, ulong byteOffset, byte[] data, CancellationToken ct)
    {
        lba += byteOffset / SS;
        int off = (int)(byteOffset % SS);
        await EnsureAsync(pp, lba, (ulong)((off + data.Length + SS - 1) / SS), ct);

        for (int i = 0; i < data.Length; i++)
        {
            int abs = off + i;
            var key = (pp, lba + (ulong)(abs / SS));
            _cache[key][abs % SS] = data[i];
            _dirty.Add(key);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var keys = _dirty.ToList();
        int i = 0;
        while (i < keys.Count)
        {
            int j = i + 1;
            while (j < keys.Count && keys[j].pp == keys[i].pp && keys[j].lba == keys[j - 1].lba + 1) j++;

            var buf = new byte[(j - i) * SS];
            for (int k = i; k < j; k++) Buffer.BlockCopy(_cache[keys[k]], 0, buf, (k - i) * SS, SS);
            await _write(keys[i].pp, keys[i].lba, buf, ct);
            i = j;
        }
        _dirty.Clear();
    }
}