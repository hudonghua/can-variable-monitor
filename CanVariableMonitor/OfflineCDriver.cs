using System.Text;
using System.Text.RegularExpressions;
using CLanguage;
using CLanguage.Interpreter;

namespace CanVariableMonitor;

internal sealed class OfflineCDriver
{
    private const string EntryFunctionName = "__canmon_tick";
    private const int MaxVmCycles = 20000;
    private const int MaxHelperFunctions = 96;
    private const int MaxInferredGlobals = 320;

    private string _signature = "";
    private CInterpreter? _interpreter;
    private Dictionary<string, CompiledVariable> _globals = new(StringComparer.OrdinalIgnoreCase);
    private List<Binding> _bindings = new();

    public bool TryRun(
        string functionName,
        string sourceFilePath,
        IReadOnlyList<string> sourceLines,
        IReadOnlyList<WatchItem> watchItems,
        Func<WatchItem, double> readValue,
        Action<WatchItem, double> writeValue,
        Func<string, string?>? resolveFunctionSource = null,
        string signatureSalt = "")
    {
        List<Binding> bindings = BuildBindings(watchItems);
        if (bindings.Count == 0 || sourceLines.Count == 0)
        {
            return false;
        }

        string signature = BuildSignature(functionName, sourceFilePath, sourceLines, bindings, signatureSalt);
        if (!signature.Equals(_signature, StringComparison.Ordinal))
        {
            if (!TryCompile(signature, sourceFilePath, sourceLines, bindings, resolveFunctionSource))
            {
                return false;
            }
        }

        if (_interpreter == null || _bindings.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (Binding binding in _bindings)
            {
                CompiledVariable global = _globals[binding.SymbolName];
                _interpreter.WriteMemory(global.StackOffset, ToValue(readValue(binding.Item), binding));
            }

            _interpreter.Reset(EntryFunctionName);
            _interpreter.RemainingTime = MaxVmCycles;
            _interpreter.Run();
            if (_interpreter.RemainingTime <= 0)
            {
                return false;
            }

            foreach (Binding binding in _bindings)
            {
                CompiledVariable global = _globals[binding.SymbolName];
                writeValue(binding.Item, FromValue(_interpreter.ReadMemory(global.StackOffset), binding));
            }

            return true;
        }
        catch
        {
            ResetCache();
            return false;
        }
    }

