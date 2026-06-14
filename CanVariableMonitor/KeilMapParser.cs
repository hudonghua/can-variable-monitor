using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal static class KeilMapParser
{
    private static readonly Regex ArmSymbolRegex = new(
        @"^\s*(?<name>[A-Za-z_.$][A-Za-z0-9_.$]*)\s+" +
        @"(?<addr>0x[0-9a-fA-F]{6,16})\s+" +
        @"(?:(?<ov>(?!Data\b|Number\b)\S+)\s+)?" +
        @"(?<type>Data|Number)\s+" +
        @"(?<size>\d+)\s+" +
        @"(?<object>.+)$",
        RegexOptions.Compiled);

    public static List<MapSymbol> Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("变量文件不存在", path);
        }

        var symbols = new Dictionary<string, MapSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            Match m = ArmSymbolRegex.Match(line);
            if (!m.Success || m.Groups["type"].Value != "Data")
            {
                continue;
            }

            if (!uint.TryParse(m.Groups["addr"].Value[2..], System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                continue;
            }

            if (!int.TryParse(m.Groups["size"].Value, out int size) || size <= 0)
            {
                continue;
            }

            if (!IsRamAddress(address))
            {
                continue;
            }

            string name = m.Groups["name"].Value;
            if (name.StartsWith(".", StringComparison.Ordinal) || symbols.ContainsKey(name))
            {
                continue;
            }

            symbols[name] = new MapSymbol
            {
                Name = name,
                Address = address,
                Size = size,
                ObjectName = m.Groups["object"].Value.Trim()
            };
        }

        return symbols.Values.OrderBy(s => s.Address).ToList();
    }

    public static bool IsRamAddress(uint address)
    {
        return address is >= 0x10000000 and <= 0x1000FFFF
            or >= 0x20000000 and <= 0x200FFFFF
            or >= 0x1FFF0000 and <= 0x1FFFFFFF;
    }

    public static bool TryResolve(string text, IReadOnlyDictionary<string, MapSymbol> symbols, out WatchItem item, out string error)
    {
        item = null!;
        error = "";

        string raw = text.Trim();
        if (raw.Length == 0)
        {
            error = "请输入变量名";
            return false;
        }

        string name = raw;
        uint offset = 0;
        Match offsetMatch = Regex.Match(raw, @"^(?<name>[A-Za-z_.$][A-Za-z0-9_.$]*)(?:\s*(?:\+|\[)\s*(?<offset>0x[0-9a-fA-F]+|\d+)\s*\]?)?$");
        if (offsetMatch.Success)
        {
            name = offsetMatch.Groups["name"].Value;
            if (offsetMatch.Groups["offset"].Success)
            {
                string offsetText = offsetMatch.Groups["offset"].Value;
                offset = offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt32(offsetText[2..], 16)
                    : Convert.ToUInt32(offsetText);
            }
        }

        if (!symbols.TryGetValue(name, out MapSymbol? symbol))
        {
            error = "变量文件中没有找到变量；函数名不能作为监控变量";
            return false;
        }

        int remaining = Math.Max(1, symbol.Size - (int)offset);
        bool expandable = offset == 0 && IsExpandable(symbol, symbols.Values);
        item = new WatchItem
        {
            Name = offset == 0 ? symbol.Name : $"{symbol.Name}+{offset}",
            Address = symbol.Address + offset,
            Size = remaining,
            TotalSize = remaining,
            TypeName = symbol.TypeName,
            IsExpandable = expandable,
            Status = "待读取"
        };
        return true;
    }

    public static bool IsExpandable(MapSymbol symbol, IEnumerable<MapSymbol> symbols)
    {
        if (symbol.Size <= 2)
        {
            return false;
        }

        if (HasChildSymbols(symbol, symbols))
        {
            return true;
        }

        string typeName = symbol.TypeName.Trim();
        if (typeName.Length > 0)
        {
            if (typeName.Contains('[', StringComparison.Ordinal) ||
                typeName.Contains("array", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("struct", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("union", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsKnownScalarType(typeName))
            {
                return false;
            }

            return symbol.Size > 4;
        }

        return symbol.Size > 4;
    }

    private static bool HasChildSymbols(MapSymbol symbol, IEnumerable<MapSymbol> symbols)
    {
        string fieldPrefix = symbol.Name + ".";
        string arrayPrefix = symbol.Name + "[";
        uint end = symbol.Address + (uint)symbol.Size;
        return symbols.Any(s =>
            !s.Name.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase) &&
            (s.Name.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase) ||
             s.Name.StartsWith(arrayPrefix, StringComparison.OrdinalIgnoreCase)) &&
            s.Address >= symbol.Address &&
            s.Address < end);
    }

    private static bool IsKnownScalarType(string typeName)
    {
        string t = typeName.Trim();
        string[] scalarTokens =
        {
            "char",
            "short",
            "int",
            "long",
            "float",
            "double",
            "bool",
            "uint8_t",
            "int8_t",
            "uint16_t",
            "int16_t",
            "uint32_t",
            "int32_t",
            "uint64_t",
            "int64_t"
        };

        if (t.Contains('*', StringComparison.Ordinal))
        {
            return true;
        }

        return scalarTokens.Any(token =>
            Regex.IsMatch(t, $@"(^|[^A-Za-z0-9_]){Regex.Escape(token)}([^A-Za-z0-9_]|$)", RegexOptions.IgnoreCase));
    }
}
