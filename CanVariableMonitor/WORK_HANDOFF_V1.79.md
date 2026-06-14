# CAN 上位机 V1.79 工作交接与经验记录

记录时间：2026-06-14  
项目路径：`C:\Users\DELL\Documents\遥控器的杂碎事情\CanVariableMonitor`  
当前版本：`V1.79`  
当前发布目录：`F:\工作\AI模型\s上位机\监控上位机\上位机\上位机监控_V1.2_20260612_120554`

## 1. 当前状态

这个上位机已经从“变量监控工具”推进成“现场代码调试工作台”：

- 左侧 `1 程序透视`：用于业务函数位置、父子链路、当前代码段映射。
- 中间 `2 看数值`：唯一代码 + 实时值窗口，旧的独立 `3 看程序` 应退出刷新链路。
- 在线监控：当前代码可见变量优先轮询，PEAK / SYS / GC 只作为传输层，上层策略一致。
- 离线模拟：已引入 TinyCC/offline worker 思路，目标是按 `MyLogic_10ms + 显示链` 执行业务 C。
- 固件同步：刷新时检查工程、map/axf/bin 和监控固件状态，下载必须基于当前目标 bin 有效。
- 自动更新：V1.79 已加入服务器 manifest + zip 包的更新框架。

这份文件的目的不是写总结，而是防止后续客户反馈继续迭代时丢掉已经踩过的坑。

## 2. 最近确认过的构建与发布

常用命令：

```powershell
dotnet build "C:\Users\DELL\Documents\遥控器的杂碎事情\CanVariableMonitor\CanVariableMonitor.csproj"
powershell -ExecutionPolicy Bypass -File "C:\Users\DELL\Documents\遥控器的杂碎事情\CanVariableMonitor\PublishUnified.ps1"
```

最近已验证：

- `dotnet build`：0 error，保留既有 warning。
- `PublishUnified.ps1`：发布成功。
- 发布包：`CanVariableMonitor\release\上位机监控_V1.79_20260614_224434.zip`
- 更新 manifest：`CanVariableMonitor\release\update_manifest.json`
- 发布目录 exe 可启动，标题为 `上位机监控 V1.79`。
- 日志路径：`%APPDATA%\CanVariableMonitor\diagnostic.log`

## 3. V1.66 到 V1.79 的关键脉络

### V1.66：界面重组与业务链路

- 用户明确：原来程序透视仍在左侧，不要把函数位置单独做成右侧大图。
- `4 函数位置` 的能力应融合进 `1 程序透视`。
- `2 看数值` 与代码查看合并，代码旁边显示实时值。
- 删除独立 `3 看程序` 的主交互意义，避免两套代码窗口互相抢刷新。
- 刷新按钮承担重读工程、重读 map/axf/bin、固件检查和程序理解重建。

### V1.67：丝滑性能

- RichTextBox 全量 RTF 重绘会导致闪、卡、自动跳回。
- 改用 Scintilla 作为代码区，代码文本只在切换文件/刷新源码时加载。
- UI 刷新必须变成调度器：后台采集/离线模拟写快照，UI 80-120ms 合并刷新。
- 滚轮期间不要重绘整段代码，不要 `ScrollToCaret()` 抢滚动位置。

### V1.68-V1.70：值层和高亮收口

- 不能用 Scintilla 文本选区表现定位，否则会出现从第 1 行到目标函数整段高亮。
- 导航统一走单行 marker/indicator，并在程序主动导航后折叠 selection。
- 鼠标手动选择、双击选词、拖选文本时不能折叠 selection，否则 `Ctrl+C` 失效。
- 实时值层不能挡住代码。后续一度尝试换行和浮层，结论是要简单、稳定、同一行显示。

### V1.71：程序透视 E 风格与值颜色

- 左侧程序透视围绕当前位置显示，不让深层函数抢 C 位。
- 当前函数绿色即可，普通节点弱色。
- 实时值颜色不能受鼠标点击影响，只能由监控运行状态和值变化驱动。

### V1.72-V1.75：离线模拟重构方向

