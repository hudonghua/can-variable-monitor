using System.Text.Json.Serialization;

namespace CanVariableMonitor;

internal sealed class MapSymbol
{
    public required string Name { get; init; }
    public uint Address { get; init; }
    public int Size { get; init; }
    public string TypeName { get; init; } = "";
    public string ObjectName { get; init; } = "";
}

internal sealed class WatchItem
{
    public bool Enabled { get; set; } = true;
    public required string Name { get; set; }
    public uint Address { get; set; }
    public int Size { get; set; } = 1;
    public int TotalSize { get; set; } = 1;
    public string TypeName { get; set; } = "";
    public bool IsExpandable { get; set; }
    public bool IsChild { get; set; }
    public string ParentName { get; set; } = "";
    public string ExpandMode { get; set; } = "";
    public string ValueDec { get; set; } = "";
    public string ValueHex { get; set; } = "";
    public uint RawValue { get; set; }
    public string DisplayValue { get; set; } = "";
    public string Status { get; set; } = "未读取";
    public DateTime? LastUpdate { get; set; }

    [JsonIgnore]
    public DateTime? LastValueChange { get; set; }

    [JsonIgnore]
    public string ForceText { get; set; } = "";

    [JsonIgnore]
    public bool ForceActive { get; set; }

    [JsonIgnore]
    public bool AutoVisible { get; set; }

    [JsonIgnore]
    public int MissCount { get; set; }
}

internal sealed class MonitorProfile
{
    public string WorkDirectory { get; set; } = "";
    public string MapPath { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string ThemeName { get; set; } = "护眼暗绿";
    public uint RequestId { get; set; } = 0x7F0;
    public uint ResponseId { get; set; } = 0x7F1;
    public int PollIntervalMs { get; set; } = 20;
    public bool ShowHexValue { get; set; }
    public int LeftPanelWidth { get; set; }
    public int MonitorPanelWidth { get; set; }
    public int UiDpi { get; set; } = 96;
    public float FunctionCodeFontSize { get; set; } = 10.5f;
    public float ProgramTreeFontSize { get; set; } = 15f;
    public string KeilProjectPath { get; set; } = "";
    public string KeilTargetName { get; set; } = "";
    public bool SourceEditEnabled { get; set; } = true;
    public bool AutoBuildAfterSourceSave { get; set; } = true;
    public List<int> MonitorColumnWidths { get; set; } = new();
    public List<string> OfflineRootFunctions { get; set; } = new();
    public List<WatchItem> Variables { get; set; } = new();
}

internal readonly record struct CanFrame(uint Id, byte Dlc, byte[] Data);
