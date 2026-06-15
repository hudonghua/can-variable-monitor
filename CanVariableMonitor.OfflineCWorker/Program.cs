using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CanVariableMonitor.OfflineCWorker;

internal static class Program
{
    private const int TinyCcTickTimeoutMs = 12000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly WorkerState State = new();

    public static int Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            WorkerRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<WorkerRequest>(line, JsonOptions);
                if (request == null)
                {
                    continue;
                }

                WorkerResult result = Handle(request);
                WriteResult(result);
                if (request.Command.Equals("Shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
            }
            catch (Exception ex)
            {
                WriteResult(new WorkerResult
                {
                    Id = request?.Id ?? 0,
                    Ok = false,
                    EngineAvailable = State.EngineAvailable,
                    Status = "离线 C worker 异常。",
                    Error = ex.Message,
                    Coverage = State.Coverage.Take(20).ToList()
                });
            }
        }

        return 0;
    }

    private static WorkerResult Handle(WorkerRequest request)
    {
        return request.Command switch
        {
            "InitProject" => InitProject(request),
            "RunTick" => RunTick(request),
            "ReadSnapshot" => ReadSnapshot(request),
            "WriteVariable" => WriteVariable(request, force: false),
            "ForceVariable" => WriteVariable(request, force: true),
            "ReleaseVariable" => ReleaseVariable(request),
            "GetCoverage" => Result(request, true, "离线 C worker 覆盖信息。", State.Values),
            "Shutdown" => Result(request, true, "离线 C worker 已退出。", State.Values),
            _ => Result(request, false, "未知命令：" + request.Command, State.Values)
        };
    }

    private static WorkerResult InitProject(WorkerRequest request)
    {
        OfflineWorkerProjectPayload? project = request.Payload.HasValue
            ? request.Payload.Value.Deserialize<OfflineWorkerProjectPayload>(JsonOptions)
            : null;
        if (project == null)
        {
            return Result(request, false, "InitProject 参数为空。", State.Values);
        }

        State.Project = project;
        State.Signature = project.Signature;
        State.Values.Clear();
        State.ForceValues.Clear();
        State.OneShotForceValues.Clear();
        State.Coverage.Clear();
        foreach (OfflineWorkerVariablePayload variable in project.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Key))
            {
                continue;
            }
            State.Values[variable.Key] = Mask(variable.RawValue, variable.Size);
            if (variable.ForceActive)
            {
                State.ForceValues[variable.Key] = Mask(variable.RawValue, variable.Size);
            }
        }

        State.TinyCcPath = TinyCcLocator.Resolve();
        State.EngineAvailable = State.TinyCcPath.Length > 0;
        if (!State.EngineAvailable)
        {
            State.Coverage.Add("离线未覆盖：未找到 tinycc/tcc.exe。");
            return Result(request, false, "TinyCC 内核不可用：发布目录缺少 tinycc\\tcc.exe。", State.Values);
        }

        State.WorkDirectory = PrepareWorkDirectory(project);
        State.Coverage.Add("TinyCC: " + State.TinyCcPath);
        State.Coverage.Add("仿真函数: " + project.Sources.Count.ToString(CultureInfo.InvariantCulture));
        State.Coverage.Add("仿真变量: " + project.Variables.Count.ToString(CultureInfo.InvariantCulture));
        return Result(request, true, "TinyCC 离线 C worker 已初始化。", State.Values);
    }

    private static WorkerResult RunTick(WorkerRequest request)
    {
        if (State.Project == null)
        {
            return Result(request, false, "离线 C worker 尚未初始化。", State.Values);
        }
        if (!State.EngineAvailable || State.TinyCcPath.Length == 0)
        {
            return Result(request, false, "TinyCC 内核不可用，未执行离线 tick。", State.Values);
        }

        string sourcePath = Path.Combine(State.WorkDirectory, "canmon_tick.c");
        Dictionary<string, uint> activeForceValues = BuildActiveForceValues();
        string cSource = SimulationCGenerator.Generate(State.Project, State.Values, activeForceValues);
        SimulationCGenerator.WriteSupportFiles(State.WorkDirectory);
        foreach (string note in SimulationCGenerator.LastCoverageNotes)
        {
            AddCoverageOnce(note);
        }
        File.WriteAllText(sourcePath, cSource, new UTF8Encoding(false));

        string output;
        string error;
        int exitCode;
        try
        {
            (exitCode, output, error) = RunTinyCc(State.TinyCcPath, sourcePath, State.WorkDirectory, TinyCcTickTimeoutMs);
        }
        catch (Exception ex)
        {
            AddCoverageOnce("离线未覆盖：TinyCC 执行失败：" + ex.Message);
            return Result(request, false, "TinyCC 执行失败：" + ex.Message, State.Values);
        }

        if (exitCode != 0)
        {
            string compact = CompactError(error.Length > 0 ? error : output);
            AddCoverageOnce("离线未覆盖：TinyCC 编译/执行失败：" + compact);
            return Result(request, false, "离线未覆盖：TinyCC 编译/执行失败：" + compact, State.Values);
        }

        Dictionary<string, uint> nextValues = ParseTickOutput(output);
        if (nextValues.Count == 0)
        {
            AddCoverageOnce("离线未覆盖：tick 未返回变量快照。");
            return Result(request, false, "离线未覆盖：tick 未返回变量快照。", State.Values);
        }

        foreach ((string key, uint value) in nextValues)
        {
            OfflineWorkerVariablePayload? variable = State.Project.Variables.FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            int size = variable?.Size ?? 4;
            State.Values[key] = Mask(value, size);
        }
        foreach (string outputEvent in ParseOutputEvents(output).Take(24))
        {
            AddCoverageOnce("离线输出记录：" + outputEvent);
        }
        State.OneShotForceValues.Clear();

        return Result(request, true, "离线 C tick 已执行。", State.Values);
    }

    private static Dictionary<string, uint> BuildActiveForceValues()
    {
        Dictionary<string, uint> values = new(State.ForceValues, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, uint value) in State.OneShotForceValues)
        {
            values[key] = value;
        }
        return values;
    }

    private static WorkerResult ReadSnapshot(WorkerRequest request)
    {
        OfflineWorkerSnapshotRequest? snapshotRequest = request.Payload.HasValue
            ? request.Payload.Value.Deserialize<OfflineWorkerSnapshotRequest>(JsonOptions)
            : null;
        if (snapshotRequest != null)
        {
            foreach (OfflineWorkerVariablePayload variable in snapshotRequest.Variables)
            {
                RegisterRuntimeVariable(variable);
                if (!string.IsNullOrWhiteSpace(variable.Key) && !State.Values.ContainsKey(variable.Key))
                {
                    State.Values[variable.Key] = Mask(variable.RawValue, variable.Size);
                }
            }
        }

        return Result(request, true, "变量快照已读取。", State.Values);
    }

    private static WorkerResult WriteVariable(WorkerRequest request, bool force)
    {
        OfflineWorkerVariableWritePayload? payload = request.Payload.HasValue
            ? request.Payload.Value.Deserialize<OfflineWorkerVariableWritePayload>(JsonOptions)
            : null;
        if (payload == null || string.IsNullOrWhiteSpace(payload.Key))
        {
            return Result(request, false, "变量写入参数为空。", State.Values);
        }

        uint raw = Mask(payload.RawValue, payload.Size);
        RegisterRuntimeVariable(payload);
        State.Values[payload.Key] = raw;
        if (force)
        {
            State.ForceValues[payload.Key] = raw;
            State.OneShotForceValues.Remove(payload.Key);
        }
        else
        {
            State.OneShotForceValues[payload.Key] = raw;
        }
        return Result(request, true, force ? "变量已保持。" : "变量已写入一次。", State.Values);
    }

    private static WorkerResult ReleaseVariable(WorkerRequest request)
    {
        OfflineWorkerVariableWritePayload? payload = request.Payload.HasValue
            ? request.Payload.Value.Deserialize<OfflineWorkerVariableWritePayload>(JsonOptions)
            : null;
        if (payload == null || string.IsNullOrWhiteSpace(payload.Key))
        {
            return Result(request, false, "变量释放参数为空。", State.Values);
        }

        State.ForceValues.Remove(payload.Key);
        State.OneShotForceValues.Remove(payload.Key);
        return Result(request, true, "变量保持已释放。", State.Values);
    }

    private static void RegisterRuntimeVariable(OfflineWorkerVariablePayload variable)
    {
        if (State.Project == null || string.IsNullOrWhiteSpace(variable.Key))
        {
            return;
        }

        if (State.Project.Variables.Any(item => item.Key.Equals(variable.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        State.Project.Variables.Add(variable);
    }

    private static void RegisterRuntimeVariable(OfflineWorkerVariableWritePayload variable)
    {
        if (State.Project == null || string.IsNullOrWhiteSpace(variable.Key))
        {
            return;
        }

        if (State.Project.Variables.Any(item => item.Key.Equals(variable.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        State.Project.Variables.Add(new OfflineWorkerVariablePayload
        {
            Key = variable.Key,
            Name = variable.Name,
            Size = variable.Size,
            RawValue = variable.RawValue,
            Aliases = string.IsNullOrWhiteSpace(variable.Name)
                ? new List<string>()
                : new List<string> { variable.Name }
        });
    }

    private static WorkerResult Result(WorkerRequest request, bool ok, string status, IReadOnlyDictionary<string, uint> values)
    {
        return new WorkerResult
        {
            Id = request.Id,
            Ok = ok,
            EngineAvailable = State.EngineAvailable,
            Status = status,
            Error = ok ? "" : status,
            Values = values.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            Coverage = State.Coverage.TakeLast(32).ToList()
        };
    }

    private static void AddCoverageOnce(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        if (!State.Coverage.Contains(message, StringComparer.Ordinal))
        {
            State.Coverage.Add(message);
        }
        if (State.Coverage.Count > 96)
        {
            State.Coverage.RemoveRange(0, State.Coverage.Count - 96);
        }
    }

    private static void WriteResult(WorkerResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        Console.Out.Flush();
    }

    private static string PrepareWorkDirectory(OfflineWorkerProjectPayload project)
    {
        string signaturePart = SanitizePathPart(project.Signature.Length == 0 ? "default" : project.Signature);
        string instancePart = SanitizePathPart(State.InstanceId);
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CanVariableMonitor",
            "offline_c_worker",
            signaturePart + "_" + instancePart);
        Directory.CreateDirectory(root);
        return root;
    }

    private static (int ExitCode, string Output, string Error) RunTinyCc(string tccPath, string sourcePath, string workDirectory, int timeoutMs)
    {
        var start = new ProcessStartInfo(tccPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-run");
        start.ArgumentList.Add(sourcePath);
        using Process process = new() { StartInfo = start };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw new TimeoutException("tick 超时");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static Dictionary<string, uint> ParseTickOutput(string text)
    {
        var values = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("__CANMON__", StringComparison.Ordinal))
            {
                continue;
            }
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                continue;
            }
            if (uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value))
            {
                values[parts[1]] = value;
            }
        }
        return values;
    }

    private static IEnumerable<string> ParseOutputEvents(string text)
    {
        foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("__CANMON_OUTPUT__ ", StringComparison.Ordinal))
            {
                string name = line.Substring("__CANMON_OUTPUT__ ".Length).Trim();
                if (name.Length > 0)
                {
                    yield return name;
                }
            }
        }
    }

    private static uint Mask(uint value, int size)
    {
        int bytes = Math.Clamp(size, 1, 4);
        return bytes switch
        {
            1 => value & 0xFFu,
            2 => value & 0xFFFFu,
            3 => value & 0xFFFFFFu,
            _ => value
        };
    }

    private static string CompactError(string text)
    {
        string clean = Regex.Replace(text.Trim(), @"\s+", " ");
        return clean.Length <= 900 ? clean : clean[..900];
    }

    private static string SanitizePathPart(string text)
    {
        string clean = Regex.Replace(text, @"[^A-Za-z0-9_.-]+", "_");
        return clean.Length <= 80 ? clean : clean[..80];
    }
}

