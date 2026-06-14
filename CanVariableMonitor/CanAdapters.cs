using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace CanVariableMonitor;

internal interface ICanAdapter : IDisposable
{
    string Name { get; }
    void Open();
    void Close();
    void Send(CanFrame frame);
    bool TryReceive(out CanFrame frame);
}

internal static class CanAdapterFactory
{
    public static ICanAdapter Create(string name)
    {
        return name switch
        {
            "PEAK" => new PeakCanAdapter(),
            "PEAK PCAN-USB" => new PeakCanAdapter(),
            "广成GC" => new GcCanAdapter(),
            "SYS" => new SysCanAdapterDirect(),
            _ => new MockCanAdapter(),
        };
    }

    public static bool TryOpenAvailable(out ICanAdapter? adapter, out string message, string? deprioritizedName = null, string? preferredName = null)
    {
        var errors = new List<string>();
        IEnumerable<ICanAdapter> candidates = new ICanAdapter[] { new PeakCanAdapter(), new SysCanAdapterDirect(), new GcCanAdapter() };
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            candidates = candidates.OrderBy(candidate => candidate.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }
        if (!string.IsNullOrWhiteSpace(deprioritizedName))
        {
            candidates = candidates.OrderBy(candidate => candidate.Name.Equals(deprioritizedName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(candidate => !string.IsNullOrWhiteSpace(preferredName) && candidate.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        foreach (ICanAdapter current in candidates)
        {
            try
            {
                current.Open();
                CanAdapterDiagnostics.Write("CAN candidate opened: " + current.Name);
                adapter = current;
                message = current.Name;
                return true;
            }
            catch (Exception ex)
            {
                errors.Add(current.Name + "：" + ex.Message);
                CanAdapterDiagnostics.Write("CAN candidate failed: " + current.Name + ", " + ex.Message);
                current.Dispose();
            }
        }

        adapter = null;
        message = errors.Count == 0 ? "未检测到 CAN 工具" : string.Join("；", errors);
        return false;
    }
}

internal static class CanAdapterDiagnostics
{
    public static void Write(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "diagnostic.log");
            File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}

internal sealed class MockCanAdapter : ICanAdapter
{
    private readonly ConcurrentQueue<CanFrame> _rx = new();
    private readonly Random _random = new();

    public string Name => "Mock";

    public void Open() { }
    public void Close() { }
    public void Dispose() { }

    public void Send(CanFrame frame)
    {
        if (frame.Data.Length >= 8 && frame.Data[0] == MonitorProtocol.ReadCommand)
        {
            byte seq = frame.Data[1];
            int len = frame.Data[6];
            ushort value = (ushort)_random.Next(0, len == 1 ? 255 : 30000);
            byte[] data =
            {
                MonitorProtocol.ReadAck,
                seq,
                (byte)len,
                0,
                (byte)(value & 0xFF),
                (byte)(value >> 8),
                0,
                0
            };
            data[7] = MonitorProtocol.Checksum(data, 7);
            _rx.Enqueue(new CanFrame(0x7F1, 8, data));
        }
        else if (frame.Data.Length >= 8 && frame.Data[0] == MonitorProtocol.TraceCommand)
        {
            byte seq = frame.Data[1];
            ushort traceId = 0x2001;
            byte[] data =
            {
                MonitorProtocol.TraceAck,
                seq,
                (byte)(traceId & 0xFF),
                (byte)(traceId >> 8),
                0,
                0,
                0,
                0
            };
            data[7] = MonitorProtocol.Checksum(data, 7);
            _rx.Enqueue(new CanFrame(0x7F1, 8, data));
        }
    }

    public bool TryReceive(out CanFrame frame) => _rx.TryDequeue(out frame);
}

internal sealed class SysCanAdapterDirect : ICanAdapter
{
    private readonly object _sync = new();
    private readonly ConcurrentQueue<CanFrame> _rx = new();
    private Type? _serverType;
    private object? _device;
    private Type? _msgType;
    private MethodInfo? _createMsgMethod;
    private MethodInfo? _readCanMsgMethod;
    private MethodInfo? _writeCanMsgMethod;
    private FieldInfo? _idField;
    private FieldInfo? _dlcField;
    private FieldInfo? _dataField;
    private Array? _receiveMsgArray;
    private byte _ch0;
    private byte _anyChannel;
    private bool _opened;
    private int _txProbeRemaining = 4;

    public string Name => "SYS";

    public void Open()
    {
        lock (_sync)
        {
            if (_opened)
            {
                return;
            }

            string appDir = AppContext.BaseDirectory;
            NativeMethods.SetDllDirectory(appDir);
            string ucanPath = Path.Combine(appDir, "UcanDotNET.dll");
            if (!File.Exists(ucanPath))
            {
                throw new FileNotFoundException("UcanDotNET.dll not found.", ucanPath);
            }

            string usbCanPath = Path.Combine(appDir, "usbcan32.dll");
            if (!File.Exists(usbCanPath))
            {
                throw new FileNotFoundException("usbcan32.dll not found.", usbCanPath);
            }

            try
            {
                Assembly asm = Assembly.LoadFrom(ucanPath);
                _serverType = asm.GetType("UcanDotNET.USBcanServer", true)!;
                _device = Activator.CreateInstance(_serverType);
                _msgType = asm.GetType("UcanDotNET.USBcanServer+tCanMsgStruct", true)!;
                _createMsgMethod = _msgType.GetMethod("CreateInstance")!;
                _readCanMsgMethod = _serverType.GetMethod("ReadCanMsg")!;
                _writeCanMsgMethod = _serverType.GetMethod("WriteCanMsg")!;
                _idField = _msgType.GetField("m_dwID")!;
                _dlcField = _msgType.GetField("m_bDLC")!;
                _dataField = _msgType.GetField("m_bData")!;
                _receiveMsgArray = CreateMessageArray(64);

                Type channelType = asm.GetType("UcanDotNET.USBcanServer+eUcanChannel", true)!;
                Type baudType = asm.GetType("UcanDotNET.USBcanServer+eUcanBaudrate", true)!;
                Type baudExType = asm.GetType("UcanDotNET.USBcanServer+eUcanBaudrateEx", true)!;
                Type modeType = asm.GetType("UcanDotNET.USBcanServer+tUcanMode", true)!;
                Type resetType = asm.GetType("UcanDotNET.USBcanServer+eUcanResetFlags", true)!;

                byte anyModule = Convert.ToByte(_serverType.GetField("USBCAN_ANY_MODULE")!.GetValue(null));
                _ch0 = Convert.ToByte(Enum.Parse(channelType, "USBCAN_CHANNEL_CH0"));
                _anyChannel = Convert.ToByte(Enum.Parse(channelType, "USBCAN_CHANNEL_ANY"));
                short baud250 = Convert.ToInt16(Enum.Parse(baudType, "USBCAN_BAUD_250kBit"));
                int baudEx = Convert.ToInt32(Enum.Parse(baudExType, "USBCAN_BAUDEX_USE_BTR01"));
                byte normalMode = Convert.ToByte(Enum.Parse(modeType, "kUcanModeNormal"));
                int resetFlags = Convert.ToInt32(Enum.Parse(resetType, "USBCAN_RESET_ONLY_ALL_BUFF"));
                int amrAll = Convert.ToInt32(_serverType.GetField("USBCAN_AMR_ALL")!.GetValue(null));
                int acrAll = Convert.ToInt32(_serverType.GetField("USBCAN_ACR_ALL")!.GetValue(null));

                byte initHardwareResult = Convert.ToByte(_serverType.GetMethod("InitHardware")!.Invoke(_device, new object[] { anyModule }));
                if (initHardwareResult != 0)
                {
                    throw new InvalidOperationException("SYS InitHardware failed: " + initHardwareResult);
                }

                byte initCanResult = Convert.ToByte(_serverType.GetMethod("InitCan")!.Invoke(_device, new object[] { _ch0, baud250, baudEx, amrAll, acrAll, normalMode, (byte)0x1A }));
                if (initCanResult != 0)
                {
                    throw new InvalidOperationException("SYS InitCan failed: " + initCanResult);
                }

                try
                {
                    _serverType.GetMethod("SetTxTimeout")?.Invoke(_device, new object[] { _ch0, 50 });
                }
                catch
                {
                }

                _serverType.GetMethod("ResetCan")!.Invoke(_device, new object[] { _ch0, resetFlags });
                _opened = true;
                DrainReceiveQueueCore();
                CanAdapterDiagnostics.Write("SYS direct opened");
            }
            catch
            {
                CloseCore();
                throw;
            }
        }
    }

    public void Send(CanFrame frame)
    {
        lock (_sync)
        {
            EnsureOpen();
            Array msgArray = Array.CreateInstance(_msgType!, 1);
            object msg = _createMsgMethod!.Invoke(null, new object[] { (int)frame.Id, (byte)0 })!;
            _dlcField!.SetValue(msg, frame.Dlc);
            byte[] payload = (byte[])_dataField!.GetValue(msg)!;
            Buffer.BlockCopy(frame.Data, 0, payload, 0, Math.Min(frame.Data.Length, 8));
            msgArray.SetValue(msg, 0);

            object[] args = { _ch0, msgArray, 0 };
            object ret = _writeCanMsgMethod!.Invoke(_device, args)!;
            byte retCode = Convert.ToByte(ret);
            int sentCount = Convert.ToInt32(args[2]);
            if (_txProbeRemaining > 0)
            {
                _txProbeRemaining--;
                CanAdapterDiagnostics.Write($"SYS direct TX id=0x{frame.Id:X3} dlc={frame.Dlc} ret={retCode} count={sentCount}");
            }
            if (retCode != 0 || sentCount <= 0)
            {
                throw new InvalidOperationException("SYS CAN send failed.");
            }
        }
    }

    public bool TryReceive(out CanFrame frame)
    {
        lock (_sync)
        {
            EnsureOpen();
            if (_rx.TryDequeue(out frame))
            {
                return true;
            }

            Array msgArray = _receiveMsgArray ??= CreateMessageArray(64);

            object[] args = { _anyChannel, msgArray, 0 };
            object retObj = _readCanMsgMethod!.Invoke(_device, args)!;
            int count = Convert.ToInt32(args[2]);
            if (Convert.ToByte(retObj) == 0 && count > 0)
            {
                int frameCount = Math.Min(count, msgArray.Length);
                for (int i = 0; i < frameCount; i++)
                {
                    object msg = msgArray.GetValue(i)!;
                    uint id = Convert.ToUInt32(_idField!.GetValue(msg));
                    byte dlc = Convert.ToByte(_dlcField!.GetValue(msg));
                    byte[] sourceData = (byte[])_dataField!.GetValue(msg)!;
                    byte[] data = new byte[8];
                    Buffer.BlockCopy(sourceData, 0, data, 0, Math.Min(sourceData.Length, 8));
                    _rx.Enqueue(new CanFrame(id, dlc, data));
                }

                return _rx.TryDequeue(out frame);
            }

            frame = default;
            return false;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            CloseCore();
        }
    }

    public void Dispose() => Close();

    private void DrainReceiveQueueCore()
    {
        while (_rx.TryDequeue(out _))
        {
        }

        for (int i = 0; i < 16; i++)
        {
            Array msgArray = _receiveMsgArray ??= CreateMessageArray(64);

            object[] args = { _anyChannel, msgArray, 0 };
            object retObj = _readCanMsgMethod!.Invoke(_device, args)!;
            int count = Convert.ToInt32(args[2]);
            if (Convert.ToByte(retObj) != 0 || count <= 0)
            {
                break;
            }
        }
    }

    private void CloseCore()
    {
        if (_serverType != null && _device != null)
        {
            try
            {
                ResetSysCanQuietly();
            }
            catch
            {
            }

            try
            {
                MethodInfo? shutdown = _serverType.GetMethod("Shutdown", new[] { typeof(byte), typeof(bool) });
                if (shutdown != null)
                {
                    shutdown.Invoke(_device, new object[] { (byte)0, true });
                }
                else
                {
                    _serverType.GetMethod("Shutdown")?.Invoke(_device, null);
                }
            }
            catch
            {
            }

            try
            {
                if (_device is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else
                {
                    _serverType.GetMethod("Dispose")?.Invoke(_device, Array.Empty<object>());
                }
            }
            catch
            {
            }

            Thread.Sleep(300);
        }

        _opened = false;
        _device = null;
        _serverType = null;
        _msgType = null;
        _createMsgMethod = null;
        _readCanMsgMethod = null;
        _writeCanMsgMethod = null;
        _idField = null;
        _dlcField = null;
        _dataField = null;
        _receiveMsgArray = null;
        while (_rx.TryDequeue(out _))
        {
        }
    }

    private void ResetSysCanQuietly()
    {
        if (_serverType == null || _device == null)
        {
            return;
        }

        Type resetType = _serverType.Assembly.GetType("UcanDotNET.USBcanServer+eUcanResetFlags", true)!;
        int resetFlags = Convert.ToInt32(Enum.Parse(resetType, "USBCAN_RESET_ONLY_ALL_BUFF"));
        _serverType.GetMethod("ResetCan")!.Invoke(_device, new object[] { _ch0, resetFlags });
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("CAN adapter is not connected.");
        }
    }

    private Array CreateMessageArray(int length)
    {
        Array msgArray = Array.CreateInstance(_msgType!, length);
        for (int i = 0; i < msgArray.Length; i++)
        {
            object msg = _createMsgMethod!.Invoke(null, new object[] { 0, (byte)0 })!;
            msgArray.SetValue(msg, i);
        }

        return msgArray;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string lpPathName);
    }
}

internal sealed class GcCanAdapter : ICanAdapter
{
    private const int ReceiveBatchSize = 128;
    private static readonly uint[] DeviceTypes = { 4, 3, 21, 20 };
    private readonly ConcurrentQueue<CanFrame> _rx = new();
    private readonly NativeMethods.VCI_CAN_OBJ[] _receiveBuffer = CreateReceiveBuffer(ReceiveBatchSize);
    private readonly NativeMethods.VCI_CAN_OBJ[] _sendBuffer = CreateReceiveBuffer(1);
    private uint _deviceType;
    private readonly uint _deviceIndex = 0;
    private readonly uint _channel = 0;
    private bool _opened;
    private int _txProbeRemaining = 4;

    public string Name => "广成GC";

    public void Open()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "ECanVci.dll")))
        {
            throw new FileNotFoundException("缺少 ECanVci.dll");
        }

