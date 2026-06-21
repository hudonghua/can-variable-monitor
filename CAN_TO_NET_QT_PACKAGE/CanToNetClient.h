#pragma once

#include <QByteArray>
#include <QMap>
#include <QObject>
#include <QQueue>
#include <QTcpSocket>
#include <QTimer>
#include <QVector>

class CanToNetClient final : public QObject
{
    Q_OBJECT

public:
    explicit CanToNetClient(QObject *parent = nullptr);

    void setEndpoint(const QString &host, quint16 port);
    void setUnitId(quint8 unitId);
    void setAddressOffset(int offset);
    void setTimeoutMs(int timeoutMs);

    void connectToCanToNet();
    void disconnectFromCanToNet();
    bool isConnected() const;

    void readHoldingRegisters(int canToNetStartAddress, int count);
    void writeHoldingRegisters(const QMap<int, quint16> &canToNetAddressValues);

    void readCanFrame(quint32 canId);
    void writeCanFrame(quint32 canId, const QByteArray &canData8);

    void startPolling(int canToNetStartAddress, int count, int intervalMs);
    void startPollingCanFrame(quint32 canId, int intervalMs);
    void stopPolling();

    static bool canIdToAddressRange(quint32 canId, int *startAddress, int *registerCount = nullptr);
    static QByteArray registersToCanData(const QVector<quint16> &registers);
    static QMap<int, quint16> canDataToRegisters(int startAddress, const QByteArray &canData8);

signals:
    void connected();
    void disconnected();
    void registersRead(int canToNetStartAddress, QVector<quint16> values);
    void canFrameRead(quint32 canId, QByteArray canData8);
    void writeFinished(int count);
    void canFrameWritten(quint32 canId);
    void errorOccurred(QString message);

private slots:
    void onReadyRead();
    void onSocketError(QAbstractSocket::SocketError error);
    void onRequestTimeout();
    void onPollTimeout();

private:
    enum class RequestType
    {
        Read,
        Write
    };

    struct Request
    {
        RequestType type;
        quint16 transactionId = 0;
        quint8 functionCode = 0;
        int canToNetStartAddress = 0;
        int count = 0;
        quint32 canId = 0;
        bool isCanFrame = false;
        QByteArray pdu;
    };

    quint16 toProtocolAddress(int canToNetAddress);
    void enqueue(Request request);
    void sendNext();
    void handleResponse(const QByteArray &adu);
    void completeCurrentRequest();
    QByteArray buildMbap(quint16 transactionId, quint16 pduLength) const;
    Request buildReadRequest(int canToNetStartAddress, int count, quint32 canId = 0);
    Request buildWriteRequest(int canToNetStartAddress, const QVector<quint16> &values, quint32 canId = 0);

    QTcpSocket _socket;
    QTimer _timeoutTimer;
    QTimer _pollTimer;
    QByteArray _rxBuffer;
    QQueue<Request> _queue;
    Request _current;

    QString _host = QStringLiteral("192.168.0.105");
    quint16 _port = 503;
    quint8 _unitId = 255;
    int _addressOffset = 1;
    int _timeoutMs = 100;
    int _pollStartAddress = 50;
    int _pollCount = 4;
    quint32 _pollCanId = 0;
    bool _pollByCanId = false;
    quint16 _nextTransactionId = 1;
    bool _hasCurrentRequest = false;
};