internal sealed class WorkerState
{
    public string InstanceId { get; } =
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];

    public OfflineWorkerProjectPayload? Project { get; set; }
    public string Signature { get; set; } = "";
    public string WorkDirectory { get; set; } = Path.GetTempPath();
    public string TinyCcPath { get; set; } = "";
    public bool EngineAvailable { get; set; }
    public Dictionary<string, uint> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, uint> ForceValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, uint> OneShotForceValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Coverage { get; } = new();
}

internal static class TinyCcLocator
{
    public static string Resolve()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "tinycc", "tcc.exe"),
            Path.Combine(baseDir, "tcc.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "tinycc", "tcc.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "tools", "tinycc", "tcc.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools", "tinycc", "tcc.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CanVariableMonitor", "tools", "tinycc", "tcc.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "can_monitor_client_V1.0", "offline_c_worker", "tinycc", "tcc.exe"))
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string part in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }
                string candidate = Path.Combine(part.Trim(), "tcc.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "";
    }
}

internal static class SimulationCGenerator
{
    private static readonly List<string> _lastCoverageNotes = new();

    public static IReadOnlyList<string> LastCoverageNotes => _lastCoverageNotes;

    private static readonly LPC1765_Keil_AppStubPack SupportPack = LPC1765_Keil_AppStubPack.Default;

