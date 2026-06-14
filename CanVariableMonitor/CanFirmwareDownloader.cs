using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CanVariableMonitor;

internal static class CanFirmwareDownloader
{
    private const int AckTimeoutMs = 3000;
    private const uint DefaultStartAddress = 0x2000;
    private const uint MonitorRequestId = 0x7F0;
    private const uint MonitorResponseId = 0x7F1;
    private const bool LogDataFrames = false;
    private const int MaxFramesPerWaitSweep = 2048;
    private const int MaxFramesPerDrainSweep = 4096;
    private const int FastAckPollMs = 12;
    private const int FastAckSpinLoops = 80;
    private const int FastModeAckTimeoutMs = 600;
    private const int FastPageSize = 0x100;
    private static readonly bool EnableFastModeProbe = false;
    private const int BootControlFailureResyncThreshold = 3;
    private static readonly object PendingLock = new();
    private static readonly Queue<CanFrame> PendingFrames = new();
    private static readonly object RxDiagnosticLock = new();
    private static string _lastRxDiagnosticText = "";
    private static DateTime _lastRxDiagnosticWriteUtc = DateTime.MinValue;
    private static int _suppressedRxDiagnosticCount;

    public static void Download(ICanAdapter adapter, string binPath, string? projectRoot, IProgress<string>? progress, CancellationToken token)
    {
        using TimerResolutionScope timerResolution = TimerResolutionScope.Begin();
        token.ThrowIfCancellationRequested();
        byte[] firmwareFile = File.ReadAllBytes(binPath);
        token.ThrowIfCancellationRequested();
        if (firmwareFile.Length == 0)
        {
            throw new InvalidOperationException("bin 文件为空。");
        }

        byte[] firmware = TrimFirmwareForBoot(firmwareFile, out _);
        uint startAddress = DefaultStartAddress;
        uint endAddress = startAddress + (uint)firmware.Length - 1;

        ClearPendingFrames();
        WriteDiagnostic($"下载准备：adapter={adapter.Name}，bin={binPath}，控制器复位入口=0x{MonitorRequestId:X3}/0x{MonitorProtocol.RebootCommand:X2}");
        Drain(adapter, 30, token);
        TryRequestMonitorReboot(adapter, token);
        WaitForBootHandshake(adapter, token);

        WriteDiagnostic($"发送下载范围：0x{startAddress:X} - 0x{endAddress:X}");
        SendControlAndWait(
            adapter,
            0x11,
            new byte[]
            {
                0x81,
                (byte)((startAddress >> 8) & 0xFF),
                (byte)(startAddress & 0xFF),
                0x00,
                (byte)((endAddress >> 16) & 0xFF),
                (byte)((endAddress >> 8) & 0xFF),
                (byte)(endAddress & 0xFF)
            },
            0x16,
            0x02,
            token);
        SleepCancelable(20, token);
        bool useFastMode = TryEnableFastMode(adapter, token);
        SleepCancelable(20, token);
        SendControlAndWait(adapter, 0x11, new byte[] { 0x80, 0xFF, 0xFF }, 0x16, 0x01, token);
        SleepCancelable(20, token);
        DownloadPayload(adapter, firmware, useFastMode, progress, token);
    }

