# QT接入CAN_TO_NET

## 链路

```text
QT电脑端 <--以太网/Modbus TCP 503--> CAN_TO_NET <--CAN总线--> 控制器
```

QT侧直接使用 `CanToNetClient.h/.cpp`。

`.pro` 工程添加：

```text
QT += network
```

CMake 工程添加：

```cmake
find_package(Qt6 REQUIRED COMPONENTS Network)
target_link_libraries(your_target PRIVATE Qt6::Network)
```

## 默认参数

```text
IP: 192.168.0.105
Port: 503
Unit ID: 255
Timeout: 100ms
Address offset: 1
```

## CAN ID到CAN_TO_NET地址

每个CAN ID占4个连续CAN_TO_NET地址，对应8字节CAN DATA。

| CAN ID | CAN_TO_NET地址 |
|---|---|
| 0x50 | 100-103 |
| 0x71 | 104-107 |
| 0x75 | 108-111 |
| 0x7A | 112-115 |
| 0x150 | 50-53 |
| 0x152 | 54-57 |
| 0x153 | 58-61 |
| 0x154 | 62-65 |
| 0x15A | 66-69 |
| 0x170 | 70-73 |
| 0x176 | 74-77 |

## 字节打包规则

一个CAN_TO_NET地址放两个CAN字节，低字节在前。

```text
地址值 = 低字节 + 高字节 * 256
```

例如CAN ID `0x50`：

```text
地址100 = B0 + B1 * 256
地址101 = B2 + B3 * 256
地址102 = B4 + B5 * 256
地址103 = B6 + B7 * 256
```

`CanToNetClient` 已经自动完成这个打包和拆包。

## 示例

```cpp
auto client = new CanToNetClient(this);
client->setEndpoint("192.168.0.105", 503);
client->setUnitId(255);
client->setAddressOffset(1);
client->setTimeoutMs(100);
client->connectToCanToNet();

connect(client, &CanToNetClient::canFrameRead, this,
        [](quint32 canId, const QByteArray &data) {
            // data[0]..data[7] 对应 CAN B0..B7
        });

// 读取 0x150，对应 CAN_TO_NET 地址 50-53
client->readCanFrame(0x150);

// 100ms轮询 0x150
client->startPollingCanFrame(0x150, 100);

// 写 0x50，对应 CAN_TO_NET 地址 100-103
QByteArray data(8, 0);
data[0] = char(0x01); // B0
data[1] = char(0x00); // B1
data[7] = char(0xA1); // B7
client->writeCanFrame(0x50, data);
```

底层仍保留按CAN_TO_NET地址读写寄存器：

```cpp
client->readHoldingRegisters(50, 4);

QMap<int, quint16> values;
values[100] = 0x0001;
values[101] = 0x0000;
values[102] = 0x0000;
values[103] = 0xA100;
client->writeHoldingRegisters(values);
```
