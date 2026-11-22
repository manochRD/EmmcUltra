using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmmcUltra.Services;

public static class GptVerifier
{
    public static async Task<List<string>> CheckAsync(
        GptPatcher.ReadFn read, int pp, ulong disk, CancellationToken ct)
    {
        var errs = new List<string>();
        if (disk == 0) { errs.Add("ไม่ทราบ NUM_DISK_SECTORS — ต้อง Identify ก่อน"); return errs; }

        await CheckHeader("PRIMARY", 1, disk - 1);
        await CheckHeader("BACKUP", disk - 1, 1);
        return errs;

        async Task CheckHeader(string name, ulong lba, ulong expectAlt)
        {
            var h = await read(pp, lba, 1, ct);
            if (Encoding.ASCII.GetString(h, 0, 8) != "EFI PART")
            { errs.Add($"{name}: ไม่พบ signature 'EFI PART' ที่ LBA 0x{lba:X}"); return; }

            uint hsz = BitConverter.ToUInt32(h, 12);
            if (hsz < 92 || hsz > 512) { errs.Add($"{name}: HeaderSize={hsz} ผิดปกติ"); return; }

            var tmp = h.AsSpan(0, (int)hsz).ToArray();
            uint hcrc = BitConverter.ToUInt32(h, 16);
            tmp[16] = tmp[17] = tmp[18] = tmp[19] = 0;
            if (Crc32.Compute(tmp) != hcrc) errs.Add($"{name}: Header CRC ไม่ถูกต้อง");

            ulong my = BitConverter.ToUInt64(h, 24);
            ulong alt = BitConverter.ToUInt64(h, 32);
            ulong firstUse = BitConverter.ToUInt64(h, 40);
            ulong lastUse = BitConverter.ToUInt64(h, 48);
            ulong arrLba = BitConverter.ToUInt64(h, 72);
            uint num = BitConverter.ToUInt32(h, 80);
            uint esz = BitConverter.ToUInt32(h, 84);
            uint acrc = BitConverter.ToUInt32(h, 88);

            if (my != lba) errs.Add($"{name}: MyLBA=0x{my:X} ควรเป็น 0x{lba:X}");
            if (alt != expectAlt) errs.Add($"{name}: AlternateLBA=0x{alt:X} ควรเป็น 0x{expectAlt:X}");
            if (lastUse >= disk) errs.Add($"{name}: LastUsableLBA=0x{lastUse:X} เกินขนาดดิสก์");

            int arrBytes = (int)(num * esz);
            if (arrBytes <= 0 || arrBytes > 1 << 20) { errs.Add($"{name}: ขนาด partition array ผิดปกติ"); return; }

            var arr = await read(pp, arrLba, (uint)((arrBytes + 511) / 512), ct);
            if (Crc32.Compute(arr.AsSpan(0, arrBytes)) != acrc) errs.Add($"{name}: Partition Array CRC ไม่ถูกต้อง");

            for (int i = 0; i < num; i++)
            {
                int o = i * (int)esz;
                if (arr.AsSpan(o, 16).ToArray().All(b => b == 0)) continue;   // entry ว่าง
                ulong first = BitConverter.ToUInt64(arr, o + 32);
                ulong last = BitConverter.ToUInt64(arr, o + 40);
                string pn = Encoding.Unicode.GetString(arr, o + 56, 72).TrimEnd('\0');
                if (first > last || first < firstUse || last > lastUse)
                    errs.Add($"{name}: #{i + 1} '{pn}' LBA 0x{first:X}-0x{last:X} อยู่นอกช่วงที่ใช้ได้");
            }
        }
    }
}