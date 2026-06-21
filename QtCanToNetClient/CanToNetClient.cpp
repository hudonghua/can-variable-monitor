#include "CanToNetClient.h"

#include <QDataStream>
#include <QIODevice>
#include <QtGlobal>

CanToNetClient::CanToNetClient(QObject *parent)
    : QObject(parent)
{
    _timeoutTimer.setSingleShot(true);

    connect(&_socket, &QTcpSocket::connected, this, &CanToNetClient::connected);
    connect(&_socket, &QTcpSocket::disconnected, this, &CanToNetClient::disconnected);
    connect(&_socket, &QTcpSocket::readyRead, this, &CanToNetClient::onReadyRead);
    connect(&_socket, &QTcpSocket::errorOccurred, this, &CanToNetClient::onSocketError);
    connect(&_timeoutTimer, &QTimer::timeout, this, &CanToNetClient::onRequestTimeout);
    connect(&_pollTimer, &QTimer::timeout, this, &CanToNetClient::onPollTimeout);
}

void CanToNetClient::setEndpoint(const QString &host, quint16 port)
{
    _host = host;
    _port = port;
}

void CanToNetClient::setUnitId(quint8 unitId)
{
    _unitId = unitId;
}

void CanToNetClient::setAddressOffset(int offset)
{
    _addressOffset = offset;
}

void CanToNetClient::setTimeoutMs(int timeoutMs)
{
    _timeoutMs = timeoutMs;
}

void CanToNetClient::connectToCanToNet()
{
    if (_socket.state() != QAbstractSocket::UnconnectedState) {
        _socket.abort();
    }

    _socket.connectToHost(_host, _port);
}

void CanToNetClient::disconnectFromCanToNet()
{
    stopPolling();
    _queue.clear();
    _hasCurrentRequest = false;
    _timeoutTimer.stop();
    _socket.disconnectFromHost();
}

bool CanToNetClient::isConnected() const
{
    return _socket.state() == QAbstractSocket::ConnectedState;
}

void CanToNetClient::readHoldingRegisters(int canToNetStartAddress, int count)
{
    if (count <= 0 || count > 125) {
        emit errorOccurred(QStringLiteral("Read register count must be 1..125."));
        return;
    }

    enqueue(buildReadRequest(canToNetStartAddress, count));
}

void CanToNetClient::writeHoldingRegisters(const QMap<int, quint16> &canToNetAddressValues)
{
    if (canToNetAddressValues.isEmpty()) {
        return;
    }

    auto it = canToNetAddressValues.cbegin();
    while (it != canToNetAddressValues.cend()) {
        const int startAddress = it.key();
        QVector<quint16> values;
        values.append(it.value());
        ++it;

        int nextAddress = startAddress + 1;
        while (it != canToNetAddressValues.cend() && it.key() == nextAddress && values.size() < 123) {
            values.append(it.value());
            ++it;
            ++nextAddress;
        }

        enqueue(buildWriteRequest(startAddress, values));
    }
}

void CanToNetClient::readCanFrame(quint32 canId)
{
    int startAddress = 0;
    int registerCount = 0;
    if (!canIdToAddressRange(canId, &startAddress, &registerCount)) {
        emit errorOccurred(QStringLiteral("Unknown CAN ID: 0x%1").arg(canId, 0, 16).toUpper());
        return;
    }

    enqueue(buildReadRequest(startAddress, registerCount, canId));
}

void CanToNetClient::writeCanFrame(quint32 canId, const QByteArray &canData8)
{
    int startAddress = 0;
    int registerCount = 0;
    if (!canIdToAddressRange(canId, &startAddress, &registerCount)) {
        emit errorOccurred(QStringLiteral("Unknown CAN ID: 0x%1").arg(canId, 0, 16).toUpper());
        return;
    }

    if (canData8.size() != 8) {
        emit errorOccurred(QStringLiteral("CAN DATA must be exactly 8 bytes."));
        return;
    }

    const auto registers = canDataToRegisters(startAddress, canData8);
    QVector<quint16> values;
    for (auto it = registers.cbegin(); it != registers.cend(); ++it) {
        values.append(it.value());
    }

    if (values.size() != registerCount) {
        emit errorOccurred(QStringLiteral("CAN DATA register packing failed."));
        return;
    }

    enqueue(buildWriteRequest(startAddress, values, canId));
}

