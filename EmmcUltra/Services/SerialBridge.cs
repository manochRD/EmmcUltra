using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using EmmcUltra.Models;

namespace EmmcUltra.Services;

/// MCU ตอบกลับมาแล้ว แต่ status != 0 (แยกจาก timeout / พอร์ตผิด)
public sealed class McuStatusException : IOException
{
    public McuStatusException(byte cmd, byte status, uint? errCode)
        : base($"{SerialBridge.CmdName(cmd)}: MCU Status Error 0x{status:X2}" +
               (errCode is uint e ? $" (SDMMC Error Code: 0x{e:X8})" : ""))
    {
        Cmd = cmd; Status = status; ErrorCode = errCode;
    }
    public byte Cmd { get; }
    public byte Status { get; }
    public uint? ErrorCode { get; }
}

public sealed class SerialBridge : IDisposable
{
    // ================= Timeout (ms) =================
    public const int ProbeTimeoutMs = 1500;   // PING ตอน CONNECT → พอร์ตผิดรู้ภายใน 1.5 วินาที
    public const int CtrlTimeoutMs = 3000;   // CID / CSD / EXT_CSD / SET_PART / CLKDIV
    public const int InitTimeoutMs = 5000;   // INIT / RPMB
    public const int IoBaseTimeoutMs = 3000;   // READ / WRITE = ฐาน + (sector × ค่าด้านล่าง)
    public const int ReadPerSectorMs = 50;
    public const int WritePerSectorMs = 100;
    public const int MaxTimeoutMs = 60000;
    private const uint MaxResponseBytes = 16 * 1024 * 1024;   // กัน header ขยะที่บอกขนาดผิด ๆ

    private SerialPort? _p;
    private readonly object _lock = new();
    private readonly AppLog _log = AppLog.Current;

    public static int MaxSectors { get; set; } = 64;
    public string FirmwareVersion { get; private set; } = "";
    private byte? _currentPart;

    public bool IsOpen => _p?.IsOpen == true;

    private static class Cmd
    {
        public const byte PING = 0x01, INIT = 0x02, CID = 0x03, CSD = 0x04, EXTCSD = 0x05, CLKDIV = 0x06;
        public const byte SET_PART = 0x10, READ = 0x11, WRITE = 0x12, RPMB_XFER = 0x20;
    }

    public static string CmdName(byte cmd) => cmd switch
    {
        Cmd.PING => "CMD01 PING",
        Cmd.INIT => "CMD02 INIT",
        Cmd.CID => "CMD03 CID",
        Cmd.CSD => "CMD04 CSD",
        Cmd.EXTCSD => "CMD05 EXT_CSD",
        Cmd.CLKDIV => "CMD06 CLKDIV",
        Cmd.SET_PART => "CMD10 SET_PART",
        Cmd.READ => "CMD11 READ",
        Cmd.WRITE => "CMD12 WRITE",
        Cmd.RPMB_XFER => "CMD20 RPMB_XFER",
        _ => $"CMD{cmd:X2}",
    };

    // ================= Port =================
    public void Open(string port)
    {
        Close();
        var p = new SerialPort(port, 115200)
        {
            ReadTimeout = ProbeTimeoutMs,
            WriteTimeout = ProbeTimeoutMs,
            DtrEnable = true,
            RtsEnable = true,
            ReadBufferSize = 1 << 20,
            WriteBufferSize = 1 << 16
        };
        _log.Dbg($"OPEN {port} @115200 8N1, DTR=1, RTS=1");
        try
        {
            p.Open();
            p.DiscardInBuffer();
            p.DiscardOutBuffer();
        }
        catch
        {
            try { p.Dispose(); } catch { }
            throw;
        }
        _p = p;
        _currentPart = null;
    }

