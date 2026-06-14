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
        return clean.Length <= 240 ? clean : clean[..240];
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
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CanVariableMonitor", "tools", "tinycc", "tcc.exe"))
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

    private static readonly HashSet<string> CKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "return", "sizeof",
        "typedef", "struct", "union", "enum", "static", "extern", "const", "volatile", "break",
        "continue", "goto", "void", "int", "char", "short", "long", "float", "double", "signed",
        "unsigned", "auto", "register", "true", "false", "NULL",
        "__irq", "__weak", "__IO", "__I", "__O", "__packed", "__align", "__attribute__", "__asm", "__nop",
        "reentrant", "interrupt", "using", "xdata", "idata", "pdata", "code"
    };

    private static readonly HashSet<string> KnownTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned",
        "int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t",
        "int8", "uint8", "int16", "uint16", "int32", "uint32",
        "s8", "u8", "s16", "u16", "s32", "u32",
        "S8", "U8", "S16", "U16", "S32", "U32",
        "bit",
        "uchar", "UCHAR", "ushort", "USHORT", "uint", "UINT", "ulong", "ULONG",
        "BYTE", "WORD", "DWORD", "BOOL", "bool",
        "INT8U", "INT16U", "INT32U", "INT8S", "INT16S", "INT32S",
        "size_t"
    };

    private static readonly HashSet<string> BuiltinFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "printf", "strlen", "strcpy", "strncpy", "memcpy", "memset", "memcmp",
        "abs", "labs", "llabs"
    };

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

        HashSet<string> definedFunctionNames = sanitizedSources
            .Where(item => SourceDefinesFunction(item.Source, item.Text))
            .Select(item => item.Source.FunctionName)
            .Where(IsValidIdentifier)
            .Where(name => !BuiltinFunctionNames.Contains(name))
            .Where(name => !IsStubOnlyFunctionName(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach ((OfflineWorkerSourcePayload _, string sanitized) in sanitizedSources)
        {
            foreach (Match match in Regex.Matches(sanitized, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
            {
                string name = match.Groups["name"].Value;
                if (!CKeywords.Contains(name) && !definedFunctionNames.Contains(name) && !BuiltinFunctionNames.Contains(name))
                {
                    calledFunctions.Add(name);
                }
            }
        }
        foreach (string note in notes.Take(16))
        {
            _lastCoverageNotes.Add(note);
        }

        HashSet<string> externalTypeNames = CollectExternalTypeIdentifiers(sanitizedSources.Select(item => item.Text));
        HashSet<string> externalScalars = CollectExternalScalarIdentifiers(
            sanitizedSources.Select(item => item.Text),
            sourceFunctionNames,
            calledFunctions,
            externalTypeNames);

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

        builder.AppendLine("extern int printf(const char*, ...);");
        builder.AppendLine("extern int abs(int);");
        builder.AppendLine();

        Dictionary<string, string> aliasToStorage = new(StringComparer.OrdinalIgnoreCase);
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
                if (!CKeywords.Contains(alias) && !aliasToStorage.ContainsKey(alias))
                {
                    aliasToStorage.Add(alias, storage);
                }
            }
        }
        foreach ((string alias, string storage) in aliasToStorage.OrderByDescending(p => p.Key.Length))
        {
            builder.Append("#define ");
            builder.Append(alias);
            builder.Append(' ');
            builder.AppendLine(storage);
        }
        builder.AppendLine();

        bool hasActiveForces = forceValues.Count > 0;
        HashSet<string> forcedAliases = hasActiveForces
            ? BuildForcedAliases(project, forceValues)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasActiveForces)
        {
            AppendForceApplyFunction(builder, project, forceValues);
            builder.AppendLine();
        }

        foreach (OfflineWorkerSourcePayload source in project.Sources)
        {
            if (IsValidIdentifier(source.FunctionName) && definedFunctionNames.Contains(source.FunctionName))
            {
                builder.AppendLine(BuildFunctionPrototype(source));
            }
        }
        foreach (string stub in calledFunctions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsValidIdentifier(stub))
            {
                continue;
            }
            builder.Append("long long ");
            builder.Append(stub);
            builder.AppendLine("();");
        }
        builder.AppendLine();

        foreach ((OfflineWorkerSourcePayload source, string sanitized) in sanitizedSources)
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

        foreach (string stub in calledFunctions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsValidIdentifier(stub))
            {
                continue;
            }
            builder.Append("long long ");
            builder.Append(stub);
            builder.Append("() { return ");
            builder.Append(stub.Equals("CanMonitor_BusinessGate", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
            builder.AppendLine("; }");
        }

        builder.AppendLine("int main() {");
        foreach (string root in project.RootFunctions.Where(name => definedFunctionNames.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
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
        builder.AppendLine("typedef signed char int8_t;");
        builder.AppendLine("typedef unsigned char uint8_t;");
        builder.AppendLine("typedef signed short int16_t;");
        builder.AppendLine("typedef unsigned short uint16_t;");
        builder.AppendLine("typedef signed int int32_t;");
        builder.AppendLine("typedef unsigned int uint32_t;");
        builder.AppendLine("typedef signed char int8;");
        builder.AppendLine("typedef unsigned char uint8;");
        builder.AppendLine("typedef signed short int16;");
        builder.AppendLine("typedef unsigned short uint16;");
        builder.AppendLine("typedef signed int int32;");
        builder.AppendLine("typedef unsigned int uint32;");
        builder.AppendLine("typedef unsigned char uchar;");
        builder.AppendLine("typedef unsigned char UCHAR;");
        builder.AppendLine("typedef unsigned short ushort;");
        builder.AppendLine("typedef unsigned short USHORT;");
        builder.AppendLine("typedef unsigned int uint;");
        builder.AppendLine("typedef unsigned int UINT;");
        builder.AppendLine("typedef unsigned long ulong;");
        builder.AppendLine("typedef unsigned long ULONG;");
        builder.AppendLine("typedef unsigned char u8;");
        builder.AppendLine("typedef unsigned short u16;");
        builder.AppendLine("typedef unsigned int u32;");
        builder.AppendLine("typedef signed char s8;");
        builder.AppendLine("typedef signed short s16;");
        builder.AppendLine("typedef signed int s32;");
        builder.AppendLine("typedef unsigned char BYTE;");
        builder.AppendLine("typedef unsigned short WORD;");
        builder.AppendLine("typedef unsigned int DWORD;");
        builder.AppendLine("typedef unsigned char BOOL;");
        builder.AppendLine("typedef unsigned char INT8U;");
        builder.AppendLine("typedef unsigned short INT16U;");
        builder.AppendLine("typedef unsigned int INT32U;");
        builder.AppendLine("typedef signed char INT8S;");
        builder.AppendLine("typedef signed short INT16S;");
        builder.AppendLine("typedef signed int INT32S;");
        builder.AppendLine("typedef unsigned char bool;");
        builder.AppendLine("typedef unsigned char bit;");
        builder.AppendLine("typedef unsigned int size_t;");
        builder.AppendLine("#define true 1");
        builder.AppendLine("#define false 0");
        builder.AppendLine("#define NULL 0");
        builder.AppendLine("#define __irq");
        builder.AppendLine("#define __weak");
        builder.AppendLine("#define __IO");
        builder.AppendLine("#define __I");
        builder.AppendLine("#define __O");
        builder.AppendLine("#define __packed");
        builder.AppendLine("#define __align(x)");
        builder.AppendLine("#define __attribute__(x)");
        builder.AppendLine("#define __asm(x)");
        builder.AppendLine("#define __nop()");
        builder.AppendLine("#define reentrant");
        builder.AppendLine("#define interrupt");
        builder.AppendLine("#define using(x)");
        builder.AppendLine("#define xdata");
        builder.AppendLine("#define idata");
        builder.AppendLine("#define pdata");
        builder.AppendLine("#define code");
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

    private static bool IsStubOnlyFunctionName(string name)
    {
        return name.Equals("sprintf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("snprintf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("UARTSend", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("LCD_GO_Page", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LCD_WR_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CanMonitor_", StringComparison.OrdinalIgnoreCase) ||
            IsHardwareSendFunctionName(name);
    }

    private static bool IsHardwareSendFunctionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return Regex.IsMatch(name, @"^CAN\d*_?Send", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"^CAN_Send", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"^Remote.*_Send", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"_Send(?:_|$)", RegexOptions.IgnoreCase);
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
            line = NormalizePointerSyntax(line, source, coverageNotes);
            if (line.Contains("sbit", StringComparison.Ordinal))
            {
                coverageNotes.Add("离线未覆盖：已跳过 sbit/硬件位定义。");
                continue;
            }
            string normalized = NormalizeComplexAccess(line, source, coverageNotes);
            line = normalized;
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

    private static string NormalizePointerSyntax(string line, OfflineWorkerSourcePayload source, ISet<string> coverageNotes)
    {
        string normalized = Regex.Replace(
            line,
            @"^\s*(?<type>[A-Za-z_][A-Za-z0-9_]*(?:\s+[A-Za-z_][A-Za-z0-9_]*)*)\s*\*\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*NULL\s*;",
            "long long ${name} = 0;");
        normalized = Regex.Replace(normalized, @"\([A-Za-z_][A-Za-z0-9_]*\s*\*\)", "");
        if (!normalized.Equals(line, StringComparison.Ordinal))
        {
            coverageNotes.Add("Offline uncovered: pointer access approximated as scalar (" + source.FunctionName + ").");
        }
        return normalized;
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
            CKeywords.Contains(name) ||
            KnownTypeNames.Contains(name) ||
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