void CanToNetClient::startPolling(int canToNetStartAddress, int count, int intervalMs)
{
    _pollByCanId = false;
    _pollStartAddress = canToNetStartAddress;
    _pollCount = count;
    _pollTimer.start(qMax(20, intervalMs));
}

void CanToNetClient::startPollingCanFrame(quint32 canId, int intervalMs)
{
    int startAddress = 0;
    int registerCount = 0;
    if (!canIdToAddressRange(canId, &startAddress, &registerCount)) {
        emit errorOccurred(QStringLiteral("Unknown CAN ID: 0x%1").arg(canId, 0, 16).toUpper());
        return;
    }

    _pollByCanId = true;
    _pollCanId = canId;
    _pollStartAddress = startAddress;
    _pollCount = registerCount;
    _pollTimer.start(qMax(20, intervalMs));
}

void CanToNetClient::stopPolling()
{
    _pollTimer.stop();
}

bool CanToNetClient::canIdToAddressRange(quint32 canId, int *startAddress, int *registerCount)
{
    int address = 0;
    switch (canId) {
    case 0x50:  address = 100; break;
    case 0x71:  address = 104; break;
    case 0x75:  address = 108; break;
    case 0x7A:  address = 112; break;
    case 0x150: address = 50; break;
    case 0x152: address = 54; break;
    case 0x153: address = 58; break;
    case 0x154: address = 62; break;
    case 0x15A: address = 66; break;
    case 0x170: address = 70; break;
    case 0x176: address = 74; break;
    default:
        return false;
    }

    if (startAddress != nullptr) {
        *startAddress = address;
    }
    if (registerCount != nullptr) {
        *registerCount = 4;
    }
    return true;
}

QByteArray CanToNetClient::registersToCanData(const QVector<quint16> &registers)
{
    QByteArray data;
    data.reserve(registers.size() * 2);
    for (const auto value : registers) {
        data.append(char(value & 0xFF));
        data.append(char((value >> 8) & 0xFF));
    }
    return data.left(8);
}

QMap<int, quint16> CanToNetClient::canDataToRegisters(int startAddress, const QByteArray &canData8)
{
    QMap<int, quint16> registers;
    for (int i = 0; i < 8; i += 2) {
        const quint8 low = i < canData8.size() ? quint8(canData8[i]) : 0;
        const quint8 high = (i + 1) < canData8.size() ? quint8(canData8[i + 1]) : 0;
        registers[startAddress + i / 2] = quint16(low) | (quint16(high) << 8);
    }
    return registers;
}

void CanToNetClient::onReadyRead()
{
    _rxBuffer.append(_socket.readAll());

    while (_rxBuffer.size() >= 7) {
        const quint16 length = (quint8(_rxBuffer[4]) << 8) | quint8(_rxBuffer[5]);
        const int frameSize = 6 + length;
        if (_rxBuffer.size() < frameSize) {
            return;
        }

        const QByteArray adu = _rxBuffer.left(frameSize);
        _rxBuffer.remove(0, frameSize);
        handleResponse(adu);
    }
}

void CanToNetClient::onSocketError(QAbstractSocket::SocketError)
{
    emit errorOccurred(_socket.errorString());
    _timeoutTimer.stop();
    _hasCurrentRequest = false;
    _queue.clear();
}

void CanToNetClient::onRequestTimeout()
{
    emit errorOccurred(QStringLiteral("CAN_TO_NET communication timeout."));
    _socket.abort();
    _hasCurrentRequest = false;
    _queue.clear();
}

void CanToNetClient::onPollTimeout()
{
    if (_pollByCanId) {
        readCanFrame(_pollCanId);
    } else {
        readHoldingRegisters(_pollStartAddress, _pollCount);
    }
}

quint16 CanToNetClient::toProtocolAddress(int canToNetAddress)
{
    const int protocolAddress = canToNetAddress - _addressOffset;
    if (protocolAddress < 0 || protocolAddress > 65535) {
        emit errorOccurred(QStringLiteral("CAN_TO_NET address out of range."));
        return 0;
    }

    return quint16(protocolAddress);
}

void CanToNetClient::enqueue(Request request)
{
    _queue.enqueue(request);
    sendNext();
}

