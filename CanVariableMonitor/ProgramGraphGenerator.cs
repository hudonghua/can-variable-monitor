using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal sealed class ProgramGraphResult
{
    public bool Success { get; init; }
    public string ReportPath { get; init; } = "";
    public string Message { get; init; } = "";
    public int SourceFileCount { get; init; }
    public int FunctionCount { get; init; }
    public int EdgeCount { get; init; }
}

internal sealed class ProgramGraphSnapshot
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int SourceFileCount { get; init; }
    public int FunctionCount { get; init; }
    public int EdgeCount { get; init; }
    public string StartFunction { get; init; } = "";
    public IReadOnlyList<ProgramFrameworkStep> FrameworkSteps { get; init; } = Array.Empty<ProgramFrameworkStep>();
    public IReadOnlyList<ProgramFunctionInfo> FlowFunctions { get; init; } = Array.Empty<ProgramFunctionInfo>();
    public IReadOnlyList<ProgramFunctionInfo> HotFunctions { get; init; } = Array.Empty<ProgramFunctionInfo>();
    public IReadOnlyList<ProgramCallGraphNode> CallGraphNodes { get; init; } = Array.Empty<ProgramCallGraphNode>();
    public IReadOnlyList<ProgramCallGraphEdge> CallGraphEdges { get; init; } = Array.Empty<ProgramCallGraphEdge>();
    public IReadOnlyList<ProgramCallGraphNode> AllCallGraphNodes { get; init; } = Array.Empty<ProgramCallGraphNode>();
    public IReadOnlyList<ProgramCallGraphEdge> AllCallGraphEdges { get; init; } = Array.Empty<ProgramCallGraphEdge>();
}

internal sealed record ProgramFrameworkStep(string Name, string Detail, ushort TraceId, string FunctionName, string Summary = "");

internal sealed record ProgramFunctionInfo(string Name, string FilePath, int Incoming, int Outgoing, ushort TraceId, string Summary = "");

internal sealed record ProgramCallGraphNode(string Id, string Name, string FilePath, int Level, int Incoming, int Outgoing, string Kind, ushort TraceId, string Summary = "");

internal sealed record ProgramCallGraphEdge(string FromId, string ToId);

