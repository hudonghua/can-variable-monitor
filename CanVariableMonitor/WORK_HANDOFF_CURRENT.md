# 上位机监控当前交接说明

## 当前状态

- GitHub 仓库：`https://github.com/hudonghua/can-variable-monitor.git`
- 关键词：`上位机监控`
- 当前源码版本号：`V1.3`
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

## 2026-06-15 V1.3 main-loop 离线内测结果

本轮修正后，程序透视优先显示真实 `main` 入口；离线模式不再把 `MyLogic_10ms`、`work_logic` 等客户函数当根入口硬跑，而是自动抽取 `main` 里第一个 `while(1)` / `for(;;)` 循环体，生成临时入口 `__canmon_main_loop_tick`，外围初始化不重复执行。worker 每个 tick 会把 `gTimeFlg_*` / `*TimeFlg*` / `*flg*ms*` 这类定时标志 mock 为触发态，用来模拟“中断置位后主循环进入业务函数”的常见结构。

`OfflineRealProjectProbe.ps1` 已升级为 UI 风格验证：根入口必须是 `__canmon_main_loop_tick`，再自动找应用层 `if` 分支，baseline 目标变量为 0，通过 `ForceVariable` 强制条件变量，确认分支内目标变量变为非 0 合理值。七个真实工程复测结果：

- 乌拉特后旗中深孔：`E:\AI_划时代\T天腾\C采矿装药车\单独装药系统\小洪给我的版本（以此为准kx119_tt_zy）\乌拉特后旗装药车\keil5_中深孔\MC_LCD - 7Control_V1.2` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 36；定时标志 6；强制 `kong_rev_retain=1` 后 `kong_rev_retain_flags=1`。
- 简约款劈裂车主工程：`E:\AI_划时代\T天腾\P劈裂车\简约款劈裂\显示屏7-200\MC_LCD - 7Control_V1.2` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 33；定时标志 6；强制 `Engine_Start_Network_dly=1` 后 `Engine_Start_DO=1`。
- 简约款劈裂车 `ui_kl` 子工程：`E:\AI_划时代\T天腾\P劈裂车\简约款劈裂\显示屏7-200\ui_kl\MC_LCD - 7Control_V1.2` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 33；定时标志 6；强制 `Engine_Start_Network_dly=1` 后 `Engine_Start_DO=1`。
- 湿喷机：`E:\AI_划时代\T天腾\湿喷机\MC_LCD - 7Control_V3.0dev210919_JJ` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 41；定时标志 6；强制 `Force_work=1` 后 `force_time=249`。
- 铵油装药车编码器铁轮版：`E:\AI_划时代\T天腾\C采矿装药车\铵油装药车\中深孔修改后\江南爆破中深孔\速度为设置编码器铁轮\MC_LCD - 7Control_V1.2` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 36；定时标志 6；强制 `Shovels_up_DO=1` 后 `PWM_3A_119_CAN1=600`。
- 旭工干喷：`E:\AI_划时代\旭工\干喷\程序\显示屏7-200\MC_LCD - 7Control_V1.2` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 32；定时标志 6；强制 `Main_Pump_Current_up_DI=1` 后 `Paramet_Set1=5`。
- 华矿二代半液压主控：`E:\AI_划时代\H华矿\华旷二代半液压\二代半液压\主控\主控\主控` 通过；程序透视 `Start=main`；离线入口 `__canmon_main_loop_tick`；应用层可达源码 60；定时标志 6；强制 `Hydraulic_Temperature_control_DI=1` 后 `Hydraulic_Temperature_control_DI_dly=499`。

本轮通用修复点：程序图谱和 UI 摘要优先 `main`；离线 worker tick 前自动 mock 定时标志；切换工程时清空上一工程保存的离线入口选择，避免跨项目串配置；找不到 main-loop 时仍回落旧候选入口和静态离线诊断。

## 当前已知风险