- 离线模式不能靠“当前代码窗口在哪里”来驱动执行，必须从应用入口跑。
- 应按真实应用层顺序：`MyLogic_10ms / work_logic` 后继续显示入口，如 `Disp_main` 或 `gp_lcdtask` 指向函数。
- 跨 `.c` 全局变量必须绑定到同一份模拟值，不能 `App_usr.c` 自增一份、`App_lcd.c` 清零另一份。
- 强制变量只固定变量值，程序仍要按真实调用链执行到对应语句。
- 引入 TinyCC/libtcc worker 的方向：不改客户源码，生成临时仿真工程，硬件函数 stub，业务函数真实执行。

### V1.77：左侧树线与层级

- 父子线要清楚，中灰实线，线宽随 DPI。
- 红菱形应为鲜粉色，当前函数只绿字/绿菱形，不铺整行背景。
- 子函数字体更弱、更小，可用斜体；父函数稍微加强。
- 高亮必须完整函数名匹配，大小写不敏感，不能因为部分字符相同误亮。

### V1.78：Keil 经典主题与 2 -> 1 联动

- 新增 Keil 经典主题：浅灰工具栏、浅米白代码区、关键字蓝、注释绿、函数名深紫蓝。
- 右侧代码光标/鼠标所在行映射到左侧函数树时，即使节点折叠，也要自动展开父链、居中并高亮。
- 鼠标在调用 token 上时只能弱提示被调用函数，C 位仍优先是当前行所属函数。

### V1.79：自动更新框架

- 软件启动后可以检查服务器 manifest。
- 本地 `update_config.json` 指向服务器 `update_manifest.json`。
- 服务器放 manifest 和最新 zip。
- 客户端下载 zip 后校验 SHA256，再启动外部 updater 替换文件并重启。
- 主 exe 不应直接覆盖自己。

## 4. 服务器自动更新记录

### 客户端需要带什么

交付给客户的发布目录里应包含真实的 `update_config.json`，客户不需要手动处理。当前仓库有 `update_config.example.json`，等服务器 IP/端口确定后生成真实文件并打包进发布 zip。

示例：

```json
{
  "autoCheck": true,
  "autoInstall": false,
  "channel": "stable",
  "manifestUrl": "http://服务器IP:端口/update_manifest.json",
  "timeoutSeconds": 8
}
```

### 服务器需要放什么

服务器至少放两个文件：

```text
update_manifest.json
上位机监控_V1.79_20260614_224434.zip
```

后续如果发布 `V1.80`，服务器 manifest 里的 `version/packageUrl/sha256` 改成新包即可。

示例 manifest：

```json
{
  "version": "V1.79",
  "channel": "stable",
  "packageUrl": "上位机监控_V1.79_20260614_224434.zip",
  "sha256": "8b94be034371fb2d9dd0c7fd1863d345e7ee54f4c0f4225a09337f7315ee979e",
  "releaseNotes": "",
  "force": false
}
```

注意：

- `packageUrl` 可以是相对路径，表示和 manifest 在同一目录。
- 也可以是完整 URL。
- 老客户如果还没装 V1.79，需要先手动装一次 V1.79，之后才能走自动更新闭环。

## 5. 不要再重复踩的坑

### UI 丝滑原则

- 代码区只能有一个主显示控件：`2 看数值` 内 Scintilla。
- 旧 `3 看程序` 不允许再参与刷新、BringToFront、定位、实时值渲染。
- 后台刷新只能更新值快照，不能改变滚动位置、光标、selection。
- 鼠标移动不能触发代码跳转。
- 自动跳转只允许来自用户主动操作：左侧节点、搜索结果、返回/下级、Ctrl+点击。
- 任何高亮都优先用 marker/indicator，不用文本选区。
- `Ctrl+C` 必须优先交给 Scintilla 原生复制。
- 手动选择和双击选词不能被保护逻辑折叠。

### 实时值显示原则