    private static void TryRequestMonitorReboot(ICanAdapter adapter, CancellationToken token)
    {
        byte seq = (byte)Environment.TickCount;
        byte[] data = MonitorProtocol.BuildRebootRequest(seq);

        for (int i = 0; i < 3 && !token.IsCancellationRequested; i++)
        {
            WriteDiagnostic($"发送监控复位请求：0x{MonitorRequestId:X3}，第 {i + 1} 次");
            token.ThrowIfCancellationRequested();
            SendFrame(adapter, MonitorRequestId, data);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(350);
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                CanFrame? frame = WaitForFrame(adapter, 20, f =>
                    (f.Id == MonitorResponseId && MonitorProtocol.TryParseRebootAck(f, seq)) ||
                    f.Id == 0x702 ||
                    f.Id == 0x703 ||
                    IsBootHandshakeAck(f), token);

                if (frame == null)
                {
                    continue;
                }

                if (frame.Value.Id == MonitorResponseId)
                {
                    WriteDiagnostic("已收到监控复位应答，等待控制器重启。");
                    return;
                }

                if (frame.Value.Id == 0x702)
                {
                    WriteDiagnostic("复位后已收到 0x702，立即发送 0x10 握手。");
                    token.ThrowIfCancellationRequested();
                    SendBootOpen(adapter);
                    return;
                }

                if (IsBootHandshakeAck(frame.Value))
                {
                    WriteDiagnostic("复位阶段已收到 0x15，boot 握手成功。");
                    SavePendingFrame(frame.Value);
                    return;
                }

                if (frame.Value.Id == 0x703)
                {
                    WriteDiagnostic("复位阶段收到 0x703，boot 窗口已快退出。");
                    SavePendingFrame(frame.Value);
                    return;
                }
            }
        }

