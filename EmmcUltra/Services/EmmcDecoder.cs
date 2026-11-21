using System.Text;
using EmmcUltra.Models;

namespace EmmcUltra.Services;

/// ถอดรหัส CID + EXT_CSD ตาม JEDEC JESD84-B51 (eMMC 5.1)
/// สมมติว่า cid[0] = บิต [127:120] (MID) และ ext[i] = EXT_CSD byte i
public static class EmmcDecoder
{
    public static List<InfoRow> Decode(byte[] cid, byte[] ext)
    {
        var r = new List<InfoRow>();
        int rev = ext.Length >= 512 ? ext[192] : 0;

        if (cid.Length >= 16) DecodeCid(r, cid, rev);
        else r.Add(new InfoRow("CID", "CID", "-", "-", $"ข้อมูลไม่ครบ ({cid.Length}/16 ไบต์)"));

        if (ext.Length >= 512) DecodeExtCsd(r, ext);
        else r.Add(new InfoRow("EXT_CSD", "EXT_CSD", "-", "-", $"ข้อมูลไม่ครบ ({ext.Length}/512 ไบต์)"));
        return r;
    }

    // ============================ CID ============================
    private static void DecodeCid(List<InfoRow> r, byte[] c, int rev)
    {
        const string G = "CID";
        int cbx = c[1] & 0x03;
        int month = c[14] >> 4, y = c[14] & 0x0F;
        int year = (rev > 4 && y <= 12) ? 2013 + y : 1997 + y;
        uint psn = (uint)((c[10] << 24) | (c[11] << 16) | (c[12] << 8) | c[13]);

        r.Add(new InfoRow(G, "MID  Manufacturer ID", "[127:120]", H8(c[0]), MidName(c[0])));
        r.Add(new InfoRow(G, "CBX  Device type", "[113:112]", H8(cbx),
            cbx switch { 0 => "Removable", 1 => "BGA (Discrete embedded)", 2 => "POP", _ => "Reserved" }));
        r.Add(new InfoRow(G, "OID  OEM/Application ID", "[111:104]", H8(c[2]), Printable(c[2])));
        r.Add(new InfoRow(G, "PNM  Product name", "[103:56]", HexBytes(c, 3, 6), Ascii(c, 3, 6)));
        r.Add(new InfoRow(G, "PRV  Product revision", "[55:48]", H8(c[9]), $"{c[9] >> 4}.{c[9] & 0x0F}"));
        r.Add(new InfoRow(G, "PSN  Serial number", "[47:16]", $"0x{psn:X8}", psn.ToString()));
        r.Add(new InfoRow(G, "MDT  Manufacturing date", "[15:8]", H8(c[14]), $"{year}-{month:00}"));
        r.Add(new InfoRow(G, "CRC  CRC7", "[7:1]", H8(c[15] >> 1), "checksum"));
    }

    private static string MidName(byte mid) => mid switch
    {
        0x02 or 0x45 => "SanDisk / Western Digital",
        0x11 => "Toshiba / Kioxia",
        0x13 or 0xFE => "Micron",
        0x15 => "Samsung",
        0x70 => "Kingston",
        0x88 => "Foresee (Longsys)",
        0x90 => "SK hynix",
        0x9B => "YMTC",
        _ => "Unknown (ไม่อยู่ในตาราง)",
    };