- 运行中才显示 `//值:【...】`，停止/断开/退出离线后恢复纯代码。
- 不要用全屏浮层遮住代码。
- 不要再做复杂自动换行，优先同一行稳定显示。
- 数值颜色要与当前主题明显区分，不能借用主题背景导致看不清。
- 鼠标点击、空白点击、选中代码不允许改变值颜色或触发闪烁。
- 如果值不更新，在线模式保留最后有效值，不显示“无响应”。

### 程序透视原则

- 左侧是业务位置导航，不是炫技图。
- 当前代码段所属函数永远是 C 位。
- 深层函数只在进入其函数体后才成为 C 位。
- 当前行调用的下级函数可以弱提示，但不能抢 C 位。
- 节点高亮必须完整函数名匹配。
- 右侧光标落到函数内时，左侧应自动展开父链、居中并绿色高亮。

### 在线监控原则

- PEAK / SYS / GC 不应该有不同业务策略，只是传输适配器。
- 联机成功必须以控制器回复为准，不是 CAN 设备打开成功。
- 当前可见变量优先轮询，不要补满 100 个拖慢刷新。
- 没有新回复时保留最后有效值，不显示“无响应”。
- 死变量或无回复变量不能拖慢整屏。

### 离线模拟原则

- 离线模式不是代码阅读窗口驱动，而是应用入口驱动。
- `MyLogic_10ms / work_logic` 与显示链路要在同一个应用 tick 视角里执行。
- 所有跨 `.c` 全局变量必须共享同一身份，优先 map/axf 地址，没有地址时只允许全工程唯一名 fallback。
- 强制变量只固定变量值，不能让程序跳过真实调用链。
- 离线强制不能把 `__canmon_apply_forces()` 插进用户函数内部语句之间，避免破坏 C 语法。
- worker 生成临时 C 文件时，目录必须按进程/worker 唯一，避免多个 worker 抢同一个 `canmon_tick.c`。
- 无法解释的宏、指针、结构体、硬件寄存器写入要标记“离线未覆盖”，不能假装执行成功。

## 6. 后续客户反馈记录模板

以后客户提优化意见，建议追加在本文件下面，格式如下：

```text
### YYYY-MM-DD - 客户反馈标题

- 场景：
- 用户看到的现象：
- 期望行为：
- 涉及模块：
- 初步判断：
- 修改文件：
- 验证方式：
- 结果截图/日志：
- 是否应沉淀到 skill：
```

## 7. 修改前检查清单

每次开工前先做：

```powershell
git status --short
dotnet build "C:\Users\DELL\Documents\遥控器的杂碎事情\CanVariableMonitor\CanVariableMonitor.csproj"
```

如果改 UI：

- 启动发布目录 exe。
- 截图确认窗口不闪、不跳、不遮挡。
- 快速滚轮 10 秒。
- 点击左侧函数、右侧代码、搜索结果、返回/下级。
- 检查 `%APPDATA%\CanVariableMonitor\diagnostic.log` 没有刷屏。

如果改离线：

- 测强制、释放、跨 `.c` 变量。
- 测 `MyLogic_10ms -> ZY_logic -> KM_NO` 这种真实链路。
- 测显示链 `Disp_main -> mainFrame`。
- 确认没有 TinyCC `identifier expected`、临时 C 被重复覆盖、变量每拍重置。

如果改发布：

- `dotnet build`
- `PublishUnified.ps1`
- 启动 F 盘发布 exe
- 确认版本标题
- 检查 release zip 和 `update_manifest.json`

## 8. 当前已知风险

- 离线 C worker 方向是对的，但仍需要针对真实客户工程做更多覆盖测试。
- UI 实时值层经历过多次方案切换，后续任何改动都要先守住“不闪、不挡、不抢滚动”。
- 自动更新 V1.79 有框架，但还缺真实服务器地址和正式 `update_config.json`。
- 客户老版本无法自己获得自动更新能力，需要先手动安装一次带自动更新能力的版本。

## 9. 关联全局 skill

已使用并更新的全局经验位置：

```text
C:\Users\DELL\.codex\skills\can-upper-computer-debugging\SKILL.md
```

这份 `WORK_HANDOFF_V1.79.md` 记录项目现场状态；skill 记录以后做类似 CAN 上位机时必须遵守的经验规则。
