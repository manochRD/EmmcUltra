using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EmmcUltra.Models;

public enum HwPart : byte { User = 0, Boot1 = 1, Boot2 = 2, Rpmb = 3 }

public partial class PartitionEntry : ObservableObject
{
    public PartitionEntry()
    {
        Status = "Not Selected";
    }

    public HwPart Part { get; init; }
    public string Name { get; init; } = "";
    public ulong StartLba { get; init; }
    public ulong EndLba { get; init; }
    public string Type { get; init; } = "";

    public ulong Sectors => EndLba - StartLba + 1;
    public string PartLabel => Part switch
    {
        HwPart.Boot1 => "BOOT1",
        HwPart.Boot2 => "BOOT2",
        HwPart.Rpmb => "RPMB",
        _ => "USER"
    };
    public string LbaHex => $"0x{StartLba:X8}";
    public string SizeText => Fmt.Size(Sectors * 512);
    public string SafeName => string.Concat(Name.Split(Path.GetInvalidFileNameChars()));

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    public string FileName => FilePath is null ? "... double-click to add file" : Path.GetFileName(FilePath);

    partial void OnIsSelectedChanged(bool value)
    {
        if (Status is "Not Selected" or "Selected")
            Status = value ? "Selected" : "Not Selected";
    }
}

public sealed record EmmcInfo(
    string Product, string Manufacturer, string Capacity, string Spec, string MfgDate,
    string BootPartition, ulong BootBytes, ulong RpmbBytes, string LifeA, string LifeB, string PreEol)
{
    private static readonly Dictionary<byte, string> Mids = new()
    {
        [0x15] = "Samsung", [0x90] = "SK hynix", [0x13] = "Micron", [0xFE] = "Micron",
        [0x45] = "SanDisk / WD", [0x11] = "Toshiba / Kioxia", [0x70] = "Kingston"
    };

    // cid = 16 ไบต์ เรียง MSB ก่อน (byte0 = MID) ตามที่ STM32 ส่งมา
    public static EmmcInfo Decode(byte[] cid, byte[] ext)
    {
        if (cid.Length < 16 || ext.Length < 512)
            throw new InvalidDataException("CID / EXT_CSD สั้นเกินไป");

        byte mid = cid[0];
        string pnm = Encoding.ASCII.GetString(cid, 3, 6).Trim('\0', ' ');
        int rev = ext[192];

        int month = cid[14] >> 4;
        int year = 1997 + (cid[14] & 0x0F);
        if (rev > 4 && year < 2010) year += 16;

        ulong sectors = BitConverter.ToUInt32(ext, 212);
        ulong boot = ext[226] * 131072UL;
        ulong rpmb = ext[168] * 131072UL;
        int bootEn = (ext[179] >> 3) & 0x7;

        string spec = rev switch { 5 => "4.41", 6 => "4.5", 7 => "5.0", 8 => "5.1", _ => $"rev {rev}" };
        string bootName = bootEn switch { 1 => "BOOT1", 2 => "BOOT2", 7 => "USER", _ => "Not enabled" };
        string mname = Mids.TryGetValue(mid, out var n) ? n : "Unknown";

        return new EmmcInfo(
            pnm, $"0x{mid:X2} ({mname})", Fmt.Size(sectors * 512), spec,
            $"{year}-{month:00}", bootName, boot, rpmb,
            Life(ext[268]), Life(ext[269]), Eol(ext[267]));
    }

    private static string Life(byte v) => v switch
    {
        0 => "N/A",
        >= 1 and <= 10 => $"{(v - 1) * 10}–{v * 10}% used",
        0x0B => "Exceeded",
        _ => $"0x{v:X2}"
    };

    private static string Eol(byte v) => v switch
    {
        1 => "Normal", 2 => "Warning (ใช้บล็อกสำรองไป 80%)", 3 => "Urgent", _ => "N/A"
    };
}

public static class GptParser
{
    private static readonly Dictionary<Guid, string> Types = new()
    {
        [new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")] = "Basic Data",
        [new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4")] = "Linux Data",
        [new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = "EFI System",
    };

    // d = ข้อมูลเริ่มที่ LBA 1 (header) ต่อด้วย partition entries
    public static List<PartitionEntry> Parse(byte[] d)
    {
        if (d.Length < 512 || Encoding.ASCII.GetString(d, 0, 8) != "EFI PART")
            throw new InvalidDataException("ไม่พบ GPT header ที่ LBA 1");

        ulong entLba = BitConverter.ToUInt64(d, 72);
        uint num = BitConverter.ToUInt32(d, 80);
        uint esz = BitConverter.ToUInt32(d, 84);
        int baseOff = (int)((entLba - 1) * 512);

        var list = new List<PartitionEntry>();
        for (int i = 0; i < num; i++)
        {
            int o = baseOff + i * (int)esz;
            if (o + 128 > d.Length) break;

            var type = new Guid(d.AsSpan(o, 16));
            if (type == Guid.Empty) continue;

            list.Add(new PartitionEntry
            {
                Part = HwPart.User,
                Name = Encoding.Unicode.GetString(d, o + 56, 72).TrimEnd('\0'),
                StartLba = BitConverter.ToUInt64(d, o + 32),
                EndLba = BitConverter.ToUInt64(d, o + 40),
                Type = Types.TryGetValue(type, out var s) ? s : type.ToString()[..8].ToUpperInvariant()
            });
        }
        return list;
    }
}

public static class Fmt
{
    public static string Human(ulong b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    public static string Size(ulong b) => $"{b:N0} Bytes ({Human(b)})";

    public static bool TryParseNum(string s, out ulong v)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, null, out v)
            : ulong.TryParse(s, out v);
    }

    public static string Hex(byte[] d, ulong baseAddr)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < d.Length; i += 16)
        {
            sb.Append($"{baseAddr + (ulong)i:X8}  ");
            for (int j = 0; j < 16; j++)
                sb.Append(i + j < d.Length ? $"{d[i + j]:X2} " : "   ");
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < d.Length; j++)
            {
                byte c = d[i + j];
                sb.Append(c is >= 32 and < 127 ? (char)c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}