    public static void WriteSupportFiles(string directory)
    {
        SupportPack.WriteSupportFiles(directory);
    }

    public static string Generate(
        OfflineWorkerProjectPayload project,
        IReadOnlyDictionary<string, uint> values,
        IReadOnlyDictionary<string, uint> forceValues)
    {
        _lastCoverageNotes.Clear();
        var notes = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder(1024 * 128);
        AppendCompatibilityPreamble(builder);
        builder.AppendLine();

        HashSet<string> sourceFunctionNames = project.Sources
            .Select(s => s.FunctionName)
            .Where(IsValidIdentifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> calledFunctions = new(StringComparer.OrdinalIgnoreCase);
        var sanitizedSources = new List<(OfflineWorkerSourcePayload Source, string Text)>(project.Sources.Count);
        foreach (OfflineWorkerSourcePayload source in project.Sources)
        {
            string sanitized = SanitizeFunctionSource(source, notes);
            sanitizedSources.Add((source, sanitized));
        }

        List<(OfflineWorkerSourcePayload Source, string Text)> appSources = sanitizedSources
            .Where(item => SupportPack.IsApplicationSourceFile(project.WorkDirectory, item.Source.FilePath))
            .ToList();
        List<string> hardBoundarySourceNames = appSources
            .Where(item => SupportPack.IsHardStubBoundaryFunctionName(item.Source.FunctionName))
            .Select(item => item.Source.FunctionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        if (hardBoundarySourceNames.Count > 0)
        {
            _lastCoverageNotes.Add("离线未覆盖：底层/存储边界函数不真实执行，调用自动 stub/mock：" + string.Join(", ", hardBoundarySourceNames));
        }
        appSources = appSources
            .Where(item => !SupportPack.IsHardStubBoundaryFunctionName(item.Source.FunctionName))
            .ToList();
        List<string> excludedSourceNames = sanitizedSources
            .Where(item => !SupportPack.IsApplicationSourceFile(project.WorkDirectory, item.Source.FilePath))
            .Select(item => item.Source.FunctionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        if (excludedSourceNames.Count > 0)
        {
            _lastCoverageNotes.Add("离线未覆盖：底层源码未参与编译，相关调用自动 stub/mock：" + string.Join(", ", excludedSourceNames));
        }
        HashSet<string> caseConstantNames = CollectCaseConstantIdentifiers(appSources.Select(item => item.Text));

        HashSet<string> definedFunctionNames = appSources
            .Where(item => SourceDefinesFunction(item.Source, item.Text))
            .Select(item => item.Source.FunctionName)
            .Where(IsValidIdentifier)
            .Where(name => !SupportPack.IsBuiltinFunctionName(name))
            .Where(name => !IsHarnessReservedFunctionName(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach ((OfflineWorkerSourcePayload _, string sanitized) in appSources)
        {
            foreach (Match match in Regex.Matches(sanitized, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
            {
                string name = match.Groups["name"].Value;
                if (!SupportPack.IsCKeyword(name) && !definedFunctionNames.Contains(name) && !SupportPack.IsBuiltinFunctionName(name))
                {
                    calledFunctions.Add(name);
                }
            }
        }

        AddGenerationCoverage(project, definedFunctionNames, calledFunctions);

        foreach (string note in notes.Take(16))
        {
            _lastCoverageNotes.Add(note);
        }

        HashSet<string> externalTypeNames = CollectExternalTypeIdentifiers(appSources.Select(item => item.Text));
        HashSet<string> externalScalars = CollectExternalScalarIdentifiers(
            appSources.Select(item => item.Text),
            sourceFunctionNames,
            calledFunctions,
            externalTypeNames);
        externalScalars.ExceptWith(caseConstantNames);

        foreach (string typeName in externalTypeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("typedef long long ");
            builder.Append(typeName);
            builder.AppendLine(";");
        }
        if (externalTypeNames.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (string symbol in externalScalars.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("static long long ");
            builder.Append(symbol);
            builder.AppendLine(";");
        }
        if (externalScalars.Count > 0)
        {
            builder.AppendLine();
        }

        AppendCaseConstantDefines(builder, caseConstantNames);

        builder.AppendLine("extern int printf(const char*, ...);");
        builder.AppendLine();

        HashSet<string> functionLikeNames = definedFunctionNames
            .Concat(calledFunctions)
            .Where(IsValidIdentifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> aliasToStorage = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> skippedFunctionLikeAliases = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < project.Variables.Count; i++)
        {
            OfflineWorkerVariablePayload variable = project.Variables[i];
            string storage = "__cm_v" + i.ToString(CultureInfo.InvariantCulture);
            uint value = values.TryGetValue(variable.Key, out uint current) ? current : variable.RawValue;
            builder.Append("static ");
            builder.Append(CTypeFor(variable));
            builder.Append(' ');
            builder.Append(storage);
            builder.Append(" = ");
            builder.Append(FormatCInitialValue(variable, value));
            builder.AppendLine(";");

            foreach (string alias in variable.Aliases.Where(IsValidIdentifier).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (SupportPack.IsCKeyword(alias) || aliasToStorage.ContainsKey(alias) || caseConstantNames.Contains(alias))
                {
                    continue;
                }
                if (functionLikeNames.Contains(alias))
                {
                    skippedFunctionLikeAliases.Add(alias);
                    continue;
                }
                if (!aliasToStorage.ContainsKey(alias))
                {
                    aliasToStorage.Add(alias, storage);
                }
            }
        }
        if (skippedFunctionLikeAliases.Count > 0)
        {
            _lastCoverageNotes.Add("离线未覆盖：变量名同时作为函数调用，已跳过变量宏别名 " +
                string.Join(", ", skippedFunctionLikeAliases.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(8)));
        }
        foreach ((string alias, string storage) in aliasToStorage.OrderByDescending(p => p.Key.Length))
        {
            builder.Append("#define ");
            builder.Append(alias);
            builder.Append(' ');
            builder.AppendLine(storage);
        }
        builder.AppendLine();

        bool hasSchedulerFlags = AppendSchedulerFlagMockFunction(builder, project);
        if (hasSchedulerFlags)
        {
            _lastCoverageNotes.Add("离线覆盖：定时标志变量已按 tick 触发态 mock。");
            builder.AppendLine();
        }

        bool hasActiveForces = forceValues.Count > 0;
        HashSet<string> forcedAliases = hasActiveForces
            ? BuildForcedAliases(project, forceValues)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasActiveForces)
        {
            AppendForceApplyFunction(builder, project, forceValues);
            builder.AppendLine();
        }

        List<string> orderedStubs = calledFunctions
            .Where(IsValidIdentifier)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (OfflineWorkerSourcePayload source in appSources.Select(item => item.Source))
        {
            if (IsValidIdentifier(source.FunctionName) && definedFunctionNames.Contains(source.FunctionName))
            {
                builder.AppendLine(BuildFunctionPrototype(source));
            }
        }
        foreach (string stub in orderedStubs)
        {
            builder.Append("static long long ");
            builder.Append(BuildStubImplementationName(stub));
            builder.AppendLine("();");
        }
        foreach (string stub in orderedStubs)
        {
            builder.Append("#define ");
            builder.Append(stub);
            builder.Append("(...) ");
            builder.Append(BuildStubImplementationName(stub));
            builder.AppendLine("()");
        }
        builder.AppendLine();

        foreach ((OfflineWorkerSourcePayload source, string sanitized) in appSources)
        {
            if (!definedFunctionNames.Contains(source.FunctionName))
            {
                continue;
            }
            string sourceText = hasActiveForces
                ? ProtectForcedVariableWrites(sanitized, forcedAliases)
                : sanitized;
            builder.AppendLine(sourceText);
            builder.AppendLine();
        }

        foreach (string stub in orderedStubs)
        {
            builder.Append("static long long ");
            builder.Append(BuildStubImplementationName(stub));
            builder.AppendLine(SupportPack.BuildStubBody(stub));
        }

        Dictionary<string, OfflineWorkerSourcePayload> sourceByFunctionName = appSources
            .Select(item => item.Source)
            .Where(source => definedFunctionNames.Contains(source.FunctionName))
            .GroupBy(source => source.FunctionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (orderedStubs.Any(stub => stub.Equals("main", StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine("#undef main");
        }
        builder.AppendLine("int main() {");
        if (hasSchedulerFlags)
        {
            builder.AppendLine("  __canmon_mock_scheduler_flags();");
        }
        if (hasActiveForces)
        {
            builder.AppendLine("  __canmon_apply_forces();");
        }
        foreach (string root in project.RootFunctions.Where(name => definedFunctionNames.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!sourceByFunctionName.TryGetValue(root, out OfflineWorkerSourcePayload? rootSource) ||
                !CanCallFunctionWithoutArguments(rootSource))
            {
                _lastCoverageNotes.Add("离线未覆盖：入口需要参数，未自动调用 " + root);
                continue;
            }
            if (hasActiveForces)
            {
                builder.AppendLine("  __canmon_apply_forces();");
            }
            builder.Append("  ");
            builder.Append(root);
            builder.AppendLine("();");
            if (hasActiveForces)
            {
                builder.AppendLine("  __canmon_apply_forces();");
            }
        }
        if (hasActiveForces)
        {
            builder.AppendLine("  __canmon_apply_forces();");
        }
        for (int i = 0; i < project.Variables.Count; i++)
        {
            OfflineWorkerVariablePayload variable = project.Variables[i];
            string storage = "__cm_v" + i.ToString(CultureInfo.InvariantCulture);
            AppendSnapshotPrintf(builder, variable, storage);
        }
        builder.AppendLine("  return 0;");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static bool AppendSchedulerFlagMockFunction(StringBuilder builder, OfflineWorkerProjectPayload project)
    {
        List<int> schedulerFlags = project.Variables
            .Select((variable, index) => new { Variable = variable, Index = index })
            .Where(item => IsSchedulerFlagVariable(item.Variable))
            .Select(item => item.Index)
            .Distinct()
            .ToList();
        if (schedulerFlags.Count == 0)
        {
            return false;
        }

        builder.AppendLine("static void __canmon_mock_scheduler_flags(void) {");
        foreach (int index in schedulerFlags)
        {
            builder.Append("  __cm_v");
            builder.Append(index.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" = 1;");
        }
        builder.AppendLine("}");
        return true;
    }

    private static bool IsSchedulerFlagVariable(OfflineWorkerVariablePayload variable)
    {
        IEnumerable<string> aliases = variable.Aliases ?? Enumerable.Empty<string>();
        foreach (string alias in aliases
            .Append(variable.Name)
            .Append(variable.Key)
            .Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            string normalized = Regex.Replace(alias, @"[^A-Za-z0-9]", "").ToLowerInvariant();
            if (normalized.Contains("timeflg", StringComparison.Ordinal) ||
                normalized.Contains("timeflag", StringComparison.Ordinal) ||
                normalized.Contains("t0flg", StringComparison.Ordinal) ||
                normalized.Contains("t010msflg", StringComparison.Ordinal) ||
                (normalized.Contains("flg", StringComparison.Ordinal) && normalized.Contains("ms", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddGenerationCoverage(
        OfflineWorkerProjectPayload project,
        IReadOnlySet<string> definedFunctionNames,
        IReadOnlySet<string> calledFunctions)
    {
        string roots = string.Join(", ", project.RootFunctions
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (roots.Length == 0)
        {
            roots = "none";
        }
        _lastCoverageNotes.Add("离线覆盖：入口 " + roots + "；定义函数 " + definedFunctionNames.Count.ToString(CultureInfo.InvariantCulture) + " 个。");

        List<string> missingRoots = project.RootFunctions
            .Where(name => !string.IsNullOrWhiteSpace(name) && !definedFunctionNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (missingRoots.Count > 0)
        {
            _lastCoverageNotes.Add("离线未覆盖：入口未生成定义 " + string.Join(", ", missingRoots));
        }

        List<string> suspiciousStubs = calledFunctions
            .Where(IsLikelyBusinessFunctionStub)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        if (suspiciousStubs.Count > 0)
        {
            _lastCoverageNotes.Add("离线未覆盖：业务调用被 stub " + string.Join(", ", suspiciousStubs));
        }

        List<string> appStubbed = calledFunctions
            .Where(SupportPack.IsStubOnlyFunctionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        if (appStubbed.Count > 0)
        {
            _lastCoverageNotes.Add("离线覆盖：底层/边界函数已自动 stub/mock " + string.Join(", ", appStubbed));
        }
    }

    private static bool IsLikelyBusinessFunctionStub(string name)
    {
        if (!IsValidIdentifier(name) ||
            SupportPack.IsCKeyword(name) ||
            SupportPack.IsBuiltinFunctionName(name) ||
            SupportPack.IsStubOnlyFunctionName(name))
        {
            return false;
        }

        return true;
    }

    private static HashSet<string> BuildForcedAliases(
        OfflineWorkerProjectPayload project,
        IReadOnlyDictionary<string, uint> forceValues)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OfflineWorkerVariablePayload variable in project.Variables)
        {
            if (!forceValues.ContainsKey(variable.Key))
            {
                continue;
            }

            if (IsValidIdentifier(variable.Name))
            {
                aliases.Add(variable.Name);
            }
            foreach (string alias in variable.Aliases)
            {
                if (IsValidIdentifier(alias))
                {
                    aliases.Add(alias);
                }
            }
        }
        return aliases;
    }

    private static string ProtectForcedVariableWrites(string source, IReadOnlyCollection<string> forcedAliases)
    {
        if (forcedAliases.Count == 0 || string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        string result = source;
        foreach (string alias in forcedAliases.OrderByDescending(name => name.Length))
        {
            string escaped = Regex.Escape(alias);
            string token = $@"(?<![A-Za-z0-9_])(?<name>{escaped})(?![A-Za-z0-9_])";
            string statementPrefix = @"(?<prefix>(^|[;{}])\s*)";
            string assignOp = @"(?<op><<=|>>=|\+=|-=|\*=|/=|%=|&=|\|=|\^=|=(?!=))";

            result = Regex.Replace(
                result,
                statementPrefix + token + @"\s*" + assignOp + @"\s*[^;]*;",
                match => match.Groups["prefix"].Value + match.Groups["name"].Value + " = " + match.Groups["name"].Value + ";",
                RegexOptions.Multiline);

            result = Regex.Replace(
                result,
                statementPrefix + token + @"\s*(\+\+|--)\s*;",
                match => match.Groups["prefix"].Value + match.Groups["name"].Value + ";",
                RegexOptions.Multiline);

            result = Regex.Replace(
                result,
                statementPrefix + @"(\+\+|--)\s*" + token + @"\s*;",
                match => match.Groups["prefix"].Value + match.Groups["name"].Value + ";",
                RegexOptions.Multiline);
        }
        return result;
    }

    private static void AppendForceApplyFunction(
        StringBuilder builder,
        OfflineWorkerProjectPayload project,
        IReadOnlyDictionary<string, uint> forceValues)
    {
        builder.AppendLine("static void __canmon_apply_forces(void) {");
        for (int i = 0; i < project.Variables.Count; i++)
        {
            OfflineWorkerVariablePayload variable = project.Variables[i];
            if (!forceValues.TryGetValue(variable.Key, out uint forced))
            {
                continue;
            }

            builder.Append("  __cm_v");
            builder.Append(i.ToString(CultureInfo.InvariantCulture));
            builder.Append(" = ");
            builder.Append(FormatCInitialValue(variable, forced));
            builder.AppendLine(";");
        }
        builder.AppendLine("}");
    }

    private static string InjectForceApplications(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        var builder = new StringBuilder(source.Length + 1024);
        using var reader = new StringReader(source);
        string? line;
        bool bodyStarted = false;
        while ((line = reader.ReadLine()) != null)
        {
            builder.AppendLine(line);
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            if (trimmed.Contains('{'))
            {
                bodyStarted = true;
            }
            if (!bodyStarted)
            {
                continue;
            }
            if (ShouldApplyForcesAfterLine(trimmed))
            {
                builder.AppendLine("  __canmon_apply_forces();");
            }
        }
        return builder.ToString();
    }

    private static bool ShouldApplyForcesAfterLine(string trimmed)
    {
        if (trimmed.StartsWith("else", StringComparison.Ordinal) ||
            trimmed.StartsWith("case ", StringComparison.Ordinal) ||
            trimmed.StartsWith("default", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.EndsWith(";", StringComparison.Ordinal) ||
            trimmed.EndsWith("{", StringComparison.Ordinal) ||
            trimmed.Contains("} else", StringComparison.Ordinal);
    }

    private static void AppendSnapshotPrintf(StringBuilder builder, OfflineWorkerVariablePayload variable, string storage)
    {
        if (IsFloat32(variable))
        {
            builder.AppendLine("  {");
            builder.Append("    union { float f; unsigned int u; } __cm_bits; __cm_bits.f = ");
            builder.Append(storage);
            builder.AppendLine(";");
            builder.Append("    printf(\"__CANMON__ ");
            builder.Append(EscapeCString(variable.Key));
            builder.AppendLine(" %u\\n\", __cm_bits.u);");
            builder.AppendLine("  }");
            return;
        }

        builder.Append("  printf(\"__CANMON__ ");
        builder.Append(EscapeCString(variable.Key));
        builder.Append(" %u\\n\", (unsigned int)(");
        builder.Append(storage);
        builder.AppendLine("));");
    }

    private static bool IsFloat32(OfflineWorkerVariablePayload variable)
    {
        return variable.Size == 4 &&
            (variable.TypeName ?? "").Contains("float", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCompatibilityPreamble(StringBuilder builder)
    {
        builder.Append("#include \"");
        builder.Append(SupportPack.CompatibilityHeaderFileName);
        builder.AppendLine("\"");
    }

    private static string BuildFunctionPrototype(OfflineWorkerSourcePayload source)
    {
        string joined = string.Join(" ", source.Lines.Take(8));
        int brace = joined.IndexOf('{');
        if (brace >= 0)
        {
            joined = joined[..brace];
        }
        string escapedName = Regex.Escape(source.FunctionName);
        Match match = Regex.Match(joined, @"(?<ret>[A-Za-z_][A-Za-z0-9_\s\*]*?)\b" + escapedName + @"\s*\(");
        string returnType = match.Success ? Regex.Replace(match.Groups["ret"].Value.Trim(), @"\s+", " ") : "long long";
        if (returnType.Length == 0 || returnType.Contains(';') || returnType.Contains('='))
        {
            returnType = "long long";
        }
        returnType = Regex.Replace(returnType, @"\bbit\b", "unsigned char");
        return returnType + " " + source.FunctionName + "();";
    }

    private static bool CanCallFunctionWithoutArguments(OfflineWorkerSourcePayload source)
    {
        string joined = string.Join(" ", source.Lines.Take(12));
        int brace = joined.IndexOf('{');
        if (brace >= 0)
        {
            joined = joined[..brace];
        }
        string escapedName = Regex.Escape(source.FunctionName);
        Match match = Regex.Match(joined, @"\b" + escapedName + @"\s*\((?<params>[^)]*)\)");
        return !match.Success || IsEmptyParameterList(match.Groups["params"].Value);
    }

    private static bool IsEmptyParameterList(string parameters)
    {
        string text = Regex.Replace(parameters ?? "", @"\s+", "");
        return text.Length == 0 || text.Equals("void", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SourceDefinesFunction(OfflineWorkerSourcePayload source, string sanitized)
    {
        if (!IsValidIdentifier(source.FunctionName))
        {
            return false;
        }
        string escapedName = Regex.Escape(source.FunctionName);
        return Regex.IsMatch(
            sanitized,
            @"(?m)^\s*(?:[A-Za-z_][A-Za-z0-9_]*\s+)+(?:\*+\s*)?" + escapedName + @"\s*\([^;{}]*\)\s*\{",
            RegexOptions.Singleline);
    }

    private static bool IsHarnessReservedFunctionName(string name)
    {
        return name.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sprintf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("snprintf", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CanMonitor_", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFunctionSource(OfflineWorkerSourcePayload source, ISet<string> coverageNotes)
    {
        var builder = new StringBuilder();
        bool inBlockComment = false;
        int startIndex = FindFunctionStartLine(source);
        if (startIndex > 0)
        {
            coverageNotes.Add("离线未覆盖：已丢弃函数切片前置残片（" + source.FunctionName + "）。");
        }
        if (startIndex < 0)
        {
            return "";
        }
        bool bodyStarted = false;
        int braceDepth = 0;
        foreach (string raw in source.Lines.Skip(startIndex))
        {
            string line = StripComments(raw, ref inBlockComment).TrimEnd();
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            line = Regex.Replace(line, @"\b(?:__irq|__weak|__IO|__I|__O|__packed|reentrant|interrupt|xdata|idata|pdata|code)\b", "");
            line = Regex.Replace(line, @"\busing\s*\(\s*\d+\s*\)", "");
            line = Regex.Replace(line, @"__attribute__\s*\(\s*\([^)]*\)\s*\)", "");
            line = Regex.Replace(line, @"__align\s*\([^)]*\)", "");
            line = Regex.Replace(line, @"__asm\s*\([^)]*\)", "");
            line = Regex.Replace(line, @"\b(?:data|near|far|large|small|compact)\b", "");
            line = Regex.Replace(line, @"\bbit\b", "unsigned char");
            if (TryNormalizeBareMacroStatement(line, source, coverageNotes, out string macroStatement))
            {
                line = macroStatement;
            }
            if (TryNormalizeFunctionPointerAssignment(line, source, coverageNotes, out string functionPointerAssignment))
            {
                line = functionPointerAssignment;
            }
            line = NormalizeScalarCasts(line, source, coverageNotes);
            line = NormalizePointerSyntax(line, source, coverageNotes);
            if (line.Contains("sbit", StringComparison.Ordinal))
            {
                coverageNotes.Add("离线未覆盖：已跳过 sbit/硬件位定义。");
                continue;
            }
            string normalized = NormalizeComplexAccess(line, source, coverageNotes);
            line = normalized;
            line = NormalizeSafeArithmetic(line, source, coverageNotes);
            builder.AppendLine(line);
            foreach (char ch in line)
            {
                if (ch == '{')
                {
                    bodyStarted = true;
                    braceDepth++;
                }
                else if (ch == '}')
                {
                    braceDepth--;
                }
            }
            if (bodyStarted && braceDepth <= 0)
            {
                break;
            }
        }
        return builder.ToString();
    }

    private static bool TryNormalizeBareMacroStatement(
        string line,
        OfflineWorkerSourcePayload source,
        ISet<string> coverageNotes,
        out string normalized)
    {
        normalized = line;
        string trimmed = line.Trim();
        if (!Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*;?$"))
        {
            return false;
        }

        string identifier = trimmed.TrimEnd(';');
        if (SupportPack.IsCKeyword(identifier) ||
            SupportPack.IsKnownTypeName(identifier) ||
            SupportPack.IsBuiltinFunctionName(identifier))
        {
            return false;
        }

        coverageNotes.Add("离线未覆盖：硬件/编译器宏语句已按空操作处理（" + source.FunctionName + "）。");
        normalized = ";";
        return true;
    }

    private static bool TryNormalizeFunctionPointerAssignment(
        string line,
        OfflineWorkerSourcePayload source,
        ISet<string> coverageNotes,
        out string normalized)
    {
        normalized = line;
        Match match = Regex.Match(
            line,
            @"^\s*(?<lhs>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<rhs>[A-Za-z_][A-Za-z0-9_]*)\s*;\s*$");
        if (!match.Success)
        {
            return false;
        }

        string lhs = match.Groups["lhs"].Value;
        string rhs = match.Groups["rhs"].Value;
        bool looksLikeTaskPointer =
            lhs.StartsWith("gp_", StringComparison.OrdinalIgnoreCase) ||
            lhs.Contains("task", StringComparison.OrdinalIgnoreCase) ||
            lhs.Contains("callback", StringComparison.OrdinalIgnoreCase) ||
            lhs.Contains("handler", StringComparison.OrdinalIgnoreCase);
        bool rhsLooksLikeEntry =
            rhs.Contains("disp", StringComparison.OrdinalIgnoreCase) ||
            rhs.Contains("display", StringComparison.OrdinalIgnoreCase) ||
            rhs.Contains("frame", StringComparison.OrdinalIgnoreCase) ||
            rhs.Contains("task", StringComparison.OrdinalIgnoreCase) ||
            rhs.Contains("main", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeTaskPointer && !rhsLooksLikeEntry)
        {
            return false;
        }

        coverageNotes.Add("离线未覆盖：函数指针/任务入口绑定已按调度 no-op 处理（" + source.FunctionName + "）。");
        normalized = ";";
        return true;
    }

    private static string NormalizeScalarCasts(string line, OfflineWorkerSourcePayload source, ISet<string> coverageNotes)
    {
        string normalized = Regex.Replace(
            line,
            @"\(\s*(?:const\s+|volatile\s+)*(?:signed\s+|unsigned\s+)?(?:char|short|int|long|float|double|bool|u8|u16|u32|s8|s16|s32|uint8|uint16|uint32|int8|int16|int32|uint8_t|uint16_t|uint32_t|int8_t|int16_t|int32_t|BYTE|WORD|DWORD|BOOL|size_t)\s*\)",
            "");
        if (!normalized.Equals(line, StringComparison.Ordinal))
        {
            coverageNotes.Add("离线未覆盖：标量类型转换已按仿真兼容处理（" + source.FunctionName + "）。");
        }
        return normalized;
    }

    private static string NormalizePointerSyntax(string line, OfflineWorkerSourcePayload source, ISet<string> coverageNotes)
    {
        string normalized = Regex.Replace(
            line,
            @"^\s*(?<type>[A-Za-z_][A-Za-z0-9_]*(?:\s+[A-Za-z_][A-Za-z0-9_]*)*)\s*\*\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*NULL\s*;",
            "long long ${name} = 0;");
        normalized = Regex.Replace(normalized, @"\([A-Za-z_][A-Za-z0-9_]*(?:\s+[A-Za-z_][A-Za-z0-9_]*)*\s*\*\)", "");
        normalized = Regex.Replace(normalized, @"&\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)", "${name}");
        normalized = NormalizePointerDereferences(normalized);
        if (!normalized.Equals(line, StringComparison.Ordinal))
        {
            coverageNotes.Add("Offline uncovered: pointer access approximated as scalar (" + source.FunctionName + ").");
        }
        return normalized;
    }

    private static string NormalizePointerDereferences(string line)
    {
        return Regex.Replace(
            line,
            @"\*\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            match =>
            {
                int previous = match.Index - 1;
                while (previous >= 0 && char.IsWhiteSpace(line[previous]))
                {
                    previous--;
                }

                if (previous < 0 || "=({[,!~?:;+-".Contains(line[previous], StringComparison.Ordinal))
                {
                    return match.Groups["name"].Value;
                }

                return match.Value;
            });
    }

    private static int FindFunctionStartLine(OfflineWorkerSourcePayload source)
    {
        if (!IsValidIdentifier(source.FunctionName))
        {
            return -1;
        }
        string escapedName = Regex.Escape(source.FunctionName);
        for (int i = 0; i < source.Lines.Count; i++)
        {
            string line = source.Lines[i];
            if (Regex.IsMatch(line, @"^\s*(?:[A-Za-z_][A-Za-z0-9_]*\s+)+(?:\*+\s*)?" + escapedName + @"\s*\("))
            {
                return i;
            }
        }
        return -1;
    }

    private static string NormalizeSafeArithmetic(string line, OfflineWorkerSourcePayload source, ISet<string> coverageNotes)
    {
        if ((!line.Contains('/') && !line.Contains('%')) || line.Contains('"', StringComparison.Ordinal))
        {
            return line;
        }

        string normalized = Regex.Replace(
            line,
            @"(?<!/)/(?!/)\s*(?<den>[A-Za-z_][A-Za-z0-9_]*\s*\([^;\r\n]*?\)|\([^;\r\n]*?\)|[0-9]+(?:\.[0-9]+)?|[A-Za-z_][A-Za-z0-9_]*)",
            match =>
            {
                string denominator = match.Groups["den"].Value;
                string helper = LooksLikeFloatingDenominator(denominator)
                    ? "__canmon_safe_den"
                    : "__canmon_safe_den_i64";
                return "/ " + helper + "(" + denominator + ")";
            });
        normalized = Regex.Replace(
            normalized,
            @"%\s*(?<den>[A-Za-z_][A-Za-z0-9_]*\s*\([^;\r\n]*?\)|\([^;\r\n]*?\)|[0-9]+(?:\.[0-9]+)?|[A-Za-z_][A-Za-z0-9_]*)",
            "% __canmon_safe_mod_den((long long)(${den}))");
        if (!normalized.Equals(line, StringComparison.Ordinal))
        {
            coverageNotes.Add("离线未覆盖：除法/取模分母已加零值保护（" + source.FunctionName + "）。");
        }
        return normalized;
    }

    private static bool LooksLikeFloatingDenominator(string denominator)
    {
        return Regex.IsMatch(denominator, @"\d+\.\d+");
    }

    private static string NormalizeComplexAccess(string line, OfflineWorkerSourcePayload source, ISet<string> coverageNotes)
    {
        if (!line.Contains('[') && !line.Contains('.') && !line.Contains("->", StringComparison.Ordinal))
        {
            return line;
        }

        string normalized = Regex.Replace(
            line,
            @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[[^\[\]\r\n;]*\]|\s*(?:->|\.)\s*[A-Za-z_][A-Za-z0-9_]*)+",
            "${name}");
        if (!normalized.Equals(line, StringComparison.Ordinal))
        {
            coverageNotes.Add("离线未覆盖：复杂数组/结构体访问已按标量近似处理（" + source.FunctionName + "）。");
        }
        return normalized;
    }

    private static HashSet<string> CollectExternalTypeIdentifiers(IEnumerable<string> sanitizedSources)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sanitizedSources)
        {
            foreach (Match match in Regex.Matches(source, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*_TypeDef)\b"))
            {
                string name = match.Groups["name"].Value;
                if (IsValidIdentifier(name))
                {
                    types.Add(name);
                }
            }
        }
        return types;
    }

    private static HashSet<string> CollectCaseConstantIdentifiers(IEnumerable<string> sanitizedSources)
    {
        var constants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sanitizedSources)
        {
            foreach (Match caseMatch in Regex.Matches(source, @"(?m)^\s*case\s+(?<expr>[^:]+):"))
            {
                string expression = caseMatch.Groups["expr"].Value;
                foreach (Match identifier in Regex.Matches(expression, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
                {
                    string name = identifier.Value;
                    if (!IsValidIdentifier(name) ||
                        SupportPack.IsCKeyword(name) ||
                        SupportPack.IsKnownTypeName(name))
                    {
                        continue;
                    }

                    int after = identifier.Index + identifier.Length;
                    while (after < expression.Length && char.IsWhiteSpace(expression[after]))
                    {
                        after++;
                    }
                    if (after < expression.Length && expression[after] == '(')
                    {
                        continue;
                    }

                    constants.Add(name);
                }
            }
        }
        return constants;
    }

    private static void AppendCaseConstantDefines(StringBuilder builder, IReadOnlyCollection<string> caseConstantNames)
    {
        if (caseConstantNames.Count == 0)
        {
            return;
        }

        int ordinal = 1;
        foreach (string name in caseConstantNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("#ifndef ");
            builder.AppendLine(name);
            builder.Append("#define ");
            builder.Append(name);
            builder.Append(' ');
            builder.Append(InferCaseConstantValue(name, ordinal).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine();
            builder.Append("#endif");
            builder.AppendLine();
            ordinal++;
        }
        builder.AppendLine();
        _lastCoverageNotes.Add("离线覆盖：case 宏/枚举常量已临时定义 " +
            string.Join(", ", caseConstantNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(12)));
    }

    private static long InferCaseConstantValue(string name, int ordinal)
    {
        Match keyMatch = Regex.Match(name, @"(?:^|_)(?:F|KEY|K)(?<num>[0-9]+)$", RegexOptions.IgnoreCase);
        if (keyMatch.Success &&
            int.TryParse(keyMatch.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keyNumber) &&
            keyNumber >= 1 &&
            keyNumber <= 30)
        {
            return 1L << (keyNumber - 1);
        }

        Match trailingNumber = Regex.Match(name, @"(?<num>[0-9]+)$");
        if (trailingNumber.Success &&
            int.TryParse(trailingNumber.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        return 1000L + ordinal;
    }

    private static HashSet<string> CollectExternalScalarIdentifiers(
        IEnumerable<string> sanitizedSources,
        IReadOnlySet<string> sourceFunctionNames,
        IReadOnlySet<string> calledFunctions,
        IReadOnlySet<string> externalTypeNames)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sanitizedSources)
        {
            foreach (Match match in Regex.Matches(source, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
            {
                string name = match.Value;
                if (!ShouldDeclareExternalScalar(source, match.Index, name, sourceFunctionNames, calledFunctions, externalTypeNames))
                {
                    continue;
                }
                symbols.Add(name);
            }
        }
        return symbols;
    }

    private static bool ShouldDeclareExternalScalar(
        string source,
        int index,
        string name,
        IReadOnlySet<string> sourceFunctionNames,
        IReadOnlySet<string> calledFunctions,
        IReadOnlySet<string> externalTypeNames)
    {
        if (!IsValidIdentifier(name) ||
            SupportPack.IsCKeyword(name) ||
            SupportPack.IsKnownTypeName(name) ||
            externalTypeNames.Contains(name) ||
            name.EndsWith("_TypeDef", StringComparison.Ordinal) ||
            sourceFunctionNames.Contains(name) ||
            calledFunctions.Contains(name) ||
            name.Equals("printf", StringComparison.Ordinal))
        {
            return false;
        }

        int after = index + name.Length;
        while (after < source.Length && char.IsWhiteSpace(source[after]))
        {
            after++;
        }
        if (after < source.Length && source[after] == '(')
        {
            return false;
        }

        string before = source[..index];
        Match previous = Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)\s*$");
        if (previous.Success)
        {
            string prev = previous.Groups[1].Value;
            if (prev.Equals("struct", StringComparison.OrdinalIgnoreCase) ||
                prev.Equals("union", StringComparison.OrdinalIgnoreCase) ||
                prev.Equals("enum", StringComparison.OrdinalIgnoreCase) ||
                prev.Equals("typedef", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string StripComments(string line, ref bool inBlockComment)
    {
        var builder = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                break;
            }
            builder.Append(line[i]);
        }
        return builder.ToString();
    }

    private static string CTypeFor(OfflineWorkerVariablePayload variable)
    {
        string type = variable.TypeName ?? "";
        if (type.Contains("float", StringComparison.OrdinalIgnoreCase))
        {
            return "float";
        }
        if (type.Contains("double", StringComparison.OrdinalIgnoreCase))
        {
            return "double";
        }
        bool signed = Regex.IsMatch(type, @"\b(signed|int8_t|int16_t|int32_t|s8|s16|s32|char|short|int|long)\b", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(type, @"\b(unsigned|uint8_t|uint16_t|uint32_t|u8|u16|u32|BYTE|WORD|DWORD)\b", RegexOptions.IgnoreCase);
        return signed ? "long long" : "unsigned long long";
    }

    private static string FormatCInitialValue(OfflineWorkerVariablePayload variable, uint raw)
    {
        if ((variable.TypeName ?? "").Contains("float", StringComparison.OrdinalIgnoreCase) && variable.Size == 4)
        {
            float value = BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0;
            }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
        return raw.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsValidIdentifier(string value)
    {
        return Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
    }

    private static string BuildStubImplementationName(string name)
    {
        return "__canmon_stub_" + name;
    }

    private static string EscapeCString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

internal sealed class WorkerRequest
{
    public int Id { get; set; }
    public string Command { get; set; } = "";
    public JsonElement? Payload { get; set; }
}

internal sealed class WorkerResult
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public bool EngineAvailable { get; set; }
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, uint> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Coverage { get; set; } = new();
}

internal sealed class OfflineWorkerProjectPayload
{
    public string WorkDirectory { get; set; } = "";
    public string Signature { get; set; } = "";
    public List<string> RootFunctions { get; set; } = new();
    public List<OfflineWorkerSourcePayload> Sources { get; set; } = new();
    public List<OfflineWorkerVariablePayload> Variables { get; set; } = new();
}

internal sealed class OfflineWorkerSourcePayload
{
    public string FunctionName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int StartLine { get; set; }
    public List<string> Lines { get; set; } = new();
}

internal sealed class OfflineWorkerVariablePayload
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public uint Address { get; set; }
    public int Size { get; set; }
    public string TypeName { get; set; } = "";
    public uint RawValue { get; set; }
    public bool ForceActive { get; set; }
    public List<string> Aliases { get; set; } = new();
}

internal sealed class OfflineWorkerVariableWritePayload
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public uint RawValue { get; set; }
    public int Size { get; set; }
}

internal sealed class OfflineWorkerSnapshotRequest
{
    public IReadOnlyList<OfflineWorkerVariablePayload> Variables { get; set; } = Array.Empty<OfflineWorkerVariablePayload>();
}