- 离线 C worker 比早期正则模拟更接近真实 C 逻辑，但仍需要用客户真实工程继续探针验证。
- TinyCC 是离线 C worker 的运行依赖；源码仓库按 `.gitignore` 不提交 `*.exe/*.dll` 和 `CanVariableMonitor/tools/tinycc/`，发布机需要提前放好 `CanVariableMonitor\tools\tinycc`，或保留已解压的 `can_monitor_client_V1.0\offline_c_worker\tinycc` 供发布脚本复制。
- 2026-06-15 当前方向调整为 AppStubPack：应用层函数真实执行，底层源码不参与编译；缺失底层函数自动 stub/mock，输出边界记录到 coverage，失败时 MainForm 自动回落到静态离线快照。
- 代码旁数值显示已经多次调整，后续修改时必须优先保证不闪、不抢焦点、不破坏 Ctrl+C 和滚动手感。
- 在线模式以控制器真实 RAM 为准，不应被离线 worker 逻辑影响。
- 版本号近期用于测试在线更新，修改版本时要同步确认服务器 manifest。

## 2026-06-15 离线变量不刷新修正

用户现场确认界面布局已经可用，但进入离线模式后代码旁/变量表看起来没有跑。注意：离线模式没有 100 个变量容量限制，`IsWatchCapacityLimited()` 在离线时返回 `false`，`GetEnabledWatchSnapshot()` 会允许全量 enabled watch items。前一版交接里把在线容量限制误写成离线根因，这是错误表述。

本轮实际修正点是离线 UI 数据闭环：自动变量发现应基于完整 reachable application sources，而不是只看候选/入口 seed sources；worker 程序模型里的变量绑定也必须映射回真实 `_watchItems` 对象后再刷新，否则模型克隆对象即使有快照值，也不会被 `FlushPendingWatchUpdates()` 应用到界面。

本轮修正：

- `EnsureOfflineApplicationWatchItems()` 改为优先使用完整 `OfflineProgramModel.Sources`，自动加入 reachable 应用层变量，而不是只扫入口/候选 seed sources。
- `PollOfflineSimulation()` 在 worker tick 后通过 `BuildOfflineWorkerRefreshItems()` 合并当前轮询项和 `model.Bindings`。
- `BuildOfflineWorkerRefreshItems()` 会把模型绑定映射回真实 `_watchItems` 对象；模型克隆对象不直接排队刷新，避免 `FlushPendingWatchUpdates()` 跳过导致统计虚高。

验证结果：

- `dotnet build .\CanVariableMonitor\CanVariableMonitor.csproj -v:minimal`：0 错误，59 个历史警告。
- `OfflineWorkerSelfTest.ps1`：通过，应用层链路连续 tick，底层 `CAN_SendFrame` 被记录为输出 stub。
- `OfflineRealProjectProbe.ps1` 三个真实工程通过：旭工干喷、江南爆破中深孔编码器铁轮版、华矿二代半液压主控。三者均以 `__canmon_main_loop_tick` 为离线入口，强制输入后目标变量变化。

## 2026-06-15 离线 tick 真实执行修正

用户现场再次确认“没有跑起来”时，真实日志显示 worker 已初始化，但 `RunTick()` 每轮失败，统计为 `执行 0 拍`。这不是 UI 容量限制，也不是离线按钮没开；根因是生成的临时 `canmon_tick.c` 无法通过 TinyCC。

本轮修正：

- 变量名和函数调用同名时，不再把变量别名生成成 `#define name __cm_vN`。例如 `gp_lcdtask` 在客户工程里既出现在变量表，也以 `gp_lcdtask(...)` 形式被调用；旧生成器会同时生成变量宏和 stub 宏，导致 TinyCC 宏重定义。
- 客户工程里的 `main()` 引用可以被 stub 成 `__canmon_stub_main()`，但 worker 自己的 harness `int main()` 前必须 `#undef main`，否则预处理器会把 harness main 也替换成 `__canmon_stub_main`，最终报 `redefinition of '__canmon_stub_main'`。
- `OfflineWorkerSelfTest.ps1` 加入两个回归场景：函数式变量别名 `TaskHook`，以及客户源码里调用 `main()`。自测要求前者不生成变量宏，后者不污染 worker harness main。

实测验证：