        WriteDiagnostic("未收到监控复位应答，继续等待 boot ready。");
    }

    private static void WaitForBootHandshake(ICanAdapter adapter, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(18);
        DateTime nextOpenSendUtc = DateTime.MinValue;
        int sweep = 0;
        int controlFailureCount = 0;

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextOpenSendUtc)
            {
                token.ThrowIfCancellationRequested();
                SendBootOpen(adapter);
                nextOpenSendUtc = DateTime.UtcNow.AddMilliseconds(120);
            }

            CanFrame? frame = WaitForFrame(adapter, 80, f => f.Id == 0x702 || f.Id == 0x703 || IsBootHandshakeAck(f) || IsControlAck(f), token);
            token.ThrowIfCancellationRequested();
            if (frame != null)
            {
                if (IsBootHandshakeAck(frame.Value))
                {
                    WriteDiagnostic("boot handshake OK.");
                    ClearPendingFrames();
                    return;
                }

                if (IsControlAck(frame.Value))
                {
                    byte code = frame.Value.Data[0];
                    if (code == 0x00)
                    {
                        controlFailureCount++;
                        WriteDiagnostic("RX 0x016 00 while waiting boot handshake; resync count=" + controlFailureCount + ".");
                        if (controlFailureCount >= BootControlFailureResyncThreshold)
                        {
                            ClearPendingFrames();
                            Drain(adapter, 5, token);
                            SendBootOpen(adapter);
                            nextOpenSendUtc = DateTime.UtcNow.AddMilliseconds(120);
                            controlFailureCount = 0;
                        }

                        continue;
                    }

                    WriteDiagnostic("RX 0x016 while waiting boot handshake; send 0x10 to resync.");
                    ClearPendingFrames();
                    SendBootOpen(adapter);
                    nextOpenSendUtc = DateTime.UtcNow.AddMilliseconds(120);
                    continue;
                }

                if (frame.Value.Id == 0x702)
                {
                    WriteDiagnostic("收到 0x702，立即发送 0x10 握手。");
                    SendBootOpen(adapter);
                    nextOpenSendUtc = DateTime.UtcNow.AddMilliseconds(120);
                }
                else if (frame.Value.Id == 0x703)
                {
                    WriteDiagnostic("收到 0x703，boot 即将退出，继续发送 0x10 抢窗口。");
                }
            }

            sweep++;
            if ((sweep % 10) == 0)
            {
                WriteDiagnostic($"等待控制器 boot ready/0x15，第 {sweep} 轮。");
            }
        }

        throw new TimeoutException("未收到 boot 握手。请确认控制器在线、CAN 通道正常、程序已安装并下载过最新监控固件。");
    }

    private static void SendBootOpen(ICanAdapter adapter)
    {
        SendFrame(adapter, 0x10, new byte[] { 0xFF, 0xFE, 0xFD, 0xDF, 0xEF });
    }

    private static bool IsBootHandshakeAck(CanFrame frame)
    {
        return frame.Id == 0x15 &&
            frame.Dlc >= 2 &&
            frame.Data.Length >= 2 &&
            frame.Data[0] == 0x04 &&
            frame.Data[1] == 0x01;
    }

    private static bool IsControlAck(CanFrame frame)
    {
        return frame.Id == 0x16 &&
            frame.Dlc >= 1 &&
            frame.Data.Length >= 1;
    }

    private static bool IsControlFailureAck(CanFrame frame)
    {
        return IsControlAck(frame) && frame.Data[0] == 0x00;
    }

    private static void SendControlAndWait(ICanAdapter adapter, uint id, byte[] data, uint ackId, byte ackByte0, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ClearPendingFrames();
        SendFrame(adapter, id, data);
        CanFrame? ack = WaitForFrame(adapter, Math.Max(AckTimeoutMs, 10000), f =>
            f.Id == ackId &&
            f.Dlc >= 1 &&
            f.Data.Length >= 1, token);
        if (ack == null)
        {
            throw new TimeoutException($"等待 0x{ackId:X3} 应答超时。");
        }

        byte code = ack.Value.Data[0];
        if (code == ackByte0)
        {
            return;
        }

        if (ackId == 0x16 && code == 0x00)
        {
            WriteDiagnostic("Boot control rejected: TX 0x" + id.ToString("X3") + " " + FormatData(data, data.Length) + ", RX 0x016 00.");
            TryRecoverBootOpen(adapter, token);
            throw new InvalidOperationException("Boot 控制命令被拒绝，已停止下载。请重新点击下载，必要时给控制器重新上电。");
        }

        throw new InvalidOperationException("Boot 应答异常：0x" + ackId.ToString("X3") + " " + FormatFrameData(ack.Value));
    }

    private static bool TryEnableFastMode(ICanAdapter adapter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!EnableFastModeProbe)
        {
            WriteDiagnostic("Boot download mode: legacy-safe; fast probe disabled for old bootloader compatibility.");
            return false;
        }

        ClearPendingFrames();
        SendFrame(adapter, 0x11, new byte[] { 0x82, 0x01 });
        CanFrame? ack = WaitForFrame(adapter, FastModeAckTimeoutMs, f =>
            f.Id == 0x16 &&
            f.Dlc >= 1 &&
            f.Data.Length >= 1, token);
        if (ack != null &&
            ack.Value.Dlc >= 2 &&
            ack.Value.Data.Length >= 2 &&
            ack.Value.Data[0] == 0x04 &&
            ack.Value.Data[1] == 0x01)
        {
            WriteDiagnostic("Boot download mode: fast page ACK.");
            return true;
        }

        if (ack != null && ack.Value.Data[0] == 0x00)
        {
            WriteDiagnostic("Boot download mode: fast probe rejected; resync and use legacy.");
            TryRecoverBootOpen(adapter, token);
        }

        WriteDiagnostic("Boot download mode: legacy per-frame ACK.");
        return false;
    }

    private static void TryRecoverBootOpen(ICanAdapter adapter, CancellationToken token)
    {
        try
        {
            ClearPendingFrames();
            Drain(adapter, 5, token);
            for (int i = 0; i < 3 && !token.IsCancellationRequested; i++)
            {
                SendBootOpen(adapter);
                CanFrame? frame = WaitForFrame(adapter, 180, f =>
                    IsBootHandshakeAck(f) ||
                    f.Id == 0x702 ||
                    f.Id == 0x703 ||
                    IsControlFailureAck(f), token);
                if (frame != null && IsBootHandshakeAck(frame.Value))
                {
                    WriteDiagnostic("Boot resync handshake OK.");
                    ClearPendingFrames();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteDiagnostic("Boot resync failed: " + ex.Message);
        }
        finally
        {
            ClearPendingFrames();
        }
    }

    private static void DownloadPayload(ICanAdapter adapter, byte[] firmware, bool useFastMode, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int bytesSent = 0;
        Stopwatch sw = Stopwatch.StartNew();
        int lastPercent = -1;

        ReportProgress(progress, 0, firmware.Length, ref lastPercent);

        if (useFastMode)
        {
            while (bytesSent < firmware.Length && !token.IsCancellationRequested)
            {
                int pageRemaining = Math.Min(FastPageSize - (bytesSent % FastPageSize), firmware.Length - bytesSent);
                int batchSent = 0;
                while (batchSent < pageRemaining && !token.IsCancellationRequested)
                {
                    int chunkLength = Math.Min(8, pageRemaining - batchSent);
                    byte[] chunk = new byte[chunkLength];
                    Buffer.BlockCopy(firmware, bytesSent, chunk, 0, chunkLength);
                    token.ThrowIfCancellationRequested();
                    SendFrame(adapter, 0x12, chunk);
                    bytesSent += chunkLength;
                    batchSent += chunkLength;
                }

                byte ackCode = WaitDataAck(adapter, fastMode: true, token);
                if ((bytesSent % FastPageSize) == 0 || bytesSent == firmware.Length || ackCode == 0x00)
                {
                    ReportProgress(progress, bytesSent, firmware.Length, ref lastPercent);
                }
            }
        }
        else
        {
            while (bytesSent < firmware.Length && !token.IsCancellationRequested)
            {
                int chunkLength = Math.Min(8, firmware.Length - bytesSent);
                byte[] chunk = new byte[chunkLength];
                Buffer.BlockCopy(firmware, bytesSent, chunk, 0, chunkLength);
                token.ThrowIfCancellationRequested();
                byte ackCode = SendDataChunkAndWait(adapter, chunk, bytesSent, token);
                bytesSent += chunkLength;
                if ((bytesSent % 512) == 0 || bytesSent == firmware.Length || ackCode == 0x00)
                {
                    ReportProgress(progress, bytesSent, firmware.Length, ref lastPercent);
                }
            }
        }

        sw.Stop();
        token.ThrowIfCancellationRequested();
        ReportProgress(progress, firmware.Length, firmware.Length, ref lastPercent);
        double kbPerSecond = firmware.Length <= 0 || sw.Elapsed.TotalSeconds <= 0
            ? 0
            : firmware.Length / 1024.0 / sw.Elapsed.TotalSeconds;
        WriteDiagnostic($"下载数据发送完成，耗时 {sw.ElapsedMilliseconds} ms，速度 {kbPerSecond:F2} KB/s。");
    }

    private static byte SendDataChunkAndWait(ICanAdapter adapter, byte[] chunk, int offset, CancellationToken token)
    {
        Exception? lastError = null;
        for (int retry = 0; retry < 3 && !token.IsCancellationRequested; retry++)
        {
            if (LogDataFrames || retry > 0)
            {
                WriteDiagnostic("TX 0x012 offset=0x" + offset.ToString("X") + " try=" + (retry + 1) + " " + FormatData(chunk, chunk.Length));
            }
            token.ThrowIfCancellationRequested();
            SendFrame(adapter, 0x12, chunk);
            try
            {
                return WaitDataAck(adapter, fastMode: false, token);
            }
            catch (TimeoutException ex)
            {
                lastError = ex;
                WriteDiagnostic("等待 0x017 超时，重发当前数据帧。");
                SleepCancelable(20, token);
            }
        }

        throw lastError ?? new TimeoutException("等待数据应答超时。");
    }

    private static byte WaitDataAck(ICanAdapter adapter, bool fastMode, CancellationToken token)
    {
        CanFrame? ack = WaitForFrame(adapter, AckTimeoutMs, f => f.Id == 0x17 && f.Dlc >= 1 && f.Data.Length >= 1, token);
        if (ack == null)
        {
            throw new TimeoutException("等待数据应答超时。");
        }

        byte ackCode = ack.Value.Data[0];
        bool valid = fastMode
            ? ackCode == 0x00 || ackCode == 0x03
            : ackCode == 0x00 || ackCode == 0x02 || ackCode == 0x03;
        if (!valid)
        {
            throw new InvalidOperationException("数据应答异常：0x" + ackCode.ToString("X2"));
        }

        return ackCode;
    }

    private static void ReportProgress(IProgress<string>? progress, int bytesSent, int totalBytes, ref int lastPercent)
    {
        if (totalBytes <= 0)
        {
            return;
        }

        int percent = Math.Clamp((int)Math.Round(bytesSent * 100.0 / totalBytes), 0, 100);
        if (percent == lastPercent)
        {
            return;
        }

        lastPercent = percent;
        progress?.Report("下载进度：" + percent + "%");
    }

    private static CanFrame? WaitForFrame(ICanAdapter adapter, int timeoutMs, Func<CanFrame, bool> match, CancellationToken token)
    {
        long startTicks = Stopwatch.GetTimestamp();
        long timeoutTicks = timeoutMs <= 0 ? 0 : timeoutMs * Stopwatch.Frequency / 1000;
        int idleLoops = 0;
        while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() - startTicks < timeoutTicks)
        {
            if (TryTakePendingFrame(match, out CanFrame pending))
            {
                return pending;
            }

            int count = 0;
            bool receivedAny = false;
            while (count++ < MaxFramesPerWaitSweep && adapter.TryReceive(out CanFrame frame))
            {
                receivedAny = true;
                if (IsDiagnosticFrame(frame))
                {
                    WriteRxDiagnostic(frame);
                }

                if (match(frame))
                {
                    return frame;
                }

                SavePendingFrame(frame);
            }

            if (receivedAny)
            {
                idleLoops = 0;
                continue;
            }

            WaitForFrameIdle(startTicks, ref idleLoops);
        }

        token.ThrowIfCancellationRequested();
        return null;
    }

    private static void WaitForFrameIdle(long startTicks, ref int idleLoops)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < FastAckPollMs)
        {
            if (idleLoops++ < FastAckSpinLoops)
            {
                Thread.SpinWait(80);
            }
            else
            {
                Thread.Sleep(0);
            }
            return;
        }

        Thread.Sleep(1);
    }

    private static void SleepCancelable(int milliseconds, CancellationToken token)
    {
        if (milliseconds <= 0)
        {
            token.ThrowIfCancellationRequested();
            return;
        }

        if (token.WaitHandle.WaitOne(milliseconds))
        {
            throw new OperationCanceledException(token);
        }
    }

    private static bool TryTakePendingFrame(Func<CanFrame, bool> match, out CanFrame frame)
    {
        lock (PendingLock)
        {
            int count = PendingFrames.Count;
            while (count-- > 0)
            {
                CanFrame current = PendingFrames.Dequeue();
                if (match(current))
                {
                    frame = current;
                    return true;
                }

                PendingFrames.Enqueue(current);
            }
        }

        frame = default;
        return false;
    }

    private static void SavePendingFrame(CanFrame frame)
    {
        if (frame.Id == 0x16)
        {
            return;
        }

        if (!IsProtocolFrame(frame))
        {
            return;
        }

        lock (PendingLock)
        {
            if (PendingFrames.Count >= 64)
            {
                PendingFrames.Dequeue();
            }

            PendingFrames.Enqueue(frame);
        }
    }

    private static void ClearPendingFrames()
    {
        lock (PendingLock)
        {
            PendingFrames.Clear();
        }
    }

    private static bool IsProtocolFrame(CanFrame frame)
    {
        return frame.Id == MonitorResponseId ||
            frame.Id == 0x702 ||
            frame.Id == 0x703 ||
            frame.Id == 0x15 ||
            frame.Id == 0x17;
    }

    private static bool IsDiagnosticFrame(CanFrame frame)
    {
        return frame.Id == MonitorResponseId ||
            frame.Id == 0x702 ||
            frame.Id == 0x703 ||
            frame.Id == 0x15 ||
            frame.Id == 0x16;
    }

    private static string FormatFrameData(CanFrame frame)
    {
        int len = Math.Min(frame.Dlc, (byte)Math.Min(frame.Data.Length, 8));
        if (len <= 0)
        {
            return "";
        }

        return string.Join(" ", frame.Data.Take(len).Select(b => b.ToString("X2")));
    }

    private static void WriteRxDiagnostic(CanFrame frame)
    {
        string text = "RX 0x" + frame.Id.ToString("X3") + " " + FormatFrameData(frame);
        DateTime now = DateTime.UtcNow;

        lock (RxDiagnosticLock)
        {
            if (!string.Equals(text, _lastRxDiagnosticText, StringComparison.Ordinal))
            {
                if (_suppressedRxDiagnosticCount > 0)
                {
                    WriteDiagnostic(_lastRxDiagnosticText + " repeated " + _suppressedRxDiagnosticCount + " more times.");
                }

                _lastRxDiagnosticText = text;
                _lastRxDiagnosticWriteUtc = now;
                _suppressedRxDiagnosticCount = 0;
                WriteDiagnostic(text);
                return;
            }

            if ((now - _lastRxDiagnosticWriteUtc).TotalMilliseconds >= 750)
            {
                int total = _suppressedRxDiagnosticCount + 1;
                _lastRxDiagnosticWriteUtc = now;
                _suppressedRxDiagnosticCount = 0;
                WriteDiagnostic(text + " repeated " + total + " times.");
                return;
            }

            _suppressedRxDiagnosticCount++;
        }
    }

    private static void Drain(ICanAdapter adapter, int milliseconds, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        int idleLoops = 0;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            int count = 0;
            bool receivedAny = false;
            while (count++ < MaxFramesPerDrainSweep && DateTime.UtcNow < deadline && adapter.TryReceive(out _))
            {
                receivedAny = true;
            }

            if (receivedAny)
            {
                idleLoops = 0;
                continue;
            }

            if (idleLoops++ < FastAckSpinLoops)
            {
                Thread.SpinWait(80);
            }
            else
            {
                Thread.Sleep(0);
            }
        }
    }

    private static void SendFrame(ICanAdapter adapter, uint id, byte[] data)
    {
        if (id == 0x10 || id == 0x11 || (LogDataFrames && id == 0x12))
        {
            WriteDiagnostic("TX 0x" + id.ToString("X3") + " " + FormatData(data, data.Length));
        }

        adapter.Send(new CanFrame(id, (byte)data.Length, data));
    }

    private static string FormatData(byte[] data, int length)
    {
        int len = Math.Min(length, Math.Min(data.Length, 8));
        if (len <= 0)
        {
            return "";
        }

        return string.Join(" ", data.Take(len).Select(b => b.ToString("X2")));
    }

    private static byte[] TrimFirmwareForBoot(byte[] firmwareFile, out int skippedBytes)
    {
        skippedBytes = 0;
        if (firmwareFile.Length <= DefaultStartAddress)
        {
            return firmwareFile;
        }

        for (int i = 0; i < DefaultStartAddress; i++)
        {
            if (firmwareFile[i] != 0x00 && firmwareFile[i] != 0xFF)
            {
                return firmwareFile;
            }
        }

        skippedBytes = (int)DefaultStartAddress;
        byte[] trimmed = new byte[firmwareFile.Length - skippedBytes];
        Buffer.BlockCopy(firmwareFile, skippedBytes, trimmed, 0, trimmed.Length);
        return trimmed;
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "diagnostic.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);

            FileInfo info = new FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024)
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                File.WriteAllLines(path, lines.Skip(Math.Max(0, lines.Length - 2000)), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private sealed class TimerResolutionScope : IDisposable
    {
        private readonly bool _active;

        private TimerResolutionScope(bool active)
        {
            _active = active;
        }

        public static TimerResolutionScope Begin()
        {
            try
            {
                return new TimerResolutionScope(timeBeginPeriod(1) == 0);
            }
            catch
            {
                return new TimerResolutionScope(false);
            }
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            try
            {
                timeEndPeriod(1);
            }
            catch
            {
            }
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
