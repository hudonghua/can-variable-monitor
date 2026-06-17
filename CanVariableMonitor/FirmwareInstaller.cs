using System.Text;

using System.Diagnostics;

namespace CanVariableMonitor;

internal class FirmwareInstallResult
{
    public bool Success { get; set; }
    public List<string> Messages { get; } = new();
}

internal sealed class FirmwareBinaryResult : FirmwareInstallResult
{
    public string BinPath { get; set; } = "";
}

internal static class FirmwareInstaller
{
    private const string AgentFileName = "can_monitor_agent.c";
    private const long MinimumUsableBinSize = 1024;
    private const int ArtifactTimeToleranceSeconds = 3;
    private readonly record struct XmlTagBlock(int Start, int End, string Text);

    public static FirmwareInstallResult Install(string projectRoot, string agentSourcePath)
    {
        var result = new FirmwareInstallResult();

        if (!Directory.Exists(projectRoot))
        {
            result.Messages.Add("工程目录不存在。");
            return result;
        }

        KeilProjectSelection projectSelection = SelectProjectFile(projectRoot);
        result.Messages.AddRange(projectSelection.Messages);
        string? projectFile = projectSelection.ProjectFile;
        if (projectFile == null)
        {
            return result;
        }

        string projectDir = Path.GetDirectoryName(projectFile)!;
        string targetName = FindBuildTarget(projectFile) ?? "";
        ProjectSourceScan sourceScan = ScanProjectSources(projectRoot, projectFile, targetName);
        result.Messages.AddRange(sourceScan.Messages);

        string srcDir = FindSourceDirectory(projectRoot, projectDir);

        string agentDest = Path.Combine(srcDir, AgentFileName);
        InstallPlan installPlan = FindInstallPlan(projectRoot, sourceScan.ActiveCustomerCFiles);
        string? receiveFile = installPlan.FilePath;
        uint bundledAgentVersion = ReadAgentVersion(agentSourcePath);
        uint installedAgentVersion = File.Exists(agentDest) ? ReadAgentVersion(agentDest) : 0;
        bool agentFileExists = File.Exists(agentDest);
        bool projectHasAgent = sourceScan.ActiveTargetHasAgent;
        bool projectHasAgentAnywhere = sourceScan.ProjectHasAgentAnywhere;
        bool agentVersionIsCurrent = agentFileExists && bundledAgentVersion != 0 && installedAgentVersion == bundledAgentVersion;
        FirmwareShape shape = AnalyzeProject(projectRoot, installPlan);
        HookAudit hookAudit = AnalyzeHooks(projectRoot, sourceScan.ActiveCustomerCFiles);
        BusinessGatePlan businessGatePlan = FindBusinessGatePlan(projectRoot, sourceScan.ActiveCustomerCFiles);
        bool processHasCall = hookAudit.ProcessCalls > 0;
        if (processHasCall && !shape.HasReceiveFile)
        {
            shape.MarkReceiveSatisfiedByExistingHook();
        }
        bool businessGateAtPreferredEntry = businessGatePlan.Found &&
            BusinessGateExistsInFunction(businessGatePlan.FilePath!, businessGatePlan.FunctionName);
        bool businessGateNeedsRelocation = hookAudit.BusinessGateCalls > 0 &&
            businessGatePlan.Found &&
            !businessGateAtPreferredEntry;
        bool businessGateNeedsInstall = businessGatePlan.Found &&
            (hookAudit.BusinessGateCalls == 0 || businessGateNeedsRelocation);
        var backups = new List<(string Original, string Backup)>();
        var createdFiles = new List<string>();

        result.Messages.Add($"固件代理版本：工程 {FormatVersion(installedAgentVersion)}，软件内置 {FormatVersion(bundledAgentVersion)}。");
        result.Messages.Add(shape.Summary);
        result.Messages.Add(hookAudit.Summary);
        if (projectHasAgentAnywhere && !projectHasAgent)
        {
            result.Messages.Add("检测到工程文件里存在固件代理，但不在当前 Target，本次会按当前 Target 重新校正。");
        }
        if (!string.IsNullOrWhiteSpace(installPlan.Description))
        {
            result.Messages.Add("安装方案：" + installPlan.Description);
        }
        if (!string.IsNullOrWhiteSpace(businessGatePlan.Description))
        {
            result.Messages.Add("业务强制方案：" + businessGatePlan.Description);
        }
        if (businessGateNeedsRelocation)
        {
            result.Messages.Add("注意：现有业务门控不在首选业务入口，本次将迁移到 " + businessGatePlan.FunctionName + "()。");
        }
        else if (hookAudit.BusinessGateCalls > 1)
        {
            result.Messages.Add("注意：检测到多个 CanMonitor_BusinessGate()，本次不会继续增加；建议最终只保留一个业务入口门控。");
        }
        result.Messages.Add("业务位置追踪：刷新同步默认不插入 CanMonitor_Trace，避免修改过多业务代码。");

        result.Messages.Add("固件预检：开始调用 Keil 编译原工程，确认客户代码当前状态。");
        BuildCheck beforeBuild = BuildProject(projectFile);
        result.Messages.Add(beforeBuild.Message);
        bool canRepairMonitorLinkFailure = !beforeBuild.Success &&
            IsMonitorLinkOnlyFailure(beforeBuild) &&
            (hookAudit.ProcessCalls > 0 || hookAudit.BusinessGateCalls > 0 || agentFileExists || projectHasAgentAnywhere);
        if (!beforeBuild.Success && !canRepairMonitorLinkFailure)
        {
            result.Messages.Add("预检未通过：原工程当前 Keil 编译有问题，未修改客户工程。");
            result.Success = false;
            return result;
        }
        if (canRepairMonitorLinkFailure)
        {
            result.Messages.Add("检测到旧监控钩子链接失败，本次只允许进入监控固件修复模式。");
        }

        if (!shape.IsCompatible)
        {
            foreach (string missing in shape.MissingItems)
            {
                result.Messages.Add("未安装固件：" + missing);
            }
            result.Messages.Add("该工程 CAN 接口与当前低耦合代理不匹配，未修改客户工程。");
            result.Success = false;
            return result;
        }

        if (agentFileExists && projectHasAgent && processHasCall && !businessGateNeedsInstall && agentVersionIsCurrent)
        {
            result.Messages.Add("检测到该工程固件代理已是最新，本次未做任何修改。");
            BinCheck verifyBin = ConfirmCurrentTargetBin(projectFile, beforeBuild.TargetName, beforeBuild.StartedUtc, requireFresh: false);
            result.Messages.AddRange(verifyBin.Messages);
            result.Success = verifyBin.Success;
            if (verifyBin.Success)
            {
                result.Messages.Add("固件源码已同步，当前 Target bin 已确认。");
            }
            return result;
        }

        if (!agentFileExists)
        {
            Directory.CreateDirectory(srcDir);
            File.WriteAllBytes(agentDest, PrepareAgentBytes(agentSourcePath, shape));
            createdFiles.Add(agentDest);
            installedAgentVersion = bundledAgentVersion;
            agentVersionIsCurrent = true;
            result.Messages.Add("已复制固件代理：" + agentDest);
        }
        else
        {
            if (!agentVersionIsCurrent)
            {
                backups.Add((agentDest, Backup(agentDest)));
                Directory.CreateDirectory(srcDir);
                File.WriteAllBytes(agentDest, PrepareAgentBytes(agentSourcePath, shape));
                installedAgentVersion = bundledAgentVersion;
                agentVersionIsCurrent = true;
                result.Messages.Add("已升级固件代理到最新版本：" + agentDest);
            }
            else
            {
                result.Messages.Add("固件代理已是最新，未覆盖：" + agentDest);
            }
        }

        if (!projectHasAgent)
        {
            AgentProjectUpdateResult projectUpdate = BuildAgentProjectUpdate(projectFile, projectDir, agentDest, targetName);
            if (!projectUpdate.Success)
            {
                Rollback(backups, createdFiles);
                result.Messages.Add(projectUpdate.Message);
                result.Messages.Add("固件安装未完成：没有把 can_monitor_agent.c 加入当前 Keil Target，已停止，避免继续生成无效备份。");
                result.Success = false;
                return result;
            }

            if (projectUpdate.Added)
            {
                backups.Add((projectFile, Backup(projectFile)));
                File.WriteAllText(projectFile, projectUpdate.UpdatedProjectText, Encoding.Default);
            }
            result.Messages.Add(projectUpdate.Message);
        }
        else
        {
            result.Messages.Add("Keil 当前 Target 中已存在固件文件，未重复添加。");
        }

        if (receiveFile == null && !processHasCall)
        {
            result.Messages.Add("没有自动找到安全的 CAN 后台轮询入口，请手工调用 CanMonitor_Process()。");
            result.Success = false;
            return result;
        }

        if (!processHasCall)
        {
            backups.Add((receiveFile!, Backup(receiveFile!)));
            int callInsertCount = InsertProcessCall(installPlan);
            if (callInsertCount <= 0)
            {
                Rollback(backups, createdFiles);
                result.Messages.Add("没有找到可安全插入 CanMonitor_Process 的接收位置，已回滚。");
                result.Success = false;
                return result;
            }
            result.Messages.Add(callInsertCount > 0
                ? $"已插入 CanMonitor_Process 调用：{receiveFile}，{callInsertCount} 处。"
                : "接收函数中已存在 CanMonitor_Process 调用。");
        }
        else
        {
            result.Messages.Add($"已检测到 CanMonitor_Process 调用 {hookAudit.ProcessCalls} 处，未重复添加。");
        }

        if (businessGateNeedsInstall)
        {
            if (businessGateNeedsRelocation)
            {
                foreach (string gateFile in FindFilesWithBusinessGate(projectRoot))
                {
                    BackupOnce(backups, gateFile);
                }
                int removed = RemoveStandardBusinessGateCalls(projectRoot);
                result.Messages.Add($"已移除旧业务门控：{removed} 处。");
            }

            BackupOnce(backups, businessGatePlan.FilePath!);
            int gateInsertCount = InsertBusinessGateCall(businessGatePlan);
            if (gateInsertCount <= 0)
            {
                Rollback(backups, createdFiles);
                result.Messages.Add("没有找到可安全插入 CanMonitor_BusinessGate 的业务入口，已回滚。");
                result.Success = false;
                return result;
            }

            result.Messages.Add($"已插入业务强制/单步门控：{businessGatePlan.FilePath}，{gateInsertCount} 处。");
        }
        else if (hookAudit.BusinessGateCalls > 0)
        {
            result.Messages.Add($"已检测到 CanMonitor_BusinessGate 调用 {hookAudit.BusinessGateCalls} 处，未重复添加。");
        }
        else
        {
            result.Messages.Add("未识别到安全业务入口，变量读写可用；强制/单步需要手工接入 CanMonitor_BusinessGate()。");
        }

        result.Messages.Add("未自动插入业务实时位置标记；需要实时位置时再由专门功能确认后开启。");

        result.Messages.Add("低耦合安装：未修改 CAN ID 初始化函数。");

        BuildCheck afterBuild = BuildProject(projectFile);
        result.Messages.Add(afterBuild.Message);
        if (!afterBuild.Success)
        {
            Rollback(backups, createdFiles);
            result.Messages.Add("固件安装后 Keil 编译未通过，已自动恢复安装前状态。");
            result.Success = false;
            return result;
        }

        BinCheck binCheck = ConfirmCurrentTargetBin(projectFile, afterBuild.TargetName, afterBuild.StartedUtc, requireFresh: true);
        result.Messages.AddRange(binCheck.Messages);
        if (!binCheck.Success)
        {
            Rollback(backups, createdFiles);
            result.Messages.Add("固件安装后未确认当前 Target 的正常 bin，已自动恢复安装前状态。");
            result.Success = false;
            return result;
        }

        result.Messages.Add("安装完成，Keil 编译验证通过，当前 Target bin 已确认。");
        result.Success = true;
        return result;
    }