    // ========================== EXT_CSD ==========================
    private static void DecodeExtCsd(List<InfoRow> r, byte[] e)
    {
        ulong hcErase = e[224] * 512UL * 1024;      // bytes
        ulong hcWp = e[221] * hcErase;               // bytes

        // ---------- DEVICE ----------
        string G = "DEVICE";
        Add(r, G, "EXT_CSD_REV", e, 192, RevName(e[192]));
        Add(r, G, "CSD_STRUCTURE", e, 194, e[194] switch { 0 => "CSD v1.0", 1 => "CSD v1.1", 2 => "CSD v1.2", _ => "Reserved" });
        Add(r, G, "DEVICE_TYPE", e, 196, Flags(e[196], "HS26", "HS52", "DDR52 1.8/3V", "DDR52 1.2V",
                                                     "HS200 1.8V", "HS200 1.2V", "HS400 1.8V", "HS400 1.2V"));
        Add(r, G, "STROBE_SUPPORT", e, 184, YesNo(e[184] & 1));
        Add(r, G, "DRIVER_STRENGTH", e, 197, Flags(e[197], "Type0", "Type1", "Type2", "Type3", "Type4"));

        // ---------- CAPACITY ----------
        G = "CAPACITY";
        uint sec = U32(e, 212);
        r.Add(new InfoRow(G, "SEC_COUNT (User area)", "[215:212]", $"0x{sec:X8}", $"{sec:N0} sectors = {Sz(sec * 512UL)}"));
        Add(r, G, "HC_ERASE_GRP_SIZE", e, 224, $"{Sz(hcErase)} ต่อ erase group");
        Add(r, G, "HC_WP_GRP_SIZE", e, 221, $"{e[221]} × erase group = {Sz(hcWp)}");
        int acc = e[225] & 0x0F;
        Add(r, G, "ACC_SIZE (super-page)", e, 225, (acc >= 1 && acc <= 8) ? Sz(512UL << (acc - 1)) : "ไม่ได้กำหนด");
        Add(r, G, "REL_WR_SEC_C", e, 222, $"{e[222]} sector(s)");
        uint cache = U32(e, 249);
        r.Add(new InfoRow(G, "CACHE_SIZE", "[252:249]", $"0x{cache:X8}", cache == 0 ? "ไม่มี cache" : $"{cache} KiB"));
        Add(r, G, "DATA_SECTOR_SIZE", e, 61, e[61] == 1 ? "4 KB" : "512 B");
        Add(r, G, "NATIVE_SECTOR_SIZE", e, 63, e[63] == 1 ? "4 KB" : "512 B");

        // ---------- BOOT ----------
        G = "BOOT";
        Add(r, G, "BOOT_SIZE_MULT", e, 226, $"{e[226]} × 128 KB = {Sz(e[226] * 128UL * 1024)} ต่อพาร์ทิชัน");
        Add(r, G, "BOOT_INFO", e, 228, Flags(e[228], "ALT_BOOT", "DDR_BOOT", "HS_BOOT"));
        int pc = e[179];
        Add(r, G, "PARTITION_CONFIG", e, 179,
            $"BOOT_ACK={(pc >> 6) & 1}, BOOT_EN={BootEn((pc >> 3) & 7)}, ACCESS={PartName(pc & 7)}");
        int bb = e[177];
        string bw = (bb & 3) switch { 0 => "x1 SDR / x4 DDR", 1 => "x4", 2 => "x8", _ => "Reserved" };
        string bm = ((bb >> 3) & 3) switch { 0 => "SDR (compat)", 1 => "SDR High Speed", 2 => "DDR", _ => "Reserved" };
        Add(r, G, "BOOT_BUS_CONDITIONS", e, 177, $"WIDTH={bw}, MODE={bm}, RESET_BUS={((bb >> 2) & 1)}");
        Add(r, G, "BOOT_CONFIG_PROT", e, 178, Flags(e[178], "PWR_BOOT_CONFIG_PROT", null, null, null, "PERM_BOOT_CONFIG_PROT"));
        Add(r, G, "BOOT_WP", e, 173, Flags(e[173], "B_PWR_WP_EN", "B_PWR_WP_SEC_SEL", "B_PERM_WP_EN",
                                                  "B_PERM_WP_SEC_SEL", "B_PERM_WP_DIS", null, "B_PWR_WP_DIS", "B_SEC_WP_SEL"));
        Add(r, G, "BOOT_WP_STATUS", e, 174, $"BOOT1: {Wp(e[174] & 3)}, BOOT2: {Wp((e[174] >> 2) & 3)}");

        // ---------- PARTITION ----------
        G = "PARTITION";
        Add(r, G, "RPMB_SIZE_MULT", e, 168, $"{e[168]} × 128 KB = {Sz(e[168] * 128UL * 1024)}");
        Add(r, G, "PARTITIONING_SUPPORT", e, 160, Flags(e[160], "PARTITIONING_EN", "ENH_ATTRIBUTE_EN", "EXT_ATTRIBUTE_EN"));
        Add(r, G, "PARTITION_SETTING_COMPLETED", e, 155, (e[155] & 1) == 1 ? "ตั้งค่าแล้ว (ล็อก)" : "ยังไม่ได้ตั้งค่า");
        Add(r, G, "PARTITIONS_ATTRIBUTE", e, 156, Flags(e[156], "ENH_USR", "ENH_1", "ENH_2", "ENH_3", "ENH_4"));
        for (int gp = 0; gp < 4; gp++)
        {
            int lo = 143 + gp * 3;
            uint mult = U24(e, lo);
            r.Add(new InfoRow(G, $"GP_SIZE_MULT_{gp + 1} (GP{gp + 1})", $"[{lo + 2}:{lo}]", $"0x{mult:X6}",
                mult == 0 ? "ไม่มี" : Sz(mult * hcWp)));
        }
        uint enh = U24(e, 140);
        r.Add(new InfoRow(G, "ENH_SIZE_MULT", "[142:140]", $"0x{enh:X6}", enh == 0 ? "ไม่มี" : Sz(enh * hcWp)));
        uint enhStart = U32(e, 136);
        r.Add(new InfoRow(G, "ENH_START_ADDR", "[139:136]", $"0x{enhStart:X8}", $"{enhStart} (sector address สำหรับ >2GB)"));
        uint maxEnh = U24(e, 157);
        r.Add(new InfoRow(G, "MAX_ENH_SIZE_MULT", "[159:157]", $"0x{maxEnh:X6}", Sz(maxEnh * hcWp)));
        Add(r, G, "ERASE_GROUP_DEF", e, 175, (e[175] & 1) == 1 ? "High-Capacity erase group" : "Legacy erase group");
        Add(r, G, "USER_WP", e, 171, Flags(e[171], "US_PWR_WP_EN", null, "US_PERM_WP_EN", "US_PWR_WP_DIS",
                                                  "US_PERM_WP_DIS", null, "CD_PERM_WP_DIS", "PERM_PSWD_DIS"));
        Add(r, G, "WR_REL_SET", e, 167, Flags(e[167], "WR_DATA_REL_USR", "WR_DATA_REL_1", "WR_DATA_REL_2", "WR_DATA_REL_3", "WR_DATA_REL_4"));
        Add(r, G, "WR_REL_PARAM", e, 166, Flags(e[166], "HS_CTRL_REL", null, "EN_REL_WR", null, "EN_RPMB_REL_WR"));

        // ---------- BUS / TIMING ----------
        G = "BUS";
        Add(r, G, "HS_TIMING", e, 185, (e[185] & 0x0F) switch
        { 0 => "Backward compatible", 1 => "High Speed", 2 => "HS200", 3 => "HS400", _ => "Reserved" });
        string busw = (e[183] & 0x0F) switch
        { 0 => "1-bit SDR", 1 => "4-bit SDR", 2 => "8-bit SDR", 5 => "4-bit DDR", 6 => "8-bit DDR", _ => "Reserved" };
        Add(r, G, "BUS_WIDTH", e, 183, busw + ((e[183] & 0x80) != 0 ? " + Enhanced Strobe" : ""));
        Add(r, G, "POWER_CLASS", e, 187, $"class {e[187] & 0x0F}");
        Add(r, G, "PARTITION_SWITCH_TIME", e, 199, $"{e[199] * 10} ms");
        Add(r, G, "GENERIC_CMD6_TIME", e, 248, $"{e[248] * 10} ms");
        Add(r, G, "S_A_TIMEOUT", e, 217, (e[217] > 0 && e[217] <= 0x17)
            ? $"{100.0 * (1UL << e[217]) / 1_000_000:0.###} ms" : "ไม่ได้กำหนด");
        Add(r, G, "ERASE_TIMEOUT_MULT", e, 223, $"{e[223] * 300} ms");
        Add(r, G, "TRIM_MULT", e, 232, $"{e[232] * 300} ms");

        // ---------- HEALTH ----------
        G = "HEALTH";
        Add(r, G, "PRE_EOL_INFO", e, 267, e[267] switch
        { 1 => "Normal", 2 => "Warning (ใช้ reserved block ไป 80%)", 3 => "Urgent (ใกล้หมดอายุ)", _ => "ไม่ได้กำหนด" });
        Add(r, G, "LIFE_TIME_EST_TYP_A (SLC)", e, 268, Life(e[268]));
        Add(r, G, "LIFE_TIME_EST_TYP_B (MLC)", e, 269, Life(e[269]));
        Add(r, G, "BKOPS_STATUS", e, 246, (e[246] & 3) switch
        { 0 => "ไม่ต้องทำ", 1 => "Non-critical", 2 => "Performance impacted", _ => "Critical" });
        uint fwSec = U32(e, 302);
        r.Add(new InfoRow(G, "FW_SECTORS_CORRECTLY_PROGRAMMED", "[305:302]", $"0x{fwSec:X8}", fwSec.ToString()));

        // ---------- FIRMWARE ----------
        G = "FIRMWARE";
        r.Add(new InfoRow(G, "FIRMWARE_VERSION", "[261:254]", HexBytes(e, 254, 8), Ascii(e, 254, 8)));
        int devVer = e[262] | (e[263] << 8);
        r.Add(new InfoRow(G, "DEVICE_VERSION", "[263:262]", $"0x{devVer:X4}", devVer.ToString()));
        Add(r, G, "SUPPORTED_MODES", e, 493, Flags(e[493], "FFU", "VSM"));
        Add(r, G, "FFU_FEATURES", e, 492, Flags(e[492], "SUPPORTED_MODE_OPERATION_CODES"));
        Add(r, G, "FW_CONFIG", e, 169, (e[169] & 1) == 1 ? "Update_Disable (ห้ามอัปเดต FW)" : "อนุญาตให้อัปเดต FW");
        Add(r, G, "FFU_STATUS", e, 26, e[26] switch
        { 0x00 => "Success", 0x10 => "General error", 0x11 => "Firmware install error", 0x12 => "Download error", _ => "Reserved" });

        // ---------- FEATURE ----------
        G = "FEATURE";
        Add(r, G, "HPI_FEATURES", e, 503, (e[503] & 1) == 0 ? "ไม่รองรับ" : ((e[503] & 2) != 0 ? "รองรับ (CMD12)" : "รองรับ (CMD13)"));
        Add(r, G, "BKOPS_SUPPORT", e, 502, YesNo(e[502] & 1));
        Add(r, G, "BKOPS_EN", e, 163, Flags(e[163], "MANUAL_EN", "AUTO_EN"));
        Add(r, G, "CMDQ_SUPPORT", e, 308, (e[308] & 1) == 1 ? $"รองรับ, depth = {(e[307] & 0x1F) + 1}" : "ไม่รองรับ");
        Add(r, G, "CMDQ_MODE_EN", e, 15, YesNo(e[15] & 1, "เปิด", "ปิด"));
        Add(r, G, "BARRIER_SUPPORT", e, 486, YesNo(e[486] & 1));
        Add(r, G, "CACHE_CTRL", e, 33, YesNo(e[33] & 1, "เปิด", "ปิด"));
        Add(r, G, "RST_n_FUNCTION", e, 162, (e[162] & 3) switch
        { 0 => "ปิด (ชั่วคราว)", 1 => "เปิดถาวร", 2 => "ปิดถาวร", _ => "Reserved" });
        Add(r, G, "POWER_OFF_NOTIFICATION", e, 34, e[34] switch
        { 0 => "NO_POWER_NOTIFICATION", 1 => "POWERED_ON", 2 => "POWER_OFF_SHORT", 3 => "POWER_OFF_LONG", 4 => "SLEEP_NOTIFICATION", _ => "Reserved" });

        // ---------- SECURITY ----------
        G = "SECURITY";
        Add(r, G, "SEC_FEATURE_SUPPORT", e, 231, Flags(e[231], "SECURE_ER_EN", null, "SEC_BD_BLK_EN", null, "SEC_GB_CL_EN", null, "SEC_SANITIZE"));
        Add(r, G, "SEC_ERASE_MULT", e, 230, $"{e[230]} × erase timeout");
        Add(r, G, "SEC_TRIM_MULT", e, 229, $"{e[229]} × erase timeout");
        Add(r, G, "ERASED_MEM_CONT", e, 181, (e[181] & 1) == 1 ? "อ่านได้ 0xFF หลัง erase" : "อ่านได้ 0x00 หลัง erase");
        Add(r, G, "SECURE_WP_INFO", e, 211, Flags(e[211], "SECURE_WP_SUPPORT", "SECURE_WP_EN_STATUS"));
        Add(r, G, "SECURE_REMOVAL_TYPE", e, 16, $"supported=0x{e[16] & 0x0F:X}, configured={(e[16] >> 4) & 3}");
        Add(r, G, "SEC_BAD_BLK_MGMNT", e, 134, YesNo(e[134] & 1, "เปิด", "ปิด"));
    }

