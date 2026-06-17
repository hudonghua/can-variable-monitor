using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal sealed record KeilProjectInfo(string ProjectFile, string TargetName, string OutputDirectory, string ListingPath, string OutputName);

internal sealed record KeilBuildDiagnostic(string FilePath, int Line, string Severity, string Message)
{
    public override string ToString()
    {
        string file = string.IsNullOrWhiteSpace(FilePath) ? "" : FilePath + (Line > 0 ? ":" + Line.ToString(CultureInfo.InvariantCulture) : "");
        return string.IsNullOrWhiteSpace(file)
            ? $"{Severity}: {Message}"
            : $"{file}  {Severity}: {Message}";
    }
}

internal sealed class KeilBuildResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string ProjectFile { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string LogPath { get; init; } = "";
    public string LogText { get; init; } = "";
    public string AxfPath { get; init; } = "";
    public string MapPath { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public List<KeilBuildDiagnostic> Diagnostics { get; } = new();
}

internal static class KeilBuildService
{
    private const int BuildTimeoutMs = 180000;
    private readonly record struct XmlTagBlock(int Start, int End, string Text);

    public static KeilProjectInfo? FindProject(string rootOrProjectFile, string preferredProjectFile = "", string preferredTargetName = "")
    {
        string projectFile = "";
        if (!string.IsNullOrWhiteSpace(preferredProjectFile) && File.Exists(preferredProjectFile))
        {
            projectFile = preferredProjectFile;
        }
        else if (File.Exists(rootOrProjectFile) && IsKeilProject(rootOrProjectFile))
        {
            projectFile = rootOrProjectFile;
        }
        else if (Directory.Exists(rootOrProjectFile))
        {
            projectFile = Directory.EnumerateFiles(rootOrProjectFile, "*.*", SearchOption.AllDirectories)
                .Where(IsKeilProject)
                .OrderByDescending(ScoreProjectFile)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "";
        }

        if (projectFile.Length == 0)
        {
            return null;
        }

        IReadOnlyList<string> targets = ReadTargets(projectFile);
        string target = targets.FirstOrDefault(t => t.Equals(preferredTargetName, StringComparison.OrdinalIgnoreCase)) ??
            targets.FirstOrDefault(t => t.Contains("FLASH", StringComparison.OrdinalIgnoreCase)) ??
            targets.FirstOrDefault() ??
            "";
        if (target.Length == 0)
        {
            return null;
        }

        TargetOutputInfo output = FindTargetOutputInfo(projectFile, target) ?? new TargetOutputInfo();
        return new KeilProjectInfo(projectFile, target, output.OutputDirectory, output.ListingPath, output.OutputName);
    }

    public static KeilBuildResult BuildProject(string rootOrProjectFile, string preferredProjectFile = "", string preferredTargetName = "")
    {
        string? uv4 = KeilToolLocator.FindUv4();
        if (uv4 == null)
        {
            return new KeilBuildResult
            {
                Success = false,
                Message = "未找到 Keil UV4.exe，自动构建已跳过。"
            };
        }

        KeilProjectInfo? project = FindProject(rootOrProjectFile, preferredProjectFile, preferredTargetName);
        if (project == null)
        {
            return new KeilBuildResult
            {
                Success = false,
                Message = "没有找到 Keil 工程或 Target，自动构建已跳过。"
            };
        }

        string projectDir = Path.GetDirectoryName(project.ProjectFile)!;
        string logPath = Path.Combine(projectDir, "canmon_v143_build.log");
        try
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
        catch
        {
        }

        DateTime startedUtc = DateTime.UtcNow;
        using var process = new Process();
        process.StartInfo.FileName = uv4;
        process.StartInfo.Arguments = "-b \"" + project.ProjectFile + "\" -t \"" + project.TargetName + "\" -o \"" + logPath + "\"";
        process.StartInfo.WorkingDirectory = projectDir;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        AddArmccToPath(process, uv4);

        try
        {
            process.Start();
            if (!process.WaitForExit(BuildTimeoutMs))
            {
                try { process.Kill(); } catch { }
                return BuildResultFromLog(project, logPath, startedUtc, false, "Keil 编译超时，源码已保留。");
            }
        }
        catch (Exception ex)
        {
            return new KeilBuildResult
            {
                Success = false,
                ProjectFile = project.ProjectFile,
                TargetName = project.TargetName,
                LogPath = logPath,
                StartedUtc = startedUtc,
                Message = "启动 Keil 编译失败：" + ex.Message
            };
        }

        string logText = ReadLogText(logPath);
        bool success = IsBuildSuccess(logText);
        string message = success
            ? "Keil 编译通过：" + project.TargetName
            : "Keil 编译失败，源码已保留。日志：" + logPath;
        return BuildResultFromLog(project, logPath, startedUtc, success, message);
    }