- `OfflineWorkerSelfTest.ps1`：通过，连续 5 tick，输出 stub 和函数式变量别名均覆盖。
- `dotnet build .\CanVariableMonitor.OfflineCWorker\CanVariableMonitor.OfflineCWorker.csproj -v:minimal`：0 警告，0 错误。
- `dotnet build .\CanVariableMonitor\CanVariableMonitor.csproj -v:minimal`：0 警告，0 错误。
- `PublishUnified.ps1` 已重新发布到 `F:\工作\AI模型\s上位机\监控上位机\上位机\上位机监控_V1.2_20260612_120554`，发布包 `上位机监控_V1.3_20260615_225304.zip`。
- 真实打开发布版 `上位机监控 V1.3`，工程 `E:\AI_划时代\旭工\干喷\程序\显示屏7-200\MC_LCD - 7Control_V1.2`，点击“离线”后日志显示 `离线性能：循环，目标 99 ms，耗时 4389 ms，变量 887，执行 1 拍，刷新 887，跳过 0`，10 秒后仍为 `执行 1 拍`。
- 直接运行最新临时 `canmon_tick.c` 输出 914 行快照，检测到 8 个变量 tick 后变化：`gYunx20ms/gYunx10ms/gYunx220/gYunx40ms/gYunx60ms` 从 87 到 88，`Prog_Run_1s` 从 36 到 37，`Prog_Run_2s` 从 25 到 26，`Prog_Run_3s` 从 3 到 4。

## 2026-06-15 离线入口语义修正

用户实测发现：同一个全局变量在一个 `.c` 写、另一个 `.c` 读并判断时，如果手动选择某个离线入口，变量链路可能不执行；切回“自动入口”后恢复正常。根因是旧逻辑一旦存在手动入口配置，就禁用了自动 `main while(1)` tick，导致真实主循环里的跨文件调度顺序被截断。

本轮修正：

- `BuildOfflineApplicationRootSources()` 不再让手动入口覆盖自动主循环。
- 只要能从 `main` 抽取 `while(1)` / `for(;;)`，都会优先加入 `__canmon_main_loop_tick`。
- 手动选择的离线入口保留，但语义变成“追加入口/观察入口”，不再切断自动 main-loop。
- 这样跨 `.c` 的中间全局状态仍按主循环顺序跑；用户误选业务函数时，也不应破坏自动入口。

验证：

- 最小 worker 探针已验证 `writer.c` 写 `SharedFlag`、`reader.c` 读 `SharedFlag` 后 `OutputCount++` 可以跨文件生效。
- `dotnet build .\CanVariableMonitor\CanVariableMonitor.csproj -v:minimal`：0 错误，59 个历史警告。

## 2026-06-15 应用函数被名字规则误 stub 修正

用户进一步指出：失败函数基本都是简单变量赋值，并不是复杂指针、结构体或硬件寄存器。复查 worker 后确认另一个真实根因：`definedFunctionNames` 以前直接套用 `SupportPack.IsStubOnlyFunctionName()` 过滤，导致名字像底层函数的应用层函数被误当 stub。例如应用源码里真实定义的 `DI_Scan()`，即使函数体只是 `InputReady = 1;`，也会因为 `DI` 前缀被排除出真实编译函数表，最终调用走 `__canmon_stub_DI_Scan()`，简单赋值自然不会执行。

本轮修正：

- 应用层源码中能找到真实定义的函数，优先真实编译执行，不再因为函数名像 `DI_*` / `KEY*` / `ADC*` / `CAN*` / `PWM*` / `LCD*` 就直接 stub。
- `SupportPack.IsStubOnlyFunctionName()` 只用于没有应用层定义的边界调用；底层/驱动缺失函数仍自动 stub/mock。
- 保留 harness/内部保留名过滤：`main`、`sprintf`、`snprintf`、`CanMonitor_*` 仍不作为普通应用函数编译，避免污染 worker 自己的 `main()` 或监控内部逻辑。

验证：

- `OfflineWorkerSelfTest.ps1` 新增 `DI_Scan()` 应用源码用例，`DI_Scan()` 只做 `InputReady = 1;`；自测要求 `OutputCount` 连续递增，且生成的 `canmon_tick.c` 中必须存在 `void DI_Scan(void)`，不能出现 `#define DI_Scan(...) __canmon_stub_DI_Scan()`。
- `OfflineWorkerSelfTest.ps1`：通过。
- `dotnet build .\CanVariableMonitor.OfflineCWorker\CanVariableMonitor.OfflineCWorker.csproj -v:minimal`：0 警告，0 错误。
- `dotnet build .\CanVariableMonitor\CanVariableMonitor.csproj -v:minimal`：0 错误，59 个历史警告。

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