void CanToNetClient::sendNext()
{
    if (_hasCurrentRequest || _queue.isEmpty()) {
        return;
    }

    if (!isConnected()) {
        emit errorOccurred(QStringLiteral("CAN_TO_NET is not connected."));
        return;
    }

    _current = _queue.dequeue();
    _current.transactionId = _nextTransactionId++;

    QByteArray adu = buildMbap(_current.transactionId, _current.pdu.size());
    adu.append(_current.pdu);
    _socket.write(adu);
    _socket.flush();

    _hasCurrentRequest = true;
    _timeoutTimer.start(_timeoutMs);
}

void CanToNetClient::handleResponse(const QByteArray &adu)
{
    if (!_hasCurrentRequest || adu.size() < 9) {
        return;
    }

    const quint16 transactionId = (quint8(adu[0]) << 8) | quint8(adu[1]);
    const quint8 unitId = quint8(adu[6]);
    const QByteArray pdu = adu.mid(7);

    if (transactionId != _current.transactionId || unitId != _unitId) {
        emit errorOccurred(QStringLiteral("CAN_TO_NET response does not match request."));
        return;
    }

    if ((quint8(pdu[0]) & 0x80) != 0) {
        const quint8 code = pdu.size() > 1 ? quint8(pdu[1]) : 0;
        emit errorOccurred(QStringLiteral("CAN_TO_NET Modbus exception: %1").arg(code));
        completeCurrentRequest();
        return;
    }

    if (_current.type == RequestType::Read) {
        const int byteCount = pdu.size() > 1 ? quint8(pdu[1]) : 0;
        QVector<quint16> values;
        for (int i = 0; i + 1 < byteCount; i += 2) {
            values.append((quint8(pdu[2 + i]) << 8) | quint8(pdu[3 + i]));
        }

        if (_current.isCanFrame) {
            emit canFrameRead(_current.canId, registersToCanData(values));
        } else {
            emit registersRead(_current.canToNetStartAddress, values);
        }
    } else {
        if (_current.isCanFrame) {
            emit canFrameWritten(_current.canId);
        } else {
            emit writeFinished(_current.count);
        }
    }

    completeCurrentRequest();
}

void CanToNetClient::completeCurrentRequest()
{
    _timeoutTimer.stop();
    _hasCurrentRequest = false;
    sendNext();
}

QByteArray CanToNetClient::buildMbap(quint16 transactionId, quint16 pduLength) const
{
    QByteArray mbap;
    QDataStream stream(&mbap, QIODevice::WriteOnly);
    stream.setByteOrder(QDataStream::BigEndian);
    stream << transactionId << quint16(0) << quint16(1 + pduLength) << _unitId;
    return mbap;
}

CanToNetClient::Request CanToNetClient::buildReadRequest(int canToNetStartAddress, int count, quint32 canId)
{
    const auto protocolAddress = toProtocolAddress(canToNetStartAddress);

    QByteArray pdu;
    QDataStream stream(&pdu, QIODevice::WriteOnly);
    stream.setByteOrder(QDataStream::BigEndian);
    stream << quint8(0x03) << protocolAddress << quint16(count);

    Request request;
    request.type = RequestType::Read;
    request.functionCode = 0x03;
    request.canToNetStartAddress = canToNetStartAddress;
    request.count = count;
    request.canId = canId;
    request.isCanFrame = canId != 0;
    request.pdu = pdu;
    return request;
}

CanToNetClient::Request CanToNetClient::buildWriteRequest(int canToNetStartAddress, const QVector<quint16> &values, quint32 canId)
{
    const auto protocolAddress = toProtocolAddress(canToNetStartAddress);

    QByteArray pdu;
    QDataStream stream(&pdu, QIODevice::WriteOnly);
    stream.setByteOrder(QDataStream::BigEndian);

    if (values.size() == 1) {
        stream << quint8(0x06) << protocolAddress << values.first();
    } else {
        stream << quint8(0x10) << protocolAddress << quint16(values.size()) << quint8(values.size() * 2);
        for (const auto value : values) {
            stream << value;
        }
    }

    Request request;
    request.type = RequestType::Write;
    request.functionCode = values.size() == 1 ? 0x06 : 0x10;
    request.canToNetStartAddress = canToNetStartAddress;
    request.count = values.size();
    request.canId = canId;
    request.isCanFrame = canId != 0;
    request.pdu = pdu;
    return request;
}