    public static FirmwareBinaryResult BuildBinaryForDownload(string projectRoot)
    {
        var result = new FirmwareBinaryResult();

        if (!Directory.Exists(projectRoot))
        {
            result.Messages.Add("工程目录不存在。");
            return result;
        }

        KeilProjectSelection projectSelection = SelectProjectFile(projectRoot);
        result.Messages.AddRange(projectSelection.Messages);
        string? projectFile = projectSelection.ProjectFile;
        if (projectFile == null)
        {
            return result;
        }

        BuildCheck build = BuildProject(projectFile);
        result.Messages.Add(build.Message);
        if (!build.Success)
        {
            result.Messages.Add("Keil 编译未通过，未生成下载文件。");
            return result;
        }

        BinCheck binCheck = ConfirmCurrentTargetBin(projectFile, build.TargetName, build.StartedUtc, requireFresh: false);
        result.Messages.AddRange(binCheck.Messages);
        if (binCheck.Success)
        {
            result.BinPath = binCheck.BinPath!;
            result.Success = true;
            return result;
        }

        result.Messages.Add("Keil 编译完成，但没有确认当前 Target 的正常 .bin 文件。");
        result.Messages.Add("下载必须使用 Keil 已生成的 bin，上位机不会从 axf 另行转换。");
        return result;
    }

    internal static bool TryConfirmCurrentProjectBin(string projectRoot, out string message)
    {
        message = "";
        if (!Directory.Exists(projectRoot))
        {
            message = "工程目录不存在";
            return false;
        }

        KeilProjectSelection selection = SelectProjectFile(projectRoot);
        if (selection.ProjectFile == null)
        {
            message = selection.Messages.LastOrDefault() ?? "未找到 Keil 工程";
            return false;
        }

        string? target = FindBuildTarget(selection.ProjectFile);
        if (target == null)
        {
            message = "未解析到 Keil Target";
            return false;
        }

        BinCheck binCheck = ConfirmCurrentTargetBin(selection.ProjectFile, target, DateTime.UtcNow, requireFresh: false);
        message = binCheck.Messages.LastOrDefault() ?? (binCheck.Success ? "bin已确认" : "bin未确认");
        return binCheck.Success;
    }

    private sealed class BinCheck
    {
        public bool Success { get; set; }
        public string? BinPath { get; set; }
        public List<string> Messages { get; } = new();
    }

    private static BinCheck ConfirmCurrentTargetBin(string projectFile, string targetName, DateTime buildStartedUtc, bool requireFresh)
    {
        var result = new BinCheck();
        if (targetName.Length == 0)
        {
            result.Messages.Add("没有解析到当前 Keil Target，无法确认 bin。");
            return result;
        }

        TargetOutputInfo? output = FindTargetOutputInfo(projectFile, targetName);
        if (output == null)
        {
            result.Messages.Add("没有解析到当前 Target 输出配置，无法确认 bin。");
            return result;
        }

        string projectDir = Path.GetDirectoryName(projectFile)!;
        string outputName = output.OutputName.Length > 0 ? output.OutputName : Path.GetFileNameWithoutExtension(projectFile);
        string outputDir = ResolveProjectPath(projectDir, output.OutputDirectory);
        string listingDir = ResolveProjectPath(projectDir, output.ListingPath);
        List<string> candidates = BuildCurrentTargetBinCandidates(projectDir, output, outputName);
        if (candidates.Count == 0)
        {
            result.Messages.Add("当前 Target 没有可推导的 bin 输出路径。");
            return result;
        }

        result.Messages.Add("当前 Target：" + targetName + "，期望 bin：" + candidates[0]);
        string axfPath = Path.Combine(outputDir, outputName + ".axf");
        string mapPath = Path.Combine(outputDir, outputName + ".map");
        if (!File.Exists(mapPath) && listingDir.Length > 0)
        {
            string listingMap = Path.Combine(listingDir, outputName + ".map");
            if (File.Exists(listingMap))
            {
                mapPath = listingMap;
            }
        }

        var failures = new List<string>();
        foreach (string candidate in candidates)
        {
            if (TryConfirmBinFile(candidate, axfPath, mapPath, buildStartedUtc, requireFresh, out string message))
            {
                result.Success = true;
                result.BinPath = candidate;
                result.Messages.Add(message);
                return result;
            }

            failures.Add(message);
        }

        foreach (string failure in failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
        {
            result.Messages.Add(failure);
        }
        return result;
    }

    private static bool TryConfirmBinFile(string binPath, string axfPath, string mapPath, DateTime buildStartedUtc, bool requireFresh, out string message)
    {
        if (!File.Exists(binPath))
        {
            message = "未找到当前 Target bin：" + binPath;
            return false;
        }

        var binInfo = new FileInfo(binPath);
        if (binInfo.Length < MinimumUsableBinSize)
        {
            message = "当前 Target bin 过小，未认为是正常固件：" + binPath + "，大小 " + binInfo.Length + " 字节。";
            return false;
        }

        if (!File.Exists(axfPath))
        {
            message = "未找到同名 axf，未确认 bin 来源：" + axfPath;
            return false;
        }

        if (!File.Exists(mapPath))
        {
            message = "未找到同名 map，未确认 bin 来源：" + mapPath;
            return false;
        }

        DateTime binUtc = binInfo.LastWriteTimeUtc;
        DateTime axfUtc = File.GetLastWriteTimeUtc(axfPath);
        DateTime mapUtc = File.GetLastWriteTimeUtc(mapPath);
        DateTime tolerance = binUtc.AddSeconds(ArtifactTimeToleranceSeconds);
        if (requireFresh && binUtc < buildStartedUtc.AddSeconds(-ArtifactTimeToleranceSeconds))
        {
            message = "当前 Target bin 不是本次编译生成：" + binPath;
            return false;
        }

        if (tolerance < axfUtc)
        {
            message = "当前 Target bin 早于 axf，可能是旧固件：" + binPath;
            return false;
        }

        if (tolerance < mapUtc)
        {
            message = "当前 Target bin 早于 map，可能是旧固件：" + binPath;
            return false;
        }

        message = "当前 Target bin 已确认：" + binPath + "，大小 " + binInfo.Length + " 字节。";
        return true;
    }

    private static List<string> BuildCurrentTargetBinCandidates(string projectDir, TargetOutputInfo output, string outputName)
    {
        var candidates = new List<string>();
        AddCandidate(output.OutputDirectory);
        AddCandidate(output.ListingPath);

        return candidates
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        void AddCandidate(string dir)
        {
            if (dir.Length == 0)
            {
                return;
            }

            string resolved = ResolveProjectPath(projectDir, dir);
            candidates.Add(Path.Combine(resolved, outputName + ".bin"));
        }
    }

    private static string? FindCurrentTargetBin(string projectFile, string targetName)
    {
        if (targetName.Length == 0)
        {
            return null;
        }

        TargetOutputInfo? output = FindTargetOutputInfo(projectFile, targetName);
        if (output == null)
        {
            return null;
        }

        string projectDir = Path.GetDirectoryName(projectFile)!;
        string outputDir = ResolveProjectPath(projectDir, output.OutputDirectory);
        string outputName = output.OutputName.Length > 0 ? output.OutputName : Path.GetFileNameWithoutExtension(projectFile);
        return Path.Combine(outputDir, outputName + ".bin");
    }

    private sealed class TargetOutputInfo
    {
        public string OutputDirectory { get; init; } = "";
        public string ListingPath { get; init; } = "";
        public string OutputName { get; init; } = "";
    }

    private static TargetOutputInfo? FindTargetOutputInfo(string projectFile, string targetName)
    {
        string text = File.ReadAllText(projectFile, Encoding.Default);
        foreach (XmlTagBlock target in EnumerateTopLevelTagBlocks(text, "Target"))
        {
            string block = target.Text;
            string name = ExtractTagValue(block, "TargetName");
            if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            {
                return new TargetOutputInfo
                {
                    OutputDirectory = ExtractTagValue(block, "OutputDirectory"),
                    ListingPath = ExtractTagValue(block, "ListingPath"),
                    OutputName = ExtractTagValue(block, "OutputName")
                };
            }
        }
        return null;
    }

    private static string ExtractTagValue(string text, string tag)
    {
        string open = "<" + tag + ">";
        string close = "</" + tag + ">";
        int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        start += open.Length;
        int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return "";
        }

        return System.Net.WebUtility.HtmlDecode(text.Substring(start, end - start)).Trim();
    }

