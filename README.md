# 上位机监控

关键词：上位机监控、CAN 上位机、Keil 变量监控、在线监控、离线模拟、固件同步、自动更新、modbus、CAN_TO_NET。

这是用于 Keil/嵌入式 C 工程现场调试的 Windows 上位机源码仓库。当前重点是 CAN 变量监控、程序透视、代码旁实时值、离线 C worker、监控固件同步和服务器自动更新。

同事在 GitHub 搜索：

```text
上位机监控
```

即可定位本仓库。

## 目录

- `CanVariableMonitor/`：主 WinForms 上位机源码，当前交付版本从 `V1.0` 重新计数。
- `CanVariableMonitor.OfflineCWorker/`：离线 C worker 源码。
- `CanVariableMonitor/WORK_HANDOFF_CURRENT.md`：当前交接说明和注意事项。
- `can_monitor_agent.c`：控制器侧 CAN 监控固件 agent。
- `CAN_MONITOR_PROTOCOL.md`：CAN 监控协议说明。
- `keil_live_watch.py`：早期 Keil/J-Link 变量监控原型脚本。
- `MODBUS_CAN_TO_NET_HANDOFF.md`：modbus/CAN_TO_NET 接手总资料。
- `McgsModbusTool/`：电脑端 Modbus TCP 调试工具源码。
- `QtCanToNetClient/`：QT 同事可直接接入的 CAN_TO_NET 客户端封装。
- `CAN_TO_NET_QT_DATA_TABLE.md`：CAN ID、CAN_TO_NET 地址、CAN 字节内容对照表。
- `CAN_TO_NET_QT_PACKAGE/`：QT 交付包，包含 `.h/.cpp/README/数据表`。

## 构建

需要 Windows 和 .NET 9 SDK。

```powershell
dotnet build ".\CanVariableMonitor\CanVariableMonitor.csproj"
```

统一发布：

```powershell
powershell -ExecutionPolicy Bypass -File ".\CanVariableMonitor\PublishUnified.ps1"
```

## 自动更新

客户端配置读取：

```text
http://8.148.250.52:9999/update_manifest.json
```

更新判断规则：服务器版本号和本地版本号只要不一致，就触发更新，不再比较谁大谁小。

服务器根目录只需要放两个文件：

```text
update_manifest.json
can_monitor_latest.zip
```

不要把外层 `server_upload_*.zip`、旧版本备份包、README 或其他说明文件放到服务器根目录。

## 协作规则

本仓库只上传项目源码和安全文档。不要上传任何 Codex 机器配置、登录状态、MCP 配置、SQLite 记忆库、个人 toolkit、`.env` 或本机客户私有配置，避免影响其他电脑登录和使用 Codex。

发布包、临时截图、驱动安装包、构建产物和本地配置已经通过 `.gitignore` 排除。
