using System.Text;
using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal enum SourceSymbolKind
{
    Function,
    Parameter,
    LocalVariable,
    FileStaticVariable,
    ExternVariable,
    GlobalVariable,
    Typedef,
    StructType,
    Macro
}

internal enum SourceDiagnosticKind
{
    Info,
    Warning,
    Error
}

internal sealed record SourceLocation(string FilePath, int Line, int Column);

internal sealed record SourceDiagnostic(SourceDiagnosticKind Kind, string Message, string FilePath = "", int Line = 0, int Column = 0);

internal sealed record SourceSymbol(
    string Name,
    string TypeName,
    SourceSymbolKind Kind,
    string Storage,
    string ScopeName,
    string FilePath,
    int StartLine,
    int EndLine,
    SourceLocation? Declaration,
    SourceLocation? Definition,
    uint? Address,
    int Size)
{
    public bool HasAddress => Address.HasValue;
}

internal sealed class SourceRenameResult
{
    public bool Success { get; init; }
    public int ReplacementCount { get; init; }
    public List<string> ChangedFiles { get; } = new();
    public List<SourceDiagnostic> Diagnostics { get; } = new();
}

internal sealed class SourceSymbolIndex
{
    private static readonly Regex FunctionRegex = new(
        @"(?m)^[\t ]*(?<prefix>(?:(?:static|extern|inline|__inline|__irq|__task|const|volatile|unsigned|signed|long|short|struct\s+[A-Za-z_][A-Za-z0-9_]*|enum\s+[A-Za-z_][A-Za-z0-9_]*|union\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*|\*|\s)+?))\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>[^;{}]*)\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex GlobalDeclRegex = new(
        @"(?m)^[\t ]*(?<storage>extern|static)?[\t ]*(?<type>(?:(?:const|volatile|unsigned|signed|long|short|struct\s+[A-Za-z_][A-Za-z0-9_]*|enum\s+[A-Za-z_][A-Za-z0-9_]*|union\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*)[\t \*]+)+)(?<names>[A-Za-z_][A-Za-z0-9_]*(?:\s*\[[^\]]*\])?(?:\s*=\s*[^;\n]+)?(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*(?:\s*\[[^\]]*\])?(?:\s*=\s*[^;\n]+)?)*)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex LocalDeclRegex = new(
        @"^[\t ]*(?<storage>static)?[\t ]*(?<type>(?:(?:const|volatile|unsigned|signed|long|short|struct\s+[A-Za-z_][A-Za-z0-9_]*|enum\s+[A-Za-z_][A-Za-z0-9_]*|union\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*)[\t \*]+)+)(?<names>[^;()]+);",
        RegexOptions.Compiled);

    private readonly List<SourceSymbol> _symbols = new();
    private readonly List<FunctionRange> _functions = new();
    private readonly List<string> _sourceFiles = new();

    public static SourceSymbolIndex Empty { get; } = new SourceSymbolIndex("", Array.Empty<string>(), Array.Empty<SourceSymbol>(), Array.Empty<FunctionRange>());

    private SourceSymbolIndex(string root, IEnumerable<string> sourceFiles, IEnumerable<SourceSymbol> symbols, IEnumerable<FunctionRange> functions)
    {
        Root = root;
        _sourceFiles.AddRange(sourceFiles);
        _symbols.AddRange(symbols);
        _functions.AddRange(functions);
    }

    private SourceSymbolIndex()
    {
        Root = "";
    }

    public string Root { get; }
    public IReadOnlyList<SourceSymbol> Symbols => _symbols;
    public IReadOnlyList<string> SourceFiles => _sourceFiles;