        NativeMethods.SetDllDirectory(AppContext.BaseDirectory);
        var config = new NativeMethods.VCI_INIT_CONFIG
        {
            AccCode = 0,
            AccMask = 0xFFFFFFFF,
            Reserved = 0,
            Filter = 1,
            Timing0 = 0x01,
            Timing1 = 0x1C,
            Mode = 0
        };

        foreach (uint deviceType in DeviceTypes)
        {
            DefensiveCloseDevice(deviceType, _deviceIndex);
            if (NativeMethods.OpenDevice(deviceType, _deviceIndex, 0) != 1)
            {
                continue;
            }

            if (NativeMethods.InitCAN(deviceType, _deviceIndex, _channel, ref config) != 1)
            {
                ReleaseOpenedDevice(deviceType, _deviceIndex, _channel);
                continue;
            }

            if (NativeMethods.StartCAN(deviceType, _deviceIndex, _channel) != 1)
            {
                ReleaseOpenedDevice(deviceType, _deviceIndex, _channel);
                continue;
            }

            if (NativeMethods.ClearBuffer(deviceType, _deviceIndex, _channel) == 1)
            {
                NativeMethods.ClearBuffer(deviceType, _deviceIndex, _channel);
                ClearLocalReceiveQueue();
                _deviceType = deviceType;
                _opened = true;
                CanAdapterDiagnostics.Write($"GC opened type={deviceType}");
                return;
            }

            ReleaseOpenedDevice(deviceType, _deviceIndex, _channel);
        }

