namespace CanVariableMonitor;

internal static class MonitorProtocol
{
    public const byte ReadCommand = 0xA5;
    public const byte ReadAck = 0x5A;
    public const byte OpenCommand = 0xA0;
    public const byte CloseCommand = 0xA1;
    public const byte RebootCommand = 0xA2;
    public const byte RebootAck = 0xA3;
    public const byte WriteByteCommand = 0xA6;
    public const byte WriteWordCommand = 0xA7;
    public const byte ForceByteCommand = 0xA8;
    public const byte ForceWordCommand = 0xA9;
    public const byte ReleaseForceCommand = 0xAA;
    public const byte WriteAck = 0x6A;
    public const byte RuntimeControlCommand = 0xAC;
    public const byte RuntimeControlAck = 0xCA;
    public const byte TraceCommand = 0xB0;
    public const byte TraceAck = 0xB1;

    private static readonly byte[] SessionMagic = { (byte)'K', (byte)'X', (byte)'M', (byte)'O', (byte)'N' };
    private static readonly byte[] RuntimeMagic = { (byte)'K', (byte)'X', (byte)'R', (byte)'T' };

    public const byte RuntimeRun = 0;
    public const byte RuntimePause = 1;
    public const byte RuntimeStep = 2;

    public static byte[] BuildOpenRequest(byte seq) => BuildSessionRequest(OpenCommand, seq);

    public static byte[] BuildCloseRequest(byte seq) => BuildSessionRequest(CloseCommand, seq);

    public static byte[] BuildRebootRequest(byte seq) => BuildSessionRequest(RebootCommand, seq);

    public static byte[] BuildTraceRequest(byte seq) => BuildSessionRequest(TraceCommand, seq);

    public static byte[] BuildReadRequest(byte seq, uint address, int len)
    {
        byte[] data =
        {
            ReadCommand,
            seq,
            (byte)(address & 0xFF),
            (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF),
            (byte)((address >> 24) & 0xFF),
            (byte)len,
            0
        };
        data[7] = Checksum(data, 7);
        return data;
    }

    public static byte[] BuildWriteRequest(byte seq, uint address, int len, ushort value, bool force)
    {
        byte command = (len == 1)
            ? (force ? ForceByteCommand : WriteByteCommand)
            : (force ? ForceWordCommand : WriteWordCommand);
        return new byte[]
        {
            command,
            seq,
            (byte)(address & 0xFF),
            (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF),
            (byte)((address >> 24) & 0xFF),
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF)
        };
    }

    public static byte[] BuildReleaseForceRequest(byte seq, uint address)
    {
        return new byte[]
        {
            ReleaseForceCommand,
            seq,
            (byte)(address & 0xFF),
            (byte)((address >> 8) & 0xFF),
            (byte)((address >> 16) & 0xFF),
            (byte)((address >> 24) & 0xFF),
            0,
            0
        };
    }

    public static byte[] BuildRuntimeControlRequest(byte seq, byte mode)
    {
        byte[] data =
        {
            RuntimeControlCommand,
            seq,
            RuntimeMagic[0],
            RuntimeMagic[1],
            RuntimeMagic[2],
            RuntimeMagic[3],
            mode,
            0
        };
        data[7] = Checksum(data, 7);
        return data;
    }

    public static bool TryParseReadAck(CanFrame frame, byte seq, out int len, out byte status, out ushort value)
    {
        len = 0;
        status = 0xFF;
        value = 0;

        if (frame.Dlc < 8 || frame.Data.Length < 8)
        {
            return false;
        }

        byte[] d = frame.Data;
        if (d[0] != ReadAck || d[1] != seq || d[7] != Checksum(d, 7))
        {
            return false;
        }

        len = d[2];
        status = d[3];
        value = (ushort)(d[4] | (d[5] << 8));
        return true;
    }

    public static bool TryParseTraceAck(CanFrame frame, byte seq, out ushort traceId)
    {
        traceId = 0;

        if (frame.Dlc < 8 || frame.Data.Length < 8)
        {
            return false;
        }

        byte[] d = frame.Data;
        if (d[0] != TraceAck || d[1] != seq || d[7] != Checksum(d, 7))
        {
            return false;
        }

        traceId = (ushort)(d[2] | (d[3] << 8));
        return true;
    }

    public static bool TryParseWriteAck(CanFrame frame, byte seq, out byte status, out int len, out byte command)
    {
        status = 0xFF;
        len = 0;
        command = 0;

        if (frame.Dlc < 8 || frame.Data.Length < 8)
        {
            return false;
        }

        byte[] d = frame.Data;
        if (d[0] != WriteAck || d[1] != seq || d[7] != Checksum(d, 7))
        {
            return false;
        }

        status = d[2];
        len = d[3];
        command = d[4];
        return true;
    }

    public static bool TryParseRuntimeControlAck(CanFrame frame, byte seq, out byte status, out byte mode)
    {
        status = 0xFF;
        mode = 0;

        if (frame.Dlc < 8 || frame.Data.Length < 8)
        {
            return false;
        }

        byte[] d = frame.Data;
        if (d[0] != RuntimeControlAck || d[1] != seq || d[7] != Checksum(d, 7))
        {
            return false;
        }

        status = d[2];
        mode = d[3];
        return true;
    }

    public static bool TryParseRebootAck(CanFrame frame, byte seq)
    {
        if (frame.Dlc < 8 || frame.Data.Length < 8)
        {
            return false;
        }

        byte[] d = frame.Data;
        return d[0] == RebootAck && d[1] == seq && d[7] == Checksum(d, 7);
    }

    private static byte[] BuildSessionRequest(byte command, byte seq)
    {
        byte[] data =
        {
            command,
            seq,
            SessionMagic[0],
            SessionMagic[1],
            SessionMagic[2],
            SessionMagic[3],
            SessionMagic[4],
            0
        };
        data[7] = Checksum(data, 7);
        return data;
    }

    public static byte Checksum(byte[] data, int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += data[i];
        }

        return (byte)sum;
    }
}