    /// ปลอดภัยที่จะเรียกจาก thread อื่นระหว่างที่ Xfer ค้างอยู่ → Read ที่ค้างจะหลุดทันที
    public void Close()
    {
        var p = Interlocked.Exchange(ref _p, null);
        _currentPart = null;
        if (p == null) return;
        try { if (p.IsOpen) p.Close(); } catch { }
        try { p.Dispose(); } catch { }
        _log.Dbg("CLOSE port");
    }

    public void Dispose() => Close();

    public void Resync()
    {
        var p = _p;
        if (p != null) Resync(p);
        _currentPart = null;
    }

    private void Resync(SerialPort p)
    {
        Thread.Sleep(50);
        try
        {
            int junk = p.BytesToRead;
            p.DiscardInBuffer();
            if (junk > 0) _log.Dbg($"RESYNC: ทิ้งข้อมูลค้างในบัฟเฟอร์ {junk} ไบต์");
        }
        catch { }
    }

    // ================= Bridge Actions =================
    /// ใช้ตรวจว่าพอร์ตนี้เป็น STM32 bridge จริง — พอร์ตผิดจะ throw ภายใน ProbeTimeoutMs
    public (string fw, bool card) Ping()
    {
        byte[] resp;
        try
        {
            resp = Xfer(Cmd.PING, default, ProbeTimeoutMs);
        }
        catch (McuStatusException ex)
        {
            // อุปกรณ์ตอบ header ถูกต้อง (เป็น bridge จริง) แต่ไม่รองรับ PING
            _log.Warn($"PING: เฟิร์มแวร์ตอบ status 0x{ex.Status:X2} (อาจไม่รองรับ PING) — ใช้ค่าเริ่มต้น");
            return ("STM32-Bridge", true);
        }

        if (resp.Length < 8) return ("STM32-Bridge", true);

        int max = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp);
        if (max >= 1 && max <= 4096) MaxSectors = max;
        else _log.Warn($"PING: MaxSectors={max} ผิดปกติ — ใช้ค่าเดิม {MaxSectors}");

