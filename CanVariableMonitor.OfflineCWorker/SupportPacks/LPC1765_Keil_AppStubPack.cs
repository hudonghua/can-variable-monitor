using System.Text;
using System.Text.RegularExpressions;

namespace CanVariableMonitor.OfflineCWorker;

internal sealed class LPC1765_Keil_AppStubPack
{
    public static readonly LPC1765_Keil_AppStubPack Default = new();

    public string CompatibilityHeaderFileName => "keil_compat.h";

    private readonly HashSet<string> _cKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "return", "sizeof",
        "typedef", "struct", "union", "enum", "static", "extern", "const", "volatile", "break",
        "continue", "goto", "void", "int", "char", "short", "long", "float", "double", "signed",
        "unsigned", "auto", "register", "true", "false", "NULL",
        "__irq", "__weak", "__IO", "__I", "__O", "__packed", "__align", "__attribute__", "__asm", "__nop",
        "reentrant", "interrupt", "using", "xdata", "idata", "pdata", "code"
    };

    private readonly HashSet<string> _knownTypeNames = new(StringComparer.OrdinalIgnoreCase)
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

    private readonly HashSet<string> _builtinFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "printf", "strlen", "strcpy", "strncpy", "memcpy", "memset", "memcmp",
        "abs", "labs", "llabs", "_BitV",
        "sin", "cos", "tan", "atan", "fabs", "sqrt", "floor", "ceil",
        "__canmon_abs", "__canmon_fabs", "__canmon_sin", "__canmon_cos", "__canmon_tan",
        "__canmon_atan", "__canmon_sqrt", "__canmon_floor", "__canmon_ceil",
        "__canmon_safe_den", "__canmon_safe_den_i64", "__canmon_safe_mod_den"
    };

    private readonly string[] _excludedDirectoryNames =
    [
        "bsp", "driver", "drivers", "cmsis", "startup", "system", "hal", "rte", "core",
        "periph", "peripheral", "uart", "usart", "adc", "gpio", "can", "timer", "tim",
        "eeprom", "flash", "i2c", "spi", "pwm", "usb", "eth", "objects", "listings"
    ];

    private readonly string[] _excludedFilePrefixes =
    [
        "startup", "system_lpc17", "core_cm", "lpc17xx", "bsp", "driver",
        "gpio", "uart", "usart", "adc", "can", "timer", "tim", "eeprom", "flash",
        "i2c", "spi", "pwm"
    ];

    public IReadOnlyList<string> CompatibilityHeaderLines { get; } =
    [
        "#ifndef CANMON_KEIL_COMPAT_H",
        "#define CANMON_KEIL_COMPAT_H",
        "typedef signed char int8_t;",
        "typedef unsigned char uint8_t;",
        "typedef signed short int16_t;",
        "typedef unsigned short uint16_t;",
        "typedef signed int int32_t;",
        "typedef unsigned int uint32_t;",
        "typedef signed char int8;",
        "typedef unsigned char uint8;",
        "typedef signed short int16;",
        "typedef unsigned short uint16;",
        "typedef signed int int32;",
        "typedef unsigned int uint32;",
        "typedef unsigned char uchar;",
        "typedef unsigned char UCHAR;",
        "typedef unsigned short ushort;",
        "typedef unsigned short USHORT;",
        "typedef unsigned int uint;",
        "typedef unsigned int UINT;",
        "typedef unsigned long ulong;",
        "typedef unsigned long ULONG;",
        "typedef unsigned char u8;",
        "typedef unsigned short u16;",
        "typedef unsigned int u32;",
        "typedef signed char s8;",
        "typedef signed short s16;",
        "typedef signed int s32;",
        "typedef unsigned char BYTE;",
        "typedef unsigned short WORD;",
        "typedef unsigned int DWORD;",
        "typedef unsigned char BOOL;",
        "typedef unsigned char INT8U;",
        "typedef unsigned short INT16U;",
        "typedef unsigned int INT32U;",
        "typedef signed char INT8S;",
        "typedef signed short INT16S;",
        "typedef signed int INT32S;",
        "typedef unsigned char bool;",
        "typedef unsigned char bit;",
        "typedef unsigned int size_t;",
        "#define true 1",
        "#define false 0",
        "#define NULL 0",
        "#define __irq",
        "#define __weak",
        "#define __IO",
        "#define __I",
        "#define __O",
        "#define __packed",
        "#define __align(x)",
        "#define __attribute__(x)",
        "#define __asm(x)",
        "#define __nop()",
        "#define reentrant",
        "#define interrupt",
        "#define using(x)",
        "#define xdata",
        "#define idata",
        "#define pdata",
        "#define code",
        "extern int printf(const char*, ...);",
        "static void* memset(void* dest, int value, size_t count) { (void)value; (void)count; return dest; }",
        "static void* memcpy(void* dest, const void* src, size_t count) { (void)src; (void)count; return dest; }",
        "static long long __canmon_abs(long long x) { return x < 0 ? -x : x; }",
        "static double __canmon_fabs(double x) { return x < 0 ? -x : x; }",
        "static double __canmon_sin(double x) {",
        "    while (x > 3.14159265358979323846) x -= 6.28318530717958647692;",
        "    while (x < -3.14159265358979323846) x += 6.28318530717958647692;",
        "    double x2 = x * x;",
        "    return x * (1.0 - x2 / 6.0 + (x2 * x2) / 120.0 - (x2 * x2 * x2) / 5040.0);",
        "}",
        "static double __canmon_cos(double x) { return __canmon_sin(x + 1.57079632679489661923); }",
        "static double __canmon_tan(double x) { double c = __canmon_cos(x); return __canmon_fabs(c) < 0.000001 ? 0.0 : __canmon_sin(x) / c; }",
        "static double __canmon_atan(double x) {",
        "    int neg = x < 0;",
        "    if (neg) x = -x;",
        "    double r = x > 1.0 ? 1.57079632679489661923 - x / (x * x + 0.28) : x / (1.0 + 0.28 * x * x);",
        "    return neg ? -r : r;",
        "}",
        "static double __canmon_sqrt(double x) { if (x <= 0.0) return 0.0; double r = x; for (int i = 0; i < 8; ++i) r = 0.5 * (r + x / r); return r; }",
        "static double __canmon_floor(double x) { long long i = (long long)x; return (double)((x < 0.0 && (double)i != x) ? i - 1 : i); }",
        "static double __canmon_ceil(double x) { long long i = (long long)x; return (double)((x > 0.0 && (double)i != x) ? i + 1 : i); }",
        "static double __canmon_safe_den(double x) { return __canmon_fabs(x) < 0.000001 ? 1.0 : x; }",
        "static long long __canmon_safe_den_i64(long long x) { return x == 0 ? 1 : x; }",
        "static long long __canmon_safe_mod_den(long long x) { return x == 0 ? 1 : x; }",
        "#define abs(x) __canmon_abs((long long)(x))",
        "#define labs(x) __canmon_abs((long long)(x))",
        "#define llabs(x) __canmon_abs((long long)(x))",
        "#define sin(x) __canmon_sin((double)(x))",
        "#define cos(x) __canmon_cos((double)(x))",
        "#define tan(x) __canmon_tan((double)(x))",
        "#define atan(x) __canmon_atan((double)(x))",
        "#define fabs(x) __canmon_fabs((double)(x))",
        "#define sqrt(x) __canmon_sqrt((double)(x))",
        "#define floor(x) __canmon_floor((double)(x))",
        "#define ceil(x) __canmon_ceil((double)(x))",
        "static long long _BitV(long long v, long long b) { return (b >= 0 && b < 63 && ((v & (1LL << b)) == (1LL << b))) ? 1 : 0; }",
        "static long long __canmon_mock_input(const char* name) { (void)name; return 0; }",
        "static void __canmon_record_output(const char* name) { printf(\"__CANMON_OUTPUT__ %s\\n\", name); }",
        "#endif"
    ];

    public void WriteSupportFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CompatibilityHeaderFileName),
            string.Join(Environment.NewLine, CompatibilityHeaderLines) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    public bool IsApplicationSourceFile(string workDirectory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string extension = Path.GetExtension(filePath);
        if (!extension.Equals(".c", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = ToRelativePath(workDirectory, filePath).Replace('\\', '/');
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Take(Math.Max(0, segments.Length - 1)).Any(segment =>
            _excludedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        if (_excludedFilePrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    public bool IsCKeyword(string name)
    {
        return _cKeywords.Contains(name);
    }

    public bool IsKnownTypeName(string name)
    {
        return _knownTypeNames.Contains(name);
    }

    public bool IsBuiltinFunctionName(string name)
    {
        return _builtinFunctionNames.Contains(name);
    }

    public bool IsStubOnlyFunctionName(string name)
    {
        return name.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sprintf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("snprintf", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CanMonitor_", StringComparison.OrdinalIgnoreCase) ||
            IsInputMockFunctionName(name) ||
            IsOutputRecordFunctionName(name) ||
            IsLowLevelServiceFunctionName(name);
    }

    public bool IsInputMockFunctionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return Regex.IsMatch(name, @"^(?:ADC|KEY|DI|IN|Input)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"(?:Read|Get|Recv|Receive|Rx).*(?:ADC|KEY|DI|CAN|UART|GPIO|Input)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"(?:CAN|UART).*?(?:Read|Get|Recv|Receive|Rx)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"(?:_RX|_Rx|Rcv|Recv)(?:_|$)", RegexOptions.IgnoreCase);
    }

    public bool IsOutputRecordFunctionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return Regex.IsMatch(name, @"^CAN\d*_?Send", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"^CAN_Send", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"^Remote.*_Send", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"^LCD(?:_|[A-Z]).*(?:WR|Write|GO|Page|Data)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"^(?:GPIO|DO|DOut|PWM).*?(?:Set|Reset|Write|Out|On|Off)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name, @"(?:_Send|_Write|_Set)(?:_|$)", RegexOptions.IgnoreCase);
    }

    public string BuildStubBody(string name)
    {
        if (name.Equals("CanMonitor_BusinessGate", StringComparison.OrdinalIgnoreCase))
        {
            return "() { return 1; }";
        }

        if (IsInputMockFunctionName(name))
        {
            return "() { return __canmon_mock_input(\"" + EscapeCString(name) + "\"); }";
        }

        if (IsOutputRecordFunctionName(name))
        {
            return "() { __canmon_record_output(\"" + EscapeCString(name) + "\"); return 0; }";
        }

        return "() { return 0; }";
    }

    private bool IsLowLevelServiceFunctionName(string name)
    {
        return name.Equals("delay_ms", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("delay_us", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(name, @"^(?:I2C|SPI|UART|USART|GPIO|CAN|ADC|PWM|EEPROM|FLASH|Timer|TIM)", RegexOptions.IgnoreCase);
    }

    private static string ToRelativePath(string root, string filePath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(root) ? filePath : Path.GetRelativePath(root, filePath);
        }
        catch
        {
            return filePath;
        }
    }

    private static string EscapeCString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
