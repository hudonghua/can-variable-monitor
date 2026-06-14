using System.Reflection;
using System.Text;

namespace CanVariableMonitor;

internal static class BundledFirmwareAgent
{
    private const string AgentFileName = "can_monitor_agent.c";
    private const string ResourceName = "CanVariableMonitor.can_monitor_agent.c";

    private static byte[]? _bytes;
    private static uint? _version;

    public static uint ReadVersion()
    {
        if (_version.HasValue)
        {
            return _version.Value;
        }

        byte[] bytes = ReadBytes();
        string text = Encoding.ASCII.GetString(bytes);
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("CAN_MONITOR_AGENT_VERSION", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (!parts[i].Equals("CAN_MONITOR_AGENT_VERSION", StringComparison.Ordinal))
                {
                    continue;
                }

                string value = parts[i + 1].TrimEnd('U', 'u', 'L', 'l');
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hexVersion))
                {
                    _version = hexVersion;
                    return hexVersion;
                }
            }
        }

        _version = 0;
        return 0;
    }

    public static string WriteTempCopy()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CanVariableMonitor");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, AgentFileName);
        File.WriteAllBytes(path, ReadBytes());
        return path;
    }

    private static byte[] ReadBytes()
    {
        if (_bytes != null)
        {
            return _bytes;
        }

        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            string available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new FileNotFoundException("内置固件代理不存在：" + ResourceName + "；可用资源：" + available);
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        _bytes = memory.ToArray();
        return _bytes;
    }
}