    public static IReadOnlyList<KeilBuildDiagnostic> ParseDiagnostics(string logText, string projectDir = "")
    {
        var diagnostics = new List<KeilBuildDiagnostic>();
        if (string.IsNullOrWhiteSpace(logText))
        {
            return diagnostics;
        }

        string[] lines = logText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match armcc = Regex.Match(line, @"^(?<file>.+?)\((?<line>\d+)\)\s*:\s*(?<sev>error|warning)\s*:\s*(?<msg>.+)$", RegexOptions.IgnoreCase);
            if (armcc.Success)
            {
                string file = armcc.Groups["file"].Value.Trim('"');
                if (!Path.IsPathRooted(file) && !string.IsNullOrWhiteSpace(projectDir))
                {
                    file = Path.GetFullPath(Path.Combine(projectDir, file));
                }
                diagnostics.Add(new KeilBuildDiagnostic(
                    file,
                    int.TryParse(armcc.Groups["line"].Value, out int lineNumber) ? lineNumber : 0,
                    armcc.Groups["sev"].Value.ToLowerInvariant(),
                    armcc.Groups["msg"].Value.Trim()));
                continue;
            }

            Match generic = Regex.Match(line, @"(?<sev>\*\*\*\s*Error|Error|Warning)\s*:?\s*(?<msg>.+)$", RegexOptions.IgnoreCase);
            if (generic.Success)
            {
                diagnostics.Add(new KeilBuildDiagnostic(
                    "",
                    0,
                    generic.Groups["sev"].Value.Contains("warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "error",
                    generic.Groups["msg"].Value.Trim()));
            }
        }

        return diagnostics;
    }

    public static int RunSelfTest(TextWriter output)
    {
        string log =
            ".\\Src\\main.c(42): error:  #20: identifier \"x\" is undefined\r\n" +
            "App.c(7): warning:  #177-D: variable \"y\" was declared but never referenced\r\n" +
            "*** Error: Target not created.\r\n";
        IReadOnlyList<KeilBuildDiagnostic> diagnostics = ParseDiagnostics(log, @"C:\demo");
        bool ok = diagnostics.Count >= 3 &&
            diagnostics[0].FilePath.EndsWith(@"Src\main.c", StringComparison.OrdinalIgnoreCase) &&
            diagnostics[0].Line == 42 &&
            diagnostics[0].Severity == "error" &&
            diagnostics[1].Severity == "warning";
        output.WriteLine(ok ? "KeilBuildServiceSelfTest: PASS" : "KeilBuildServiceSelfTest: FAIL");
        output.WriteLine($"  diagnostics={diagnostics.Count}");
        return ok ? 0 : 1;
    }

    private static KeilBuildResult BuildResultFromLog(KeilProjectInfo project, string logPath, DateTime startedUtc, bool success, string message)
    {
        string logText = ReadLogText(logPath);
        string projectDir = Path.GetDirectoryName(project.ProjectFile)!;
        string axfPath = FindOutputArtifact(project, ".axf", startedUtc);
        string mapPath = FindOutputArtifact(project, ".map", startedUtc);
        var result = new KeilBuildResult
        {
            Success = success,
            ProjectFile = project.ProjectFile,
            TargetName = project.TargetName,
            LogPath = logPath,
            LogText = logText,
            StartedUtc = startedUtc,
            AxfPath = axfPath,
            MapPath = mapPath,
            Message = message
        };
        result.Diagnostics.AddRange(ParseDiagnostics(logText, projectDir));
        return result;
    }