    public static SourceSymbolIndex Build(string root, IEnumerable<MapSymbol> mapSymbols)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Empty;
        }

        var map = mapSymbols
            .GroupBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var files = EnumerateEditableSourceFiles(root).ToArray();
        var symbols = new List<SourceSymbol>();
        var functions = new List<FunctionRange>();

        foreach (string file in files)
        {
            string text;
            try
            {
                text = SourceEditService.LoadFile(file).Text;
            }
            catch
            {
                continue;
            }

            string masked = MaskCommentsAndStrings(text);
            string[] lines = ToLines(text);
            foreach (FunctionRange function in FindFunctions(file, text, masked))
            {
                functions.Add(function);
                symbols.Add(new SourceSymbol(
                    function.Name,
                    function.ReturnType,
                    SourceSymbolKind.Function,
                    function.Storage,
                    "",
                    file,
                    function.StartLine,
                    function.EndLine,
                    new SourceLocation(file, function.StartLine, 1),
                    new SourceLocation(file, function.StartLine, 1),
                    null,
                    0));

                foreach (SourceSymbol parameter in ExtractParameters(file, function))
                {
                    symbols.Add(parameter);
                }

                foreach (SourceSymbol local in ExtractLocalVariables(file, function, lines, map))
                {
                    symbols.Add(local);
                }
            }

            string globalMasked = BlankFunctionBodies(masked, functions.Where(f => f.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)));
            foreach (SourceSymbol global in ExtractGlobalVariables(file, globalMasked, map))
            {
                symbols.Add(global);
            }
            foreach (SourceSymbol macro in ExtractMacros(file, globalMasked))
            {
                symbols.Add(macro);
            }
        }

        foreach (MapSymbol mapSymbol in map.Values)
        {
            if (symbols.Any(symbol => symbol.Name.Equals(mapSymbol.Name, StringComparison.OrdinalIgnoreCase) &&
                (symbol.Kind == SourceSymbolKind.GlobalVariable ||
                 symbol.Kind == SourceSymbolKind.ExternVariable ||
                 symbol.Kind == SourceSymbolKind.FileStaticVariable)))
            {
                continue;
            }

            symbols.Add(new SourceSymbol(
                mapSymbol.Name,
                string.IsNullOrWhiteSpace(mapSymbol.TypeName) ? "map/axf" : mapSymbol.TypeName,
                SourceSymbolKind.GlobalVariable,
                "",
                "map/axf",
                "",
                1,
                int.MaxValue,
                null,
                null,
                mapSymbol.Address,
                mapSymbol.Size));
        }

        return new SourceSymbolIndex(root, files, symbols, functions);
    }

    public bool TryResolveIdentifier(string filePath, int lineNumber, string identifier, out SourceSymbol symbol)
    {
        symbol = default!;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        FunctionRange? function = _functions
            .Where(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
                lineNumber >= f.StartLine &&
                lineNumber <= f.EndLine)
            .OrderByDescending(f => f.StartLine)
            .FirstOrDefault();
        if (function != null)
        {
            SourceSymbol? local = _symbols
                .Where(s => s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
                    s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase) &&
                    (s.Kind == SourceSymbolKind.LocalVariable || s.Kind == SourceSymbolKind.Parameter) &&
                    lineNumber >= s.StartLine &&
                    lineNumber <= s.EndLine &&
                    s.ScopeName.Equals(function.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Kind == SourceSymbolKind.LocalVariable)
                .ThenByDescending(s => s.StartLine)
                .FirstOrDefault();
            if (local != null)
            {
                symbol = local;
                return true;
            }
        }

        SourceSymbol? fileStatic = _symbols.FirstOrDefault(s =>
            s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
            s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase) &&
            s.Kind == SourceSymbolKind.FileStaticVariable);
        if (fileStatic != null)
        {
            symbol = fileStatic;
            return true;
        }

        SourceSymbol? definition = _symbols.FirstOrDefault(s =>
            s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase) &&
            s.Kind == SourceSymbolKind.GlobalVariable);
        if (definition != null)
        {
            symbol = definition;
            return true;
        }

        SourceSymbol? externDecl = _symbols.FirstOrDefault(s =>
            s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase) &&
            s.Kind == SourceSymbolKind.ExternVariable);
        if (externDecl != null)
        {
            symbol = externDecl;
            return true;
        }

        SourceSymbol? functionSymbol = _symbols.FirstOrDefault(s =>
            s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase) &&
            s.Kind == SourceSymbolKind.Function);
        if (functionSymbol != null)
        {
            symbol = functionSymbol;
            return true;
        }

        return false;
    }

    public string BuildHoverText(SourceSymbol symbol)
    {
        string scope = symbol.Kind switch
        {
            SourceSymbolKind.Parameter => "参数：" + symbol.ScopeName,
            SourceSymbolKind.LocalVariable => "局部变量：" + symbol.ScopeName,
            SourceSymbolKind.FileStaticVariable => "文件 static",
            SourceSymbolKind.ExternVariable => "extern 声明",
            SourceSymbolKind.GlobalVariable => "全局变量",
            SourceSymbolKind.Function => "函数",
            SourceSymbolKind.Macro => "宏",
            _ => symbol.Kind.ToString()
        };
        string address = symbol.HasAddress
            ? $"地址 0x{symbol.Address!.Value:X8}，大小 {symbol.Size}"
            : "未编译/无地址";
        SourceLocation? location = symbol.Definition ?? symbol.Declaration;
        string where = location == null ? "" : $"  {Path.GetFileName(location.FilePath)}:{location.Line}";
        return $"{symbol.Name}  {symbol.TypeName}\r\n{scope}  {address}{where}";
    }

    public SourceRenameResult RenameSymbolInProject(SourceSymbol symbol, string newName)
    {
        var result = new SourceRenameResult();
        if (!IsIdentifier(newName))
        {
            result.Diagnostics.Add(new SourceDiagnostic(SourceDiagnosticKind.Error, "新名称不是合法 C 标识符。"));
            return result;
        }

        IEnumerable<string> files = GetRenameFiles(symbol);
        int replacements = 0;
        foreach (string file in files)
        {
            SourceFileBuffer buffer;
            try
            {
                buffer = SourceEditService.LoadFile(file);
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add(new SourceDiagnostic(SourceDiagnosticKind.Warning, "读取失败：" + ex.Message, file));
                continue;
            }

            string newText = ReplaceIdentifier(buffer.Text, symbol.Name, newName, GetRenameLineRange(symbol, file), out int count);
            if (count <= 0)
            {
                continue;
            }

            try
            {
                SourceEditService.CreateSingleBackup(file);
                SourceEditService.WriteFile(file, newText, buffer.Encoding, buffer.NewLine);
                replacements += count;
                result.ChangedFiles.Add(file);
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add(new SourceDiagnostic(SourceDiagnosticKind.Error, "写入失败：" + ex.Message, file));
            }
        }

        result.Diagnostics.Add(new SourceDiagnostic(SourceDiagnosticKind.Info, $"替换 {replacements} 处。"));
        return new SourceRenameResult
        {
            Success = replacements > 0 && result.Diagnostics.All(d => d.Kind != SourceDiagnosticKind.Error),
            ReplacementCount = replacements
        }.MergeFrom(result);
    }

    public bool TryDeclareLocalVariable(string filePath, string text, int lineNumber, string variableName, string typeName, out string newText, out SourceDiagnostic diagnostic)
    {
        newText = text;
        diagnostic = new SourceDiagnostic(SourceDiagnosticKind.Error, "没有找到可插入声明的函数。", filePath, lineNumber);
        if (!IsIdentifier(variableName))
        {
            diagnostic = new SourceDiagnostic(SourceDiagnosticKind.Error, "变量名不是合法 C 标识符。", filePath, lineNumber);
            return false;
        }

        FunctionRange? function = _functions.FirstOrDefault(f =>
            f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
            lineNumber >= f.StartLine &&
            lineNumber <= f.EndLine);
        if (function == null)
        {
            return false;
        }

        string[] lines = ToLines(text);
        int insertLine = Math.Clamp(function.StartLine, 0, lines.Length);
        for (int i = function.StartLine; i < Math.Min(lines.Length, function.EndLine); i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                insertLine = i + 1;
                continue;
            }
            if (LocalDeclRegex.IsMatch(MaskCommentsAndStrings(lines[i])))
            {
                insertLine = i + 1;
                continue;
            }
            break;
        }

        string indent = InferIndent(lines, insertLine);
        string declaration = indent + (string.IsNullOrWhiteSpace(typeName) ? "int" : typeName.Trim()) + " " + variableName + " = 0;";
        var list = lines.ToList();
        list.Insert(insertLine, declaration);
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        newText = string.Join(nl, list);
        diagnostic = new SourceDiagnostic(SourceDiagnosticKind.Info, "已插入局部变量声明。", filePath, insertLine + 1, 1);
        return true;
    }

    public static int RunSelfTest(TextWriter output)
    {
        string root = Path.Combine(Path.GetTempPath(), "CanVariableMonitor_SourceSymbolSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = Path.Combine(root, "a.c");
            string b = Path.Combine(root, "b.c");
            File.WriteAllText(a,
                "int shared;\r\n" +
                "extern int other;\r\n" +
                "void f(int p)\r\n{\r\n" +
                "    int shared;\r\n" +
                "    shared = p;\r\n" +
                "    // shared in comment\r\n" +
                "    const char *s = \"shared in string\";\r\n" +
                "}\r\n",
                Encoding.UTF8);
            File.WriteAllText(b,
                "extern int shared;\r\n" +
                "void g(void)\r\n{\r\n" +
                "    shared = 2;\r\n" +
                "}\r\n",
                Encoding.UTF8);

            var index = Build(root, new[] { new MapSymbol { Name = "shared", Address = 0x1000, Size = 4 } });
            bool localResolved = index.TryResolveIdentifier(a, 6, "shared", out SourceSymbol local) && local.Kind == SourceSymbolKind.LocalVariable;
            string renamedLocal = ReplaceIdentifier(File.ReadAllText(a), "shared", "localShared", (local.StartLine, local.EndLine), out int localCount);
            bool localRenameOk = localCount == 2 &&
                renamedLocal.Contains("localShared = p", StringComparison.Ordinal) &&
                renamedLocal.Contains("// shared in comment", StringComparison.Ordinal) &&
                renamedLocal.Contains("\"shared in string\"", StringComparison.Ordinal);
            bool globalResolved = index.TryResolveIdentifier(b, 4, "shared", out SourceSymbol global) &&
                (global.Kind == SourceSymbolKind.GlobalVariable || global.Kind == SourceSymbolKind.ExternVariable) &&
                global.HasAddress;
            bool declareOk = index.TryDeclareLocalVariable(a, File.ReadAllText(a), 6, "newLocal", "uint16_t", out string declared, out _) &&
                declared.Contains("uint16_t newLocal = 0;", StringComparison.Ordinal);

            bool ok = localResolved && localRenameOk && globalResolved && declareOk;
            output.WriteLine(ok ? "SourceSymbolIndexSelfTest: PASS" : "SourceSymbolIndexSelfTest: FAIL");
            output.WriteLine($"  localResolved={localResolved}, localRename={localRenameOk}, globalResolved={globalResolved}, declare={declareOk}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            output.WriteLine("SourceSymbolIndexSelfTest: FAIL");
            output.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private IEnumerable<string> GetRenameFiles(SourceSymbol symbol)
    {
        return symbol.Kind switch
        {
            SourceSymbolKind.LocalVariable or SourceSymbolKind.Parameter or SourceSymbolKind.FileStaticVariable => new[] { symbol.FilePath },
            _ => _sourceFiles
        };
    }

    private static (int Start, int End)? GetRenameLineRange(SourceSymbol symbol, string file)
    {
        if (!file.Equals(symbol.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return symbol.Kind switch
        {
            SourceSymbolKind.LocalVariable or SourceSymbolKind.Parameter => (symbol.StartLine, symbol.EndLine),
            _ => null
        };
    }

    private static string ReplaceIdentifier(string text, string oldName, string newName, (int Start, int End)? lineRange, out int count)
    {
        count = 0;
        string masked = MaskCommentsAndStrings(text);
        int[] lineStarts = BuildLineStarts(text);
        var builder = new StringBuilder(text);
        foreach (Match match in Regex.Matches(masked, @"\b" + Regex.Escape(oldName) + @"\b").Cast<Match>().Reverse())
        {
            int line = GetLineNumber(lineStarts, match.Index);
            if (lineRange.HasValue && (line < lineRange.Value.Start || line > lineRange.Value.End))
            {
                continue;
            }
            builder.Remove(match.Index, match.Length);
            builder.Insert(match.Index, newName);
            count++;
        }
        return builder.ToString();
    }

    private static IEnumerable<SourceSymbol> ExtractParameters(string file, FunctionRange function)
    {
        if (string.IsNullOrWhiteSpace(function.ParameterText) || function.ParameterText.Trim().Equals("void", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (string raw in SplitTopLevel(function.ParameterText, ','))
        {
            string value = raw.Trim();
            Match match = Regex.Match(value, @"(?<type>.+?)(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?$");
            if (!match.Success)
            {
                continue;
            }
            string name = match.Groups["name"].Value;
            string type = value.Substring(0, value.LastIndexOf(name, StringComparison.Ordinal)).Trim();
            yield return new SourceSymbol(
                name,
                NormalizeType(type),
                SourceSymbolKind.Parameter,
                "",
                function.Name,
                file,
                function.StartLine,
                function.EndLine,
                new SourceLocation(file, function.StartLine, 1),
                new SourceLocation(file, function.StartLine, 1),
                null,
                0);
        }
    }

    private static IEnumerable<SourceSymbol> ExtractLocalVariables(string file, FunctionRange function, string[] lines, IReadOnlyDictionary<string, MapSymbol> map)
    {
        int start = Math.Max(0, function.StartLine - 1);
        int end = Math.Min(lines.Length - 1, function.EndLine - 1);
        for (int i = start; i <= end; i++)
        {
            string line = MaskCommentsAndStrings(lines[i]);
            Match match = LocalDeclRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string type = NormalizeType(match.Groups["type"].Value);
            foreach (string name in ExtractDeclaredNames(match.Groups["names"].Value))
            {
                yield return new SourceSymbol(
                    name,
                    type,
                    SourceSymbolKind.LocalVariable,
                    match.Groups["storage"].Value,
                    function.Name,
                    file,
                    i + 1,
                    function.EndLine,
                    new SourceLocation(file, i + 1, Math.Max(1, lines[i].IndexOf(name, StringComparison.Ordinal) + 1)),
                    new SourceLocation(file, i + 1, Math.Max(1, lines[i].IndexOf(name, StringComparison.Ordinal) + 1)),
                    null,
                    0);
            }
        }
    }

    private static IEnumerable<SourceSymbol> ExtractGlobalVariables(string file, string maskedText, IReadOnlyDictionary<string, MapSymbol> map)
    {
        int[] lineStarts = BuildLineStarts(maskedText);
        foreach (Match match in GlobalDeclRegex.Matches(maskedText))
        {
            string storage = match.Groups["storage"].Value.Trim();
            string type = NormalizeType(match.Groups["type"].Value);
            if (type.Contains("typedef", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string name in ExtractDeclaredNames(match.Groups["names"].Value))
            {
                SourceSymbolKind kind = storage.Equals("static", StringComparison.OrdinalIgnoreCase)
                    ? SourceSymbolKind.FileStaticVariable
                    : storage.Equals("extern", StringComparison.OrdinalIgnoreCase)
                        ? SourceSymbolKind.ExternVariable
                        : SourceSymbolKind.GlobalVariable;
                map.TryGetValue(name, out MapSymbol? mapSymbol);
                int line = GetLineNumber(lineStarts, match.Index);
                var location = new SourceLocation(file, line, 1);
                yield return new SourceSymbol(
                    name,
                    type,
                    kind,
                    storage,
                    "",
                    file,
                    1,
                    int.MaxValue,
                    location,
                    kind == SourceSymbolKind.ExternVariable ? null : location,
                    mapSymbol?.Address,
                    mapSymbol?.Size ?? 0);
            }
        }
    }

    private static IEnumerable<SourceSymbol> ExtractMacros(string file, string maskedText)
    {
        int[] lineStarts = BuildLineStarts(maskedText);
        foreach (Match match in Regex.Matches(maskedText, @"(?m)^[\t ]*#\s*define\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)"))
        {
            string name = match.Groups["name"].Value;
            int line = GetLineNumber(lineStarts, match.Index);
            yield return new SourceSymbol(
                name,
                "#define",
                SourceSymbolKind.Macro,
                "",
                "",
                file,
                line,
                line,
                new SourceLocation(file, line, 1),
                new SourceLocation(file, line, 1),
                null,
                0);
        }
    }

    private static IEnumerable<FunctionRange> FindFunctions(string file, string text, string masked)
    {
        foreach (Match match in FunctionRegex.Matches(masked))
        {
            string name = match.Groups["name"].Value;
            if (IsCKeyword(name))
            {
                continue;
            }

            int openBrace = masked.IndexOf('{', match.Index + match.Length - 1);
            int closeBrace = FindMatchingBrace(masked, openBrace);
            if (openBrace < 0 || closeBrace < 0)
            {
                continue;
            }

            int[] lineStarts = BuildLineStarts(text);
            int startLine = GetLineNumber(lineStarts, match.Index);
            int endLine = GetLineNumber(lineStarts, closeBrace);
            string prefix = match.Groups["prefix"].Value.Trim();
            string storage = prefix.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(token => token.Equals("static", StringComparison.OrdinalIgnoreCase) || token.Equals("extern", StringComparison.OrdinalIgnoreCase)) ?? "";
            string returnType = NormalizeType(Regex.Replace(prefix, @"\b(static|extern|inline|__inline|__irq|__task)\b", "", RegexOptions.IgnoreCase));
            yield return new FunctionRange(file, name, returnType, storage, match.Groups["params"].Value, startLine, endLine, match.Index, openBrace, closeBrace);
        }
    }

    private static string BlankFunctionBodies(string masked, IEnumerable<FunctionRange> functions)
    {
        var chars = masked.ToCharArray();
        foreach (FunctionRange function in functions)
        {
            int start = Math.Max(0, function.StartIndex);
            int end = Math.Min(chars.Length - 1, function.EndIndex);
            for (int i = start; i <= end; i++)
            {
                if (chars[i] != '\r' && chars[i] != '\n')
                {
                    chars[i] = ' ';
                }
            }
        }
        return new string(chars);
    }

    private static IEnumerable<string> EnumerateEditableSourceFiles(string root)
    {
        var blocked = new[] { ".git", ".svn", ".vs", "bin", "obj", "debug", "release", "listings", "objects", "rte", "__pycache__" };
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file =>
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".c", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".h", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string[] parts = file.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Any(part => blocked.Contains(part, StringComparer.OrdinalIgnoreCase));
            })
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static string MaskCommentsAndStrings(string text)
    {
        var chars = text.ToCharArray();
        bool line = false;
        bool block = false;
        bool str = false;
        bool chr = false;
        bool esc = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            char n = i + 1 < chars.Length ? chars[i + 1] : '\0';
            if (line)
            {
                if (c == '\r' || c == '\n')
                {
                    line = false;
                }
                else
                {
                    chars[i] = ' ';
                }
                continue;
            }
            if (block)
            {
                if (c == '*' && n == '/')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    block = false;
                }
                else if (c != '\r' && c != '\n')
                {
                    chars[i] = ' ';
                }
                continue;
            }
            if (str || chr)
            {
                bool end = !esc && ((str && c == '"') || (chr && c == '\''));
                esc = !esc && c == '\\';
                if (!esc && c != '\\')
                {
                    esc = false;
                }
                if (c != '\r' && c != '\n')
                {
                    chars[i] = ' ';
                }
                if (end)
                {
                    str = false;
                    chr = false;
                }
                continue;
            }
            if (c == '/' && n == '/')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                line = true;
                continue;
            }
            if (c == '/' && n == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                block = true;
                continue;
            }
            if (c == '"')
            {
                chars[i] = ' ';
                str = true;
                esc = false;
                continue;
            }
            if (c == '\'')
            {
                chars[i] = ' ';
                chr = true;
                esc = false;
            }
        }
        return new string(chars);
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        if (openBrace < 0 || openBrace >= text.Length)
        {
            return -1;
        }
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

    private static IEnumerable<string> ExtractDeclaredNames(string names)
    {
        foreach (string raw in SplitTopLevel(names, ','))
        {
            string value = raw.Split('=')[0].Trim();
            Match match = Regex.Match(value, @"[*\s]*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?$");
            if (match.Success && !IsCKeyword(match.Groups["name"].Value))
            {
                yield return match.Groups["name"].Value;
            }
        }
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == separator && depth == 0)
            {
                yield return text.Substring(start, i - start);
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    private static string NormalizeType(string text)
    {
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string[] ToLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }

    private static int GetLineNumber(int[] lineStarts, int index)
    {
        int pos = Array.BinarySearch(lineStarts, index);
        if (pos >= 0)
        {
            return pos + 1;
        }
        return Math.Max(1, ~pos);
    }

    private static string InferIndent(string[] lines, int insertLine)
    {
        for (int i = insertLine; i < lines.Length; i++)
        {
            Match match = Regex.Match(lines[i], @"^\s+");
            if (match.Success)
            {
                return match.Value;
            }
            if (lines[i].Trim().Length > 0)
            {
                break;
            }
        }
        return "    ";
    }

    private static bool IsIdentifier(string value)
    {
        return Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$") && !IsCKeyword(value);
    }

    private static bool IsCKeyword(string value)
    {
        string[] keywords =
        {
            "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else",
            "enum", "extern", "float", "for", "goto", "if", "inline", "int", "long", "register",
            "restrict", "return", "short", "signed", "sizeof", "static", "struct", "switch", "typedef",
            "union", "unsigned", "void", "volatile", "while"
        };
        return keywords.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record FunctionRange(
        string FilePath,
        string Name,
        string ReturnType,
        string Storage,
        string ParameterText,
        int StartLine,
        int EndLine,
        int StartIndex,
        int BodyStartIndex,
        int EndIndex);
}

internal static class SourceRenameResultExtensions
{
    public static SourceRenameResult MergeFrom(this SourceRenameResult target, SourceRenameResult source)
    {
        target.ChangedFiles.AddRange(source.ChangedFiles);
        target.Diagnostics.AddRange(source.Diagnostics);
        return target;
    }
}
