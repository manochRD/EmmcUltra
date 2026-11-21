using System.Text;

namespace EmmcUltra.Services;

public static class HexDump
{
    /// OFFSET  00 01 .. 07  08 .. 0F   |ASCII...........|
    public static string Format(byte[] data, ulong baseAddr, bool sectorMarks = true)
    {
        if (data.Length == 0) return "(ไม่มีข้อมูล)";
        var sb = new StringBuilder(data.Length * 5 + 256);
        bool wide = baseAddr + (ulong)data.Length > 0xFFFFFFFFUL;
        int w = wide ? 12 : 8;

        sb.Append("OFFSET".PadRight(w)).Append("  ");
        for (int i = 0; i < 16; i++) { sb.Append(i.ToString("X2")).Append(' '); if (i == 7) sb.Append(' '); }
        sb.Append(" ASCII\n");

        for (int off = 0; off < data.Length; off += 16)
        {
            ulong addr = baseAddr + (ulong)off;
            if (sectorMarks && addr % 512 == 0)
                sb.Append($"---- LBA {addr / 512} (0x{addr / 512:X}) ----\n");

            sb.Append(addr.ToString(wide ? "X12" : "X8")).Append("  ");
            int len = Math.Min(16, data.Length - off);
            for (int i = 0; i < 16; i++)
            {
                sb.Append(i < len ? data[off + i].ToString("X2") + " " : "   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < len; i++)
            {
                byte b = data[off + i];
                sb.Append((b >= 0x20 && b < 0x7F) ? (char)b : '.');
            }
            sb.Append("|\n");
        }
        return sb.ToString();
    }
}