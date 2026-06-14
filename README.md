# 上位机监控

关键词：上位机监控、CAN 上位机、Keil 变量监控、在线监控、离线模拟、固件同步。

这是用于 Keil/嵌入式 C 工程现场调试的 Windows 上位机监控工具源码和配套文档。它围绕 CAN 变量监控、程序透视、代码旁实时值、离线 C 仿真 worker、监控固件同步和服务器自动更新展开。

同事查找资料时，在 GitHub 搜索：

```text
上位机监控
```

即可定位到本仓库。

## 目录

- `CanVariableMonitor/`：主 WinForms 上位机源码，当前记录版本为 `V1.79`。
- `CanVariableMonitor.OfflineCWorker/`：离线 C 仿真 worker 源码。
- `can_monitor_agent.c`：控制器侧 CAN 监控固件 agent。
- `CAN_MONITOR_PROTOCOL.md`：CAN 监控协议说明。
- `CanVariableMonitor/WORK_HANDOFF_V1.79.md`：V1.66 到 V1.79 的迭代交接记录和经验清单。
- `CanVariableMonitor/README.md`：主程序早期使用说明。
- `keil_live_watch.py`：早期 J-Link/Keil 变量监控原型脚本。

## 当前重点能力

- 自动解析 Keil `.map/.axf` 符号，建立变量监控索引。
- 在线模式下当前代码可见变量优先轮询，避免被 100 个全量变量拖慢。
- `PEAK / SYS / GC` 作为传输适配器，上层监控策略保持一致。
- 左侧程序透视与右侧代码区联动，便于现场理解业务函数链路。
- 右侧 `2 看数值` 同时显示代码和实时值，是唯一主代码窗口。
- 离线模式尝试通过 TinyCC/offline worker 执行业务层 C 逻辑。
- 刷新流程包含工程重读、map/axf/bin 检查和监控固件同步。
- V1.79 加入服务器 `update_manifest.json` + zip 包的自动更新框架。

## 构建

需要 Windows、.NET 9 SDK。

```powershell
dotnet build ".\CanVariableMonitor\CanVariableMonitor.csproj"
```

统一发布：

```powershell
powershell -ExecutionPolicy Bypass -File ".\CanVariableMonitor\PublishUnified.ps1"
```

## 自动更新

客户端发布目录中放置真实 `update_config.json`，指向服务器的 `update_manifest.json`。服务器放：

```text
update_manifest.json
上位机监控_Vx.xx_yyyyMMdd_HHmmss.zip
```

详细策略见：

```text
CanVariableMonitor/WORK_HANDOFF_V1.79.md
```

## 注意

仓库不上传本地构建产物、发布包、驱动安装包、临时截图、备份文件和客户私有配置。需要给客户交付时，请使用发布脚本生成发布包。