    // ========================== helpers ==========================
    private static void Add(List<InfoRow> r, string g, string name, byte[] e, int idx, string value)
        => r.Add(new InfoRow(g, name, $"[{idx}]", H8(e[idx]), value));

    private static string H8(int v) => $"0x{v:X2}";
    private static uint U24(byte[] e, int i) => (uint)(e[i] | (e[i + 1] << 8) | (e[i + 2] << 16));
    private static uint U32(byte[] e, int i) => (uint)(e[i] | (e[i + 1] << 8) | (e[i + 2] << 16) | (e[i + 3] << 24));
    private static string HexBytes(byte[] b, int start, int len) =>
        string.Join(" ", b.Skip(start).Take(len).Select(x => x.ToString("X2")));

    private static string Ascii(byte[] b, int start, int len)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < len; i++)
        {
            byte x = b[start + i];
            sb.Append((x >= 0x20 && x < 0x7F) ? (char)x : '.');
        }
        return sb.ToString().Trim();
    }

    private static string Printable(byte b) => (b >= 0x20 && b < 0x7F) ? $"'{(char)b}'" : "-";

    private static string Flags(int v, params string?[] names)
    {
        var on = new List<string>();
        for (int i = 0; i < names.Length; i++)
            if (names[i] is string n && (v & (1 << i)) != 0) on.Add(n);
        return on.Count == 0 ? "(ไม่มี)" : string.Join(", ", on);
    }

    private static string YesNo(int bit, string yes = "รองรับ", string no = "ไม่รองรับ") => bit != 0 ? yes : no;

    private static string Sz(ulong b) =>
        b >= (1UL << 30) ? $"{b / (double)(1UL << 30):0.##} GiB" :
        b >= (1UL << 20) ? $"{b / (double)(1UL << 20):0.##} MiB" :
        b >= 1024 ? $"{b / 1024.0:0.##} KiB" : $"{b} B";

    private static string RevName(byte v) => v switch
    {
        0 => "v4.0",
        1 => "v4.1",
        2 => "v4.2",
        3 => "v4.3",
        4 => "v4.4 (obsolete)",
        5 => "v4.41",
        6 => "v4.5 / v4.51",
        7 => "v5.0 / v5.01",
        8 => "v5.1 / v5.1A",
        _ => $"Unknown ({v})",
    };

    private static string Life(byte b) =>
        b == 0 ? "ไม่ได้กำหนด" :
        (b >= 1 && b <= 10) ? $"ใช้ไปแล้ว {(b - 1) * 10}%–{b * 10}%" :
        b == 11 ? "เกินอายุการใช้งานแล้ว" : "Reserved";

    private static string BootEn(int v) => v switch { 0 => "ไม่เปิด", 1 => "BOOT1", 2 => "BOOT2", 7 => "USER", _ => "Reserved" };
    private static string PartName(int v) => v switch
    { 0 => "USER", 1 => "BOOT1", 2 => "BOOT2", 3 => "RPMB", 4 => "GP1", 5 => "GP2", 6 => "GP3", 7 => "GP4", _ => "?" };
    private static string Wp(int v) => v switch { 0 => "ไม่ป้องกัน", 1 => "Power-on WP", 2 => "Permanent WP", _ => "Reserved" };
}