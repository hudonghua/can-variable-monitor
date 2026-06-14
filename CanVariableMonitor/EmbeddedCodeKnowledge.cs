using System.Text.RegularExpressions;

namespace CanVariableMonitor;

internal sealed record EmbeddedCodeInsight(string Kind, string Name, string Detail);

internal sealed record EmbeddedCodeAnalysis(
	string Domain,
	string Summary,
	int BusinessScore,
	bool IsBusinessAnchor,
	IReadOnlyList<string> Signals,
	IReadOnlyList<EmbeddedCodeInsight> Insights);

internal static class EmbeddedCodeKnowledge
{
	private static readonly string[] BusinessFileTokens =
	{
		"app_usr", "usr", "user", "lcd", "logic", "control", "ctrl", "disp", "screen", "main"
	};

	private static readonly string[] BusinessNameTokens =
	{
		"logic", "ctrl", "control", "binding", "work", "mode", "state", "press",
		"mpa", "motor", "pump", "dfs", "remote", "key", "alarm", "fault", "stop",
		"valve", "sensor", "page", "lcd"
	};

	private static readonly Regex CanRxRegex = new(
		@"\b(?:CAN\d*_|Can\d*_|can\d*_|)(?:RBuf|RxBuf|Rcv|Recv|Receive|Rx)\b|\bCAN\d?_RBuf\b|\bCAN_RBuf\b",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex CanTxRegex = new(
		@"\b(?:CAN\d*_|Can\d*_|can\d*_|)(?:SBuf|TxBuf|Send|Tx)\b|\bCAN\d?_SBuf\b|\bCAN_SBuf\b",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex IoRegex = new(
		@"\b[A-Za-z0-9_]*(?:_DI|_DO|DI_|DO_|IO|GPIO)[A-Za-z0-9_]*\b",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex SafetyRegex = new(
		@"\b(?:E_STOP|STOP|JiTing|Alarm|Fault|Protect|Timeout|Err|Jiting|Emergency)\b",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex StorageRegex = new(
		@"(?:AT24|EEPROM|FLASH|Save|Store|Param)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex DriverRegex = new(
		@"(?:IRQ|ISR|Handler|DMA|UART|USART|SPI|I2C|ADC|PWM|WDT|SysTick|SystemInit|Driver|BSP|HAL)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex IdentifierRegex = new(
		@"\b[A-Za-z_][A-Za-z0-9_]*\b",
		RegexOptions.Compiled);

	public static EmbeddedCodeAnalysis Analyze(string functionName, string filePath, string body)
	{
		string name = functionName ?? "";
		string path = (filePath ?? "").Replace('\\', '/');
		string text = body ?? "";
		string combined = path + "/" + name;
		string lowerCombined = combined.ToLowerInvariant();

		bool isMain = name.Equals("main", StringComparison.OrdinalIgnoreCase);
		bool is10ms = name.Equals("MyLogic_10ms", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("10ms", StringComparison.OrdinalIgnoreCase);
		bool is1ms = name.Equals("MyLogic_1ms", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("1ms", StringComparison.OrdinalIgnoreCase);
		bool hasLcdWrite = text.Contains("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase);
		bool hasCanRx = CanRxRegex.IsMatch(text);
		bool hasCanTx = CanTxRegex.IsMatch(text);
		bool hasCan = hasCanRx || hasCanTx || lowerCombined.Contains("can");
		bool hasSensor = text.Contains("Sensor_Logic_V", StringComparison.OrdinalIgnoreCase) ||
			Regex.IsMatch(text, @"\bAI_Pin\d+\b|\bgADV_mV\b|Senso_Logic_out", RegexOptions.IgnoreCase);
		bool hasIo = IoRegex.IsMatch(combined + " " + text);
		bool hasSafety = SafetyRegex.IsMatch(combined + " " + text);
		bool hasStorage = StorageRegex.IsMatch(combined);
		bool hasDriver = DriverRegex.IsMatch(combined);
		bool businessFile = BusinessFileTokens.Any(token => lowerCombined.Contains(token));
		bool businessName = BusinessNameTokens.Any(token => lowerCombined.Contains(token));

		string domain = PickDomain(isMain, is10ms, is1ms, hasLcdWrite, hasCanRx, hasCanTx, hasCan, hasSensor, hasIo, hasSafety, hasStorage, hasDriver, businessFile, businessName);
		List<string> signals = ExtractBusinessSignals(text).ToList();
		string summary = BuildSummary(name, domain, hasLcdWrite, hasCanRx, hasCanTx, hasSensor, hasIo, hasSafety, hasStorage, signals);
		int score = BuildBusinessScore(domain, businessFile, businessName, hasLcdWrite, hasCanRx, hasCanTx, hasSensor, hasIo, hasSafety, signals.Count);
		bool anchor = score >= 85 || hasLcdWrite || hasCanRx || hasCanTx || hasSensor || hasSafety || is10ms || is1ms;
		List<EmbeddedCodeInsight> insights = BuildInsights(summary, domain, text, signals, hasLcdWrite, hasCanRx, hasCanTx, hasSensor, hasIo, hasSafety);

		return new EmbeddedCodeAnalysis(domain, summary, score, anchor, signals, insights);
	}

	public static string ImproveSummary(string functionName, string filePath, string body, string existingSummary)
	{
		EmbeddedCodeAnalysis analysis = Analyze(functionName, filePath, body);
		if (string.IsNullOrWhiteSpace(analysis.Summary))
		{
			return existingSummary;
		}

		if (string.IsNullOrWhiteSpace(existingSummary) ||
			existingSummary.Equals(functionName, StringComparison.OrdinalIgnoreCase) ||
			IsWeakSummary(existingSummary))
		{
			return analysis.Summary;
		}

		return existingSummary.Length <= 28 ? existingSummary : analysis.Summary;
	}

	public static string DomainLabel(string domain)
	{
		return domain switch
		{
			"main" => "主循环",
			"period10" => "10ms业务",
			"period1" => "1ms业务",
			"disp" => "显示业务",
			"can-rx" => "CAN接收",
			"can-tx" => "CAN发送",
			"can" => "CAN通讯",
			"analog" => "模拟量",
			"io" => "IO映射",
			"safety" => "保护逻辑",
			"storage" => "参数保存",
			"driver" => "底层驱动",
			"business" => "业务逻辑",
			_ => "代码"
		};
	}

	private static string PickDomain(
		bool isMain,
		bool is10ms,
		bool is1ms,
		bool hasLcdWrite,
		bool hasCanRx,
		bool hasCanTx,
		bool hasCan,
		bool hasSensor,
		bool hasIo,
		bool hasSafety,
		bool hasStorage,
		bool hasDriver,
		bool businessFile,
		bool businessName)
	{
		if (isMain)
		{
			return "main";
		}
		if (is10ms)
		{
			return "period10";
		}
		if (is1ms)
		{
			return "period1";
		}
		if (hasSafety)
		{
			return "safety";
		}
		if (hasLcdWrite)
		{
			return "disp";
		}
		if (hasCanRx)
		{
			return "can-rx";
		}
		if (hasCanTx)
		{
			return "can-tx";
		}
		if (hasCan)
		{
			return "can";
		}
		if (hasSensor)
		{
			return "analog";
		}
		if (hasIo)
		{
			return "io";
		}
		if (hasStorage)
		{
			return "storage";
		}
		if (hasDriver && !businessFile)
		{
			return "driver";
		}
		if (businessFile || businessName)
		{
			return "business";
		}
		return "normal";
	}

	private static string BuildSummary(
		string functionName,
		string domain,
		bool hasLcdWrite,
		bool hasCanRx,
		bool hasCanTx,
		bool hasSensor,
		bool hasIo,
		bool hasSafety,
		bool hasStorage,
		IReadOnlyList<string> signals)
	{
		if (functionName.Equals("MyLogic_10ms", StringComparison.OrdinalIgnoreCase))
		{
			return "10ms业务入口：周期扫描输入、控制、通讯和输出";
		}
		if (functionName.Equals("MyLogic_1ms", StringComparison.OrdinalIgnoreCase))
		{
			return "1ms快速业务入口：处理高频计时、边沿和快速状态";
		}
		if (functionName.Equals("PIN_Binding", StringComparison.OrdinalIgnoreCase))
		{
			return "输入绑定：把AI/DI换算成压力、开关和业务状态";
		}
		if (functionName.Equals("Sensor_Logic_V", StringComparison.OrdinalIgnoreCase))
		{
			return "传感器换算：按上下限把电压值转换为工程量";
		}
		if (hasSafety)
		{
			return "安全保护：处理急停、故障、超时或保护条件";
		}
		if (hasLcdWrite)
		{
			return "显示逻辑：把业务变量写到屏幕寄存器";
		}
		if (hasCanRx && hasCanTx)
		{
			return "CAN通讯：解析接收数据并组织发送数据";
		}
		if (hasCanRx)
		{
			return "CAN接收解析：把报文字节写入业务变量";
		}
		if (hasCanTx)
		{
			return "CAN发送组织：把业务变量打包到报文";
		}
		if (hasSensor)
		{
			return "模拟量处理：采样、滤波或工程量换算";
		}
		if (hasIo)
		{
			return "IO映射：处理DI输入和DO输出";
		}
		if (hasStorage)
		{
			return "参数保存：读写EEPROM/Flash中的配置";
		}
		if (signals.Count > 0)
		{
			return "业务逻辑：围绕 " + string.Join("、", signals.Take(3)) + " 处理";
		}
		return domain == "business" ? "业务逻辑：处理现场控制状态" : "";
	}

	private static int BuildBusinessScore(
		string domain,
		bool businessFile,
		bool businessName,
		bool hasLcdWrite,
		bool hasCanRx,
		bool hasCanTx,
		bool hasSensor,
		bool hasIo,
		bool hasSafety,
		int signalCount)
	{
		int score = 0;
		if (domain is "period10" or "period1")
		{
			score += 140;
		}
		if (hasSafety)
		{
			score += 115;
		}
		if (hasLcdWrite)
		{
			score += 110;
		}
		if (hasCanRx || hasCanTx)
		{
			score += 96;
		}
		if (hasSensor)
		{
			score += 90;
		}
		if (hasIo)
		{
			score += 72;
		}
		if (businessFile)
		{
			score += 50;
		}
		if (businessName)
		{
			score += 34;
		}
		score += Math.Min(36, signalCount * 6);
		if (domain == "driver")
		{
			score -= 80;
		}
		return score;
	}

	private static List<EmbeddedCodeInsight> BuildInsights(
		string summary,
		string domain,
		string body,
		IReadOnlyList<string> signals,
		bool hasLcdWrite,
		bool hasCanRx,
		bool hasCanTx,
		bool hasSensor,
		bool hasIo,
		bool hasSafety)
	{
		var result = new List<EmbeddedCodeInsight>();
		if (!string.IsNullOrWhiteSpace(summary))
		{
			result.Add(new EmbeddedCodeInsight("理解", DomainLabel(domain), summary));
		}
		if (hasSensor)
		{
			result.Add(new EmbeddedCodeInsight("输入", "AI/传感器", "关注AI_Pin、gADV_mV、上下限和换算后的压力变量"));
		}
		if (hasCanRx)
		{
			result.Add(new EmbeddedCodeInsight("输入", "CAN接收", "关注CAN_RBuf字节到业务变量的赋值"));
		}
		if (hasCanTx)
		{
			result.Add(new EmbeddedCodeInsight("输出", "CAN发送", "关注业务变量到CAN_SBuf字节的打包"));
		}
		if (hasLcdWrite)
		{
			result.Add(new EmbeddedCodeInsight("输出", "屏幕显示", "关注LCD_WR_Data2B参数里的业务变量"));
		}
		if (hasIo)
		{
			result.Add(new EmbeddedCodeInsight("IO", "输入输出", "关注_DI/_DO变量和硬件点位映射"));
		}
		if (hasSafety)
		{
			result.Add(new EmbeddedCodeInsight("保护", "安全条件", "关注急停、报警、故障和通信超时分支"));
		}
		foreach (string signal in signals.Take(8))
		{
			result.Add(new EmbeddedCodeInsight("信号", signal, GuessSignalMeaning(signal, body)));
		}
		return result;
	}

	public static IEnumerable<string> ExtractBusinessSignals(string body)
	{
		var result = new LinkedList<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Add(string value)
		{
			if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || !seen.Add(value))
			{
				return;
			}
			if (!IsNoiseIdentifier(value))
			{
				result.AddLast(value);
			}
		}

		foreach (Match call in Regex.Matches(body, @"\bLCD_WR_Data2B\s*\((?<args>[^;]*)\)", RegexOptions.IgnoreCase))
		{
			foreach (Match id in IdentifierRegex.Matches(call.Groups["args"].Value))
			{
				Add(id.Value);
			}
		}

		foreach (Match call in Regex.Matches(body, @"\bSensor_Logic_V\s*\((?<args>[^;]*)\)", RegexOptions.IgnoreCase))
		{
			foreach (Match id in IdentifierRegex.Matches(call.Groups["args"].Value))
			{
				Add(id.Value);
			}
		}

		foreach (Match assign in Regex.Matches(body, @"(?<left>\b[A-Za-z_][A-Za-z0-9_]*\b)\s*=\s*(?<right>[^;]+);"))
		{
			string left = assign.Groups["left"].Value;
			string right = assign.Groups["right"].Value;
			if (LooksBusinessIdentifier(left) ||
				right.Contains("CAN", StringComparison.OrdinalIgnoreCase) ||
				right.Contains("Senso_Logic_out", StringComparison.OrdinalIgnoreCase))
			{
				Add(left);
			}
			foreach (Match id in IdentifierRegex.Matches(right))
			{
				if (LooksBusinessIdentifier(id.Value))
				{
					Add(id.Value);
				}
			}
		}

		foreach (Match id in IdentifierRegex.Matches(body))
		{
			if (LooksBusinessIdentifier(id.Value))
			{
				Add(id.Value);
			}
		}

		return result.Take(24);
	}

	private static bool LooksBusinessIdentifier(string value)
	{
		if (IsNoiseIdentifier(value))
		{
			return false;
		}
		return Regex.IsMatch(value, @"(?:AI_Pin\d+|DI_|DO_|_DI|_DO|Press|Mpa|Motor|Pump|DFS|Alarm|Fault|STOP|Dly|Flg|Flag|Mode|Data|Remote|Key|LCD|Valve|Count)", RegexOptions.IgnoreCase);
	}

	private static bool IsNoiseIdentifier(string value)
	{
		string[] noise =
		{
			"if", "else", "for", "while", "switch", "case", "return", "void", "int", "char",
			"short", "long", "float", "double", "unsigned", "signed", "static", "extern",
			"LCD_WR_Data2B", "Sensor_Logic_V", "CAN_RBuf", "CAN_SBuf", "Senso_Logic_out",
			"TRUE", "FALSE", "NULL"
		};
		return value.All(c => c == '_' || char.IsDigit(c)) ||
			noise.Any(x => value.Equals(x, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsWeakSummary(string text)
	{
		string value = text.Trim();
		return value.Length == 0 ||
			value.Equals("业务逻辑", StringComparison.OrdinalIgnoreCase) ||
			value.Equals("现场控制", StringComparison.OrdinalIgnoreCase) ||
			value.Contains("涓", StringComparison.Ordinal) ||
			value.Contains("鍑", StringComparison.Ordinal) ||
			value.Contains("鎺", StringComparison.Ordinal) ||
			value.Contains("杈", StringComparison.Ordinal) ||
			value.Contains("鏄", StringComparison.Ordinal) ||
			value.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
			Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
	}

	private static string GuessSignalMeaning(string signal, string body)
	{
		if (Regex.IsMatch(signal, @"AI_Pin\d+", RegexOptions.IgnoreCase))
		{
			return "模拟量输入，通常来自ADC毫伏值";
		}
		if (signal.Contains("_DI", StringComparison.OrdinalIgnoreCase) || signal.Contains("DI_", StringComparison.OrdinalIgnoreCase))
		{
			return "开关量输入或现场状态";
		}
		if (signal.Contains("_DO", StringComparison.OrdinalIgnoreCase) || signal.Contains("DO_", StringComparison.OrdinalIgnoreCase))
		{
			return "开关量输出或执行器控制";
		}
		if (signal.Contains("Press", StringComparison.OrdinalIgnoreCase) || signal.Contains("Mpa", StringComparison.OrdinalIgnoreCase))
		{
			return "压力/工程量，适合现场监控";
		}
		if (signal.Contains("Motor", StringComparison.OrdinalIgnoreCase))
		{
			return "电机控制或电机状态";
		}
		if (signal.Contains("Pump", StringComparison.OrdinalIgnoreCase))
		{
			return "泵控制或泵状态";
		}
		if (signal.Contains("Dly", StringComparison.OrdinalIgnoreCase))
		{
			return "延时计数，确认周期后再判断时间";
		}
		if (signal.Contains("Flg", StringComparison.OrdinalIgnoreCase) || signal.Contains("Flag", StringComparison.OrdinalIgnoreCase))
		{
			return "状态标志，适合观察分支是否成立";
		}
		if (body.Contains("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
		{
			return "参与屏幕显示";
		}
		return "业务变量，可加入监控";
	}
}
