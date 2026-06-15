# 上位机监控当前交接说明

## 当前状态

- GitHub 仓库：`https://github.com/hudonghua/can-variable-monitor.git`
- 关键词：`上位机监控`
- 当前源码版本号：`V1.0`
- 自动更新地址：`http://8.148.250.52:9999/update_manifest.json`
- F 盘本机测试目录：`F:\工作\AI模型\s上位机\监控上位机\上位机\上位机监控_V1.2_20260612_120554`

## 构建命令

```powershell
dotnet build ".\CanVariableMonitor\CanVariableMonitor.csproj"
```

## 发布命令

```powershell
powershell -ExecutionPolicy Bypass -File ".\CanVariableMonitor\PublishUnified.ps1"
```

发布脚本会生成客户端包和服务器上传用文件。服务器根目录只放：

```text
update_manifest.json
can_monitor_latest.zip
```

不要把外层打包 zip、说明文档、备份包一起放到服务器。

## 自动更新规则

- 客户端读取 `update_config.json` 或内置默认配置。
- `manifestUrl` 指向 `http://8.148.250.52:9999/update_manifest.json`。
- 只要服务器 `version` 和本地版本字符串不一致，就提示更新。
- `packageUrl` 固定使用 `can_monitor_latest.zip`，按 manifest 所在服务器根目录解析。

## 近期重点改动

- `AppUpdateService.cs`：自动更新改为版本字符串不一致即更新，更新失败会弹窗提示。
- `PublishUnified.ps1`：服务器包精简为两个文件。
- `MainForm.cs`：代码区、数值显示、主题、函数树联动、在线/离线调试交互仍在持续调整。
- `CanVariableMonitor.OfflineCWorker/Program.cs` + `SupportPacks/LPC1765_Keil_AppStubPack.cs`：离线 worker 只执行应用层 C；BSP/Driver/CMSIS/Startup/CAN/ADC/GPIO/UART/Timer/EEPROM 等底层边界自动 stub/mock，不做 LPC1765 寄存器级仿真。
- `keil_compat.h` 由 AppStubPack 写入 `%LOCALAPPDATA%\CanVariableMonitor\offline_c_worker\...` 临时仿真目录，通过 wrapper 注入；不写客户工程，不要求客户源码 include，在线 CAN 监控完全不依赖它。
- 离线 worker 不能靠逐个客户工程“调教”；入口由调用图和通用 entry rules 自动发现，UI 可选择并保存项目入口配置；找不到入口时降级静态离线并显示候选/未覆盖诊断。
- `OfflineWorkerSelfTest.ps1`、`OfflineRealProjectProbe.ps1`：用于离线 worker 本机验证。

## 当前已知风险

- 离线 C worker 比早期正则模拟更接近真实 C 逻辑，但仍需要用客户真实工程继续探针验证。
- TinyCC 是离线 C worker 的运行依赖；源码仓库按 `.gitignore` 不提交 `*.exe/*.dll` 和 `CanVariableMonitor/tools/tinycc/`，发布机需要提前放好 `CanVariableMonitor\tools\tinycc`，或保留已解压的 `can_monitor_client_V1.0\offline_c_worker\tinycc` 供发布脚本复制。
- 2026-06-15 当前方向调整为 AppStubPack：应用层函数真实执行，底层源码不参与编译；缺失底层函数自动 stub/mock，输出边界记录到 coverage，失败时 MainForm 自动回落到静态离线快照。
- 代码旁数值显示已经多次调整，后续修改时必须优先保证不闪、不抢焦点、不破坏 Ctrl+C 和滚动手感。
- 在线模式以控制器真实 RAM 为准，不应被离线 worker 逻辑影响。
- 版本号近期用于测试在线更新，修改版本时要同步确认服务器 manifest。

## 绝对不要上传的内容

不要把以下内容提交到这个仓库或任何交接包：

- `C:\Users\DELL\.codex\config.toml`
- Codex 登录态、认证文件、token
- MCP 本机配置、SQLite 记忆库
- 个人 toolkit 或跨电脑同步包
- `.env`、客户私有配置、真实密钥
- `release/`、`dist/`、`bin/`、`obj/`、截图、驱动安装包、zip 发布包

这条规则来自一次真实教训：上传机器级 Codex/toolkit 配置会导致别的电脑 Codex 登录或配置加载异常。接手同事只需要本仓库源码和本文档。

## 接手建议

1. 先运行 `dotnet build`，确认工程能编译。
2. 再用 `PublishUnified.ps1` 生成本机发布包。
3. 本地验证在线更新时，只替换服务器根目录的 `update_manifest.json` 和 `can_monitor_latest.zip`。
4. 修改 UI 或实时值刷新后，必须用真实工程测试滚轮、鼠标点击、Ctrl+C、在线/离线切换。