        int card = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(4));
        FirmwareVersion = System.Text.Encoding.ASCII.GetString(resp, 8, resp.Length - 8).TrimEnd('\0');
        if (FirmwareVersion.Length == 0) FirmwareVersion = "STM32-Bridge";
        return (FirmwareVersion, card != 0);
    }

    public (int sectors, int hz, int width) Init(int clkDiv, int busWidth)
    {
        var pl = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(pl, (ushort)clkDiv);
        pl[2] = (byte)busWidth;

        var resp = Xfer(Cmd.INIT, pl);
        _currentPart = 0;

        if (resp.Length < 8) throw new IOException($"INIT: ข้อมูลตอบกลับสั้นเกินไป ({resp.Length} ไบต์)");
        int sectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp);
        int hz = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(4));
        int width = resp.Length >= 9 ? resp[8] : busWidth;
        return (sectors, hz, width);
    }

    // รองรับทั้งชื่อเก่าและใหม่
    public byte[] Cid() => Xfer(Cmd.CID);
    public byte[] ReadCid() => Cid();

    public byte[] Csd() => Xfer(Cmd.CSD);

    public byte[] ExtCsd() => Xfer(Cmd.EXTCSD);
    public byte[] ReadExtCsd() => ExtCsd();

    public int SetClkDiv(int div)
    {
        var pl = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(pl, (ushort)div);
        var resp = Xfer(Cmd.CLKDIV, pl);
        return resp.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(resp) : div;
    }

    public void SetClock(int div, int busBits = 1)
    {
        SetClkDiv(div);
        Init(div, busBits);
    }

    public void SelectPart(int p, bool force = false)
    {
        if (force || _currentPart != (byte)p)
        {
            Xfer(Cmd.SET_PART, [(byte)p]);
            _currentPart = (byte)p;
        }
        else _log.Dbg($"SET_PART ข้าม — อยู่ที่ {PartName((byte)p)} แล้ว");
    }
    public void SelectPart(HwPart part) => SelectPart((int)part);

    // ================= Read / Write (แบ่งชุดตาม MaxSectors อัตโนมัติ) =================
    public byte[] Read(ulong lba, int count)
    {
        if (count <= 0) return [];
        int max = Math.Max(1, MaxSectors);
        if (count <= max) return ReadChunk(lba, count);

        var all = new byte[count * 512];
        int done = 0;
        while (done < count)
        {
            int n = Math.Min(max, count - done);
            ReadChunk(lba + (ulong)done, n).CopyTo(all, done * 512);
            done += n;
        }
        return all;
    }

    private byte[] ReadChunk(ulong lba, int count)
    {
        CheckRange(lba, count);
        var pl = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pl, (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(pl.AsSpan(4), (uint)count);
        var d = Xfer(Cmd.READ, pl);
        if (d.Length != count * 512)
            _log.Warn($"READ LBA 0x{lba:X}: ขอ {count * 512} ไบต์ แต่ได้รับ {d.Length} ไบต์");
        return d;
    }

    public void Write(ulong lba, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        if (data.Length % 512 != 0)
        {
            int padded = (data.Length + 511) / 512 * 512;
            _log.Warn($"WRITE LBA 0x{lba:X}: ข้อมูล {data.Length} ไบต์ไม่ลงตัว sector — เติม 0x00 เป็น {padded} ไบต์");
            var buf = new byte[padded];
            data.CopyTo(buf);
            Write(lba, buf);
            return;
        }

        int maxBytes = Math.Max(1, MaxSectors) * 512;
        for (int off = 0; off < data.Length; off += maxBytes)
        {
            var chunk = data.Slice(off, Math.Min(maxBytes, data.Length - off));
            WriteChunk(lba + (ulong)(off / 512), chunk);
        }
    }

    private void WriteChunk(ulong lba, ReadOnlySpan<byte> data)
    {
        int sectors = data.Length / 512;
        CheckRange(lba, sectors);
        var pl = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pl, (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(pl.AsSpan(4), (uint)sectors);
        data.CopyTo(pl.AsSpan(8));
        Xfer(Cmd.WRITE, pl);
    }

    private static void CheckRange(ulong lba, int count)
    {
        if (lba + (ulong)count > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lba), $"LBA 0x{lba:X} เกินขอบเขต 32-bit ของโปรโตคอล");
    }

    // ================= Low-level Xfer =================
    private byte[] Xfer(byte cmd, ReadOnlySpan<byte> payload = default, int timeoutMs = 0)
    {
        var p = _p ?? throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อพอร์ต Serial");
        string name = CmdName(cmd);
        int tmo = timeoutMs > 0 ? timeoutMs : TimeoutFor(cmd, payload);

        lock (_lock)
        {
            try
            {
                if (p.ReadTimeout != tmo) { p.ReadTimeout = tmo; p.WriteTimeout = tmo; }

                var header = new byte[8];
                header[0] = (byte)'E';
                header[1] = (byte)'M';
                header[2] = cmd;
                header[3] = 0;
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);

                if (AppLog.DebugEnabled)
                {
                    string desc = Describe(cmd, payload);
                    _log.Tx(desc.Length > 0 ? $"{name} [{desc}] timeout={tmo}ms" : $"{name} timeout={tmo}ms", header);
                    if (payload.Length > 0) _log.Tx($"{name} PAYLOAD", payload);
                }

                var sw = Stopwatch.StartNew();
                p.BaseStream.Write(header, 0, 8);
                if (payload.Length > 0) p.BaseStream.Write(payload);
                p.BaseStream.Flush();

                var respHdr = ReadExact(p, 8);
                if (AppLog.DebugEnabled) _log.Rx($"{name} HEADER ({sw.ElapsedMilliseconds} ms)", respHdr);

                if (respHdr[0] != 'R' || respHdr[1] != 'S' || respHdr[2] != cmd)
                {
                    Resync(p);
                    throw new IOException(
                        $"{name}: Bad response header [{BitConverter.ToString(respHdr)}] — " +
                        "อุปกรณ์นี้อาจไม่ใช่ STM32 eMMC bridge หรือมีข้อมูลเก่าค้างในบัฟเฟอร์");
                }

                byte status = respHdr[3];
                uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(respHdr.AsSpan(4));
                if (dataLen > MaxResponseBytes)
                {
                    Resync(p);
                    throw new IOException($"{name}: ขนาดข้อมูลตอบกลับผิดปกติ ({dataLen} ไบต์)");
                }

                byte[] data = dataLen > 0 ? ReadExact(p, (int)dataLen) : [];
                if (AppLog.DebugEnabled && data.Length > 0)
                    _log.Rx($"{name} DATA status=0x{status:X2} ({sw.ElapsedMilliseconds} ms)", data);

                if (status != 0)
                {
                    uint? err = data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : (uint?)null;
                    throw new McuStatusException(cmd, status, err);
                }
                return data;
            }
            catch (TimeoutException ex)
            {
                _currentPart = null;
                Resync(p);
                throw new TimeoutException($"{name}: STM32 ไม่ตอบภายใน {tmo} ms", ex);
            }
            catch (McuStatusException)
            {
                throw;                       // MCU ตอบครบแล้ว — สถานะพาร์ทิชันยังถูกต้อง
            }
            catch
            {
                _currentPart = null;         // error อื่น ๆ → บังคับ SET_PART ใหม่ครั้งถัดไป
                throw;
            }
        }
    }

    private static int TimeoutFor(byte cmd, ReadOnlySpan<byte> pl)
    {
        uint sectors = pl.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(pl.Slice(4)) : 1;
        long t = cmd switch
        {
            Cmd.READ => IoBaseTimeoutMs + (long)sectors * ReadPerSectorMs,
            Cmd.WRITE => IoBaseTimeoutMs + (long)sectors * WritePerSectorMs,
            Cmd.INIT => InitTimeoutMs,
            Cmd.RPMB_XFER => InitTimeoutMs,
            _ => CtrlTimeoutMs,
        };
        return (int)Math.Min(t, MaxTimeoutMs);
    }

    /// แปลง payload เป็นข้อความอ่านง่ายสำหรับ debug log
    private static string Describe(byte cmd, ReadOnlySpan<byte> pl)
    {
        switch (cmd)
        {
            case Cmd.SET_PART when pl.Length >= 1:
                return $"part={PartName(pl[0])}";
            case Cmd.READ when pl.Length >= 8:
                {
                    uint lba = U32(pl, 0), n = U32(pl, 4);
                    return $"LBA=0x{lba:X} count={n} ({n * 512} B)";
                }
            case Cmd.WRITE when pl.Length >= 8:
                {
                    uint lba = U32(pl, 0), n = U32(pl, 4);
                    return $"LBA=0x{lba:X} sectors={n} ({pl.Length - 8} B data)";
                }
            case Cmd.INIT when pl.Length >= 3:
                return $"clkdiv={BinaryPrimitives.ReadUInt16LittleEndian(pl)} bus={pl[2]}-bit";
            case Cmd.CLKDIV when pl.Length >= 2:
                return $"clkdiv={BinaryPrimitives.ReadUInt16LittleEndian(pl)}";
            default:
                return "";
        }
    }

    private static uint U32(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i));

    private static string PartName(byte p) => p switch
    {
        0 => "USER",
        1 => "BOOT1",
        2 => "BOOT2",
        3 => "RPMB",
        4 => "GP1",
        5 => "GP2",
        6 => "GP3",
        7 => "GP4",
        _ => $"0x{p:X2}",
    };

    private static byte[] ReadExact(SerialPort p, int n)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = p.BaseStream.Read(b, got, n - got);   // ไม่มีข้อมูลเข้าเกิน ReadTimeout → TimeoutException
            if (r <= 0) throw new IOException("ขาดการเชื่อมต่อกับอุปกรณ์");
            got += r;
        }
        return b;
    }
}