    private bool TryCompile(
        string signature,
        string sourceFilePath,
        IReadOnlyList<string> sourceLines,
        List<Binding> bindings,
        Func<string, string?>? resolveFunctionSource)
    {
        string code = BuildProgram(sourceFilePath, sourceLines, bindings, resolveFunctionSource);
        if (string.IsNullOrWhiteSpace(code))
        {
            ResetCache();
            return false;
        }

        try
        {
            var report = new Report.SavedPrinter();
            Executable executable = CLanguageService.Compile(code, MachineInfo.Windows32, report);
            if (report.Messages.Any(message => message.IsError))
            {
                ResetCache();
                return false;
            }

            CInterpreter interpreter = new(executable, 4096, 4096);
            Dictionary<string, CompiledVariable> globals = executable.Globals
                .Where(global => bindings.Any(binding => binding.SymbolName.Equals(global.Name, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(global => global.Name, StringComparer.OrdinalIgnoreCase);
            if (globals.Count == 0)
            {
                ResetCache();
                return false;
            }

            _signature = signature;
            _interpreter = interpreter;
            _globals = globals;
            _bindings = bindings.Where(binding => globals.ContainsKey(binding.SymbolName)).ToList();
            return _bindings.Count > 0;
        }
        catch
        {
            ResetCache();
            return false;
        }
    }

    private void ResetCache()
    {
        _signature = "";
        _interpreter = null;
        _globals = new Dictionary<string, CompiledVariable>(StringComparer.OrdinalIgnoreCase);
        _bindings = new List<Binding>();
    }

    private static string BuildProgram(
        string sourceFilePath,
        IReadOnlyList<string> sourceLines,
        IReadOnlyList<Binding> bindings,
        Func<string, string?>? resolveFunctionSource)
    {
        List<string> rawBodyLines = ExtractBodyLines(sourceLines).ToList();
        List<FunctionSnippet> helpers = LoadCalledFunctionSnippets(sourceFilePath, rawBodyLines, bindings, resolveFunctionSource);
        HashSet<string> callableFunctions = helpers.Select(helper => helper.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<SanitizedFunction> sanitizedHelpers = helpers
            .Select(helper => BuildSanitizedFunction(helper, bindings, callableFunctions))
            .Where(helper => !string.IsNullOrWhiteSpace(helper.SourceText))
            .ToList();

        callableFunctions = sanitizedHelpers
            .Select(helper => helper.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> bodyLines = rawBodyLines
            .Select(line => SanitizeLine(line, bindings, callableFunctions, keepSimpleStatements: true))
            .Where(line => line.Length > 0)
            .ToList();
        if (bodyLines.Count == 0)
        {
            return "";
        }

        List<InferredGlobal> inferredGlobals = BuildInferredGlobalDeclarations(bodyLines, sanitizedHelpers, bindings, callableFunctions);

        StringBuilder builder = new();
        builder.AppendLine("typedef signed char int8_t;");
        builder.AppendLine("typedef unsigned char uint8_t;");
        builder.AppendLine("typedef signed short int16_t;");
        builder.AppendLine("typedef unsigned short uint16_t;");
        builder.AppendLine("typedef signed int int32_t;");
        builder.AppendLine("typedef unsigned int uint32_t;");
        builder.AppendLine("typedef signed char s8;");
        builder.AppendLine("typedef unsigned char u8;");
        builder.AppendLine("typedef signed short s16;");
        builder.AppendLine("typedef unsigned short u16;");
        builder.AppendLine("typedef signed int s32;");
        builder.AppendLine("typedef unsigned int u32;");
        builder.AppendLine("typedef unsigned char BYTE;");
        builder.AppendLine("typedef unsigned short WORD;");
        builder.AppendLine("typedef unsigned int DWORD;");
        foreach (Binding binding in bindings)
        {
            builder.Append(binding.CType).Append(' ').Append(binding.SymbolName).AppendLine(";");
        }
        foreach (InferredGlobal global in inferredGlobals)
        {
            builder.Append(global.CType).Append(' ').Append(global.Name).AppendLine(";");
        }
        foreach (SanitizedFunction helper in sanitizedHelpers)
        {
            builder.AppendLine(helper.Prototype);
        }
        foreach (SanitizedFunction helper in sanitizedHelpers)
        {
            builder.AppendLine();
            builder.AppendLine(helper.SourceText);
        }

        builder.Append("void ").Append(EntryFunctionName).AppendLine("(void)");
        builder.AppendLine("{");
        foreach (string line in bodyLines)
        {
            builder.AppendLine(line);
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static IEnumerable<string> ExtractBodyLines(IReadOnlyList<string> lines)
    {
        bool started = false;
        int depth = 0;
        foreach (string rawLine in lines)
        {
            string line = rawLine.Replace("\r", "");
            if (!started)
            {
                int open = line.IndexOf('{');
                if (open < 0)
                {
                    continue;
                }

                started = true;
                depth = 1;
                string rest = line[(open + 1)..];
                if (!string.IsNullOrWhiteSpace(rest))
                {
                    foreach (string part in TrimOuterClose(rest, ref depth))
                    {
                        yield return part;
                    }
                }
                continue;
            }

            int before = depth;
            foreach (string part in TrimOuterClose(line, ref depth))
            {
                if (before > 0)
                {
                    yield return part;
                }
            }

            if (depth <= 0)
            {
                yield break;
            }
        }
    }

    private static List<string> TrimOuterClose(string line, ref int depth)
    {
        List<string> result = new();
        StringBuilder current = new();
        foreach (char c in line)
        {
            if (c == '{')
            {
                depth++;
                current.Append(c);
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth <= 0)
                {
                    string value = current.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                    return result;
                }
            }

            current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }

    private static string SanitizeLine(
        string rawLine,
        IReadOnlyList<Binding> bindings,
        IReadOnlySet<string> callableFunctions,
        bool keepSimpleStatements)
    {
        string line = StripLineComment(rawLine).TrimEnd();
        line = Regex.Replace(line, @"\btrue\b", "1", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\bfalse\b", "0", RegexOptions.IgnoreCase);
        line = SimplifyDecimalConstantProducts(line);
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
            Regex.IsMatch(trimmed, @"^(__asm|asm)\b", RegexOptions.IgnoreCase))
        {
            return "";
        }

        if (Regex.IsMatch(trimmed, @"^\s*return\b"))
        {
            return "return;";
        }

        if (Regex.IsMatch(trimmed, @"^\s*(if|else|while|for|switch|do)\b", RegexOptions.IgnoreCase))
        {
            return line;
        }

        Match callOnly = Regex.Match(trimmed, @"^\s*(?<else>else\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(.*\)\s*;?\s*$");
        if (callOnly.Success)
        {
            string name = callOnly.Groups["name"].Value;
            if (callableFunctions.Contains(name))
            {
                return line;
            }
            return callOnly.Groups["else"].Success ? "else ;" : ";";
        }

        Match inlineIfCall = Regex.Match(trimmed, @"^(?<head>\s*if\s*\(.*\)\s*)(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(.*\)\s*;?\s*$");
        if (inlineIfCall.Success)
        {
            if (callableFunctions.Contains(inlineIfCall.Groups["name"].Value))
            {
                return line;
            }
            return inlineIfCall.Groups["head"].Value + ";";
        }

        if (IsArrayOrMemberStatement(trimmed))
        {
            return ";";
        }

        if (!keepSimpleStatements && !LineMentionsBinding(trimmed, bindings) && IsSimpleNonControlStatement(trimmed))
        {
            return ";";
        }

        return line;
    }

    private static bool IsArrayOrMemberStatement(string trimmed)
    {
        if (!trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.Contains('[', StringComparison.Ordinal) ||
            trimmed.Contains("->", StringComparison.Ordinal) ||
            Regex.IsMatch(trimmed, @"\b[A-Za-z_][A-Za-z0-9_]*\s*\.");
    }

    private static string SimplifyDecimalConstantProducts(string line)
    {
        string number = @"(?:0x[0-9A-Fa-f]+|\d+(?:\.\d+)?)";
        return Regex.Replace(
            line,
            @"(?<left>" + number + @")\s*\*\s*(?<right>" + number + @")",
            match =>
            {
                string leftText = match.Groups["left"].Value;
                string rightText = match.Groups["right"].Value;
                if (!leftText.Contains('.', StringComparison.Ordinal) && !rightText.Contains('.', StringComparison.Ordinal))
                {
                    return match.Value;
                }

                if (!TryParseNumericConstant(leftText, out double left) ||
                    !TryParseNumericConstant(rightText, out double right))
                {
                    return match.Value;
                }

                return Math.Round(left * right, MidpointRounding.AwayFromZero)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
    }

    private static bool TryParseNumericConstant(string text, out double value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint hex))
            {
                value = hex;
                return true;
            }

            value = 0;
            return false;
        }

        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool IsSimpleNonControlStatement(string trimmed)
    {
        if (!trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        return !Regex.IsMatch(trimmed, @"^\s*(if|else|for|while|switch|case|default|do)\b");
    }

    private static bool LineMentionsBinding(string line, IReadOnlyList<Binding> bindings)
    {
        return bindings.Any(binding => ContainsIdentifier(line, binding.SymbolName));
    }

    private static string StripLineComment(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }

    private static string BuildSignature(
        string functionName,
        string sourceFilePath,
        IReadOnlyList<string> lines,
        IReadOnlyList<Binding> bindings,
        string signatureSalt)
    {
        StringBuilder builder = new();
        builder.Append(functionName).AppendLine();
        builder.Append("salt:").Append(signatureSalt).AppendLine();
        builder.Append(sourceFilePath).Append(':');
        try
        {
            builder.Append(File.GetLastWriteTimeUtc(sourceFilePath).Ticks);
        }
        catch
        {
            builder.Append('0');
        }
        builder.AppendLine();
        foreach (Binding binding in bindings)
        {
            builder.Append(binding.SymbolName).Append(':').Append(binding.CType).AppendLine();
        }
        foreach (string line in lines)
        {
            builder.AppendLine(line);
        }
        return builder.ToString();
    }

    private static List<Binding> BuildBindings(IEnumerable<WatchItem> watchItems)
    {
        List<Binding> all = watchItems
            .Where(item => item.Enabled && !item.IsChild)
            .Select(item => CreateBinding(item))
            .Where(binding => binding.SymbolName.Length > 0)
            .ToList();

        HashSet<string> ambiguous = all.GroupBy(binding => binding.SymbolName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return all.Where(binding => !ambiguous.Contains(binding.SymbolName)).ToList();
    }

    private static Binding CreateBinding(WatchItem item)
    {
        string symbolName = GetIdentifierBase(item.Name);
        if (symbolName.Length == 0 || IsCKeyword(symbolName))
        {
            return new Binding(item, "", "int", Signed: true, Bytes: 4, IsFloat: false);
        }

        int bytes = Math.Clamp(item.Size, 1, 4);
        if (IsFloatWatchItem(item))
        {
            return new Binding(item, symbolName, "float", Signed: true, Bytes: 4, IsFloat: true);
        }

        bool signed = IsSignedWatchItem(item);
        string cType = (bytes, signed) switch
        {
            (1, true) => "signed char",
            (1, false) => "signed char",
            (2, true) => "signed short",
            (2, false) => "signed short",
            (_, true) => "signed int",
            _ => "signed int"
        };
        return new Binding(item, symbolName, cType, signed, bytes, IsFloat: false);
    }

    private static Value ToValue(double value, Binding binding)
    {
        if (binding.IsFloat)
        {
            return (Value)(float)value;
        }

        long rounded = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        return (binding.Bytes, binding.Signed) switch
        {
            (1, true) => (Value)(sbyte)rounded,
            (1, false) => (Value)(byte)rounded,
            (2, true) => (Value)(short)rounded,
            (2, false) => (Value)(ushort)rounded,
            (_, true) => (Value)(int)rounded,
            _ => (Value)(uint)rounded
        };
    }

    private static double FromValue(Value value, Binding binding)
    {
        if (binding.IsFloat)
        {
            return value.Float32Value;
        }

        return (binding.Bytes, binding.Signed) switch
        {
            (1, true) => value.Int8Value,
            (1, false) => value.UInt8Value,
            (2, true) => value.Int16Value,
            (2, false) => value.UInt16Value,
            (_, true) => value.Int32Value,
            _ => value.UInt32Value
        };
    }

    private static bool IsSignedWatchItem(WatchItem item)
    {
        string typeName = item.TypeName.Trim();
        if (typeName.Length == 0)
        {
            return true;
        }

        string t = Regex.Replace(typeName, @"\b(const|volatile|static|extern|register)\b", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+", " ").Trim().ToLowerInvariant();
        if (Regex.IsMatch(t, @"\b(unsigned|uint|uint8_t|uint16_t|uint32_t|u8|u16|u32|byte|word|dword|bool|boolean)\b") ||
            t.Contains("uint", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("uchar", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("ushort", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsFloatWatchItem(WatchItem item)
    {
        string typeName = item.TypeName.Trim();
        return typeName.Equals("float", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("double", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(typeName, @"\b(float|double)\b", RegexOptions.IgnoreCase);
    }

    private static string GetIdentifierBase(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsIdentifierStart(text[0]))
        {
            return "";
        }

        int end = 1;
        while (end < text.Length && IsIdentifierChar(text[end]))
        {
            end++;
        }
        return text[..end];
    }

    private static bool ContainsIdentifier(string line, string identifier)
    {
        if (identifier.Length == 0 || line.Length < identifier.Length)
        {
            return false;
        }

        int index = 0;
        while (index <= line.Length - identifier.Length)
        {
            int found = line.IndexOf(identifier, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            int before = found - 1;
            int after = found + identifier.Length;
            bool leftOk = before < 0 || !IsIdentifierChar(line[before]);
            bool rightOk = after >= line.Length || !IsIdentifierChar(line[after]);
            if (leftOk && rightOk)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }

    private static bool IsIdentifierStart(char c)
    {
        return c == '_' || char.IsLetter(c);
    }

    private static bool IsIdentifierChar(char c)
    {
        return c == '_' || char.IsLetterOrDigit(c);
    }

    private static bool IsCKeyword(string value)
    {
        return value is "auto" or "break" or "case" or "char" or "const" or "continue" or "default" or "do"
            or "double" or "else" or "enum" or "extern" or "float" or "for" or "goto" or "if" or "inline"
            or "int" or "long" or "register" or "restrict" or "return" or "short" or "signed" or "sizeof"
            or "static" or "struct" or "switch" or "typedef" or "union" or "unsigned" or "void" or "volatile"
            or "while";
    }

    private static List<FunctionSnippet> LoadCalledFunctionSnippets(
        string sourceFilePath,
        IEnumerable<string> callerLines,
        IReadOnlyList<Binding> bindings,
        Func<string, string?>? resolveFunctionSource)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return new List<FunctionSnippet>();
        }

        Queue<string> pending = new(FindCalledFunctionNames(callerLines));
        if (pending.Count == 0)
        {
            return new List<FunctionSnippet>();
        }

        string sourceText;
        try
        {
            sourceText = ReadSourceText(sourceFilePath);
        }
        catch
        {
            return new List<FunctionSnippet>();
        }

        Dictionary<string, FunctionSnippet> snippets = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0 && snippets.Count < MaxHelperFunctions)
        {
            string name = pending.Dequeue();
            if (!visited.Add(name))
            {
                continue;
            }

            if ((!TryExtractFunctionDefinition(sourceText, name, out string functionText) || !IsSupportedVoidFunction(functionText, name)) &&
                resolveFunctionSource != null)
            {
                string? resolved = resolveFunctionSource(name);
                if (!string.IsNullOrWhiteSpace(resolved) &&
                    IsSupportedVoidFunction(resolved, name))
                {
                    functionText = resolved;
                }
            }

            if (!string.IsNullOrWhiteSpace(functionText) &&
                IsSupportedVoidFunction(functionText, name))
            {
                FunctionSnippet snippet = new(name, functionText);
                snippets[name] = snippet;
                foreach (string nested in FindCalledFunctionNames(ExtractBodyLines(SplitLines(functionText))))
                {
                    if (!visited.Contains(nested))
                    {
                        pending.Enqueue(nested);
                    }
                }
            }
        }

        return snippets.Values.ToList();
    }

    private static List<FunctionSnippet> FilterRelevantFunctions(
        List<FunctionSnippet> snippets,
        IEnumerable<string> callerLines,
        IReadOnlyList<Binding> bindings)
    {
        Dictionary<string, FunctionSnippet> byName = snippets.ToDictionary(snippet => snippet.Name, StringComparer.OrdinalIgnoreCase);
        HashSet<string> relevant = new(StringComparer.OrdinalIgnoreCase);

        foreach (FunctionSnippet snippet in snippets)
        {
            if (LineMentionsBinding(snippet.SourceText, bindings))
            {
                relevant.Add(snippet.Name);
            }
        }

        foreach (string line in callerLines)
        {
            if (!LineMentionsBinding(line, bindings))
            {
                continue;
            }

            foreach (string called in FindCalledFunctionNames(new[] { line }))
            {
                if (byName.ContainsKey(called))
                {
                    relevant.Add(called);
                }
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (FunctionSnippet snippet in snippets)
            {
                if (relevant.Contains(snippet.Name))
                {
                    continue;
                }

                IEnumerable<string> bodyLines = ExtractBodyLines(SplitLines(snippet.SourceText));
                foreach (string line in bodyLines)
                {
                    if (!LineMentionsBinding(line, bindings) && !FindCalledFunctionNames(new[] { line }).Any(relevant.Contains))
                    {
                        continue;
                    }

                    relevant.Add(snippet.Name);
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return snippets.Where(snippet => relevant.Contains(snippet.Name)).ToList();
    }

    private static bool IsSupportedVoidFunction(string sourceText, string functionName)
    {
        if (!TryGetFunctionHeaderAndBody(sourceText, out string header, out _))
        {
            return false;
        }

        string cleanHeader = NormalizeFunctionHeader(header);
        return Regex.IsMatch(cleanHeader, @"(^|\s)void\s+" + Regex.Escape(functionName) + @"\s*\(", RegexOptions.IgnoreCase);
    }

    private static SanitizedFunction BuildSanitizedFunction(
        FunctionSnippet snippet,
        IReadOnlyList<Binding> bindings,
        IReadOnlySet<string> callableFunctions)
    {
        if (!TryGetFunctionHeaderAndBody(snippet.SourceText, out string header, out string body))
        {
            return new SanitizedFunction(snippet.Name, "", "");
        }

        string cleanHeader = NormalizeFunctionHeader(header);
        if (!Regex.IsMatch(cleanHeader, @"(^|\s)void\s+" + Regex.Escape(snippet.Name) + @"\s*\(", RegexOptions.IgnoreCase))
        {
            return new SanitizedFunction(snippet.Name, "", "");
        }

        StringBuilder source = new();
        source.AppendLine(cleanHeader);
        source.AppendLine("{");
        foreach (string rawLine in SplitLines(body))
        {
            string line = SanitizeLine(rawLine, bindings, callableFunctions, keepSimpleStatements: true);
            if (line.Length > 0)
            {
                source.AppendLine(line);
            }
        }
        source.AppendLine("}");

        string prototype = cleanHeader.TrimEnd();
        if (!prototype.EndsWith(")", StringComparison.Ordinal))
        {
            return new SanitizedFunction(snippet.Name, "", "");
        }
        return new SanitizedFunction(snippet.Name, prototype + ";", source.ToString());
    }

    private static bool TryGetFunctionHeaderAndBody(string sourceText, out string header, out string body)
    {
        string text = StripBlockComments(sourceText).Replace("\r\n", "\n").Replace('\r', '\n');
        header = "";
        body = "";
        int openBrace = text.IndexOf('{');
        if (openBrace < 0)
        {
            return false;
        }

        int closeBrace = FindMatchingBrace(text, openBrace);
        if (closeBrace < 0)
        {
            return false;
        }

        header = text[..openBrace];
        body = text.Substring(openBrace + 1, closeBrace - openBrace - 1);
        return true;
    }

    private static string NormalizeFunctionHeader(string header)
    {
        string normalized = string.Join(" ", SplitLines(header)
                .Select(StripLineComment)
                .Select(line => Regex.Replace(line.Trim(), @"\s+", " "))
                .Where(line => line.Length > 0))
            .Trim();
        normalized = Regex.Replace(normalized, @"\bunsigned\s+int\b", "int", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bunsigned\s+short\b", "short", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bunsigned\s+char\b", "char", RegexOptions.IgnoreCase);
        return normalized;
    }

    private static List<InferredGlobal> BuildInferredGlobalDeclarations(
        IReadOnlyList<string> bodyLines,
        IReadOnlyList<SanitizedFunction> helpers,
        IReadOnlyList<Binding> bindings,
        IReadOnlySet<string> callableFunctions)
    {
        HashSet<string> known = bindings
            .Select(binding => binding.SymbolName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in callableFunctions)
        {
            known.Add(name);
        }
        known.Add(EntryFunctionName);

        Dictionary<string, string> globals = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in bodyLines)
        {
            AddInferredIdentifiers(line, known, globals);
        }
        foreach (SanitizedFunction helper in helpers)
        {
            foreach (string line in SplitLines(helper.SourceText))
            {
                AddInferredIdentifiers(line, known, globals);
            }
        }

        return globals
            .Take(MaxInferredGlobals)
            .Select(pair => new InferredGlobal(pair.Key, pair.Value))
            .ToList();
    }

    private static void AddInferredIdentifiers(string rawLine, HashSet<string> known, Dictionary<string, string> globals)
    {
        string line = StripLineComment(rawLine);
        line = Regex.Replace(line, "\"(?:\\\\.|[^\"])*\"", "\"\"");
        line = Regex.Replace(line, "'(?:\\\\.|[^'])*'", "''");

        string floatHint = "";
        Match assign = Regex.Match(line, @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=");
        if (assign.Success &&
            (line.Contains("(float)", StringComparison.OrdinalIgnoreCase) ||
             Regex.IsMatch(line, @"\d+\.\d+")))
        {
            floatHint = assign.Groups["name"].Value;
        }

        foreach (Match match in Regex.Matches(line, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
        {
            string name = match.Value;
            if (known.Contains(name) ||
                IsCKeyword(name) ||
                IsKnownTypeAlias(name) ||
                name.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int after = match.Index + match.Length;
            while (after < line.Length && char.IsWhiteSpace(line[after]))
            {
                after++;
            }
            if (after < line.Length && line[after] == '(')
            {
                continue;
            }

            string cType = name.Equals(floatHint, StringComparison.OrdinalIgnoreCase) ? "float" : "int";
            if (!globals.ContainsKey(name))
            {
                globals.Add(name, cType);
            }
            else if (cType == "float")
            {
                globals[name] = "float";
            }
        }
    }

    private static bool IsKnownTypeAlias(string name)
    {
        return name is "int8_t" or "uint8_t" or "int16_t" or "uint16_t" or "int32_t" or "uint32_t"
            or "s8" or "u8" or "s16" or "u16" or "s32" or "u32"
            or "BYTE" or "WORD" or "DWORD";
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static IEnumerable<string> FindCalledFunctionNames(IEnumerable<string> lines)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            string code = StripLineComment(line);
            foreach (Match match in Regex.Matches(code, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
            {
                string name = match.Groups["name"].Value;
                if (!IsCKeyword(name) && !names.Contains(name))
                {
                    names.Add(name);
                    yield return name;
                }
            }
        }
    }

    private static bool TryExtractFunctionDefinition(string text, string functionName, out string functionText)
    {
        functionText = "";
        foreach (Match match in Regex.Matches(text, @"\b" + Regex.Escape(functionName) + @"\s*\("))
        {
            int closeParen = FindMatchingParen(text, match.Index + functionName.Length);
            if (closeParen < 0)
            {
                continue;
            }

            int openBrace = SkipTriviaAndComments(text, closeParen + 1);
            if (openBrace < 0 || openBrace >= text.Length || text[openBrace] != '{')
            {
                continue;
            }

            int closeBrace = FindMatchingBrace(text, openBrace);
            if (closeBrace < 0)
            {
                continue;
            }

            int lineStart = text.LastIndexOf('\n', match.Index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            functionText = text.Substring(lineStart, closeBrace - lineStart + 1)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            return true;
        }
        return false;
    }

    private static int FindMatchingParen(string text, int openParen)
    {
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool escape = false;
        for (int i = openParen; i < text.Length; i++)
        {
            char c = text[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if ((inString || inChar) && c == '\\')
            {
                escape = true;
                continue;
            }
            if (!inChar && c == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString && c == '\'')
            {
                inChar = !inChar;
                continue;
            }
            if (inString || inChar)
            {
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
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

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool escape = false;
        for (int i = openBrace; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (!inString && !inChar && c == '/' && next == '/')
            {
                int newline = text.IndexOf('\n', i + 2);
                if (newline < 0)
                {
                    return -1;
                }
                i = newline;
                continue;
            }
            if (!inString && !inChar && c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return -1;
                }
                i = end + 1;
                continue;
            }
            if (escape)
            {
                escape = false;
                continue;
            }
            if ((inString || inChar) && c == '\\')
            {
                escape = true;
                continue;
            }
            if (!inChar && c == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString && c == '\'')
            {
                inChar = !inChar;
                continue;
            }
            if (inString || inChar)
            {
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
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

    private static int SkipTriviaAndComments(string text, int start)
    {
        int index = start;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
            {
                int newline = text.IndexOf('\n', index + 2);
                if (newline < 0)
                {
                    return text.Length;
                }
                index = newline + 1;
                continue;
            }
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
            {
                int end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return -1;
                }
                index = end + 2;
                continue;
            }
            return index;
        }
        return index;
    }

    private static string ReadSourceText(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes);
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch
        {
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    private static string StripBlockComments(string text)
    {
        return Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private readonly record struct Binding(WatchItem Item, string SymbolName, string CType, bool Signed, int Bytes, bool IsFloat);

    private readonly record struct FunctionSnippet(string Name, string SourceText);

    private readonly record struct SanitizedFunction(string Name, string Prototype, string SourceText);

    private readonly record struct InferredGlobal(string Name, string CType);
}
