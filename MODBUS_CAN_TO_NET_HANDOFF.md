# modbus / CAN_TO_NET 接手资料

这份文档给后续接手的人看。关键词：`modbus`、`CAN_TO_NET`、`电脑`、`QT`、`CAN ID`。

## 一句话结论

电脑通过以太网访问 `CAN_TO_NET`，现场已经调通的方式是 `Modbus TCP 503`。`CAN_TO_NET` 再通过 CAN 总线和控制器通讯。

```text
QT电脑端 / 调试电脑 <---以太网 Modbus TCP 503---> CAN_TO_NET <---CAN总线---> 控制器
```

## 默认通讯参数

| 项目 | 值 |
|---|---|
| CAN_TO_NET IP | 192.168.0.105 |
| Modbus TCP 端口 | 503 |
| Unit ID / 站号 | 255 |
| 建议超时 | 100ms |
| 建议轮询周期 | 100ms |
| 地址偏移 | 1 |

说明：软件界面和 QT 封装里填写的是 `CAN_TO_NET地址`。底层 Modbus 报文会自动做 `协议地址 = CAN_TO_NET地址 - 1`，接手的人不用再手动换算。

## 资料入口

| 路径 | 用途 |
|---|---|
| `McgsModbusTool/` | WinForms 调试工具源码，可读写 CAN_TO_NET 的 Modbus 保持寄存器 |
| `run_mcg_modbus_tool.bat` | 一键启动调试工具 |
| `QtCanToNetClient/` | QT 电脑端接入用的 `.h/.cpp` 封装 |
| `CAN_TO_NET_QT_PACKAGE/` | 给 QT 同事的交付包，包含源码和表格 |
| `CAN_TO_NET_QT_DATA_TABLE.md` | CAN ID、CAN_TO_NET地址、CAN字节、业务含义总表 |

## CAN ID 和 CAN_TO_NET 地址

一个 CAN ID 占 4 个连续 `CAN_TO_NET地址`，对应 8 个 CAN DATA 字节。

| 方向 | CAN ID | CAN_TO_NET地址 |
|---|---|---|
| 电脑 -> CAN_TO_NET -> 控制器 | 0x50 | 100-103 |
| 电脑 -> CAN_TO_NET -> 控制器 | 0x71 | 104-107 |
| 电脑 -> CAN_TO_NET -> 控制器 | 0x75 | 108-111 |
| 电脑 -> CAN_TO_NET -> 控制器 | 0x7A | 112-115 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x150 | 50-53 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x152 | 54-57 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x153 | 58-61 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x154 | 62-65 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x15A | 66-69 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x170 | 70-73 |
| 控制器 -> CAN_TO_NET -> 电脑 | 0x176 | 74-77 |

## 字节打包规则

一个 `CAN_TO_NET地址` 放 2 个 CAN 字节，低字节在前，高字节在后。

```text
地址值 = 低字节 + 高字节 * 256
```

例如 `CAN ID 0x50` 对应 `CAN_TO_NET地址 100-103`：

| CAN_TO_NET地址 | CAN字节 | 写入的16位值 |
|---:|---|---|
| 100 | B0、B1 | B0 + B1 * 256 |
| 101 | B2、B3 | B2 + B3 * 256 |
| 102 | B4、B5 | B4 + B5 * 256 |
| 103 | B6、B7 | B6 + B7 * 256 |

QT 侧如果使用 `CanToNetClient`，直接调用 `readCanFrame()` / `writeCanFrame()` 即可，封装内部已经完成上面的打包和拆包。

## QT 接入方式

把下面两个文件加入 QT 工程：

```text
QtCanToNetClient/CanToNetClient.h
QtCanToNetClient/CanToNetClient.cpp
```

`.pro` 工程增加：

```text
QT += network
```

常用调用：

```cpp
auto client = new CanToNetClient(this);
client->setEndpoint("192.168.0.105", 503);
client->setUnitId(255);
client->setAddressOffset(1);
client->setTimeoutMs(100);
client->connectToCanToNet();

client->readCanFrame(0x150);
client->startPollingCanFrame(0x150, 100);

QByteArray data(8, 0);
data[0] = char(0x01);
data[1] = char(0x00);
data[7] = char(0xA1);
client->writeCanFrame(0x50, data);
```

## 调试工具使用

需要 Windows 和 .NET 9 SDK。

```powershell
dotnet run --project .\McgsModbusTool\McgsModbusTool.csproj
```

也可以双击：

```text
run_mcg_modbus_tool.bat
```

左侧用于读取 CAN_TO_NET 到电脑的数据，右侧用于编辑并写入电脑到 CAN_TO_NET 的数据。连续地址会自动合并为一包 `16 写多个保持寄存器`，单个地址会用 `06 写单个保持寄存器`。

## 注意事项

- 本仓库上传源码和文档，不上传 `bin/`、`obj/`、`.zip`、`.bak_*`、驱动安装包和本地私有配置。
- `CAN_TO_NET_QT_DATA_TABLE.md` 是业务信号查表入口；先按 CAN ID 找地址段，再按 Byte/Bit 看具体含义。
- 现场测试过的 Modbus 入口是 `192.168.0.105:503`。资料里也出现过 CAN-NET 透传端口 `500`，本次交接包重点是已经封装好的 Modbus 503 方案。
