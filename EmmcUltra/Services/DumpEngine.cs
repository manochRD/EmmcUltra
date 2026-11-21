using System.Diagnostics;
using EmmcUltra.Models;

namespace EmmcUltra.Services;

public sealed record DumpProgress(long Done, long Total, double BytesPerSec, TimeSpan Eta, ulong Lba);

internal sealed class Meter(long total, IProgress<DumpProgress> prog)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastMs, _lastDone;
    private double _speed;

    public void Update(long done, ulong lba, bool force = false)
    {
        long now = _sw.ElapsedMilliseconds;
        if (!force && now - _lastMs < 250) return;          // อัปเดต UI แค่ ~4 ครั้ง/วินาที
        double inst = (done - _lastDone) * 1000.0 / Math.Max(1, now - _lastMs);
        _speed = _speed == 0 ? inst : _speed * 0.7 + inst * 0.3;
        var eta = _speed > 1 ? TimeSpan.FromSeconds((total - done) / _speed) : TimeSpan.Zero;
        prog.Report(new DumpProgress(done, total, _speed, eta, lba));
        _lastMs = now; _lastDone = done;
    }
}

public static class DumpEngine
{
    private static int Chunk => SerialBridge.MaxSectors;

    private static byte[] ReadRetry(SerialBridge br, ulong lba, int n)
    {
        for (int a = 1; ; a++)
        {
            try { return br.Read(lba, n); }
            catch (Exception e) when (a < 3 && e is IOException or TimeoutException)
            { br.Resync(); }
        }
    }

    public static void ReadToFile(SerialBridge br, HwPart part, ulong startLba, ulong count,
                                  string path, IProgress<DumpProgress> prog, CancellationToken ct)
    {
        br.SelectPart(part);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4 << 20);
        long total = (long)count * 512, done = 0;
        var meter = new Meter(total, prog);
        ulong lba = startLba, left = count;

        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min((ulong)Chunk, left);
            var buf = ReadRetry(br, lba, n);
            fs.Write(buf, 0, buf.Length);
            lba += (ulong)n; left -= (ulong)n; done += buf.Length;
            meter.Update(done, lba);
        }
        meter.Update(done, lba, force: true);
    }

    public static void WriteFromFile(SerialBridge br, HwPart part, ulong startLba, string path,
                                     IProgress<DumpProgress> prog, CancellationToken ct)
    {
        br.SelectPart(part);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 << 20);
        long total = (fs.Length + 511) / 512 * 512, done = 0;
        var meter = new Meter(total, prog);
        var buf = new byte[Chunk * 512];
        ulong lba = startLba;

        while (done < total)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buf.Length, total - done);
            int got = 0, r;
            while (got < want && (r = fs.Read(buf, got, want - got)) > 0) got += r;
            if (got < want) Array.Clear(buf, got, want - got);   // เติม 0 ให้ครบเซกเตอร์

            br.Write(lba, buf.AsSpan(0, want));
            lba += (ulong)(want / 512); done += want;
            meter.Update(done, lba);
        }
        meter.Update(done, lba, force: true);
    }
}