        throw new InvalidOperationException("未找到广成 CAN 适配器");
    }

    public void Send(CanFrame frame)
    {
        EnsureOpen();
        NativeMethods.VCI_CAN_OBJ obj = _sendBuffer[0];
        obj.ID = frame.Id;
        obj.SendType = 0;
        obj.RemoteFlag = 0;
        obj.ExternFlag = 0;
        obj.DataLen = frame.Dlc;
        Array.Clear(obj.Data, 0, obj.Data.Length);
        Buffer.BlockCopy(frame.Data, 0, obj.Data, 0, Math.Min(frame.Data.Length, 8));
        _sendBuffer[0] = obj;
        uint sent = NativeMethods.Transmit(_deviceType, _deviceIndex, _channel, _sendBuffer, 1);
        if (_txProbeRemaining > 0)
        {
            _txProbeRemaining--;
            CanAdapterDiagnostics.Write($"GC TX id=0x{frame.Id:X3} dlc={frame.Dlc} ret={sent}");
        }
        if (sent != 1)
        {
            throw new InvalidOperationException("CAN 发送失败");
        }
    }

    public bool TryReceive(out CanFrame frame)
    {
        EnsureOpen();
        if (_rx.TryDequeue(out frame))
        {
            return true;
        }

        uint count = NativeMethods.Receive(_deviceType, _deviceIndex, _channel, _receiveBuffer, (uint)_receiveBuffer.Length, 0);
        if (count > 0)
        {
            int frameCount = (int)Math.Min(count, (uint)_receiveBuffer.Length);
            frame = CopyReceivedFrame(_receiveBuffer[0]);
            for (int i = 1; i < frameCount; i++)
            {
                _rx.Enqueue(CopyReceivedFrame(_receiveBuffer[i]));
            }

            return true;
        }

        frame = default;
        return false;
    }

    public void Close()
    {
        if (_opened)
        {
            ReleaseOpenedDevice(_deviceType, _deviceIndex, _channel);
            _opened = false;
            ClearLocalReceiveQueue();
        }
    }

    public void Dispose() => Close();

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("CAN 适配器未连接");
        }
    }

    private static NativeMethods.VCI_CAN_OBJ[] CreateReceiveBuffer(int length)
    {
        var buffer = new NativeMethods.VCI_CAN_OBJ[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i].Data = new byte[8];
            buffer[i].Reserved = new byte[3];
        }

        return buffer;
    }

    private static CanFrame CopyReceivedFrame(NativeMethods.VCI_CAN_OBJ received)
    {
        byte dlc = Math.Min(received.DataLen, (byte)8);
        byte[] data = new byte[8];
        if (received.Data != null)
        {
            Buffer.BlockCopy(received.Data, 0, data, 0, Math.Min(received.Data.Length, 8));
        }

        return new CanFrame(received.ID, dlc, data);
    }

    private void ClearLocalReceiveQueue()
    {
        while (_rx.TryDequeue(out _))
        {
        }
    }

    private static void DefensiveCloseDevice(uint deviceType, uint deviceIndex)
    {
        try
        {
            NativeMethods.CloseDevice(deviceType, deviceIndex);
        }
        catch
        {
        }

        Thread.Sleep(20);
    }

    private static void ReleaseOpenedDevice(uint deviceType, uint deviceIndex, uint channel)
    {
        try
        {
            NativeMethods.ClearBuffer(deviceType, deviceIndex, channel);
        }
        catch
        {
        }

        try
        {
            NativeMethods.ResetCAN(deviceType, deviceIndex, channel);
        }
        catch
        {
        }

        try
        {
            NativeMethods.CloseDevice(deviceType, deviceIndex);
        }
        catch
        {
        }

        Thread.Sleep(80);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string lpPathName);

        [DllImport("ECanVci.dll", EntryPoint = "OpenDevice", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

        [DllImport("ECanVci.dll", EntryPoint = "CloseDevice", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint CloseDevice(uint deviceType, uint deviceIndex);

        [DllImport("ECanVci.dll", EntryPoint = "InitCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref VCI_INIT_CONFIG initConfig);

        [DllImport("ECanVci.dll", EntryPoint = "StartCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint StartCAN(uint deviceType, uint deviceIndex, uint canIndex);

        [DllImport("ECanVci.dll", EntryPoint = "ResetCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ResetCAN(uint deviceType, uint deviceIndex, uint canIndex);

        [DllImport("ECanVci.dll", EntryPoint = "ClearBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ClearBuffer(uint deviceType, uint deviceIndex, uint canIndex);

        [DllImport("ECanVci.dll", EntryPoint = "Receive", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Receive(uint deviceType, uint deviceIndex, uint canIndex, [In, Out] VCI_CAN_OBJ[] receive, uint length, int waitTime);

        [DllImport("ECanVci.dll", EntryPoint = "Transmit", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Transmit(uint deviceType, uint deviceIndex, uint canIndex, [In] VCI_CAN_OBJ[] send, uint length);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct VCI_INIT_CONFIG
        {
            public uint AccCode;
            public uint AccMask;
            public uint Reserved;
            public byte Filter;
            public byte Timing0;
            public byte Timing1;
            public byte Mode;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct VCI_CAN_OBJ
        {
            public uint ID;
            public uint TimeStamp;
            public byte TimeFlag;
            public byte SendType;
            public byte RemoteFlag;
            public byte ExternFlag;
            public byte DataLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] Reserved;
        }
    }
}

internal sealed class SysCanAdapter : ICanAdapter
{
    private Type? _serverType;
    private object? _device;
    private Type? _msgType;
    private byte _ch0;
    private byte _anyChannel;
    private bool _opened;
    private readonly ConcurrentQueue<CanFrame> _rx = new();
    private BlockingCollection<DriverCall>? _driverCalls;
    private Thread? _driverThread;
    private int _txProbeRemaining = 4;

    public string Name => "SYS";

    private sealed class DriverCall
    {
        public DriverCall(Action action)
        {
            Action = action;
        }

        public Action Action { get; }
        public ManualResetEventSlim Done { get; } = new(false);
        public Exception? Exception { get; set; }
    }

    public void Open()
    {
        StartDriverThread();
        try
        {
            InvokeDriver(OpenCore, 3000);
        }
        catch
        {
            Close();
            throw;
        }
    }

    private void OpenCore()
    {
        string appDir = AppContext.BaseDirectory;
        GcCanAdapterNative.SetDllDirectory(appDir);
        string ucanPath = Path.Combine(appDir, "UcanDotNET.dll");
        if (!File.Exists(ucanPath))
        {
            throw new FileNotFoundException("缺少 UcanDotNET.dll", ucanPath);
        }

        string usbCanPath = Path.Combine(appDir, "usbcan32.dll");
        if (!File.Exists(usbCanPath))
        {
            throw new FileNotFoundException("缺少 usbcan32.dll", usbCanPath);
        }

        Assembly asm = Assembly.LoadFrom(ucanPath);
        _serverType = asm.GetType("UcanDotNET.USBcanServer", true)!;
        _device = Activator.CreateInstance(_serverType);
        _msgType = asm.GetType("UcanDotNET.USBcanServer+tCanMsgStruct", true)!;

        Type channelType = asm.GetType("UcanDotNET.USBcanServer+eUcanChannel", true)!;
        Type baudType = asm.GetType("UcanDotNET.USBcanServer+eUcanBaudrate", true)!;
        Type baudExType = asm.GetType("UcanDotNET.USBcanServer+eUcanBaudrateEx", true)!;
        Type modeType = asm.GetType("UcanDotNET.USBcanServer+tUcanMode", true)!;
        Type resetType = asm.GetType("UcanDotNET.USBcanServer+eUcanResetFlags", true)!;

        byte anyModule = Convert.ToByte(_serverType.GetField("USBCAN_ANY_MODULE")!.GetValue(null));
        _ch0 = Convert.ToByte(Enum.Parse(channelType, "USBCAN_CHANNEL_CH0"));
        _anyChannel = Convert.ToByte(Enum.Parse(channelType, "USBCAN_CHANNEL_ANY"));
        short baud250 = Convert.ToInt16(Enum.Parse(baudType, "USBCAN_BAUD_250kBit"));
        int baudEx = Convert.ToInt32(Enum.Parse(baudExType, "USBCAN_BAUDEX_USE_BTR01"));
        byte normalMode = Convert.ToByte(Enum.Parse(modeType, "kUcanModeNormal"));
        int resetFlags = Convert.ToInt32(Enum.Parse(resetType, "USBCAN_RESET_ONLY_ALL_BUFF"));
        int amrAll = Convert.ToInt32(_serverType.GetField("USBCAN_AMR_ALL")!.GetValue(null));
        int acrAll = Convert.ToInt32(_serverType.GetField("USBCAN_ACR_ALL")!.GetValue(null));

        byte initHardwareResult = Convert.ToByte(_serverType.GetMethod("InitHardware")!.Invoke(_device, new object[] { anyModule }));
        if (initHardwareResult != 0)
        {
            throw new InvalidOperationException("未检测到可用 SYS 适配器");
        }

        byte initCanResult = Convert.ToByte(_serverType.GetMethod("InitCan")!.Invoke(_device, new object[] { _ch0, baud250, baudEx, amrAll, acrAll, normalMode, (byte)0x1A }));
        if (initCanResult != 0)
        {
            throw new InvalidOperationException("未能初始化 SYS CAN");
        }

        try
        {
            _serverType.GetMethod("SetTxTimeout")?.Invoke(_device, new object[] { _ch0, 50 });
        }
        catch
        {
        }

        _serverType.GetMethod("ResetCan")!.Invoke(_device, new object[] { _ch0, resetFlags });
        _opened = true;
        DrainReceiveQueue();
    }

    public void Send(CanFrame frame)
    {
        InvokeDriver(() => SendCore(frame), 500);
    }

    private void SendCore(CanFrame frame)
    {
        EnsureOpen();
        Array msgArray = Array.CreateInstance(_msgType!, 1);
        object msg = _msgType!.GetMethod("CreateInstance")!.Invoke(null, new object[] { (int)frame.Id, (byte)0 })!;
        _msgType.GetField("m_bDLC")!.SetValue(msg, frame.Dlc);
        byte[] payload = (byte[])_msgType.GetField("m_bData")!.GetValue(msg)!;
        Buffer.BlockCopy(frame.Data, 0, payload, 0, Math.Min(frame.Data.Length, 8));
        msgArray.SetValue(msg, 0);
        object[] args = { _ch0, msgArray, 0 };
        object ret = _serverType!.GetMethod("WriteCanMsg")!.Invoke(_device, args)!;
        byte retCode = Convert.ToByte(ret);
        int sentCount = Convert.ToInt32(args[2]);
        if (_txProbeRemaining > 0)
        {
            _txProbeRemaining--;
            CanAdapterDiagnostics.Write($"SYS TX id=0x{frame.Id:X3} dlc={frame.Dlc} ret={retCode} count={sentCount}");
        }
        if (retCode != 0)
        {
            throw new InvalidOperationException("SYS CAN 发送失败");
        }
    }

    public bool TryReceive(out CanFrame frame)
    {
        CanFrame received = default;
        bool hasFrame = InvokeDriver(() => TryReceiveCore(out received), 500);
        frame = received;
        return hasFrame;
    }

    private bool TryReceiveCore(out CanFrame frame)
    {
        EnsureOpen();
        if (_rx.TryDequeue(out frame))
        {
            return true;
        }

        Array msgArray = Array.CreateInstance(_msgType!, 64);
        for (int i = 0; i < msgArray.Length; i++)
        {
            object msg = _msgType!.GetMethod("CreateInstance")!.Invoke(null, new object[] { 0, (byte)0 })!;
            msgArray.SetValue(msg, i);
        }

        object[] args = { _anyChannel, msgArray, 0 };
        object retObj = _serverType!.GetMethod("ReadCanMsg")!.Invoke(_device, args)!;
        int count = Convert.ToInt32(args[2]);
        if (Convert.ToByte(retObj) == 0 && count > 0)
        {
            for (int i = 0; i < count && i < msgArray.Length; i++)
            {
                object msg = msgArray.GetValue(i)!;
                uint id = Convert.ToUInt32(_msgType!.GetField("m_dwID")!.GetValue(msg));
                byte dlc = Convert.ToByte(_msgType.GetField("m_bDLC")!.GetValue(msg));
                byte[] data = (byte[])_msgType.GetField("m_bData")!.GetValue(msg)!;
                _rx.Enqueue(new CanFrame(id, dlc, data));
            }

            return _rx.TryDequeue(out frame);
        }

        frame = default;
        return false;
    }

    private void DrainReceiveQueue()
    {
        while (_rx.TryDequeue(out _))
        {
        }

        for (int i = 0; i < 16; i++)
        {
            Array msgArray = Array.CreateInstance(_msgType!, 64);
            for (int j = 0; j < msgArray.Length; j++)
            {
                object msg = _msgType!.GetMethod("CreateInstance")!.Invoke(null, new object[] { 0, (byte)0 })!;
                msgArray.SetValue(msg, j);
            }

            object[] args = { _anyChannel, msgArray, 0 };
            object retObj = _serverType!.GetMethod("ReadCanMsg")!.Invoke(_device, args)!;
            int count = Convert.ToInt32(args[2]);
            if (Convert.ToByte(retObj) != 0 || count <= 0)
            {
                break;
            }
        }
    }

    public void Close()
    {
        var queue = _driverCalls;
        var thread = _driverThread;
        if (queue == null)
        {
            CloseCore();
            return;
        }

        try
        {
            InvokeDriver(CloseCore, 1000);
        }
        catch
        {
        }

        try
        {
            queue.CompleteAdding();
        }
        catch
        {
        }

        if (thread != null && Thread.CurrentThread != thread)
        {
            thread.Join(1000);
        }

        _driverCalls = null;
        _driverThread = null;
    }

    private void CloseCore()
    {
        if (!_opened && _device == null)
        {
            return;
        }

        try
        {
            if (_serverType != null && _device != null)
            {
                _serverType.GetMethod("Shutdown")?.Invoke(_device, new object[] { _ch0, true });
            }
        }
        catch
        {
        }

        try
        {
            if (_device is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                _serverType?.GetMethod("Dispose")?.Invoke(_device, Array.Empty<object>());
            }
        }
        catch
        {
        }

        _opened = false;
        _device = null;
        _serverType = null;
        _msgType = null;
    }

    public void Dispose() => Close();

    private void StartDriverThread()
    {
        if (_driverCalls != null)
        {
            return;
        }

        var queue = new BlockingCollection<DriverCall>();
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(delegate()
        {
            ready.Set();
            foreach (DriverCall call in queue.GetConsumingEnumerable())
            {
                try
                {
                    call.Action();
                }
                catch (Exception ex)
                {
                    call.Exception = ex;
                }
                finally
                {
                    call.Done.Set();
                }
            }
        });
        thread.IsBackground = true;
        thread.Name = "SYS CAN Driver";
        thread.SetApartmentState(ApartmentState.STA);
        _driverCalls = queue;
        _driverThread = thread;
        thread.Start();
        ready.Wait(1000);
    }

    private void InvokeDriver(Action action, int timeoutMs)
    {
        if (Thread.CurrentThread == _driverThread)
        {
            action();
            return;
        }

        var queue = _driverCalls;
        if (queue == null)
        {
            throw new InvalidOperationException("SYS CAN driver is not running.");
        }

        var call = new DriverCall(action);
        queue.Add(call);
        if (!call.Done.Wait(timeoutMs))
        {
            throw new TimeoutException("SYS CAN driver call timed out.");
        }

        if (call.Exception != null)
        {
            throw new InvalidOperationException(call.Exception.Message, call.Exception);
        }
    }

    private T InvokeDriver<T>(Func<T> action, int timeoutMs)
    {
        T result = default!;
        InvokeDriver(delegate
        {
            result = action();
        }, timeoutMs);
        return result;
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("CAN 适配器未连接");
        }
    }

    private static class GcCanAdapterNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string lpPathName);
    }
}

internal sealed class PeakCanAdapter : ICanAdapter
{
    private const ushort PcanBaud250K = 0x011C;
    private const uint PcanErrorOk = 0x00000;
    private const uint PcanErrorQrcvEmpty = 0x00020;
    private const uint PcanErrorInitialize = 0x04000000;
    private const byte PcanMessageStandard = 0x00;
    private const byte PcanMessageExtended = 0x02;
    private const ushort PcanNoneBus = 0x00;
    private static readonly ushort[] UsbChannels = Enumerable.Range(0, 16).Select(i => (ushort)(0x51 + i)).ToArray();

    private readonly object _sync = new();
    private ushort _channel;
    private bool _opened;
    private int _txProbeRemaining = 4;

    public string Name => "PEAK PCAN-USB";

    public void Open()
    {
        lock (_sync)
        {
            if (_opened)
            {
                return;
            }

            var errors = new List<string>();
            PeakNative.Uninitialize(PcanNoneBus);
            foreach (ushort candidate in UsbChannels)
            {
                uint status = PeakNative.Initialize(candidate, PcanBaud250K, 0, 0, 0);
                if (status == PcanErrorInitialize)
                {
                    PeakNative.Uninitialize(candidate);
                    Thread.Sleep(20);
                    status = PeakNative.Initialize(candidate, PcanBaud250K, 0, 0, 0);
                }

                if (status == PcanErrorOk)
                {
                    _channel = candidate;
                    _opened = true;
                    PeakNative.Reset(_channel);
                    CanAdapterDiagnostics.Write($"PEAK opened channel=0x{_channel:X2} baud=250k");
                    return;
                }

                if (!IsChannelNotPresent(status))
                {
                    errors.Add($"0x{candidate:X2}=0x{status:X}");
                }
            }

            string detail = errors.Count == 0 ? "no PCAN-USB channel initialized" : string.Join(", ", errors);
            throw new InvalidOperationException("PEAK PCAN-USB open failed: " + detail);
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (!_opened)
            {
                return;
            }

            PeakNative.Uninitialize(_channel);
            PeakNative.Uninitialize(PcanNoneBus);
            _opened = false;
            _channel = 0;
        }
    }

    public void Dispose() => Close();

    public void Send(CanFrame frame)
    {
        lock (_sync)
        {
            EnsureOpen();

            byte[] payload = new byte[8];
            int length = Math.Min(Math.Min(frame.Dlc, (byte)8), frame.Data.Length);
            Buffer.BlockCopy(frame.Data, 0, payload, 0, length);

            var msg = new PeakNative.PcanMsg
            {
                Id = frame.Id,
                MsgType = frame.Id > 0x7FF ? PcanMessageExtended : PcanMessageStandard,
                Len = (byte)length,
                Data = payload
            };

            uint status = PcanErrorOk;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                status = PeakNative.Write(_channel, ref msg);
                if (status == PcanErrorOk)
                {
                    break;
                }

                Thread.SpinWait(120);
                if (attempt >= 2)
                {
                    Thread.Sleep(0);
                }
            }
            if (_txProbeRemaining > 0)
            {
                _txProbeRemaining--;
                CanAdapterDiagnostics.Write($"PEAK TX id=0x{frame.Id:X3} dlc={frame.Dlc} status=0x{status:X}");
            }

            if (status != PcanErrorOk)
            {
                throw new InvalidOperationException("PEAK CAN send failed: 0x" + status.ToString("X"));
            }
        }
    }

    public bool TryReceive(out CanFrame frame)
    {
        lock (_sync)
        {
            EnsureOpen();

            uint status = PeakNative.Read(_channel, out PeakNative.PcanMsg msg, out _);
            if (status == PcanErrorQrcvEmpty)
            {
                frame = default;
                return false;
            }

            if (status != PcanErrorOk)
            {
                frame = default;
                return false;
            }

            byte dlc = (byte)Math.Min(msg.Len, (byte)8);
            byte[] data = new byte[dlc];
            if (msg.Data != null && dlc > 0)
            {
                Buffer.BlockCopy(msg.Data, 0, data, 0, dlc);
            }

            frame = new CanFrame(msg.Id, dlc, data);
            return true;
        }
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("PEAK PCAN-USB is not connected.");
        }
    }

    private static bool IsChannelNotPresent(uint status)
    {
        const uint pcanErrorIllHandle = 0x01C00;
        const uint pcanErrorInitializeOld = 0x40000;
        const uint pcanErrorIllOperation = 0x80000;
        return status == pcanErrorIllHandle ||
            status == pcanErrorInitializeOld ||
            status == PcanErrorInitialize ||
            status == pcanErrorIllOperation;
    }

    private static class PeakNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PcanMsg
        {
            internal uint Id;
            internal byte MsgType;
            internal byte Len;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            internal byte[] Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PcanTimestamp
        {
            internal uint Millis;
            internal ushort MillisOverflow;
            internal ushort Micros;
        }

        [DllImport("PCANBasic.dll", EntryPoint = "CAN_Initialize")]
        internal static extern uint Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt);

        [DllImport("PCANBasic.dll", EntryPoint = "CAN_Uninitialize")]
        internal static extern uint Uninitialize(ushort channel);

        [DllImport("PCANBasic.dll", EntryPoint = "CAN_Reset")]
        internal static extern uint Reset(ushort channel);

        [DllImport("PCANBasic.dll", EntryPoint = "CAN_Write")]
        internal static extern uint Write(ushort channel, ref PcanMsg message);

        [DllImport("PCANBasic.dll", EntryPoint = "CAN_Read")]
        internal static extern uint Read(ushort channel, out PcanMsg message, out PcanTimestamp timestamp);
    }
}
