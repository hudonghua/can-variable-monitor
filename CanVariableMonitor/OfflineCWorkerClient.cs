using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanVariableMonitor;

internal sealed class OfflineCWorkerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _nextId;
    private string _lastWorkerError = "";

    public string LastStatus { get; private set; } = "";
    public bool EngineAvailable { get; private set; }

    public OfflineWorkerResult InitProject(OfflineWorkerProjectPayload project, int timeoutMs = 10000)
    {
        return Send("InitProject", project, timeoutMs);
    }

    public OfflineWorkerResult RunTick(int timeoutMs = 15000)
    {
        return Send("RunTick", null, timeoutMs);
    }

    public OfflineWorkerResult ReadSnapshot(IReadOnlyList<OfflineWorkerVariablePayload> variables, int timeoutMs = 5000)
    {
        return Send("ReadSnapshot", new OfflineWorkerSnapshotRequest { Variables = variables }, timeoutMs);
    }

    public OfflineWorkerResult WriteVariable(OfflineWorkerVariableWritePayload payload, int timeoutMs = 5000)
    {
        return Send("WriteVariable", payload, timeoutMs);
    }

    public OfflineWorkerResult ForceVariable(OfflineWorkerVariableWritePayload payload, int timeoutMs = 5000)
    {
        return Send("ForceVariable", payload, timeoutMs);
    }

    public OfflineWorkerResult ReleaseVariable(OfflineWorkerVariableWritePayload payload, int timeoutMs = 5000)
    {
        return Send("ReleaseVariable", payload, timeoutMs);
    }

    public OfflineWorkerResult GetCoverage(int timeoutMs = 5000)
    {
        return Send("GetCoverage", null, timeoutMs);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    TrySendShutdown();
                    if (!_process.WaitForExit(250))
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _stdin?.Dispose();
                _stdout?.Dispose();
                _process?.Dispose();
                _stdin = null;
                _stdout = null;
                _process = null;
                EngineAvailable = false;
                LastStatus = "";
            }
        }
    }

    private OfflineWorkerResult Send(string command, object? payload, int timeoutMs)
    {
        lock (_sync)
        {
            if (!TryEnsureStarted(out string startError))
            {
                EngineAvailable = false;
                LastStatus = startError;
                return OfflineWorkerResult.Fail(startError);
            }

            if (_stdin == null || _stdout == null)
            {
                EngineAvailable = false;
                LastStatus = "离线 C worker 通道未打开。";
                return OfflineWorkerResult.Fail(LastStatus);
            }

            int id = unchecked(++_nextId);
            string request = JsonSerializer.Serialize(new OfflineWorkerRequest
            {
                Id = id,
                Command = command,
                Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions)
            }, JsonOptions);

            try
            {
                _stdin.WriteLine(request);
                _stdin.Flush();

                Task<string?> readTask = _stdout.ReadLineAsync();
                if (!readTask.Wait(Math.Max(200, timeoutMs)))
                {
                    RestartAfterProtocolError();
                    string message = "离线 C worker 超时，已重启。";
                    LastStatus = message;
                    EngineAvailable = false;
                    return OfflineWorkerResult.Fail(message);
                }

                string? line = readTask.Result;
                if (string.IsNullOrWhiteSpace(line))
                {
                    RestartAfterProtocolError();
                    string message = "离线 C worker 无响应，已重启。";
                    LastStatus = message;
                    EngineAvailable = false;
                    return OfflineWorkerResult.Fail(message);
                }

                OfflineWorkerResult? result = JsonSerializer.Deserialize<OfflineWorkerResult>(line, JsonOptions);
                if (result == null)
                {
                    RestartAfterProtocolError();
                    string message = "离线 C worker 返回格式异常。";
                    LastStatus = message;
                    EngineAvailable = false;
                    return OfflineWorkerResult.Fail(message);
                }

                EngineAvailable = result.EngineAvailable;
                LastStatus = result.Status ?? result.Error ?? "";
                return result;
            }
            catch (Exception ex)
            {
                RestartAfterProtocolError();
                string message = "离线 C worker 通信失败：" + ex.Message;
                LastStatus = message;
                EngineAvailable = false;
                return OfflineWorkerResult.Fail(message);
            }
        }
    }

    private bool TryEnsureStarted(out string error)
    {
        error = "";
        if (_process is { HasExited: false } && _stdin != null && _stdout != null)
        {
            return true;
        }

        string workerPath = ResolveWorkerPath();
        if (workerPath.Length == 0)
        {
            error = "离线 C worker 未随包发布。";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory
            };
            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _lastWorkerError = e.Data;
                }
            };
            if (!process.Start())
            {
                error = "离线 C worker 启动失败。";
                return false;
            }
            process.BeginErrorReadLine();
            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            return true;
        }
        catch (Exception ex)
        {
            error = "离线 C worker 启动失败：" + ex.Message;
            if (!string.IsNullOrWhiteSpace(_lastWorkerError))
            {
                error += "；" + _lastWorkerError;
            }
            return false;
        }
    }

    private void TrySendShutdown()
    {
        if (_stdin == null)
        {
            return;
        }

        try
        {
            int id = unchecked(++_nextId);
            string request = JsonSerializer.Serialize(new OfflineWorkerRequest { Id = id, Command = "Shutdown" }, JsonOptions);
            _stdin.WriteLine(request);
            _stdin.Flush();
        }
        catch
        {
        }
    }

    private void RestartAfterProtocolError()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        _stdin?.Dispose();
        _stdout?.Dispose();
        _process?.Dispose();
        _stdin = null;
        _stdout = null;
        _process = null;
    }

    private static string ResolveWorkerPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "offline_c_worker", "CanVariableMonitor.OfflineCWorker.exe"),
            Path.Combine(baseDir, "CanVariableMonitor.OfflineCWorker.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CanVariableMonitor.OfflineCWorker", "bin", "Debug", "net9.0", "CanVariableMonitor.OfflineCWorker.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CanVariableMonitor.OfflineCWorker", "bin", "Release", "net9.0", "CanVariableMonitor.OfflineCWorker.exe"))
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }
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

internal sealed class OfflineWorkerResult
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public bool EngineAvailable { get; set; }
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, uint> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Coverage { get; set; } = new();

    public static OfflineWorkerResult Fail(string message)
    {
        return new OfflineWorkerResult
        {
            Ok = false,
            EngineAvailable = false,
            Error = message,
            Status = message
        };
    }
}

internal sealed class OfflineWorkerRequest
{
    public int Id { get; set; }
    public string Command { get; set; } = "";
    public JsonElement? Payload { get; set; }
}
