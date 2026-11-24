using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class EmmcFlashTool
{
    public static async Task WriteFirmwareAsync(
        string filePath,
        ulong startSector,
        DispatcherQueue dispatcherQueue,
        Action<int, string> onProgress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("ไม่พบไฟล์เฟิร์มแวร์ที่ระบุ", filePath);

        var fileInfo = new FileInfo(filePath);
        long totalBytes = fileInfo.Length;
        int sectorSize = 512;

        // คำนวณจำนวนเซกเตอร์ (ปัดเศษขึ้นรองรับไฟล์ทุกขนาด ไม่บังคับหารลงตัวแล้ว)
        ulong totalSectors = (ulong)((totalBytes + sectorSize - 1) / sectorSize);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        byte[] buffer = new byte[1024 * 1024]; // 1 MB buffer
        long bytesReadTotal = 0;
        ulong currentSector = startSector;
        int currentProgress = -1;

        while (bytesReadTotal < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bytesToRead = (int)Math.Min(buffer.Length, totalBytes - bytesReadTotal);
            int read = await fs.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (read == 0) break;

            // ถ้าบล็อกสุดท้ายอ่านมาแล้วไม่ลงตัว 512 ไบต์ ให้เติม 0 (Zero-padding) ให้ครบเซกเตอร์
            int remainder = read % sectorSize;
            int bytesToWrite = read;
            if (remainder != 0)
            {
                int paddedLength = read + (sectorSize - remainder);
                if (paddedLength > buffer.Length)
                {
                    // ป้องกันบัฟเฟอร์ล้น
                    Array.Resize(ref buffer, paddedLength);
                }
                Array.Clear(buffer, read, paddedLength - read);
                bytesToWrite = paddedLength;
            }

            int sectorsToWrite = bytesToWrite / sectorSize;

            // ⚠️ [จุดเชื่อมฮาร์ดแวร์]: ส่งข้อมูลลง eMMC จริงตรงนี้
            // เช่น: EmmcLowLevelWrite(currentSector, buffer, bytesToWrite);

            bytesReadTotal += read; // นับตามไบต์ไฟล์จริง
            currentSector += (ulong)sectorsToWrite;

            int progress = (int)((bytesReadTotal * 100) / totalBytes);
            if (progress != currentProgress)
            {
                currentProgress = progress;
                string statusText = $"กำลังเขียนเฟิร์มแวร์... {currentProgress}% ({bytesReadTotal:N0} / {totalBytes:N0} bytes)";

                dispatcherQueue.TryEnqueue(() =>
                {
                    onProgress?.Invoke(currentProgress, statusText);
                });
            }
        }
    }
}