    private static IEnumerable<XmlTagBlock> EnumerateTopLevelTagBlocks(string text, string tag)
    {
        string open = "<" + tag + ">";
        string close = "</" + tag + ">";
        int search = 0;
        while (true)
        {
            int start = text.IndexOf(open, search, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                yield break;
            }

            int position = start + open.Length;
            int depth = 1;
            while (depth > 0)
            {
                int nextOpen = text.IndexOf(open, position, StringComparison.OrdinalIgnoreCase);
                int nextClose = text.IndexOf(close, position, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0)
                {
                    yield break;
                }

                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    depth++;
                    position = nextOpen + open.Length;
                    continue;
                }

                depth--;
                position = nextClose + close.Length;
            }

            yield return new XmlTagBlock(start, position, text.Substring(start, position - start));
            search = position;
        }
    }

    private static string ResolveProjectPath(string projectDir, string path)
    {
        path = path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (path.Length == 0)
        {
            return projectDir;
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectDir, path));
    }

    private sealed class KeilProjectSelection
    {
        public string? ProjectFile { get; set; }
        public List<string> Messages { get; } = new();
    }

    private sealed class KeilProjectCandidate
    {
        public string Path { get; init; } = "";
        public string TargetName { get; init; } = "";
        public string OutputName { get; init; } = "";
        public int Score { get; init; }
        public int ExistingSourceCount { get; init; }
        public bool HasTargetBin { get; init; }
        public bool HasTargetAxf { get; init; }
        public bool HasTargetMap { get; init; }
        public DateTime LastWriteUtc { get; init; }
    }

    private sealed class ProjectSourceScan
    {
        public int AllSourceFileCount { get; set; }
        public int ActiveSourceFileCount { get; set; }
        public bool ProjectHasAgentAnywhere { get; set; }
        public bool ActiveTargetHasAgent { get; set; }
        public List<string> ActiveCustomerCFiles { get; } = new();
        public List<string> Messages { get; } = new();
    }

    private static ProjectSourceScan ScanProjectSources(string root, string projectFile, string targetName)
    {
        var scan = new ProjectSourceScan();
        string projectDir = Path.GetDirectoryName(projectFile)!;

        try
        {
            scan.AllSourceFileCount = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(IsProjectSourceFile)
                .Where(p => !IsIgnoredSourcePath(p))
                .Count();
        }
        catch (Exception ex)
        {
            scan.Messages.Add("源码全盘扫描失败：" + ex.Message);
        }

        string projectText;
        try
        {
            projectText = File.ReadAllText(projectFile, Encoding.Default);
        }
        catch (Exception ex)
        {
            scan.Messages.Add("读取 Keil 工程失败：" + ex.Message);
            return scan;
        }

        scan.ProjectHasAgentAnywhere = projectText.Contains(AgentFileName, StringComparison.OrdinalIgnoreCase);
        string targetBlock = FindTargetBlockText(projectText, targetName);
        if (targetBlock.Length == 0)
        {
            scan.Messages.Add("固件预检扫描：未解析到当前 Target 源码成员，将退回全工程扫描。");
            return scan;
        }

        foreach (string filePath in ExtractTagValues(targetBlock, "FilePath"))
        {
            if (!IsProjectSourcePath(filePath))
            {
                continue;
            }

            string fullPath = ResolveProjectPath(projectDir, filePath);
            if (!File.Exists(fullPath) || IsIgnoredSourcePath(fullPath))
            {
                continue;
            }

            scan.ActiveSourceFileCount++;
            if (Path.GetFileName(fullPath).Equals(AgentFileName, StringComparison.OrdinalIgnoreCase))
            {
                scan.ActiveTargetHasAgent = true;
                continue;
            }

            if (fullPath.EndsWith(".c", StringComparison.OrdinalIgnoreCase) && IsCustomerSourceFile(fullPath))
            {
                scan.ActiveCustomerCFiles.Add(fullPath);
            }
        }

        scan.ActiveTargetHasAgent |= targetBlock.Contains(AgentFileName, StringComparison.OrdinalIgnoreCase);
        scan.Messages.Add($"固件预检扫描：全目录源码/头文件 {scan.AllSourceFileCount} 个，当前 Target 源码成员 {scan.ActiveSourceFileCount} 个，业务 C 文件 {scan.ActiveCustomerCFiles.Count} 个。");
        scan.Messages.Add("当前 Target 固件代理：" + Mark(scan.ActiveTargetHasAgent) + "。");
        if (scan.ActiveSourceFileCount == 0)
        {
            scan.Messages.Add("注意：当前 Target 没有解析到源码成员，安装器会更保守地处理该工程。");
        }

        return scan;
    }

    private static string FindTargetBlockText(string projectText, string targetName)
    {
        foreach (XmlTagBlock target in EnumerateTopLevelTagBlocks(projectText, "Target"))
        {
            string block = target.Text;
            string name = ExtractTagValue(block, "TargetName");
            if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }
        return "";
    }

    private static bool IsProjectSourceFile(string path)
    {
        return IsProjectSourcePath(path);
    }

    private static bool IsProjectSourcePath(string path)
    {
        return path.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".s", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredSourcePath(string path)
    {
        string normalized = path.Replace('/', '\\');
        string name = Path.GetFileName(normalized);
        if (name.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".saved", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("canmon_bak", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~", StringComparison.Ordinal))
        {
            return true;
        }

        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part =>
            part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("release", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
            part.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
            part.Contains("备份", StringComparison.OrdinalIgnoreCase));
    }

    private static KeilProjectSelection SelectProjectFile(string root)
    {
        var selection = new KeilProjectSelection();
        string[] allProjects;
        try
        {
            allProjects = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(IsKeilProjectFile)
                .ToArray();
        }
        catch (Exception ex)
        {
            selection.Messages.Add("扫描 Keil 工程失败：" + ex.Message);
            return selection;
        }

        string[] usableProjects = allProjects
            .Where(p => !IsIgnoredProjectFile(p))
            .ToArray();
        var candidates = usableProjects
            .Select(EvaluateKeilProject)
            .Where(c => c.TargetName.Length > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => GetPathDepth(root, c.Path))
            .ThenByDescending(c => c.LastWriteUtc)
            .ToList();

        selection.Messages.Add($"工程扫描：找到 {allProjects.Length} 个 Keil 工程，排除 {allProjects.Length - usableProjects.Length} 个备份/临时工程。");
        if (candidates.Count == 0)
        {
            selection.Messages.Add("没有找到可用的 .uvproj 或 .uvprojx。");
            return selection;
        }

        foreach (KeilProjectCandidate candidate in candidates.Take(4))
        {
            string rel = GetRelativePathSafe(root, candidate.Path);
            string artifacts =
                "bin " + Mark(candidate.HasTargetBin) +
                "，axf " + Mark(candidate.HasTargetAxf) +
                "，map " + Mark(candidate.HasTargetMap);
            selection.Messages.Add($"候选工程：{rel}，Target {candidate.TargetName}，输出 {candidate.OutputName}，源码 {candidate.ExistingSourceCount} 个，{artifacts}，评分 {candidate.Score}。");
        }

        KeilProjectCandidate selected = candidates[0];
        selection.ProjectFile = selected.Path;
        selection.Messages.Add("已选择 Keil 工程：" + selected.Path);
        return selection;
    }

    private static bool IsKeilProjectFile(string path)
    {
        return path.EndsWith(".uvproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".uvprojx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredProjectFile(string path)
    {
        string normalized = path.Replace('/', '\\');
        string name = Path.GetFileName(normalized);
        if (name.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".saved", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("canmon_bak", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~", StringComparison.Ordinal))
        {
            return true;
        }

        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part =>
            part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("release", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
            part.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
            part.Contains("备份", StringComparison.OrdinalIgnoreCase));
    }

    private static KeilProjectCandidate EvaluateKeilProject(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.Default);
        }
        catch
        {
            return new KeilProjectCandidate { Path = path };
        }

        string targetName = FindBuildTargetInText(text) ?? "";
        TargetOutputInfo? output = targetName.Length > 0 ? FindTargetOutputInfo(path, targetName) : null;
        string projectDir = Path.GetDirectoryName(path)!;
        string outputName = output?.OutputName.Length > 0 ? output.OutputName : Path.GetFileNameWithoutExtension(path);
        string outputDir = output == null ? projectDir : ResolveProjectPath(projectDir, output.OutputDirectory);
        string listingDir = output == null ? projectDir : ResolveProjectPath(projectDir, output.ListingPath);
        string binPath = Path.Combine(outputDir, outputName + ".bin");
        string axfPath = Path.Combine(outputDir, outputName + ".axf");
        string mapPath = Path.Combine(outputDir, outputName + ".map");
        if (!File.Exists(mapPath) && listingDir.Length > 0)
        {
            string listingMap = Path.Combine(listingDir, outputName + ".map");
            if (File.Exists(listingMap))
            {
                mapPath = listingMap;
            }
        }

        int existingSourceCount = CountExistingProjectSourceFiles(projectDir, text);
        bool hasTargetBin = File.Exists(binPath);
        bool hasTargetAxf = File.Exists(axfPath);
        bool hasTargetMap = File.Exists(mapPath);
        int score = 0;
        if (targetName.Length > 0)
        {
            score += 100;
        }
        if (targetName.Contains("FLASH", StringComparison.OrdinalIgnoreCase))
        {
            score += 120;
        }
        if (path.EndsWith(".uvprojx", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }
        if (output != null)
        {
            score += 40;
        }
        if (outputName.Length > 0)
        {
            score += 20;
        }
        if (Directory.Exists(outputDir))
        {
            score += 20;
        }
        if (hasTargetBin)
        {
            score += 25;
        }
        if (hasTargetAxf)
        {
            score += 15;
        }
        if (hasTargetMap)
        {
            score += 15;
        }
        score += Math.Min(70, existingSourceCount);
        if (text.Contains("main.c", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }
        if (text.Contains("MyLogic_10ms", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("work_logic", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }
        if (text.Contains(AgentFileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        return new KeilProjectCandidate
        {
            Path = path,
            TargetName = targetName,
            OutputName = outputName,
            Score = score,
            ExistingSourceCount = existingSourceCount,
            HasTargetBin = hasTargetBin,
            HasTargetAxf = hasTargetAxf,
            HasTargetMap = hasTargetMap,
            LastWriteUtc = File.GetLastWriteTimeUtc(path)
        };
    }

    private static int CountExistingProjectSourceFiles(string projectDir, string projectText)
    {
        int count = 0;
        foreach (string filePath in ExtractTagValues(projectText, "FilePath"))
        {
            if (!filePath.EndsWith(".c", StringComparison.OrdinalIgnoreCase) &&
                !filePath.EndsWith(".h", StringComparison.OrdinalIgnoreCase) &&
                !filePath.EndsWith(".s", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fullPath = ResolveProjectPath(projectDir, filePath);
            if (File.Exists(fullPath))
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> ExtractTagValues(string text, string tag)
    {
        string open = "<" + tag + ">";
        string close = "</" + tag + ">";
        int pos = 0;
        while (true)
        {
            int start = text.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                yield break;
            }

            start += open.Length;
            int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                yield break;
            }

            yield return System.Net.WebUtility.HtmlDecode(text.Substring(start, end - start)).Trim();
            pos = end + close.Length;
        }
    }

    private static int GetPathDepth(string root, string path)
    {
        string rel = GetRelativePathSafe(root, path);
        return rel.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
    }

    private static string GetRelativePathSafe(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }

    private static string Mark(bool value) => value ? "OK" : "缺失";

    private static string? FindLatestAxfFile(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, "*.axf", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).Contains(".bak", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string FindSourceDirectory(string root, string projectDir)
    {
        string direct = Path.Combine(projectDir, "Src");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        string? appCan = Directory.EnumerateFiles(root, "App_Can.c", SearchOption.AllDirectories).FirstOrDefault();
        if (appCan != null)
        {
            return Path.GetDirectoryName(appCan)!;
        }

        string? canOpen = Directory.EnumerateFiles(root, "CanOpen.c", SearchOption.AllDirectories).FirstOrDefault();
        if (canOpen != null)
        {
            return Path.GetDirectoryName(canOpen)!;
        }

        return direct;
    }

    private enum InstallPlanKind
    {
        None,
        UsrCanReceive,
        CanReceiveScheduler
    }

    private sealed class InstallPlan
    {
        public string? FilePath { get; set; }
        public InstallPlanKind Kind { get; set; }
        public string Anchor { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Found => FilePath != null && Kind != InstallPlanKind.None;
    }

    private static InstallPlan FindInstallPlan(string root, IReadOnlyList<string>? preferredSourceFiles = null)
    {
        string[] sourceFiles = GetCustomerCFiles(root, preferredSourceFiles);

        string? appCan = sourceFiles.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("App_Can.c", StringComparison.OrdinalIgnoreCase) &&
            ContainsAscii(p, "Usr_Can_Rcv") &&
            ContainsAscii(p, "CAN1_Get_Data"));
        if (appCan != null)
        {
            return new InstallPlan
            {
                FilePath = appCan,
                Kind = InstallPlanKind.UsrCanReceive,
                Anchor = "CAN1_Get_Data",
                Description = "识别到 App_Can.c 的用户 CAN 接收轮询，按接收函数挂载。"
            };
        }

        string? usrReceive = sourceFiles.FirstOrDefault(p =>
            ContainsAscii(p, "Usr_Can_Rcv") &&
            ContainsAscii(p, "CAN1_Get_Data"));
        if (usrReceive != null)
        {
            return new InstallPlan
            {
                FilePath = usrReceive,
                Kind = InstallPlanKind.UsrCanReceive,
                Anchor = "CAN1_Get_Data",
                Description = "识别到用户 CAN 接收轮询函数，按接收函数挂载。"
            };
        }

        string? scheduler = sourceFiles.FirstOrDefault(p =>
            ContainsAscii(p, "CAN1RxDone") &&
            ContainsAscii(p, "Can_Prog_Rcv"));
        if (scheduler != null)
        {
            return new InstallPlan
            {
                FilePath = scheduler,
                Kind = InstallPlanKind.CanReceiveScheduler,
                Anchor = "CAN1RxDone",
                Description = "识别到 CAN 收包后台调度点，按后台轮询函数挂载。"
            };
        }

        string? canDataScheduler = sourceFiles.FirstOrDefault(p =>
            ContainsAscii(p, "CAN1RxDone") &&
            ContainsAscii(p, "CAN1_Get_Data"));
        if (canDataScheduler != null)
        {
            return new InstallPlan
            {
                FilePath = canDataScheduler,
                Kind = InstallPlanKind.CanReceiveScheduler,
                Anchor = "CAN1RxDone",
                Description = "识别到 CAN 接收标志处理点，按后台轮询函数挂载。"
            };
        }

        return new InstallPlan
        {
            Description = "已扫描客户源码，但没有找到安全的 CAN 后台轮询入口。"
        };
    }

    private static bool IsCustomerSourceFile(string path)
    {
        string name = Path.GetFileName(path);
        return !name.Equals(AgentFileName, StringComparison.OrdinalIgnoreCase) &&
            !name.Contains(".bak", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("canmon_bak", StringComparison.OrdinalIgnoreCase) &&
            !IsIgnoredSourcePath(path);
    }

    private static string[] GetCustomerCFiles(string root, IReadOnlyList<string>? preferredSourceFiles = null)
    {
        IEnumerable<string> files = preferredSourceFiles != null && preferredSourceFiles.Count > 0
            ? preferredSourceFiles
            : Directory.EnumerateFiles(root, "*.c", SearchOption.AllDirectories);

        return files
            .Where(IsCustomerSourceFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(SourceFilePriority)
            .ThenBy(p => p.Length)
            .ToArray();
    }

    private static int SourceFilePriority(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("App_Can.c", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (name.Equals("App_sys.c", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("App_Sys.c", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (name.Equals("main.c", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (name.Contains("can", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        return 9;
    }

    private sealed class HookAudit
    {
        public int ProcessCalls { get; set; }
        public int BusinessGateCalls { get; set; }
        public int TraceCalls { get; set; }

        public string Summary =>
            "固件钩子检查：后台轮询 " + ProcessCalls +
            " 处，业务门控 " + BusinessGateCalls +
            " 处，位置标记 " + TraceCalls + " 处。";
    }

    private sealed class BusinessGatePlan
    {
        public string? FilePath { get; set; }
        public string FunctionName { get; set; } = "";
        public string Anchor { get; set; } = "";
        public int InsertPosition { get; set; } = -1;
        public string Description { get; set; } = "";
        public bool Found => FilePath != null && InsertPosition >= 0;
    }

    private static HookAudit AnalyzeHooks(string root, IReadOnlyList<string>? preferredSourceFiles = null)
    {
        var audit = new HookAudit();
        foreach (string file in GetCustomerCFiles(root, preferredSourceFiles))
        {
            audit.ProcessCalls += CountNonExternLineOccurrences(file, "CanMonitor_Process();");
            audit.BusinessGateCalls += CountNonExternLineOccurrences(file, "CanMonitor_BusinessGate()");
            audit.TraceCalls += CountNonExternLineOccurrences(file, "CanMonitor_Trace(");
        }

        return audit;
    }

    private static int CountNonExternLineOccurrences(string file, string pattern)
    {
        int count = 0;
        foreach (string line in File.ReadLines(file, Encoding.Default))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("extern ", StringComparison.Ordinal))
            {
                continue;
            }

            int pos = 0;
            while (true)
            {
                int hit = line.IndexOf(pattern, pos, StringComparison.Ordinal);
                if (hit < 0)
                {
                    break;
                }

                count++;
                pos = hit + pattern.Length;
            }
        }

        return count;
    }

    private static BusinessGatePlan FindBusinessGatePlan(string root, IReadOnlyList<string>? preferredSourceFiles = null)
    {
        string[] sourceFiles = GetCustomerCFiles(root, preferredSourceFiles);

        string[] entryFunctions =
        {
            "MyLogic_10ms",
            "app10ms",
            "App_10ms",
            "app_Ctrl",
            "work_logic",
            "MyLogic_1ms"
        };

        string[] businessAnchors =
        {
            "work_logic",
            "ASS_logic",
            "app_Ctrl",
            "App_JiTing",
            "app_Logic",
            "app_ctrl_dfs",
            "APP_DO_DFS"
        };

        foreach (string entry in entryFunctions)
        {
            foreach (string file in sourceFiles)
            {
                BusinessGatePlan plan = TryCreateBusinessGatePlan(file, entry, businessAnchors);
                if (plan.Found)
                {
                    return plan;
                }
            }
        }

        return new BusinessGatePlan
        {
            Description = "已扫描 MyLogic_10ms/MyLogic_1ms 等入口，但没有找到可自动插入的安全业务门控位置。"
        };
    }

    private static BusinessGatePlan TryCreateBusinessGatePlan(string file, string functionName, string[] anchors)
    {
        byte[] data = File.ReadAllBytes(file);
        int openBrace = FindFunctionOpenBrace(data, functionName);
        if (openBrace < 0)
        {
            return new BusinessGatePlan();
        }

        int closeBrace = FindMatchingBrace(data, openBrace);
        if (closeBrace < 0)
        {
            return new BusinessGatePlan();
        }

        int existing = IndexOf(data, Encoding.ASCII.GetBytes("CanMonitor_BusinessGate"), openBrace);
        if (existing >= 0 && existing < closeBrace)
        {
            return new BusinessGatePlan
            {
                FilePath = file,
                FunctionName = functionName,
                Anchor = "CanMonitor_BusinessGate",
                InsertPosition = existing,
                Description = functionName + " 中已存在业务门控。"
            };
        }

        if (IsPreferredBusinessEntry(functionName))
        {
            int insertPos = FindAfterLocalDeclarations(data, openBrace);
            if (IsC90SafeInsertionPoint(data, openBrace, insertPos, closeBrace))
            {
                return new BusinessGatePlan
                {
                    FilePath = file,
                    FunctionName = functionName,
                    Anchor = functionName,
                    InsertPosition = insertPos,
                    Description = "识别到业务入口 " + functionName + "，将在函数入口挂业务强制/单步门控。"
                };
            }
        }

        foreach (string anchor in anchors)
        {
            if (anchor.Equals(functionName, StringComparison.Ordinal))
            {
                continue;
            }

            int anchorPos = FindCallInRange(data, anchor, openBrace, closeBrace);
            if (anchorPos < 0)
            {
                continue;
            }

            int insertPos = FindLineStart(data, anchorPos);
            if (!IsC90SafeInsertionPoint(data, openBrace, insertPos, closeBrace))
            {
                continue;
            }

            return new BusinessGatePlan
            {
                FilePath = file,
                FunctionName = functionName,
                Anchor = anchor,
                InsertPosition = insertPos,
                Description = "识别到 " + functionName + "，将在 " + anchor + "() 前挂业务强制/单步门控。"
            };
        }

        return new BusinessGatePlan();
    }

    private static bool IsPreferredBusinessEntry(string functionName)
    {
        return functionName.Equals("MyLogic_10ms", StringComparison.OrdinalIgnoreCase) ||
            functionName.Equals("app10ms", StringComparison.OrdinalIgnoreCase) ||
            functionName.Equals("App_10ms", StringComparison.OrdinalIgnoreCase) ||
            functionName.Equals("app_Ctrl", StringComparison.OrdinalIgnoreCase) ||
            functionName.Equals("work_logic", StringComparison.OrdinalIgnoreCase) ||
            functionName.Equals("MyLogic_1ms", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindCallInRange(byte[] data, string functionName, int start, int end)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(functionName);
        int pos = start;
        while (pos >= 0 && pos < end)
        {
            int name = IndexOf(data, nameBytes, pos);
            if (name < 0 || name >= end)
            {
                return -1;
            }

            if (!IsIdentifierBoundary(data, name - 1) || !IsIdentifierBoundary(data, name + nameBytes.Length))
            {
                pos = name + nameBytes.Length;
                continue;
            }

            int next = SkipWhiteSpace(data, name + nameBytes.Length);
            if (next < end && data[next] == (byte)'(')
            {
                return name;
            }

            pos = name + nameBytes.Length;
        }

        return -1;
    }

    private static bool IsC90SafeInsertionPoint(byte[] data, int openBrace, int insertPos, int closeBrace)
    {
        int depth = 1;
        int pos = FindLineEnd(data, openBrace);

        while (pos < closeBrace)
        {
            int lineEnd = FindLineEnd(data, pos);
            if (pos >= insertPos)
            {
                string line = Encoding.ASCII.GetString(data, pos, lineEnd - pos);
                string trimmed = line.Trim();
                if (depth == 1 && IsLocalDeclarationLine(trimmed))
                {
                    return false;
                }
            }

            for (int i = pos; i < lineEnd && i < closeBrace; i++)
            {
                if (data[i] == (byte)'{')
                {
                    depth++;
                }
                else if (data[i] == (byte)'}' && depth > 0)
                {
                    depth--;
                }
            }

            pos = lineEnd;
        }

        return true;
    }

    private sealed class TraceTarget
    {
        public required string FunctionName { get; init; }
        public required string FilePath { get; init; }
        public ushort TraceId { get; init; }
    }

    private static List<TraceTarget> FindTraceTargets(string root)
    {
        (string Name, ushort Id)[] preferred =
        {
            ("MyLogic_10ms", 0x2100),
            ("MyLogic_1ms", 0x2106),
            ("app_Ctrl", 0x2102),
            ("App_JiTing", 0x2101),
            ("app_Logic", 0x2103),
            ("app_ctrl_dfs", 0x2104),
            ("App_PWM", 0x2105)
        };

        string[] sourceFiles = Directory.EnumerateFiles(root, "*.c", SearchOption.AllDirectories)
            .Where(IsCustomerSourceFile)
            .OrderBy(SourceFilePriority)
            .ThenBy(p => p.Length)
            .ToArray();

        var result = new List<TraceTarget>();
        foreach ((string name, ushort id) in preferred)
        {
            string? file = sourceFiles.FirstOrDefault(path => FunctionDefinitionExists(path, name));
            if (file == null)
            {
                continue;
            }

            result.Add(new TraceTarget
            {
                FunctionName = name,
                FilePath = file,
                TraceId = id
            });
        }

        return result;
    }

    private static int CountMissingTraceCalls(List<TraceTarget> traceTargets)
    {
        int count = 0;
        foreach (TraceTarget target in traceTargets)
        {
            if (!TraceCallExists(target))
            {
                count++;
            }
        }
        return count;
    }

    private static bool FunctionDefinitionExists(string path, string functionName)
    {
        byte[] data = File.ReadAllBytes(path);
        return FindFunctionOpenBrace(data, functionName) >= 0;
    }

    private sealed class FirmwareShape
    {
        public bool HasReceiveFile { get; set; }
        public bool HasCanOpenHeader { get; set; }
        public bool HasCan1RBuf { get; set; }
        public bool HasCan1GetData { get; set; }
        public bool HasCanSendXLen { get; set; }
        public bool HasCan2RBuf { get; set; }
        public bool HasCan2GetData { get; set; }
        public bool HasCan2SendXLen { get; set; }
        public bool HasCan2ReceiveIdTable { get; set; }
        public bool HasReceiveIdTable { get; set; }
        public bool HasReceiveIdCount { get; set; }
        public List<string> MissingItems { get; } = new();
        public bool IsCompatible => MissingItems.Count == 0;
        public bool HasCan2Interface => HasCan2RBuf && HasCan2GetData && HasCan2SendXLen && HasCan2ReceiveIdTable;

        public string Summary =>
            "已自动读取工程代码：CAN接收入口 " + Mark(HasReceiveFile) +
            "，CanOpen.h " + Mark(HasCanOpenHeader) +
            "，接收缓冲 " + Mark(HasCan1RBuf) +
            "，接收函数 " + Mark(HasCan1GetData) +
            "，发送函数 " + Mark(HasCanSendXLen) +
            "，接收ID表 " + Mark(HasReceiveIdTable && HasReceiveIdCount) +
            "，CAN2可选支持 " + Mark(HasCan2Interface) + "。";

        public void MarkReceiveSatisfiedByExistingHook()
        {
            HasReceiveFile = true;
            MissingItems.RemoveAll(x => x.Contains("CAN 接收轮询函数", StringComparison.Ordinal));
        }

        private static string Mark(bool value) => value ? "OK" : "缺失";
    }

    private static FirmwareShape AnalyzeProject(string root, InstallPlan installPlan)
    {
        var shape = new FirmwareShape
        {
            HasReceiveFile = installPlan.Found
        };

        IEnumerable<string> sourceFiles = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p =>
                (p.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
                 p.EndsWith(".h", StringComparison.OrdinalIgnoreCase)) &&
                !Path.GetFileName(p).Contains(".bak", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(p).Contains("canmon_bak", StringComparison.OrdinalIgnoreCase) &&
                !IsIgnoredSourcePath(p));

        foreach (string file in sourceFiles)
        {
            string name = Path.GetFileName(file);
            shape.HasCanOpenHeader |= name.Equals("CanOpen.h", StringComparison.OrdinalIgnoreCase);
            shape.HasCan1RBuf |= ContainsAscii(file, "CAN1_RBuf");
            shape.HasCan1GetData |= ContainsAscii(file, "CAN1_Get_Data");
            shape.HasCanSendXLen |= ContainsAscii(file, "CAN_SendXLen");
            shape.HasCan2RBuf |= ContainsAscii(file, "CAN2_RBuf");
            shape.HasCan2GetData |= ContainsAscii(file, "CAN2_Get_Data");
            shape.HasCan2SendXLen |= ContainsAscii(file, "CAN2_SendXLen");
            shape.HasCan2ReceiveIdTable |= ContainsAscii(file, "gRcvCan2ID");
            shape.HasReceiveIdTable |= ContainsAscii(file, "gRcvCanID");
            shape.HasReceiveIdCount |= ContainsAscii(file, "ID_RCV_NUM");
        }

        if (!shape.HasReceiveFile)
        {
            shape.MissingItems.Add("没有自动找到 CAN 接收轮询函数。");
        }
        if (!shape.HasCanOpenHeader)
        {
            shape.MissingItems.Add("没有找到 CanOpen.h。");
        }
        if (!shape.HasCan1RBuf)
        {
            shape.MissingItems.Add("没有找到 CAN1_RBuf 接收数据缓冲。");
        }
        if (!shape.HasCan1GetData)
        {
            shape.MissingItems.Add("没有找到 CAN1_Get_Data 接收函数。");
        }
        if (!shape.HasCanSendXLen)
        {
            shape.MissingItems.Add("没有找到 CAN_SendXLen 发送函数。");
        }
        if (!shape.HasReceiveIdTable || !shape.HasReceiveIdCount)
        {
            shape.MissingItems.Add("没有找到 gRcvCanID/ID_RCV_NUM 接收 ID 表。");
        }

        return shape;
    }

    private static byte[] PrepareAgentBytes(string agentSourcePath, FirmwareShape shape)
    {
        string text = File.ReadAllText(agentSourcePath, Encoding.ASCII);
        string can2Value = shape.HasCan2Interface ? "1" : "0";
        text = text.Replace("#define CAN_MONITOR_ENABLE_CAN2 0", "#define CAN_MONITOR_ENABLE_CAN2 " + can2Value);
        return Encoding.ASCII.GetBytes(text);
    }

    private static bool ContainsAscii(string path, string text)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return IndexOf(bytes, Encoding.ASCII.GetBytes(text), 0) >= 0;
    }

    internal static uint ReadAgentVersion(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        byte[] bytes = File.ReadAllBytes(path);
        byte[] key = Encoding.ASCII.GetBytes("CAN_MONITOR_AGENT_VERSION");
        int pos = IndexOf(bytes, key, 0);
        if (pos < 0)
        {
            return 0;
        }

        int lineEnd = FindLineEnd(bytes, pos);
        string line = Encoding.ASCII.GetString(bytes, pos, lineEnd - pos);
        string[] parts = line.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (!parts[i].Equals("CAN_MONITOR_AGENT_VERSION", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 >= parts.Length)
            {
                return 0;
            }

            string value = parts[i + 1].TrimEnd('U', 'u', 'L', 'l');
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hexVersion))
            {
                return hexVersion;
            }

            if (uint.TryParse(value, out uint decVersion))
            {
                return decVersion;
            }
        }

        return 0;
    }

    internal static string FormatVersion(uint version)
    {
        return version == 0 ? "未知" : "0x" + version.ToString("X8");
    }

    private static string Backup(string path)
    {
        string backup = path + ".canmon_bak_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    private static void BackupOnce(List<(string Original, string Backup)> backups, string path)
    {
        if (backups.Any(x => x.Original.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        backups.Add((path, Backup(path)));
    }

    private static void Rollback(List<(string Original, string Backup)> backups, List<string> createdFiles)
    {
        foreach ((string original, string backup) in backups.AsEnumerable().Reverse())
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, original, overwrite: true);
            }
        }

        foreach (string created in createdFiles.AsEnumerable().Reverse())
        {
            if (File.Exists(created))
            {
                File.Delete(created);
            }
        }
    }

    private sealed class BuildCheck
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string TargetName { get; set; } = "";
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public string LogText { get; set; } = "";
        public string LogPath { get; set; } = "";
    }

    private static BuildCheck BuildProject(string projectFile)
    {
        string? uv4 = FindKeilUv4();
        if (uv4 == null)
        {
            return new BuildCheck
            {
                Success = false,
                Message = "未找到 Keil UV4.exe，未修改工程。"
            };
        }

        string? target = FindBuildTarget(projectFile);
        if (target == null)
        {
            return new BuildCheck
            {
                Success = false,
                Message = "没有解析到 Keil Target，未修改工程。"
            };
        }

        string projectDir = Path.GetDirectoryName(projectFile)!;
        string logPath = Path.Combine(projectDir, "canmon_verify_build.log");
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        using var process = new Process();
        process.StartInfo.FileName = uv4;
        process.StartInfo.Arguments = "-b \"" + projectFile + "\" -t \"" + target + "\" -o \"" + logPath + "\"";
        process.StartInfo.WorkingDirectory = projectDir;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        string? keilRoot = Path.GetDirectoryName(Path.GetDirectoryName(uv4)!);
        string armccBin = keilRoot == null ? "" : Path.Combine(keilRoot, "ARM", "ARMCC", "Bin");
        if (Directory.Exists(armccBin))
        {
            process.StartInfo.Environment["Path"] = armccBin + ";" + process.StartInfo.Environment["Path"];
        }

        DateTime startedUtc = DateTime.UtcNow;
        try
        {
            process.Start();
            if (!process.WaitForExit(180000))
            {
                try { process.Kill(); } catch { }
                return new BuildCheck
                {
                    Success = false,
                    TargetName = target,
                    StartedUtc = startedUtc,
                    LogText = "",
                    LogPath = logPath,
                    Message = "Keil 编译超时，未确认工程安全。日志：" + logPath
                };
            }
        }
        catch (Exception ex)
        {
            return new BuildCheck
            {
                Success = false,
                TargetName = target,
                StartedUtc = startedUtc,
                LogText = "",
                LogPath = logPath,
                Message = "启动 Keil 编译失败：" + ex.Message
            };
        }

        string logText = File.Exists(logPath) ? File.ReadAllText(logPath, Encoding.Default) : "";
        bool hasZeroError = logText.Contains("0 Error(s)", StringComparison.OrdinalIgnoreCase);
        bool hasError = logText.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("*** Error", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("Target not created", StringComparison.OrdinalIgnoreCase);

        if (hasZeroError && !hasError)
        {
            return new BuildCheck
            {
                Success = true,
                TargetName = target,
                StartedUtc = startedUtc,
                LogText = logText,
                LogPath = logPath,
                Message = "Keil 编译验证通过：" + target + "，日志：" + logPath
            };
        }

        return new BuildCheck
        {
            Success = false,
            TargetName = target,
            StartedUtc = startedUtc,
            LogText = logText,
            LogPath = logPath,
            Message = "Keil 编译验证失败，日志：" + logPath + "，关键错误：" + LastMeaningfulBuildLines(logText)
        };
    }

    private static bool IsMonitorLinkOnlyFailure(BuildCheck build)
    {
        string logText = build.LogText;
        if (string.IsNullOrWhiteSpace(logText))
        {
            return false;
        }

        bool hasMonitorUndefined =
            logText.Contains("Undefined symbol CanMonitor_Process", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("Undefined symbol CanMonitor_BusinessGate", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("Undefined symbol CanMonitor_Trace", StringComparison.OrdinalIgnoreCase);
        if (!hasMonitorUndefined)
        {
            return false;
        }

        string[] errorLines = logText.Replace("\r", "").Split('\n')
            .Select(x => x.Trim())
            .Where(IsKeilBuildErrorLine)
            .ToArray();
        return errorLines.Length > 0 && errorLines.All(line =>
            line.Contains("CanMonitor_", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Target not created", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error messages", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error(s)", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKeilBuildErrorLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        bool explicitError =
            line.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("*** Error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Undefined symbol", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Target not created", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error messages", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" Error(s)", StringComparison.OrdinalIgnoreCase);
        if (!explicitError)
        {
            return false;
        }

        return !line.Contains("0 Error(s)", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("0 errors", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindKeilUv4()
    {
        return KeilToolLocator.FindUv4();
    }

    private static string? FindBuildTarget(string projectFile)
    {
        string text = File.ReadAllText(projectFile, Encoding.Default);
        return FindBuildTargetInText(text);
    }

    private static string? FindBuildTargetInText(string text)
    {
        var targets = new List<string>();
        int pos = 0;
        while (true)
        {
            int start = text.IndexOf("<TargetName>", pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            start += "<TargetName>".Length;
            int end = text.IndexOf("</TargetName>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            string target = text.Substring(start, end - start).Trim();
            if (target.Length > 0)
            {
                targets.Add(target);
            }
            pos = end + "</TargetName>".Length;
        }

        return targets.FirstOrDefault(t => t.Contains("FLASH", StringComparison.OrdinalIgnoreCase)) ??
            targets.FirstOrDefault();
    }

    private static string LastMeaningfulBuildLines(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return "没有生成 Keil 编译日志。";
        }

        string[] allLines = logText.Replace("\r", "").Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        string[] important = allLines
            .Where(line =>
                IsKeilBuildErrorLine(line) ||
                line.Contains("Undefined symbol", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Target not created", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("not appear after executable statement", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("referred from", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12)
            .ToArray();

        string[] lines = important.Length > 0 ? important : allLines.TakeLast(8).ToArray();

        return string.Join(" | ", lines);
    }

    private sealed class AgentProjectUpdateResult
    {
        public bool Success { get; init; }
        public bool Added { get; init; }
        public string Message { get; init; } = "";
        public string UpdatedProjectText { get; init; } = "";
    }

    private static AgentProjectUpdateResult BuildAgentProjectUpdate(string projectFile, string projectDir, string agentPath, string targetName)
    {
        string text = File.ReadAllText(projectFile, Encoding.Default);
        string targetBlock = FindTargetBlockText(text, targetName);
        if (targetBlock.Length == 0)
        {
            return new AgentProjectUpdateResult
            {
                Success = false,
                Message = "未加入 Keil 当前 Target：无法解析完整 Target 块。"
            };
        }

        if (targetBlock.Contains(AgentFileName, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProjectUpdateResult
            {
                Success = true,
                Added = false,
                Message = "Keil 当前 Target 中已存在固件文件，未重复添加。"
            };
        }

        string relPath = MakeRelativeProjectPath(projectDir, agentPath);
        string entry =
            "\r\n            <File>" +
            "\r\n              <FileName>can_monitor_agent.c</FileName>" +
            "\r\n              <FileType>1</FileType>" +
            "\r\n              <FilePath>" + relPath + "</FilePath>" +
            "\r\n            </File>";

        int targetStart = text.IndexOf(targetBlock, StringComparison.Ordinal);
        if (targetStart < 0)
        {
            return new AgentProjectUpdateResult
            {
                Success = false,
                Message = "未加入 Keil 当前 Target：无法定位 Target 原文。"
            };
        }

        int insertInBlock = FindSourceGroupFilesEnd(targetBlock);
        if (insertInBlock < 0)
        {
            insertInBlock = targetBlock.LastIndexOf("</Files>", StringComparison.OrdinalIgnoreCase);
        }
        if (insertInBlock < 0)
        {
            return new AgentProjectUpdateResult
            {
                Success = false,
                Message = "未加入 Keil 当前 Target：没有找到可插入的源码 Files 节点。"
            };
        }

        text = text.Insert(targetStart + insertInBlock, entry);
        return new AgentProjectUpdateResult
        {
            Success = true,
            Added = true,
            Message = "已加入 Keil 当前 Target：can_monitor_agent.c。",
            UpdatedProjectText = text
        };
    }

    private static int FindSourceGroupFilesEnd(string targetBlock)
    {
        int pos = 0;
        while (true)
        {
            int groupStart = targetBlock.IndexOf("<Group>", pos, StringComparison.OrdinalIgnoreCase);
            if (groupStart < 0)
            {
                return -1;
            }

            int groupEnd = targetBlock.IndexOf("</Group>", groupStart, StringComparison.OrdinalIgnoreCase);
            if (groupEnd < 0)
            {
                return -1;
            }

            string groupBlock = targetBlock.Substring(groupStart, groupEnd - groupStart);
            string groupName = ExtractTagValue(groupBlock, "GroupName");
            if (groupName.Equals("Source Code", StringComparison.OrdinalIgnoreCase) ||
                groupName.Contains("source", StringComparison.OrdinalIgnoreCase) ||
                groupName.Contains("源码", StringComparison.OrdinalIgnoreCase))
            {
                int filesEnd = targetBlock.IndexOf("</Files>", groupStart, StringComparison.OrdinalIgnoreCase);
                if (filesEnd >= 0 && filesEnd < groupEnd)
                {
                    return filesEnd;
                }
            }

            pos = groupEnd + "</Group>".Length;
        }
    }

    private static string MakeRelativeProjectPath(string projectDir, string filePath)
    {
        string rel = Path.GetRelativePath(projectDir, filePath).Replace('/', '\\');
        if (!rel.StartsWith(".", StringComparison.Ordinal))
        {
            rel = ".\\" + rel;
        }
        return rel;
    }

    private static int InsertProcessCall(InstallPlan installPlan)
    {
        if (installPlan.FilePath == null)
        {
            return 0;
        }

        string receiveFile = installPlan.FilePath;
        byte[] data = File.ReadAllBytes(receiveFile);
        byte[] callPattern = Encoding.ASCII.GetBytes("CanMonitor_Process();");
        bool alreadyHasCall = IndexOf(data, callPattern, 0) >= 0;
        int insertCount = 0;

        if (IndexOf(data, Encoding.ASCII.GetBytes("extern void CanMonitor_Process(void);"), 0) < 0)
        {
            byte[] externBytes = Encoding.ASCII.GetBytes("extern void CanMonitor_Process(void);\r\n");
            data = InsertBytes(data, 0, externBytes);
        }

        if (!alreadyHasCall)
        {
            if (installPlan.Kind == InstallPlanKind.UsrCanReceive)
            {
                byte[] funcPattern = Encoding.ASCII.GetBytes("Usr_Can_Rcv");
                int pos = 0;
                while (true)
                {
                    int func = IndexOf(data, funcPattern, pos);
                    if (func < 0)
                    {
                        break;
                    }

                    int nextFunc = IndexOf(data, funcPattern, func + funcPattern.Length);
                    int scopeEnd = nextFunc > 0 ? nextFunc : data.Length;
                    int getData = IndexOf(data, Encoding.ASCII.GetBytes("CAN1_Get_Data"), func);
                    if (getData > 0 && getData < scopeEnd)
                    {
                        int lineStart = FindLineStart(data, getData);
                        byte[] callBytes = Encoding.ASCII.GetBytes("\tCanMonitor_Process();\r\n");
                        data = InsertBytes(data, lineStart, callBytes);
                        insertCount++;
                        pos = lineStart + callBytes.Length + 1;
                    }
                    else
                    {
                        pos = func + funcPattern.Length;
                    }
                }
            }
            else if (installPlan.Kind == InstallPlanKind.CanReceiveScheduler)
            {
                int anchor = IndexOf(data, Encoding.ASCII.GetBytes(installPlan.Anchor), 0);
                int openBrace = anchor >= 0 ? FindTopLevelOpenBraceBefore(data, anchor) : -1;
                if (openBrace >= 0)
                {
                    int insertPos = FindAfterLocalDeclarations(data, openBrace);
                    byte[] callBytes = Encoding.ASCII.GetBytes("\tCanMonitor_Process();\r\n");
                    data = InsertBytes(data, insertPos, callBytes);
                    insertCount++;
                }
            }
        }

        File.WriteAllBytes(receiveFile, data);
        return insertCount;
    }

    private static int InsertBusinessGateCall(BusinessGatePlan plan)
    {
        if (plan.FilePath == null || plan.InsertPosition < 0)
        {
            return 0;
        }

        string file = plan.FilePath;
        byte[] data = File.ReadAllBytes(file);
        int openBrace = FindFunctionOpenBrace(data, plan.FunctionName);
        if (openBrace < 0)
        {
            return 0;
        }

        int closeBrace = FindMatchingBrace(data, openBrace);
        if (closeBrace < 0)
        {
            return 0;
        }

        int existing = IndexOf(data, Encoding.ASCII.GetBytes("CanMonitor_BusinessGate"), openBrace);
        if (existing >= 0 && existing < closeBrace)
        {
            return 0;
        }

        string call =
            "\tif(!CanMonitor_BusinessGate())\r\n" +
            "\t{\r\n" +
            "\t\treturn;\r\n" +
            "\t}\r\n\r\n";
        data = InsertBytes(data, plan.InsertPosition, Encoding.ASCII.GetBytes(call));

        if (IndexOf(data, Encoding.ASCII.GetBytes("extern unsigned char CanMonitor_BusinessGate(void);"), 0) < 0)
        {
            byte[] externBytes = Encoding.ASCII.GetBytes("extern unsigned char CanMonitor_BusinessGate(void);\r\n");
            data = InsertBytes(data, 0, externBytes);
        }

        File.WriteAllBytes(file, data);
        return 1;
    }

    private static bool BusinessGateExistsInFunction(string file, string functionName)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(functionName) || !File.Exists(file))
        {
            return false;
        }

        byte[] data = File.ReadAllBytes(file);
        int openBrace = FindFunctionOpenBrace(data, functionName);
        if (openBrace < 0)
        {
            return false;
        }

        int closeBrace = FindMatchingBrace(data, openBrace);
        if (closeBrace < 0)
        {
            return false;
        }

        int existing = IndexOf(data, Encoding.ASCII.GetBytes("CanMonitor_BusinessGate"), openBrace);
        return existing >= 0 && existing < closeBrace;
    }

    private static IEnumerable<string> FindFilesWithBusinessGate(string root)
    {
        return Directory.EnumerateFiles(root, "*.c", SearchOption.AllDirectories)
            .Where(IsCustomerSourceFile)
            .Where(path => ContainsAscii(path, "CanMonitor_BusinessGate"));
    }

    private static int RemoveStandardBusinessGateCalls(string root)
    {
        int removed = 0;
        byte[][] patterns =
        {
            Encoding.ASCII.GetBytes("\tif(!CanMonitor_BusinessGate())\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n"),
            Encoding.ASCII.GetBytes("\tif(!CanMonitor_BusinessGate())\n\t{\n\t\treturn;\n\t}\n\n"),
            Encoding.ASCII.GetBytes("\tif (!CanMonitor_BusinessGate())\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n"),
            Encoding.ASCII.GetBytes("\tif (!CanMonitor_BusinessGate())\n\t{\n\t\treturn;\n\t}\n\n")
        };

        foreach (string file in FindFilesWithBusinessGate(root))
        {
            byte[] data = File.ReadAllBytes(file);
            int fileRemoved = 0;
            foreach (byte[] pattern in patterns)
            {
                while (true)
                {
                    int pos = IndexOf(data, pattern, 0);
                    if (pos < 0)
                    {
                        break;
                    }

                    data = RemoveBytes(data, pos, pattern.Length);
                    fileRemoved++;
                }
            }

            if (fileRemoved > 0)
            {
                File.WriteAllBytes(file, data);
                removed += fileRemoved;
            }
        }

        return removed;
    }

    private static bool TraceCallExists(TraceTarget target)
    {
        byte[] data = File.ReadAllBytes(target.FilePath);
        int openBrace = FindFunctionOpenBrace(data, target.FunctionName);
        if (openBrace < 0)
        {
            return true;
        }

        int closeBrace = FindMatchingBrace(data, openBrace);
        if (closeBrace < 0)
        {
            closeBrace = data.Length;
        }

        string needle = "CanMonitor_Trace(" + FormatTraceId(target.TraceId) + ")";
        int pos = IndexOf(data, Encoding.ASCII.GetBytes("CanMonitor_Trace"), openBrace);
        return pos >= 0 && pos < closeBrace &&
            IndexOf(data, Encoding.ASCII.GetBytes(needle), openBrace) >= 0 &&
            IndexOf(data, Encoding.ASCII.GetBytes(needle), openBrace) < closeBrace;
    }

    private static int InsertTraceCalls(List<TraceTarget> traceTargets)
    {
        int total = 0;
        foreach (IGrouping<string, TraceTarget> group in traceTargets.GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            byte[] data = File.ReadAllBytes(group.Key);
            if (IndexOf(data, Encoding.ASCII.GetBytes("extern void CanMonitor_Trace(unsigned short traceId);"), 0) < 0)
            {
                byte[] externBytes = Encoding.ASCII.GetBytes("extern void CanMonitor_Trace(unsigned short traceId);\r\n");
                data = InsertBytes(data, 0, externBytes);
            }

            var inserts = new List<(int Position, TraceTarget Target)>();
            foreach (TraceTarget target in group)
            {
                int openBrace = FindFunctionOpenBrace(data, target.FunctionName);
                if (openBrace < 0)
                {
                    continue;
                }

                int closeBrace = FindMatchingBrace(data, openBrace);
                if (closeBrace < 0)
                {
                    continue;
                }

                string needle = "CanMonitor_Trace(" + FormatTraceId(target.TraceId) + ")";
                int existing = IndexOf(data, Encoding.ASCII.GetBytes(needle), openBrace);
                if (existing >= 0 && existing < closeBrace)
                {
                    continue;
                }

                int insertPos = FindAfterLocalDeclarations(data, openBrace);
                inserts.Add((insertPos, target));
            }

            foreach ((int position, TraceTarget target) in inserts.OrderByDescending(x => x.Position))
            {
                string call = "\tCanMonitor_Trace(" + FormatTraceId(target.TraceId) + ");\r\n";
                data = InsertBytes(data, position, Encoding.ASCII.GetBytes(call));
                total++;
            }

            File.WriteAllBytes(group.Key, data);
        }

        return total;
    }

    private static string FormatTraceId(ushort traceId)
    {
        return "0x" + traceId.ToString("X4");
    }

    private static int FindFunctionOpenBrace(byte[] data, string functionName)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(functionName);
        int pos = 0;
        while (true)
        {
            int name = IndexOf(data, nameBytes, pos);
            if (name < 0)
            {
                return -1;
            }

            if (!IsIdentifierBoundary(data, name - 1) || !IsIdentifierBoundary(data, name + nameBytes.Length))
            {
                pos = name + nameBytes.Length;
                continue;
            }

            int next = SkipWhiteSpace(data, name + nameBytes.Length);
            if (next >= data.Length || data[next] != (byte)'(')
            {
                pos = name + nameBytes.Length;
                continue;
            }

            int closeParen = FindClosingParen(data, next);
            if (closeParen < 0)
            {
                return -1;
            }

            int afterParen = SkipWhiteSpace(data, closeParen + 1);
            if (afterParen < data.Length && data[afterParen] == (byte)'{')
            {
                return afterParen;
            }

            pos = closeParen + 1;
        }
    }

    private static int FindClosingParen(byte[] data, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < data.Length; i++)
        {
            if (data[i] == (byte)'(')
            {
                depth++;
            }
            else if (data[i] == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindMatchingBrace(byte[] data, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < data.Length; i++)
        {
            if (data[i] == (byte)'{')
            {
                depth++;
            }
            else if (data[i] == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int SkipWhiteSpace(byte[] data, int index)
    {
        int i = index;
        while (i < data.Length && (data[i] == (byte)' ' || data[i] == (byte)'\t' || data[i] == (byte)'\r' || data[i] == (byte)'\n'))
        {
            i++;
        }
        return i;
    }

    private static bool IsIdentifierBoundary(byte[] data, int index)
    {
        if (index < 0 || index >= data.Length)
        {
            return true;
        }

        byte c = data[index];
        return !((c >= (byte)'a' && c <= (byte)'z') ||
                 (c >= (byte)'A' && c <= (byte)'Z') ||
                 (c >= (byte)'0' && c <= (byte)'9') ||
                 c == (byte)'_');
    }

    private static int FindTopLevelOpenBraceBefore(byte[] data, int index)
    {
        int depth = 0;
        int lastOpen = -1;
        for (int i = 0; i < index && i < data.Length; i++)
        {
            if (data[i] == (byte)'{')
            {
                if (depth == 0)
                {
                    lastOpen = i;
                }
                depth++;
            }
            else if (data[i] == (byte)'}' && depth > 0)
            {
                depth--;
            }
        }
        return lastOpen;
    }

    private static int FindAfterLocalDeclarations(byte[] data, int openBrace)
    {
        int pos = FindLineEnd(data, openBrace);
        int insertPos = pos;

        while (pos < data.Length)
        {
            int lineEnd = FindLineEnd(data, pos);
            string line = Encoding.ASCII.GetString(data, pos, lineEnd - pos);
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || IsCommentOnlyLine(trimmed))
            {
                insertPos = lineEnd;
                pos = lineEnd;
                continue;
            }

            if (IsLocalDeclarationLine(trimmed))
            {
                insertPos = lineEnd;
                pos = lineEnd;
                continue;
            }

            break;
        }

        return insertPos;
    }

    private static bool IsCommentOnlyLine(string trimmed)
    {
        return trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/*", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal) ||
            trimmed.StartsWith("*/", StringComparison.Ordinal);
    }

    private static bool IsLocalDeclarationLine(string trimmed)
    {
        if (!trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        string[] executablePrefixes =
        {
            "if", "for", "while", "switch", "return", "do", "break", "continue",
            "goto", "case", "default"
        };
        string firstToken = FirstToken(trimmed);
        foreach (string prefix in executablePrefixes)
        {
            if (firstToken.Equals(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        string[] declarationPrefixes =
        {
            "auto", "register", "static", "extern", "const", "volatile",
            "signed", "unsigned", "char", "short", "int", "long", "float", "double",
            "void", "struct", "union", "enum",
            "uint8_t", "uint16_t", "uint32_t", "int8_t", "int16_t", "int32_t",
            "UINT8", "UINT16", "UINT32", "INT8", "INT16", "INT32",
            "u8", "u16", "u32", "s8", "s16", "s32",
            "BOOL", "bool"
        };
        foreach (string prefix in declarationPrefixes)
        {
            if (firstToken.Equals(prefix, StringComparison.Ordinal) ||
                firstToken.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        int semicolon = trimmed.IndexOf(';');
        int paren = trimmed.IndexOf('(');
        return semicolon >= 0 && (paren < 0 || paren > semicolon) && !trimmed.Contains("=", StringComparison.Ordinal);
    }

    private static string FirstToken(string text)
    {
        int i = 0;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }
        return text.Substring(0, i);
    }

    private static int FindLineStart(byte[] data, int index)
    {
        int i = index;
        while (i > 0 && data[i - 1] != (byte)'\n')
        {
            i--;
        }
        return i;
    }

    private static int FindLineEnd(byte[] data, int index)
    {
        int i = index;
        while (i < data.Length && data[i] != (byte)'\n')
        {
            i++;
        }

        return i < data.Length ? i + 1 : data.Length;
    }

    private static byte[] InsertBytes(byte[] source, int index, byte[] insert)
    {
        byte[] output = new byte[source.Length + insert.Length];
        Buffer.BlockCopy(source, 0, output, 0, index);
        Buffer.BlockCopy(insert, 0, output, index, insert.Length);
        Buffer.BlockCopy(source, index, output, index + insert.Length, source.Length - index);
        return output;
    }

    private static byte[] RemoveBytes(byte[] source, int index, int count)
    {
        if (index < 0 || count <= 0 || index >= source.Length)
        {
            return source;
        }

        count = Math.Min(count, source.Length - index);
        byte[] output = new byte[source.Length - count];
        Buffer.BlockCopy(source, 0, output, 0, index);
        Buffer.BlockCopy(source, index + count, output, index, source.Length - index - count);
        return output;
    }

    private static int IndexOf(byte[] source, byte[] pattern, int start)
    {
        for (int i = Math.Max(0, start); i <= source.Length - pattern.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int LastIndexOf(byte[] source, byte[] pattern)
    {
        for (int i = source.Length - pattern.Length; i >= 0; i--)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return i;
        }
        return -1;
    }
}
