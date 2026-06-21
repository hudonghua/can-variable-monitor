using System.Buffers.Binary;
using System.Net.Sockets;

namespace McgsModbusTool;

internal sealed class ModbusTcpClient : IDisposable
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task ConnectAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        Disconnect();

        var client = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);

        _tcpClient = client;
        _stream = client.GetStream();
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _tcpClient?.Close();
        _stream = null;
        _tcpClient = null;
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity, int timeoutMs, CancellationToken cancellationToken)
    {
        return ReadRegistersAsync(unitId, 0x03, startAddress, quantity, timeoutMs, cancellationToken);
    }

    public Task<ushort[]> ReadInputRegistersAsync(byte unitId, ushort startAddress, ushort quantity, int timeoutMs, CancellationToken cancellationToken)
    {
        return ReadRegistersAsync(unitId, 0x04, startAddress, quantity, timeoutMs, cancellationToken);
    }

    public async Task WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, int timeoutMs, CancellationToken cancellationToken)
    {
        var pdu = new byte[5];
        pdu[0] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), value);

        var response = await SendRequestAsync(unitId, pdu, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (response.Length != 5 || response[0] != 0x06)
        {
            throw new InvalidDataException("写单寄存器返回长度或功能码不正确。");
        }
    }

    public async Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, IReadOnlyList<ushort> values, int timeoutMs, CancellationToken cancellationToken)
    {
        if (values.Count is < 1 or > 123)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "连续写数量必须在 1 到 123 之间。");
        }

        var byteCount = values.Count * 2;
        var pdu = new byte[6 + byteCount];
        pdu[0] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)values.Count);
        pdu[5] = (byte)byteCount;

        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2, 2), values[i]);
        }

        var response = await SendRequestAsync(unitId, pdu, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (response.Length != 5 || response[0] != 0x10)
        {
            throw new InvalidDataException("连续写寄存器返回长度或功能码不正确。");
        }
    }

    private async Task<ushort[]> ReadRegistersAsync(byte unitId, byte functionCode, ushort startAddress, ushort quantity, int timeoutMs, CancellationToken cancellationToken)
    {
        if (quantity is < 1 or > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "读取数量必须在 1 到 125 之间。");
        }

        var pdu = new byte[5];
        pdu[0] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), quantity);

        var response = await SendRequestAsync(unitId, pdu, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[0] != functionCode)
        {
            throw new InvalidDataException("读取返回长度或功能码不正确。");
        }

        var byteCount = response[1];
        if (byteCount != quantity * 2 || response.Length != 2 + byteCount)
        {
            throw new InvalidDataException("读取返回的数据长度不正确。");
        }

        var registers = new ushort[quantity];
        for (var i = 0; i < quantity; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2, 2));
        }

        return registers;
    }

    private async Task<byte[]> SendRequestAsync(byte unitId, byte[] pdu, int timeoutMs, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is null || _tcpClient is null)
            {
                throw new InvalidOperationException("尚未连接到屏。");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var transactionId = unchecked(++_transactionId);
            var request = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)(1 + pdu.Length));
            request[6] = unitId;
            Buffer.BlockCopy(pdu, 0, request, 7, pdu.Length);

            await _stream.WriteAsync(request, timeoutCts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            var header = await ReadExactAsync(_stream, 7, timeoutCts.Token).ConfigureAwait(false);
            var responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var responseUnitId = header[6];

            if (responseTransactionId != transactionId)
            {
                throw new InvalidDataException($"事务号不匹配：发送 {transactionId}，收到 {responseTransactionId}。");
            }

            if (protocolId != 0)
            {
                throw new InvalidDataException($"协议号不是 Modbus TCP：{protocolId}。");
            }

            if (responseUnitId != unitId)
            {
                throw new InvalidDataException($"站号不匹配：发送 {unitId}，收到 {responseUnitId}。");
            }

            if (length < 2)
            {
                throw new InvalidDataException("返回长度不正确。");
            }

            var responsePdu = await ReadExactAsync(_stream, length - 1, timeoutCts.Token).ConfigureAwait(false);
            if (responsePdu.Length >= 2 && (responsePdu[0] & 0x80) != 0)
            {
                throw new ModbusException(responsePdu[0], responsePdu[1]);
            }

            return responsePdu;
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("连接已被对方关闭。");
            }

            offset += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        Disconnect();
        _requestLock.Dispose();
    }
}

internal sealed class ModbusException(byte functionCode, byte exceptionCode) : Exception($"Modbus 异常：功能码 0x{functionCode:X2}，异常码 0x{exceptionCode:X2}（{Describe(exceptionCode)}）")
{
    private static string Describe(byte exceptionCode)
    {
        return exceptionCode switch
        {
            0x01 => "非法功能码",
            0x02 => "非法数据地址，通常是屏里没配置这个寄存器",
            0x03 => "非法数据值，通常是数量或数值范围不允许",
            0x04 => "从站设备故障",
            0x06 => "从站忙",
            _ => "未知异常"
        };
    }
}