    private static string FindOutputArtifact(KeilProjectInfo project, string extension, DateTime startedUtc)
    {
        string projectDir = Path.GetDirectoryName(project.ProjectFile)!;
        var roots = new List<string>
        {
            ResolveProjectPath(projectDir, project.OutputDirectory),
            ResolveProjectPath(projectDir, project.ListingPath),
            projectDir
        };
        string baseName = string.IsNullOrWhiteSpace(project.OutputName)
            ? Path.GetFileNameWithoutExtension(project.ProjectFile)
            : project.OutputName;
        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string direct = Path.Combine(root, baseName + extension);
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        return roots.Where(Directory.Exists)
            .SelectMany(root =>
            {
                try
                {
                    return Directory.EnumerateFiles(root, "*" + extension, SearchOption.AllDirectories);
                }
                catch
                {
                    return Enumerable.Empty<string>();
                }
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? "";
    }

    private static bool IsBuildSuccess(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return false;
        }
        bool zeroErrors = logText.Contains("0 Error(s)", StringComparison.OrdinalIgnoreCase);
        bool hasError = logText.Contains(" error:", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("*** Error", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("Target not created", StringComparison.OrdinalIgnoreCase);
        return zeroErrors && !hasError;
    }

    private static string ReadLogText(string logPath)
    {
        try
        {
            return File.Exists(logPath) ? File.ReadAllText(logPath, Encoding.Default) : "";
        }
        catch
        {
            return "";
        }
    }

    private static void AddArmccToPath(Process process, string uv4)
    {
        string? uv4Dir = Path.GetDirectoryName(uv4);
        string? keilRoot = uv4Dir == null ? null : Path.GetDirectoryName(uv4Dir);
        if (keilRoot == null)
        {
            return;
        }

        string[] bins =
        {
            Path.Combine(keilRoot, "ARM", "ARMCC", "Bin"),
            Path.Combine(keilRoot, "ARM", "ARMCLANG", "bin"),
            Path.Combine(keilRoot, "ARM", "BIN40")
        };
        foreach (string bin in bins)
        {
            if (Directory.Exists(bin))
            {
                process.StartInfo.Environment["Path"] = bin + ";" + process.StartInfo.Environment["Path"];
            }
        }
    }

    private static bool IsKeilProject(string file)
    {
        string ext = Path.GetExtension(file);
        return ext.Equals(".uvprojx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".uvproj", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreProjectFile(string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file, Encoding.Default);
        }
        catch
        {
            return 0;
        }
        int score = 0;
        if (text.Contains("<TargetName>", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        if (text.Contains("FLASH", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }
        score += Math.Min(50, Regex.Matches(text, @"\.(c|h)</FilePath>", RegexOptions.IgnoreCase).Count);
        return score;
    }

    private static IReadOnlyList<string> ReadTargets(string projectFile)
    {
        string text = File.ReadAllText(projectFile, Encoding.Default);
        return Regex.Matches(text, @"<TargetName>(?<name>.*?)</TargetName>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["name"].Value).Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TargetOutputInfo? FindTargetOutputInfo(string projectFile, string targetName)
    {
        string text = File.ReadAllText(projectFile, Encoding.Default);
        foreach (XmlTagBlock target in EnumerateTopLevelTagBlocks(text, "Target"))
        {
            string body = target.Text;
            string name = ExtractTagValue(body, "TargetName");
            if (!name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return new TargetOutputInfo
            {
                OutputDirectory = ExtractTagValue(body, "OutputDirectory"),
                ListingPath = ExtractTagValue(body, "ListingPath"),
                OutputName = ExtractTagValue(body, "OutputName")
            };
        }
        return null;
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

    private static string ExtractTagValue(string text, string tag)
    {
        Match match = Regex.Match(text, "<" + Regex.Escape(tag) + @">(?<value>.*?)</" + Regex.Escape(tag) + ">", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : "";
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

    private sealed class TargetOutputInfo
    {
        public string OutputDirectory { get; init; } = "";
        public string ListingPath { get; init; } = "";
        public string OutputName { get; init; } = "";
    }
}