internal static partial class ProgramGraphGenerator
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", "bin", "obj", "Debug", "Release", "Listings", "Objects", "RTE", "__pycache__"
    };

    private static readonly HashSet<string> KeywordCalls = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "return", "sizeof", "case", "do", "else", "typedef",
        "defined", "__asm", "__asm__", "__attribute__", "__irq", "__weak"
    };

    public static ProgramGraphResult Generate(string workDirectory)
    {
        RawProgramGraph raw = AnalyzeRaw(workDirectory);
        if (!raw.Success)
        {
            return new ProgramGraphResult
            {
                SourceFileCount = raw.SourceFileCount,
                FunctionCount = raw.Functions.Count,
                EdgeCount = raw.Edges.Count,
                Message = raw.Message
            };
        }

        string outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanVariableMonitor");
        Directory.CreateDirectory(outputDirectory);
        string reportPath = Path.Combine(outputDirectory, "program_graph.html");
        File.WriteAllText(reportPath, BuildHtml(workDirectory, raw.Files, raw.Functions, raw.Edges), new UTF8Encoding(false));

        return new ProgramGraphResult
        {
            Success = true,
            ReportPath = reportPath,
            SourceFileCount = raw.SourceFileCount,
            FunctionCount = raw.Functions.Count,
            EdgeCount = raw.Edges.Count,
            Message = $"程序图谱已生成：{raw.Functions.Count} 个函数，{raw.Edges.Count} 条引用关系。"
        };
    }

    public static ProgramGraphSnapshot Analyze(string workDirectory)
    {
        RawProgramGraph raw = AnalyzeRaw(workDirectory);
        if (!raw.Success)
        {
            return new ProgramGraphSnapshot
            {
                SourceFileCount = raw.SourceFileCount,
                FunctionCount = raw.Functions.Count,
                EdgeCount = raw.Edges.Count,
                Message = raw.Message
            };
        }

        List<FunctionNode> businessFunctions = raw.Functions.Where(IsBusinessFunction).ToList();
        if (businessFunctions.Count < 3)
        {
            businessFunctions = raw.Functions.Where(f => !IsLowLevelFunction(f)).ToList();
        }

        if (businessFunctions.Count == 0)
        {
            businessFunctions = raw.Functions;
        }

        HashSet<string> businessIds = businessFunctions.Select(f => f.Id).ToHashSet();
        HashSet<CallEdge> businessEdges = raw.Edges
            .Where(e => businessIds.Contains(e.FromId) && businessIds.Contains(e.ToId))
            .ToHashSet();
        Dictionary<string, FunctionNode> byId = businessFunctions.ToDictionary(f => f.Id);
        Dictionary<string, int> incoming = businessEdges.GroupBy(e => e.ToId).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> outgoing = businessEdges.GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.Count());
        FunctionNode start = PickFlowStart(businessFunctions);
        FunctionNode? primaryEntry = FindPrimaryBusinessEntry(raw.Functions, start);
        IReadOnlyList<ProgramFrameworkStep> frameworkSteps = BuildFrameworkSteps(raw.Functions);
        IReadOnlyList<ProgramFunctionInfo> flow = BuildFlowList(businessFunctions, businessEdges, start.Id, 18);
        (IReadOnlyList<ProgramCallGraphNode> callGraphNodes, IReadOnlyList<ProgramCallGraphEdge> callGraphEdges) callGraph =
            BuildCallGraph(raw.Functions, raw.Edges, start);
        (IReadOnlyList<ProgramCallGraphNode> allCallGraphNodes, IReadOnlyList<ProgramCallGraphEdge> allCallGraphEdges) allCallGraph =
            BuildCompleteCallGraph(raw.Functions, raw.Edges, start);
        IReadOnlyList<ProgramFunctionInfo> hot = businessEdges
            .SelectMany(e => new[] { e.FromId, e.ToId })
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .Take(18)
            .Select(g => ToInfo(byId[g.Key], incoming, outgoing))
            .ToList();

        if (hot.Count == 0)
        {
            hot = businessFunctions.Take(18).Select(f => ToInfo(f, incoming, outgoing)).ToList();
        }

        if (primaryEntry != null)
        {
            flow = BuildLogicCallInfos(raw.Functions, primaryEntry, 24);
            hot = flow;
        }

        return new ProgramGraphSnapshot
        {
            Success = true,
            SourceFileCount = raw.SourceFileCount,
            FunctionCount = raw.Functions.Count,
            EdgeCount = raw.Edges.Count,
            StartFunction = primaryEntry?.Name ?? start.Name,
            FrameworkSteps = frameworkSteps,
            FlowFunctions = flow,
            HotFunctions = hot,
            CallGraphNodes = callGraph.callGraphNodes,
            CallGraphEdges = callGraph.callGraphEdges,
            AllCallGraphNodes = allCallGraph.allCallGraphNodes,
            AllCallGraphEdges = allCallGraph.allCallGraphEdges,
            Message = $"已识别 {callGraph.callGraphNodes.Count} 个核心业务节点。"
        };
    }

    private static RawProgramGraph AnalyzeRaw(string workDirectory)
    {
        if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
        {
            return new RawProgramGraph(Message: "工作目录不存在。");
        }

        List<string> files = EnumerateSourceFiles(workDirectory).ToList();
        if (files.Count == 0)
        {
            return new RawProgramGraph(SourceFileCount: 0, Message: "没有找到 C/H 源文件。");
        }

        List<FunctionNode> functions = new();
        foreach (string file in files)
        {
            string text = ReadSourceText(file);
            functions.AddRange(ParseFunctions(workDirectory, file, text));
        }
        functions = functions
            .Where(function => !IsMonitorInternalFunctionName(function.Name) &&
                !function.FilePath.Contains("can_monitor", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (functions.Count == 0)
        {
            return new RawProgramGraph(Files: files, SourceFileCount: files.Count, Message: "找到源文件，但没有识别到函数定义。");
        }

        Dictionary<string, List<FunctionNode>> byName = functions
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        HashSet<CallEdge> edges = new();
        foreach (FunctionNode function in functions)
        {
            foreach (Match match in CallRegex().Matches(function.Body))
            {
                string calleeName = match.Groups["name"].Value;
                if (KeywordCalls.Contains(calleeName) ||
                    IsMonitorInternalFunctionName(calleeName) ||
                    !byName.TryGetValue(calleeName, out List<FunctionNode>? targets))
                {
                    continue;
                }

                FunctionNode target = ChooseBestTarget(function, targets);
                if (!ReferenceEquals(function, target))
                {
                    edges.Add(new CallEdge(function.Id, target.Id));
                }
            }
        }

        return new RawProgramGraph(true, files, functions, edges, files.Count, "");
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (string child in children)
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static string ReadSourceText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes);
        }
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes);
            }
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private static List<FunctionNode> ParseFunctions(string root, string file, string rawText)
    {
        string text = RemoveCommentsAndLiterals(rawText);
        List<FunctionNode> result = new();
        string relativePath = Path.GetRelativePath(root, file);
        foreach (Match match in FunctionRegex().Matches(text))
        {
            string name = match.Groups["name"].Value;
            if (KeywordCalls.Contains(name))
            {
                continue;
            }

            int braceIndex = text.IndexOf('{', match.Index + match.Length - 1);
            if (braceIndex < 0)
            {
                continue;
            }

            int endIndex = FindMatchingBrace(text, braceIndex);
            if (endIndex <= braceIndex)
            {
                continue;
            }

            string body = text.Substring(braceIndex + 1, endIndex - braceIndex - 1);
            string summary = ExtractFunctionSummary(rawText, match.Index, braceIndex, name);
            summary = EmbeddedCodeKnowledge.ImproveSummary(name, relativePath, body, summary);
            result.Add(new FunctionNode(
                Id: "F" + result.Count.ToString("D4") + "_" + StableToken(relativePath + "_" + name),
                Name: name,
                FilePath: relativePath,
                Body: body,
                Summary: summary));
        }

        return result;
    }

    private static string ExtractFunctionSummary(string rawText, int functionStart, int braceIndex, string functionName)
    {
        string comment = ExtractLeadingFunctionComment(rawText, functionStart);
        if (comment.Length == 0)
        {
            comment = ExtractSignatureLineComment(rawText, functionStart, braceIndex);
        }
        if (comment.Length == 0)
        {
            comment = ExtractFirstBodyComment(rawText, braceIndex);
        }

        string summary = SimplifyComment(comment, functionName);
        return summary.Length > 0 ? summary : BusinessDisplayName(functionName);
    }

    private static string ExtractLeadingFunctionComment(string text, int functionStart)
    {
        if (functionStart <= 0)
        {
            return "";
        }

        int end = functionStart - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
        {
            end--;
        }
        if (end < 0)
        {
            return "";
        }

        if (end > 0 && text[end] == '/' && text[end - 1] == '*')
        {
            int start = text.LastIndexOf("/*", end - 1, StringComparison.Ordinal);
            if (start >= 0)
            {
                return text.Substring(start, end - start + 1);
            }
        }

        int lineStart = text.LastIndexOf('\n', end);
        if (lineStart < 0)
        {
            lineStart = 0;
        }
        else
        {
            lineStart++;
        }

        var lines = new List<string>();
        int scanEnd = end;
        int blankCount = 0;
        while (scanEnd >= 0)
        {
            int start = text.LastIndexOf('\n', scanEnd);
            int lineBegin = start < 0 ? 0 : start + 1;
            string line = text.Substring(lineBegin, scanEnd - lineBegin + 1).Trim();
            if (line.Length == 0)
            {
                blankCount++;
                if (blankCount > 1 || lines.Count > 0)
                {
                    break;
                }
                scanEnd = start - 1;
                continue;
            }

            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            lines.Add(line);
            scanEnd = start - 1;
        }

        lines.Reverse();
        return string.Join('\n', lines);
    }

    private static string ExtractSignatureLineComment(string text, int functionStart, int braceIndex)
    {
        if (braceIndex <= functionStart || functionStart < 0 || functionStart >= text.Length)
        {
            return "";
        }

        string signature = text.Substring(functionStart, Math.Min(text.Length, braceIndex + 1) - functionStart);
        int lineComment = signature.IndexOf("//", StringComparison.Ordinal);
        if (lineComment >= 0)
        {
            return signature.Substring(lineComment);
        }

        int blockComment = signature.IndexOf("/*", StringComparison.Ordinal);
        if (blockComment >= 0)
        {
            int blockEnd = signature.IndexOf("*/", blockComment + 2, StringComparison.Ordinal);
            if (blockEnd > blockComment)
            {
                return signature.Substring(blockComment, blockEnd - blockComment + 2);
            }
        }

        return "";
    }

    private static string ExtractFirstBodyComment(string text, int braceIndex)
    {
        if (braceIndex < 0 || braceIndex + 1 >= text.Length)
        {
            return "";
        }

        int i = braceIndex + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i + 1 >= text.Length)
        {
            return "";
        }

        if (text[i] == '/' && text[i + 1] == '/')
        {
            int end = text.IndexOf('\n', i);
            return end < 0 ? text.Substring(i) : text.Substring(i, end - i);
        }

        if (text[i] == '/' && text[i + 1] == '*')
        {
            int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            if (end >= 0)
            {
                return text.Substring(i, end - i + 2);
            }
        }

        return "";
    }

    private static string SimplifyComment(string comment, string functionName)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return "";
        }

        var candidates = CleanCommentLines(comment)
            .Where(line => line.Length > 0)
            .Where(line => !IsCommentNoise(line, functionName))
            .Select(line => CompactCommentMeaning(line, functionName))
            .Where(line => line.Length > 0)
            .ToList();

        string text = candidates.FirstOrDefault() ?? "";
        if (text.Length == 0)
        {
            text = CleanCommentLines(comment)
                .Select(line => CompactCommentMeaning(line, functionName))
                .FirstOrDefault(line => line.Length > 0) ?? "";
        }

        return ShortenSummary(text);
    }

    private static IEnumerable<string> CleanCommentLines(string comment)
    {
        string normalized = comment
            .Replace("\r", "\n")
            .Replace("/*", "\n")
            .Replace("*/", "\n")
            .Replace("//", "\n");

        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = rawLine.Trim();
            line = Regex.Replace(line, @"^[\*\s/\\\-_=]+", "");
            line = Regex.Replace(line, @"[\*\s/\\\-_=]+$", "");
            line = Regex.Replace(line, @"\s+", " ").Trim();
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static bool IsCommentNoise(string line, string functionName)
    {
        if (line.Length < 2)
        {
            return true;
        }
        if (Regex.IsMatch(line, @"^[=\-*#~_ ]+$"))
        {
            return true;
        }
        if (line.Contains(".c", StringComparison.OrdinalIgnoreCase) || line.Contains(".h", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (line.Contains(functionName, StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(line, @"(?:函数名|函数名称|function|name|函数)\s*[:：]?", RegexOptions.IgnoreCase))
        {
            return true;
        }
        if (Regex.IsMatch(line, @"^(?:参数|输入|输出|返回|作者|日期|版本|param|return|author|date|version)\b", RegexOptions.IgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static string CompactCommentMeaning(string line, string functionName)
    {
        string text = line.Trim();
        text = Regex.Replace(text, @"^[@\\]brief\s*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^(?:功能|说明|描述|作用|用途|函数功能|函数说明)\s*[:：\-]?\s*", "");
        text = Regex.Replace(text, @"^(?:brief|desc|description)\s*[:：\-]?\s*", "", RegexOptions.IgnoreCase);
        text = text.Replace(functionName, "", StringComparison.OrdinalIgnoreCase).Trim();
        text = Regex.Replace(text, @"\b[A-Za-z0-9_]+\.(?:c|h)\b", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', ':', '：', '-', ';', '；', ',', '，', '.', '。');
        return text;
    }

    private static string ShortenSummary(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= 22)
        {
            return text;
        }

        int stop = text.IndexOfAny(new[] { '。', '；', ';', '，', ',', '.', '、' });
        if (stop > 0 && stop <= 22)
        {
            return text.Substring(0, stop).Trim();
        }

        return text.Substring(0, 22).Trim() + "...";
    }

    private static string RemoveCommentsAndLiterals(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool lineComment = false;
        bool blockComment = false;
        bool stringLiteral = false;
        bool charLiteral = false;
        bool escape = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (lineComment)
            {
                if (c == '\n')
                {
                    lineComment = false;
                    builder.Append(c);
                }
                else
                {
                    builder.Append(' ');
                }
                continue;
            }

            if (blockComment)
            {
                if (c == '*' && next == '/')
                {
                    blockComment = false;
                    builder.Append("  ");
                    i++;
                }
                else
                {
                    builder.Append(c == '\n' ? '\n' : ' ');
                }
                continue;
            }

            if (stringLiteral || charLiteral)
            {
                if (escape)
                {
                    escape = false;
                    builder.Append(' ');
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    builder.Append(' ');
                    continue;
                }

                if ((stringLiteral && c == '"') || (charLiteral && c == '\''))
                {
                    stringLiteral = false;
                    charLiteral = false;
                }

                builder.Append(c == '\n' ? '\n' : ' ');
                continue;
            }

            if (c == '/' && next == '/')
            {
                lineComment = true;
                builder.Append("  ");
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                blockComment = true;
                builder.Append("  ");
                i++;
                continue;
            }

            if (c == '"')
            {
                stringLiteral = true;
                builder.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                charLiteral = true;
                builder.Append(' ');
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
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

    private static FunctionNode ChooseBestTarget(FunctionNode caller, List<FunctionNode> targets)
    {
        FunctionNode? sameFile = targets.FirstOrDefault(t => t.FilePath.Equals(caller.FilePath, StringComparison.OrdinalIgnoreCase));
        return sameFile ?? targets[0];
    }

    private static string BuildHtml(string root, List<string> sourceFiles, List<FunctionNode> functions, HashSet<CallEdge> edges)
    {
        string moduleGraph = BuildModuleGraph(functions, edges);
        string flowGraph = BuildFlowGraph(functions, edges);
        string functionGraph = BuildFunctionGraph(functions, edges);
        string topFunctions = BuildTopFunctions(functions, edges);

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>程序图谱</title>
  <script src="https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js"></script>
  <style>
    :root {
      color-scheme: dark;
      --bg: #090c12;
      --panel: #111827;
      --surface: #172033;
      --ink: #e5e7eb;
      --muted: #94a3b8;
      --accent: #0ea5e9;
      --line: #263348;
    }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--ink);
      font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif;
    }
    header {
      padding: 22px 30px 18px;
      background: #020617;
      border-bottom: 1px solid var(--line);
    }
    h1 {
      margin: 0 0 6px;
      font-size: 26px;
      letter-spacing: 0;
    }
    .meta {
      color: var(--muted);
      font-size: 13px;
    }
    main {
      padding: 18px 30px 36px;
      display: grid;
      gap: 18px;
    }
    section {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 16px;
    }
    h2 {
      margin: 0 0 12px;
      font-size: 17px;
    }
    .stats {
      display: grid;
      grid-template-columns: repeat(4, minmax(120px, 1fr));
      gap: 10px;
    }
    .stat {
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 12px;
    }
    .num {
      color: var(--accent);
      font-size: 24px;
      font-weight: 700;
    }
    .label {
      color: var(--muted);
      font-size: 12px;
      margin-top: 4px;
    }
    .mermaid {
      background: #0f172a;
      border-radius: 6px;
      padding: 14px;
      overflow: auto;
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
    }
    th, td {
      border-bottom: 1px solid var(--line);
      text-align: left;
      padding: 9px 8px;
    }
    th {
      color: var(--muted);
      font-weight: 600;
    }
    code {
      color: #bae6fd;
    }
  </style>
</head>
<body>
  <header>
    <h1>程序图谱</h1>
    <div class="meta">{{Html(root)}} · {{DateTime.Now:yyyy-MM-dd HH:mm:ss}}</div>
  </header>
  <main>
    <section class="stats">
      <div class="stat"><div class="num">{{sourceFiles.Count}}</div><div class="label">源文件</div></div>
      <div class="stat"><div class="num">{{functions.Count}}</div><div class="label">函数</div></div>
      <div class="stat"><div class="num">{{edges.Count}}</div><div class="label">引用关系</div></div>
      <div class="stat"><div class="num">{{functions.Select(f => f.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()}}</div><div class="label">模块</div></div>
    </section>
    <section>
      <h2>程序框架图</h2>
      <pre class="mermaid">{{moduleGraph}}</pre>
    </section>
    <section>
      <h2>主流程图</h2>
      <pre class="mermaid">{{flowGraph}}</pre>
    </section>
    <section>
      <h2>函数引用图</h2>
      <pre class="mermaid">{{functionGraph}}</pre>
    </section>
    <section>
      <h2>重点函数</h2>
      {{topFunctions}}
    </section>
  </main>
  <script>
    mermaid.initialize({ startOnLoad: true, theme: "dark", securityLevel: "loose" });
  </script>
</body>
</html>
""";
    }

    private static string BuildModuleGraph(List<FunctionNode> functions, HashSet<CallEdge> edges)
    {
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        List<string> modules = functions.Select(f => f.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(80).ToList();
        Dictionary<string, string> moduleIds = modules.Select((m, i) => new { m, id = "M" + i }).ToDictionary(x => x.m, x => x.id, StringComparer.OrdinalIgnoreCase);

        var lines = new List<string> { "flowchart LR" };
        foreach (string module in modules)
        {
            lines.Add($"  {moduleIds[module]}[\"{Mermaid(module)}\"]");
        }

        foreach (var edge in edges.Take(600))
        {
            if (!byId.TryGetValue(edge.FromId, out FunctionNode? from) || !byId.TryGetValue(edge.ToId, out FunctionNode? to))
            {
                continue;
            }

            if (from.FilePath.Equals(to.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (moduleIds.TryGetValue(from.FilePath, out string? fromModule) && moduleIds.TryGetValue(to.FilePath, out string? toModule))
            {
                lines.Add($"  {fromModule} --> {toModule}");
            }
        }

        if (lines.Count == modules.Count + 1)
        {
            lines.Add("  A[\"当前工程模块之间没有识别到跨文件调用\"]");
        }

        return string.Join('\n', lines.Distinct());
    }

    private static string BuildFlowGraph(List<FunctionNode> functions, HashSet<CallEdge> edges)
    {
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        FunctionNode start = PickFlowStart(functions);
        Dictionary<string, List<string>> outgoing = edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).Distinct().ToList());

        var used = new HashSet<string> { start.Id };
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((start.Id, 0));
        var graphEdges = new List<CallEdge>();
        while (queue.Count > 0 && used.Count < 55)
        {
            var current = queue.Dequeue();
            if (current.Depth >= 5 || !outgoing.TryGetValue(current.Id, out List<string>? next))
            {
                continue;
            }

            foreach (string target in next.Take(8))
            {
                graphEdges.Add(new CallEdge(current.Id, target));
                if (used.Add(target))
                {
                    queue.Enqueue((target, current.Depth + 1));
                }
            }
        }

        var lines = new List<string> { "flowchart TD" };
        foreach (string id in used)
        {
            FunctionNode function = byId[id];
            lines.Add($"  {id}[\"{Mermaid(function.Name)}<br/>{Mermaid(FunctionSummary(function))}\"]");
        }

        foreach (CallEdge edge in graphEdges)
        {
            if (used.Contains(edge.FromId) && used.Contains(edge.ToId))
            {
                lines.Add($"  {edge.FromId} --> {edge.ToId}");
            }
        }

        return string.Join('\n', lines.Distinct());
    }

    private static FunctionNode PickFlowStart(List<FunctionNode> functions)
    {
        string[] preferred =
        {
            "MyLogic_10ms", "MyLogic", "Work", "Control", "Ctrl", "Logic", "Process", "Usr", "App", "main"
        };

        foreach (string name in preferred)
        {
            FunctionNode? exact = functions.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            FunctionNode? contains = functions.FirstOrDefault(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (contains != null)
            {
                return contains;
            }
        }

        return functions[0];
    }

    private static FunctionNode? FindPrimaryBusinessEntry(List<FunctionNode> functions, FunctionNode preferredStart)
    {
        FunctionNode? exact10ms = FindFunction(functions, "MyLogic_10ms");
        if (exact10ms != null && HasMeaningfulBusinessBody(exact10ms))
        {
            return exact10ms;
        }

        FunctionNode? bestPeriodic = FindPeriodicEntryCandidates(functions).FirstOrDefault();
        if (bestPeriodic != null)
        {
            return bestPeriodic;
        }

        if (IsBusinessFunction(preferredStart) &&
            !IsSchedulerTickFunction(preferredStart) &&
            HasMeaningfulBusinessBody(preferredStart) &&
            !preferredStart.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return preferredStart;
        }

        return functions
            .Where(f => IsBusinessSourceFile(f) && IsBusinessFunctionName(f.Name) && !IsLowLevelFunction(f))
            .OrderByDescending(f => CountDirectCallLikeTokens(f.Body))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<FunctionNode> FindPeriodicEntryCandidates(List<FunctionNode> functions)
    {
        return functions
            .Where(IsPeriodicEntryCandidate)
            .OrderByDescending(PeriodicEntryScore)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPeriodicEntryCandidate(FunctionNode function)
    {
        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        string combined = path + "/" + name;
        if (IsSchedulerTickFunction(function))
        {
            return false;
        }

        if (name.Equals("MyLogic_10ms", StringComparison.OrdinalIgnoreCase))
        {
            return HasMeaningfulBusinessBody(function);
        }

        if (name.Equals("MyLogic_1ms", StringComparison.OrdinalIgnoreCase))
        {
            return HasMeaningfulBusinessBody(function);
        }

        bool hasPeriodToken =
            combined.Contains("10ms", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("1ms", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("T010ms", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Period", StringComparison.OrdinalIgnoreCase);
        if (hasPeriodToken && !IsLowLevelFunction(function) && HasMeaningfulBusinessBody(function))
        {
            return true;
        }

        bool hasTaskToken =
            name.Contains("Loop", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Run", StringComparison.OrdinalIgnoreCase);
        return hasTaskToken &&
            (IsBusinessSourceFile(function) || IsBusinessFunctionName(name)) &&
            !IsLowLevelFunction(function) &&
            HasMeaningfulBusinessBody(function);
    }

    private static bool HasMeaningfulBusinessBody(FunctionNode function)
    {
        string body = RemoveCommentsAndLiterals(function.Body);
        string compact = Regex.Replace(body, @"[\s{};]", "");
        if (compact.Length == 0)
        {
            return false;
        }

        if (IsSchedulerTickFunction(function))
        {
            return false;
        }

        return CallsLcdBusinessWrite(function) ||
            TouchesCanPayload(function) ||
            ContainsRuntimeBusinessSignal(function) ||
            CountDirectCallLikeTokens(function.Body) > 0 ||
            AnalyzeFunction(function).BusinessScore >= 68;
    }

    private static bool IsSchedulerTickFunction(FunctionNode function)
    {
        string name = function.Name;
        string combined = function.FilePath.Replace('\\', '/') + "/" + name;
        if (name.Equals("Task_sys_tick", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("smt_sys_tick", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("sys_tick", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SysTick", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (combined.Contains("/Task.c/", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("tick", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("task", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return function.Body.Contains("g_tasks", StringComparison.OrdinalIgnoreCase) &&
            function.Body.Contains("ready", StringComparison.OrdinalIgnoreCase) &&
            function.Body.Contains("period", StringComparison.OrdinalIgnoreCase);
    }

    private static int PeriodicEntryScore(FunctionNode function)
    {
        string name = function.Name;
        string combined = function.FilePath.Replace('\\', '/') + "/" + name;
        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        int score = 0;
        if (name.Equals("MyLogic_10ms", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }
        if (combined.Contains("10ms", StringComparison.OrdinalIgnoreCase) || analysis.Domain == "period10")
        {
            score += 500;
        }
        if (name.Equals("MyLogic_1ms", StringComparison.OrdinalIgnoreCase) || analysis.Domain == "period1")
        {
            score += 260;
        }
        if (IsBusinessSourceFile(function))
        {
            score += 110;
        }
        if (IsBusinessFunctionName(name))
        {
            score += 80;
        }
        if (CallsLcdBusinessWrite(function) || TouchesCanPayload(function) || ContainsRuntimeBusinessSignal(function))
        {
            score += 70;
        }
        score += Math.Min(80, CountDirectCallLikeTokens(function.Body) * 4);
        return score;
    }

    private static int CountDirectCallLikeTokens(string body)
    {
        int count = 0;
        foreach (Match match in CallRegex().Matches(body))
        {
            string name = match.Groups["name"].Value;
            if (!KeywordCalls.Contains(name))
            {
                count++;
            }
        }
        return count;
    }

    private static (IReadOnlyList<ProgramCallGraphNode> nodes, IReadOnlyList<ProgramCallGraphEdge> edges) BuildCallGraph(
        List<FunctionNode> functions,
        HashSet<CallEdge> edges,
        FunctionNode preferredStart)
    {
        const int maxNodes = 420;
        const int maxDepth = 16;
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        Dictionary<string, int> incoming = edges.GroupBy(e => e.ToId).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> outgoingCount = edges.GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.Count());
        BusinessGraphHints hints = BuildBusinessGraphHints(functions);
        HashSet<string> visibleBusinessIds = functions
            .Where(f => IsBusinessGraphVisibleFunction(f, hints, incoming, outgoingCount))
            .Select(f => f.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        FunctionNode? logic10msEntry = FindPrimaryBusinessEntry(functions, preferredStart);
        if (logic10msEntry != null)
        {
            HashSet<string> entryScopeIds = BuildReachableFunctionIds(functions, edges, logic10msEntry.Id, maxDepth, rootIsId: true);
            entryScopeIds.Add(logic10msEntry.Id);
            visibleBusinessIds.RemoveWhere(id => !entryScopeIds.Contains(id));
            visibleBusinessIds.Add(logic10msEntry.Id);
            foreach (FunctionNode function in functions.Where(f => entryScopeIds.Contains(f.Id) && IsReachableBusinessChainFunction(f)))
            {
                visibleBusinessIds.Add(function.Id);
            }
        }
        else if (IsBusinessGraphVisibleFunction(preferredStart, hints, incoming, outgoingCount))
        {
            visibleBusinessIds.Add(preferredStart.Id);
        }
        IReadOnlyList<FunctionNode> directLogicChildren = BuildDirectLogicEntryChildren(functions, logic10msEntry, 80);
        foreach (FunctionNode child in directLogicChildren)
        {
            visibleBusinessIds.Add(child.Id);
        }
        FunctionNode? displayEntry = FindDisplayBusinessEntry(functions, edges);
        IReadOnlyList<FunctionNode> displayChildren = BuildDisplayBusinessChildren(functions, displayEntry, 120);
        if (displayEntry != null)
        {
            visibleBusinessIds.Add(displayEntry.Id);
        }
        foreach (FunctionNode child in displayChildren)
        {
            visibleBusinessIds.Add(child.Id);
        }
        Dictionary<string, List<string>> outgoing = BuildCollapsedBusinessOutgoingMap(functions, edges, visibleBusinessIds, hints, incoming, outgoingCount);
        Dictionary<string, List<string>> incomingMap = outgoing
            .SelectMany(pair => pair.Value.Select(target => new { FromId = pair.Key, ToId = target }))
            .GroupBy(e => e.ToId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.FromId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(byId.ContainsKey)
                    .OrderByDescending(id => BusinessFunctionScore(byId[id], hints, incoming, outgoingCount))
                    .ThenBy(id => byId[id].Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var selected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, int Level)>();
        void AddSeed(FunctionNode? function, int level)
        {
            if (function == null ||
                selected.Count >= maxNodes ||
                IsSchedulerTickFunction(function) ||
                !HasMeaningfulBusinessBody(function))
            {
                return;
            }
            int normalizedLevel = level;
            if (logic10msEntry != null)
            {
                bool isLogic10Entry = function.Id.Equals(logic10msEntry.Id, StringComparison.OrdinalIgnoreCase);
                bool isOtherPeriodicEntry = IsPeriodicEntryCandidate(function) && !isLogic10Entry;
                if (!isLogic10Entry && !isOtherPeriodicEntry && normalizedLevel <= 0)
                {
                    normalizedLevel = 1;
                }
                else if (isOtherPeriodicEntry)
                {
                    normalizedLevel = 0;
                }
            }

            normalizedLevel = Math.Clamp(normalizedLevel, 0, maxDepth);
            if (!selected.TryGetValue(function.Id, out int oldLevel) || normalizedLevel < oldLevel)
            {
                selected[function.Id] = normalizedLevel;
                queue.Enqueue((function.Id, normalizedLevel));
            }
        }

        AddSeed(logic10msEntry, 0);
        if (logic10msEntry == null)
        {
            foreach (FunctionNode periodicEntry in FindPeriodicEntryCandidates(functions).Take(3))
            {
                AddSeed(periodicEntry, 0);
            }
        }
        foreach (FunctionNode child in directLogicChildren)
        {
            AddSeed(child, 1);
        }
        AddSeed(displayEntry, 0);
        foreach (FunctionNode child in displayChildren.Take(80))
        {
            AddSeed(child, displayEntry == null ? 1 : 1);
        }

        List<FunctionNode> anchors = functions
            .Where(f => visibleBusinessIds.Contains(f.Id) && hints.AnchorFunctionIds.Contains(f.Id))
            .OrderBy(f => IsDisplayDomainFunction(f) ? 1 : 0)
            .ThenByDescending(f => BusinessFunctionScore(f, hints, incoming, outgoingCount))
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        int displayAnchorCount = 0;
        foreach (FunctionNode anchor in anchors)
        {
            bool displayAnchor = IsDisplayDomainFunction(anchor);
            if (displayAnchor && displayAnchorCount >= 4)
            {
                continue;
            }

            bool addedCaller = false;
            if (incomingMap.TryGetValue(anchor.Id, out List<string>? callers))
            {
                foreach (string callerId in callers.Where(id => visibleBusinessIds.Contains(id)).Take(2))
                {
                    AddSeed(byId[callerId], 0);
                    addedCaller = true;
                }
            }
            AddSeed(anchor, addedCaller ? 1 : 0);
            if (displayAnchor)
            {
                displayAnchorCount++;
            }
        }

        if (selected.Count == 0)
        {
            AddSeed(preferredStart, 0);
            AddSeed(FindFunction(functions, "MyLogic_10ms"), 0);
        }

        foreach (FunctionNode candidate in functions
            .Where(f => visibleBusinessIds.Contains(f.Id) && outgoingCount.ContainsKey(f.Id))
            .OrderByDescending(f => BusinessFunctionScore(f, hints, incoming, outgoingCount))
            .Take(16))
        {
            AddSeed(candidate, selected.Count == 0 ? 0 : 1);
        }

        if (selected.Count == 0)
        {
            AddSeed(functions.FirstOrDefault(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase)), 0);
            AddSeed(functions[0], 0);
        }

        while (queue.Count > 0 && selected.Count < maxNodes)
        {
            (string id, int level) = queue.Dequeue();
            if (level >= maxDepth || !outgoing.TryGetValue(id, out List<string>? next))
            {
                continue;
            }

            int perCallerLimit = level == 0 ? 64 : level == 1 ? 36 : 24;
            foreach (string targetId in next.Take(perCallerLimit))
            {
                if (selected.Count >= maxNodes)
                {
                    break;
                }
                if (!visibleBusinessIds.Contains(targetId))
                {
                    continue;
                }
                if (selected.ContainsKey(targetId))
                {
                    continue;
                }
                selected[targetId] = level + 1;
                queue.Enqueue((targetId, level + 1));
            }
        }

        if (selected.Count < Math.Min(maxNodes, 14))
        {
            foreach (FunctionNode function in functions
                .Where(f => visibleBusinessIds.Contains(f.Id))
                .OrderBy(f => IsDisplayDomainFunction(f) ? 1 : 0)
                .ThenByDescending(f => (incoming.TryGetValue(f.Id, out int inCount) ? inCount : 0) + (outgoingCount.TryGetValue(f.Id, out int outCount) ? outCount : 0))
                .Take(maxNodes))
            {
                if (selected.Count >= maxNodes)
                {
                    break;
                }
                selected.TryAdd(function.Id, Math.Min(3, maxDepth));
            }
        }

        HashSet<string> period10Ids = logic10msEntry == null
            ? BuildReachableFunctionIds(functions, edges, "MyLogic_10ms", maxDepth)
            : BuildReachableFunctionIds(functions, edges, logic10msEntry.Id, maxDepth, rootIsId: true);

        var graphNodes = selected
            .OrderBy(x => x.Value)
            .ThenBy(x => byId[x.Key].FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => byId[x.Key].Name, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                FunctionNode function = byId[x.Key];
                incoming.TryGetValue(function.Id, out int inCount);
                outgoingCount.TryGetValue(function.Id, out int outCount);
                return new ProgramCallGraphNode(
                    function.Id,
                    function.Name,
                    function.FilePath,
                    x.Value,
                    inCount,
                    outCount,
                    ClassifyFunction(function, period10Ids),
                    TraceIdForFunction(function),
                    FunctionSummary(function));
            })
            .ToList();

        HashSet<string> selectedIds = selected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graphEdges = outgoing
            .SelectMany(pair => pair.Value.Select(target => new ProgramCallGraphEdge(pair.Key, target)))
            .Where(e => selectedIds.Contains(e.FromId) && selectedIds.Contains(e.ToId))
            .OrderBy(e => selected[e.FromId])
            .ThenBy(e => selected[e.ToId])
            .Take(500)
			.ToList();

        return (graphNodes, graphEdges);
    }

    private static (IReadOnlyList<ProgramCallGraphNode> allCallGraphNodes, IReadOnlyList<ProgramCallGraphEdge> allCallGraphEdges) BuildCompleteCallGraph(
        List<FunctionNode> functions,
        HashSet<CallEdge> edges,
        FunctionNode preferredStart)
    {
        Dictionary<string, int> incoming = edges
            .GroupBy(e => e.ToId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> outgoing = edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> period10Ids = BuildReachableFunctionIds(functions, edges, preferredStart.Id, 8, rootIsId: true);

        List<ProgramCallGraphNode> nodes = functions
            .Where(f => !IsMonitorInternalFunctionName(f.Name))
            .Select(function =>
            {
                incoming.TryGetValue(function.Id, out int inCount);
                outgoing.TryGetValue(function.Id, out int outCount);
                return new ProgramCallGraphNode(
                    function.Id,
                    function.Name,
                    function.FilePath,
                    6,
                    inCount,
                    outCount,
                    ClassifyFunction(function, period10Ids),
                    TraceIdForFunction(function),
                    FunctionSummary(function));
            })
            .ToList();
        HashSet<string> nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ProgramCallGraphEdge> graphEdges = edges
            .Where(edge => nodeIds.Contains(edge.FromId) && nodeIds.Contains(edge.ToId))
            .Select(edge => new ProgramCallGraphEdge(edge.FromId, edge.ToId))
            .ToList();
        return (nodes, graphEdges);
    }

    private static IReadOnlyList<FunctionNode> BuildDirectLogicEntryChildren(List<FunctionNode> functions, FunctionNode? entry, int maxCount)
    {
        if (entry == null || maxCount <= 0)
        {
            return Array.Empty<FunctionNode>();
        }

        var result = new List<FunctionNode>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FunctionNode child in DirectCalledFunctions(functions, entry))
        {
            if (result.Count >= maxCount)
            {
                break;
            }
            if (!used.Add(child.Id) || !IsDirectLogicEntryStep(child))
            {
                continue;
            }

            result.Add(child);
        }

        return result;
    }

    private static FunctionNode? FindDisplayBusinessEntry(List<FunctionNode> functions, HashSet<CallEdge> edges)
    {
        FunctionNode? dispMain = FindFunction(functions, "Disp_main");
        if (dispMain != null && HasMeaningfulBusinessBody(dispMain))
        {
            return dispMain;
        }

        List<FunctionNode> lcdWriters = functions
            .Where(f => CallsLcdBusinessWrite(f) && !IsHiddenGraphUtility(f))
            .OrderByDescending(f => CountLcdBusinessWrites(f))
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        if (lcdWriters.Count == 0)
        {
            return null;
        }

        HashSet<string> writerIds = lcdWriters.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        FunctionNode? commonCaller = edges
            .Where(e => writerIds.Contains(e.ToId) && byId.ContainsKey(e.FromId))
            .GroupBy(e => e.FromId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Select(e => e.ToId).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenBy(g => byId[g.Key].FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => byId[g.Key])
            .FirstOrDefault(f => !IsHiddenGraphUtility(f) && !IsGraphInfrastructureFunction(f));
        return commonCaller ?? lcdWriters.FirstOrDefault();
    }

    private static IReadOnlyList<FunctionNode> BuildDisplayBusinessChildren(List<FunctionNode> functions, FunctionNode? displayEntry, int maxCount)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<FunctionNode>();
        }

        var result = new List<FunctionNode>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(FunctionNode? function)
        {
            if (function == null ||
                result.Count >= maxCount ||
                !used.Add(function.Id) ||
                IsHiddenGraphUtility(function))
            {
                return;
            }
            if (function.Name.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (CallsLcdBusinessWrite(function) || IsDisplayDomainFunction(function) || IsBusinessSourceFile(function))
            {
                result.Add(function);
            }
        }

        if (displayEntry != null)
        {
            foreach (FunctionNode child in DirectCalledFunctions(functions, displayEntry))
            {
                Add(child);
                foreach (FunctionNode grandChild in DirectCalledFunctions(functions, child))
                {
                    Add(grandChild);
                }
            }
        }

        foreach (FunctionNode writer in functions
            .Where(f => CallsLcdBusinessWrite(f))
            .OrderByDescending(CountLcdBusinessWrites)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Add(writer);
            if (result.Count >= maxCount)
            {
                break;
            }
        }

        return result;
    }

    private static int CountLcdBusinessWrites(FunctionNode function)
    {
        return Regex.Matches(function.Body, @"\bLCD_WR_Data2B\s*\(", RegexOptions.IgnoreCase).Count;
    }

    private static bool IsDirectLogicEntryStep(FunctionNode function)
    {
        if (!HasMeaningfulBusinessBody(function))
        {
            return false;
        }

        if (IsGraphInfrastructureFunction(function) || IsHiddenGraphUtility(function))
        {
            return false;
        }

        if (IsBusinessSourceFile(function) ||
            IsBusinessCanDataFunction(function) ||
            CallsLcdBusinessWrite(function) ||
            ContainsRuntimeBusinessSignal(function))
        {
            return true;
        }

        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        return analysis.Domain is "safety" or "analog" or "io" or "disp" or "can-rx" or "can-tx" or "business" or "period10";
    }

    private static Dictionary<string, List<string>> BuildCollapsedBusinessOutgoingMap(
        List<FunctionNode> functions,
        HashSet<CallEdge> edges,
        HashSet<string> visibleBusinessIds,
        BusinessGraphHints hints,
        Dictionary<string, int> incoming,
        Dictionary<string, int> outgoingCount)
    {
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        Dictionary<string, List<string>> rawOutgoing = edges
            .GroupBy(e => e.FromId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ToId).Distinct(StringComparer.OrdinalIgnoreCase).Where(byId.ContainsKey).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceId in visibleBusinessIds.Where(byId.ContainsKey))
        {
            var targets = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceId };
            var queue = new Queue<(string Id, int Depth)>();
            if (rawOutgoing.TryGetValue(sourceId, out List<string>? direct))
            {
                foreach (string target in direct)
                {
                    queue.Enqueue((target, 1));
                }
            }

            while (queue.Count > 0 && targets.Count < 64)
            {
                (string targetId, int depth) = queue.Dequeue();
                if (!used.Add(targetId) || !byId.TryGetValue(targetId, out FunctionNode? target))
                {
                    continue;
                }

                if (visibleBusinessIds.Contains(targetId))
                {
                    targets.Add(targetId);
                    continue;
                }

                if (depth >= 6 || IsHardGraphStopFunction(target))
                {
                    continue;
                }

                if (!rawOutgoing.TryGetValue(targetId, out List<string>? next))
                {
                    continue;
                }

                foreach (string nextId in next
                    .OrderByDescending(id => BusinessFunctionScore(byId[id], hints, incoming, outgoingCount))
                    .ThenBy(id => byId[id].Name, StringComparer.OrdinalIgnoreCase)
                    .Take(48))
                {
                    queue.Enqueue((nextId, depth + 1));
                }
            }

            if (targets.Count > 0)
            {
                result[sourceId] = targets
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(id => BusinessFunctionScore(byId[id], hints, incoming, outgoingCount))
                    .ThenBy(id => byId[id].Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return result;
    }

    private static bool IsBusinessGraphVisibleFunction(
        FunctionNode function,
        BusinessGraphHints hints,
        Dictionary<string, int> incoming,
        Dictionary<string, int> outgoing)
    {
        if (IsMandatoryBusinessEntry(function))
        {
            return true;
        }

        if (function.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!HasMeaningfulBusinessBody(function))
        {
            return false;
        }

        if (IsSchedulerTickFunction(function))
        {
            return false;
        }

        if (IsGraphInfrastructureFunction(function))
        {
            return false;
        }

        if (IsHiddenGraphUtility(function))
        {
            return false;
        }

        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        if (analysis.Domain == "driver" && !IsBusinessSourceFile(function))
        {
            return false;
        }

        if (IsLowLevelFunction(function) &&
            !IsBusinessSourceFile(function) &&
            !IsBusinessCanDataFunction(function) &&
            !CallsLcdBusinessWrite(function))
        {
            return false;
        }

        if (CallsLcdBusinessWrite(function) || IsBusinessCanDataFunction(function))
        {
            return true;
        }

        if (analysis.Domain is "safety" or "analog" or "io" or "disp" or "can-rx" or "can-tx" or "period10" or "period1")
        {
            return true;
        }

        if (IsBusinessSourceFile(function) && IsBusinessFunctionName(function.Name))
        {
            return true;
        }

        int score = BusinessFunctionScore(function, hints, incoming, outgoing);
        if (hints.AnchorFunctionIds.Contains(function.Id) && score >= 130)
        {
            return true;
        }

        return analysis.BusinessScore >= 82 && !IsLowLevelFunction(function);
    }

    private static bool IsReachableBusinessChainFunction(FunctionNode function)
    {
        if (!HasMeaningfulBusinessBody(function) ||
            IsSchedulerTickFunction(function) ||
            IsGraphInfrastructureFunction(function) ||
            IsHiddenGraphUtility(function) ||
            IsMonitorInternalFunctionName(function.Name))
        {
            return false;
        }

        if (CallsLcdBusinessWrite(function) ||
            IsBusinessCanDataFunction(function) ||
            ContainsRuntimeBusinessSignal(function) ||
            IsBusinessSourceFile(function))
        {
            return true;
        }

        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        if (analysis.Domain is "driver" or "timer" or "storage")
        {
            return false;
        }

        if (IsLowLevelFunction(function))
        {
            return false;
        }

        return analysis.BusinessScore >= 45 || IsBusinessFunctionName(function.Name);
    }

    private static bool IsMandatoryBusinessEntry(FunctionNode function)
    {
        return IsPeriodicEntryCandidate(function) ||
            function.Name.Equals("Disp_main", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHiddenGraphUtility(FunctionNode function)
    {
        string name = function.Name;
        string combined = function.FilePath.Replace('\\', '/') + "/" + name;
        if (name.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase) ||
            IsSchedulerTickFunction(function) ||
            IsMonitorInternalFunctionName(name))
        {
            return true;
        }

        string[] hidden =
        {
            "Init", "Cfg", "Config", "Register", "RcvID", "ID_Cfg", "Clr_Data",
            "Get_Data", "SetID", "Delay", "Memcpy", "Memset"
        };
        if (hidden.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
            !CallsLcdBusinessWrite(function) &&
            !IsBusinessCanDataFunction(function))
        {
            return true;
        }

        return false;
    }

    private static bool IsMonitorInternalFunctionName(string name)
    {
        return name.StartsWith("CanMonitor_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CANMonitor_", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CanMonitor_BusinessGate", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CanMonitor_Process", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CanMonitor_Trace", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraphInfrastructureFunction(FunctionNode function)
    {
        string name = function.Name;
        if (name.Equals("main", StringComparison.OrdinalIgnoreCase) || IsMandatoryBusinessEntry(function))
        {
            return false;
        }

        if (IsSchedulerTickFunction(function))
        {
            return true;
        }

        string path = function.FilePath.Replace('\\', '/');
        string fileName = Path.GetFileName(path);
        string combined = path + "/" + name;
        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);

        if (combined.Contains("CanMonitor", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Contains("Can_ask", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CanAsk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Can_Prog", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CAN_Prog", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((fileName.Equals("can.c", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("can1.c", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("can2.c", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("canopen.c", StringComparison.OrdinalIgnoreCase)) &&
            !IsBusinessSourceFile(function))
        {
            return true;
        }

        if (name.StartsWith("Sys_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("System_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((combined.Contains("DGUS", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LCD_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Lcd_", StringComparison.OrdinalIgnoreCase)) &&
            !CallsLcdBusinessWrite(function) &&
            !TouchesCanPayload(function) &&
            !ContainsRuntimeBusinessSignal(function))
        {
            return true;
        }

        if (analysis.Domain == "storage" &&
            !CallsLcdBusinessWrite(function) &&
            !TouchesCanPayload(function) &&
            !ContainsRuntimeBusinessSignal(function))
        {
            return true;
        }

        string[] storageTokens =
        {
            "AT24", "EEPROM", "FLASH", "Flash", "Read_BD", "Read_Info", "Write_Page",
            "Read_Page", "Param_Read", "Param_Write"
        };
        if (storageTokens.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
            !CallsLcdBusinessWrite(function) &&
            !TouchesCanPayload(function))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsRuntimeBusinessSignal(FunctionNode function)
    {
        string text = function.Name + " " + function.Body;
        string[] tokens =
        {
            "_DI", "_DO", "DI_", "DO_", "AI_Pin", "AO_Pin", "Mpa", "Press", "Motor",
            "Pump", "Valve", "Safe", "Alarm", "Fault", "Stop", "Remote", "Work",
            "Logic", "Sensor_Logic_V"
        };
        return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHardGraphStopFunction(FunctionNode function)
    {
        string combined = function.FilePath.Replace('\\', '/') + "/" + function.Name;
        return IsGraphInfrastructureFunction(function) ||
            combined.Contains("CanMonitor", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("SystemInit", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("startup", StringComparison.OrdinalIgnoreCase) ||
            (IsLowLevelFunction(function) && !IsBusinessSourceFile(function) && !IsBusinessCanDataFunction(function));
    }

    private static bool IsBusinessCanDataFunction(FunctionNode function)
    {
        string name = function.Name;
        string combined = (function.FilePath.Replace('\\', '/') + "/" + name).ToLowerInvariant();
        if (!combined.Contains("can", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] hideTokens = { "init", "cfg", "config", "register", "rcvid", "id_cfg", "clr_data", "get_data" };
        if (hideTokens.Any(token => combined.Contains(token)))
        {
            return false;
        }

        bool dataLike = name.Contains("data", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("send", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("receive", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("recv", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rcv", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rx", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tx", StringComparison.OrdinalIgnoreCase) ||
            function.Body.Contains("RBuf", StringComparison.OrdinalIgnoreCase) ||
            function.Body.Contains("SBuf", StringComparison.OrdinalIgnoreCase);
        return dataLike && (IsBusinessSourceFile(function) || TouchesCanPayload(function));
    }

    private static HashSet<string> BuildReachableFunctionIds(
        List<FunctionNode> functions,
        HashSet<CallEdge> edges,
        string rootName,
        int maxDepth,
        bool rootIsId = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FunctionNode? root = rootIsId
            ? functions.FirstOrDefault(f => f.Id.Equals(rootName, StringComparison.OrdinalIgnoreCase))
            : FindFunction(functions, rootName);
        if (root == null)
        {
            return result;
        }

        Dictionary<string, List<string>> outgoing = edges
            .GroupBy(e => e.FromId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ToId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, int Depth)>();
        result.Add(root.Id);
        queue.Enqueue((root.Id, 0));
        while (queue.Count > 0)
        {
            (string id, int depth) = queue.Dequeue();
            if (depth >= maxDepth || !outgoing.TryGetValue(id, out List<string>? next))
            {
                continue;
            }

            foreach (string childId in next)
            {
                if (result.Add(childId))
                {
                    queue.Enqueue((childId, depth + 1));
                }
            }
        }

        return result;
    }

    private static EmbeddedCodeAnalysis AnalyzeFunction(FunctionNode function)
    {
        return EmbeddedCodeKnowledge.Analyze(function.Name, function.FilePath, function.Body);
    }

    private static string ClassifyFunction(FunctionNode function, HashSet<string> period10Ids)
    {
        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        string combined = path + "/" + name;
        if (analysis.Domain == "main" || IsMainDomainFunction(function))
        {
            return "main";
        }
        if (analysis.Domain == "disp" || IsDisplayDomainFunction(function))
        {
            return "disp";
        }
        if (analysis.Domain == "period10" || IsPeriod10DomainFunction(function) || period10Ids.Contains(function.Id))
        {
            return "period10";
        }
        if (analysis.Domain == "period1")
        {
            return "timer";
        }
        if (analysis.Domain is "can-rx" or "can-tx" or "can")
        {
            return "can";
        }
        if (analysis.Domain == "io")
        {
            return "io";
        }
        if (analysis.Domain == "storage")
        {
            return "storage";
        }
        if (analysis.Domain == "driver" && !IsBusinessSourceFile(function))
        {
            return "driver";
        }
        if (analysis.Domain is "analog" or "safety" or "business")
        {
            return "business";
        }
        if (name.Contains("CAN", StringComparison.OrdinalIgnoreCase))
        {
            return "can";
        }
        if (CallsLcdBusinessWrite(function) || IsBusinessSourceFile(function))
        {
            return "business";
        }
        if (name.Contains("DI", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DO", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("GPIO", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("IO", StringComparison.OrdinalIgnoreCase))
        {
            return "io";
        }
        if (name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Flash", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("EEPROM", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AT24", StringComparison.OrdinalIgnoreCase))
        {
            return "storage";
        }
        if (name.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Timer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("10ms", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("1ms", StringComparison.OrdinalIgnoreCase))
        {
            return "timer";
        }
        if (IsLowLevelFunction(function))
        {
            return "driver";
        }
        if (IsBusinessFunction(function))
        {
            return "business";
        }
        return "normal";
    }

    private static bool IsMainDomainFunction(FunctionNode function)
    {
        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        return name.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Main", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MainLoop", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Loop", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/main", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisplayDomainFunction(FunctionNode function)
    {
        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        string combined = path + "/" + name;
        return CallsLcdBusinessWrite(function) ||
            combined.Contains("disp", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("display", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("lcd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPeriod10DomainFunction(FunctionNode function)
    {
        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        string combined = path + "/" + name;
        return combined.Contains("10ms", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("T010ms", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("MyLogic_10ms", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBusinessFunction(FunctionNode function)
    {
        if (IsSchedulerTickFunction(function))
        {
            return false;
        }

        if (IsGraphInfrastructureFunction(function))
        {
            return false;
        }

        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        if (analysis.IsBusinessAnchor || analysis.BusinessScore >= 68)
        {
            return true;
        }

        if (IsLowLevelFunction(function) && !IsBusinessSourceFile(function) && !CallsLcdBusinessWrite(function) && !TouchesCanPayload(function))
        {
            return false;
        }

        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        string combined = path + "/" + name;
        string[] positive =
        {
            "App", "Usr", "User", "Work", "Logic", "Control", "Ctrl",
            "Process", "State", "Mode", "YK", "遥控", "业务", "故障", "保护", "输出", "输入"
        };

        return IsBusinessSourceFile(function) ||
            CallsLcdBusinessWrite(function) ||
            TouchesCanPayload(function) ||
            positive.Any(x => combined.Contains(x, StringComparison.OrdinalIgnoreCase)) ||
            name.Equals("main", StringComparison.OrdinalIgnoreCase);
    }

    private static BusinessGraphHints BuildBusinessGraphHints(List<FunctionNode> functions)
    {
        var anchorFunctionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchorIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FunctionNode function in functions)
        {
            if (IsGraphInfrastructureFunction(function))
            {
                continue;
            }

            EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
            bool anchor = false;
            if (analysis.IsBusinessAnchor)
            {
                anchor = true;
                foreach (string signal in analysis.Signals)
                {
                    anchorIdentifiers.Add(signal);
                }
            }

            if (CallsLcdBusinessWrite(function))
            {
                anchor = true;
                foreach (string identifier in ExtractLcdWriteIdentifiers(function.Body))
                {
                    anchorIdentifiers.Add(identifier);
                }
            }

            if (TouchesCanPayload(function))
            {
                anchor = true;
                foreach (string identifier in ExtractCanPayloadIdentifiers(function.Body))
                {
                    anchorIdentifiers.Add(identifier);
                }
            }

            if (IsBusinessSourceFile(function) && (anchor || IsBusinessFunctionName(function.Name)))
            {
                anchor = true;
            }

            if (anchor)
            {
                anchorFunctionIds.Add(function.Id);
            }
        }

        foreach (FunctionNode function in functions)
        {
            if (anchorFunctionIds.Contains(function.Id))
            {
                continue;
            }
            if (IsGraphInfrastructureFunction(function))
            {
                continue;
            }
            if (IsLowLevelFunction(function) && !IsBusinessSourceFile(function))
            {
                continue;
            }
            int mentions = CountAnchorIdentifierMentions(function.Body, anchorIdentifiers, 4);
            if (mentions >= 2 || (mentions >= 1 && IsBusinessSourceFile(function)))
            {
                anchorFunctionIds.Add(function.Id);
            }
        }

        return new BusinessGraphHints(anchorFunctionIds, anchorIdentifiers);
    }

    private static int BusinessFunctionScore(
        FunctionNode function,
        BusinessGraphHints hints,
        Dictionary<string, int> incoming,
        Dictionary<string, int> outgoing)
    {
        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        int score = analysis.BusinessScore;
        if (IsSchedulerTickFunction(function))
        {
            score -= 260;
        }
        if (IsGraphInfrastructureFunction(function))
        {
            score -= 180;
        }
        if (hints.AnchorFunctionIds.Contains(function.Id))
        {
            score += 120;
        }
        if (CallsLcdBusinessWrite(function))
        {
            score += 90;
        }
        if (TouchesCanPayload(function))
        {
            score += 76;
        }
        if (IsBusinessSourceFile(function))
        {
            score += 64;
        }
        if (IsBusinessFunctionName(function.Name))
        {
            score += 28;
        }
        score += Math.Min(32, CountAnchorIdentifierMentions(function.Body, hints.AnchorIdentifiers, 6) * 8);
        score += Math.Min(16, outgoing.TryGetValue(function.Id, out int outCount) ? outCount * 2 : 0);
        score += Math.Min(10, incoming.TryGetValue(function.Id, out int inCount) ? inCount : 0);
        if (IsLowLevelFunction(function) && !IsBusinessSourceFile(function) && !TouchesCanPayload(function))
        {
            score -= 90;
        }
        return score;
    }

    private static bool IsBusinessSourceFile(FunctionNode function)
    {
        string fileName = Path.GetFileName(function.FilePath);
        return fileName.Equals("Lcd.c", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("LCD.c", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("usr.c", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("user.c", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("App_usr.c", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Lcd", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Usr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBusinessFunctionName(string name)
    {
        string[] tokens =
        {
            "Logic", "Ctrl", "Control", "Mode", "State", "Work", "Main", "Motor", "Valve",
            "Press", "Mpa", "Pump", "DFS", "DO", "DI", "Remote", "Key", "Alarm"
        };
        return tokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CallsLcdBusinessWrite(FunctionNode function)
    {
        return function.Body.Contains("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TouchesCanPayload(FunctionNode function)
    {
        string combined = function.FilePath + "/" + function.Name;
        bool canFunction = combined.Contains("CAN", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Can", StringComparison.OrdinalIgnoreCase);
        bool rxTxName = combined.Contains("Rcv", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Recv", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Receive", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Rx", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Tx", StringComparison.OrdinalIgnoreCase);
        if (canFunction && rxTxName)
        {
            return true;
        }

        return CanPayloadRegex().IsMatch(function.Body);
    }

    private static IEnumerable<string> ExtractLcdWriteIdentifiers(string body)
    {
        foreach (Match call in LcdWriteCallRegex().Matches(body))
        {
            string args = call.Groups["args"].Value;
            foreach (Match identifier in IdentifierRegex().Matches(args))
            {
                string value = identifier.Value;
                if (IsUsefulBusinessIdentifier(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<string> ExtractCanPayloadIdentifiers(string body)
    {
        foreach (Match identifier in IdentifierRegex().Matches(body))
        {
            string value = identifier.Value;
            if (!IsUsefulBusinessIdentifier(value))
            {
                continue;
            }
            if (value.Contains("CAN", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Can", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Rx", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Rcv", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Recv", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Tx", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Data", StringComparison.OrdinalIgnoreCase))
            {
                yield return value;
            }
        }
    }

    private static int CountAnchorIdentifierMentions(string body, HashSet<string> identifiers, int stopAt)
    {
        if (identifiers.Count == 0 || body.Length == 0)
        {
            return 0;
        }

        int count = 0;
        foreach (Match match in IdentifierRegex().Matches(body))
        {
            if (identifiers.Contains(match.Value))
            {
                count++;
                if (count >= stopAt)
                {
                    return count;
                }
            }
        }
        return count;
    }

    private static bool IsUsefulBusinessIdentifier(string identifier)
    {
        if (identifier.Length < 2 || KeywordCalls.Contains(identifier))
        {
            return false;
        }
        if (identifier.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (identifier.All(c => c == '_' || char.IsDigit(c)))
        {
            return false;
        }
        return true;
    }

    private static bool IsLowLevelFunction(FunctionNode function)
    {
        string name = function.Name;
        string path = function.FilePath.Replace('\\', '/');
        string combined = path + "/" + name;
        string[] lowLevel =
        {
            "CanMonitor", "CAN_Send", "CAN1_Get_Data", "CAN2_Get_Data", "RegisterID",
            "Init", "IRQ", "ISR", "Handler", "SysTick", "DMA", "GPIO", "UART", "USART",
            "SPI", "I2C", "ADC", "PWM", "EEPROM", "FLASH", "WDT", "WatchDog",
            "Driver", "drv", "bsp", "hal", "lib", "startup", "system_", "SystemInit",
            "memcpy", "memset", "strlen", "strcmp"
        };

        return lowLevel.Any(x => combined.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ProgramFunctionInfo> BuildFlowList(
        List<FunctionNode> functions,
        HashSet<CallEdge> edges,
        string startId,
        int maxCount)
    {
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        Dictionary<string, int> incoming = edges.GroupBy(e => e.ToId).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> outgoingCount = edges.GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, List<string>> outgoing = edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).Distinct().ToList());

        var result = new List<ProgramFunctionInfo>();
        var used = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        used.Add(startId);

        while (queue.Count > 0 && result.Count < maxCount)
        {
            string id = queue.Dequeue();
            if (byId.TryGetValue(id, out FunctionNode? function))
            {
                result.Add(ToInfo(function, incoming, outgoingCount));
            }

            if (!outgoing.TryGetValue(id, out List<string>? next))
            {
                continue;
            }

            foreach (string target in next.Take(6))
            {
                if (used.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<ProgramFrameworkStep> BuildFrameworkSteps(List<FunctionNode> functions)
    {
        FunctionNode start = PickFlowStart(functions);
        FunctionNode? logicEntry = FindPrimaryBusinessEntry(functions, start);
        if (logicEntry != null)
        {
            return BuildLogicFrameworkSteps(functions, logicEntry);
        }

        var result = new List<ProgramFrameworkStep>();
        FunctionNode? main = functions.FirstOrDefault(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
        string mainBody = main?.Body ?? "";

        if (main != null)
        {
            result.Add(new ProgramFrameworkStep("主循环", "while(1)", 0x2001, "main"));
        }

        AddStepIfPresent(result, functions, mainBody, "急停处理", "1ms / 急停分支", 0x2101, "App_JiTing");
        AddStepIfPresent(result, functions, mainBody, "主业务控制", "1ms / 正常分支", 0x2102, "app_Ctrl");
        AddStepIfPresent(result, functions, mainBody, "逻辑联锁", "1ms / app_Logic", 0x2103, "app_Logic");
        AddStepIfPresent(result, functions, mainBody, "DFS 控制", "1ms / app_ctrl_dfs", 0x2104, "app_ctrl_dfs");
        AddStepIfPresent(result, functions, mainBody, "PWM 输出", "约25ms / App_PWM", 0x2105, "App_PWM");
        AddStepIfPresent(result, functions, mainBody, "CAN 接收", "10ms / Usr_Can_Rcv", 0x2110, "Usr_Can_Rcv");
        AddStepIfPresent(result, functions, mainBody, "CAN 发送", "10ms / Usr_Can_Send", 0x2111, "Usr_Can_Send");
        AddStepIfPresent(result, functions, mainBody, "接收超时", "10ms / Can_Rcv_Dly", 0x2112, "Can_Rcv_Dly");
        AddStepIfPresent(result, functions, mainBody, "无线接收", "事件 / Uart0_WL_Rcv", 0x2120, "Uart0_WL_Rcv");
        AddStepIfPresent(result, functions, mainBody, "无线回发", "事件 / Uart0_WL_Send", 0x2121, "Uart0_WL_Send");
        AddStepIfPresent(result, functions, mainBody, "参数保存", "秒级 / Save_Info_Prog", 0x2130, "Save_Info_Prog");
        AddStepIfPresent(result, functions, mainBody, "无线恢复", "秒级 / wl_reset", 0x2131, "wl_reset");

        AddStepIfPresent(result, functions, mainBody, "10ms业务入口", "10ms / MyLogic_10ms", 0x2100, "MyLogic_10ms");

        if (result.Count == 0)
        {
            result.AddRange(functions
                .Where(IsBusinessFunction)
                .Take(12)
                .Select((f, i) => new ProgramFrameworkStep(f.Name, FunctionSummary(f), (ushort)(0x2200 + i), f.Name, FunctionSummary(f))));
        }

        return result;
    }

    private static FunctionNode? FindFunction(List<FunctionNode> functions, string name)
    {
        return functions.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ProgramFrameworkStep> BuildLogicFrameworkSteps(List<FunctionNode> functions, FunctionNode logicEntry)
    {
        var result = new List<ProgramFrameworkStep>
        {
            new ProgramFrameworkStep("周期业务入口", FunctionSummary(logicEntry), 0x2100, logicEntry.Name, FunctionSummary(logicEntry))
        };

		foreach (FunctionNode function in BusinessVisibleCalledFunctions(functions, logicEntry, 23))
		{
			result.Add(new ProgramFrameworkStep(
				BusinessDisplayName(function.Name),
				FunctionSummary(function),
                TraceIdForFunction(function),
                function.Name,
				FunctionSummary(function)));
		}

		FunctionNode? displayEntry = FindFunction(functions, "Disp_main") ??
			functions
				.Where(f => CallsLcdBusinessWrite(f) && !IsHiddenGraphUtility(f))
				.OrderByDescending(CountLcdBusinessWrites)
				.ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
		if (displayEntry != null && result.All(step => !step.FunctionName.Equals(displayEntry.Name, StringComparison.OrdinalIgnoreCase)))
		{
			result.Add(new ProgramFrameworkStep(
				displayEntry.Name.Equals("Disp_main", StringComparison.OrdinalIgnoreCase) ? "200屏显示入口" : "显示输出入口",
				FunctionSummary(displayEntry),
				TraceIdForFunction(displayEntry),
				displayEntry.Name,
				FunctionSummary(displayEntry)));
		}

		return result;
	}

    private static IReadOnlyList<ProgramFunctionInfo> BuildLogicCallInfos(List<FunctionNode> functions, FunctionNode logicEntry, int maxCount)
    {
        Dictionary<string, int> outgoingCount = BuildOutgoingCount(functions);
        var result = new List<ProgramFunctionInfo>
        {
            new ProgramFunctionInfo("周期业务入口", logicEntry.FilePath, 0, BusinessVisibleCalledFunctions(functions, logicEntry, maxCount).Count, 0x2100, FunctionSummary(logicEntry))
        };

        int order = 1;
		foreach (FunctionNode function in BusinessVisibleCalledFunctions(functions, logicEntry, Math.Max(0, maxCount - 1)))
		{
			outgoingCount.TryGetValue(function.Id, out int outCount);
			result.Add(new ProgramFunctionInfo(BusinessDisplayName(function.Name), function.FilePath, order, outCount, TraceIdForFunction(function), FunctionSummary(function)));
			order++;
		}

		FunctionNode? displayEntry = FindFunction(functions, "Disp_main") ??
			functions
				.Where(f => CallsLcdBusinessWrite(f) && !IsHiddenGraphUtility(f))
				.OrderByDescending(CountLcdBusinessWrites)
				.ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
		if (displayEntry != null &&
			result.Count < maxCount &&
			result.All(item => !item.Name.Equals(displayEntry.Name, StringComparison.OrdinalIgnoreCase) &&
				!item.Summary.Equals(FunctionSummary(displayEntry), StringComparison.OrdinalIgnoreCase)))
		{
			outgoingCount.TryGetValue(displayEntry.Id, out int outCount);
			result.Add(new ProgramFunctionInfo(displayEntry.Name, displayEntry.FilePath, order, outCount, TraceIdForFunction(displayEntry), FunctionSummary(displayEntry)));
		}

		return result;
	}

    private static Dictionary<string, int> BuildOutgoingCount(List<FunctionNode> functions)
    {
        Dictionary<string, List<FunctionNode>> byName = functions
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (FunctionNode function in functions)
        {
            int count = 0;
            foreach (string callee in DirectCallNames(function.Body))
            {
                if (byName.ContainsKey(callee))
                {
                    count++;
                }
            }
            result[function.Id] = count;
        }

        return result;
    }

    private static IReadOnlyList<FunctionNode> BusinessVisibleCalledFunctions(List<FunctionNode> functions, FunctionNode caller, int maxCount)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<FunctionNode>();
        }

        var result = new List<FunctionNode>();
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { caller.Id };
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(FunctionNode Function, int Depth)>();
        foreach (FunctionNode direct in DirectCalledFunctions(functions, caller))
        {
            queue.Enqueue((direct, 1));
        }

        while (queue.Count > 0 && result.Count < maxCount)
        {
            (FunctionNode function, int depth) = queue.Dequeue();
            if (!scanned.Add(function.Id))
            {
                continue;
            }

            if (IsLogicVisibleBusinessStep(function))
            {
                if (added.Add(function.Id))
                {
                    result.Add(function);
                }
                continue;
            }

            if (depth >= 3 || IsHardGraphStopFunction(function))
            {
                continue;
            }

            foreach (FunctionNode child in DirectCalledFunctions(functions, function).Take(10))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return result;
    }

    private static bool IsLogicVisibleBusinessStep(FunctionNode function)
    {
        if (!HasMeaningfulBusinessBody(function))
        {
            return false;
        }

        if (IsMandatoryBusinessEntry(function))
        {
            return true;
        }

        if (IsSchedulerTickFunction(function))
        {
            return false;
        }

        if (IsGraphInfrastructureFunction(function))
        {
            return false;
        }

        if (IsHiddenGraphUtility(function))
        {
            return false;
        }

        EmbeddedCodeAnalysis analysis = AnalyzeFunction(function);
        if (analysis.Domain == "driver" && !IsBusinessSourceFile(function))
        {
            return false;
        }

        if (CallsLcdBusinessWrite(function) || IsBusinessCanDataFunction(function))
        {
            return true;
        }

        if (analysis.Domain is "safety" or "analog" or "io" or "disp" or "can-rx" or "can-tx" or "business")
        {
            return true;
        }

        return IsBusinessSourceFile(function) && IsBusinessFunctionName(function.Name) && !IsLowLevelFunction(function);
    }

    private static IReadOnlyList<FunctionNode> DirectCalledFunctions(List<FunctionNode> functions, FunctionNode caller)
    {
        Dictionary<string, List<FunctionNode>> byName = functions
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<FunctionNode>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string calleeName in DirectCallNames(caller.Body))
        {
            if (!byName.TryGetValue(calleeName, out List<FunctionNode>? targets))
            {
                continue;
            }

            FunctionNode target = ChooseBestTarget(caller, targets);
            if (ReferenceEquals(caller, target) || !used.Add(target.Id))
            {
                continue;
            }

            result.Add(target);
        }

        return result;
    }

    private static IEnumerable<string> DirectCallNames(string body)
    {
        foreach (Match match in CallRegex().Matches(body))
        {
            string calleeName = match.Groups["name"].Value;
            if (KeywordCalls.Contains(calleeName) ||
                calleeName.Equals("CanMonitor_Trace", StringComparison.OrdinalIgnoreCase) ||
                calleeName.Equals("abs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return calleeName;
        }
    }

    private static ushort TraceIdForFunction(FunctionNode function)
    {
        if (function.Name.Equals("MyLogic_10ms", StringComparison.OrdinalIgnoreCase))
        {
            return 0x2100;
        }

        if (function.Name.Equals("MyLogic_1ms", StringComparison.OrdinalIgnoreCase))
        {
            return 0x2106;
        }

        return MakeTraceId(function);
    }

    private static string BusinessDisplayName(string functionName)
    {
        if (functionName.Contains("Binding", StringComparison.OrdinalIgnoreCase))
        {
            return "引脚绑定";
        }
        if (functionName.Contains("Time", StringComparison.OrdinalIgnoreCase) || functionName.Contains("Calendar", StringComparison.OrdinalIgnoreCase))
        {
            return "时间处理";
        }
        if (functionName.Contains("CAN", StringComparison.OrdinalIgnoreCase) && functionName.Contains("Send", StringComparison.OrdinalIgnoreCase))
        {
            return "CAN发送";
        }
        if (functionName.Contains("CAN", StringComparison.OrdinalIgnoreCase) && functionName.Contains("receive", StringComparison.OrdinalIgnoreCase))
        {
            return "CAN接收";
        }
        if (functionName.Contains("Remote", StringComparison.OrdinalIgnoreCase))
        {
            return "遥控数据";
        }
        if (functionName.Contains("DI", StringComparison.OrdinalIgnoreCase) || functionName.Contains("DO", StringComparison.OrdinalIgnoreCase))
        {
            return "输入输出";
        }
        if (functionName.Contains("Binding", StringComparison.OrdinalIgnoreCase))
        {
            return "引脚绑定";
        }
        if (functionName.Contains("work", StringComparison.OrdinalIgnoreCase))
        {
            return "作业逻辑";
        }
        if (functionName.Contains("Main", StringComparison.OrdinalIgnoreCase))
        {
            return "主控逻辑";
        }
        if (functionName.Contains("Press", StringComparison.OrdinalIgnoreCase) || functionName.Contains("Mpa", StringComparison.OrdinalIgnoreCase))
        {
            return "压力处理";
        }
        if (functionName.Contains("walking", StringComparison.OrdinalIgnoreCase))
        {
            return "行走系统";
        }
        if (functionName.Contains("Time", StringComparison.OrdinalIgnoreCase) || functionName.Contains("Calendar", StringComparison.OrdinalIgnoreCase))
        {
            return "时间处理";
        }
        if (functionName.Contains("Lock", StringComparison.OrdinalIgnoreCase))
        {
            return "锁机逻辑";
        }
        if (functionName.Contains("Message", StringComparison.OrdinalIgnoreCase))
        {
            return "状态转换";
        }

        return functionName;
    }

    private static void AddStepIfPresent(
        List<ProgramFrameworkStep> result,
        List<FunctionNode> functions,
        string mainBody,
        string name,
        string detail,
        ushort traceId,
        string functionName)
    {
        if (!functions.Any(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (mainBody.Length > 0 && !ContainsCall(mainBody, functionName))
        {
            return;
        }

        FunctionNode? function = functions.FirstOrDefault(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));
        if (function != null && !HasMeaningfulBusinessBody(function))
        {
            return;
        }
        result.Add(new ProgramFrameworkStep(name, function == null ? detail : FunctionSummary(function), traceId, functionName, function == null ? detail : FunctionSummary(function)));
    }

    private static bool ContainsCall(string body, string functionName)
    {
        return Regex.IsMatch(body, $@"\b{Regex.Escape(functionName)}\s*\(", RegexOptions.IgnoreCase);
    }

    private static ProgramFunctionInfo ToInfo(
        FunctionNode function,
        Dictionary<string, int> incoming,
        Dictionary<string, int> outgoing)
    {
        incoming.TryGetValue(function.Id, out int inCount);
        outgoing.TryGetValue(function.Id, out int outCount);
        return new ProgramFunctionInfo(function.Name, function.FilePath, inCount, outCount, MakeTraceId(function), FunctionSummary(function));
    }

    private static string FunctionSummary(FunctionNode function)
    {
        return string.IsNullOrWhiteSpace(function.Summary) ? BusinessDisplayName(function.Name) : function.Summary;
    }

    private static ushort MakeTraceId(FunctionNode function)
    {
        int hash = 17;
        string text = function.FilePath + "/" + function.Name;
        foreach (char c in text)
        {
            hash = unchecked(hash * 31 + char.ToUpperInvariant(c));
        }

        return (ushort)(0x3000 + (Math.Abs(hash) % 0x4000));
    }

    private static string BuildFunctionGraph(List<FunctionNode> functions, HashSet<CallEdge> edges)
    {
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        HashSet<string> hotIds = edges
            .SelectMany(e => new[] { e.FromId, e.ToId })
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .Take(90)
            .Select(g => g.Key)
            .ToHashSet();

        if (hotIds.Count == 0)
        {
            hotIds.Add(functions[0].Id);
        }

        var lines = new List<string> { "flowchart LR" };
        foreach (string id in hotIds)
        {
            FunctionNode function = byId[id];
            lines.Add($"  {id}[\"{Mermaid(function.Name)}<br/>{Mermaid(FunctionSummary(function))}\"]");
        }

        foreach (CallEdge edge in edges.Where(e => hotIds.Contains(e.FromId) && hotIds.Contains(e.ToId)).Take(220))
        {
            lines.Add($"  {edge.FromId} --> {edge.ToId}");
        }

        return string.Join('\n', lines.Distinct());
    }

    private static string BuildTopFunctions(List<FunctionNode> functions, HashSet<CallEdge> edges)
    {
        Dictionary<string, FunctionNode> byId = functions.ToDictionary(f => f.Id);
        var rows = edges
            .SelectMany(e => new[] { e.FromId, e.ToId })
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .Take(30)
            .Select(g =>
            {
                FunctionNode function = byId[g.Key];
                int outCount = edges.Count(e => e.FromId == g.Key);
                int inCount = edges.Count(e => e.ToId == g.Key);
                return $"<tr><td><code>{Html(function.Name)}</code></td><td>{Html(FunctionSummary(function))}</td><td>{inCount}</td><td>{outCount}</td></tr>";
            });

        return "<table><thead><tr><th>函数</th><th>说明</th><th>被调用</th><th>调用其他</th></tr></thead><tbody>" +
            string.Join("", rows) +
            "</tbody></table>";
    }

    private static string StableToken(string text)
    {
        int hash = 17;
        foreach (char c in text)
        {
            hash = unchecked(hash * 31 + c);
        }

        return Math.Abs(hash).ToString("X");
    }

    private static string Mermaid(string value)
    {
        return value.Replace("\\", "/").Replace("\"", "'").Replace("&", "and");
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    [GeneratedRegex(@"(?m)^[\t ]*(?:[A-Za-z_][A-Za-z0-9_\s\*\(\),\[\]]+?[\s\*]+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*\{")]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"\bLCD_WR_Data2B\s*\((?<args>[^;]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LcdWriteCallRegex();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\b(?:CAN|Can|can)[A-Za-z0-9_]*(?:Data|Rx|Rcv|Recv|Receive|Tx|Send)|(?:Rx|Rcv|Recv|Receive|Tx|Send)[A-Za-z0-9_]*(?:Data|CAN|Can)\b")]
    private static partial Regex CanPayloadRegex();

    private sealed record FunctionNode(string Id, string Name, string FilePath, string Body, string Summary);

    private sealed record BusinessGraphHints(HashSet<string> AnchorFunctionIds, HashSet<string> AnchorIdentifiers);

    private readonly record struct CallEdge(string FromId, string ToId);

    private sealed record RawProgramGraph(
        bool Success = false,
        List<string>? Files = null,
        List<FunctionNode>? Functions = null,
        HashSet<CallEdge>? Edges = null,
        int SourceFileCount = 0,
        string Message = "")
    {
        public List<string> Files { get; init; } = Files ?? new List<string>();
        public List<FunctionNode> Functions { get; init; } = Functions ?? new List<FunctionNode>();
        public HashSet<CallEdge> Edges { get; init; } = Edges ?? new HashSet<CallEdge>();
    }
}
