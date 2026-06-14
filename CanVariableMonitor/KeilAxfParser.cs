using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal static class KeilAxfParser
{
    private static readonly Regex AxfSymbolRegex = new(
        @"^\s*(?<addr>0x[0-9a-fA-F]{6,16})\s+" +
        @"(?<size>0x[0-9a-fA-F]+|\d+)\s+" +
        @"(?:\*\s*)?" +
        @"(?<name>[A-Za-z_.$][A-Za-z0-9_.$\[\]]*)\s+" +
        @"(?<type>.+?)\s*$",
        RegexOptions.Compiled);

    public static bool TryParse(string axfPath, out List<MapSymbol> symbols, out string message)
    {
        symbols = new List<MapSymbol>();
        message = "";

        string? fromelf = FindFromElf();
        if (fromelf == null)
        {
            message = "未找到 Keil fromelf.exe";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fromelf,
                Arguments = $"--text -a \"{axfPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (!process.HasExited)
            {
                process.Kill();
                message = "fromelf 解析超时";
                return false;
            }

            if (output.Length == 0 && error.Length > 0)
            {
                message = error.Trim();
                return false;
            }

            symbols = ParseFromElfText(output);
            message = $"已读取 AXF 调试信息：{Path.GetFileName(axfPath)}，变量 {symbols.Count} 个";
            return symbols.Count > 0;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static List<MapSymbol> ParseFromElfText(string text)
    {
        var symbols = new Dictionary<string, MapSymbol>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            Match m = AxfSymbolRegex.Match(line);
            if (!m.Success)
            {
                continue;
            }

            if (!TryParseNumber(m.Groups["addr"].Value, out uint address) || !KeilMapParser.IsRamAddress(address))
            {
                continue;
            }

            if (!TryParseNumber(m.Groups["size"].Value, out uint sizeValue) || sizeValue == 0 || sizeValue > int.MaxValue)
            {
                continue;
            }

            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0 || name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            symbols[name] = new MapSymbol
            {
                Name = name,
                Address = address,
                Size = (int)sizeValue,
                TypeName = m.Groups["type"].Value.Trim(),
                ObjectName = ""
            };
        }

        return symbols.Values
            .OrderBy(s => s.Address)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParseNumber(string text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        return uint.TryParse(text, out value);
    }

    private static string? FindFromElf()
    {
        return KeilToolLocator.FindFromElf();
    }
}
