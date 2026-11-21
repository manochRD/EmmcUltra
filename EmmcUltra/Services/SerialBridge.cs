using System.Buffers.Binary;
using System.IO.Ports;
using EmmcUltra.Models;
using EmmcUltra.Services;

namespace EmmcUltra.Services;

public sealed class SerialBridge : IDisposable
{
    private SerialPort? _p;
    private readonly object _lock = new();

    // ทำให้ MaxSectors เป็น static หรือ property ที่เรียกใช้ง่าย
    public static int MaxSectors { get; set; } = 64;
    public string FirmwareVersion { get; private set; } = "";
    private byte? _currentPart;

    public bool IsOpen => _p?.IsOpen == true;

    private static class Cmd
    {
        public const byte PING = 0x01, INIT = 0x02, CID = 0x03, CSD = 0x04, EXTCSD = 0x05, CLKDIV = 0x06;
        public const byte SET_PART = 0x10, READ = 0x11, WRITE = 0x12, RPMB_XFER = 0x20;
    }

    public void Open(string port)
    {
        Close();
        _p = new SerialPort(port, 115200)
        {
            ReadTimeout = 15000,
            WriteTimeout = 15000,
            DtrEnable = true,
            RtsEnable = true,
            ReadBufferSize = 1 << 20,
            WriteBufferSize = 1 << 16
        };
        _p.Open();
        _p.DiscardInBuffer();
        _currentPart = null;
        AppLog.Current.Dbg($"OPEN {port} (ReadTimeout={_p.ReadTimeout} ms)");
    }

    public void Close()
    {
        var sp = _p; _p = null;
        if (sp == null) return;
        try { sp.Close(); } catch { }
        sp.Dispose();
        _currentPart = null;
        AppLog.Current.Dbg("CLOSE port");
    }

    public void Dispose() => Close();

    public void Resync()
    {
        Thread.Sleep(50);
        try { _p?.DiscardInBuffer(); } catch { }
    }

    // --- Bridge Actions & Aliases ---
    public (string fw, bool card) Ping()
    {
        try
        {
            var resp = Xfer(Cmd.PING);
            if (resp.Length >= 8)
            {
                MaxSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp);
                int card = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(4));
                FirmwareVersion = System.Text.Encoding.ASCII.GetString(resp[8..]).TrimEnd('\0');
                return (FirmwareVersion, card != 0);
            }
        }
        catch { }
        return ("STM32-Bridge", true);
    }

    public (int sectors, int hz, int width) Init(int clkDiv, int busWidth)
    {
        var pl = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(pl, (ushort)clkDiv);
        pl[2] = (byte)busWidth;

        var resp = Xfer(Cmd.INIT, pl);
        _currentPart = 0;

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

    // รองรับ SetClock ด้วย
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
    }
    public void SelectPart(HwPart part) => SelectPart((int)part);

    public byte[] Read(ulong lba, int count)
    {
        var pl = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pl, (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(pl.AsSpan(4), (uint)count);
        return Xfer(Cmd.READ, pl);
    }

    public void Write(ulong lba, ReadOnlySpan<byte> data)
    {
        int sectors = data.Length / 512;
        var pl = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pl, (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(pl.AsSpan(4), (uint)sectors);
        data.CopyTo(pl.AsSpan(8));
        Xfer(Cmd.WRITE, pl);
    }

    // --- Low-level Xfer ---
    private byte[] Xfer(byte cmd, ReadOnlySpan<byte> payload = default)
    {
        var p = _p ?? throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อพอร์ต Serial");
        lock (_lock)
        {
            var header = new byte[8];
            header[0] = (byte)'E';
            header[1] = (byte)'M';
            header[2] = cmd;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);

            p.BaseStream.Write(header, 0, 8);
            if (payload.Length > 0)
            {
                p.BaseStream.Write(payload);
            }
            p.BaseStream.Flush();

            var respHdr = ReadExact(p, 8);
            if (respHdr[0] != 'R' || respHdr[1] != 'S' || respHdr[2] != cmd)
            {
                p.DiscardInBuffer();
                throw new IOException($"Bad response header: {BitConverter.ToString(respHdr)}");
            }

            byte status = respHdr[3];
            uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(respHdr.AsSpan(4));
            byte[] data = dataLen > 0 ? ReadExact(p, (int)dataLen) : [];

            if (status != 0)
            {
                string errDetail = "";
                if (data.Length >= 4)
                {
                    uint errCode = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    errDetail = $" (SDMMC Error Code: 0x{errCode:X8})";
                }
                throw new IOException(string.Format("MCU Status Error: 0x{0:X2}{1}", status, errDetail));
            }

            return data;
        }
    }

    private static byte[] ReadExact(SerialPort p, int n)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = p.BaseStream.Read(b, got, n - got);
            if (r <= 0) throw new IOException("ขาดการเชื่อมต่อกับอุปกรณ์");
            got += r;
        }
        return b;
    }
}