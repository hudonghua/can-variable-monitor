using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Scintilla = ScintillaNET.Scintilla;

namespace CanVariableMonitor;

public sealed partial class MainForm : Form
{
	private const string UpperComputerVersion = "V1.0";

	private const string AppDisplayName = "上位机监控";

	private readonly record struct ReadSegmentResult(bool Success, int Len, byte Status, ushort Value);

	private readonly record struct WriteSegmentResult(bool Success, int Len, byte Status, byte Command);

	private readonly record struct RuntimeControlResult(bool Success, byte Status, byte Mode);

	private readonly record struct PollCycleStats(int Requested, int Sent, int Success, int Timeout, int Skipped);

	private readonly record struct BatchReadRequest(WatchItem Item, byte Seq, uint Address, int Len, int Offset);

	private sealed record CodeLineRender(int LineNumber, string Code, List<string> Values, bool IsTrueCondition, List<InlineValueSpan> ValueSpans);

	private readonly record struct CodeValueOverlayRow(int X, int Y, string Text, bool Fresh, bool TrueCondition);

	private readonly record struct InlineWatchValuePlacement(WatchItem Item, string Token, int TokenIndex, string Value, bool Fresh);

	private readonly record struct InlineValueSpan(int Start, int Length, bool Fresh);

	private readonly record struct ScintillaValueAnnotationRow(int LineIndex, string Text);

	private readonly record struct OfflineFunctionCall(string Name, string Arguments);

	private readonly record struct OfflineParameter(string Name, string TypeName);

	private readonly record struct CodeFunctionFocus(string ContainingFunction, string CalledFunction);

	private readonly record struct OfflineWriteTrace(string FunctionName, string FilePath, int LineNumber, string Operation);

	private readonly record struct OfflineRootCandidate(string FunctionName, string Reason, int Score);

	private sealed record OfflineProgramModel(
		string Directory,
		string Signature,
		IReadOnlyList<FunctionSourceView> Roots,
		IReadOnlyList<FunctionSourceView> Sources,
		IReadOnlyList<WatchItem> Bindings,
		IReadOnlyDictionary<string, WatchItem> Aliases,
		IReadOnlyDictionary<string, IReadOnlyList<OfflineWriteTrace>> WriteTraces);

	private enum ConditionEval
	{
		Unknown,
		False,
		True
	}

	private enum BatchApplyResult
	{
		Deferred,
		Success,
		Timeout
	}

	private sealed record ThemePalette(string Name, Color Bg, Color Header, Color Panel, Color Surface, Color SurfaceAlt, Color GridHeader, Color Ink, Color Muted, Color Accent, Color Button, Color StatusOff);

	private sealed record FunctionSourceView(string FunctionName, string FilePath, int StartLine, IReadOnlyList<string> Lines, int StartIndex, int Length);

	private readonly record struct CodeViewSnapshot(FunctionSourceView Source, int FirstVisibleLine, int CurrentPosition);

	private sealed record FunctionLogicAnalysis(string Summary, IReadOnlyList<FunctionLogicStep> Steps, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, IReadOnlyList<FunctionSignal> Signals);

	private sealed record FunctionLogicStep(string Title, string Detail, string Kind, string FunctionName, int SourceLine);

	private sealed record FunctionSignal(string Name, string Direction, string Role, string TypeName, int Score, string Description = "");

	private sealed record FunctionIndexEntry(string Name, string FilePath);

	private sealed record SourceTextCacheEntry(string Text, DateTime LastWriteUtc, long Length);

	private sealed class FunctionSignalAccumulator
	{
		public MapSymbol Symbol { get; init; } = null!;
		public int Occurrences { get; set; }
		public bool Read { get; set; }
		public bool Write { get; set; }
		public int Score { get; set; }
	}

	private sealed record ProgramSearchResult(string Display, string FilePath, int LineNumber, string? FunctionName)
	{
		public override string ToString() => Display;
	}

	private sealed record ProgramInsightAction(string Kind, string Value);

	private readonly record struct PendingWatchUpdate(WatchItem Item, int Len, byte Status, uint Value, bool Timeout, bool Error);

	private sealed class Suggestion
	{
		public MapSymbol Symbol { get; }

		public Suggestion(MapSymbol symbol)
		{
			Symbol = symbol;
		}

		public override string ToString()
		{
			return Symbol.Name;
		}
	}

	private sealed class PollBackoffState
	{
		public int MissCount { get; set; }

		public DateTime RetryAfterUtc { get; set; }
	}

	private sealed class BatchReadState
	{
		public required WatchItem Item { get; init; }

		public bool IsFourByte { get; init; }

		public bool LowSent { get; set; }

		public bool HighSent { get; set; }

		public bool LowDone { get; set; }

		public bool HighDone { get; set; }

		public ReadSegmentResult Low { get; set; }

		public ReadSegmentResult High { get; set; }
	}

	private Color _bg = Color.FromArgb(9, 12, 18);

	private Color _header = Color.FromArgb(2, 6, 23);

	private Color _panel = Color.FromArgb(17, 24, 39);

	private Color _surface = Color.FromArgb(15, 23, 42);

	private Color _surfaceAlt = Color.FromArgb(20, 30, 48);

	private Color _gridHeader = Color.FromArgb(30, 41, 59);

	private Color _ink = Color.FromArgb(229, 231, 235);

	private Color _muted = Color.FromArgb(148, 163, 184);

	private Color _accent = Color.FromArgb(14, 165, 233);

	private Color _button = Color.FromArgb(30, 41, 59);

	private Color _statusOff = Color.FromArgb(75, 85, 99);

	private string _themeName = "护眼暗绿";

	private readonly BindingList<WatchItem> _watchItems = new BindingList<WatchItem>();

	private readonly BindingSource _source = new BindingSource();

	private readonly System.Windows.Forms.Timer _mapReloadTimer = new System.Windows.Forms.Timer();

	private readonly System.Windows.Forms.Timer _codeRefreshTimer = new System.Windows.Forms.Timer();

	private readonly System.Windows.Forms.Timer _valueRefreshTimer = new System.Windows.Forms.Timer();

	private readonly System.Windows.Forms.Timer _suggestionTimer = new System.Windows.Forms.Timer();

	private readonly System.Windows.Forms.Timer _programGraphDebounceTimer = new System.Windows.Forms.Timer();

	private readonly System.Windows.Forms.Timer _visibleDataRefreshTimer = new System.Windows.Forms.Timer();

	private readonly System.Windows.Forms.Timer _programInsightRefreshTimer = new System.Windows.Forms.Timer();

	private readonly string _defaultProfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor", "monitor_profile.json");

	private readonly string _connectionStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor", "connection_status.txt");

	private readonly string _diagnosticLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor", "diagnostic.log");

	private readonly AppUpdateService _appUpdateService = new();

	private readonly object _diagnosticLogLock = new object();

	private DateTime _lastLogTrimUtc = DateTime.MinValue;

	private DateTime _lastWatchContextMenuShownUtc = DateTime.MinValue;

	private Control? _lastWatchContextMenuOwner;

	private TextBox _mapPathBox;

	private TextBox _variableBox;

	private ListBox _suggestions;

	private DataGridView _grid;

	private TextBox _logBox;

	private Button _connectButton;

	private Button? _simulationButton;

	private ComboBox? _offlineRootBox;

	private Button _startButton;

	private Button _valueFormatButton;

	private Button _runtimeStepButton;

	private Button? _runtimeRunButton;

	private Button _downloadButton;

	private NumericUpDown _intervalBox;

	private Label _statusLabel;

	private Label _firmwareVersionLabel;

	private Label _appVersionLabel;

	private Button _refreshButton;

	private ComboBox _themeBox;

	private Label _symbolCountLabel;

	private ListView? _programInsightList = null;

	private Label _cycleEstimateLabel;

	private Label? _forceHoldLabel;

	private Label _programSummaryLabel;

	private Label _runtimeLocationLabel;

	private TextBox _programSearchBox;

	private ListBox _programSearchResults;

	private FlowChartView _flowChart;

	private RowStyle _programSearchResultsRow;

	private Panel _functionCodePanel;

	private RichTextBox _functionCodeBox;

	private Scintilla? _codeEditor;

	private Label _functionCodeTitle;

	private Button _functionBackButton;

	private Button _functionForwardButton;

	private RichTextBox _dataCodeBox;

	private CodeValueOverlay _codeValueOverlay;

	private CodeValueOverlayWindow? _codeValueOverlayWindow;

	private Label _dataCodeTitle;

	private Label _visibleValuesLabel;

	private Button _analysisFunctionButton;

	private Button _analysisInsightButton;

	private Panel _analysisFunctionPanel;

	private Panel _analysisInsightPanel;

	private Label _functionAnalysisTitle;

	private RichTextBox _functionAnalysisSummaryBox;

	private FlowChartView _functionAnalysisChart;

	private readonly HashSet<string> _expandedProgramTreeNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _collapsedProgramTreeNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private string _programTreeFocusedFunction = "";

	private FunctionSourceView? _currentFunctionSource;

	private FunctionSourceView? _codeViewSource;

	private readonly Stack<CodeViewSnapshot> _functionHistory = new Stack<CodeViewSnapshot>();

	private readonly Stack<CodeViewSnapshot> _functionForwardHistory = new Stack<CodeViewSnapshot>();

	private readonly object _pendingWatchLock = new object();

	private readonly Dictionary<WatchItem, PendingWatchUpdate> _pendingWatchUpdates = new Dictionary<WatchItem, PendingWatchUpdate>();

	private readonly object _pollPriorityLock = new object();

	private List<string> _visiblePollPriorityNames = new List<string>();

	private List<string> _contextPollPriorityNames = new List<string>();

	private readonly object _pollBackoffLock = new object();

	private readonly Dictionary<WatchItem, PollBackoffState> _pollBackoff = new Dictionary<WatchItem, PollBackoffState>();

	private int _visiblePipelineLimit = 1;

	private int _visiblePipelineGoodCycles;

	private int _visibleBatchStartIndex;

	private Dictionary<string, FunctionIndexEntry> _functionIndex = new Dictionary<string, FunctionIndexEntry>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, SourceTextCacheEntry> _sourceTextCache = new Dictionary<string, SourceTextCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private string _functionIndexRoot = "";

	private int _lastFunctionHoverCharIndex = -1;

	private string _lastFunctionHoverIdentifier = "";

	private bool _lastFunctionHoverNavigable;

	private (int Start, int Length)? _lastScintillaFunctionHoverRange;

	private (int Open, int Close)? _lastScintillaScopePair;

	private int _lastScintillaScopeCaret = -1;

	private string _lastFunctionHoverContextName = "";

	private DateTime _lastFunctionHoverContextUtc;

	private string _lastFunctionCodeText = "";

	private string _lastDataCodeText = "";

	private string _lastFunctionAnalysisSignature = "";

	private string _lastVisibleValuesText = "";

	private string _lastVisibleConditionSignature = "";

	private string _lastVisibleRangeSignature = "";

	private string _lastCodeValueOverlaySignature = "";

	private int _codeValueOverlayEmptyRefreshCount;

	private DateTime _lastCodeValueOverlayRowsUtc = DateTime.MinValue;

	private readonly Dictionary<int, string> _scintillaValueEolTextByLine = new Dictionary<int, string>();

	private int _pendingScintillaValueAnnotationRepaint;

	private DateTime _lastDataInlineRenderUtc;

	private DateTime? _nextInlineValueFadeUtc;

	private bool _forceDataCodeRtfRefresh;

	private bool _resetDataScrollOnNextRender;

	private bool _resetFunctionScrollOnNextRender;

	private bool _batchFunctionNavigationRedraw;

	private int _functionNavigationVersion;

	private string _activeProgramSearchKeyword = "";

	private int _activeProgramSearchLine;

	private string _programTreeLocateTargetFunction = "";

	private string _focusedVariableName = "";

	private string _lastProgramTreeHoverKey = "";

	private DateTime _lastFunctionWheelUtc;

	private DateTime _lastUiWheelUtc;

	private DateTime _suppressCodeInteractionSideEffectsUntilUtc;

	private int _lastScopeHighlightSelectionStart = -1;

	private float _functionCodeFontSize = 10.5f;
	private float _programTreeFontSize = 15f;

	private HashSet<string> _currentFunctionIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private SplitContainer _mainSplit;

	private SplitContainer? _rightSplit = null;

	private List<MapSymbol> _symbols = new List<MapSymbol>();

	private Dictionary<string, MapSymbol> _symbolLookup = new Dictionary<string, MapSymbol>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, MapSymbol> _symbolBaseLookup = new Dictionary<string, MapSymbol>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, MapSymbol> _symbolTailLookup = new Dictionary<string, MapSymbol>(StringComparer.OrdinalIgnoreCase);

	private string _workDirectory = "";

	private string _mapFilePath = "";

	private DateTime _mapLastWrite;

	private ICanAdapter? _adapter;

	private string _preferredAdapterName = "";

	private bool _offlineSimulation;

	private readonly object _simulationLock = new object();

	private readonly Dictionary<string, uint> _simulatedValues = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, OfflineCDriver> _offlineCDrivers = new Dictionary<string, OfflineCDriver>(StringComparer.OrdinalIgnoreCase);

	private List<FunctionSourceView> _offlineApplicationSources = new List<FunctionSourceView>();

	private string _offlineApplicationSourceDirectory = "";

	private OfflineProgramModel? _offlineProgramModel;

	private string _offlineRootSelectionText = "";

	private bool _offlineRootSelectionUpdating;

	private DateTime _lastOfflineApplicationWatchLogUtc = DateTime.MinValue;

	private DateTime _lastOfflineSimulationTickUtc = DateTime.MinValue;

	private DateTime _lastOfflineUncoveredLogUtc = DateTime.MinValue;

	private OfflineCWorkerClient? _offlineCWorker;

	private string _offlineCWorkerSignature = "";

	private readonly HashSet<string> _offlineCWorkerCoverageLogged = new HashSet<string>(StringComparer.Ordinal);

	private DateTime _lastOfflineCWorkerLogUtc = DateTime.MinValue;

	private string _lastOfflineCWorkerLogMessage = "";

	private int _offlineRuntimePaused;

	private int _offlineStepRequests;

	private CancellationTokenSource? _pollCts;

	private Task? _pollTask;

	private CancellationTokenSource? _downloadCts;

	private Task? _downloadTask;

	private byte _seq;

	private bool _running;

	private bool _profileLoaded;

	private bool _loadingProfile;

	private bool _monitorSessionOpen;

	private int _dragRowIndex = -1;

	private int _valueColumnIndex = -1;

	private int _forceColumnIndex = -1;

	private int _savedLeftPanelWidth;

	private int _savedMonitorPanelWidth;

	private List<int> _savedMonitorColumnWidths = new List<int>();

	private int _currentUiDpi = 96;

	private bool _layoutReadyForProfileSave;

	private DateTime _programGraphLastSourceWrite;

	private DateTime _programGraphLastCheckUtc;

	private int _programGraphSourceCount;

	private bool _programGraphCheckPending;

	private int _programGraphAnalysisVersion;

	private ProgramGraphSnapshot? _programGraphSnapshot;

	private string _pendingProgramGraphDirectory = "";

	private string _activeProgramGraphDirectory = "";

	private ushort _lastTraceId = ushort.MaxValue;

	private bool _showHexValue;

	private int _targetCycleMs = 50;

	private readonly Dictionary<ushort, string> _traceLabels = new Dictionary<ushort, string>();

	private bool _functionCodeDirty;

	private bool _dataCodeDirty;

	private bool _refreshMapBusy;

	private DateTime _lastManualRefreshUtc;

	private bool _suppressFunctionScopeHighlight;

	private readonly List<(int Start, int Length)> _functionScopeHighlights = new List<(int Start, int Length)>();

	private string _lastProgramInsightSignature = "";

	private DateTime _lastProgramInsightRenderUtc = DateTime.MinValue;

	private DateTime _lastMonitorSendErrorLogUtc;

	private DateTime _lastWatchSnapshotErrorLogUtc;

	private DateTime _lastPollLoopErrorLogUtc;

	private DateTime _lastScintillaSelectionLeakLogUtc = DateTime.MinValue;
	private DateTime _lastInlineValueStyleLogUtc = DateTime.MinValue;
	private DateTime _protectCodeViewportUntilUtc = DateTime.MinValue;

	private DateTime _lastPollPerfLogUtc = DateTime.MinValue;

	private DateTime _lastOfflinePerfLogUtc = DateTime.MinValue;

	private DateTime _nextTracePollUtc = DateTime.MinValue;

	private DateTime _traceBackoffUntilUtc = DateTime.MinValue;

	private int _traceMissCount;

	private int _monitorTxProbeRemaining;

	private int _lastSnapshotVisibleOnly;

	private int _consecutiveCanNoResponseCount;

	private int _noResponseStopRequested;

	private DateTime _lastNoResponseStopUtc = DateTime.MinValue;

	private int _controllerResponded;

	private readonly SemaphoreSlim _canRequestLock = new SemaphoreSlim(1, 1);

	private int _uiLogLineCount;

	private const int WmSetRedraw = 0x000B;

	private const int EmGetScrollPos = 0x04DD;

	private const int EmSetScrollPos = 0x04DE;

	private const int EmGetFirstVisibleLine = 0x00CE;

	private const int EmLineScroll = 0x00B6;

	private const int DataMirrorPaddingLines = 2;

	private const int MaxWatchItems = 100;

	private const int VisibleWatchTargetLimit = 40;

	private const int CurrentFunctionWatchTargetLimit = 80;

	private const int NoResponseDisplayMissCount = 12;

	private const int DefaultPollIntervalMs = 100;

	private const int ReadResponseTimeoutMs = 35;

	private const int VisiblePipelineMaxInFlight = 4;

	private const int VisiblePipelineRequestTimeoutMs = 22;

	private const int VisibleMirrorMinCycleMs = 100;

	private const int VisibleMirrorMaxAutoCycleMs = 200;

	private const int TracePollIntervalMs = 500;

	private const int TraceResponseTimeoutMs = 20;

	private const int TraceNoResponseBackoffMs = 1500;

	private const int PollPerfLogIntervalMs = 1200;

	private const int OfflinePerfLogIntervalMs = 10000;

	private const int CodeRenderRefreshMs = 80;

	private const int WatchValueFlushMs = 50;

	private const int DataInlineRefreshMs = 100;

	private const int CodeValueFreshHighlightMs = 3000;

	private static readonly bool CodeValueBlinkEnabled = false;

	private const int ManualRefreshThrottleMs = 250;

	private const int NoResponseStopThreshold = 20;

	private const int NoResponseStopCooldownMs = 5000;

	private const int NoResponsePollBackoffStartMisses = 5;

	private const int NoResponsePollBackoffBaseMs = 260;

	private const int NoResponsePollBackoffMaxMs = 1800;

	private bool IsWatchCapacityLimited()
	{
		return !_offlineSimulation;
	}

	private bool ShouldShowInlineCodeValues()
	{
		return _running;
	}

	private const int FunctionCodeWheelDeferMs = 320;

	private const int VisibleDataScrollDebounceMs = 120;

	private const int ProgramInsightRefreshMs = 360;

	private const int ScintillaIndicatorFocus = 20;

	private const int ScintillaIndicatorSearch = 21;

	private const int ScintillaIndicatorValueNormal = 22;

	private const int ScintillaIndicatorValueFresh = 23;

	private const int ScintillaIndicatorValueNormalText = 24;

	private const int ScintillaIndicatorValueFreshText = 25;

	private const int ScintillaIndicatorScopeBrace = 26;

	private const int ScintillaIndicatorFunctionHover = 27;

	private const int ScintillaIndicatorForceHold = 28;

	private const int ScintillaIndicatorForceHoldText = 29;

	private const int ScintillaIndicatorTrueCondition = 30;

	private const int ScintillaIndicatorValueNormalBorder = 31;

	private const int ScintillaIndicatorValueFreshBorder = 32;

	private const int ScintillaMarkerTrueLine = 8;

	private const int ScintillaMarkerSearchLine = 9;

	private const int ScintillaValueMarginIndex = 1;

	private const int ScintillaStyleValueFresh = 40;

	private const int ScintillaStyleValueStale = 41;

	private const int SciEolAnnotationSetText = 2740;

	private const int SciEolAnnotationSetStyle = 2742;

	private const int SciEolAnnotationClearAll = 2744;

	private const int SciEolAnnotationSetVisible = 2745;

	private const int SciEolAnnotationHidden = 0x0;

	private const int SciEolAnnotationStandard = 0x1;

	private const int FunctionCodeContextBeforeLines = 90;

	private const int FunctionCodeContextAfterLines = 180;

	private const int FunctionHoverSwitchMinIntervalMs = 120;

	private static int _memoryTrimPending;

	private static readonly Color FixedCodeValueTagBackColor = Color.Empty;

	private static readonly Color FixedCodeValueTagForeColor = Color.FromArgb(51, 51, 51);

	private Color _codeCommentColor = Color.FromArgb(74, 222, 128);

	private Color _codeFunctionColor = Color.FromArgb(96, 165, 250);

	private Color _codeKeywordColor = Color.FromArgb(244, 114, 182);

	private Color _codeValueColor = Color.FromArgb(11, 51, 132);

	private Color _codeValueFreshColor = Color.FromArgb(22, 163, 74);

	private Color _codeValueBackColor = Color.FromArgb(251, 191, 36);

	private Color _codeValueStaleBackColor = Color.FromArgb(92, 75, 32);

	private Color _codeValueTagActiveBackColor = FixedCodeValueTagBackColor;

	private Color _codeValueTagInactiveBackColor = FixedCodeValueTagBackColor;

	private Color _codeValueTagActiveForeColor = FixedCodeValueTagForeColor;

	private Color _codeValueTagInactiveForeColor = FixedCodeValueTagForeColor;

	private Color _codeValueTagBorderColor = Color.FromArgb(14, 116, 144);

	private static readonly Color ForceHoldBackColor = Color.FromArgb(245, 158, 11);

	private static readonly Color ForceHoldForeColor = Color.FromArgb(17, 24, 39);

	private Color _codeTrueLineBackColor = Color.FromArgb(34, 197, 94);

	private Color _codeFocusVariableForeColor = Color.FromArgb(2, 6, 23);

	private Color _codeFocusVariableBackColor = Color.FromArgb(74, 222, 128);

	private Color _programSearchLineBackColor = Color.FromArgb(64, 55, 26);

	private Color _programSearchMatchBackColor = Color.FromArgb(135, 88, 18);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(IntPtr hWnd);

	private const uint MonitorRequestId = 2032u;

	private const uint MonitorResponseId = 2033u;

	private const int UiLogMaxLines = 120;

	private const long DiagnosticLogMaxBytes = 512 * 1024;

	private const int DiagnosticLogKeepLines = 2000;

	private int Ui(int value)
	{
		return ScaleLayoutValue(value, 96, Math.Max(96, _currentUiDpi));
	}

	private float Ui(float value)
	{
		return value * Math.Max(96, _currentUiDpi) / 96f;
	}

	private static int ScaleLayoutValue(int value, int fromDpi, int toDpi)
	{
		if (value <= 0)
		{
			return 0;
		}

		int sourceDpi = Math.Clamp(fromDpi <= 0 ? 96 : fromDpi, 72, 384);
		int targetDpi = Math.Clamp(toDpi <= 0 ? 96 : toDpi, 72, 384);
		return Math.Max(1, (int)Math.Round((double)value * targetDpi / sourceDpi));
	}

	private int ScaleProfileLayoutValue(int value, int profileDpi)
	{
		return ScaleLayoutValue(value, profileDpi, Math.Max(96, _currentUiDpi));
	}

	private int DetectRuntimeDpi()
	{
		try
		{
			if (IsHandleCreated)
			{
				uint dpi = GetDpiForWindow(Handle);
				if (dpi >= 72 && dpi <= 384)
				{
					return (int)dpi;
				}
			}
		}
		catch
		{
		}

		try
		{
			using Graphics graphics = CreateGraphics();
			int dpi = (int)Math.Round(graphics.DpiX);
			if (dpi >= 72 && dpi <= 384)
			{
				return dpi;
			}
		}
		catch
		{
		}

		return 96;
	}

	private bool IsWindowZoomedSafe()
	{
		try
		{
			return IsHandleCreated && IsZoomed(Handle);
		}
		catch
		{
			return WindowState == FormWindowState.Maximized;
		}
	}

	private Size GetSafeMinimumSize()
	{
		Rectangle workingArea = Screen.FromControl(this)?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1180, 760);
		int width = Math.Min(Ui(1180), Math.Max(Ui(900), workingArea.Width));
		int height = Math.Min(Ui(760), Math.Max(Ui(640), workingArea.Height));
		return new Size(width, height);
	}

	public MainForm()
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		_currentUiDpi = DetectRuntimeDpi();
		Text = AppDisplayName;
		MinimumSize = GetSafeMinimumSize();
		base.StartPosition = FormStartPosition.CenterScreen;
		WindowState = FormWindowState.Maximized;
		BackColor = _bg;
		Font = new Font("Microsoft YaHei UI", 9f);
		BuildUi();
		_watchItems.ListChanged += delegate
		{
			UpdateForceHoldReminder();
		};
		UpdateFirmwareVersionDisplay();
		ApplyTheme(_themeName);
		UpdateFirmwareVersionDisplay();
		LoadDefaultProfile();
		_mapReloadTimer.Interval = 1000;
		_mapReloadTimer.Tick += delegate
		{
			CheckMapReload();
			CheckProgramGraphReload();
		};
		_mapReloadTimer.Start();
		_codeRefreshTimer.Interval = CodeRenderRefreshMs;
		_codeRefreshTimer.Tick += delegate
		{
			if (CodeValueBlinkEnabled &&
				!_dataCodeDirty &&
				_nextInlineValueFadeUtc.HasValue &&
				DateTime.Now >= _nextInlineValueFadeUtc.Value &&
				_functionCodePanel != null &&
				_functionCodePanel.Visible)
			{
				_nextInlineValueFadeUtc = null;
				RefreshScintillaVisibleRuntimeValues(force: true);
			}
			if ((_functionCodeDirty || _dataCodeDirty) && _functionCodePanel != null && _functionCodePanel.Visible)
			{
				bool mergedCodeView = ReferenceEquals(_functionCodeBox, _dataCodeBox);
				bool renderFunction = _functionCodeDirty || (mergedCodeView && _dataCodeDirty);
				bool renderData = _dataCodeDirty && !mergedCodeView;
				if (renderFunction && ShouldDeferFunctionCodeRefresh())
				{
					if (renderData)
					{
						_dataCodeDirty = false;
						RenderDataFunctionMirror(resetScroll: false);
					}
					return;
				}
				_functionCodeDirty = false;
				_dataCodeDirty = false;
				if (renderFunction)
				{
					RenderFunctionSource();
				}
				else if (renderData)
				{
					RenderDataFunctionMirror(resetScroll: false);
				}
			}
		};
		_codeRefreshTimer.Start();
		_valueRefreshTimer.Interval = WatchValueFlushMs;
		_valueRefreshTimer.Tick += delegate
		{
			FlushPendingWatchUpdates();
		};
		_valueRefreshTimer.Start();
		_visibleDataRefreshTimer.Interval = VisibleDataScrollDebounceMs;
		_visibleDataRefreshTimer.Tick += delegate
		{
			_visibleDataRefreshTimer.Stop();
			RefreshVisibleDataAfterScroll();
		};
		_programInsightRefreshTimer.Interval = ProgramInsightRefreshMs;
		_programInsightRefreshTimer.Tick += delegate
		{
			_programInsightRefreshTimer.Stop();
			RenderProgramInsightPanel(force: false);
		};
		_suggestionTimer.Interval = 160;
		_suggestionTimer.Tick += delegate
		{
			_suggestionTimer.Stop();
			RefreshSuggestions();
		};
		_programGraphDebounceTimer.Interval = 280;
		_programGraphDebounceTimer.Tick += delegate
		{
			_programGraphDebounceTimer.Stop();
			string directory = _pendingProgramGraphDirectory;
			if (directory.Length > 0)
			{
				StartProgramGraphAnalysis(directory);
			}
		};
		base.Shown += delegate
		{
			BeginInvoke((Action)(() =>
			{
				_currentUiDpi = DetectRuntimeDpi();
				MinimumSize = GetSafeMinimumSize();
				ApplySavedSplitterDistances();
				_layoutReadyForProfileSave = true;
				Log($"UI DPI：{_currentUiDpi}，最大化：{IsWindowZoomedSafe()}。");
				SaveDefaultProfileQuietly();
				RefreshStartupProgramGraph();
				ShowAnalysisPane(showFunctionAnalysis: true);
				_ = RunStartupFirmwareSyncAsync();
				_ = CheckForApplicationUpdateAsync();
			}));
		};
		Log("软件启动，内存 " + GetProcessMemoryText() + "。");
	}

	private async Task CheckForApplicationUpdateAsync()
	{
		bool updateStartedByUser = false;
		try
		{
			await Task.Delay(1600);
			AppUpdateService.UpdateCheckResult result = await _appUpdateService.CheckAsync(UpperComputerVersion, CancellationToken.None);
			if (!result.Configured)
			{
				Log("自动更新：" + result.Message);
				return;
			}

			if (!result.UpdateAvailable || result.Config == null || result.Manifest == null)
			{
				Log("自动更新：" + result.Message);
				return;
			}

			AppUpdateService.UpdateManifest manifest = result.Manifest;
			string releaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
				? ""
				: Environment.NewLine + Environment.NewLine + manifest.ReleaseNotes.Trim();
			bool unattendedInstall = result.Config.AutoInstall || manifest.Force;
			if (!unattendedInstall)
			{
				DialogResult choice = MessageBox.Show(
					$"服务器发现新版本 {manifest.Version}，当前版本 {UpperComputerVersion}。{releaseNotes}{Environment.NewLine}{Environment.NewLine}是否现在下载并更新？",
					"发现新版本",
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Information);
				if (choice != DialogResult.Yes)
				{
					Log("自动更新：用户暂不更新 " + manifest.Version + "。");
					return;
				}
			}

			Log("自动更新：开始下载 " + manifest.Version + "。");
			updateStartedByUser = true;
			if (_statusLabel != null)
			{
				_statusLabel.Text = "下载更新";
				_statusLabel.BackColor = _accent;
			}
			var progress = new Progress<string>(message => Log("自动更新：" + message));
			string packagePath = await _appUpdateService.DownloadPackageAsync(result.Config, manifest, progress, CancellationToken.None);

			if (!unattendedInstall)
			{
				DialogResult installChoice = MessageBox.Show(
					$"更新包已下载并校验完成。{Environment.NewLine}现在需要关闭软件并替换文件，然后自动重启。是否继续？",
					"安装更新",
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Question);
				if (installChoice != DialogResult.Yes)
				{
					Log("自动更新：更新包已下载，用户暂不安装：" + packagePath);
					return;
				}
			}

			Log("自动更新：启动外部更新器，准备退出主程序。");
			_appUpdateService.LaunchUpdater(packagePath);
			Close();
		}
		catch (Exception ex)
		{
			string message = "自动更新失败：" + ex.Message;
			Log(message);
			if (updateStartedByUser && IsHandleCreated && !IsDisposed)
			{
				BeginInvoke((Action)(() =>
				{
					MessageBox.Show(
						this,
						message + Environment.NewLine + Environment.NewLine + "请确认服务器根目录同时存在 update_manifest.json 和 can_monitor_latest.zip。",
						"自动更新失败",
						MessageBoxButtons.OK,
						MessageBoxIcon.Warning);
				}));
			}
		}
		finally
		{
			if (!IsDisposed && !Disposing && _statusLabel != null && _statusLabel.Text == "下载更新")
			{
				_statusLabel.Text = "未连接";
				_statusLabel.BackColor = _statusOff;
			}
		}
	}

	private async Task RunStartupFirmwareSyncAsync()
	{
		try
		{
			string directory = _workDirectory;
			if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
			{
				return;
			}
			await EnsureFirmwareSynchronizedByRefreshAsync(directory).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			Log("启动固件检查失败：" + ex.Message);
		}
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		_currentUiDpi = DetectRuntimeDpi();
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		CancelActiveDownload(waitForExit: true);
		SaveDefaultProfileQuietly();
		_mapReloadTimer.Stop();
		_mapReloadTimer.Dispose();
		_codeRefreshTimer.Stop();
		_codeRefreshTimer.Dispose();
		_valueRefreshTimer.Stop();
		_valueRefreshTimer.Dispose();
		_visibleDataRefreshTimer.Stop();
		_visibleDataRefreshTimer.Dispose();
		_suggestionTimer.Stop();
		_suggestionTimer.Dispose();
		_programInsightRefreshTimer.Stop();
		_programInsightRefreshTimer.Dispose();
		_programGraphDebounceTimer.Stop();
		_programGraphDebounceTimer.Dispose();
		_codeValueOverlayWindow?.HideOverlay();
		_codeValueOverlayWindow?.Dispose();
		_codeValueOverlayWindow = null;
		StopPolling(waitForExit: true);
		_adapter?.Close();
		_adapter?.Dispose();
		_adapter = null;
		_canRequestLock.Dispose();
		_monitorSessionOpen = false;
		base.OnFormClosing(e);
	}

	private void BuildUi()
	{
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			Padding = new Padding(Ui(10)),
			BackColor = _bg
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(56)));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(48)));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		base.Controls.Add(tableLayoutPanel);
		tableLayoutPanel.Controls.Add(BuildHeader(), 0, 0);
		tableLayoutPanel.Controls.Add(BuildToolbar(), 0, 1);
		tableLayoutPanel.Controls.Add(BuildMainArea(), 0, 2);
	}

	private Control BuildHeader()
	{
		Panel header = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _header,
			Padding = new Padding(Ui(18), Ui(8), Ui(18), Ui(8)),
			Tag = "header"
		};
		Label value = new Label
		{
			Text = AppDisplayName,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(Ui(18), Ui(4)),
			Tag = "title"
		};
		_appVersionLabel = new Label
		{
			Text = BuildUpperComputerVersionDisplay(),
			ForeColor = _accent,
			Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(Ui(150), Ui(10)),
			Tag = "appVersion"
		};
		_firmwareVersionLabel = new Label
		{
			Text = BuildFirmwareVersionDisplay(""),
			ForeColor = _muted,
			Font = new Font("Microsoft YaHei UI", 8f),
			AutoSize = true,
			Location = new Point(Ui(22), Ui(33)),
			Tag = "small"
		};
		_statusLabel = new Label
		{
			Text = "未连接",
			ForeColor = _ink,
			TextAlign = ContentAlignment.MiddleCenter,
			BackColor = _statusOff,
			Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
			Size = new Size(Ui(102), Ui(28)),
			Anchor = (AnchorStyles.Top | AnchorStyles.Right),
			Location = new Point(base.Width - Ui(132), Ui(13))
		};
		header.Resize += delegate
		{
			_statusLabel.Location = new Point(header.Width - Ui(124), Ui(13));
		};
		header.Controls.Add(value);
		header.Controls.Add(_appVersionLabel);
		header.Controls.Add(_firmwareVersionLabel);
		header.Controls.Add(_statusLabel);
		return header;
	}

	private static string BuildUpperComputerVersionDisplay()
	{
		return "上位机版本： " + UpperComputerVersion;
	}

	private static string GetBundledFirmwareVersionText()
	{
		return FirmwareInstaller.FormatVersion(BundledFirmwareAgent.ReadVersion());
	}

	private static string BuildFirmwareVersionDisplay(string workDirectory)
	{
		string bundledVersion = GetBundledFirmwareVersionText();
		string installedAgent = FindInstalledAgentFile(workDirectory);
		if (installedAgent.Length == 0)
		{
			return "长沙康旭电子科技有限公司    工程固件：未安装    刷新后自动同步";
		}

		string installedVersion = FirmwareInstaller.FormatVersion(FirmwareInstaller.ReadAgentVersion(installedAgent));
		if (!installedVersion.Equals(bundledVersion, StringComparison.OrdinalIgnoreCase))
		{
			return "长沙康旭电子科技有限公司    工程固件：" + installedVersion + "    刷新后自动同步";
		}

		if (!FirmwareInstaller.TryConfirmCurrentProjectBin(workDirectory, out _))
		{
			return "长沙康旭电子科技有限公司    工程固件：" + installedVersion + "    源码已同步 / bin未确认";
		}

		return "长沙康旭电子科技有限公司    工程固件：" + installedVersion + "    已同步";
	}

	private void UpdateFirmwareVersionDisplay()
	{
		string text = BuildFirmwareVersionDisplay(_workDirectory);
		if (_firmwareVersionLabel != null)
		{
			_firmwareVersionLabel.Text = text;
			_firmwareVersionLabel.ForeColor = IsWorkFirmwareVersionCurrent(_workDirectory)
				? _muted
				: Color.FromArgb(251, 191, 36);
		}
		if (_appVersionLabel != null)
		{
			_appVersionLabel.Text = BuildUpperComputerVersionDisplay();
			_appVersionLabel.ForeColor = _accent;
		}
		Text = AppDisplayName + " " + UpperComputerVersion;
		UpdateDownloadButtonState();
	}

	private void UpdateDownloadButtonState()
	{
		if (_downloadButton == null)
		{
			return;
		}

		bool enabled = IsWorkFirmwareVersionCurrent(_workDirectory);
		if (enabled)
		{
			_downloadButton.Text = "下载";
			_downloadButton.Enabled = true;
			ApplyButtonStyle(_downloadButton, "download");
		}
		else
		{
			_downloadButton.Text = "需刷新";
			_downloadButton.Enabled = false;
			ApplyButtonStyle(_downloadButton, "blocked");
		}
	}

	private static bool IsWorkFirmwareVersionCurrent(string workDirectory)
	{
		string installedAgent = FindInstalledAgentFile(workDirectory);
		if (installedAgent.Length == 0)
		{
			return false;
		}

		string bundledVersion = GetBundledFirmwareVersionText();
		string installedVersion = FirmwareInstaller.FormatVersion(FirmwareInstaller.ReadAgentVersion(installedAgent));
		return installedVersion.Equals(bundledVersion, StringComparison.OrdinalIgnoreCase) &&
			FirmwareInstaller.TryConfirmCurrentProjectBin(workDirectory, out _);
	}

	private static string ExtractWorkFirmwareVersionText(string workDirectory)
	{
		string installedAgent = FindInstalledAgentFile(workDirectory);
		if (installedAgent.Length == 0)
		{
			return "未安装";
		}
		return FirmwareInstaller.FormatVersion(FirmwareInstaller.ReadAgentVersion(installedAgent));
	}

	private static string FindInstalledAgentFile(string workDirectory)
	{
		if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
		{
			return "";
		}

		string direct = Path.Combine(workDirectory, "Src", "can_monitor_agent.c");
		if (File.Exists(direct))
		{
			return direct;
		}

		return Directory.EnumerateFiles(workDirectory, "can_monitor_agent.c", SearchOption.AllDirectories)
			.OrderBy(p => p.Length)
			.FirstOrDefault() ?? "";
	}

	private Control BuildToolbar()
	{
		Panel panel = CardPanel();
		panel.Margin = new Padding(0, 0, 0, Ui(4));
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 6,
			RowCount = 1,
			Padding = new Padding(Ui(10), Ui(5), Ui(10), Ui(5))
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(46)));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(220)));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(420)));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(150)));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(230)));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		_mapPathBox = new TextBox
		{
			Dock = DockStyle.Fill,
			PlaceholderText = "工作目录"
		};
		StyleTextBox(_mapPathBox);
		Button button = CommandButton("选择目录");
		MakeToolbarButton(button, "directory");
		button.Click += delegate
		{
			BrowseProjectDirectory();
		};
		_refreshButton = CommandButton("刷新");
		MakeToolbarButton(_refreshButton, "refresh");
		_refreshButton.Click += async delegate
		{
			await RefreshMapAsync();
		};
		_downloadButton = CommandButton("下载");
		MakeToolbarButton(_downloadButton, "download");
		_downloadButton.Click += async delegate
		{
			await DownloadFirmwareAsync(_downloadButton);
		};
		_connectButton = CommandButton("连接");
		MakeToolbarButton(_connectButton, "connect");
		_connectButton.Click += delegate
		{
			ToggleConnect();
		};
		_simulationButton = CommandButton("离线");
		MakeToolbarButton(_simulationButton, "simulate");
		_simulationButton.Click += delegate
		{
			ToggleOfflineSimulation();
		};
		_offlineRootBox = new ComboBox
		{
			Dock = DockStyle.None,
			DropDownStyle = ComboBoxStyle.DropDown,
			Width = Ui(210),
			Height = Ui(26),
			Margin = new Padding(Ui(2), Ui(2), 0, 0)
		};
		_offlineRootBox.Text = "自动入口";
		StyleComboBox(_offlineRootBox);
		_offlineRootBox.SelectedIndexChanged += delegate { CommitOfflineRootSelectionFromUi(); };
		_offlineRootBox.Validated += delegate { CommitOfflineRootSelectionFromUi(); };
		_offlineRootBox.KeyDown += delegate(object? sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Enter)
			{
				CommitOfflineRootSelectionFromUi();
				e.SuppressKeyPress = true;
			}
		};
		FlowLayoutPanel commandPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Padding = new Padding(0, Ui(4), 0, 0),
			Margin = Padding.Empty
		};
		button.Margin = new Padding(0, 0, Ui(8), 0);
		_refreshButton.Margin = new Padding(0, 0, Ui(8), 0);
		_downloadButton.Margin = new Padding(0, 0, Ui(8), 0);
		_connectButton.Margin = new Padding(0, 0, Ui(8), 0);
		_simulationButton.Margin = new Padding(0);
		commandPanel.Controls.Add(button);
		commandPanel.Controls.Add(_refreshButton);
		commandPanel.Controls.Add(_downloadButton);
		commandPanel.Controls.Add(_connectButton);
		commandPanel.Controls.Add(_simulationButton);
		_intervalBox = new NumericUpDown
		{
			Minimum = 10m,
			Maximum = 1000m,
			Value = DefaultPollIntervalMs,
			Dock = DockStyle.None,
			Width = Ui(78),
			Height = Ui(24),
			Margin = new Padding(Ui(2), Ui(2), 0, 0)
		};
		_intervalBox.ValueChanged += delegate
		{
			_targetCycleMs = (int)_intervalBox.Value;
			UpdateCycleEstimate();
			SaveDefaultProfileQuietly();
		};
		tableLayoutPanel.Controls.Add(SmallLabel("目录"), 0, 0);
		tableLayoutPanel.Controls.Add(_mapPathBox, 1, 0);
		tableLayoutPanel.Controls.Add(commandPanel, 2, 0);
		FlowLayoutPanel offlineRootPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Padding = new Padding(0, Ui(3), 0, 0),
			Margin = Padding.Empty
		};
		Label offlineRootLabel = SmallLabel("离线入口");
		offlineRootLabel.Width = Ui(70);
		offlineRootPanel.Controls.Add(offlineRootLabel);
		offlineRootPanel.Controls.Add(_offlineRootBox);
		tableLayoutPanel.Controls.Add(offlineRootPanel, 3, 0);
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Padding = new Padding(0, Ui(3), 0, 0),
			Margin = Padding.Empty
		};
		flowLayoutPanel.Controls.Add(SmallLabel("周期"));
		flowLayoutPanel.Controls.Add(_intervalBox);
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 4, 0);
		_themeBox = new ComboBox
		{
			Dock = DockStyle.None,
			DropDownStyle = ComboBoxStyle.DropDownList
		};
		_themeBox.Width = Ui(160);
		_themeBox.Height = Ui(26);
		_themeBox.Margin = new Padding(Ui(4), Ui(2), 0, 0);
		_themeBox.Items.AddRange(new object[]
		{
			"护眼暗绿",
			"工业黑金",
			"夜航蓝灰",
			"极简暗色",
			"设备控制台",
			"低蓝墨绿",
			"米黄灰工位",
			"Keil经典",
			"淡蓝工控",
			"琥珀终端",
			"钢铁青",
			"冷白实验室",
			"深蓝仪表盘",
			"高对比黑"
		});
		_themeBox.SelectedItem = _themeName;
		StyleComboBox(_themeBox);
		_themeBox.SelectedIndexChanged += delegate
		{
			if (_themeBox.SelectedItem is string themeName && !_themeName.Equals(themeName, StringComparison.Ordinal))
			{
				ApplyTheme(themeName);
				UpdateFirmwareVersionDisplay();
				SaveDefaultProfileQuietly();
			}
		};
		FlowLayoutPanel themePanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Padding = new Padding(0, Ui(3), 0, 0),
			Margin = Padding.Empty
		};
		Label themeLabel = SmallLabel("主题");
		themeLabel.Width = Ui(42);
		themePanel.Controls.Add(themeLabel);
		themePanel.Controls.Add(_themeBox);
		tableLayoutPanel.Controls.Add(themePanel, 5, 0);
		panel.Controls.Add(tableLayoutPanel);
		return panel;
	}

	private Control BuildMainArea()
	{
		_mainSplit = new SplitContainer
		{
			Dock = DockStyle.Fill,
			SplitterWidth = Ui(14),
			Panel1MinSize = 1,
			Panel2MinSize = 1,
			BackColor = GetSplitterColor("split-main"),
			Tag = "split-main"
		};
		_mainSplit.Paint += SplitContainerPaint;
		bool splitterReady = false;
		_mainSplit.SizeChanged += delegate
		{
			ApplyMainSplitMinimums();
			if (!splitterReady && _mainSplit.Width > Ui(1120))
			{
				int savedLeftPanelWidth = GetSavedLeftPanelWidth();
				_mainSplit.SplitterDistance = ((savedLeftPanelWidth > 0) ? ClampMainSplitter(savedLeftPanelWidth) : ClampMainSplitter(Ui(560)));
				splitterReady = true;
			}
		};
		_mainSplit.SplitterMoved += delegate
		{
			SaveDefaultProfileQuietly();
		};
		_mainSplit.Panel1.Controls.Add(BuildLeftPanel());
		_mainSplit.Panel2.Controls.Add(BuildGridPanel());
		return _mainSplit;
	}

	private void ApplyMainSplitMinimums()
	{
		if (_mainSplit == null || _mainSplit.Width <= Ui(640))
		{
			return;
		}

		int leftMin = Ui(360);
		int rightMin = Ui(760);
		int total = Math.Max(1, _mainSplit.Width - _mainSplit.SplitterWidth);
		if (total < leftMin + rightMin)
		{
			rightMin = Math.Max(Ui(420), total - leftMin);
			if (leftMin + rightMin > total)
			{
				leftMin = Math.Max(Ui(260), total - rightMin);
			}
		}
		if (leftMin <= 0 || rightMin <= 0 || leftMin + rightMin > total)
		{
			return;
		}

		try
		{
			int maxDistance = Math.Max(leftMin, _mainSplit.Width - rightMin);
			_mainSplit.SplitterDistance = Math.Clamp(_mainSplit.SplitterDistance, leftMin, maxDistance);
			_mainSplit.Panel1MinSize = leftMin;
			_mainSplit.Panel2MinSize = rightMin;
		}
		catch (InvalidOperationException)
		{
			_mainSplit.Panel1MinSize = 1;
			_mainSplit.Panel2MinSize = 1;
		}
	}

	private int GetSavedLeftPanelWidth()
	{
		if (_savedLeftPanelWidth <= 0)
		{
			return 0;
		}
		return _savedLeftPanelWidth;
	}

	private int GetSavedMonitorPanelWidth()
	{
		if (_savedMonitorPanelWidth <= 0)
		{
			return 0;
		}
		return _savedMonitorPanelWidth;
	}

	private int ClampMainSplitter(int value)
	{
		if (_mainSplit.Width <= 0)
		{
			return value;
		}
		int min = Math.Max(_mainSplit.Panel1MinSize, Ui(360));
		int maxByRight = _mainSplit.Width - Math.Max(_mainSplit.Panel2MinSize, Ui(760));
		int max = Math.Max(min, Math.Min(Ui(720), maxByRight));
		return Math.Clamp(value, min, max);
	}

	private int ClampRightSplitter(int value)
	{
		if (_rightSplit == null || _rightSplit.Width <= 0)
		{
			return value;
		}
		int num = Math.Max(_rightSplit.Panel1MinSize, Ui(520));
		int max = Math.Max(num, _rightSplit.Width - Math.Max(_rightSplit.Panel2MinSize, Ui(300)));
		return Math.Clamp(value, num, max);
	}

	private void ApplySavedSplitterDistances()
	{
		if (_mainSplit != null && _savedLeftPanelWidth > 0 && _mainSplit.Width > 0)
		{
			_mainSplit.SplitterDistance = ClampMainSplitter(_savedLeftPanelWidth);
		}
		if (_rightSplit != null && _savedMonitorPanelWidth > 0 && _rightSplit.Width > 0)
		{
			_rightSplit.SplitterDistance = ClampRightSplitter(_savedMonitorPanelWidth);
		}
	}

	private List<int> GetMonitorColumnWidths()
	{
		if (_grid == null)
		{
			return _savedMonitorColumnWidths.ToList();
		}
		return _grid.Columns.Cast<DataGridViewColumn>()
			.Select(column => Math.Clamp(column.Width, Ui(36), Ui(800)))
			.ToList();
	}

	private void ApplySavedMonitorColumnWidths()
	{
		if (_grid == null || _savedMonitorColumnWidths.Count == 0)
		{
			return;
		}
		int count = Math.Min(_grid.Columns.Count, _savedMonitorColumnWidths.Count);
		for (int i = 0; i < count; i++)
		{
			int width = Math.Clamp(_savedMonitorColumnWidths[i], Ui(36), Ui(800));
			_grid.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
			_grid.Columns[i].Width = width;
		}
	}

	private bool CanSaveLayoutToProfile()
	{
		if (!_layoutReadyForProfileSave || _loadingProfile || WindowState == FormWindowState.Minimized)
		{
			return false;
		}
		if (_mainSplit == null || _grid == null)
		{
			return false;
		}
		if (_mainSplit.Width < Ui(1120))
		{
			return false;
		}

		int leftWidth = _mainSplit.SplitterDistance;
		int codeWidth = _mainSplit.Width - leftWidth - _mainSplit.SplitterWidth;
		return leftWidth >= Ui(360) &&
			codeWidth >= Ui(760);
	}

	private int GetProfileDpi(MonitorProfile? profile)
	{
		return Math.Clamp(profile?.UiDpi > 0 ? profile.UiDpi : Math.Max(96, _currentUiDpi), 72, 384);
	}

	private Control BuildLeftPanel()
	{
		Panel panel = CardPanel();
		_variableBox = new TextBox { Visible = false };
		_suggestions = new ListBox { Visible = false };
		panel.Controls.Add(BuildProgramInsightPanel());
		panel.Controls.Add(_variableBox);
		panel.Controls.Add(_suggestions);
		return panel;
	}

	private Label BuildLeftBadge(string text)
	{
		return new Label
		{
			Dock = DockStyle.Fill,
			Text = text,
			TextAlign = ContentAlignment.MiddleCenter,
			ForeColor = _ink,
			BackColor = _surfaceAlt,
			Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
			Margin = new Padding(0, 0, 0, Ui(5)),
			Tag = "surfaceAlt"
		};
	}

	private void ResizeProgramInsightColumns()
	{
		if (_programInsightList == null || _programInsightList.Columns.Count < 2)
		{
			return;
		}

		int firstWidth = Math.Clamp(_programInsightList.ClientSize.Width / 5, Ui(46), Ui(64));
		_programInsightList.Columns[0].Width = firstWidth;
		_programInsightList.Columns[1].Width = Math.Max(Ui(120), _programInsightList.ClientSize.Width - firstWidth - Ui(6));
	}

	private void UpdateProgramInsightPanel(bool force = false)
	{
		if (force)
		{
			_programInsightRefreshTimer.Stop();
			RenderProgramInsightPanel(force: true);
			return;
		}

		if ((DateTime.UtcNow - _lastProgramInsightRenderUtc).TotalMilliseconds < ProgramInsightRefreshMs)
		{
			ScheduleProgramInsightRefresh();
			return;
		}

		RenderProgramInsightPanel(force: false);
	}

	private void ScheduleProgramInsightRefresh()
	{
		if (_programInsightList == null)
		{
			return;
		}

		_programInsightRefreshTimer.Stop();
		_programInsightRefreshTimer.Start();
	}

	private void RenderProgramInsightPanel(bool force = false)
	{
		if (_programInsightList == null)
		{
			return;
		}

		_lastProgramInsightRenderUtc = DateTime.UtcNow;
		List<ListViewItem> items = BuildProgramInsightItems();
		var signature = new StringBuilder();
		foreach (ListViewItem item in items)
		{
			signature.Append(item.Text).Append('|').Append(item.SubItems.Count > 1 ? item.SubItems[1].Text : "").Append('|').Append(item.Tag).Append('\n');
		}
		string signatureText = signature.ToString();
		if (!force && signatureText.Equals(_lastProgramInsightSignature, StringComparison.Ordinal))
		{
			return;
		}

		_lastProgramInsightSignature = signatureText;
		_programInsightList.BeginUpdate();
		try
		{
			_programInsightList.Items.Clear();
			_programInsightList.Items.AddRange(items.ToArray());
			ResizeProgramInsightColumns();
		}
		finally
		{
			_programInsightList.EndUpdate();
		}
	}

	private List<ListViewItem> BuildProgramInsightItems()
	{
		var items = new List<ListViewItem>();
		if (_currentFunctionSource != null)
		{
			BuildCurrentFunctionInsightItems(items);
		}
		else
		{
			BuildProgramHomeInsightItems(items);
		}

		if (items.Count == 0)
		{
			AddProgramInsightItem(items, "状态", "等待工程分析", "", null, _muted, _surface);
		}
		return items;
	}

	private void BuildProgramHomeInsightItems(List<ListViewItem> items)
	{
		AddFocusedVariableInsightItem(items);
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null || !snapshot.Success)
		{
			AddProgramInsightItem(items, "目录", string.IsNullOrWhiteSpace(_workDirectory) ? "未选择工作目录" : "正在读取代码", "", null, _muted, _surface);
			return;
		}

		string entry = string.IsNullOrWhiteSpace(snapshot.StartFunction) ? "自动识别" : snapshot.StartFunction;
		ProgramInsightAction? entryAction = string.IsNullOrWhiteSpace(snapshot.StartFunction) ? null : new ProgramInsightAction("function", entry);
		AddProgramInsightItem(items, "入口", entry, "", entryAction, _accent, _surfaceAlt);
		AddProgramInsightItem(items, "规模", $"{snapshot.CallGraphNodes.Count} 函数 / {snapshot.CallGraphEdges.Count} 调用", $"{snapshot.SourceFileCount} 源文件", null, _muted, _surface);

		foreach (ProgramCallGraphNode node in snapshot.CallGraphNodes
			.Where(n => !IsProgramGraphNoiseNode(n))
			.OrderByDescending(GetBusinessNodeScore)
			.ThenByDescending(n => n.Outgoing + n.Incoming)
			.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
			.Take(18))
		{
			string detail = TrimInsightText(string.IsNullOrWhiteSpace(node.Summary) ? BuildNodeRelationText(node) : node.Summary, 42);
			AddProgramInsightItem(items, ClassifyFunctionKind(node.Name, node.Kind), node.Name, detail, new ProgramInsightAction("function", node.Name), GetFunctionKindColor(node.Name, node.Kind), _surface);
		}
	}

	private void BuildCurrentFunctionInsightItems(List<ListViewItem> items)
	{
		AddFocusedVariableInsightItem(items);
		FunctionSourceView? activeSource = _currentFunctionSource;
		if (activeSource == null)
		{
			return;
		}

		FunctionSourceView source = activeSource;
		ProgramCallGraphNode? currentNode = FindGraphNodeForFunction(source.FunctionName, source.FilePath);
		string currentDetail = currentNode != null && !string.IsNullOrWhiteSpace(currentNode.Summary)
			? currentNode.Summary
			: $"第 {source.StartLine} 行";
		AddProgramInsightItem(items, "当前", source.FunctionName, TrimInsightText(currentDetail, 46), new ProgramInsightAction("function", source.FunctionName), _accent, _surfaceAlt);

		EmbeddedCodeAnalysis semantic = EmbeddedCodeKnowledge.Analyze(
			source.FunctionName,
			source.FilePath,
			string.Join(Environment.NewLine, source.Lines));
		foreach (EmbeddedCodeInsight insight in semantic.Insights.Take(8))
		{
			ProgramInsightAction? action = insight.Kind.Equals("信号", StringComparison.OrdinalIgnoreCase)
				? new ProgramInsightAction("search", insight.Name)
				: null;
			Color foreColor = insight.Kind.Equals("信号", StringComparison.OrdinalIgnoreCase) ? _codeFunctionColor : _ink;
			AddProgramInsightItem(items, insight.Kind, insight.Name, TrimInsightText(insight.Detail, 44), action, foreColor, _surface);
		}

		if (currentNode != null)
		{
			foreach (ProgramCallGraphNode caller in GetGraphLinkedNodes(currentNode.Id, callers: true).Take(5))
			{
				AddProgramInsightItem(items, "上游", caller.Name, TrimInsightText(caller.Summary, 42), new ProgramInsightAction("function", caller.Name), GetFunctionKindColor(caller.Name, caller.Kind), _surface);
			}
			foreach (ProgramCallGraphNode callee in GetGraphLinkedNodes(currentNode.Id, callers: false).Take(10))
			{
				AddProgramInsightItem(items, "下游", callee.Name, TrimInsightText(callee.Summary, 42), new ProgramInsightAction("function", callee.Name), GetFunctionKindColor(callee.Name, callee.Kind), _surface);
			}
		}

		List<WatchItem> visibleWatchItems = GetVisibleWatchItems();
		foreach (WatchItem item in visibleWatchItems.Take(14))
		{
			string name = GetWatchDisplayName(item);
			AddProgramInsightItem(items, "可见值", name, FormatInlineWatchValue(item), new ProgramInsightAction("search", name), _codeValueColor, _codeValueBackColor);
		}

		foreach (WatchItem item in GetCurrentFunctionWatchItems()
			.Where(item => !visibleWatchItems.Any(visible => visible.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
			.Take(8))
		{
			string name = GetWatchDisplayName(item);
			AddProgramInsightItem(items, "本函数", name, FormatInlineWatchValue(item), new ProgramInsightAction("search", name), _codeValueColor, _codeValueBackColor);
		}

		foreach (string identifier in BuildBusinessSignals().Take(12))
		{
			AddProgramInsightItem(items, "信号", identifier, ResolveInsightSymbolState(identifier), new ProgramInsightAction("search", identifier), _ink, _surface);
		}
	}

	private void AddFocusedVariableInsightItem(List<ListViewItem> items)
	{
		if (string.IsNullOrWhiteSpace(_focusedVariableName))
		{
			return;
		}

		string detail = GetFocusedVariableDetail(_focusedVariableName);
		AddProgramInsightItem(items, "锁定", _focusedVariableName, detail, new ProgramInsightAction("search", _focusedVariableName), _codeFocusVariableForeColor, _codeFocusVariableBackColor);
	}

	private string GetFocusedVariableDetail(string variableName)
	{
		WatchItem? item = FindWatchItemByIdentifier(variableName);
		if (item != null)
		{
			return FormatInlineWatchValue(item);
		}
		if (_symbolLookup.ContainsKey(variableName) || _symbolBaseLookup.ContainsKey(variableName) || _symbolTailLookup.ContainsKey(variableName))
		{
			return "可监控";
		}
		return "代码定位";
	}

	private WatchItem? FindWatchItemByIdentifier(string identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			return null;
		}

		return _watchItems.FirstOrDefault(item =>
			item.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
			GetWatchDisplayName(item).Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierBase(item.Name).Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierTail(item.Name).Equals(identifier, StringComparison.OrdinalIgnoreCase));
	}

	private bool WatchMatchesFocusedVariable(WatchItem item)
	{
		if (string.IsNullOrWhiteSpace(_focusedVariableName))
		{
			return false;
		}

		return item.Name.Equals(_focusedVariableName, StringComparison.OrdinalIgnoreCase) ||
			GetWatchDisplayName(item).Equals(_focusedVariableName, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierBase(item.Name).Equals(_focusedVariableName, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierTail(item.Name).Equals(_focusedVariableName, StringComparison.OrdinalIgnoreCase);
	}

	private void AddProgramInsightItem(List<ListViewItem> items, string kind, string name, string detail, ProgramInsightAction? action, Color foreColor, Color backColor)
	{
		string text = string.IsNullOrWhiteSpace(detail) ? name : name + "    " + detail;
		var item = new ListViewItem(kind)
		{
			BackColor = backColor,
			ForeColor = foreColor,
			Tag = action,
			UseItemStyleForSubItems = false,
			ToolTipText = text
		};
		item.SubItems.Add(text);
		item.SubItems[1].ForeColor = foreColor;
		item.SubItems[1].BackColor = backColor;
		items.Add(item);
	}

	private void ProgramInsightListDoubleClick(object? sender, EventArgs e)
	{
		if (_programInsightList?.SelectedItems.Count != 1 || _programInsightList.SelectedItems[0].Tag is not ProgramInsightAction action)
		{
			return;
		}

		if (action.Kind.Equals("search", StringComparison.OrdinalIgnoreCase))
		{
			FillProgramSearchFromCode(action.Value);
			return;
		}

		if (action.Kind.Equals("function", StringComparison.OrdinalIgnoreCase) &&
			!string.IsNullOrWhiteSpace(_workDirectory) &&
			Directory.Exists(_workDirectory) &&
			TryLoadFunctionSource(_workDirectory, action.Value, out FunctionSourceView? sourceView) &&
			sourceView != null)
		{
			ShowFunctionSource(sourceView, pushCurrent: _currentFunctionSource != null, clearForward: true);
		}
	}

	private ProgramCallGraphNode? FindGraphNodeForFunction(string functionName, string filePath)
	{
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null)
		{
			return null;
		}

		IReadOnlyList<ProgramCallGraphNode> nodes = GetAllGraphNodes(snapshot);
		if (nodes.Count == 0)
		{
			return null;
		}

		string relativeFilePath = GetRelativePathSafe(_workDirectory, filePath);
		ProgramCallGraphNode? sameFile = nodes.FirstOrDefault(n =>
			n.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase) &&
			(n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) ||
			 n.FilePath.Equals(relativeFilePath, StringComparison.OrdinalIgnoreCase)));
		return sameFile ?? nodes.FirstOrDefault(n => n.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));
	}

	private IEnumerable<ProgramCallGraphNode> GetGraphLinkedNodes(string nodeId, bool callers)
	{
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null)
		{
			yield break;
		}

		Dictionary<string, ProgramCallGraphNode> nodes = GetAllGraphNodes(snapshot).ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
		IEnumerable<ProgramCallGraphEdge> edges = callers
			? GetAllGraphEdges(snapshot).Where(e => e.ToId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
			: GetAllGraphEdges(snapshot).Where(e => e.FromId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
		foreach (ProgramCallGraphEdge edge in edges)
		{
			string linkedId = callers ? edge.FromId : edge.ToId;
			if (nodes.TryGetValue(linkedId, out ProgramCallGraphNode? node))
			{
				yield return node;
			}
		}
	}

	private static IReadOnlyList<ProgramCallGraphNode> GetAllGraphNodes(ProgramGraphSnapshot snapshot)
	{
		return snapshot.AllCallGraphNodes.Count > 0 ? snapshot.AllCallGraphNodes : snapshot.CallGraphNodes;
	}

	private static IReadOnlyList<ProgramCallGraphEdge> GetAllGraphEdges(ProgramGraphSnapshot snapshot)
	{
		return snapshot.AllCallGraphEdges.Count > 0 ? snapshot.AllCallGraphEdges : snapshot.CallGraphEdges;
	}

	private List<string> BuildBusinessSignals()
	{
		IEnumerable<string> visibleIdentifiers = BuildIdentifierList(GetVisibleRawLines(DataMirrorPaddingLines));
		return visibleIdentifiers
			.Concat(_currentFunctionIdentifiers)
			.Where(IsBusinessIdentifier)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(name => _symbolLookup.ContainsKey(name) || _symbolBaseLookup.ContainsKey(name) || _symbolTailLookup.ContainsKey(name))
			.ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private string ResolveInsightSymbolState(string identifier)
	{
		if (_watchItems.Any(item => GetWatchDisplayName(item).Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierBase(item.Name).Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierTail(item.Name).Equals(identifier, StringComparison.OrdinalIgnoreCase)))
		{
			return "已监控";
		}
		return _symbolLookup.ContainsKey(identifier) || _symbolBaseLookup.ContainsKey(identifier) || _symbolTailLookup.ContainsKey(identifier) ? "可监控" : "";
	}

	private static int GetBusinessNodeScore(ProgramCallGraphNode node)
	{
		return GetBusinessNameScore(node.Name) + GetBusinessNameScore(node.Summary) + (node.Outgoing > 0 ? 2 : 0);
	}

	private static bool IsProgramGraphNoiseNode(ProgramCallGraphNode node)
	{
		string name = node.Name;
		string combined = (node.FilePath + "/" + name + "/" + node.Kind).Replace('\\', '/');
		if (name.Equals("Task_sys_tick", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("smt_sys_tick", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("sys_tick", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("SysTick", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (combined.Contains("/Task.c/", StringComparison.OrdinalIgnoreCase) &&
			(name.Contains("task", StringComparison.OrdinalIgnoreCase) ||
				name.Contains("tick", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		return name.StartsWith("CanMonitor_", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("CANMonitor_", StringComparison.OrdinalIgnoreCase);
	}

	private static int GetBusinessNameScore(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return 0;
		}

		string upper = text.ToUpperInvariant();
		int score = 0;
		string[] strongWords = { "MYLOGIC", "LOGIC", "APP_", "LCD", "DISP", "CAN", "CTRL", "DFS", "MOTOR", "VALVE", "PRESS", "PWM", "DO", "DI" };
		foreach (string word in strongWords)
		{
			if (upper.Contains(word, StringComparison.Ordinal))
			{
				score += 3;
			}
		}
		return score;
	}

	private static string ClassifyFunctionKind(string name, string graphKind = "")
	{
		if (!string.IsNullOrWhiteSpace(graphKind))
		{
			return graphKind switch
			{
				"main" => "入口",
				"disp" => "显示",
				"period10" => "周期",
				"timer" => "周期",
				"can" => "CAN",
				"io" => "IO",
				"storage" => "参数",
				"driver" => "底层",
				"business" => "业务",
				_ => "函数"
			};
		}

		string upper = name.ToUpperInvariant();
		if (upper.Contains("CAN", StringComparison.Ordinal))
		{
			return "CAN";
		}
		if (upper.Contains("LCD", StringComparison.Ordinal) || upper.Contains("DISP", StringComparison.Ordinal))
		{
			return "显示";
		}
		if (upper.Contains("LOGIC", StringComparison.Ordinal) || upper.Contains("CTRL", StringComparison.Ordinal) || upper.Contains("APP", StringComparison.Ordinal))
		{
			return "业务";
		}
		return "函数";
	}

	private Color GetFunctionKindColor(string name, string graphKind = "")
	{
		string kind = ClassifyFunctionKind(name, graphKind);
		if (IsCurrentLightTheme())
		{
			return kind switch
			{
				"CAN" => Color.FromArgb(36, 119, 145),
				"显示" => Color.FromArgb(156, 117, 24),
				"业务" => _codeFunctionColor,
				"入口" => _accent,
				"周期" => Color.FromArgb(156, 117, 24),
				"IO" => Color.FromArgb(176, 99, 35),
				"参数" => Color.FromArgb(119, 86, 166),
				"底层" => _muted,
				_ => _ink
			};
		}

		return kind switch
		{
			"CAN" => Color.FromArgb(45, 212, 191),
			"显示" => Color.FromArgb(250, 204, 21),
			"业务" => _codeFunctionColor,
			"入口" => _accent,
			"周期" => Color.FromArgb(250, 204, 21),
			"IO" => Color.FromArgb(251, 146, 60),
			"参数" => Color.FromArgb(192, 132, 252),
			"底层" => _muted,
			_ => _ink
		};
	}

	private static bool IsBusinessIdentifier(string name)
	{
		if (name.Length < 3)
		{
			return false;
		}

		string upper = name.ToUpperInvariant();
		string[] words = { "LCD", "CAN", "DI", "DO", "PWM", "MOTOR", "VALVE", "PRESS", "DLY", "FLG", "SET", "STA", "ERR", "TIME", "COUNT", "TEMP", "MAIN" };
		return words.Any(word => upper.Contains(word, StringComparison.Ordinal));
	}

	private static string BuildNodeRelationText(ProgramCallGraphNode node)
	{
		return $"入 {node.Incoming} / 出 {node.Outgoing}";
	}

	private static string TrimInsightText(string text, int maxLength)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}

		string compact = Regex.Replace(text.Trim(), @"\s+", " ");
		return compact.Length <= maxLength ? compact : compact.Substring(0, Math.Max(0, maxLength - 1)) + "...";
	}

	private Control BuildGridPanel()
	{
		Panel panel = CardPanel();
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 5,
			Padding = new Padding(Ui(10))
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(30)));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(40)));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(40)));
		_programSearchResultsRow = new RowStyle(SizeType.Absolute, 0f);
		tableLayoutPanel.RowStyles.Add(_programSearchResultsRow);
		TableLayoutPanel headerLayout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 1,
			Margin = Padding.Empty
		};
		headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(220)));
		headerLayout.Controls.Add(SectionLabel("2 看数值"), 0, 0);
		_forceHoldLabel = SmallLabel("无保持");
		_forceHoldLabel.Dock = DockStyle.Fill;
		_forceHoldLabel.Width = Ui(220);
		_forceHoldLabel.AutoEllipsis = true;
		_forceHoldLabel.Margin = Padding.Empty;
		_forceHoldLabel.TextAlign = ContentAlignment.MiddleCenter;
		_forceHoldLabel.Visible = false;
		headerLayout.Controls.Add(_forceHoldLabel, 1, 0);
		tableLayoutPanel.Controls.Add(headerLayout, 0, 0);
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = true
		};
		_startButton = CommandButton("开始监控");
		_startButton.Size = new Size(Ui(92), Ui(30));
		ApplyButtonStyle(_startButton, "monitorStart");
		_startButton.Click += delegate
		{
			TogglePolling();
		};
		_startButton.Margin = new Padding(0, Ui(2), Ui(6), 0);
		flowLayoutPanel.Controls.Add(_startButton);
		_valueFormatButton = PlainButton("10进制");
		_valueFormatButton.Size = new Size(Ui(64), Ui(28));
		_valueFormatButton.Margin = new Padding(0, Ui(3), Ui(6), 0);
		_valueFormatButton.Click += delegate
		{
			ToggleValueDisplayMode();
		};
		flowLayoutPanel.Controls.Add(_valueFormatButton);
		UpdateValueFormatButton();
		_runtimeRunButton = PlainButton("循环");
		_runtimeRunButton.Size = new Size(Ui(64), Ui(28));
		_runtimeRunButton.Margin = new Padding(0, Ui(3), Ui(6), 0);
		ApplyButtonStyle(_runtimeRunButton, "working");
		_runtimeRunButton.Click += async delegate
		{
			await SendRuntimeControlAsync(MonitorProtocol.RuntimeRun, "循环");
		};
		flowLayoutPanel.Controls.Add(_runtimeRunButton);
		_runtimeStepButton = PlainButton("单步");
		_runtimeStepButton.Size = new Size(Ui(64), Ui(28));
		_runtimeStepButton.Margin = new Padding(0, Ui(3), Ui(6), 0);
		ApplyButtonStyle(_runtimeStepButton, "working");
		_runtimeStepButton.Click += async delegate
		{
			await SendRuntimeControlAsync(MonitorProtocol.RuntimeStep, "单步");
		};
		flowLayoutPanel.Controls.Add(_runtimeStepButton);
		_cycleEstimateLabel = SmallLabel("无变量");
		_cycleEstimateLabel.Width = Ui(110);
		_cycleEstimateLabel.Margin = new Padding(0, Ui(6), 0, 0);
		flowLayoutPanel.Controls.Add(_cycleEstimateLabel);
		UpdateForceHoldReminder();
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 1);
		_source.DataSource = _watchItems;
		_grid = new DataGridView
		{
			Dock = DockStyle.Fill,
			AutoGenerateColumns = false,
			AllowUserToAddRows = false,
			AllowUserToResizeRows = false,
			SelectionMode = DataGridViewSelectionMode.FullRowSelect,
			MultiSelect = true,
			AllowDrop = true,
			BackgroundColor = _panel,
			BorderStyle = BorderStyle.None,
			RowHeadersVisible = false,
			DataSource = _source,
			EnableHeadersVisualStyles = false,
			ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
			Tag = "grid"
		};
		_grid.ColumnHeadersDefaultCellStyle.BackColor = _gridHeader;
		_grid.ColumnHeadersDefaultCellStyle.ForeColor = _ink;
		_grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
		_grid.DefaultCellStyle.BackColor = _surface;
		_grid.DefaultCellStyle.ForeColor = _ink;
		_grid.DefaultCellStyle.SelectionBackColor = _accent;
		_grid.DefaultCellStyle.SelectionForeColor = _ink;
		_grid.AlternatingRowsDefaultCellStyle.BackColor = _surfaceAlt;
		_grid.GridColor = _gridHeader;
		EnableDoubleBuffer(_grid);
		_grid.Columns.Add(new DataGridViewCheckBoxColumn
		{
			HeaderText = "监控",
			DataPropertyName = "Enabled",
			Width = Ui(48)
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			HeaderText = "变量",
			DataPropertyName = "Name",
			Width = Ui(260)
		});
		DataGridViewTextBoxColumn sizeColumn = new DataGridViewTextBoxColumn
		{
			HeaderText = "字节",
			DataPropertyName = "Size",
			Width = Ui(52),
			Visible = false
		};
		_grid.Columns.Add(sizeColumn);
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			HeaderText = "值(10)",
			DataPropertyName = "DisplayValue",
			Width = Ui(120)
		});
		_valueColumnIndex = _grid.Columns.Count - 1;
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			HeaderText = "强制",
			DataPropertyName = "ForceText",
			Width = Ui(62)
		});
		_forceColumnIndex = _grid.Columns.Count - 1;
		DataGridViewColumnCollection columns = _grid.Columns;
		if (_savedMonitorColumnWidths.Count == 0)
		{
			columns[_valueColumnIndex].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
		}
		_grid.Columns.Add(new DataGridViewButtonColumn
		{
			HeaderText = "",
			Text = "×",
			UseColumnTextForButtonValue = true,
			Width = Ui(34)
		});
		ApplySavedMonitorColumnWidths();
		_grid.ColumnWidthChanged += delegate
		{
			SaveDefaultProfileQuietly();
		};
		_grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
		_grid.CellContentClick += GridCellContentClick;
		_grid.SelectionChanged += GridSelectionChanged;
		_grid.KeyDown += GridKeyDown;
		_grid.CurrentCellDirtyStateChanged += delegate
		{
			if (_grid.IsCurrentCellDirty)
			{
				_grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_grid.CellValueChanged += delegate(object? _, DataGridViewCellEventArgs e)
		{
			if (e.RowIndex >= 0)
			{
				UpdateCycleEstimate();
				SaveDefaultProfileQuietly();
			}
		};
		_grid.CellDoubleClick += delegate(object? _, DataGridViewCellEventArgs e)
		{
			if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is WatchItem watchItem)
			{
				ToggleExpandRows(new WatchItem[1] { watchItem });
				UpdateCycleEstimate();
				SaveDefaultProfileQuietly();
			}
		};
		_grid.MouseDown += GridMouseDown;
		_grid.MouseUp += GridMouseUp;
		_grid.ContextMenuStrip = CreateGridWatchContextMenu();
		_grid.MouseMove += GridMouseMove;
		_grid.DragOver += GridDragOver;
		_grid.DragDrop += GridDragDrop;
		_grid.Visible = false;
		tableLayoutPanel.Controls.Add(BuildDataMirrorPanel(), 0, 2);
		tableLayoutPanel.Controls.Add(BuildProgramSearchArea(), 0, 3);
		_programSearchResults = new ListBox
		{
			Dock = DockStyle.Fill,
			Visible = false,
			IntegralHeight = false,
			BackColor = _surfaceAlt,
			ForeColor = _ink,
			BorderStyle = BorderStyle.FixedSingle,
			Font = new Font("Microsoft YaHei UI", 8.5f)
		};
		_programSearchResults.DoubleClick += ProgramSearchResultsDoubleClick;
		tableLayoutPanel.Controls.Add(_programSearchResults, 0, 4);
		panel.Controls.Add(tableLayoutPanel);
		UpdateCycleEstimate();
		return panel;
	}

	private Control BuildProgramSearchArea()
	{
		TableLayoutPanel searchLayout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			RowCount = 1,
			Margin = new Padding(0),
			Padding = new Padding(Ui(1)),
			BackColor = _gridHeader,
			Tag = "searchBar"
		};
		searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(84)));
		searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(62)));
		_programSearchBox = new TextBox
		{
			Dock = DockStyle.Fill,
			PlaceholderText = "搜索函数/变量/代码",
			Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
			Margin = new Padding(0, Ui(1), Ui(4), Ui(1))
		};
		StyleTextBox(_programSearchBox);
		_programSearchBox.Tag = "searchInput";
		_programSearchBox.BackColor = _surfaceAlt;
		_programSearchBox.KeyDown += ProgramSearchBoxKeyDown;
		Button searchButton = CommandButton("搜索");
		ApplyButtonStyle(searchButton, "search");
		searchButton.Size = new Size(Ui(78), Ui(34));
		searchButton.Margin = new Padding(0, Ui(1), Ui(4), Ui(1));
		searchButton.Click += delegate { RunProgramSearch(); };
		Button clearSearchButton = PlainButton("清空");
		clearSearchButton.Size = new Size(Ui(58), Ui(30));
		clearSearchButton.Margin = new Padding(0, Ui(3), 0, Ui(3));
		clearSearchButton.Click += delegate
		{
			_programSearchBox.Clear();
			SetProgramSearchResultsVisible(false);
			_programSearchResults?.Items.Clear();
			ClearProgramSearchHighlight();
			ClearFocusedVariable();
		};
		searchLayout.Controls.Add(_programSearchBox, 0, 0);
		searchLayout.Controls.Add(searchButton, 1, 0);
		searchLayout.Controls.Add(clearSearchButton, 2, 0);
		return searchLayout;
	}

	private Control BuildDataMirrorPanel()
	{
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _surface,
			Tag = "surface"
		};
		TableLayoutPanel layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			Padding = new Padding(0),
			BackColor = _surface,
			Tag = "surface"
		};
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(32)));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		TableLayoutPanel header = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			RowCount = 1,
			Margin = Padding.Empty,
			BackColor = _gridHeader,
			Tag = "header"
		};
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(72)));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(72)));
		_dataCodeTitle = new Label
		{
			Dock = DockStyle.Fill,
			Text = "选择函数查看代码与实时值",
			TextAlign = ContentAlignment.MiddleLeft,
			Padding = new Padding(Ui(8), 0, Ui(8), 0),
			ForeColor = _ink,
			BackColor = _gridHeader,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
		_functionCodeTitle = _dataCodeTitle;
		_functionBackButton = new Button
		{
			Dock = DockStyle.Fill,
			Text = "返回",
			FlatStyle = FlatStyle.Flat,
			BackColor = _button,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 8.5f),
			Tag = "button"
		};
		_functionBackButton.FlatAppearance.BorderColor = _gridHeader;
		_functionBackButton.Click += delegate
		{
			NavigateFunctionBack();
		};
		_functionForwardButton = new Button
		{
			Dock = DockStyle.Fill,
			Text = "下级",
			FlatStyle = FlatStyle.Flat,
			BackColor = _button,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 8.5f),
			Tag = "button"
		};
		_functionForwardButton.FlatAppearance.BorderColor = _gridHeader;
		_functionForwardButton.Click += delegate
		{
			NavigateFunctionForward();
		};
		header.Controls.Add(_dataCodeTitle, 0, 0);
		header.Controls.Add(_functionBackButton, 1, 0);
		header.Controls.Add(_functionForwardButton, 2, 0);
		_visibleValuesLabel = new Label
		{
			Visible = false,
			Text = "可见变量：无",
		};
		_runtimeLocationLabel = new Label
		{
			Visible = false,
			Text = "",
			Tag = "runtime"
		};
		_dataCodeBox = new FastCodeRichTextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			WordWrap = false,
			DetectUrls = false,
			HideSelection = false,
			BackColor = _surface,
			ForeColor = _ink,
			Font = new Font("Consolas", _functionCodeFontSize),
			ScrollBars = RichTextBoxScrollBars.Both,
			Visible = false
		};
		_codeEditor = CreateScintillaCodeEditor();
		_codeValueOverlay = new CodeValueOverlay
		{
			Dock = DockStyle.Fill,
			Visible = false,
			BackColor = Color.Transparent,
			ForeColor = _codeValueColor,
			Font = new Font("Consolas", Math.Max(9f, _functionCodeFontSize - 0.5f)),
			TabStop = false,
			Tag = "surface"
		};
		_functionCodeBox = _dataCodeBox;
		_functionCodePanel = panel;
		Panel codeHost = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _surface,
			Tag = "surface"
		};
		codeHost.Controls.Add(_codeEditor);
		codeHost.Controls.Add(_codeValueOverlay);
		_codeValueOverlay.BringToFront();
		layout.Controls.Add(header, 0, 0);
		layout.Controls.Add(codeHost, 0, 1);
		panel.Controls.Add(layout);
		return panel;
	}

	private Scintilla CreateScintillaCodeEditor()
	{
		var editor = new Scintilla
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			BorderStyle = ScintillaNET.BorderStyle.None,
			HScrollBar = true,
			VScrollBar = true,
			WrapMode = ScintillaNET.WrapMode.None,
			TabWidth = 4,
			UseTabs = false,
			Font = new Font("Consolas", _functionCodeFontSize),
			BackColor = _surface,
			ForeColor = _ink,
			Tag = "codeEditor"
		};

		editor.MouseDown += CodeEditorMouseDown;
		editor.MouseUp += CodeEditorMouseUp;
		editor.MouseClick += CodeEditorMouseClick;
		editor.MouseDoubleClick += CodeEditorMouseDoubleClick;
		editor.MouseMove += CodeEditorMouseMove;
		editor.MouseLeave += delegate
		{
			ClearFunctionHoverCache();
			editor.Cursor = Cursors.IBeam;
		};
		editor.MouseWheel += CodeEditorMouseWheel;
		editor.KeyDown += CodeEditorKeyDown;
		editor.UpdateUI += CodeEditorUpdateUI;
		editor.ContextMenuStrip = CreateCodeWatchContextMenu(editor);
		ApplyScintillaTheme(editor);
		return editor;
	}

	private void ApplyScintillaTheme(Scintilla editor)
	{
		if (editor == null || editor.IsDisposed)
		{
			return;
		}

		editor.BackColor = _surface;
		editor.ForeColor = _ink;
		editor.Font = new Font("Consolas", _functionCodeFontSize);
		editor.LexerName = "cpp";
		editor.StyleResetDefault();
		editor.Styles[ScintillaNET.Style.Default].Font = "Consolas";
		editor.Styles[ScintillaNET.Style.Default].SizeF = _functionCodeFontSize;
		editor.Styles[ScintillaNET.Style.Default].BackColor = _surface;
		editor.Styles[ScintillaNET.Style.Default].ForeColor = _ink;
		editor.StyleClearAll();
		editor.Styles[ScintillaNET.Style.LineNumber].BackColor = _surface;
		editor.Styles[ScintillaNET.Style.LineNumber].ForeColor = _muted;
		editor.Styles[ScintillaNET.Style.Cpp.Default].BackColor = _surface;
		editor.Styles[ScintillaNET.Style.Cpp.Default].ForeColor = _ink;
		editor.Styles[ScintillaNET.Style.Cpp.Comment].ForeColor = _codeCommentColor;
		editor.Styles[ScintillaNET.Style.Cpp.CommentLine].ForeColor = _codeCommentColor;
		editor.Styles[ScintillaNET.Style.Cpp.CommentDoc].ForeColor = _codeCommentColor;
		editor.Styles[ScintillaNET.Style.Cpp.Number].ForeColor = _accent;
		editor.Styles[ScintillaNET.Style.Cpp.Word].ForeColor = _codeKeywordColor;
		editor.Styles[ScintillaNET.Style.Cpp.Word].Bold = true;
		editor.Styles[ScintillaNET.Style.Cpp.String].ForeColor = _codeValueColor;
		editor.Styles[ScintillaNET.Style.Cpp.StringEol].ForeColor = _codeValueColor;
		editor.Styles[ScintillaNET.Style.Cpp.Word2].ForeColor = _codeFunctionColor;
		float inlineValueFontSize = Math.Max(9f, _functionCodeFontSize + 1f);
		editor.Styles[ScintillaStyleValueFresh].Font = "Consolas";
		editor.Styles[ScintillaStyleValueFresh].SizeF = inlineValueFontSize;
		editor.Styles[ScintillaStyleValueFresh].BackColor = _codeValueTagActiveBackColor;
		editor.Styles[ScintillaStyleValueFresh].ForeColor = _codeValueTagActiveForeColor;
		editor.Styles[ScintillaStyleValueFresh].Bold = false;
		editor.Styles[ScintillaStyleValueStale].Font = "Consolas";
		editor.Styles[ScintillaStyleValueStale].SizeF = inlineValueFontSize;
		editor.Styles[ScintillaStyleValueStale].BackColor = _codeValueTagInactiveBackColor;
		editor.Styles[ScintillaStyleValueStale].ForeColor = _codeValueTagInactiveForeColor;
		editor.Styles[ScintillaStyleValueStale].Bold = false;
		editor.SetKeywords(0, string.Join(" ", new[]
		{
			"auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum",
			"extern", "float", "for", "goto", "if", "inline", "int", "long", "register", "return", "short",
			"signed", "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned", "void",
			"volatile", "while", "uint8_t", "uint16_t", "uint32_t", "int8_t", "int16_t", "int32_t"
		}));
		editor.CaretLineVisible = false;
		editor.CaretLineBackColor = Color.FromArgb(0, _surface);
		editor.CaretLineLayer = ScintillaNET.Layer.UnderText;
		Color selectionBackColor = IsCurrentLightTheme()
			? MixColor(_surface, _accent, 0.36f)
			: MixColor(_surface, _accent, 0.58f);
		editor.SelectionEolFilled = false;
		editor.SelectionLayer = ScintillaNET.Layer.UnderText;
		editor.SelectionBackColor = selectionBackColor;
		editor.SelectionTextColor = IsCurrentLightTheme() ? Color.FromArgb(8, 24, 38) : Color.White;
		editor.Margins[0].Width = Ui(36);
		editor.Margins[0].Type = ScintillaNET.MarginType.Number;
		editor.Margins[ScintillaValueMarginIndex].Type = ScintillaNET.MarginType.Text;
		editor.Margins[ScintillaValueMarginIndex].Width = 0;
		editor.Margins[ScintillaValueMarginIndex].Sensitive = false;
		editor.Markers[ScintillaMarkerTrueLine].Symbol = ScintillaNET.MarkerSymbol.Background;
		editor.Markers[ScintillaMarkerTrueLine].SetBackColor(_codeTrueLineBackColor);
		editor.Markers[ScintillaMarkerSearchLine].Symbol = ScintillaNET.MarkerSymbol.Background;
		editor.Markers[ScintillaMarkerSearchLine].SetBackColor(_programSearchLineBackColor);
		editor.Indicators[ScintillaIndicatorFocus].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorFocus].ForeColor = _codeFocusVariableBackColor;
		editor.Indicators[ScintillaIndicatorFocus].Alpha = 90;
		editor.Indicators[ScintillaIndicatorFocus].Under = true;
		editor.Indicators[ScintillaIndicatorSearch].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorSearch].ForeColor = _programSearchMatchBackColor;
		editor.Indicators[ScintillaIndicatorSearch].Alpha = 90;
		editor.Indicators[ScintillaIndicatorSearch].Under = true;
		editor.Indicators[ScintillaIndicatorValueNormal].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorValueNormal].ForeColor = _codeValueTagInactiveBackColor;
		editor.Indicators[ScintillaIndicatorValueNormal].Alpha = 115;
		editor.Indicators[ScintillaIndicatorValueNormal].OutlineAlpha = 0;
		editor.Indicators[ScintillaIndicatorValueNormal].Under = true;
		editor.Indicators[ScintillaIndicatorValueFresh].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorValueFresh].ForeColor = _codeValueTagActiveBackColor;
		editor.Indicators[ScintillaIndicatorValueFresh].Alpha = 115;
		editor.Indicators[ScintillaIndicatorValueFresh].OutlineAlpha = 0;
		editor.Indicators[ScintillaIndicatorValueFresh].Under = true;
		editor.Indicators[ScintillaIndicatorValueNormalBorder].Style = ScintillaNET.IndicatorStyle.StraightBox;
		editor.Indicators[ScintillaIndicatorValueNormalBorder].ForeColor = _codeValueTagBorderColor;
		editor.Indicators[ScintillaIndicatorValueNormalBorder].Alpha = 0;
		editor.Indicators[ScintillaIndicatorValueNormalBorder].OutlineAlpha = 170;
		editor.Indicators[ScintillaIndicatorValueNormalBorder].Under = true;
		editor.Indicators[ScintillaIndicatorValueFreshBorder].Style = ScintillaNET.IndicatorStyle.StraightBox;
		editor.Indicators[ScintillaIndicatorValueFreshBorder].ForeColor = _codeValueTagBorderColor;
		editor.Indicators[ScintillaIndicatorValueFreshBorder].Alpha = 0;
		editor.Indicators[ScintillaIndicatorValueFreshBorder].OutlineAlpha = 190;
		editor.Indicators[ScintillaIndicatorValueFreshBorder].Under = true;
		editor.Indicators[ScintillaIndicatorValueNormalText].Style = ScintillaNET.IndicatorStyle.TextFore;
		editor.Indicators[ScintillaIndicatorValueNormalText].ForeColor = _codeValueTagInactiveForeColor;
		editor.Indicators[ScintillaIndicatorValueNormalText].Under = false;
		editor.Indicators[ScintillaIndicatorValueFreshText].Style = ScintillaNET.IndicatorStyle.TextFore;
		editor.Indicators[ScintillaIndicatorValueFreshText].ForeColor = _codeValueTagActiveForeColor;
		editor.Indicators[ScintillaIndicatorValueFreshText].Under = false;
		editor.Indicators[ScintillaIndicatorScopeBrace].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorScopeBrace].ForeColor = IsCurrentLightTheme() ? Color.FromArgb(176, 107, 0) : Color.FromArgb(251, 191, 36);
		editor.Indicators[ScintillaIndicatorScopeBrace].Alpha = 120;
		editor.Indicators[ScintillaIndicatorScopeBrace].OutlineAlpha = 180;
		editor.Indicators[ScintillaIndicatorScopeBrace].Under = false;
		editor.Indicators[ScintillaIndicatorFunctionHover].Style = ScintillaNET.IndicatorStyle.StraightBox;
		editor.Indicators[ScintillaIndicatorFunctionHover].ForeColor = _codeFunctionColor;
		editor.Indicators[ScintillaIndicatorFunctionHover].Alpha = 55;
		editor.Indicators[ScintillaIndicatorFunctionHover].OutlineAlpha = 165;
		editor.Indicators[ScintillaIndicatorFunctionHover].Under = true;
		editor.Indicators[ScintillaIndicatorForceHold].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorForceHold].ForeColor = ForceHoldBackColor;
		editor.Indicators[ScintillaIndicatorForceHold].Alpha = 230;
		editor.Indicators[ScintillaIndicatorForceHold].OutlineAlpha = 255;
		editor.Indicators[ScintillaIndicatorForceHold].Under = true;
		editor.Indicators[ScintillaIndicatorForceHoldText].Style = ScintillaNET.IndicatorStyle.TextFore;
		editor.Indicators[ScintillaIndicatorForceHoldText].ForeColor = ForceHoldForeColor;
		editor.Indicators[ScintillaIndicatorForceHoldText].Under = false;
		editor.Indicators[ScintillaIndicatorTrueCondition].Style = ScintillaNET.IndicatorStyle.RoundBox;
		editor.Indicators[ScintillaIndicatorTrueCondition].ForeColor = Color.FromArgb(34, 197, 94);
		editor.Indicators[ScintillaIndicatorTrueCondition].Alpha = 150;
		editor.Indicators[ScintillaIndicatorTrueCondition].OutlineAlpha = 255;
		editor.Indicators[ScintillaIndicatorTrueCondition].Under = false;
		editor.ScrollWidthTracking = true;
	}


	private Control BuildAnalysisSwitcherPanel()
	{
		TableLayoutPanel layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 2,
			Padding = new Padding(0),
			BackColor = _surface,
			Tag = "surface"
		};
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(30)));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

		TableLayoutPanel header = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			RowCount = 1,
			Margin = Padding.Empty,
			BackColor = _gridHeader,
			Tag = "header"
		};
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(96)));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(96)));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

		_analysisFunctionButton = PlainButton("4 函数位置");
		_analysisFunctionButton.Dock = DockStyle.Fill;
		_analysisFunctionButton.Margin = new Padding(Ui(4), Ui(3), Ui(3), Ui(3));
		_analysisFunctionButton.Click += delegate { ShowAnalysisPane(showFunctionAnalysis: true); };

		_analysisInsightButton = PlainButton("1 程序透视");
		_analysisInsightButton.Dock = DockStyle.Fill;
		_analysisInsightButton.Margin = new Padding(0, Ui(3), Ui(4), Ui(3));
		_analysisInsightButton.Click += delegate { ShowAnalysisPane(showFunctionAnalysis: false); };

		_functionAnalysisTitle = new Label
		{
			Dock = DockStyle.Fill,
			Text = "4 函数位置",
			TextAlign = ContentAlignment.MiddleLeft,
			Padding = new Padding(Ui(8), 0, Ui(8), 0),
			ForeColor = _ink,
			BackColor = _gridHeader,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};

		header.Controls.Add(_analysisFunctionButton, 0, 0);
		header.Controls.Add(_analysisInsightButton, 1, 0);
		header.Controls.Add(_functionAnalysisTitle, 2, 0);
		layout.Controls.Add(header, 0, 0);

		Panel content = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _surface,
			Tag = "surface"
		};
		_analysisFunctionPanel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _surface,
			Tag = "surface"
		};
		_analysisFunctionPanel.Controls.Add(BuildFunctionAnalysisPanel());
		_analysisInsightPanel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _surface,
			Tag = "surface"
		};
		_analysisInsightPanel.Controls.Add(BuildProgramInsightPanel());
		content.Controls.Add(_analysisInsightPanel);
		content.Controls.Add(_analysisFunctionPanel);
		layout.Controls.Add(content, 0, 1);

		ShowAnalysisPane(showFunctionAnalysis: true);
		UpdateProgramInsightPanel(force: true);
		return layout;
	}

	private void ShowAnalysisPane(bool showFunctionAnalysis)
	{
		if (_analysisFunctionPanel == null || _analysisInsightPanel == null)
		{
			return;
		}

		_analysisFunctionPanel.Visible = showFunctionAnalysis;
		_analysisInsightPanel.Visible = !showFunctionAnalysis;
		if (showFunctionAnalysis)
		{
			_analysisFunctionPanel.BringToFront();
		}
		else
		{
			_analysisInsightPanel.BringToFront();
			UpdateProgramInsightPanel(force: true);
		}

		if (_analysisFunctionButton != null)
		{
			ApplyButtonStyle(_analysisFunctionButton, showFunctionAnalysis ? "working" : "plain");
		}
		if (_analysisInsightButton != null)
		{
			ApplyButtonStyle(_analysisInsightButton, showFunctionAnalysis ? "plain" : "working");
		}
		if (_functionAnalysisTitle != null)
		{
			_functionAnalysisTitle.Text = showFunctionAnalysis
				? (_currentFunctionSource == null ? "4 函数位置" : $"4 函数位置    {_currentFunctionSource.FunctionName}")
				: "1 程序透视";
		}
	}

	private Control BuildFunctionAnalysisPanel()
	{
		TableLayoutPanel layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 2,
			Padding = new Padding(0),
			BackColor = _surface,
			Tag = "surface"
		};
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(70)));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		_functionAnalysisSummaryBox = new RichTextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			DetectUrls = false,
			WordWrap = true,
			ScrollBars = RichTextBoxScrollBars.Vertical,
			BackColor = _surfaceAlt,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
		};
		_functionAnalysisSummaryBox.Text = "选择函数查看程序位置。";
		_functionAnalysisChart = new FlowChartView
		{
			Dock = DockStyle.Fill,
			Tag = "flowchart",
			HighContrastNodes = true,
			Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold)
		};
		_functionAnalysisChart.NodeClick += FunctionAnalysisNodeClick;
		_functionAnalysisChart.NodeDoubleClick += FunctionAnalysisNodeClick;
		_functionAnalysisChart.SetGraph(
			new[] { new FlowChartNode("empty", "进入函数后\n显示位置", new RectangleF(20, 20, 180, 64), 0, Kind: "business") },
			Array.Empty<FlowChartEdge>());
		_functionAnalysisChart.SetAnimationEnabled(false);

		layout.Controls.Add(_functionAnalysisSummaryBox, 0, 0);
		layout.Controls.Add(_functionAnalysisChart, 0, 1);
		return layout;
	}

	private Control BuildProgramInsightPanel()
	{
		TableLayoutPanel layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 4,
			Padding = new Padding(Ui(10)),
			BackColor = _panel,
			Tag = "panel"
		};
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(28)));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(28)));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(58)));
		layout.Controls.Add(SectionLabel("1 程序透视"), 0, 0);
		Label hint = SmallLabel("点小三角展开/收起；单击定位右侧代码；Ctrl+单击进入函数");
		hint.Dock = DockStyle.Fill;
		hint.AutoEllipsis = true;
		layout.Controls.Add(hint, 0, 1);
		_flowChart = new FlowChartView
		{
			Dock = DockStyle.Fill,
			Tag = "flowchart",
			HighContrastNodes = true,
			TreeFontSize = _programTreeFontSize
		};
		_flowChart.NodeClick += FlowChartNodeClick;
		_flowChart.NodeDoubleClick += FlowChartNodeDoubleClick;
		layout.Controls.Add(_flowChart, 0, 2);
		FlowLayoutPanel footer = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Margin = new Padding(0, Ui(8), 0, 0),
			Padding = new Padding(Ui(8), Ui(4), Ui(8), Ui(4)),
			BackColor = _surface,
			Tag = "surface"
		};
		_programSummaryLabel = SmallLabel("未生成图谱");
		_programSummaryLabel.Width = Ui(260);
		_symbolCountLabel = SmallLabel("未读取");
		_symbolCountLabel.Width = Ui(200);
		footer.Controls.Add(_programSummaryLabel);
		footer.Controls.Add(_symbolCountLabel);
		layout.Controls.Add(footer, 0, 3);
		return layout;
	}

	private Control BuildBusinessPanel()
	{
		return BuildProgramInsightPanel();
	}

	private Panel BuildFunctionCodePanel()
	{
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _surface,
			Tag = "surface"
		};
		TableLayoutPanel layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2,
			Padding = new Padding(0),
			BackColor = _surface
		};
		layout.ColumnCount = 3;
		layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(56)));
		layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui(56)));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(32)));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		_functionCodeTitle = new Label
		{
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			Padding = new Padding(Ui(8), 0, Ui(8), 0),
			ForeColor = _ink,
			BackColor = _gridHeader,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
		_functionBackButton = new Button
		{
			Dock = DockStyle.Fill,
			Text = "返回",
			FlatStyle = FlatStyle.Flat,
			BackColor = _button,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 8.5f),
			Tag = "button"
		};
		_functionBackButton.FlatAppearance.BorderColor = _gridHeader;
		_functionBackButton.Click += delegate
		{
			NavigateFunctionBack();
		};
		_functionForwardButton = new Button
		{
			Dock = DockStyle.Fill,
			Text = "下级",
			FlatStyle = FlatStyle.Flat,
			BackColor = _button,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 8.5f),
			Tag = "button"
		};
		_functionForwardButton.FlatAppearance.BorderColor = _gridHeader;
		_functionForwardButton.Click += delegate
		{
			NavigateFunctionForward();
		};
		_functionCodeBox = new FastCodeRichTextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			WordWrap = false,
			DetectUrls = false,
			HideSelection = false,
			BackColor = _surface,
			ForeColor = _ink,
			Font = new Font("Consolas", _functionCodeFontSize),
			ScrollBars = RichTextBoxScrollBars.Both
		};
		if (_functionCodeBox is FastCodeRichTextBox fastCodeBox)
		{
			fastCodeBox.ControlMouseWheel += FunctionCodeBoxMouseWheel;
			fastCodeBox.ImmediateWheel += delegate
			{
				MarkUiWheelActivity();
			};
			fastCodeBox.WheelScrolled += delegate
			{
				MarkUiWheelActivity();
				SyncDataCodeScrollFromProgram();
				ScheduleVisibleDataRefreshAfterScroll();
			};
		}
		_functionCodeBox.MouseDown += CodeBoxMouseDown;
		_functionCodeBox.MouseUp += CodeBoxMouseUp;
		_functionCodeBox.MouseClick += FunctionCodeBoxMouseClick;
		_functionCodeBox.MouseDoubleClick += CodeBoxMouseDoubleClick;
		_functionCodeBox.MouseMove += FunctionCodeBoxMouseMove;
		_functionCodeBox.SelectionChanged += FunctionCodeBoxSelectionChanged;
		_functionCodeBox.KeyDown += CodeBoxKeyDown;
		_functionCodeBox.ContextMenuStrip = CreateCodeWatchContextMenu(_functionCodeBox);
		_functionCodeBox.VScroll += delegate
		{
			MarkUiWheelActivity();
			SyncDataCodeScrollFromProgram();
			ScheduleVisibleDataRefreshAfterScroll();
		};
		_functionCodeBox.MouseWheel += delegate
		{
			MarkUiWheelActivity();
			SyncDataCodeScrollFromProgram();
			ScheduleVisibleDataRefreshAfterScroll();
		};
		_functionCodeBox.MouseLeave += delegate
		{
			if (_functionCodeBox != null)
			{
				ClearFunctionHoverCache();
				_functionCodeBox.Cursor = Cursors.IBeam;
			}
		};
		EnableDoubleBuffer(_functionCodeBox);
		layout.Controls.Add(_functionCodeTitle, 0, 0);
		layout.Controls.Add(_functionBackButton, 1, 0);
		layout.Controls.Add(_functionForwardButton, 2, 0);
		layout.Controls.Add(_functionCodeBox, 0, 1);
		layout.SetColumnSpan(_functionCodeBox, 3);
		panel.Controls.Add(layout);
		return panel;
	}

	private ListView CreateDenseListView()
	{
		ListView obj = new ListView
		{
			Dock = DockStyle.Fill,
			View = View.Details,
			FullRowSelect = true,
			GridLines = false,
			BorderStyle = BorderStyle.None,
			HeaderStyle = ColumnHeaderStyle.Nonclickable,
			BackColor = _surface,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 8.5f),
			Tag = "list"
		};
		EnableDoubleBuffer(obj);
		return obj;
	}

	private Control BuildLogArea()
	{
		Panel panel = CardPanel();
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2,
			Padding = new Padding(Ui(14), Ui(10), Ui(14), Ui(14))
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui(24)));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.Controls.Add(SectionLabel("日志"), 0, 0);
		_logBox = new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Vertical,
			BackColor = _surface,
			ForeColor = _ink,
			BorderStyle = BorderStyle.None,
			Font = new Font("Consolas", 9f),
			Tag = "input"
		};
		tableLayoutPanel.Controls.Add(_logBox, 0, 1);
		panel.Controls.Add(tableLayoutPanel);
		return panel;
	}

	private void BrowseProjectDirectory()
	{
		using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
		{
			Description = "请选择 Keil 工程目录",
			UseDescriptionForTitle = true
		};
		if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
		{
			SetWorkDirectory(folderBrowserDialog.SelectedPath, loadMap: true);
			_ = RefreshMapAsync();
		}
	}

	private async Task RefreshMapAsync()
	{
		DateTime now = DateTime.UtcNow;
		if (_refreshMapBusy || (now - _lastManualRefreshUtc).TotalMilliseconds < ManualRefreshThrottleMs)
		{
			Log("刷新请求已合并，正在处理当前工作目录。");
			return;
		}

		_refreshMapBusy = true;
		_lastManualRefreshUtc = now;
		if (_refreshButton != null)
		{
			_refreshButton.Enabled = false;
			_refreshButton.Text = "刷新中";
			ApplyButtonStyle(_refreshButton, "working");
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			string workDirectoryFromUi = GetWorkDirectoryFromUi();
			if (workDirectoryFromUi.Length == 0)
			{
				Log("请选择工作目录。");
			}
			else if (Directory.Exists(workDirectoryFromUi))
			{
				ClearOfflineSimulationProgramCache();
				SetWorkDirectory(workDirectoryFromUi, loadMap: false);
				ClearFunctionIndex();
				LoadLatestMapFromDirectory(workDirectoryFromUi);
				QueueBusinessDictionaryRefresh(workDirectoryFromUi, force: true);
				UpdateFirmwareVersionDisplay();
				RefreshCurrentFunctionSourceFromDisk(logResult: true);
				RefreshProgramGraphPanel(workDirectoryFromUi, force: true);
				WarmFunctionIndex(workDirectoryFromUi);
				await EnsureFirmwareSynchronizedByRefreshAsync(workDirectoryFromUi);
				LogPerformance("刷新完成：已重新读取当前工作目录", stopwatch);
			}
			else if (File.Exists(workDirectoryFromUi))
			{
				ClearOfflineSimulationProgramCache();
				_mapFilePath = workDirectoryFromUi;
				_workDirectory = Path.GetDirectoryName(workDirectoryFromUi) ?? "";
				_mapPathBox.Text = _workDirectory;
					LoadMapFile(workDirectoryFromUi);
					if (_workDirectory.Length > 0 && Directory.Exists(_workDirectory))
					{
						ClearFunctionIndex();
						QueueBusinessDictionaryRefresh(_workDirectory, force: true);
						UpdateFirmwareVersionDisplay();
						RefreshCurrentFunctionSourceFromDisk(logResult: true);
						RefreshProgramGraphPanel(_workDirectory, force: true);
					WarmFunctionIndex(_workDirectory);
					await EnsureFirmwareSynchronizedByRefreshAsync(_workDirectory);
					LogPerformance("刷新完成：已重新读取当前工作目录", stopwatch);
				}
			}
			else
			{
				Log("工作目录不存在：" + workDirectoryFromUi);
			}
		}
		finally
		{
			_refreshMapBusy = false;
			if (_refreshButton != null && !_refreshButton.IsDisposed)
			{
				_refreshButton.Text = "刷新";
				_refreshButton.Enabled = true;
				ApplyButtonStyle(_refreshButton, "refresh");
			}
		}
	}

	private async Task EnsureFirmwareSynchronizedByRefreshAsync(string workDirectory)
	{
		if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
		{
			return;
		}

		string installedAgent = FindInstalledAgentFile(workDirectory);
		string bundledVersion = GetBundledFirmwareVersionText();
		string installedVersion = installedAgent.Length == 0
			? "未安装"
			: FirmwareInstaller.FormatVersion(FirmwareInstaller.ReadAgentVersion(installedAgent));
		bool sourceNeedsSync = installedAgent.Length == 0 ||
			!installedVersion.Equals(bundledVersion, StringComparison.OrdinalIgnoreCase);
		bool current = IsWorkFirmwareVersionCurrent(workDirectory);
		if (current)
		{
			Log("刷新检查：工程固件已同步。");
			return;
		}

		Log(sourceNeedsSync
			? $"刷新检查：工程固件 {installedVersion}，软件内置 {bundledVersion}，开始自动同步。"
			: "刷新检查：工程固件源码已同步，开始验证当前 Target bin。");

		string agentCopy;
		try
		{
			agentCopy = BundledFirmwareAgent.WriteTempCopy();
		}
		catch (Exception ex)
		{
			Log("刷新检查：没有找到内置固件文件：" + ex.Message);
			UpdateFirmwareVersionDisplay();
			return;
		}

		FirmwareInstallResult firmwareInstallResult = await Task.Run(() => FirmwareInstaller.Install(workDirectory, agentCopy));
		foreach (string message in firmwareInstallResult.Messages)
		{
			Log(message);
		}

		LoadLatestMapFromDirectory(workDirectory);
		RefreshProgramGraphPanel(workDirectory, force: true);
		UpdateFirmwareVersionDisplay();
		Log(firmwareInstallResult.Success
			? "刷新完成：固件已自动同步，请下载到控制器后生效。"
			: "刷新完成：固件未能自动同步，未通过的原因已显示在日志里。");
	}

	private void SetWorkDirectory(string directory, bool loadMap)
	{
		bool directoryChanged = !_workDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase);
		if (directoryChanged)
		{
			ClearOfflineSimulationProgramCache();
			ClearFunctionIndex();
			_mapFilePath = "";
			_mapLastWrite = default(DateTime);
			_symbols.Clear();
			_symbolLookup.Clear();
			_symbolBaseLookup.Clear();
			_symbolTailLookup.Clear();
			_suggestions?.Items.Clear();
			if (_symbolCountLabel != null)
			{
				_symbolCountLabel.Text = "未读取";
			}
			_programGraphSnapshot = null;
			_businessDictionary = ProjectBusinessDictionary.Empty;
			_businessDictionaryDirectory = "";
			_lastProgramInsightSignature = "";
			UpdateProgramInsightPanel(force: true);
		}
		_workDirectory = directory;
		_mapPathBox.Text = directory;
		RefreshOfflineRootCandidatesUi(directory);
		UpdateFirmwareVersionDisplay();
		Log("工作目录：" + directory);
		SaveDefaultProfileQuietly();
		if (loadMap)
		{
			LoadLatestMapFromDirectory(directory);
			RefreshProgramGraphPanel(directory);
		}
		if (directoryChanged || loadMap)
		{
			WarmFunctionIndex(directory);
			QueueBusinessDictionaryRefresh(directory, force: directoryChanged);
		}
	}

	private string GetWorkDirectoryFromUi()
	{
		string text = _mapPathBox.Text.Trim('"', ' ');
		if (text.Length > 0 && (Directory.Exists(text) || File.Exists(text)))
		{
			return text;
		}
		if (_workDirectory.Length > 0 && Directory.Exists(_workDirectory))
		{
			return _workDirectory;
		}
		return text;
	}

	private void LoadLatestMapFromDirectory(string directory)
	{
		string text = FindLatestAxfFile(directory);
		if (text != null)
		{
			if (KeilAxfParser.TryParse(text, out List<MapSymbol> symbols, out string message))
			{
				_symbols = symbols;
				RebuildSymbolIndexes();
				RefreshWatchMetadataFromSymbols();
				_mapFilePath = text;
				_mapLastWrite = File.GetLastWriteTimeUtc(text);
				_symbolCountLabel.Text = $"已读取 {_symbols.Count} 个 RAM 变量";
				Log(message);
				SaveDefaultProfileQuietly();
				RefreshSuggestions();
				return;
			}
			Log("AXF 调试信息读取失败，改用 map：" + message);
		}
		string text2 = FindLatestMapFile(directory);
		if (text2 == null)
		{
			Log("没有找到 .map 文件，请先编译 Keil 工程。");
			return;
		}
		_mapFilePath = text2;
		LoadMapFile(text2);
	}

	private void LoadMapFromTextBox()
	{
		string text = _mapPathBox.Text.Trim('"', ' ');
		if (text.Length == 0)
		{
			Log("请选择变量文件。");
			return;
		}
		if (Directory.Exists(text))
		{
			LoadLatestMapFromDirectory(text);
			return;
		}
		_mapFilePath = text;
		_workDirectory = Path.GetDirectoryName(text) ?? "";
		_mapPathBox.Text = _workDirectory;
		LoadMapFile(text);
	}

	private void LoadMapFile(string path)
	{
		try
		{
			if (Path.GetExtension(path).Equals(".axf", StringComparison.OrdinalIgnoreCase))
			{
				if (!KeilAxfParser.TryParse(path, out List<MapSymbol> symbols, out string message))
				{
					throw new InvalidOperationException(message);
				}
				_symbols = symbols;
				Log(message);
			}
			else
			{
				_symbols = KeilMapParser.Parse(path);
				Log($"变量文件已读取：{Path.GetFileName(path)}，RAM 变量 {_symbols.Count} 个。");
			}
			RebuildSymbolIndexes();
			RefreshWatchMetadataFromSymbols();
			_mapLastWrite = File.GetLastWriteTimeUtc(path);
			_symbolCountLabel.Text = $"已读取 {_symbols.Count} 个 RAM 变量";
			SaveDefaultProfileQuietly();
			RefreshSuggestions();
			ScheduleMemoryTrim();
		}
		catch (Exception ex)
		{
			Log("读取变量文件失败：" + ex.Message);
		}
	}

	private void RebuildSymbolIndexes()
	{
		_symbolLookup = _symbols.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
		_symbolBaseLookup = new Dictionary<string, MapSymbol>(StringComparer.OrdinalIgnoreCase);
		var tailCandidates = new Dictionary<string, MapSymbol?>(StringComparer.OrdinalIgnoreCase);
		foreach (MapSymbol symbol in _symbols.OrderBy(s => s.Name.Length))
		{
			string baseName = GetIdentifierBase(symbol.Name);
			if (baseName.Length > 0 && !_symbolBaseLookup.ContainsKey(baseName))
			{
				_symbolBaseLookup[baseName] = symbol;
			}

			string tailName = GetIdentifierTail(symbol.Name);
			if (tailName.Length == 0)
			{
				continue;
			}
			if (!tailCandidates.ContainsKey(tailName))
			{
				tailCandidates[tailName] = symbol;
			}
			else
			{
				tailCandidates[tailName] = null;
			}
		}
		_symbolTailLookup = tailCandidates
			.Where(pair => pair.Value != null)
			.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
	}

	private void RefreshWatchMetadataFromSymbols()
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (WatchItem watchItem2 in _watchItems)
		{
			if (watchItem2.IsChild)
			{
				continue;
			}
			string key = watchItem2.Name;
			uint result = 0u;
			int num = watchItem2.Name.IndexOf('+', StringComparison.Ordinal);
			if (num > 0)
			{
				key = watchItem2.Name.Substring(0, num);
				string name = watchItem2.Name;
				int num2 = num + 1;
				if (!uint.TryParse(name.Substring(num2, name.Length - num2), out result))
				{
					result = 0u;
				}
			}
			if (_symbolLookup.TryGetValue(key, out MapSymbol value))
			{
				int num3 = Math.Max(1, value.Size - (int)result);
				watchItem2.Address = value.Address + result;
				watchItem2.Size = num3;
				watchItem2.TotalSize = num3;
				watchItem2.TypeName = value.TypeName;
				watchItem2.IsExpandable = result == 0 && KeilMapParser.IsExpandable(value, _symbols);
				ResetTransientWatchState(watchItem2);
				if (!watchItem2.IsExpandable)
				{
					watchItem2.ExpandMode = "";
					hashSet.Add(watchItem2.Name);
				}
			}
		}
		if (hashSet.Count > 0)
		{
			for (int num4 = _watchItems.Count - 1; num4 >= 0; num4--)
			{
				WatchItem watchItem = _watchItems[num4];
				if (watchItem.IsChild && hashSet.Contains(watchItem.ParentName))
				{
					_watchItems.RemoveAt(num4);
				}
			}
		}
		_source.ResetBindings(metadataChanged: false);
	}

	private void CheckMapReload()
	{
		if (_mapFilePath.Length != 0 && File.Exists(_mapFilePath))
		{
			DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(_mapFilePath);
			if (_mapLastWrite != default(DateTime) && lastWriteTimeUtc > _mapLastWrite)
			{
				Log("检测到变量文件更新，自动重新读取。");
				LoadMapFile(_mapFilePath);
				if (_workDirectory.Length > 0 && Directory.Exists(_workDirectory))
				{
					RefreshProgramGraphPanel(_workDirectory);
				}
			}
		}
	}

	private void CheckProgramGraphReload()
	{
		if (_programGraphCheckPending || _workDirectory.Length == 0 || !Directory.Exists(_workDirectory))
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		if ((now - _programGraphLastCheckUtc).TotalSeconds < 8)
		{
			return;
		}
		_programGraphLastCheckUtc = now;
		_programGraphCheckPending = true;
		string root = _workDirectory;
		Task.Run(() =>
		{
			DateTime latestWrite = GetLatestSourceWriteUtc(root, out int sourceCount);
			return (Root: root, LatestWrite: latestWrite, SourceCount: sourceCount);
		}).ContinueWith(task =>
		{
			if (IsDisposed)
			{
				return;
			}
			try
			{
				BeginInvoke((Action)(() =>
				{
					_programGraphCheckPending = false;
					if (task.Status != TaskStatus.RanToCompletion ||
						!_workDirectory.Equals(task.Result.Root, StringComparison.OrdinalIgnoreCase) ||
						task.Result.SourceCount == 0)
					{
						return;
					}
					if (_programGraphLastSourceWrite == default(DateTime))
					{
						_programGraphLastSourceWrite = task.Result.LatestWrite;
						_programGraphSourceCount = task.Result.SourceCount;
						return;
					}
					if (task.Result.LatestWrite > _programGraphLastSourceWrite || task.Result.SourceCount != _programGraphSourceCount)
					{
						Log("检测到源码变化，自动刷新程序框架。");
						ClearFunctionIndex();
						QueueBusinessDictionaryRefresh(_workDirectory, force: true);
						RefreshCurrentFunctionSourceFromDisk(logResult: false);
						RefreshProgramGraphPanel(_workDirectory, force: true);
					}
				}));
			}
			catch
			{
				_programGraphCheckPending = false;
			}
		}, TaskScheduler.Default);
	}

	private static DateTime GetLatestSourceWriteUtc(string root, out int sourceCount)
	{
		sourceCount = 0;
		DateTime latest = default(DateTime);
		var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".git", ".svn", ".vs", "bin", "obj", "Debug", "Release", "Listings", "Objects", "RTE", "__pycache__"
		};
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
				if (!ignored.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
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
				if (!extension.Equals(".c", StringComparison.OrdinalIgnoreCase) &&
					!extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				sourceCount++;
				DateTime write = File.GetLastWriteTimeUtc(file);
				if (write > latest)
				{
					latest = write;
				}
			}
		}
		return latest;
	}

	private void RefreshSuggestions()
	{
		if (_suggestions == null || _variableBox == null || !_suggestions.Visible)
		{
			return;
		}
		_suggestions.BeginUpdate();
		_suggestions.Items.Clear();
		foreach (MapSymbol item in FuzzyMatcher.Search(_symbols, _variableBox.Text))
		{
			_suggestions.Items.Add(new Suggestion(item));
		}
		_suggestions.EndUpdate();
	}

	private void ScheduleRefreshSuggestions()
	{
		if (_suggestionTimer == null)
		{
			RefreshSuggestions();
			return;
		}
		_suggestionTimer.Stop();
		_suggestionTimer.Start();
	}

	private void AddTypedVariable()
	{
		string text = _variableBox.Text.Trim();
		if (text.Length == 0 && _suggestions.SelectedItem is Suggestion suggestion)
		{
			text = suggestion.Symbol.Name;
		}
		if (text.Length == 0 && _suggestions.Items.Count > 0)
		{
			text = ((Suggestion)_suggestions.Items[0]).Symbol.Name;
		}
		AddVariableText(text);
	}

	private void AddSelectedSuggestion()
	{
		if (_suggestions.SelectedItem is Suggestion suggestion)
		{
			AddVariableText(suggestion.Symbol.Name);
		}
	}

	private void AddVariableText(string text)
	{
		WatchItem item;
		string error;
		if (KeilMapParser.TryResolve(text, _symbolLookup, out item, out error))
		{
			if (_watchItems.Any((WatchItem x) => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
			{
				Log("变量已存在：" + item.Name);
				return;
			}
			if (!EnsureWatchCapacityForCurrentContext(out int removed))
			{
				Log($"已达到 {MaxWatchItems} 个变量上限，当前窗口没有可让出的变量。");
				return;
			}
			item.AutoVisible = false;
			_watchItems.Add(item);
			UpdateCycleEstimate();
			SaveDefaultProfileQuietly();
			MarkFunctionCodeDirty();
			FocusVariableAcrossPanels(GetWatchDisplayName(item), updateSearchBox: true);
			Log($"添加变量：{item.Name}，{item.Size} 字节。");
		}
		else
		{
			Log($"添加失败：{text}，{error}。");
		}
	}

	private void AutoWatchVariablesForFunction(FunctionSourceView sourceView)
	{
		if (_symbolLookup.Count == 0 || sourceView.Lines.Count == 0)
		{
			return;
		}

		List<string> lines = sourceView.Lines.ToList();
		List<string> identifiers = BuildFunctionAutoWatchIdentifiers(lines);
		if (identifiers.Count == 0)
		{
			return;
		}

		var functionIdentifiers = identifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var existing = BuildExistingWatchAliasSet();
		int currentFunctionCount = _watchItems.Count(item => item.Enabled && IsWatchVisibleInRange(item, functionIdentifiers, lines));
		int added = 0;
		int removed = 0;
		foreach (string identifier in identifiers)
		{
			if (IsWatchCapacityLimited() && currentFunctionCount >= CurrentFunctionWatchTargetLimit)
			{
				break;
			}
			if (existing.Contains(identifier))
			{
				continue;
			}
			if (!TryResolveSourceIdentifierToWatchItem(identifier, existing, out WatchItem item, out string matchedName))
			{
				continue;
			}
			int removedNow = 0;
			if (IsWatchCapacityLimited() && !EnsureWatchCapacityForVisibleRange(functionIdentifiers, lines, out removedNow))
			{
				break;
			}
			if (removedNow > 0)
			{
				removed += removedNow;
				existing = BuildExistingWatchAliasSet();
				if (existing.Contains(identifier))
				{
					continue;
				}
			}

			item.AutoVisible = true;
			_watchItems.Add(item);
			AddWatchAliases(existing, item.Name);
			existing.Add(matchedName);
			added++;
			currentFunctionCount++;
		}

		if (added > 0 || removed > 0)
		{
			UpdateCycleEstimate();
			_lastVisibleValuesText = "";
			_lastDataCodeText = "";
		}
	}

	private void RemoveSelectedRows()
	{
		List<WatchItem> selectedWatchItems = GetSelectedWatchItems();
		foreach (WatchItem item in selectedWatchItems)
		{
			RemoveWatchItem(item);
		}
		if (selectedWatchItems.Count > 0)
		{
			UpdateCycleEstimate();
			SaveDefaultProfileQuietly();
			MarkFunctionCodeDirty();
		}
	}

	private void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
	{
		if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && _grid.Columns[e.ColumnIndex] is DataGridViewButtonColumn && _grid.Rows[e.RowIndex].DataBoundItem is WatchItem item)
		{
			RemoveWatchItem(item);
			UpdateCycleEstimate();
			SaveDefaultProfileQuietly();
			MarkFunctionCodeDirty();
		}
	}

	private void GridSelectionChanged(object? sender, EventArgs e)
	{
		if (_grid == null || _grid.CurrentRow?.DataBoundItem is not WatchItem item)
		{
			return;
		}

		string name = GetWatchDisplayName(item);
		FocusVariableAcrossPanels(name.Length > 0 ? name : item.Name, updateSearchBox: true);
	}

	private void GridKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Control && e.KeyCode == Keys.C)
		{
			List<string> list = (from item in GetSelectedWatchItems()
				orderby _watchItems.IndexOf(item)
				select item.Name into name
				where !string.IsNullOrWhiteSpace(name)
				select name).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
			if (list.Count == 0 && _grid.CurrentRow?.DataBoundItem is WatchItem watchItem)
			{
				list.Add(watchItem.Name);
			}
			if (list.Count > 0)
			{
				Clipboard.SetText(string.Join(Environment.NewLine, list));
			}
			e.SuppressKeyPress = true;
			e.Handled = true;
		}
	}

	private void RemoveWatchItem(WatchItem item)
	{
		lock (_pollBackoffLock)
		{
			_pollBackoff.Remove(item);
		}
		if (!item.IsChild)
		{
			for (int num = _watchItems.Count - 1; num >= 0; num--)
			{
				if (_watchItems[num].IsChild && _watchItems[num].ParentName.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
				{
					_watchItems.RemoveAt(num);
				}
			}
		}
		_watchItems.Remove(item);
	}

	private void ExpandSelected()
	{
		ToggleExpandRows(GetSelectedWatchItems());
	}

	private List<WatchItem> GetSelectedWatchItems()
	{
		List<WatchItem> list = (from DataGridViewRow r in _grid.SelectedRows
			select r.DataBoundItem as WatchItem into x
			where x != null
			select x).Cast<WatchItem>().ToList();
		if (list.Count == 0 && _grid.CurrentRow?.DataBoundItem is WatchItem item)
		{
			list.Add(item);
		}
		return list.DistinctBy<WatchItem, string>((WatchItem x) => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private void ToggleExpandRows(IEnumerable<WatchItem> selected)
	{
		List<WatchItem> list = (from x in selected.Select(GetExpandableParent)
			where x?.IsExpandable ?? false
			select x).Cast<WatchItem>().DistinctBy<WatchItem, string>((WatchItem x) => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			Log("请选择数组或结构体变量再展开。");
			return;
		}
		bool flag = false;
		foreach (WatchItem item in list)
		{
			if (HasChildRows(item.Name))
			{
				RemoveChildRows(item.Name);
				item.ExpandMode = "";
				flag = true;
			}
			else
			{
				bool flag2 = ExpandWatchFields(item);
				if (!flag2)
				{
					ExpandWatchItem(item, GuessExpandUnit(item));
					flag2 = HasChildRows(item.Name);
				}
				flag = flag || flag2;
			}
		}
		if (!flag)
		{
			Log("没有可展开的子项。");
			return;
		}
		UpdateCycleEstimate();
		SaveDefaultProfileQuietly();
	}

	private void ExpandRows(IEnumerable<WatchItem> selected, int unitSize)
	{
		List<WatchItem> list = (from x in selected.Select(GetExpandableParent)
			where x?.IsExpandable ?? false
			select x).Cast<WatchItem>().DistinctBy<WatchItem, string>((WatchItem x) => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			Log("请选择数组或结构体变量再展开。");
			return;
		}
		bool flag = false;
		foreach (WatchItem item in list)
		{
			if (unitSize == 0)
			{
				bool flag2 = ExpandWatchFields(item);
				if (!flag2)
				{
					ExpandWatchItem(item, GuessExpandUnit(item));
					flag2 = true;
				}
				flag = flag || flag2;
			}
			else
			{
				ExpandWatchItem(item, unitSize);
				flag = true;
			}
		}
		if (!flag)
		{
			Log("没有可展开的子项。");
			return;
		}
		UpdateCycleEstimate();
		SaveDefaultProfileQuietly();
	}

	private WatchItem? GetExpandableParent(WatchItem item)
	{
		WatchItem item2 = item;
		if (!item2.IsChild)
		{
			return item2;
		}
		return _watchItems.FirstOrDefault((WatchItem x) => !x.IsChild && x.Name.Equals(item2.ParentName, StringComparison.OrdinalIgnoreCase));
	}

	private int GuessExpandUnit(WatchItem parent)
	{
		string key = parent.Name.Split('+')[0];
		if (_symbolLookup.TryGetValue(key, out MapSymbol value))
		{
			string typeName = value.TypeName;
			if (typeName.Contains("short", StringComparison.OrdinalIgnoreCase) || typeName.Contains("uint16", StringComparison.OrdinalIgnoreCase) || typeName.Contains("int16", StringComparison.OrdinalIgnoreCase))
			{
				return 2;
			}
		}
		return 1;
	}

	private bool ExpandWatchFields(WatchItem parent)
	{
		WatchItem parent2 = parent;
		if (_watchItems.IndexOf(parent2) < 0)
		{
			return false;
		}
		string fieldPrefix = parent2.Name + ".";
		string arrayPrefix = parent2.Name + "[";
		uint parentEnd = parent2.Address + (uint)Math.Max(parent2.TotalSize, parent2.Size);
		List<MapSymbol> list = (from s in _symbols.Where((MapSymbol s) => (s.Name.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith(arrayPrefix, StringComparison.OrdinalIgnoreCase)) && s.Address >= parent2.Address && s.Address < parentEnd).Where(delegate(MapSymbol s)
			{
				string text;
				if (!s.Name.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					string name = s.Name;
					int length = parent2.Name.Length;
					text = name.Substring(length, name.Length - length);
				}
				else
				{
					string name = s.Name;
					int length = fieldPrefix.Length;
					text = name.Substring(length, name.Length - length);
				}
				string text2 = text;
				return text2.Length > 0 && !text2.Contains('.');
			})
			orderby s.Address
			select s).ToList();
		if (list.Count == 0)
		{
			return false;
		}
		RemoveChildRows(parent2.Name);
		parent2.ExpandMode = "fields";
		int num = _watchItems.IndexOf(parent2) + 1;
		int num2 = IsWatchCapacityLimited() ? Math.Max(0, MaxWatchItems - _watchItems.Count) : int.MaxValue;
		int num3 = 0;
		foreach (MapSymbol item in list)
		{
			if (num3 >= num2)
			{
				if (IsWatchCapacityLimited())
				{
					Log($"已展开到 {MaxWatchItems} 个监控变量上限。");
				}
				break;
			}
			_watchItems.Insert(num + num3, new WatchItem
			{
				Enabled = parent2.Enabled,
				Name = item.Name,
				Address = item.Address,
				Size = item.Size,
				TotalSize = item.Size,
				TypeName = item.TypeName,
				IsExpandable = KeilMapParser.IsExpandable(item, _symbols),
				IsChild = true,
				ParentName = parent2.Name,
				Status = "待读取"
			});
			num3++;
		}
		return num3 > 0;
	}

	private void ExpandWatchItem(WatchItem parent, int unitSize)
	{
		if (_watchItems.IndexOf(parent) < 0)
		{
			return;
		}
		RemoveChildRows(parent.Name);
		parent.ExpandMode = ((unitSize == 1) ? "byte" : "word");
		int num = _watchItems.IndexOf(parent) + 1;
		int num2 = IsWatchCapacityLimited() ? Math.Max(0, MaxWatchItems - _watchItems.Count) : int.MaxValue;
		int num3 = 0;
		for (int i = 0; i < parent.TotalSize; i += unitSize)
		{
			if (num3 >= num2)
			{
				break;
			}
			int num4 = Math.Min(unitSize, parent.TotalSize - i);
			string name = ((num4 == 2) ? $"{parent.Name}[{i}..{i + 1}]" : $"{parent.Name}[{i}]");
			_watchItems.Insert(num + num3, new WatchItem
			{
				Enabled = parent.Enabled,
				Name = name,
				Address = parent.Address + (uint)i,
				Size = num4,
				TotalSize = num4,
				TypeName = parent.TypeName,
				IsChild = true,
				ParentName = parent.Name,
				Status = "待读取"
			});
			num3++;
		}
		if (num3 == 0)
		{
			Log("没有可展开的子项。");
		}
		else if (IsWatchCapacityLimited() && num3 * unitSize < parent.TotalSize)
		{
			Log($"已展开到 {MaxWatchItems} 个监控变量上限。");
		}
	}

	private bool HasChildRows(string parentName)
	{
		return _watchItems.Any((WatchItem x) => x.IsChild && x.ParentName.Equals(parentName, StringComparison.OrdinalIgnoreCase));
	}

	private void RemoveChildRows(string parentName)
	{
		for (int num = _watchItems.Count - 1; num >= 0; num--)
		{
			if (_watchItems[num].IsChild && _watchItems[num].ParentName.Equals(parentName, StringComparison.OrdinalIgnoreCase))
			{
				_watchItems.RemoveAt(num);
			}
		}
	}

	private void CollapseSelectedRows()
	{
		HashSet<string> hashSet = (from x in (from DataGridViewRow r in _grid.SelectedRows
				select r.DataBoundItem as WatchItem into x
				where x != null
				select x).Cast<WatchItem>().ToList()
			select (!x.IsChild) ? x.Name : x.ParentName into x
			where x.Length > 0
			select x).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (hashSet.Count == 0 && _grid.CurrentRow?.DataBoundItem is WatchItem item)
		{
			hashSet.Add(item.IsChild ? item.ParentName : item.Name);
		}
		if (hashSet.Count == 0)
		{
			return;
		}
		for (int num = _watchItems.Count - 1; num >= 0; num--)
		{
			WatchItem watchItem = _watchItems[num];
			if (watchItem.IsChild && hashSet.Contains(watchItem.ParentName))
			{
				_watchItems.RemoveAt(num);
			}
			else if (!watchItem.IsChild && hashSet.Contains(watchItem.Name))
			{
				watchItem.ExpandMode = "";
			}
		}
		UpdateCycleEstimate();
		SaveDefaultProfileQuietly();
	}

	private void GridMouseDown(object? sender, MouseEventArgs e)
	{
		DataGridView.HitTestInfo hitTestInfo = _grid.HitTest(e.X, e.Y);
		if (e.Button == MouseButtons.Right)
		{
			_dragRowIndex = -1;
			ShowGridWatchContextMenu(e.Location);
			return;
		}

		_dragRowIndex = e.Button == MouseButtons.Left && hitTestInfo.RowIndex >= 0 ? hitTestInfo.RowIndex : -1;
	}

	private void GridMouseUp(object? sender, MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Right)
		{
			ShowGridWatchContextMenu(e.Location);
		}
	}

	private ContextMenuStrip CreateGridWatchContextMenu()
	{
		ContextMenuStrip menu = new ContextMenuStrip();
		menu.Opening += delegate(object? sender, CancelEventArgs e)
		{
			e.Cancel = true;
			if (ShouldSuppressAutomaticWatchContextMenu(_grid))
			{
				return;
			}
			ShowGridWatchContextMenu(_grid.PointToClient(Cursor.Position));
		};
		return menu;
	}

	private void ShowGridWatchContextMenu(Point location)
	{
		DataGridView.HitTestInfo hitTestInfo = _grid.HitTest(location.X, location.Y);
		if (hitTestInfo.RowIndex >= 0 && hitTestInfo.RowIndex < _grid.Rows.Count && _grid.Rows[hitTestInfo.RowIndex].DataBoundItem is WatchItem item)
		{
			_grid.ClearSelection();
			_grid.Rows[hitTestInfo.RowIndex].Selected = true;
			int columnIndex = hitTestInfo.ColumnIndex >= 0 ? hitTestInfo.ColumnIndex : 0;
			if (_grid.Columns.Count > 0)
			{
				_grid.CurrentCell = _grid.Rows[hitTestInfo.RowIndex].Cells[Math.Min(columnIndex, _grid.Columns.Count - 1)];
			}
			ShowWatchContextMenu(item, item.Name, _grid, location);
			return;
		}

		ShowWatchContextMenu(null, "", _grid, location);
	}

	private void GridMouseMove(object? sender, MouseEventArgs e)
	{
		if ((e.Button & MouseButtons.Left) == MouseButtons.Left && _dragRowIndex >= 0)
		{
			_grid.DoDragDrop(_grid.Rows[_dragRowIndex], DragDropEffects.Move);
		}
	}

	private void GridDragOver(object? sender, DragEventArgs e)
	{
		e.Effect = DragDropEffects.Move;
	}

	private void GridDragDrop(object? sender, DragEventArgs e)
	{
		if (_dragRowIndex < 0 || _dragRowIndex >= _watchItems.Count)
		{
			return;
		}
		Point point = _grid.PointToClient(new Point(e.X, e.Y));
		int num = _grid.HitTest(point.X, point.Y).RowIndex;
		if (num < 0)
		{
			num = _watchItems.Count - 1;
		}
		if (num != _dragRowIndex)
		{
			WatchItem item = _watchItems[_dragRowIndex];
			_watchItems.RemoveAt(_dragRowIndex);
			if (num > _dragRowIndex)
			{
				num--;
			}
			num = Math.Clamp(num, 0, _watchItems.Count);
			_watchItems.Insert(num, item);
			_grid.ClearSelection();
			if (num < _grid.Rows.Count)
			{
				_grid.Rows[num].Selected = true;
			}
			SaveDefaultProfileQuietly();
			_dragRowIndex = -1;
		}
	}

	private void UpdateCycleEstimate()
	{
		if (_cycleEstimateLabel != null)
		{
			if (!_running && !_offlineSimulation)
			{
				_cycleEstimateLabel.Text = _adapter == null ? "未连接" : "已连接 / 未刷新";
				return;
			}

			(int visibleCount, bool hasVisible) = GetVisiblePollEstimate();
			if (hasVisible && visibleCount > 0)
			{
				int requestedCycleMs = Math.Max(10, _targetCycleMs);
				int effectiveCycleMs = BuildVisibleEffectiveCycleMs(visibleCount, requestedCycleMs);
				_cycleEstimateLabel.Text = $"{visibleCount} 可见 / {effectiveCycleMs}ms";
				return;
			}

			int num = _watchItems.Count((WatchItem x) => x.Enabled);
			if (num == 0)
			{
				_cycleEstimateLabel.Text = "无变量";
				return;
			}
			int num2 = Math.Max(10, _targetCycleMs);
			_cycleEstimateLabel.Text = $"{num} 项 / {num2}ms";
		}
	}

	private (int Count, bool HasVisible) GetVisiblePollEstimate()
	{
		List<string> visiblePriority;
		lock (_pollPriorityLock)
		{
			visiblePriority = _visiblePollPriorityNames.ToList();
		}

		if (visiblePriority.Count == 0)
		{
			if (!InvokeRequired && _currentFunctionSource != null && _functionCodePanel != null && _functionCodePanel.Visible)
			{
				int visibleWatchCount = GetVisibleWatchItems().Count;
				return (visibleWatchCount, visibleWatchCount > 0);
			}

			return (0, false);
		}

		var names = new HashSet<string>(visiblePriority, StringComparer.OrdinalIgnoreCase);
		int count = _watchItems.Count(item => item.Enabled && names.Contains(item.Name));
		return (count, true);
	}

	private void ToggleConnect()
	{
		if (_offlineSimulation)
		{
			StopOfflineSimulation();
		}

		if (_adapter != null)
		{
			StopPolling(waitForExit: true);
			_adapter.Dispose();
			_adapter = null;
			_monitorSessionOpen = false;
			ResetCanHealthCounters();
			_connectButton.Text = "连接";
			ApplyButtonStyle(_connectButton, "connect");
			_statusLabel.Text = "未连接";
			_statusLabel.BackColor = _statusOff;
			Log("CAN 已断开。");
			WriteConnectionState("未连接");
			return;
		}
		try
		{
			if (!CanAdapterFactory.TryOpenAvailable(out ICanAdapter adapter, out string message, preferredName: _preferredAdapterName))
			{
				_connectButton.Text = "连接";
				ApplyButtonStyle(_connectButton, "connect");
				_statusLabel.Text = "未连接";
				_statusLabel.BackColor = _statusOff;
				Log("未连接：" + message);
				WriteConnectionState("未连接：" + message);
				RestorePureCodeViewAfterMonitoringStateChanged();
				return;
			}
			if (adapter == null)
			{
				throw new InvalidOperationException("未检测到 CAN 工具");
			}
			_adapter = adapter;
			ResetCanHealthCounters();
			_connectButton.Text = "断开";
			ApplyButtonStyle(_connectButton, "waiting");
			MarkWaitingForControllerResponse(adapter);
			if (!_running)
			{
				StartPolling();
			}
		}
		catch (Exception ex)
		{
			_adapter?.Dispose();
			_adapter = null;
			_monitorSessionOpen = false;
			ResetCanHealthCounters();
			_connectButton.Text = "连接";
			ApplyButtonStyle(_connectButton, "connect");
			Log("连接失败：" + ex.Message);
			WriteConnectionState("连接失败：" + ex);
			RestorePureCodeViewAfterMonitoringStateChanged();
		}
	}

	private void ToggleOfflineSimulation()
	{
		if (_offlineSimulation)
		{
			StopOfflineSimulation();
			return;
		}

		CodeViewSnapshot? codeViewSnapshot = CaptureCodeViewSnapshot();
		ProtectCodeViewport(1200);
		StopPolling(waitForExit: true, sendCloseFrame: false);
		ClearDebugForceStateForModeSwitch("切换到离线模式");
		_adapter?.Dispose();
		_adapter = null;
		_monitorSessionOpen = true;
		_offlineSimulation = true;
		Volatile.Write(ref _offlineRuntimePaused, 0);
		Interlocked.Exchange(ref _offlineStepRequests, 0);
		ResetCanHealthCounters();
		if (_connectButton != null)
		{
			_connectButton.Text = "连接";
			ApplyButtonStyle(_connectButton, "connect");
		}
		if (_simulationButton != null)
		{
			_simulationButton.Text = "退出离线";
			ApplyButtonStyle(_simulationButton, "working");
		}
		if (_statusLabel != null)
		{
			_statusLabel.Text = "离线循环";
			_statusLabel.BackColor = _gridHeader;
		}
		WriteConnectionState("离线模式");
		ClearOfflineSimulationProgramCache();
		Log("离线模式已开启：不连接 CAN；按应用层入口运行可识别的 C 逻辑。");
		if (!_running)
		{
			StartPolling();
		}
		RestoreCodeViewSnapshotLater(codeViewSnapshot, "offline-start");
	}

	private void StopOfflineSimulation()
	{
		if (!_offlineSimulation)
		{
			return;
		}

		StopPolling(waitForExit: true, sendCloseFrame: false);
		_offlineSimulation = false;
		Volatile.Write(ref _offlineRuntimePaused, 0);
		Interlocked.Exchange(ref _offlineStepRequests, 0);
		_monitorSessionOpen = false;
		ClearOfflineSimulationProgramCache();
		ClearDebugForceStateForModeSwitch("退出离线模式");
		if (_simulationButton != null)
		{
			_simulationButton.Text = "离线";
			ApplyButtonStyle(_simulationButton, "simulate");
		}
		if (_statusLabel != null)
		{
			_statusLabel.Text = "未连接";
			_statusLabel.BackColor = _statusOff;
		}
		WriteConnectionState("未连接");
		Log("离线模式已关闭。");
		RestorePureCodeViewAfterMonitoringStateChanged();
	}

	private void ClearDebugForceStateForModeSwitch(string reason)
	{
		int cleared = 0;
		foreach (WatchItem item in _watchItems)
		{
			if (!item.ForceActive && string.IsNullOrWhiteSpace(item.ForceText))
			{
				continue;
			}

			item.ForceActive = false;
			item.ForceText = "";
			RefreshForceCell(item);
			MarkFunctionCodeDirty(item);
			cleared++;
		}

		if (cleared > 0)
		{
			ClearScintillaValueDecorations();
			UpdateProgramInsightPanel();
			Log($"{reason}：已清空 {cleared} 个本地强制标记，离线/联机强制状态不继承。");
		}
	}

	private void WriteConnectionState(string message)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_connectionStatePath));
			File.WriteAllText(_connectionStatePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + message, new UTF8Encoding(false));
		}
		catch
		{
		}
	}

	private void RefreshStartupProgramGraph()
	{
		string directory = GetWorkDirectoryFromUi();
		if (directory.Length > 0 && Directory.Exists(directory))
		{
			RefreshProgramGraphPanel(directory);
		}
	}

	private void TogglePolling()
	{
		if (_running)
		{
			StopPolling();
		}
		else
		{
			StartPolling();
		}
	}

	private void PrepareVisiblePollingContext()
	{
		if (_currentFunctionSource == null || _functionCodePanel == null || !_functionCodePanel.Visible)
		{
			return;
		}

		try
		{
			(int Start, int End) visibleRange = GetVisibleSourceLineRange(DataMirrorPaddingLines);
			AutoWatchVariablesForVisibleRange(visibleRange);
			CapturePollPriorityForVisibleRange(visibleRange);
			UpdateVisibleValuesLabel(visibleRange);
		}
		catch (Exception ex)
		{
			LogWatchSnapshotError(ex);
		}
	}

	private void StartPolling()
	{
		if (_adapter == null && !_offlineSimulation)
		{
			ToggleConnect();
			if (_adapter == null || _running)
			{
				return;
			}
		}
		PrepareVisiblePollingContext();
		_pollCts = new CancellationTokenSource();
		_running = true;
		_startButton.Text = "停止监控";
		ApplyButtonStyle(_startButton, "monitorStop");
		UpdateCycleEstimate();
		MarkCodeValueRenderStateChanged();
		_monitorTxProbeRemaining = 3;
		ResetCanHealthCounters();
		if (_offlineSimulation)
		{
			EnsureOfflineApplicationWatchItems();
		}
		CancellationToken token = _pollCts.Token;
		_pollTask = Task.Run(async delegate
		{
			if (!_offlineSimulation && !SendMonitorSession(open: true))
			{
				StopPollingStateFromWorker("监控未启动：调试打开指令发送失败。");
				return;
			}
			if (_offlineSimulation)
			{
				_monitorSessionOpen = true;
			}
			await PollLoop(token).ConfigureAwait(continueOnCapturedContext: false);
		}, token);
		Log(_offlineSimulation ? "离线模式已启动：应用层入口会持续运行，代码窗口负责观察变量。" : "监控已启动。");
	}

	private void StopPolling(bool waitForExit = false, bool sendCloseFrame = true)
	{
		if (_running && sendCloseFrame)
		{
			SendMonitorSession(open: false);
		}
		CancellationTokenSource pollCts = _pollCts;
		Task pollTask = _pollTask;
		pollCts?.Cancel();
		if (waitForExit && pollTask != null)
		{
			try
			{
				pollTask.Wait(600);
			}
			catch (AggregateException ex) when (ex.InnerExceptions.All((Exception x) => x is OperationCanceledException || x is TaskCanceledException))
			{
			}
			catch (OperationCanceledException)
			{
			}
		}
		pollCts?.Dispose();
		_pollCts = null;
		_pollTask = null;
		_running = false;
		if (_startButton != null)
		{
			_startButton.Text = "开始监控";
			ApplyButtonStyle(_startButton, "monitorStart");
		}
		UpdateCycleEstimate();
		RestorePureCodeViewAfterMonitoringStateChanged();
	}

	private async Task PollLoop(CancellationToken token)
	{
		_ = 4;
		try
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					List<WatchItem> list = GetEnabledWatchSnapshot();
					if (list.Count == 0)
					{
						await PollTraceIfDue(token).ConfigureAwait(continueOnCapturedContext: false);
						await Task.Delay(100, token).ConfigureAwait(continueOnCapturedContext: false);
						continue;
					}
					int requestedCycleMs = Math.Max(10, Volatile.Read(ref _targetCycleMs));
					bool visibleOnlySnapshot = Volatile.Read(ref _lastSnapshotVisibleOnly) != 0;
					int targetCycleMs = visibleOnlySnapshot ? BuildVisibleEffectiveCycleMs(list.Count, requestedCycleMs) : requestedCycleMs;
					DateTime cycleStart = DateTime.UtcNow;
					Stopwatch cycleWatch = Stopwatch.StartNew();
					PollCycleStats stats;
					string performanceMode;
					if (_offlineSimulation)
					{
						stats = PollOfflineSimulation(list);
						performanceMode = stats.Sent > 0
							? (Volatile.Read(ref _offlineRuntimePaused) != 0 ? "offline-step" : "offline-loop")
							: "offline-paused";
					}
					else if (visibleOnlySnapshot && list.Count > 1)
					{
						stats = await PollVisibleBatch(list, targetCycleMs, token).ConfigureAwait(continueOnCapturedContext: false);
						performanceMode = "visible-batch";
					}
					else
					{
						int sent = 0;
						int success = 0;
						int timeout = 0;
						int skipped = 0;
						for (int i = 0; i < list.Count; i++)
						{
							if (token.IsCancellationRequested)
							{
								break;
							}
							DateTime itemStart = cycleStart.AddTicks(TimeSpan.FromMilliseconds((double)targetCycleMs * (double)i / (double)list.Count).Ticks);
							TimeSpan itemDelay = itemStart - DateTime.UtcNow;
							if (itemDelay.TotalMilliseconds >= 1.0)
							{
								await Task.Delay(itemDelay, token).ConfigureAwait(continueOnCapturedContext: false);
							}
							WatchItem item = list[i];
							if (ShouldDelayPollingItem(item, DateTime.UtcNow))
							{
								skipped++;
								continue;
							}
							sent++;
							if (await PollOne(item, token).ConfigureAwait(continueOnCapturedContext: false))
							{
								success++;
							}
							else
							{
								timeout++;
							}
						}
						stats = new PollCycleStats(list.Count, sent, success, timeout, skipped);
						performanceMode = visibleOnlySnapshot ? "visible-pipeline" : "serial";
					}
					await PollTraceIfDue(token).ConfigureAwait(continueOnCapturedContext: false);
					cycleWatch.Stop();
					LogPollPerformance(performanceMode, stats, cycleWatch.ElapsedMilliseconds, targetCycleMs);
					TimeSpan cycleDelay = TimeSpan.FromMilliseconds(targetCycleMs) - (DateTime.UtcNow - cycleStart);
					if (cycleDelay.TotalMilliseconds >= 1.0)
					{
						await Task.Delay(cycleDelay, token).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					LogPollLoopError(ex);
					await Task.Delay(80, token).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void StopPollingStateFromWorker(string message)
	{
		void Apply()
		{
			_running = false;
			_monitorSessionOpen = false;
			if (_startButton != null && !_startButton.IsDisposed)
			{
				_startButton.Text = "开始监控";
				ApplyButtonStyle(_startButton, "monitorStart");
			}
			UpdateCycleEstimate();
			RestorePureCodeViewAfterMonitoringStateChanged();
			Log(message);
		}

		try
		{
			if (IsHandleCreated && InvokeRequired)
			{
				BeginInvoke((Action)Apply);
			}
			else
			{
				Apply();
			}
		}
		catch
		{
			_running = false;
			_monitorSessionOpen = false;
		}
	}

	private List<WatchItem> GetEnabledWatchSnapshot()
	{
		try
		{
			Volatile.Write(ref _lastSnapshotVisibleOnly, 0);
			bool capacityLimited = IsWatchCapacityLimited();
			int limit = capacityLimited ? MaxWatchItems : int.MaxValue;
			List<WatchItem> list = new List<WatchItem>(capacityLimited ? Math.Min(MaxWatchItems, _watchItems.Count) : _watchItems.Count);
			Dictionary<string, WatchItem> byName = new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);
			int count = _watchItems.Count;
			for (int i = 0; i < count; i++)
			{
				WatchItem item = _watchItems[i];
				if (item.Enabled)
				{
					byName[item.Name] = item;
				}
			}

			var added = new HashSet<WatchItem>();
			void AddItem(WatchItem item)
			{
				if (list.Count >= limit)
				{
					return;
				}
				if (item.Enabled && added.Add(item))
				{
					list.Add(item);
				}
			}

			void AddByName(IEnumerable<string> names)
			{
				foreach (string name in names)
				{
					if (list.Count >= limit)
					{
						return;
					}
					if (byName.TryGetValue(name, out WatchItem? item))
					{
						AddItem(item);
					}
				}
			}

			List<string> visiblePriority;
			List<string> contextPriority;
			lock (_pollPriorityLock)
			{
				visiblePriority = _visiblePollPriorityNames.ToList();
				contextPriority = _contextPollPriorityNames.ToList();
			}

			AddByName(visiblePriority);
			if (!_offlineSimulation && visiblePriority.Count > 0 && list.Count > 0)
			{
				Volatile.Write(ref _lastSnapshotVisibleOnly, 1);
				return list;
			}

			if (_focusedVariableName.Length > 0)
			{
				AddByName(new[] { _focusedVariableName });
			}
			AddByName(contextPriority);
			for (int i = 0; i < count && list.Count < limit; i++)
			{
				AddItem(_watchItems[i]);
			}
			return list;
		}
		catch (Exception ex)
		{
			LogWatchSnapshotError(ex);
			return new List<WatchItem>();
		}
	}

	private bool SendMonitorSession(bool open)
	{
		if (_offlineSimulation)
		{
			_monitorSessionOpen = open;
			return true;
		}

		ICanAdapter adapter = _adapter;
		if (adapter == null)
		{
			return false;
		}
		if (open && _monitorSessionOpen)
		{
			return true;
		}
		if (!open && !_monitorSessionOpen)
		{
			return true;
		}
		byte seq = ++_seq;
		byte[] data = (open ? MonitorProtocol.BuildOpenRequest(seq) : MonitorProtocol.BuildCloseRequest(seq));
		if (!TrySendMonitorFrame(adapter, data, open ? "打开监控" : "关闭监控"))
		{
			return false;
		}
		_monitorSessionOpen = open;
		return true;
	}

	private bool TrySendMonitorFrame(ICanAdapter adapter, byte[] data, string purpose)
	{
		try
		{
			bool shouldProbe = _monitorTxProbeRemaining > 0;
			if (shouldProbe)
			{
				Log("准备发送监控请求：" + purpose + "。");
			}
			adapter.Send(new CanFrame(MonitorRequestId, 8, data));
			if (shouldProbe)
			{
				_monitorTxProbeRemaining--;
				Log("监控请求已发送：" + purpose + "。");
			}
			return true;
		}
		catch (Exception ex)
		{
			LogMonitorSendError(purpose, ex);
			return false;
		}
	}

	private void LogMonitorSendError(string purpose, Exception ex)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastMonitorSendErrorLogUtc).TotalSeconds < 2)
		{
			return;
		}
		_lastMonitorSendErrorLogUtc = now;
		Log("调试指令发送失败：" + purpose + "，" + ex.Message);
	}

	private void LogWatchSnapshotError(Exception ex)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastWatchSnapshotErrorLogUtc).TotalSeconds < 2)
		{
			return;
		}
		_lastWatchSnapshotErrorLogUtc = now;
		Log("监控列表读取冲突，已跳过本轮：" + ex.Message);
	}

	private void LogPollLoopError(Exception ex)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastPollLoopErrorLogUtc).TotalSeconds < 2)
		{
			return;
		}
		_lastPollLoopErrorLogUtc = now;
		Log("监控线程异常，已自动继续：" + ex.Message);
	}

	private void NoteCanResponse()
	{
		Interlocked.Exchange(ref _consecutiveCanNoResponseCount, 0);
		if (Interlocked.Exchange(ref _controllerResponded, 1) == 0)
		{
			MarkControllerResponded();
		}
	}

	private void NoteCanNoResponse(string reason)
	{
		if (!_running || _adapter == null)
		{
			return;
		}

		int count = Interlocked.Increment(ref _consecutiveCanNoResponseCount);
		if (count < NoResponseStopThreshold)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		if ((now - _lastNoResponseStopUtc).TotalMilliseconds < NoResponseStopCooldownMs)
		{
			return;
		}

		if (Interlocked.Exchange(ref _noResponseStopRequested, 1) != 0)
		{
			return;
		}

		_lastNoResponseStopUtc = now;
		try
		{
			if (!IsHandleCreated || IsDisposed)
			{
				Interlocked.Exchange(ref _noResponseStopRequested, 0);
				return;
			}

			BeginInvoke(new Action(() => _ = StopCanAfterNoResponseAsync(reason, count)));
		}
		catch
		{
			Interlocked.Exchange(ref _noResponseStopRequested, 0);
		}
	}

	private async Task StopCanAfterNoResponseAsync(string reason, int count)
	{
		try
		{
			if (_adapter == null)
			{
				return;
			}

			string adapterName = _adapter.Name;
			Log($"CAN 连续无回信，已停止监控：{reason}，{count} 次。请检查控制器电源、CAN 线、固件后手动连接。");
			StopPolling(waitForExit: false, sendCloseFrame: false);
			await Task.Delay(140).ConfigureAwait(true);
			if (_adapter != null)
			{
				try
				{
					_adapter.Dispose();
				}
				catch (Exception ex)
				{
					Log("释放 CAN 适配器失败：" + ex.Message);
				}
				_adapter = null;
			}
			_monitorSessionOpen = false;
			if (_connectButton != null && !_connectButton.IsDisposed)
			{
				_connectButton.Text = "连接";
				ApplyButtonStyle(_connectButton, "connect");
			}
			if (_statusLabel != null && !_statusLabel.IsDisposed)
			{
				_statusLabel.Text = "无回信";
				_statusLabel.BackColor = _statusOff;
			}
			WriteConnectionState("无回信，已停止监控：" + adapterName);
		}
		finally
		{
			ResetCanHealthCounters();
			Interlocked.Exchange(ref _noResponseStopRequested, 0);
		}
	}

	private void ResetCanHealthCounters()
	{
		Interlocked.Exchange(ref _consecutiveCanNoResponseCount, 0);
		Interlocked.Exchange(ref _controllerResponded, 0);
		lock (_pollBackoffLock)
		{
			_pollBackoff.Clear();
		}
	}

	private bool ShouldDelayPollingItem(WatchItem item, DateTime nowUtc)
	{
		lock (_pollBackoffLock)
		{
			if (!_pollBackoff.TryGetValue(item, out PollBackoffState? state))
			{
				return false;
			}
			if (state.MissCount < NoResponsePollBackoffStartMisses)
			{
				return false;
			}
			if (Volatile.Read(ref _consecutiveCanNoResponseCount) > 0)
			{
				return false;
			}
			return nowUtc < state.RetryAfterUtc;
		}
	}

	private void RegisterPollSuccess(WatchItem item)
	{
		lock (_pollBackoffLock)
		{
			_pollBackoff.Remove(item);
		}
	}

	private void RegisterPollTimeout(WatchItem item)
	{
		lock (_pollBackoffLock)
		{
			if (!_pollBackoff.TryGetValue(item, out PollBackoffState? state))
			{
				state = new PollBackoffState();
				_pollBackoff[item] = state;
			}

			state.MissCount++;
			if (state.MissCount >= NoResponsePollBackoffStartMisses)
			{
				int step = Math.Min(6, state.MissCount - NoResponsePollBackoffStartMisses + 1);
				int backoffMs = Math.Min(NoResponsePollBackoffMaxMs, NoResponsePollBackoffBaseMs * step);
				state.RetryAfterUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);
			}
		}
	}

	private void MarkWaitingForControllerResponse(ICanAdapter adapter)
	{
		if (_connectButton != null && !_connectButton.IsDisposed)
		{
			ApplyButtonStyle(_connectButton, "waiting");
		}
		if (_statusLabel != null)
		{
			_statusLabel.Text = "等待回信";
			_statusLabel.BackColor = _statusOff;
		}
		Log("CAN 工具已打开：" + adapter.Name + "，等待控制器回信。");
		WriteConnectionState("等待控制器回信：" + adapter.Name);
	}

	private void MarkControllerResponded()
	{
		void Apply()
		{
			ICanAdapter? adapter = _adapter;
			if (adapter == null)
			{
				return;
			}

			if (_statusLabel != null)
			{
				_statusLabel.Text = adapter.Name;
				_statusLabel.BackColor = _accent;
			}
			if (_connectButton != null && !_connectButton.IsDisposed)
			{
				_connectButton.Text = "断开";
				ApplyButtonStyle(_connectButton, "disconnect");
			}
			_preferredAdapterName = adapter.Name;
			Log("控制器已回信，连接成功：" + adapter.Name + "。");
			WriteConnectionState("已连接：" + adapter.Name);
		}

		try
		{
			if (IsHandleCreated && InvokeRequired)
			{
				BeginInvoke((Action)Apply);
			}
			else
			{
				Apply();
			}
		}
		catch
		{
		}
	}

	private async Task<bool> PollOne(WatchItem item, CancellationToken token)
	{
		if (_offlineSimulation)
		{
			UpdateItem(item, Math.Clamp(item.Size, 1, 4), 0, GetSimulatedValue(item));
			await Task.Yield();
			return true;
		}

		ICanAdapter adapter = _adapter;
		if (adapter == null)
		{
			return false;
		}
		try
		{
			if (item.Size == 4 && !item.IsExpandable)
			{
				ReadSegmentResult low = await TryReadSegment(adapter, item.Address, 2, token).ConfigureAwait(continueOnCapturedContext: false);
				if (!low.Success)
				{
					RegisterPollTimeout(item);
					MarkTimeout(item);
					return false;
				}
				if (low.Status != 0)
				{
					RegisterPollSuccess(item);
					UpdateItem(item, low.Len, low.Status, low.Value);
					return true;
				}
				ReadSegmentResult readSegmentResult = await TryReadSegment(adapter, item.Address + 2, 2, token).ConfigureAwait(continueOnCapturedContext: false);
				if (!readSegmentResult.Success)
				{
					RegisterPollTimeout(item);
					MarkTimeout(item);
					return false;
				}
				if (readSegmentResult.Status != 0)
				{
					RegisterPollSuccess(item);
					UpdateItem(item, readSegmentResult.Len, readSegmentResult.Status, readSegmentResult.Value);
					return true;
				}
				uint value = (uint)(low.Value | (readSegmentResult.Value << 16));
				RegisterPollSuccess(item);
				UpdateItem(item, 4, 0, value);
				return true;
			}
			int len = Math.Clamp(item.Size, 1, 2);
			ReadSegmentResult readSegmentResult2 = await TryReadSegment(adapter, item.Address, len, token).ConfigureAwait(continueOnCapturedContext: false);
			if (readSegmentResult2.Success)
			{
				RegisterPollSuccess(item);
				UpdateItem(item, readSegmentResult2.Len, readSegmentResult2.Status, readSegmentResult2.Value);
				return true;
			}
			RegisterPollTimeout(item);
			MarkTimeout(item);
			return false;
		}
		catch (Exception ex)
		{
			RegisterPollTimeout(item);
			MarkError(item, ex.Message);
			await Task.Delay(100, token).ConfigureAwait(continueOnCapturedContext: false);
			return false;
		}
	}

	private async Task<PollCycleStats> PollVisibleBatch(IReadOnlyList<WatchItem> items, int targetCycleMs, CancellationToken token)
	{
		ICanAdapter adapter = _adapter;
		if (adapter == null)
		{
			return new PollCycleStats(items.Count, 0, 0, 0, items.Count);
		}

		int itemCount = items.Count;
		int rawStartIndex = Volatile.Read(ref _visibleBatchStartIndex);
		int startIndex = itemCount > 0
			? ((rawStartIndex % itemCount) + itemCount) % itemCount
			: 0;
		int skipped = 0;
		int sentFrames = 0;
		bool anyReadAck = false;
		var states = new Dictionary<WatchItem, BatchReadState>();
		var queue = new Queue<BatchReadRequest>();
		var pending = new Dictionary<byte, (BatchReadRequest Request, DateTime Deadline)>();

		void QueueRead(WatchItem item, uint address, int len, int offset)
		{
			queue.Enqueue(new BatchReadRequest(item, 0, address, len, offset));
		}

		for (int i = 0; i < itemCount; i++)
		{
			WatchItem item = items[(startIndex + i) % itemCount];
			if (ShouldDelayPollingItem(item, DateTime.UtcNow))
			{
				skipped++;
				continue;
			}

			var state = new BatchReadState
			{
				Item = item,
				IsFourByte = item.Size == 4 && !item.IsExpandable
			};
			states[item] = state;

			if (state.IsFourByte)
			{
				QueueRead(item, item.Address, 2, 0);
				QueueRead(item, item.Address + 2, 2, 2);
			}
			else
			{
				QueueRead(item, item.Address, Math.Clamp(item.Size, 1, 2), 0);
			}
		}

		await _canRequestLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(VisibleMirrorMinCycleMs, targetCycleMs) - 5);
			int idlePasses = 0;

			bool SendNextRequest()
			{
				if (queue.Count == 0 || token.IsCancellationRequested)
				{
					return false;
				}

				BatchReadRequest template = queue.Dequeue();
				byte seq = ++_seq;
				byte[] data = MonitorProtocol.BuildReadRequest(seq, template.Address, template.Len);
				if (!TrySendMonitorFrame(adapter, data, "读取变量"))
				{
					return false;
				}

				var request = new BatchReadRequest(template.Item, seq, template.Address, template.Len, template.Offset);
				pending[seq] = (request, DateTime.UtcNow.AddMilliseconds(VisiblePipelineRequestTimeoutMs));
				if (states.TryGetValue(template.Item, out BatchReadState? state))
				{
					if (template.Offset == 0)
					{
						state.LowSent = true;
					}
					else
					{
						state.HighSent = true;
					}
				}
				sentFrames++;
				return true;
			}

			int pipelineLimit = Math.Clamp(Volatile.Read(ref _visiblePipelineLimit), 1, VisiblePipelineMaxInFlight);

			void FillPipeline()
			{
				while (pending.Count < pipelineLimit && queue.Count > 0 && DateTime.UtcNow < deadline && !token.IsCancellationRequested)
				{
					SendNextRequest();
				}
			}

			void ExpireTimedOutRequests()
			{
				DateTime now = DateTime.UtcNow;
				if (pending.Count == 0)
				{
					return;
				}

				foreach (byte seq in pending.Where(pair => now >= pair.Value.Deadline).Select(pair => pair.Key).ToList())
				{
					pending.Remove(seq);
				}
			}

			FillPipeline();
			while ((pending.Count > 0 || queue.Count > 0) && DateTime.UtcNow < deadline && !token.IsCancellationRequested)
			{
				ExpireTimedOutRequests();
				FillPipeline();

				int scanned = 0;
				bool receivedAnyFrame = false;
				while (scanned++ < 128 && DateTime.UtcNow < deadline && !token.IsCancellationRequested && adapter.TryReceive(out CanFrame frame))
				{
					receivedAnyFrame = true;
					if (frame.Id != MonitorResponseId || frame.Dlc < 8 || frame.Data.Length < 8)
					{
						continue;
					}

					byte seq = frame.Data[1];
					if (!pending.TryGetValue(seq, out var pendingRequest) ||
						!MonitorProtocol.TryParseReadAck(frame, seq, out int len, out byte status, out ushort value))
					{
						continue;
					}

					BatchReadRequest request = pendingRequest.Request;
					pending.Remove(seq);
					anyReadAck = true;
					NoteCanResponse();
					if (!states.TryGetValue(request.Item, out BatchReadState? state))
					{
						continue;
					}

					var result = new ReadSegmentResult(Success: true, len, status, value);
					if (request.Offset == 0)
					{
						state.Low = result;
						state.LowDone = true;
					}
					else
					{
						state.High = result;
						state.HighDone = true;
					}

					FillPipeline();
				}

				if (pending.Count == 0 && queue.Count == 0)
				{
					break;
				}

				if (receivedAnyFrame)
				{
					idlePasses = 0;
					continue;
				}

				idlePasses++;
				if (idlePasses <= 8)
				{
					Thread.Sleep(0);
					continue;
				}

				await Task.Delay(idlePasses <= 18 ? 1 : 2, token).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		finally
		{
			_canRequestLock.Release();
		}

		int success = 0;
		int timeout = 0;
		int deferred = 0;
		foreach (BatchReadState state in states.Values)
		{
			BatchApplyResult result = ApplyBatchReadState(state);
			if (result == BatchApplyResult.Success)
			{
				success++;
			}
			else if (result == BatchApplyResult.Timeout)
			{
				timeout++;
			}
			else
			{
				deferred++;
			}
		}

		if (itemCount > 1 && states.Count > 0)
		{
			int completedOrAttempted = Math.Max(1, success + timeout);
			int nextIndex = (startIndex + completedOrAttempted) % itemCount;
			Volatile.Write(ref _visibleBatchStartIndex, nextIndex);
		}

		if (sentFrames > 0 && !anyReadAck && success == 0 && timeout > 0)
		{
			NoteCanNoResponse("批量读取变量");
		}

		AdaptVisiblePipelineWindow(states.Count, success, timeout);

		return new PollCycleStats(items.Count, sentFrames, success, timeout, skipped + deferred);
	}

	private void AdaptVisiblePipelineWindow(int attemptedItems, int success, int timeout)
	{
		if (attemptedItems <= 0)
		{
			return;
		}

		int current = Math.Clamp(Volatile.Read(ref _visiblePipelineLimit), 1, VisiblePipelineMaxInFlight);
		if (timeout > 0 && success * 100 < attemptedItems * 95)
		{
			Volatile.Write(ref _visiblePipelineLimit, 1);
			Interlocked.Exchange(ref _visiblePipelineGoodCycles, 0);
			return;
		}

		if (timeout == 0 && success >= attemptedItems)
		{
			int goodCycles = Interlocked.Increment(ref _visiblePipelineGoodCycles);
			if (goodCycles >= 14 && current < VisiblePipelineMaxInFlight)
			{
				Volatile.Write(ref _visiblePipelineLimit, current + 1);
				Interlocked.Exchange(ref _visiblePipelineGoodCycles, 0);
			}
			return;
		}

		Interlocked.Exchange(ref _visiblePipelineGoodCycles, 0);
	}

	private BatchApplyResult ApplyBatchReadState(BatchReadState state)
	{
		WatchItem item = state.Item;
		if (!state.IsFourByte)
		{
			if (!state.LowSent)
			{
				return BatchApplyResult.Deferred;
			}

			if (!state.LowDone)
			{
				RegisterPollTimeout(item);
				MarkTimeout(item);
				return BatchApplyResult.Timeout;
			}

			RegisterPollSuccess(item);
			UpdateItem(item, state.Low.Len, state.Low.Status, state.Low.Value);
			return BatchApplyResult.Success;
		}

		if (state.LowDone && state.Low.Status != 0)
		{
			RegisterPollSuccess(item);
			UpdateItem(item, state.Low.Len, state.Low.Status, state.Low.Value);
			return BatchApplyResult.Success;
		}

		if (state.HighDone && state.High.Status != 0)
		{
			RegisterPollSuccess(item);
			UpdateItem(item, state.High.Len, state.High.Status, state.High.Value);
			return BatchApplyResult.Success;
		}

		if (!state.LowSent && !state.HighSent)
		{
			return BatchApplyResult.Deferred;
		}

		if ((state.LowSent && !state.LowDone) || (state.HighSent && !state.HighDone))
		{
			RegisterPollTimeout(item);
			MarkTimeout(item);
			return BatchApplyResult.Timeout;
		}

		if (!state.LowDone || !state.HighDone)
		{
			return BatchApplyResult.Deferred;
		}

		uint value = (uint)(state.Low.Value | (state.High.Value << 16));
		RegisterPollSuccess(item);
		UpdateItem(item, 4, 0, value);
		return BatchApplyResult.Success;
	}

	private static int BuildVisibleEffectiveCycleMs(int visibleCount, int requestedCycleMs)
	{
		int autoCycleMs = Math.Clamp(visibleCount * 5, VisibleMirrorMinCycleMs, VisibleMirrorMaxAutoCycleMs);
		return Math.Max(requestedCycleMs, autoCycleMs);
	}

	private uint NextSimulatedValue(WatchItem item)
	{
		int bytes = Math.Clamp(item.Size, 1, 4);
		string key = GetSimulationKey(item);
		lock (_simulationLock)
		{
			if (!_simulatedValues.TryGetValue(key, out uint value))
			{
				value = MaskRawValue(item.Address ^ (uint)StableNameSeed(item.Name), bytes);
			}

			if (!item.ForceActive)
			{
				value = MaskRawValue(value + (uint)(StableNameSeed(item.Name) % 5 + 1), bytes);
				_simulatedValues[key] = value;
			}

			return value;
		}
	}

	private uint GetSimulatedValue(WatchItem item)
	{
		int bytes = Math.Clamp(item.Size, 1, 4);
		string key = GetSimulationKey(item);
		lock (_simulationLock)
		{
			if (!_simulatedValues.TryGetValue(key, out uint value))
			{
				value = 0;
				_simulatedValues[key] = value;
			}

			return MaskRawValue(value, bytes);
		}
	}

	private void ClearOfflineSimulationProgramCache()
	{
		lock (_simulationLock)
		{
			_offlineCDrivers.Clear();
			_offlineApplicationSources = new List<FunctionSourceView>();
			_offlineApplicationSourceDirectory = "";
			_offlineProgramModel = null;
			_offlineCWorkerSignature = "";
			_offlineCWorkerCoverageLogged.Clear();
		}
		_offlineCWorker?.Dispose();
		_offlineCWorker = null;
	}

	private void EnsureOfflineApplicationWatchItems()
	{
		if (!_offlineSimulation || _symbolLookup.Count == 0)
		{
			return;
		}

		IReadOnlyList<FunctionSourceView> sources = GetOfflineApplicationSources();
		if (sources.Count == 0)
		{
			return;
		}

		HashSet<string> existing = BuildExistingWatchAliasSet();
		int added = 0;
		foreach (string identifier in sources
			.SelectMany(source => BuildFunctionAutoWatchIdentifiers(source.Lines))
			.Where(identifier => identifier.Length > 0 && !IsCKeywordToken(identifier))
			.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!TryResolveSourceIdentifierToWatchItem(identifier, existing, out WatchItem item, out string matchedName))
			{
				continue;
			}

			item.AutoVisible = true;
			_watchItems.Add(item);
			AddWatchAliases(existing, item.Name);
			existing.Add(matchedName);
			added++;
		}

		if (added == 0)
		{
			return;
		}

		UpdateCycleEstimate();
		SaveDefaultProfileQuietly();
		MarkFunctionCodeDirty();
		DateTime now = DateTime.UtcNow;
		if ((now - _lastOfflineApplicationWatchLogUtc).TotalSeconds >= 2)
		{
			_lastOfflineApplicationWatchLogUtc = now;
			Log($"离线应用层：已自动加入 {added} 个入口变量。");
		}
	}

	private IReadOnlyList<FunctionSourceView> GetOfflineApplicationSources()
	{
		string directory = _workDirectory;
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return _currentFunctionSource == null
				? Array.Empty<FunctionSourceView>()
				: new[] { _currentFunctionSource };
		}

		lock (_simulationLock)
		{
			if (_offlineApplicationSources.Count > 0 &&
				_offlineApplicationSourceDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase))
			{
				return _offlineApplicationSources.ToList();
			}
		}

		List<FunctionSourceView> sources = BuildOfflineApplicationSources(directory);
		if (sources.Count == 0 && _currentFunctionSource != null)
		{
			sources.Add(_currentFunctionSource);
		}

		lock (_simulationLock)
		{
			_offlineApplicationSourceDirectory = directory;
			_offlineApplicationSources = sources;
			return _offlineApplicationSources.ToList();
		}
	}

	private OfflineProgramModel? GetOfflineProgramModel()
	{
		string directory = _workDirectory;
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return null;
		}

		string signature = BuildOfflineProgramModelSignature(directory);
		lock (_simulationLock)
		{
			if (_offlineProgramModel != null &&
				_offlineProgramModel.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
				_offlineProgramModel.Signature.Equals(signature, StringComparison.Ordinal))
			{
				return _offlineProgramModel;
			}
		}

		List<FunctionSourceView> roots = BuildOfflineApplicationRootSources(directory);
		List<FunctionSourceView> sourceSeeds = BuildOfflineApplicationSources(directory);
		List<FunctionSourceView> sources = ExpandOfflineReachableSources(directory, sourceSeeds, 800);
		List<WatchItem> bindings = BuildOfflineGlobalBindings(sources);
		Dictionary<string, WatchItem> aliases = BuildWatchAliasMap(bindings);
		Dictionary<string, IReadOnlyList<OfflineWriteTrace>> traces = BuildOfflineWriteTraceIndex(sources, aliases);
		OfflineProgramModel model = new OfflineProgramModel(directory, signature, roots, sources, bindings, aliases, traces);

		lock (_simulationLock)
		{
			_offlineProgramModel = model;
		}

		string rootNames = string.Join(", ", roots.Select(root => root.FunctionName).Distinct(StringComparer.OrdinalIgnoreCase));
		Log($"离线模型：入口 {roots.Count} 个 [{rootNames}]，可达函数 {sources.Count} 个，全局变量 {bindings.Count} 个，写入变量 {traces.Count} 个。");
		return model;
	}

	private string BuildOfflineProgramModelSignature(string directory)
	{
		return string.Join("|",
		[
			directory,
			_mapFilePath,
			_mapLastWrite.Ticks.ToString(CultureInfo.InvariantCulture),
			_symbols.Count.ToString(CultureInfo.InvariantCulture),
			_programGraphLastSourceWrite.Ticks.ToString(CultureInfo.InvariantCulture),
			_functionIndexRoot,
			_offlineRootSelectionText
		]);
	}

	private List<FunctionSourceView> ExpandOfflineReachableSources(string directory, IReadOnlyList<FunctionSourceView> roots, int limit)
	{
		var result = new List<FunctionSourceView>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var pending = new Queue<FunctionSourceView>();

		void Add(FunctionSourceView source)
		{
			string key = source.FilePath + "|" + source.FunctionName;
			if (seen.Add(key))
			{
				pending.Enqueue(source);
			}
		}

		foreach (FunctionSourceView root in roots)
		{
			if (IsOfflineApplicationSourceFile(directory, root.FilePath))
			{
				Add(root);
			}
		}

		while (pending.Count > 0 && result.Count < Math.Max(16, limit))
		{
			FunctionSourceView source = pending.Dequeue();
			result.Add(source);
			foreach (string functionName in FindOfflineCalledFunctionNames(source.Lines))
			{
				if (IsMonitorInternalFunctionName(functionName) || IsCKeyword(functionName))
				{
					continue;
				}
				if (TryLoadFunctionSource(directory, functionName, out FunctionSourceView? child) &&
					child != null &&
					IsOfflineApplicationSourceFile(directory, child.FilePath))
				{
					Add(child);
				}
			}
		}

		return result;
	}

	private List<WatchItem> BuildOfflineGlobalBindings(IReadOnlyList<FunctionSourceView> sources)
	{
		var byKey = new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);
		foreach (FunctionSourceView source in sources)
		{
			foreach (string identifier in BuildIdentifierList(source.Lines))
			{
				if (identifier.Length == 0 || IsCKeywordToken(identifier))
				{
					continue;
				}
				if (!TryResolveOfflineSourceIdentifierToWatchItem(identifier, out WatchItem item))
				{
					continue;
				}
				string key = GetSimulationKey(item);
				if (!byKey.ContainsKey(key))
				{
					byKey.Add(key, item);
				}
			}
		}

		return byKey.Values
			.OrderBy(item => item.Address)
			.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private bool TryResolveOfflineSourceIdentifierToWatchItem(string identifier, out WatchItem item)
	{
		item = null!;
		if (identifier.Length == 0)
		{
			return false;
		}

		if (!TryResolveOfflineMapSymbol(identifier, out MapSymbol? symbol) || symbol == null)
		{
			return false;
		}

		if (symbol.Size <= 0 || symbol.Size > 4)
		{
			return false;
		}

		return KeilMapParser.TryResolve(symbol.Name, _symbolLookup, out item, out _);
	}

	private bool TryResolveOfflineMapSymbol(string identifier, out MapSymbol? symbol)
	{
		if (_symbolLookup.TryGetValue(identifier, out symbol))
		{
			return true;
		}

		List<MapSymbol> baseMatches = _symbols
			.Where(candidate => GetIdentifierBase(candidate.Name).Equals(identifier, StringComparison.OrdinalIgnoreCase))
			.Take(2)
			.ToList();
		if (baseMatches.Count == 1)
		{
			symbol = baseMatches[0];
			return true;
		}

		List<MapSymbol> tailMatches = _symbols
			.Where(candidate => GetIdentifierTail(candidate.Name).Equals(identifier, StringComparison.OrdinalIgnoreCase))
			.Take(2)
			.ToList();
		if (tailMatches.Count == 1)
		{
			symbol = tailMatches[0];
			return true;
		}

		symbol = null;
		return false;
	}

	private Dictionary<string, IReadOnlyList<OfflineWriteTrace>> BuildOfflineWriteTraceIndex(
		IReadOnlyList<FunctionSourceView> sources,
		Dictionary<string, WatchItem> aliases)
	{
		var traces = new Dictionary<string, List<OfflineWriteTrace>>(StringComparer.OrdinalIgnoreCase);
		foreach (FunctionSourceView source in sources)
		{
			for (int i = 0; i < source.Lines.Count; i++)
			{
				foreach ((WatchItem item, string operation) in FindOfflineWriteOperations(source.Lines[i], aliases))
				{
					string key = GetSimulationKey(item);
					if (!traces.TryGetValue(key, out List<OfflineWriteTrace>? list))
					{
						list = new List<OfflineWriteTrace>();
						traces[key] = list;
					}
					list.Add(new OfflineWriteTrace(source.FunctionName, source.FilePath, source.StartLine + i, operation));
				}
			}
		}

		return traces.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<OfflineWriteTrace>)pair.Value,
			StringComparer.OrdinalIgnoreCase);
	}

	private IEnumerable<(WatchItem Item, string Operation)> FindOfflineWriteOperations(string rawLine, Dictionary<string, WatchItem> aliases)
	{
		string line = StripLineComment(rawLine);
		if (line.Length == 0)
		{
			yield break;
		}

		foreach (Match match in Regex.Matches(line, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op>\+\+|--)\b"))
		{
			string name = match.Groups["name"].Value;
			if (TryResolveOfflineWatch(name, aliases, out WatchItem item))
			{
				yield return (item, name + match.Groups["op"].Value);
			}
		}

		foreach (Match match in Regex.Matches(line, @"(?<![=!<>])\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op>\+=|-=|=)\s*(?!=)"))
		{
			string name = match.Groups["name"].Value;
			if (IsLikelyLocalDeclarationAssignment(line, name))
			{
				continue;
			}
			if (TryResolveOfflineWatch(name, aliases, out WatchItem item))
			{
				yield return (item, name + " " + match.Groups["op"].Value);
			}
		}
	}

	private static bool IsLikelyLocalDeclarationAssignment(string line, string name)
	{
		string prefix = line;
		int index = line.IndexOf(name, StringComparison.Ordinal);
		if (index >= 0)
		{
			prefix = line[..index];
		}
		return Regex.IsMatch(prefix, @"(^|[;{]\s*)(?:static\s+|const\s+|volatile\s+|register\s+)*(?:unsigned\s+|signed\s+|short\s+|long\s+)*(?:char|int|float|double|bool|u8|u16|u32|s8|s16|s32|uint8_t|uint16_t|uint32_t|int8_t|int16_t|int32_t|BYTE|WORD|DWORD)\s+$", RegexOptions.IgnoreCase);
	}

	private static IEnumerable<string> FindOfflineCalledFunctionNames(IEnumerable<string> lines)
	{
		var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in lines)
		{
			string code = StripLineComment(line);
			foreach (Match match in Regex.Matches(code, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
			{
				string name = match.Groups["name"].Value;
				if (!IsCKeyword(name) && yielded.Add(name))
				{
					yield return name;
				}
			}
		}
	}

	private List<FunctionSourceView> BuildOfflineApplicationRootSources(string directory)
	{
		return BuildOfflineApplicationSources(directory, includeAnalysisSeeds: false);
	}

	private List<FunctionSourceView> BuildOfflineApplicationSources(string directory)
	{
		return BuildOfflineApplicationSources(directory, includeAnalysisSeeds: true);
	}

	private List<FunctionSourceView> BuildOfflineApplicationSources(string directory, bool includeAnalysisSeeds)
	{
		List<OfflineRootCandidate> candidates = BuildOfflineRootCandidates(directory, includeAnalysisSeeds);
		var sources = new List<FunctionSourceView>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int limit = includeAnalysisSeeds ? 36 : 12;
		foreach (OfflineRootCandidate candidate in candidates)
		{
			if (sources.Count >= limit)
			{
				break;
			}
			if (seen.Contains(candidate.FunctionName))
			{
				continue;
			}
			if (TryLoadFunctionSource(directory, candidate.FunctionName, out FunctionSourceView? source) && source != null)
			{
				seen.Add(candidate.FunctionName);
				sources.Add(source);
			}
		}

		if (sources.Count == 0 && _currentFunctionSource != null)
		{
			sources.Add(_currentFunctionSource);
			LogOfflineCWorkerIssue("离线静态模式：未自动确认应用入口，已使用当前函数作为观察入口。", force: true);
		}
		else if (sources.Count == 0)
		{
			LogOfflineCWorkerIssue("离线静态模式：未自动确认应用入口；请从候选入口中选择后再启用仿真。", force: true);
		}

		return sources;
	}

	private List<OfflineRootCandidate> BuildOfflineRootCandidates(string directory, bool includeAnalysisSeeds)
	{
		var candidates = new Dictionary<string, OfflineRootCandidate>(StringComparer.OrdinalIgnoreCase);

		void AddCandidate(string functionName, string reason, int score)
		{
			if (string.IsNullOrWhiteSpace(functionName) || IsCKeyword(functionName))
			{
				return;
			}
			if (!TryLoadFunctionSource(directory, functionName, out FunctionSourceView? source) || source == null)
			{
				return;
			}
			if (!IsOfflineApplicationSourceFile(directory, source.FilePath))
			{
				return;
			}
			if (!CanCallOfflineRootWithoutArguments(source))
			{
				return;
			}

			if (candidates.TryGetValue(source.FunctionName, out OfflineRootCandidate existing) &&
				existing.Score >= score)
			{
				return;
			}

			candidates[source.FunctionName] = new OfflineRootCandidate(source.FunctionName, reason, score);
		}

		foreach (string configuredRoot in GetConfiguredOfflineRootNames())
		{
			AddCandidate(configuredRoot, "项目配置", 1000);
		}

		if (_currentFunctionSource != null &&
			_currentFunctionSource.FilePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
		{
			AddCandidate(_currentFunctionSource.FunctionName, "当前观察函数", includeAnalysisSeeds ? 55 : 35);
		}

		foreach (string name in FindOfflineDisplayTaskEntries(directory))
		{
			AddCandidate(name, "任务函数指针赋值", 70);
		}

		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot != null && snapshot.Success)
		{
			IReadOnlyList<ProgramCallGraphNode> graphNodes = GetAllGraphNodes(snapshot);
			foreach (ProgramCallGraphNode node in graphNodes)
			{
				if (IsProgramGraphNoiseNode(node))
				{
					continue;
				}
				int score = ScoreOfflineRootCandidate(node, includeAnalysisSeeds);
				if (score >= (includeAnalysisSeeds ? 35 : 45))
				{
					AddCandidate(node.Name, OfflineRootReason(node), score);
				}
			}

			if (!string.IsNullOrWhiteSpace(snapshot.StartFunction))
			{
				int startScore = snapshot.StartFunction.Equals("main", StringComparison.OrdinalIgnoreCase)
					? 25
					: (includeAnalysisSeeds ? 65 : 80);
				AddCandidate(snapshot.StartFunction, "程序图谱首选入口", startScore);
			}

			foreach (ProgramFrameworkStep step in snapshot.FrameworkSteps)
			{
				AddCandidate(step.FunctionName, "框架步骤", includeAnalysisSeeds ? 52 : 60);
				if (includeAnalysisSeeds)
				{
					AddCandidate(step.Name, "框架步骤别名", 38);
				}
			}

			if (includeAnalysisSeeds)
			{
				foreach (ProgramFunctionInfo function in snapshot.HotFunctions.Take(24))
				{
					AddCandidate(function.Name, "高频调用函数", 36 + Math.Min(function.Outgoing, 12));
				}
			}
		}

		if (candidates.Count == 0)
		{
			EnsureFunctionIndex(directory);
			foreach (FunctionIndexEntry entry in _functionIndex.Values
				.Select(entry => new { Entry = entry, Score = ScoreOfflineRootName(entry.Name, entry.FilePath) })
				.Where(item => item.Score >= 30)
				.OrderByDescending(item => item.Score)
				.ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase)
				.Take(24)
				.Select(item => item.Entry))
			{
				AddCandidate(entry.Name, "源码函数名候选", 30);
			}
		}

		List<OfflineRootCandidate> result = candidates.Values
			.OrderByDescending(candidate => candidate.Score)
			.ThenBy(candidate => candidate.FunctionName, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (result.Count > 0)
		{
			string summary = string.Join(", ", result.Take(8).Select(candidate => candidate.FunctionName + ":" + candidate.Reason));
			LogOfflineCWorkerIssue("离线入口候选：" + summary, force: false);
		}
		return result;
	}

	private void RefreshOfflineRootCandidatesUi(string directory)
	{
		if (_offlineRootBox == null || _offlineRootBox.IsDisposed)
		{
			return;
		}

		_offlineRootSelectionUpdating = true;
		try
		{
			string currentText = GetOfflineRootSelectionDisplayText();
			_offlineRootBox.Items.Clear();
			_offlineRootBox.Items.Add("自动入口");
			foreach (string configuredRoot in GetConfiguredOfflineRootNames())
			{
				if (!_offlineRootBox.Items.Contains(configuredRoot))
				{
					_offlineRootBox.Items.Add(configuredRoot);
				}
			}
			if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
			{
				foreach (OfflineRootCandidate candidate in BuildOfflineRootCandidates(directory, includeAnalysisSeeds: false).Take(24))
				{
					if (!_offlineRootBox.Items.Contains(candidate.FunctionName))
					{
						_offlineRootBox.Items.Add(candidate.FunctionName);
					}
				}
			}
			_offlineRootBox.Text = currentText;
		}
		finally
		{
			_offlineRootSelectionUpdating = false;
		}
	}

	private void CommitOfflineRootSelectionFromUi()
	{
		if (_offlineRootSelectionUpdating || _offlineRootBox == null)
		{
			return;
		}

		string normalized = NormalizeOfflineRootSelectionText(_offlineRootBox.Text);
		if (_offlineRootSelectionText.Equals(normalized, StringComparison.Ordinal))
		{
			return;
		}

		_offlineRootSelectionText = normalized;
		ApplyOfflineRootSelectionToUi();
		ClearOfflineSimulationProgramCache();
		SaveDefaultProfileQuietly();
		Log(_offlineRootSelectionText.Length == 0
			? "离线入口：自动发现。"
			: "离线入口：使用项目配置 " + _offlineRootSelectionText + "。");
	}

	private void ApplyOfflineRootSelectionToUi()
	{
		if (_offlineRootBox == null || _offlineRootBox.IsDisposed)
		{
			return;
		}

		_offlineRootSelectionUpdating = true;
		try
		{
			_offlineRootBox.Text = GetOfflineRootSelectionDisplayText();
		}
		finally
		{
			_offlineRootSelectionUpdating = false;
		}
	}

	private string GetOfflineRootSelectionDisplayText()
	{
		return _offlineRootSelectionText.Length == 0 ? "自动入口" : _offlineRootSelectionText;
	}

	private IReadOnlyList<string> GetConfiguredOfflineRootNames()
	{
		return ParseOfflineRootNames(_offlineRootSelectionText);
	}

	private static string NormalizeOfflineRootSelectionText(string text)
	{
		return string.Join(", ", ParseOfflineRootNames(text));
	}

	private static List<string> ParseOfflineRootNames(string text)
	{
		if (string.IsNullOrWhiteSpace(text) ||
			text.Trim().Equals("自动入口", StringComparison.OrdinalIgnoreCase) ||
			text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return new List<string>();
		}

		return Regex.Split(text, @"[,，;；\r\n]+")
			.Select(part => part.Trim())
			.Where(part => part.Length > 0 && Regex.IsMatch(part, @"^[A-Za-z_][A-Za-z0-9_]*$"))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static bool CanCallOfflineRootWithoutArguments(FunctionSourceView source)
	{
		string joined = string.Join(" ", source.Lines.Take(12));
		int brace = joined.IndexOf('{');
		if (brace >= 0)
		{
			joined = joined[..brace];
		}
		string escapedName = Regex.Escape(source.FunctionName);
		Match match = Regex.Match(joined, @"\b" + escapedName + @"\s*\((?<params>[^)]*)\)");
		return !match.Success || IsEmptyOfflineParameterList(match.Groups["params"].Value);
	}

	private static bool IsEmptyOfflineParameterList(string parameters)
	{
		string text = Regex.Replace(parameters ?? "", @"\s+", "");
		return text.Length == 0 || text.Equals("void", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsOfflineApplicationSourceFile(string root, string filePath)
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

		string relative = GetRelativePathSafe(root, filePath).Replace('\\', '/');
		string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
		string[] excludedDirectories =
		{
			"bsp", "driver", "drivers", "cmsis", "startup", "system", "hal", "rte", "core",
			"periph", "peripheral", "uart", "usart", "adc", "gpio", "can", "timer", "tim",
			"eeprom", "flash", "i2c", "spi", "pwm", "usb", "eth", "objects", "listings"
		};
		if (segments.Take(Math.Max(0, segments.Length - 1)).Any(segment =>
			excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}

		string fileName = Path.GetFileNameWithoutExtension(filePath);
		string[] excludedPrefixes =
		{
			"startup", "system_lpc17", "core_cm", "lpc17xx", "bsp", "driver",
			"gpio", "uart", "usart", "adc", "can", "timer", "tim", "eeprom", "flash",
			"i2c", "spi", "pwm"
		};
		return !excludedPrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
	}

	private static int ScoreOfflineRootCandidate(ProgramCallGraphNode node, bool includeAnalysisSeeds)
	{
		int score = ScoreOfflineRootName(node.Name, node.FilePath);
		if (node.Incoming == 0 && node.Outgoing > 0)
		{
			score += 35;
		}
		if (node.Outgoing >= 2)
		{
			score += Math.Min(25, node.Outgoing * 3);
		}
		if (node.Kind.Equals("main", StringComparison.OrdinalIgnoreCase))
		{
			score += 35;
		}
		if (node.Kind.Equals("period10", StringComparison.OrdinalIgnoreCase) ||
			node.Kind.Equals("timer", StringComparison.OrdinalIgnoreCase) ||
			node.Kind.Equals("business", StringComparison.OrdinalIgnoreCase) ||
			node.Kind.Equals("io", StringComparison.OrdinalIgnoreCase))
		{
			score += 28;
		}
		if (node.Kind.Equals("disp", StringComparison.OrdinalIgnoreCase))
		{
			score += includeAnalysisSeeds ? 28 : 18;
		}
		if (node.Kind.Equals("driver", StringComparison.OrdinalIgnoreCase) ||
			node.Kind.Equals("storage", StringComparison.OrdinalIgnoreCase))
		{
			score -= 45;
		}
		return score;
	}

	private static int ScoreOfflineRootName(string functionName, string filePath)
	{
		string text = functionName + " " + filePath;
		int score = 0;
		if (Regex.IsMatch(functionName, @"(?:^|_)(?:loop|task|tick|logic|ctrl|control|work|process|scan|cycle)(?:_|$)", RegexOptions.IgnoreCase))
		{
			score += 35;
		}
		if (functionName.Equals("main", StringComparison.OrdinalIgnoreCase))
		{
			score -= 35;
		}
		if (Regex.IsMatch(functionName, @"\b\d+\s*ms\b|(?:^|_)\d+ms(?:_|$)", RegexOptions.IgnoreCase))
		{
			score += 25;
		}
		if (Regex.IsMatch(text, @"display|disp|lcd|screen|page", RegexOptions.IgnoreCase))
		{
			score += 22;
		}
		if (Regex.IsMatch(text, @"app|usr|user|business|logic|control|ctrl", RegexOptions.IgnoreCase))
		{
			score += 18;
		}
		if (Regex.IsMatch(functionName, @"(?:init|send|write|read|recv|receive|get|set|delay|isr|irq|handler)$", RegexOptions.IgnoreCase))
		{
			score -= 25;
		}
		if (Regex.IsMatch(text, @"driver|hal|bsp|startup|system|i2c|spi|uart|can|adc|pwm|gpio|timer", RegexOptions.IgnoreCase))
		{
			score -= 12;
		}
		return score;
	}

	private static string OfflineRootReason(ProgramCallGraphNode node)
	{
		if (node.Incoming == 0 && node.Outgoing > 0)
		{
			return "调用图根节点";
		}
		if (node.Kind.Equals("disp", StringComparison.OrdinalIgnoreCase))
		{
			return "显示候选";
		}
		if (node.Kind.Equals("main", StringComparison.OrdinalIgnoreCase) ||
			node.Kind.Equals("period10", StringComparison.OrdinalIgnoreCase) ||
			node.Kind.Equals("timer", StringComparison.OrdinalIgnoreCase))
		{
			return "调度候选";
		}
		return "业务候选";
	}

	private IEnumerable<string> FindOfflineDisplayTaskEntries(string directory)
	{
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			yield break;
		}

		var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string file in EnumerateSourceFilesForOpen(directory))
		{
			string text;
			try
			{
				text = ReadSourceTextCached(file);
			}
			catch
			{
				continue;
			}

			foreach (Match match in Regex.Matches(text, @"\bgp_lcdtask\s*=\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;"))
			{
				string name = match.Groups["name"].Value;
				if (yielded.Add(name))
				{
					yield return name;
				}
			}
		}
	}

	private string? ResolveOfflineFunctionSourceText(string functionName)
	{
		if (string.IsNullOrWhiteSpace(_workDirectory) || !Directory.Exists(_workDirectory))
		{
			return null;
		}

		return TryLoadFunctionSource(_workDirectory, functionName, out FunctionSourceView? source) && source != null
			? string.Join("\n", source.Lines)
			: null;
	}

	private PollCycleStats PollOfflineSimulation(IReadOnlyList<WatchItem> watchItems)
	{
		bool paused = Volatile.Read(ref _offlineRuntimePaused) != 0;
		bool step = TryConsumeOfflineStepRequest();
		bool shouldExecute = !paused || step;
		bool ran = false;
		OfflineProgramModel? model = GetOfflineProgramModel();
		if (model == null)
		{
			LogOfflineCWorkerIssue("离线未覆盖：没有建立应用层程序模型。");
			int fallbackRefreshed = QueueOfflineWatchValues(watchItems);
			return new PollCycleStats(watchItems.Count, 0, fallbackRefreshed, shouldExecute ? 1 : 0, 0);
		}

		if (!EnsureOfflineCWorkerReady(model, watchItems))
		{
			int fallbackRefreshed = QueueOfflineWatchValues(watchItems);
			return new PollCycleStats(watchItems.Count, 0, fallbackRefreshed, shouldExecute ? 1 : 0, 0);
		}

		if (shouldExecute && _offlineCWorker != null)
		{
			OfflineWorkerResult run = _offlineCWorker.RunTick();
			LogOfflineWorkerCoverage(run);
			ran = run.Ok && run.EngineAvailable;
			if (!ran)
			{
				LogOfflineCWorkerIssue(run.Status.Length > 0 ? run.Status : "离线未覆盖：C worker 没有执行 tick。");
			}
		}

		int refreshed = RefreshOfflineWatchValuesFromWorker(watchItems, model);
		return new PollCycleStats(watchItems.Count, ran ? 1 : 0, refreshed, shouldExecute && !ran ? 1 : 0, 0);
	}

	private bool TryConsumeOfflineStepRequest()
	{
		while (true)
		{
			int current = Volatile.Read(ref _offlineStepRequests);
			if (current <= 0)
			{
				return false;
			}
			if (Interlocked.CompareExchange(ref _offlineStepRequests, current - 1, current) == current)
			{
				return true;
			}
		}
	}

	private int QueueOfflineWatchValues(IReadOnlyList<WatchItem> watchItems)
	{
		int refreshed = 0;
		foreach (WatchItem item in watchItems)
		{
			UpdateItem(item, Math.Clamp(item.Size, 1, 4), 0, GetSimulatedValue(item));
			refreshed++;
		}
		return refreshed;
	}

	private bool EnsureOfflineCWorkerReady(OfflineProgramModel model, IReadOnlyList<WatchItem> watchItems)
	{
		IReadOnlyList<WatchItem> simulationItems = BuildOfflineSimulationItems(watchItems, model);
		string signature = BuildOfflineCWorkerSignature(model, simulationItems);
		if (_offlineCWorker != null &&
			_offlineCWorkerSignature.Equals(signature, StringComparison.Ordinal) &&
			_offlineCWorker.EngineAvailable)
		{
			return true;
		}

		if (_offlineCWorker == null)
		{
			_offlineCWorker = new OfflineCWorkerClient();
		}

		OfflineWorkerProjectPayload payload = BuildOfflineCWorkerProjectPayload(model, simulationItems, signature);
		OfflineWorkerResult result = _offlineCWorker.InitProject(payload);
		LogOfflineWorkerCoverage(result);
		if (!result.Ok || !result.EngineAvailable)
		{
			_offlineCWorkerSignature = "";
			LogOfflineCWorkerIssue(result.Status.Length > 0 ? result.Status : "离线 C worker 初始化失败。");
			return false;
		}

		_offlineCWorkerSignature = signature;
		LogOfflineCWorkerIssue("离线 C worker 已接管：TinyCC 内核已初始化。");
		return true;
	}

	private int RefreshOfflineWatchValuesFromWorker(IReadOnlyList<WatchItem> watchItems, OfflineProgramModel model)
	{
		if (_offlineCWorker == null)
		{
			return 0;
		}

		List<OfflineWorkerVariablePayload> variables = watchItems
			.Where(item => item.Enabled && !item.IsChild)
			.Select(BuildOfflineWorkerVariablePayload)
			.ToList();
		OfflineWorkerResult result = _offlineCWorker.ReadSnapshot(variables);
		LogOfflineWorkerCoverage(result);
		if (!result.Ok)
		{
			LogOfflineCWorkerIssue(result.Status.Length > 0 ? result.Status : "离线 C worker 快照读取失败。");
			return 0;
		}

		int refreshed = 0;
		foreach (WatchItem item in watchItems)
		{
			string key = GetSimulationKey(item);
			if (!result.Values.TryGetValue(key, out uint raw))
			{
				continue;
			}
			SetSimulatedValue(item, raw);
			UpdateItem(item, Math.Clamp(item.Size, 1, 4), 0, raw);
			refreshed++;
		}
		return refreshed;
	}

	private OfflineWorkerProjectPayload BuildOfflineCWorkerProjectPayload(
		OfflineProgramModel model,
		IReadOnlyList<WatchItem> simulationItems,
		string signature)
	{
		return new OfflineWorkerProjectPayload
		{
			WorkDirectory = model.Directory,
			Signature = signature,
			RootFunctions = model.Roots.Select(source => source.FunctionName)
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList(),
			Sources = model.Sources.Select(source => new OfflineWorkerSourcePayload
			{
				FunctionName = source.FunctionName,
				FilePath = source.FilePath,
				StartLine = source.StartLine,
				Lines = source.Lines.ToList()
			}).ToList(),
			Variables = simulationItems
				.Where(item => item.Enabled && !item.IsChild)
				.Select(BuildOfflineWorkerVariablePayload)
				.ToList()
		};
	}

	private OfflineWorkerVariablePayload BuildOfflineWorkerVariablePayload(WatchItem item)
	{
		return new OfflineWorkerVariablePayload
		{
			Key = GetSimulationKey(item),
			Name = item.Name,
			Address = item.Address,
			Size = Math.Clamp(item.Size, 1, 4),
			TypeName = item.TypeName,
			RawValue = GetSimulatedValue(item),
			ForceActive = item.ForceActive,
			Aliases = WatchIdentifierAliases(item.Name)
				.Append(GetWatchDisplayName(item))
				.Where(IsValidOfflineWorkerAlias)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
		};
	}

	private static OfflineWorkerVariableWritePayload BuildOfflineWorkerWritePayload(WatchItem item, uint rawValue)
	{
		return new OfflineWorkerVariableWritePayload
		{
			Key = GetSimulationKey(item),
			Name = item.Name,
			RawValue = MaskRawValue(rawValue, Math.Clamp(item.Size, 1, 4)),
			Size = Math.Clamp(item.Size, 1, 4)
		};
	}

	private static string BuildOfflineCWorkerSignature(OfflineProgramModel model, IReadOnlyList<WatchItem> simulationItems)
	{
		// The worker owns the whole application-level model. Visible watch variables can change
		// while the user scrolls, so they must not force TinyCC to rebuild every polling cycle.
		int modelVariableCount = model.Bindings.Count(item => item.Enabled && !item.IsChild);
		string roots = string.Join(",", model.Roots
			.Select(root => root.FunctionName)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
		return model.Signature + "|roots=" + roots + "|modelVars=" + modelVariableCount.ToString(CultureInfo.InvariantCulture);
	}

	private static bool IsValidOfflineWorkerAlias(string alias)
	{
		return !string.IsNullOrWhiteSpace(alias) &&
			Regex.IsMatch(alias, @"^[A-Za-z_][A-Za-z0-9_]*$");
	}

	private void LogOfflineCWorkerIssue(string message, bool force = false)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		if (!force)
		{
			int suppressSeconds = message.Equals(_lastOfflineCWorkerLogMessage, StringComparison.Ordinal)
				? 20
				: 2;
			if ((now - _lastOfflineCWorkerLogUtc).TotalSeconds < suppressSeconds)
			{
				return;
			}
		}
		_lastOfflineCWorkerLogUtc = now;
		_lastOfflineCWorkerLogMessage = message;
		Log(message);
	}

	private void LogOfflineWorkerCoverage(OfflineWorkerResult result)
	{
		if (result.Coverage.Count == 0)
		{
			return;
		}

		foreach (string rawMessage in result.Coverage)
		{
			string message = rawMessage.Trim();
			if (message.Length == 0)
			{
				continue;
			}

			bool important =
				message.StartsWith("离线覆盖：入口", StringComparison.Ordinal) ||
				message.StartsWith("离线未覆盖：", StringComparison.Ordinal) ||
				message.Contains("stub", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("TinyCC", StringComparison.OrdinalIgnoreCase);
			if (!important)
			{
				continue;
			}

			lock (_simulationLock)
			{
				if (!_offlineCWorkerCoverageLogged.Add(message))
				{
					continue;
				}
			}

			Log(message);
		}
	}

	private bool RunOfflineLightSimulationTick(IReadOnlyList<WatchItem> watchItems, bool force = false)
	{
		if (!_offlineSimulation || watchItems.Count == 0)
		{
			return false;
		}

		DateTime now = DateTime.UtcNow;
		if (!force && (now - _lastOfflineSimulationTickUtc).TotalMilliseconds < Math.Max(10, Volatile.Read(ref _targetCycleMs)) - 1)
		{
			return false;
		}
		_lastOfflineSimulationTickUtc = now;

		OfflineProgramModel? model = GetOfflineProgramModel();
		IReadOnlyList<FunctionSourceView> sources = model?.Roots ?? GetOfflineApplicationSources();
		if (sources.Count == 0)
		{
			return false;
		}

		IReadOnlyList<WatchItem> simulationItems = BuildOfflineSimulationItems(watchItems, model);
		Dictionary<string, WatchItem> aliases = BuildWatchAliasMap(simulationItems);
		lock (_simulationLock)
		{
			foreach (WatchItem item in simulationItems)
			{
				string key = GetSimulationKey(item);
				if (!_simulatedValues.ContainsKey(key))
				{
					_simulatedValues[key] = 0;
				}
			}

			foreach (FunctionSourceView source in sources)
			{
				if (source.Lines.Count == 0)
				{
					continue;
				}

				string key = source.FilePath + "|" + source.FunctionName;
				if (!_offlineCDrivers.TryGetValue(key, out OfflineCDriver driver))
				{
					driver = new OfflineCDriver();
					_offlineCDrivers[key] = driver;
				}

				if (driver.TryRun(
					source.FunctionName,
					source.FilePath,
					source.Lines,
					simulationItems,
					GetOfflineNumericValue,
					SetOfflineNumericValue,
					ResolveOfflineFunctionSourceText,
					_programGraphLastSourceWrite.Ticks.ToString()))
				{
					continue;
				}

				ExecuteOfflineLightSimulationLines(source.Lines, 0, source.Lines.Count - 1, aliases, 0);
			}
		}
		return true;
	}

	private static IReadOnlyList<WatchItem> BuildOfflineSimulationItems(IReadOnlyList<WatchItem> watchItems, OfflineProgramModel? model)
	{
		var merged = new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);
		if (model != null)
		{
			foreach (WatchItem item in model.Bindings)
			{
				if (item.Enabled && !item.IsChild)
				{
					merged[GetSimulationKey(item)] = item;
				}
			}
		}
		foreach (WatchItem item in watchItems)
		{
			if (item.Enabled && !item.IsChild)
			{
				merged[GetSimulationKey(item)] = item;
			}
		}
		return merged.Values.ToList();
	}

	private static Dictionary<string, WatchItem> BuildWatchAliasMap(IEnumerable<WatchItem> watchItems)
	{
		Dictionary<string, WatchItem> map = new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);
		foreach (WatchItem item in watchItems.Where(item => item.Enabled))
		{
			foreach (string alias in WatchIdentifierAliases(item.Name))
			{
				if (!map.ContainsKey(alias))
				{
					map.Add(alias, item);
				}
			}
		}
		return map;
	}

	private void ExecuteOfflineLightSimulationLines(
		IReadOnlyList<string> lines,
		int start,
		int end,
		Dictionary<string, WatchItem> aliases,
		int depth,
		HashSet<string>? callStack = null)
	{
		if (depth > 8)
		{
			return;
		}

		callStack ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int i = Math.Max(0, start); i <= end && i < lines.Count; i++)
		{
			string line = StripLineComment(lines[i]).Trim();
			if (line.Length == 0 || line == "{" || line == "}")
			{
				continue;
			}

			string? switchExpr = ExtractSwitchExpression(line);
			if (switchExpr != null)
			{
				int bodyStart = FindNextExecutableLine(lines, i + 1, end);
				if (bodyStart < 0 || !LineOpensBlock(lines[bodyStart]))
				{
					continue;
				}

				int close = FindMatchingCloseBrace(lines, bodyStart, end);
				if (close <= bodyStart)
				{
					continue;
				}

				if (TryEvaluateOfflineNumeric(switchExpr, aliases, out double switchValue))
				{
					ExecuteOfflineSwitchBranch(lines, bodyStart + 1, close - 1, switchValue, aliases, depth + 1, callStack);
				}
				else
				{
					LogOfflineSimulationUncovered("switch(" + switchExpr + ")");
				}
				i = close;
				continue;
			}

			if (Regex.IsMatch(line, @"^\s*(else\b|for\b|while\b|return\b|break\b|continue\b)"))
			{
				continue;
			}

			string? expr = ExtractIfExpression(line);
			if (expr != null)
			{
				ConditionEval condition = EvaluateOfflineSimulationExpression(expr, aliases);
				string afterIf = GetTextAfterIfExpression(line);
				if (afterIf.Length > 0 && afterIf != "{")
				{
					if (condition == ConditionEval.True)
					{
						ExecuteOfflineSimulationStatement(afterIf, aliases, depth, callStack);
					}
					int inlineElse = FindNextExecutableLine(lines, i + 1, end);
					if (inlineElse >= 0 && IsElseLine(lines[inlineElse]))
					{
						i = SkipOfflineElseBranch(lines, inlineElse, end, aliases, depth, callStack, execute: condition == ConditionEval.False);
					}
					continue;
				}

				int bodyStart = FindNextExecutableLine(lines, i + 1, end);
				if (bodyStart < 0)
				{
					continue;
				}

				if (LineOpensBlock(lines[bodyStart]))
				{
					int close = FindMatchingCloseBrace(lines, bodyStart, end);
					if (condition == ConditionEval.True && close > bodyStart)
					{
						ExecuteOfflineLightSimulationLines(lines, bodyStart + 1, close - 1, aliases, depth + 1, callStack);
					}
					int branchEnd = close > i ? close : bodyStart;
					int next = FindNextExecutableLine(lines, branchEnd + 1, end);
					if (next >= 0 && IsElseLine(lines[next]))
					{
						i = SkipOfflineElseBranch(lines, next, end, aliases, depth, callStack, execute: condition == ConditionEval.False);
					}
					else
					{
						i = branchEnd;
					}
					continue;
				}

				if (condition == ConditionEval.True)
				{
					ExecuteOfflineSimulationStatement(lines[bodyStart], aliases, depth, callStack);
				}
				int afterSingle = Math.Max(i, bodyStart);
				int singleElse = FindNextExecutableLine(lines, afterSingle + 1, end);
				if (singleElse >= 0 && IsElseLine(lines[singleElse]))
				{
					i = SkipOfflineElseBranch(lines, singleElse, end, aliases, depth, callStack, execute: condition == ConditionEval.False);
				}
				else
				{
					i = afterSingle;
				}
				continue;
			}

			ExecuteOfflineSimulationStatement(line, aliases, depth, callStack);
		}
	}

	private void ExecuteOfflineSimulationStatement(string rawLine, Dictionary<string, WatchItem> aliases, int depth, HashSet<string> callStack)
	{
		string line = StripLineComment(rawLine).Trim();
		if (line.Length == 0)
		{
			return;
		}

		line = line.Trim('{', '}').Trim();
		if (line.Length == 0)
		{
			return;
		}

		if (TryExecuteOfflineFunctionCall(line, aliases, depth, callStack) || LooksLikeFunctionOnlyStatement(line))
		{
			return;
		}

		Match postfix = Regex.Match(line, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op>\+\+|--)\s*;?$");
		if (postfix.Success && TryResolveOfflineWatch(postfix.Groups["name"].Value, aliases, out WatchItem item))
		{
			double value = GetOfflineNumericValue(item);
			value += postfix.Groups["op"].Value == "++" ? 1 : -1;
			SetOfflineNumericValue(item, value);
			return;
		}

		Match prefix = Regex.Match(line, @"^(?<op>\+\+|--)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;?$");
		if (prefix.Success && TryResolveOfflineWatch(prefix.Groups["name"].Value, aliases, out item))
		{
			double value = GetOfflineNumericValue(item);
			value += prefix.Groups["op"].Value == "++" ? 1 : -1;
			SetOfflineNumericValue(item, value);
			return;
		}

		Match compound = Regex.Match(line, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op>\+=|-=)\s*(?<expr>[^;]+)\s*;?$");
		if (compound.Success && TryResolveOfflineWatch(compound.Groups["name"].Value, aliases, out item) &&
			TryEvaluateOfflineNumeric(compound.Groups["expr"].Value, aliases, out double delta))
		{
			double value = GetOfflineNumericValue(item);
			value += compound.Groups["op"].Value == "+=" ? delta : -delta;
			SetOfflineNumericValue(item, value);
			return;
		}

		Match assign = Regex.Match(line, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<expr>[^;]+)\s*;?$");
		if (assign.Success && TryResolveOfflineWatch(assign.Groups["name"].Value, aliases, out item) &&
			TryEvaluateOfflineNumeric(assign.Groups["expr"].Value, aliases, out double assigned))
		{
			SetOfflineNumericValue(item, assigned);
		}
	}

	private static bool LooksLikeFunctionOnlyStatement(string line)
	{
		return Regex.IsMatch(line, @"^[A-Za-z_][A-Za-z0-9_]*\s*\(");
	}

	private int SkipOfflineElseBranch(
		IReadOnlyList<string> lines,
		int elseIndex,
		int end,
		Dictionary<string, WatchItem> aliases,
		int depth,
		HashSet<string> callStack,
		bool execute)
	{
		string elseLine = StripLineComment(lines[elseIndex]).Trim();
		string afterElse = elseLine.Length > 4 ? elseLine.Substring(4).Trim() : "";
		if (afterElse.StartsWith("if", StringComparison.OrdinalIgnoreCase))
		{
			return ExecuteOfflineIfBranch(lines, elseIndex, end, afterElse, aliases, depth, callStack, branchAllowed: execute);
		}
		if (afterElse.Length > 0 && afterElse != "{")
		{
			if (execute)
			{
				ExecuteOfflineSimulationStatement(afterElse, aliases, depth, callStack);
			}
			return elseIndex;
		}

		int bodyStart = FindNextExecutableLine(lines, elseIndex + 1, end);
		if (bodyStart < 0)
		{
			return elseIndex;
		}

		if (LineOpensBlock(lines[bodyStart]))
		{
			int close = FindMatchingCloseBrace(lines, bodyStart, end);
			if (execute && close > bodyStart)
			{
				ExecuteOfflineLightSimulationLines(lines, bodyStart + 1, close - 1, aliases, depth + 1, callStack);
			}
			return close > elseIndex ? close : bodyStart;
		}

		if (execute)
		{
			ExecuteOfflineSimulationStatement(lines[bodyStart], aliases, depth, callStack);
		}
		return bodyStart;
	}

	private int ExecuteOfflineIfBranch(
		IReadOnlyList<string> lines,
		int lineIndex,
		int end,
		string ifLine,
		Dictionary<string, WatchItem> aliases,
		int depth,
		HashSet<string> callStack,
		bool branchAllowed)
	{
		string? expr = ExtractIfExpression(ifLine);
		if (expr == null)
		{
			return lineIndex;
		}

		ConditionEval condition = branchAllowed ? EvaluateOfflineSimulationExpression(expr, aliases) : ConditionEval.Unknown;
		string afterIf = GetTextAfterIfExpression(ifLine);
		if (afterIf.Length > 0 && afterIf != "{")
		{
			if (branchAllowed && condition == ConditionEval.True)
			{
				ExecuteOfflineSimulationStatement(afterIf, aliases, depth, callStack);
			}
			int inlineElse = FindNextExecutableLine(lines, lineIndex + 1, end);
			if (inlineElse >= 0 && IsElseLine(lines[inlineElse]))
			{
				return SkipOfflineElseBranch(lines, inlineElse, end, aliases, depth, callStack, execute: branchAllowed && condition == ConditionEval.False);
			}
			return lineIndex;
		}

		int bodyStart = FindNextExecutableLine(lines, lineIndex + 1, end);
		if (bodyStart < 0)
		{
			return lineIndex;
		}

		int branchEnd;
		if (LineOpensBlock(lines[bodyStart]))
		{
			int close = FindMatchingCloseBrace(lines, bodyStart, end);
			if (branchAllowed && condition == ConditionEval.True && close > bodyStart)
			{
				ExecuteOfflineLightSimulationLines(lines, bodyStart + 1, close - 1, aliases, depth + 1, callStack);
			}
			branchEnd = close > lineIndex ? close : bodyStart;
		}
		else
		{
			if (branchAllowed && condition == ConditionEval.True)
			{
				ExecuteOfflineSimulationStatement(lines[bodyStart], aliases, depth, callStack);
			}
			branchEnd = Math.Max(lineIndex, bodyStart);
		}

		int next = FindNextExecutableLine(lines, branchEnd + 1, end);
		if (next >= 0 && IsElseLine(lines[next]))
		{
			return SkipOfflineElseBranch(lines, next, end, aliases, depth, callStack, execute: branchAllowed && condition == ConditionEval.False);
		}
		return branchEnd;
	}

	private void ExecuteOfflineSwitchBranch(
		IReadOnlyList<string> lines,
		int start,
		int end,
		double switchValue,
		Dictionary<string, WatchItem> aliases,
		int depth,
		HashSet<string> callStack)
	{
		if (depth > 8)
		{
			return;
		}

		bool executing = false;
		bool matched = false;
		for (int i = Math.Max(0, start); i <= end && i < lines.Count; i++)
		{
			string line = StripLineComment(lines[i]).Trim();
			if (line.Length == 0 || line == "{" || line == "}")
			{
				continue;
			}

			Match caseMatch = Regex.Match(line, @"^case\s+(?<expr>[^:]+)\s*:\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
			if (caseMatch.Success)
			{
				executing = TryEvaluateOfflineNumeric(caseMatch.Groups["expr"].Value, aliases, out double caseValue) &&
					Math.Abs(caseValue - switchValue) < 0.000001;
				matched |= executing;
				string rest = caseMatch.Groups["rest"].Value.Trim();
				if (executing && rest.Length > 0)
				{
					if (TryExecuteOfflineSwitchStatementList(rest, aliases, depth, callStack, out bool shouldBreak) && shouldBreak)
					{
						return;
					}
				}
				continue;
			}

			Match defaultMatch = Regex.Match(line, @"^default\s*:\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
			if (defaultMatch.Success)
			{
				executing = !matched;
				string rest = defaultMatch.Groups["rest"].Value.Trim();
				if (executing && rest.Length > 0)
				{
					if (TryExecuteOfflineSwitchStatementList(rest, aliases, depth, callStack, out bool shouldBreak) && shouldBreak)
					{
						return;
					}
				}
				continue;
			}

			if (!executing)
			{
				continue;
			}

			if (Regex.IsMatch(line, @"^break\s*;?$", RegexOptions.IgnoreCase))
			{
				return;
			}

			ExecuteOfflineSimulationStatement(line, aliases, depth, callStack);
		}
	}

	private bool TryExecuteOfflineSwitchStatementList(
		string text,
		Dictionary<string, WatchItem> aliases,
		int depth,
		HashSet<string> callStack,
		out bool shouldBreak)
	{
		shouldBreak = false;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		foreach (string part in text.Split(';'))
		{
			string statement = part.Trim();
			if (statement.Length == 0)
			{
				continue;
			}
			if (statement.Equals("break", StringComparison.OrdinalIgnoreCase))
			{
				shouldBreak = true;
				return true;
			}

			ExecuteOfflineSimulationStatement(statement + ";", aliases, depth, callStack);
		}
		return true;
	}

	private void LogOfflineSimulationUncovered(string detail)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastOfflineUncoveredLogUtc).TotalSeconds < 5)
		{
			return;
		}

		_lastOfflineUncoveredLogUtc = now;
		Log("离线未覆盖：" + detail);
	}

	private static bool IsElseLine(string line)
	{
		return Regex.IsMatch(StripLineComment(line).Trim(), @"^else\b", RegexOptions.IgnoreCase);
	}

	private bool TryExecuteOfflineFunctionCall(
		string line,
		Dictionary<string, WatchItem> aliases,
		int depth,
		HashSet<string> callStack)
	{
		if (depth >= 8 ||
			string.IsNullOrWhiteSpace(_workDirectory) ||
			!Directory.Exists(_workDirectory) ||
			!TryParseOfflineFunctionCall(line, out OfflineFunctionCall call) ||
			IsCKeyword(call.Name))
		{
			return false;
		}

		if (!TryLoadFunctionSource(_workDirectory, call.Name, out FunctionSourceView? source) || source == null)
		{
			return false;
		}

		string key = source.FilePath + "|" + source.FunctionName;
		if (!callStack.Add(key))
		{
			return true;
		}

		List<string> localNames = new List<string>();
		try
		{
			Dictionary<string, WatchItem> childAliases = BuildOfflineChildAliases(call, source, aliases, depth, localNames);
			ExecuteOfflineLightSimulationLines(source.Lines, 0, source.Lines.Count - 1, childAliases, depth + 1, callStack);
			return true;
		}
		finally
		{
			foreach (string localName in localNames)
			{
				_simulatedValues.Remove(localName);
			}
			callStack.Remove(key);
		}
	}

	private Dictionary<string, WatchItem> BuildOfflineChildAliases(
		OfflineFunctionCall call,
		FunctionSourceView source,
		Dictionary<string, WatchItem> parentAliases,
		int depth,
		List<string> localNames)
	{
		Dictionary<string, WatchItem> childAliases = new Dictionary<string, WatchItem>(parentAliases, StringComparer.OrdinalIgnoreCase);
		List<OfflineParameter> parameters = ExtractOfflineParameters(source, call.Name);
		List<string> arguments = SplitOfflineArguments(call.Arguments);
		int count = Math.Min(parameters.Count, arguments.Count);
		for (int i = 0; i < count; i++)
		{
			OfflineParameter parameter = parameters[i];
			if (parameter.Name.Length == 0)
			{
				continue;
			}

			double value = 0.0;
			string argument = NormalizeOfflineExpression(arguments[i]);
			if (!TryEvaluateOfflineNumeric(argument, parentAliases, out value))
			{
				value = 0.0;
			}

			string localName = $"__offline_local_{depth}_{call.Name}_{i}_{parameter.Name}";
			WatchItem localItem = new WatchItem
			{
				Enabled = true,
				Name = localName,
				Size = 4,
				TotalSize = 4,
				TypeName = string.IsNullOrWhiteSpace(parameter.TypeName) ? "int" : parameter.TypeName,
				Status = "正常"
			};
			childAliases[parameter.Name] = localItem;
			localNames.Add(localName);
			SetOfflineNumericValue(localItem, value);
		}
		return childAliases;
	}

	private static bool TryParseOfflineFunctionCall(string line, out OfflineFunctionCall call)
	{
		call = default;
		string text = line.Trim().TrimEnd(';').Trim();
		Match match = Regex.Match(text, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
		if (!match.Success)
		{
			return false;
		}

		int open = text.IndexOf('(', match.Index + match.Groups["name"].Length);
		int close = FindMatchingParenthesis(text, open);
		if (open < 0 || close < 0)
		{
			return false;
		}

		string tail = text.Substring(close + 1).Trim();
		if (tail.Length != 0)
		{
			return false;
		}

		call = new OfflineFunctionCall(match.Groups["name"].Value, text.Substring(open + 1, close - open - 1));
		return true;
	}

	private static List<OfflineParameter> ExtractOfflineParameters(FunctionSourceView source, string functionName)
	{
		string text = string.Join("\n", source.Lines);
		int openBrace = text.IndexOf('{');
		if (openBrace < 0)
		{
			return new List<OfflineParameter>();
		}

		string header = text.Substring(0, openBrace);
		int nameIndex = header.LastIndexOf(functionName, StringComparison.OrdinalIgnoreCase);
		if (nameIndex < 0)
		{
			return new List<OfflineParameter>();
		}

		int open = header.IndexOf('(', nameIndex + functionName.Length);
		int close = FindMatchingParenthesis(header, open);
		if (open < 0 || close < 0)
		{
			return new List<OfflineParameter>();
		}

		string parameterText = header.Substring(open + 1, close - open - 1);
		List<OfflineParameter> parameters = new List<OfflineParameter>();
		foreach (string rawParameter in SplitOfflineArguments(parameterText))
		{
			string clean = Regex.Replace(rawParameter, @"\[[^\]]*\]", " ");
			clean = clean.Replace("*", " ");
			List<Match> identifiers = Regex.Matches(clean, @"\b[A-Za-z_][A-Za-z0-9_]*\b")
				.Cast<Match>()
				.ToList();
			if (identifiers.Count == 0)
			{
				continue;
			}

			string name = identifiers[^1].Value;
			if (name.Equals("void", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			int nameIndexInParameter = clean.LastIndexOf(name, StringComparison.OrdinalIgnoreCase);
			string typeName = nameIndexInParameter > 0
				? Regex.Replace(clean.Substring(0, nameIndexInParameter), @"\s+", " ").Trim()
				: "int";
			parameters.Add(new OfflineParameter(name, typeName.Length == 0 ? "int" : typeName));
		}
		return parameters;
	}

	private static List<string> SplitOfflineArguments(string text)
	{
		List<string> parts = new List<string>();
		StringBuilder current = new StringBuilder();
		int depth = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		foreach (char c in text)
		{
			if (escape)
			{
				current.Append(c);
				escape = false;
				continue;
			}
			if ((inString || inChar) && c == '\\')
			{
				current.Append(c);
				escape = true;
				continue;
			}
			if (!inChar && c == '"')
			{
				inString = !inString;
				current.Append(c);
				continue;
			}
			if (!inString && c == '\'')
			{
				inChar = !inChar;
				current.Append(c);
				continue;
			}
			if (!inString && !inChar)
			{
				if (c == '(' || c == '[' || c == '{')
				{
					depth++;
				}
				else if (c == ')' || c == ']' || c == '}')
				{
					depth = Math.Max(0, depth - 1);
				}
				else if (c == ',' && depth == 0)
				{
					parts.Add(current.ToString().Trim());
					current.Clear();
					continue;
				}
			}
			current.Append(c);
		}

		if (current.Length > 0 || text.Contains(',', StringComparison.Ordinal))
		{
			parts.Add(current.ToString().Trim());
		}
		return parts;
	}

	private static int FindMatchingParenthesis(string text, int open)
	{
		if (open < 0 || open >= text.Length || text[open] != '(')
		{
			return -1;
		}

		int depth = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = open; i < text.Length; i++)
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

	private static string StripLineComment(string line)
	{
		int comment = line.IndexOf("//", StringComparison.Ordinal);
		return comment >= 0 ? line.Substring(0, comment) : line;
	}

	private static string? ExtractSwitchExpression(string line)
	{
		Match match = Regex.Match(line, @"^\s*switch\b", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return null;
		}

		int open = line.IndexOf('(', match.Index + match.Length);
		if (open < 0)
		{
			return null;
		}

		int depth = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = open; i < line.Length; i++)
		{
			char c = line[i];
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
					return line.Substring(open + 1, i - open - 1).Trim();
				}
			}
		}

		return null;
	}

	private static string GetTextAfterIfExpression(string line)
	{
		Match match = Regex.Match(line, @"^\s*if\b");
		if (!match.Success)
		{
			return "";
		}
		int open = line.IndexOf('(', match.Index + match.Length);
		if (open < 0)
		{
			return "";
		}
		int depth = 0;
		for (int i = open; i < line.Length; i++)
		{
			if (line[i] == '(')
			{
				depth++;
			}
			else if (line[i] == ')')
			{
				depth--;
				if (depth == 0)
				{
					return line.Substring(i + 1).Trim();
				}
			}
		}
		return "";
	}

	private static int FindNextExecutableLine(IReadOnlyList<string> lines, int start, int end)
	{
		for (int i = start; i <= end && i < lines.Count; i++)
		{
			string line = StripLineComment(lines[i]).Trim();
			if (line.Length == 0)
			{
				continue;
			}
			return i;
		}
		return -1;
	}

	private static bool LineOpensBlock(string line)
	{
		return StripLineComment(line).Contains('{', StringComparison.Ordinal);
	}

	private static int FindMatchingCloseBrace(IReadOnlyList<string> lines, int openLine, int end)
	{
		int depth = 0;
		for (int i = openLine; i <= end && i < lines.Count; i++)
		{
			string line = StripLineComment(lines[i]);
			foreach (char c in line)
			{
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
		}
		return -1;
	}

	private ConditionEval EvaluateOfflineSimulationExpression(string expr, Dictionary<string, WatchItem> aliases)
	{
		bool hasUnknown = false;
		foreach (string orPart in Regex.Split(expr, @"\|\|"))
		{
			bool andOk = true;
			bool andUnknown = false;
			foreach (string andPart in Regex.Split(orPart, @"&&"))
			{
				ConditionEval part = EvaluateOfflineSimulationCondition(andPart, aliases);
				if (part == ConditionEval.False)
				{
					andOk = false;
					break;
				}
				if (part == ConditionEval.Unknown)
				{
					andUnknown = true;
				}
			}
			if (andOk && !andUnknown)
			{
				return ConditionEval.True;
			}
			hasUnknown |= andUnknown;
		}
		return hasUnknown ? ConditionEval.Unknown : ConditionEval.False;
	}

	private ConditionEval EvaluateOfflineSimulationCondition(string expression, Dictionary<string, WatchItem> aliases)
	{
		string text = expression.Trim();
		if (text.Length == 0)
		{
			return ConditionEval.Unknown;
		}

		while (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal) && HasBalancedOuterParentheses(text))
		{
			text = text.Substring(1, text.Length - 2).Trim();
		}

		bool inverted = false;
		while (text.StartsWith("!", StringComparison.Ordinal))
		{
			inverted = !inverted;
			text = text.Substring(1).Trim();
		}

		ConditionEval result = EvaluateOfflineSimulationConditionCore(text, aliases);
		if (result == ConditionEval.Unknown)
		{
			return result;
		}
		if (!inverted)
		{
			return result;
		}
		return result == ConditionEval.True ? ConditionEval.False : ConditionEval.True;
	}

	private ConditionEval EvaluateOfflineSimulationConditionCore(string text, Dictionary<string, WatchItem> aliases)
	{
		if (TrySplitOfflineComparison(text, out string leftExpression, out string op, out string rightExpression))
		{
			if (!TryEvaluateOfflineNumeric(leftExpression, aliases, out double left) ||
				!TryEvaluateOfflineNumeric(rightExpression, aliases, out double right))
			{
				return ConditionEval.Unknown;
			}

			bool value = op switch
			{
				"==" => Math.Abs(left - right) < 0.000001,
				"!=" => Math.Abs(left - right) >= 0.000001,
				">=" => left >= right,
				"<=" => left <= right,
				">" => left > right,
				"<" => left < right,
				_ => false
			};
			return value ? ConditionEval.True : ConditionEval.False;
		}

		if (!TryEvaluateOfflineNumeric(text, aliases, out double result))
		{
			return ConditionEval.Unknown;
		}
		return Math.Abs(result) > 0.000001 ? ConditionEval.True : ConditionEval.False;
	}

	private bool TryEvaluateOfflineArithmetic(string expr, Dictionary<string, WatchItem> aliases, out long value)
	{
		value = 0;
		if (!TryEvaluateOfflineNumeric(expr, aliases, out double numeric))
		{
			return false;
		}
		value = (long)Math.Round(numeric, MidpointRounding.AwayFromZero);
		return true;
	}

	private bool TryEvaluateOfflineNumeric(string expr, Dictionary<string, WatchItem> aliases, out double value)
	{
		string text = NormalizeOfflineExpression(expr);
		int index = 0;
		if (!ParseExpression(out value))
		{
			value = 0;
			return false;
		}

		SkipWhitespace();
		return index >= text.Length;

		bool ParseExpression(out double result)
		{
			if (!ParseTerm(out result))
			{
				return false;
			}
			while (true)
			{
				SkipWhitespace();
				if (index >= text.Length || (text[index] != '+' && text[index] != '-'))
				{
					return true;
				}

				char opChar = text[index++];
				if (!ParseTerm(out double right))
				{
					return false;
				}
				result = opChar == '+' ? result + right : result - right;
			}
		}

		bool ParseTerm(out double result)
		{
			if (!ParseFactor(out result))
			{
				return false;
			}
			while (true)
			{
				SkipWhitespace();
				if (index >= text.Length || (text[index] != '*' && text[index] != '/'))
				{
					return true;
				}

				char opChar = text[index++];
				if (!ParseFactor(out double right))
				{
					return false;
				}
				if (opChar == '/')
				{
					if (Math.Abs(right) < 0.0000001)
					{
						return false;
					}
					result /= right;
				}
				else
				{
					result *= right;
				}
			}
		}

		bool ParseFactor(out double result)
		{
			result = 0;
			SkipWhitespace();
			if (index >= text.Length)
			{
				return false;
			}

			if (text[index] == '+')
			{
				index++;
				return ParseFactor(out result);
			}
			if (text[index] == '-')
			{
				index++;
				if (!ParseFactor(out result))
				{
					return false;
				}
				result = -result;
				return true;
			}

			if (text[index] == '(')
			{
				index++;
				if (!ParseExpression(out result))
				{
					return false;
				}
				SkipWhitespace();
				if (index >= text.Length || text[index] != ')')
				{
					return false;
				}
				index++;
				return true;
			}

			if (char.IsDigit(text[index]) || text[index] == '.')
			{
				return ParseNumber(out result);
			}

			if (IsIdentifierStart(text[index]))
			{
				string token = ReadOfflineValueToken();
				SkipWhitespace();
				if (index < text.Length && text[index] == '(')
				{
					int close = FindMatchingParenthesis(text, index);
					if (close < 0)
					{
						return false;
					}

					string argumentText = text.Substring(index + 1, close - index - 1);
					index = close + 1;
					return TryEvaluateOfflineMathFunction(token, argumentText, aliases, out result);
				}
				if (token.Equals("true", StringComparison.OrdinalIgnoreCase))
				{
					result = 1;
					return true;
				}
				if (token.Equals("false", StringComparison.OrdinalIgnoreCase))
				{
					result = 0;
					return true;
				}
				if (TryResolveOfflineWatch(token, aliases, out WatchItem item))
				{
					result = GetOfflineNumericValue(item);
					return true;
				}
			}
			return false;
		}

		bool ParseNumber(out double result)
		{
			result = 0;
			int start = index;
			if (index + 1 < text.Length && text[index] == '0' && (text[index + 1] == 'x' || text[index + 1] == 'X'))
			{
				index += 2;
				int hexStart = index;
				while (index < text.Length && Uri.IsHexDigit(text[index]))
				{
					index++;
				}
				if (hexStart == index ||
					!ulong.TryParse(text.Substring(hexStart, index - hexStart), System.Globalization.NumberStyles.HexNumber, null, out ulong hex))
				{
					return false;
				}
				SkipNumericSuffix();
				result = hex;
				return true;
			}

			while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.'))
			{
				index++;
			}
			if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
			{
				index++;
				if (index < text.Length && (text[index] == '+' || text[index] == '-'))
				{
					index++;
				}
				while (index < text.Length && char.IsDigit(text[index]))
				{
					index++;
				}
			}

			string number = text.Substring(start, index - start);
			SkipNumericSuffix();
			return double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
		}

		string ReadOfflineValueToken()
		{
			int start = index;
			index++;
			while (index < text.Length && IsIdentifierChar(text[index]))
			{
				index++;
			}

			while (index < text.Length)
			{
				SkipWhitespace();
				if (index < text.Length && text[index] == '[')
				{
					int close = FindMatchingBracket(text, index, '[', ']');
					if (close < 0)
					{
						break;
					}
					index = close + 1;
					continue;
				}
				if (index < text.Length && text[index] == '.')
				{
					index++;
					while (index < text.Length && IsIdentifierChar(text[index]))
					{
						index++;
					}
					continue;
				}
				break;
			}
			return text.Substring(start, index - start);
		}

		void SkipWhitespace()
		{
			while (index < text.Length && char.IsWhiteSpace(text[index]))
			{
				index++;
			}
		}

		void SkipNumericSuffix()
		{
			while (index < text.Length && "uUlLfF".IndexOf(text[index]) >= 0)
			{
				index++;
			}
		}
	}

	private static string NormalizeOfflineExpression(string expr)
	{
		string text = expr.Trim().TrimEnd(';').Trim();
		text = Regex.Replace(text, @"\btrue\b", "1", RegexOptions.IgnoreCase);
		text = Regex.Replace(text, @"\bfalse\b", "0", RegexOptions.IgnoreCase);
		text = Regex.Replace(
			text,
			@"\(\s*(?:(?:const|volatile|signed|unsigned|short|long)\s+)*(?:char|int|float|double|u8|u16|u32|s8|s16|s32|uint8_t|uint16_t|uint32_t|int8_t|int16_t|int32_t|BYTE|WORD|DWORD)\s*\)",
			"",
			RegexOptions.IgnoreCase);
		while (text.StartsWith("&", StringComparison.Ordinal))
		{
			text = text.Substring(1).TrimStart();
		}
		return text;
	}

	private static bool TrySplitOfflineComparison(string text, out string left, out string op, out string right)
	{
		left = "";
		op = "";
		right = "";
		string source = text.Trim();
		int depth = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < source.Length; i++)
		{
			char c = source[i];
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
			if (c == '(' || c == '[')
			{
				depth++;
				continue;
			}
			if (c == ')' || c == ']')
			{
				depth = Math.Max(0, depth - 1);
				continue;
			}
			if (depth != 0)
			{
				continue;
			}

			string? found = null;
			if (i + 1 < source.Length)
			{
				string two = source.Substring(i, 2);
				if (two is "==" or "!=" or ">=" or "<=")
				{
					found = two;
				}
			}
			if (found == null && (c == '>' || c == '<'))
			{
				found = c.ToString();
			}
			if (found == null)
			{
				continue;
			}

			left = source.Substring(0, i).Trim();
			op = found;
			right = source.Substring(i + found.Length).Trim();
			return left.Length > 0 && right.Length > 0;
		}
		return false;
	}

	private static int FindMatchingBracket(string text, int open, char openChar, char closeChar)
	{
		if (open < 0 || open >= text.Length || text[open] != openChar)
		{
			return -1;
		}

		int depth = 0;
		for (int i = open; i < text.Length; i++)
		{
			if (text[i] == openChar)
			{
				depth++;
			}
			else if (text[i] == closeChar)
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

	private bool TryEvaluateOfflineMathFunction(
		string name,
		string argumentText,
		Dictionary<string, WatchItem> aliases,
		out double value)
	{
		value = 0;
		List<string> arguments = SplitOfflineArguments(argumentText);
		if (arguments.Count == 0)
		{
			return false;
		}

		bool Arg(int index, out double result)
		{
			result = 0;
			return index >= 0 &&
				index < arguments.Count &&
				TryEvaluateOfflineNumeric(arguments[index], aliases, out result);
		}

		string function = name.Trim().ToLowerInvariant();
		if (function is "sin" or "sinf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Sin(x);
			return true;
		}
		if (function is "cos" or "cosf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Cos(x);
			return true;
		}
		if (function is "tan" or "tanf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Tan(x);
			return true;
		}
		if (function is "asin" or "asinf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Asin(x);
			return true;
		}
		if (function is "acos" or "acosf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Acos(x);
			return true;
		}
		if (function is "atan" or "atanf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Atan(x);
			return true;
		}
		if (function is "atan2" or "atan2f")
		{
			if (!Arg(0, out double y) || !Arg(1, out double x)) return false;
			value = Math.Atan2(y, x);
			return true;
		}
		if (function is "sqrt" or "sqrtf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Sqrt(x);
			return true;
		}
		if (function is "abs" or "fabs" or "fabsf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Abs(x);
			return true;
		}
		if (function is "pow" or "powf")
		{
			if (!Arg(0, out double x) || !Arg(1, out double y)) return false;
			value = Math.Pow(x, y);
			return true;
		}
		if (function is "floor" or "floorf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Floor(x);
			return true;
		}
		if (function is "ceil" or "ceilf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Ceiling(x);
			return true;
		}
		if (function is "round" or "roundf")
		{
			if (!Arg(0, out double x)) return false;
			value = Math.Round(x, MidpointRounding.AwayFromZero);
			return true;
		}
		return false;
	}

	private static bool TryResolveOfflineWatch(string name, Dictionary<string, WatchItem> aliases, out WatchItem item)
	{
		item = null!;
		string baseName = GetIdentifierBase(name);
		return baseName.Length > 0 && aliases.TryGetValue(baseName, out item);
	}

	private long GetOfflineSignedValue(WatchItem item)
	{
		int bytes = Math.Clamp(item.Size, 1, 4);
		string key = GetSimulationKey(item);
		if (!_simulatedValues.TryGetValue(key, out uint raw))
		{
			raw = 0;
			_simulatedValues[key] = 0;
		}
		raw = MaskRawValue(raw, bytes);
		return IsSignedWatchItem(item) ? ToSignedValue(raw, bytes) : raw;
	}

	private double GetOfflineNumericValue(WatchItem item)
	{
		int bytes = Math.Clamp(item.Size, 1, 4);
		string key = GetSimulationKey(item);
		if (!_simulatedValues.TryGetValue(key, out uint raw))
		{
			raw = 0;
			_simulatedValues[key] = 0;
		}

		raw = MaskRawValue(raw, bytes);
		if (IsFloatWatchItem(item) && bytes == 4)
		{
			float value = BitConverter.UInt32BitsToSingle(raw);
			return float.IsFinite(value) ? value : 0.0;
		}

		return IsSignedWatchItem(item) ? ToSignedValue(raw, bytes) : raw;
	}

	private void SetOfflineSignedValue(WatchItem item, long value)
	{
		if (IsOfflineForceActiveForItem(item))
		{
			return;
		}

		int bytes = Math.Clamp(item.Size, 1, 4);
		uint raw = unchecked((uint)value);
		_simulatedValues[GetSimulationKey(item)] = MaskRawValue(raw, bytes);
	}

	private void SetOfflineNumericValue(WatchItem item, double value)
	{
		if (IsOfflineForceActiveForItem(item))
		{
			return;
		}

		int bytes = Math.Clamp(item.Size, 1, 4);
		uint raw;
		if (IsFloatWatchItem(item) && bytes == 4)
		{
			raw = BitConverter.SingleToUInt32Bits((float)value);
		}
		else
		{
			raw = unchecked((uint)(long)Math.Round(value, MidpointRounding.AwayFromZero));
		}
		_simulatedValues[GetSimulationKey(item)] = MaskRawValue(raw, bytes);
	}

	private bool IsOfflineForceActiveForItem(WatchItem item)
	{
		if (item.ForceActive)
		{
			return true;
		}
		string key = GetSimulationKey(item);
		for (int i = 0; i < _watchItems.Count; i++)
		{
			WatchItem candidate = _watchItems[i];
			if (candidate.ForceActive && GetSimulationKey(candidate).Equals(key, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private void SetSimulatedValue(WatchItem item, uint rawValue)
	{
		int bytes = Math.Clamp(item.Size, 1, 4);
		lock (_simulationLock)
		{
			_simulatedValues[GetSimulationKey(item)] = MaskRawValue(rawValue, bytes);
		}
	}

	private static string GetSimulationKey(WatchItem item)
	{
		if (item.Address != 0)
		{
			return "addr:" + item.Address.ToString("X8", CultureInfo.InvariantCulture);
		}
		return item.Name;
	}

	private static int StableNameSeed(string name)
	{
		unchecked
		{
			int seed = 17;
			foreach (char ch in name)
			{
				seed = seed * 31 + ch;
			}
			return seed & 0x7FFFFFFF;
		}
	}

	private async Task<ReadSegmentResult> TryReadSegment(ICanAdapter adapter, uint address, int len, CancellationToken token)
	{
		await _canRequestLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			byte seq = ++_seq;
			byte[] data = MonitorProtocol.BuildReadRequest(seq, address, len);
			if (!TrySendMonitorFrame(adapter, data, "读取变量"))
			{
				NoteCanNoResponse("发送失败");
				return new ReadSegmentResult(Success: false, 0, byte.MaxValue, 0);
			}
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadResponseTimeoutMs);
			int idlePasses = 0;
			while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
			{
				int num = 0;
				CanFrame frame;
				bool receivedAnyFrame = false;
				while (num++ < 64 && DateTime.UtcNow < deadline && !token.IsCancellationRequested && adapter.TryReceive(out frame))
				{
					receivedAnyFrame = true;
					if (frame.Id == MonitorResponseId && MonitorProtocol.TryParseReadAck(frame, seq, out var len2, out var status, out var value))
					{
						NoteCanResponse();
						return new ReadSegmentResult(Success: true, len2, status, value);
					}
				}
				if (receivedAnyFrame)
				{
					idlePasses = 0;
					continue;
				}
				idlePasses++;
				if (idlePasses <= 6)
				{
					Thread.Sleep(0);
					continue;
				}
				await Task.Delay(idlePasses <= 18 ? 1 : 2, token).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (!token.IsCancellationRequested)
			{
				NoteCanNoResponse("读取变量");
			}
			return new ReadSegmentResult(Success: false, 0, byte.MaxValue, 0);
		}
		finally
		{
			_canRequestLock.Release();
		}
	}

	private async Task PromptAndWriteWatchValueAsync(WatchItem item, bool force)
	{
		string currentValue = item.ValueDec.Length > 0 ? item.ValueDec : (item.ValueHex.Length > 0 ? item.ValueHex : "0");
		string? valueText = PromptWatchValue(item, force, currentValue);
		if (valueText == null)
		{
			return;
		}

		await WriteWatchValueAsync(item, valueText, force).ConfigureAwait(true);
	}

	private async Task WriteWatchValueAsync(WatchItem item, string valueText, bool force)
	{
		if (!TryParseForceValue(item, valueText, out uint rawValue, out string error))
		{
			Log("强制值无效：" + error);
			return;
		}

		if (!EnsureAdapterForDebugCommand())
		{
			return;
		}

		SetForceButtonsEnabled(false);
		try
		{
			bool ok = await WriteValueToControllerAsync(item, rawValue, force).ConfigureAwait(true);
			if (!ok)
			{
				return;
			}

			int valueBytes = Math.Clamp(item.Size, 1, 4);
			item.RawValue = MaskRawValue(rawValue, valueBytes);
			item.ValueDec = FormatDecimalRawValue(item, item.RawValue, valueBytes);
			item.ValueHex = "0x" + item.RawValue.ToString("X" + HexDigitsForBytes(valueBytes));
			item.DisplayValue = FormatValue(item);
			item.LastUpdate = DateTime.Now;
			item.LastValueChange = item.LastUpdate;
			item.ForceActive = force;
			item.ForceText = force ? "保持" : "";
			RefreshValueCell(item);
			RefreshForceCell(item);
			MarkFunctionCodeDirty(item);
			UpdateProgramInsightPanel();
			Log((force ? "已保持：" : "已写入：") + GetWatchDisplayName(item) + " = " + item.DisplayValue);
		}
		finally
		{
			SetForceButtonsEnabled(true);
		}
	}

	private async Task<bool> WriteValueToControllerAsync(WatchItem item, uint rawValue, bool force)
	{
		if (_offlineSimulation)
		{
			OfflineProgramModel? model = GetOfflineProgramModel();
			if (model == null || !EnsureOfflineCWorkerReady(model, new[] { item }))
			{
				LogOfflineCWorkerIssue("写入失败：离线 C worker 不可用。", force: true);
				await Task.Yield();
				return false;
			}
			OfflineWorkerVariableWritePayload payload = BuildOfflineWorkerWritePayload(item, rawValue);
			OfflineWorkerResult result = force
				? _offlineCWorker!.ForceVariable(payload)
				: _offlineCWorker!.WriteVariable(payload);
			if (!result.Ok)
			{
				LogOfflineCWorkerIssue(result.Status.Length > 0 ? result.Status : "写入失败：离线 C worker 无响应。", force: true);
				await Task.Yield();
				return false;
			}
			SetSimulatedValue(item, rawValue);
			_lastOfflineSimulationTickUtc = DateTime.MinValue;
			Interlocked.Increment(ref _offlineStepRequests);
			await Task.Yield();
			return true;
		}

		ICanAdapter? adapter = _adapter;
		if (adapter == null)
		{
			return false;
		}

		if (!SendMonitorSession(open: true))
		{
			Log("强制失败：监控会话未打开。");
			return false;
		}

		int bytes = Math.Clamp(item.Size, 1, 4);
		if (bytes == 1)
		{
			return await SendWriteSegmentAsync(adapter, item.Address, 1, (ushort)(rawValue & 0xFF), force, CancellationToken.None).ConfigureAwait(true);
		}

		if (bytes == 2 || bytes == 3)
		{
			return await SendWriteSegmentAsync(adapter, item.Address, 2, (ushort)(rawValue & 0xFFFF), force, CancellationToken.None).ConfigureAwait(true);
		}

		bool low = await SendWriteSegmentAsync(adapter, item.Address, 2, (ushort)(rawValue & 0xFFFF), force, CancellationToken.None).ConfigureAwait(true);
		if (!low)
		{
			return false;
		}

		return await SendWriteSegmentAsync(adapter, item.Address + 2, 2, (ushort)((rawValue >> 16) & 0xFFFF), force, CancellationToken.None).ConfigureAwait(true);
	}

	private async Task<bool> SendWriteSegmentAsync(ICanAdapter adapter, uint address, int len, ushort value, bool force, CancellationToken token)
	{
		await _canRequestLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			byte seq = ++_seq;
			byte[] data = MonitorProtocol.BuildWriteRequest(seq, address, len, value, force);
			if (!TrySendMonitorFrame(adapter, data, force ? "保持变量" : "写入变量"))
			{
				return false;
			}

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(180.0);
			while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
			{
				int num = 0;
				CanFrame frame;
				while (num++ < 64 && DateTime.UtcNow < deadline && !token.IsCancellationRequested && adapter.TryReceive(out frame))
				{
					if (frame.Id == MonitorResponseId && MonitorProtocol.TryParseWriteAck(frame, seq, out byte status, out int ackLen, out byte command))
					{
						NoteCanResponse();
						if (status == 0)
						{
							return true;
						}

						Log("强制失败：" + BuildWriteStatusText(status) + $"，地址 0x{address:X8}，长度 {ackLen}。");
						return false;
					}
				}
				await Task.Delay(2, token).ConfigureAwait(continueOnCapturedContext: false);
			}

			NoteCanNoResponse(force ? "保持变量" : "写入变量");
			Log("强制失败：控制器无回信。");
			return false;
		}
		finally
		{
			_canRequestLock.Release();
		}
	}

	private async Task ReleaseWatchForceAsync(WatchItem item)
	{
		if (!EnsureAdapterForDebugCommand())
		{
			return;
		}

		SetForceButtonsEnabled(false);
		try
		{
			uint offlineBeforeRaw = _offlineSimulation ? GetSimulatedValue(item) : 0;
			bool ok = await ReleaseForceForItemAsync(item).ConfigureAwait(true);
			if (!ok)
			{
				return;
			}

			item.ForceActive = false;
			item.ForceText = "";
			RefreshForceCell(item);
			MarkFunctionCodeDirty(item);
			UpdateProgramInsightPanel();
			if (_offlineSimulation)
			{
				RequestOfflineBusinessAssimilationAfterRelease(item, offlineBeforeRaw);
			}
			Log("已释放强制：" + GetWatchDisplayName(item));
		}
		finally
		{
			SetForceButtonsEnabled(true);
		}
	}

	private async Task<bool> ReleaseForceForItemAsync(WatchItem item)
	{
		if (_offlineSimulation)
		{
			OfflineProgramModel? model = GetOfflineProgramModel();
			if (model == null || !EnsureOfflineCWorkerReady(model, new[] { item }))
			{
				LogOfflineCWorkerIssue("释放失败：离线 C worker 不可用。", force: true);
				await Task.Yield();
				return false;
			}
			OfflineWorkerResult result = _offlineCWorker!.ReleaseVariable(BuildOfflineWorkerWritePayload(item, GetSimulatedValue(item)));
			if (!result.Ok)
			{
				LogOfflineCWorkerIssue(result.Status.Length > 0 ? result.Status : "释放失败：离线 C worker 无响应。", force: true);
				await Task.Yield();
				return false;
			}
			await Task.Yield();
			return true;
		}

		ICanAdapter? adapter = _adapter;
		if (adapter == null)
		{
			return false;
		}

		if (!SendMonitorSession(open: true))
		{
			Log("释放失败：监控会话未打开。");
			return false;
		}

		bool ok = await SendReleaseSegmentAsync(adapter, item.Address, CancellationToken.None).ConfigureAwait(true);
		if (!ok)
		{
			return false;
		}

		if (Math.Clamp(item.Size, 1, 4) == 4)
		{
			ok = await SendReleaseSegmentAsync(adapter, item.Address + 2, CancellationToken.None).ConfigureAwait(true);
		}

		return ok;
	}

	private void RequestOfflineBusinessAssimilationAfterRelease(WatchItem item, uint beforeRaw)
	{
		_lastOfflineSimulationTickUtc = DateTime.MinValue;
		Interlocked.Increment(ref _offlineStepRequests);
		LogOfflineWritePointsForVariable(item);
		int bytes = Math.Clamp(item.Size, 1, 4);
		string beforeText = FormatDecimalRawValue(item, MaskRawValue(beforeRaw, bytes), bytes);
		Log("离线释放：" + GetWatchDisplayName(item) + "，释放前=" + beforeText + "，已请求下一拍业务逻辑接管。");
		ScheduleVisibleDataRefreshAfterScroll(immediate: true);
	}

	private void LogOfflineWritePointsForVariable(WatchItem item)
	{
		List<OfflineWriteTrace> traces = new List<OfflineWriteTrace>();
		OfflineProgramModel? model = GetOfflineProgramModel();
		string key = GetSimulationKey(item);
		if (model != null && model.WriteTraces.TryGetValue(key, out IReadOnlyList<OfflineWriteTrace>? indexedTraces))
		{
			traces = indexedTraces.Take(12).ToList();
		}
		if (traces.Count == 0)
		{
			IReadOnlyList<FunctionSourceView> sources = GetOfflineApplicationSources();
			traces = FindOfflineWriteTraces(item, sources).Take(8).ToList();
		}
		if (traces.Count == 0)
		{
			Log("离线写入点：" + GetWatchDisplayName(item) + " 暂未在应用层入口链中找到直接写入。");
			return;
		}

		string summary = string.Join("；", traces.Select(trace =>
			trace.FunctionName + " " + GetRelativePathSafe(_workDirectory, trace.FilePath) + ":" + trace.LineNumber.ToString(CultureInfo.InvariantCulture) + " " + trace.Operation));
		Log("离线写入点：" + GetWatchDisplayName(item) + " -> " + summary);
	}

	private IEnumerable<OfflineWriteTrace> FindOfflineWriteTraces(WatchItem item, IReadOnlyList<FunctionSourceView> sources)
	{
		HashSet<string> aliases = WatchIdentifierAliases(item.Name)
			.Where(alias => alias.Length > 0)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (aliases.Count == 0)
		{
			yield break;
		}

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (FunctionSourceView source in sources)
		{
			foreach (OfflineWriteTrace trace in Walk(source, 0))
			{
				yield return trace;
			}
		}

		IEnumerable<OfflineWriteTrace> Walk(FunctionSourceView source, int depth)
		{
			if (depth > 4)
			{
				yield break;
			}
			string key = source.FilePath + "|" + source.FunctionName;
			if (!visited.Add(key))
			{
				yield break;
			}

			for (int i = 0; i < source.Lines.Count; i++)
			{
				string line = StripLineComment(source.Lines[i]);
				foreach (string alias in aliases)
				{
					string pattern = @"\b" + Regex.Escape(alias) + @"\b\s*(?<op>\+\+|--|\+=|-=|=(?!=))";
					Match match = Regex.Match(line, pattern);
					if (!match.Success)
					{
						continue;
					}

					yield return new OfflineWriteTrace(
						source.FunctionName,
						source.FilePath,
						source.StartLine + i,
						alias + " " + match.Groups["op"].Value);
					break;
				}

				foreach (string functionName in FindOfflineFunctionCallsInLine(line))
				{
					if (TryLoadFunctionSource(_workDirectory, functionName, out FunctionSourceView? child) && child != null)
					{
						foreach (OfflineWriteTrace trace in Walk(child, depth + 1))
						{
							yield return trace;
						}
					}
				}
			}
		}
	}

	private static IEnumerable<string> FindOfflineFunctionCallsInLine(string line)
	{
		foreach (Match match in Regex.Matches(line, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
		{
			string name = match.Groups["name"].Value;
			if (!IsCKeyword(name))
			{
				yield return name;
			}
		}
	}

	private async Task<bool> SendReleaseSegmentAsync(ICanAdapter adapter, uint address, CancellationToken token)
	{
		await _canRequestLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			byte seq = ++_seq;
			byte[] data = MonitorProtocol.BuildReleaseForceRequest(seq, address);
			if (!TrySendMonitorFrame(adapter, data, "释放强制"))
			{
				return false;
			}

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(180.0);
			while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
			{
				int num = 0;
				CanFrame frame;
				while (num++ < 64 && DateTime.UtcNow < deadline && !token.IsCancellationRequested && adapter.TryReceive(out frame))
				{
					if (frame.Id == MonitorResponseId && MonitorProtocol.TryParseWriteAck(frame, seq, out byte status, out _, out _))
					{
						NoteCanResponse();
						if (status == 0)
						{
							return true;
						}

						Log("释放失败：" + BuildWriteStatusText(status) + $"，地址 0x{address:X8}。");
						return false;
					}
				}
				await Task.Delay(2, token).ConfigureAwait(continueOnCapturedContext: false);
			}

			NoteCanNoResponse("释放强制");
			Log("释放失败：控制器无回信。");
			return false;
		}
		finally
		{
			_canRequestLock.Release();
		}
	}

	private async Task SendRuntimeControlAsync(byte mode, string text)
	{
		if (_offlineSimulation)
		{
			if (mode == MonitorProtocol.RuntimeRun)
			{
				Volatile.Write(ref _offlineRuntimePaused, 0);
				Interlocked.Exchange(ref _offlineStepRequests, 0);
				SetOfflineRuntimeStatus("离线循环");
				if (!_running)
				{
					StartPolling();
				}
				Log("离线模式：已恢复循环运行。");
				return;
			}
			if (mode == MonitorProtocol.RuntimeStep)
			{
				Volatile.Write(ref _offlineRuntimePaused, 1);
				Interlocked.Increment(ref _offlineStepRequests);
				SetOfflineRuntimeStatus("离线单步");
				if (!_running)
				{
					StartPolling();
				}
				Log("离线模式：已请求单步执行一拍。");
				return;
			}

			Log("离线模式暂不支持业务" + text + "命令。");
			return;
		}

		if (!EnsureAdapterForDebugCommand())
		{
			return;
		}

		ICanAdapter? adapter = _adapter;
		if (adapter == null)
		{
			return;
		}

		if (!SendMonitorSession(open: true))
		{
			Log(text + "失败：监控会话未打开。");
			return;
		}

		RuntimeControlResult result = await SendRuntimeControlRequestAsync(adapter, mode, CancellationToken.None).ConfigureAwait(true);
		if (!result.Success)
		{
			Log(text + "失败：控制器无回信。");
			return;
		}

		if (result.Status != 0)
		{
			Log(text + "失败：控制器返回状态 " + result.Status + "。");
			return;
		}

		if (mode == MonitorProtocol.RuntimeRun)
		{
			Log("已恢复业务循环运行。");
		}
		else if (mode == MonitorProtocol.RuntimeStep)
		{
			Log("已发送业务单步命令：业务逻辑只放行一拍，CPU/CAN/喂狗仍继续运行。");
		}
		else
		{
			Log("已发送业务" + text + "命令。业务入口接入 CanMonitor_BusinessGate() 后生效。");
		}
	}

	private void SetOfflineRuntimeStatus(string text)
	{
		if (_statusLabel == null || _statusLabel.IsDisposed)
		{
			return;
		}
		_statusLabel.Text = text;
		_statusLabel.BackColor = _gridHeader;
		WriteConnectionState(text);
	}

	private async Task<RuntimeControlResult> SendRuntimeControlRequestAsync(ICanAdapter adapter, byte mode, CancellationToken token)
	{
		await _canRequestLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			byte seq = ++_seq;
			byte[] data = MonitorProtocol.BuildRuntimeControlRequest(seq, mode);
			if (!TrySendMonitorFrame(adapter, data, "业务运行控制"))
			{
				return new RuntimeControlResult(false, byte.MaxValue, mode);
			}

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(180.0);
			while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
			{
				int num = 0;
				CanFrame frame;
				while (num++ < 64 && DateTime.UtcNow < deadline && !token.IsCancellationRequested && adapter.TryReceive(out frame))
				{
					if (frame.Id == MonitorResponseId && MonitorProtocol.TryParseRuntimeControlAck(frame, seq, out byte status, out byte ackMode))
					{
						NoteCanResponse();
						return new RuntimeControlResult(true, status, ackMode);
					}
				}
				await Task.Delay(2, token).ConfigureAwait(continueOnCapturedContext: false);
			}

			NoteCanNoResponse("业务运行控制");
			return new RuntimeControlResult(false, byte.MaxValue, mode);
		}
		finally
		{
			_canRequestLock.Release();
		}
	}

	private bool EnsureAdapterForDebugCommand()
	{
		if (_offlineSimulation)
		{
			return true;
		}

		if (_adapter != null)
		{
			return true;
		}

		ToggleConnect();
		if (_adapter != null)
		{
			return true;
		}

		Log("未连接：无法发送强制/运行控制命令。");
		return false;
	}

	private void SetForceButtonsEnabled(bool enabled)
	{
		if (_runtimeRunButton != null)
		{
			_runtimeRunButton.Enabled = enabled;
		}
		if (_runtimeStepButton != null)
		{
			_runtimeStepButton.Enabled = enabled;
		}
	}

	private string? PromptWatchValue(WatchItem item, bool force, string currentValue)
	{
		using Form dialog = new Form
		{
			Text = force ? "保持变量" : "写入变量",
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterParent,
			MinimizeBox = false,
			MaximizeBox = false,
			ClientSize = new Size(Ui(360), Ui(130)),
			BackColor = _panel,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 9f),
			ShowInTaskbar = false
		};

		Label nameLabel = new Label
		{
			Text = GetWatchDisplayName(item),
			AutoSize = false,
			Location = new Point(Ui(14), Ui(12)),
			Size = new Size(Ui(332), Ui(24)),
			ForeColor = _ink,
			BackColor = Color.Transparent,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
		Label hintLabel = new Label
		{
			Text = force ? "保持为" : "写入一次",
			AutoSize = false,
			Location = new Point(Ui(14), Ui(42)),
			Size = new Size(Ui(70), Ui(24)),
			ForeColor = _muted,
			BackColor = Color.Transparent
		};
		TextBox valueBox = new TextBox
		{
			Location = new Point(Ui(88), Ui(40)),
			Size = new Size(Ui(258), Ui(24)),
			Text = currentValue
		};
		StyleTextBox(valueBox);

		Button okButton = PlainButton(force ? "保持" : "写入");
		okButton.Size = new Size(Ui(72), Ui(28));
		okButton.Location = new Point(Ui(186), Ui(86));
		okButton.DialogResult = DialogResult.OK;
		ApplyButtonStyle(okButton, "accent");

		Button cancelButton = PlainButton("取消");
		cancelButton.Size = new Size(Ui(72), Ui(28));
		cancelButton.Location = new Point(Ui(274), Ui(86));
		cancelButton.DialogResult = DialogResult.Cancel;

		dialog.Controls.Add(nameLabel);
		dialog.Controls.Add(hintLabel);
		dialog.Controls.Add(valueBox);
		dialog.Controls.Add(okButton);
		dialog.Controls.Add(cancelButton);
		dialog.AcceptButton = okButton;
		dialog.CancelButton = cancelButton;

		dialog.Shown += delegate
		{
			valueBox.Focus();
			valueBox.SelectAll();
		};

		return dialog.ShowDialog(this) == DialogResult.OK ? valueBox.Text.Trim() : null;
	}

	private bool TryParseForceValue(WatchItem item, string text, out uint rawValue, out string error)
	{
		rawValue = 0;
		error = "";
		string valueText = text.Trim();
		if (valueText.Length == 0)
		{
			error = "请输入数值";
			return false;
		}

		int bytes = Math.Clamp(item.Size, 1, 4);
		int bits = bytes * 8;
		bool signed = IsSignedWatchItem(item);
		if (valueText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			if (!ulong.TryParse(valueText.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out ulong hexValue))
			{
				error = "16进制格式错误";
				return false;
			}

			ulong maxHex = bits == 32 ? 0xFFFFFFFFUL : ((1UL << bits) - 1UL);
			if (hexValue > maxHex)
			{
				error = "数值超出 " + bytes + " 字节范围";
				return false;
			}

			rawValue = (uint)hexValue;
			return true;
		}

		if (IsFloatWatchItem(item) && bytes == 4)
		{
			if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double floatValue) &&
				!double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out floatValue))
			{
				error = "浮点数格式错误";
				return false;
			}

			rawValue = BitConverter.SingleToUInt32Bits((float)floatValue);
			return true;
		}

		if (!long.TryParse(valueText, out long signedValue))
		{
			error = "请输入十进制或 0x 开头的 16 进制";
			return false;
		}

		if (signed)
		{
			long min = -(1L << (bits - 1));
			long max = (1L << (bits - 1)) - 1L;
			if (bits == 32)
			{
				min = int.MinValue;
				max = int.MaxValue;
			}
			if (signedValue < min || signedValue > max)
			{
				error = $"有符号范围 {min} 到 {max}";
				return false;
			}

			rawValue = signedValue < 0
				? (uint)((bits == 32 ? 0x100000000L : (1L << bits)) + signedValue)
				: (uint)signedValue;
			return true;
		}

		long unsignedMax = bits == 32 ? uint.MaxValue : ((1L << bits) - 1L);
		if (signedValue < 0 || signedValue > unsignedMax)
		{
			error = $"无符号范围 0 到 {unsignedMax}";
			return false;
		}

		rawValue = (uint)signedValue;
		return true;
	}

	private static string BuildWriteStatusText(byte status)
	{
		return status switch
		{
			0 => "成功",
			1 => "地址不在 RAM",
			2 => "长度不支持",
			3 => "强制表已满",
			4 => "监控会话未打开",
			_ => "状态 " + status,
		};
	}

	private async Task PollTraceIfDue(CancellationToken token)
	{
		if (_offlineSimulation)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		if (now < _nextTracePollUtc || now < _traceBackoffUntilUtc)
		{
			return;
		}

		_nextTracePollUtc = now.AddMilliseconds(TracePollIntervalMs);
		bool success = await PollTrace(token).ConfigureAwait(continueOnCapturedContext: false);
		if (success)
		{
			_traceMissCount = 0;
			_traceBackoffUntilUtc = DateTime.MinValue;
			return;
		}

		_traceMissCount++;
		if (_traceMissCount >= 2)
		{
			_traceBackoffUntilUtc = DateTime.UtcNow.AddMilliseconds(TraceNoResponseBackoffMs);
		}
	}

	private async Task<bool> PollTrace(CancellationToken token)
	{
		ICanAdapter adapter = _adapter;
		if (adapter == null)
		{
			return false;
		}
		await _canRequestLock.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			byte seq = ++_seq;
			byte[] data = MonitorProtocol.BuildTraceRequest(seq);
			if (!TrySendMonitorFrame(adapter, data, "读取运行位置"))
			{
				NoteCanNoResponse("发送运行位置失败");
				return false;
			}
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(TraceResponseTimeoutMs);
			while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
			{
				int num = 0;
				CanFrame frame;
				while (num++ < 64 && DateTime.UtcNow < deadline && !token.IsCancellationRequested && adapter.TryReceive(out frame))
				{
					if (frame.Id == MonitorResponseId && MonitorProtocol.TryParseTraceAck(frame, seq, out var traceId))
					{
						NoteCanResponse();
						UpdateTrace(traceId);
						return true;
					}
				}
				await Task.Delay(1, token).ConfigureAwait(continueOnCapturedContext: false);
			}
			return false;
		}
		catch
		{
			return false;
		}
		finally
		{
			_canRequestLock.Release();
		}
	}

	private void UpdateTrace(ushort traceId)
	{
		if (traceId == _lastTraceId)
		{
			return;
		}
		_lastTraceId = traceId;
		BeginInvoke(delegate
		{
			if (_runtimeLocationLabel != null)
			{
				string value;
				if (traceId == 0)
				{
					_runtimeLocationLabel.Text = GetProgramEntryDisplayName();
				}
				else if (traceId == 8193)
				{
					_runtimeLocationLabel.Text = "主循环";
				}
				else if (_traceLabels.TryGetValue(traceId, out value) && !string.IsNullOrWhiteSpace(value))
				{
					_runtimeLocationLabel.Text = value;
				}
				else
				{
					_runtimeLocationLabel.Text = "运行中";
				}
				if (traceId != 0)
				{
					HighlightTraceRows(traceId);
				}
			}
		});
	}

	private void HighlightTraceRows(ushort traceId)
	{
		_flowChart?.SetHighlight(traceId);
	}

	private void FlowChartNodeClick(object? sender, FlowChartNode node)
	{
		HandleFlowChartNodeClick(sender, node, forceEnter: false);
	}

	private void FlowChartNodeDoubleClick(object? sender, FlowChartNode node)
	{
		HandleFlowChartNodeClick(sender, node, forceEnter: false);
	}

	private void HandleFlowChartNodeClick(object? sender, FlowChartNode node, bool forceEnter)
	{
		try
		{
			if (!forceEnter && sender == _flowChart && _flowChart.LastClickHitTreeExpander && IsProgramTreeExpandableNode(node))
			{
				ToggleProgramTreeNode(node);
				return;
			}

			List<string> candidates = ExtractFunctionNameCandidates(node).ToList();
			if (candidates.Count == 0)
			{
				Log("该节点没有可打开的函数。");
				return;
			}
			if (string.IsNullOrWhiteSpace(_workDirectory) || !Directory.Exists(_workDirectory))
			{
				Log("工作目录无效，无法打开函数：" + candidates[0]);
				return;
			}
			ClearProgramSearchStateOnly();
			bool ctrlPressed = (ModifierKeys & Keys.Control) == Keys.Control;
			bool isProgramTreeNode = sender == _flowChart && node.Kind.StartsWith("tree", StringComparison.OrdinalIgnoreCase);

			if (isProgramTreeNode)
			{
				foreach (string functionName in candidates)
				{
					if (!ctrlPressed)
					{
						if (TryLocateProgramTreeFunctionInCode(functionName, allowDefinitionFallback: false))
						{
							Log("已定位函数引用：" + functionName);
						}
						else
						{
							Log("未找到函数引用：" + functionName + "。按住 Ctrl 点击可进入函数体。");
						}
						return;
					}

					if (TryLoadFunctionSource(_workDirectory, functionName, out FunctionSourceView? treeSource) && treeSource != null)
					{
						bool sameCurrent = _currentFunctionSource != null &&
							treeSource.FunctionName.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase) &&
							treeSource.FilePath.Equals(_currentFunctionSource.FilePath, StringComparison.OrdinalIgnoreCase);
						ShowFunctionSource(
							treeSource,
							pushCurrent: ctrlPressed && _currentFunctionSource != null && !sameCurrent,
							clearForward: ctrlPressed && !sameCurrent);
						SelectApproximateLineInFunction(treeSource.StartLine, scheduleViewportCorrection: false);
						Log((ctrlPressed ? "已进入函数：" : "已定位程序透视节点：") + $"{functionName}  {treeSource.FilePath}:{treeSource.StartLine}");
						return;
					}
				}
			}

			foreach (string functionName in candidates)
			{
				if (!ctrlPressed && TryLocateProgramTreeFunctionInCode(functionName))
				{
					Log("已定位程序透视节点：" + functionName);
					return;
				}

				if (TryLoadFunctionSource(_workDirectory, functionName, out FunctionSourceView? sourceView) && sourceView != null)
				{
					ShowFunctionSource(
						sourceView,
						pushCurrent: ctrlPressed && _currentFunctionSource != null,
						clearForward: ctrlPressed);
					Log((ctrlPressed ? "已进入函数：" : "已定位程序透视节点：") + $"{functionName}  {sourceView.FilePath}:{sourceView.StartLine}");
					return;
				}
			}

			Log("未找到函数位置：" + string.Join(" / ", candidates.Take(4)));
		}
		catch (Exception ex)
		{
			Log("打开函数失败：" + ex.Message);
		}
	}

	private static bool IsProgramTreeExpandableNode(FlowChartNode node)
	{
		return node.Kind.StartsWith("tree", StringComparison.OrdinalIgnoreCase) &&
			(node.Kind.Contains("Expanded", StringComparison.OrdinalIgnoreCase) ||
			 node.Kind.Contains("Collapsed", StringComparison.OrdinalIgnoreCase));
	}

	private void ToggleProgramTreeNode(FlowChartNode node)
	{
		string key = GetProgramTreeNodeKey(node);
		if (string.IsNullOrWhiteSpace(key))
		{
			return;
		}

		if (node.Kind.Contains("Expanded", StringComparison.OrdinalIgnoreCase))
		{
			_expandedProgramTreeNodes.Remove(key);
			_collapsedProgramTreeNodes.Add(key);
		}
		else
		{
			_collapsedProgramTreeNodes.Remove(key);
			_expandedProgramTreeNodes.Add(key);
		}

		RefreshProgramTreeFromSnapshot();
	}

	private static string GetProgramTreeNodeKey(FlowChartNode node)
	{
		return !string.IsNullOrWhiteSpace(node.FunctionName)
			? node.FunctionName.Trim()
			: ExtractFunctionNameFromTreeText(node.Text);
	}

	private bool IsProgramTreeNodeExpanded(ProgramCallGraphNode node, int depth)
	{
		string key = node.Name.Trim();
		if (_collapsedProgramTreeNodes.Contains(key))
		{
			return false;
		}
		if (_expandedProgramTreeNodes.Contains(key))
		{
			return true;
		}
		return depth <= 1;
	}

	private void RefreshProgramTreeFromSnapshot()
	{
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null || !snapshot.Success || _flowChart == null)
		{
			return;
		}

		if (snapshot.CallGraphNodes.Count > 0)
		{
			UpdateCallRelationChart(snapshot.CallGraphNodes, snapshot.CallGraphEdges);
		}
		else
		{
			UpdateFlowChart(snapshot.FrameworkSteps);
		}
	}

	private bool TryLocateProgramTreeFunctionInCode(string functionName, bool allowDefinitionFallback = true)
	{
		if (string.IsNullOrWhiteSpace(functionName))
		{
			return false;
		}

		_programTreeFocusedFunction = functionName;
		RefreshProgramTreeFromSnapshot();
		_flowChart?.ScrollFunctionIntoView(functionName);

		if (_currentFunctionSource != null)
		{
			FunctionSourceView activeView = GetCodeViewSource() ?? _currentFunctionSource;
			if (functionName.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase))
			{
				if (!allowDefinitionFallback)
				{
					return false;
				}
				FocusFunctionSourceLine(_currentFunctionSource.StartLine);
				return true;
			}
			if (TryFindCallLineInSource(activeView, functionName, out int activeLine))
			{
				FocusFunctionSourceLine(activeLine, functionName);
				return true;
			}
		}

		ProgramCallGraphNode? targetNode = FindGraphNodeForFunction(functionName, "");
		if (targetNode != null)
		{
			foreach (ProgramCallGraphNode caller in GetGraphLinkedNodes(targetNode.Id, callers: true)
				.Where(n => !IsProgramGraphNoiseNode(n))
				.OrderByDescending(IsPreferredGraphRoot)
				.ThenByDescending(GetBusinessNodeScore)
				.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
				.Take(8))
			{
				if (TryLoadFunctionSource(_workDirectory, caller.Name, out FunctionSourceView? callerSource) &&
					callerSource != null &&
					TryFindCallLineInSource(callerSource, functionName, out int callLine))
				{
					ShowFunctionSource(callerSource, pushCurrent: false, clearForward: false);
					FocusFunctionSourceLine(callLine, functionName);
					return true;
				}
			}
		}

		if (allowDefinitionFallback && TryLoadFunctionSource(_workDirectory, functionName, out FunctionSourceView? sourceView) && sourceView != null)
		{
			ShowFunctionSource(sourceView, pushCurrent: false, clearForward: false);
			return true;
		}

		return false;
	}

	private static bool TryFindCallLineInSource(FunctionSourceView source, string functionName, out int absoluteLine)
	{
		absoluteLine = 0;
		if (string.IsNullOrWhiteSpace(functionName))
		{
			return false;
		}

		string pattern = @"\b" + Regex.Escape(functionName) + @"\s*\(";
		for (int i = 0; i < source.Lines.Count; i++)
		{
			string line = source.Lines[i];
			if (!Regex.IsMatch(line, pattern))
			{
				continue;
			}
			if (LooksLikeFunctionDefinitionLine(line, functionName))
			{
				continue;
			}

			absoluteLine = source.StartLine + i;
			return true;
		}

		return false;
	}

	private static bool LooksLikeFunctionDefinitionLine(string line, string functionName)
	{
		string trimmed = line.TrimStart();
		if (!Regex.IsMatch(trimmed, @"\b" + Regex.Escape(functionName) + @"\s*\("))
		{
			return false;
		}

		return Regex.IsMatch(trimmed, @"^(?:static\s+|extern\s+|inline\s+|const\s+|volatile\s+|unsigned\s+|signed\s+)*(?:void|int|short|long|char|float|double|bool|uint\d*_t|int\d*_t|u\d+|s\d+|[A-Za-z_][A-Za-z0-9_]*\s*\*?)\s+[*\s]*" + Regex.Escape(functionName) + @"\s*\(");
	}

	private void FunctionAnalysisNodeClick(object? sender, FlowChartNode node)
	{
		try
		{
			if (_currentFunctionSource == null)
			{
				return;
			}

			bool ctrlPressed = (ModifierKeys & Keys.Control) == Keys.Control;
			bool treeNode = node.Kind.StartsWith("tree", StringComparison.OrdinalIgnoreCase);
			if (treeNode && !string.IsNullOrWhiteSpace(node.FunctionName))
			{
				if (node.FunctionName.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase))
				{
					FocusFunctionSourceLine(_currentFunctionSource.StartLine);
					return;
				}
				if (!string.IsNullOrWhiteSpace(_workDirectory) &&
					Directory.Exists(_workDirectory) &&
					TryLoadFunctionSource(_workDirectory, node.FunctionName, out FunctionSourceView? treeSource) &&
					treeSource != null)
				{
					_activeProgramSearchKeyword = "";
					_focusedVariableName = "";
					_activeProgramSearchLine = treeSource.StartLine;
					ShowFunctionSource(treeSource, pushCurrent: ctrlPressed, clearForward: ctrlPressed);
					SelectApproximateLineInFunction(treeSource.StartLine);
					Log((ctrlPressed ? "已从函数位置进入：" : "已从函数位置定位：") + $"{node.FunctionName}  {treeSource.FilePath}:{treeSource.StartLine}");
					return;
				}
			}

			if (ctrlPressed &&
				!string.IsNullOrWhiteSpace(node.FunctionName) &&
				!node.FunctionName.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase) &&
				!string.IsNullOrWhiteSpace(_workDirectory) &&
				Directory.Exists(_workDirectory) &&
				TryLoadFunctionSource(_workDirectory, node.FunctionName, out FunctionSourceView? calledSource) &&
				calledSource != null)
			{
				_activeProgramSearchKeyword = "";
				_focusedVariableName = "";
				_activeProgramSearchLine = calledSource.StartLine;
				ShowFunctionSource(calledSource, pushCurrent: true, clearForward: true);
				SelectApproximateLineInFunction(calledSource.StartLine);
				Log($"已从函数分析进入：{node.FunctionName}  {calledSource.FilePath}:{calledSource.StartLine}");
				return;
			}

			if (TryGetFunctionAnalysisNodeLine(node, out int sourceLine))
			{
				FocusFunctionSourceLine(sourceLine);
				Log($"已定位代码行：{_currentFunctionSource.FunctionName}:{sourceLine}");
				return;
			}

			if (!string.IsNullOrWhiteSpace(node.FunctionName) &&
				node.FunctionName.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase))
			{
				FocusFunctionSourceLine(_currentFunctionSource.StartLine);
			}
		}
		catch (Exception ex)
		{
			Log("函数分析定位失败：" + ex.Message);
		}
	}

	private static bool TryGetFunctionAnalysisNodeLine(FlowChartNode node, out int line)
	{
		line = 0;
		Match match = Regex.Match(node.Id, @"^L(?<line>\d+)");
		return match.Success &&
			int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) &&
			line > 0;
	}

	private void FocusFunctionSourceLine(int absoluteLine, string locateTargetFunction = "")
	{
		if (_currentFunctionSource == null || _functionCodeBox == null)
		{
			return;
		}

		_activeProgramSearchKeyword = "";
		_focusedVariableName = "";
		_programTreeLocateTargetFunction = locateTargetFunction.Trim();
		FunctionSourceView codeView = GetCodeViewSource() ?? _currentFunctionSource;
		if ((absoluteLine < codeView.StartLine || absoluteLine > GetSourceEndLine(codeView)) &&
			_currentFunctionSource != null)
		{
			FunctionSourceView? previousCodeView = _codeViewSource;
			_codeViewSource = BuildFunctionCodeContextSource(_currentFunctionSource);
			codeView = GetCodeViewSource() ?? _currentFunctionSource;
			if (!IsSameRenderedCodeContext(previousCodeView, _codeViewSource))
			{
				_lastFunctionCodeText = "";
				_lastDataCodeText = "";
			}
		}
		_activeProgramSearchLine = Math.Clamp(
			absoluteLine,
			codeView.StartLine,
			GetSourceEndLine(codeView));
		RenderFunctionSource();
		SelectApproximateLineInFunction(_activeProgramSearchLine);
		RenderDataFunctionMirror(resetScroll: true);
		UpdateFunctionCodeTitle();
	}

	private static IEnumerable<string> ExtractFunctionNameCandidates(FlowChartNode node)
	{
		var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (!string.IsNullOrWhiteSpace(node.FunctionName) && used.Add(node.FunctionName))
		{
			yield return node.FunctionName;
		}

		foreach (Match match in Regex.Matches(node.Text, @"[A-Za-z_][A-Za-z0-9_]*"))
		{
			string name = match.Value;
			if (name.Length < 2 || IsMonitorInternalFunctionName(name))
			{
				continue;
			}
			if (used.Add(name))
			{
				yield return name;
			}
		}
	}

	private bool TryLoadFunctionSource(string root, string functionName, out FunctionSourceView? sourceView)
	{
		if (TryLoadFunctionSourceFromGraph(functionName, out sourceView))
		{
			return true;
		}

		EnsureFunctionIndex(root);
		if (_functionIndex.TryGetValue(functionName, out FunctionIndexEntry? entry) &&
			TryLoadFunctionSourceFromFile(entry.FilePath, functionName, out sourceView))
		{
			return true;
		}

		foreach (string file in EnumerateSourceFilesForOpen(root))
		{
			if (TryLoadFunctionSourceFromFile(file, functionName, out sourceView))
			{
				return true;
			}
		}
		sourceView = null;
		return false;
	}

	private bool TryLoadFunctionSourceFromGraph(string functionName, out FunctionSourceView? sourceView)
	{
		sourceView = null;
		ProgramCallGraphNode? node = FindGraphNodeForFunction(functionName, "");
		if (node == null || string.IsNullOrWhiteSpace(node.FilePath))
		{
			return false;
		}

		string filePath = ResolveGraphNodeFilePath(_workDirectory, node.FilePath);

		return filePath.Length > 0 &&
			File.Exists(filePath) &&
			TryLoadFunctionSourceFromFile(filePath, functionName, out sourceView);
	}

	private bool TryLoadFunctionSourceFromFile(string file, string functionName, out FunctionSourceView? sourceView)
	{
		string text;
		try
		{
			text = ReadSourceTextCached(file);
		}
		catch
		{
			sourceView = null;
			return false;
		}

		Match match = FindFunctionDefinitionMatch(text, functionName);
		if (!match.Success)
		{
			sourceView = null;
			return false;
		}
		int lineStart = text.LastIndexOf('\n', match.Index);
		lineStart = lineStart < 0 ? 0 : lineStart + 1;
		int openBrace = SkipTriviaAndComments(text, match.Index + match.Length);
		int closeBrace = FindMatchingBrace(text, openBrace);
		if (openBrace < 0 || closeBrace < 0)
		{
			sourceView = null;
			return false;
		}
		int startLine = 1 + text.Take(lineStart).Count(c => c == '\n');
		string sourceText = text.Substring(lineStart, closeBrace - lineStart + 1).Replace("\r\n", "\n").Replace('\r', '\n');
		sourceView = new FunctionSourceView(functionName, file, startLine, sourceText.Split('\n'), lineStart, closeBrace - lineStart + 1);
		return true;
	}

	private FunctionSourceView? GetCodeViewSource()
	{
		return _codeViewSource ?? _currentFunctionSource;
	}

	private FunctionSourceView BuildFunctionCodeContextSource(FunctionSourceView functionSource)
	{
		try
		{
			string text = ReadSourceTextCached(functionSource.FilePath);
			string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			if (lines.Length == 0)
			{
				return functionSource;
			}

			return new FunctionSourceView(functionSource.FunctionName, functionSource.FilePath, 1, lines, 0, text.Length);
		}
		catch
		{
			return functionSource;
		}
	}

	private static int GetSourceEndLine(FunctionSourceView source)
	{
		return source.StartLine + Math.Max(0, source.Lines.Count - 1);
	}

	private void UpdateFunctionCodeTitle()
	{
		if (_functionCodeTitle == null || _currentFunctionSource == null)
		{
			return;
		}

		FunctionSourceView codeView = GetCodeViewSource() ?? _currentFunctionSource;
		string relativePath = GetRelativePathSafe(_workDirectory, _currentFunctionSource.FilePath);
		string contextText = codeView.StartLine == 1 && codeView.Lines.Count > _currentFunctionSource.Lines.Count
			? $"{_currentFunctionSource.StartLine}    全文 1-{GetSourceEndLine(codeView)}"
			: _currentFunctionSource.StartLine.ToString(CultureInfo.InvariantCulture);
		string locateText = !string.IsNullOrWhiteSpace(_programTreeLocateTargetFunction) &&
			!_programTreeLocateTargetFunction.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase)
			? $"    定位：{_programTreeLocateTargetFunction}"
			: "";
		_functionCodeTitle.Text = $"{_currentFunctionSource.FunctionName}{locateText}    {relativePath}:{contextText}";
	}

	private bool RefreshCurrentFunctionSourceFromDisk(bool logResult)
	{
		if (_currentFunctionSource == null || _functionCodePanel == null || !_functionCodePanel.Visible)
		{
			return false;
		}

		FunctionSourceView oldSource = _currentFunctionSource;
		if (!File.Exists(oldSource.FilePath))
		{
			if (logResult)
			{
				Log("当前代码文件不存在，无法刷新：" + oldSource.FilePath);
			}
			return false;
		}

		bool loaded = TryLoadFunctionSourceFromFile(oldSource.FilePath, oldSource.FunctionName, out FunctionSourceView? refreshed) && refreshed != null;
		if (!loaded)
		{
			loaded = TryLoadSourceSnippet(oldSource.FilePath, oldSource.StartLine, out refreshed) && refreshed != null;
		}
		if (!loaded || refreshed == null)
		{
			if (logResult)
			{
				Log("当前代码页未找到可刷新内容：" + oldSource.FunctionName);
			}
			return false;
		}

		_currentFunctionSource = refreshed;
		_codeViewSource = BuildFunctionCodeContextSource(refreshed);
		_currentFunctionIdentifiers = BuildIdentifierSet(refreshed.Lines);
		_lastFunctionCodeText = "";
		_lastDataCodeText = "";
		_lastFunctionAnalysisSignature = "";
		_lastVisibleValuesText = "";
		_lastVisibleConditionSignature = "";
		_lastVisibleRangeSignature = "";
		_resetFunctionScrollOnNextRender = false;
		UpdateFunctionCodeTitle();
		RenderFunctionSource();
		RenderFunctionAnalysis(force: true);
		if (logResult)
		{
			Log("当前代码页已刷新：" + refreshed.FunctionName);
		}
		return true;
	}

	private void EnsureFunctionIndex(string root)
	{
		if (_functionIndex.Count > 0 && _functionIndexRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		BuildFunctionIndex(root);
	}

	private void ClearFunctionIndex()
	{
		_functionIndex.Clear();
		_sourceTextCache.Clear();
		_functionIndexRoot = "";
		ClearFunctionHoverCache();
	}

	private void BuildFunctionIndex(string root)
	{
		_functionIndex = BuildFunctionIndexSnapshot(root);
		_functionIndexRoot = root;
		ClearFunctionHoverCache();
	}

	private void WarmFunctionIndex(string root)
	{
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
		{
			return;
		}

		Task.Run(() => BuildFunctionIndexSnapshot(root)).ContinueWith(task =>
		{
			if (task.Status != TaskStatus.RanToCompletion || IsDisposed)
			{
				return;
			}

			try
			{
				BeginInvoke((Action)(() =>
				{
					if (_workDirectory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
						(_functionIndex.Count == 0 || !_functionIndexRoot.Equals(root, StringComparison.OrdinalIgnoreCase)))
					{
						_functionIndex = task.Result;
						_functionIndexRoot = root;
						ClearFunctionHoverCache();
					}
				}));
			}
			catch
			{
			}
		}, TaskScheduler.Default);
	}

	private static Dictionary<string, FunctionIndexEntry> BuildFunctionIndexSnapshot(string root)
	{
		var index = new Dictionary<string, FunctionIndexEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (string file in EnumerateSourceFilesForOpen(root))
		{
			string text;
			try
			{
				text = ReadSourceText(file);
			}
			catch
			{
				continue;
			}

			string codeText = MaskCommentsAndLiteralsPreserveLength(text);
			foreach (Match match in Regex.Matches(codeText, @"(?m)^[\t ]*(?:[A-Za-z_][A-Za-z0-9_\s\*\(\),\[\]]+?[\s\*]+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)", RegexOptions.IgnoreCase))
			{
				string name = match.Groups["name"].Value;
				if (name.Length == 0 || IsCKeyword(name))
				{
					continue;
				}

				int afterSignature = SkipTriviaAndComments(text, match.Index + match.Length);
				if (afterSignature < text.Length && text[afterSignature] == '{' && !index.ContainsKey(name))
				{
					index[name] = new FunctionIndexEntry(name, file);
				}
			}
		}

		return index;
	}

	private void ClearFunctionHoverCache()
	{
		_lastFunctionHoverCharIndex = -1;
		_lastFunctionHoverIdentifier = "";
		_lastFunctionHoverNavigable = false;
		_lastFunctionHoverContextName = "";
		_lastFunctionHoverContextUtc = DateTime.MinValue;
		ClearScintillaFunctionHoverHighlight();
	}

	private void SetScintillaFunctionHoverHighlight(int start, int length, bool navigable)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		ClearScintillaFunctionHoverHighlight();
		if (!navigable || length <= 0 || start < 0 || start >= _codeEditor.TextLength)
		{
			return;
		}

		int safeLength = Math.Min(length, _codeEditor.TextLength - start);
		if (safeLength <= 0)
		{
			return;
		}

		_codeEditor.IndicatorCurrent = ScintillaIndicatorFunctionHover;
		_codeEditor.IndicatorFillRange(start, safeLength);
		_lastScintillaFunctionHoverRange = (start, safeLength);
	}

	private void ClearScintillaFunctionHoverHighlight()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || !_lastScintillaFunctionHoverRange.HasValue)
		{
			_lastScintillaFunctionHoverRange = null;
			return;
		}

		(int start, int length) = _lastScintillaFunctionHoverRange.Value;
		_codeEditor.IndicatorCurrent = ScintillaIndicatorFunctionHover;
		if (start >= 0 && start < _codeEditor.TextLength)
		{
			_codeEditor.IndicatorClearRange(start, Math.Min(length, _codeEditor.TextLength - start));
		}
		_lastScintillaFunctionHoverRange = null;
	}

	private void WarmSourceTextCacheFromSnapshot(string directory, ProgramGraphSnapshot snapshot)
	{
		if (!snapshot.Success || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return;
		}

		List<string> files = GetAllGraphNodes(snapshot)
			.Select(node => ResolveGraphNodeFilePath(directory, node.FilePath))
			.Where(path => path.Length > 0 && File.Exists(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(16)
			.ToList();
		if (files.Count == 0)
		{
			return;
		}

		Task.Run(() =>
		{
			var entries = new List<(string Path, SourceTextCacheEntry Entry)>();
			foreach (string file in files)
			{
				try
				{
					string text = ReadSourceText(file);
					FileInfo info = new FileInfo(file);
					entries.Add((file, new SourceTextCacheEntry(text, info.LastWriteTimeUtc, info.Length)));
				}
				catch
				{
				}
			}
			return entries;
		}).ContinueWith(task =>
		{
			if (task.Status != TaskStatus.RanToCompletion || IsDisposed)
			{
				return;
			}

			try
			{
				BeginInvoke((Action)(() =>
				{
					foreach ((string path, SourceTextCacheEntry entry) in task.Result)
					{
						try
						{
							FileInfo info = new FileInfo(path);
							if (info.LastWriteTimeUtc == entry.LastWriteUtc && info.Length == entry.Length)
							{
								_sourceTextCache[path] = entry;
							}
						}
						catch
						{
						}
					}
				}));
			}
			catch
			{
			}
		}, TaskScheduler.Default);
	}

	private static string ResolveGraphNodeFilePath(string directory, string filePath)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			return "";
		}
		return Path.IsPathRooted(filePath) ? filePath : Path.Combine(directory, filePath);
	}

	private static bool IsCKeyword(string name)
	{
		return IsCKeywordToken(name.ToLowerInvariant());
	}

	private static bool IsCKeywordToken(string name)
	{
		return name.Equals("auto", StringComparison.Ordinal) ||
			name.Equals("break", StringComparison.Ordinal) ||
			name.Equals("case", StringComparison.Ordinal) ||
			name.Equals("char", StringComparison.Ordinal) ||
			name.Equals("const", StringComparison.Ordinal) ||
			name.Equals("continue", StringComparison.Ordinal) ||
			name.Equals("default", StringComparison.Ordinal) ||
			name.Equals("do", StringComparison.Ordinal) ||
			name.Equals("double", StringComparison.Ordinal) ||
			name.Equals("else", StringComparison.Ordinal) ||
			name.Equals("enum", StringComparison.Ordinal) ||
			name.Equals("extern", StringComparison.Ordinal) ||
			name.Equals("float", StringComparison.Ordinal) ||
			name.Equals("for", StringComparison.Ordinal) ||
			name.Equals("goto", StringComparison.Ordinal) ||
			name.Equals("if", StringComparison.Ordinal) ||
			name.Equals("int", StringComparison.Ordinal) ||
			name.Equals("long", StringComparison.Ordinal) ||
			name.Equals("register", StringComparison.Ordinal) ||
			name.Equals("return", StringComparison.Ordinal) ||
			name.Equals("short", StringComparison.Ordinal) ||
			name.Equals("signed", StringComparison.Ordinal) ||
			name.Equals("sizeof", StringComparison.Ordinal) ||
			name.Equals("static", StringComparison.Ordinal) ||
			name.Equals("struct", StringComparison.Ordinal) ||
			name.Equals("switch", StringComparison.Ordinal) ||
			name.Equals("typedef", StringComparison.Ordinal) ||
			name.Equals("union", StringComparison.Ordinal) ||
			name.Equals("unsigned", StringComparison.Ordinal) ||
			name.Equals("void", StringComparison.Ordinal) ||
			name.Equals("volatile", StringComparison.Ordinal) ||
			name.Equals("while", StringComparison.Ordinal);
	}

	private static Match FindFunctionDefinitionMatch(string text, string functionName)
	{
		if (IsCKeyword(functionName))
		{
			return Match.Empty;
		}

		string codeText = MaskCommentsAndLiteralsPreserveLength(text);
		foreach (Match match in Regex.Matches(codeText, $@"\b{Regex.Escape(functionName)}\s*\([^;{{}}]*\)", RegexOptions.IgnoreCase))
		{
			int afterSignature = SkipTriviaAndComments(text, match.Index + match.Length);
			if (afterSignature < text.Length && text[afterSignature] == '{')
			{
				return match;
			}
		}

		return Match.Empty;
	}

	private static int SkipTriviaAndComments(string text, int index)
	{
		int i = index;
		while (i < text.Length)
		{
			while (i < text.Length && char.IsWhiteSpace(text[i]))
			{
				i++;
			}

			if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
			{
				i += 2;
				while (i < text.Length && text[i] != '\n')
				{
					i++;
				}
				continue;
			}

			if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
			{
				i += 2;
				while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
				{
					i++;
				}
				if (i + 1 < text.Length)
				{
					i += 2;
				}
				continue;
			}

			break;
		}

		return i;
	}

	private static string MaskCommentsAndLiteralsPreserveLength(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}

		var builder = new StringBuilder(text.Length);
		bool inLineComment = false;
		bool inBlockComment = false;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			char next = i + 1 < text.Length ? text[i + 1] : '\0';
			if (inLineComment)
			{
				if (c == '\n')
				{
					inLineComment = false;
					builder.Append(c);
				}
				else
				{
					builder.Append(' ');
				}
				continue;
			}
			if (inBlockComment)
			{
				if (c == '*' && next == '/')
				{
					builder.Append(' ');
					builder.Append(' ');
					i++;
					inBlockComment = false;
				}
				else
				{
					builder.Append(c == '\n' || c == '\r' ? c : ' ');
				}
				continue;
			}
			if (inString || inChar)
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
				if (inString && c == '"')
				{
					inString = false;
					builder.Append(' ');
					continue;
				}
				if (inChar && c == '\'')
				{
					inChar = false;
					builder.Append(' ');
					continue;
				}
				builder.Append(c == '\n' || c == '\r' ? c : ' ');
				continue;
			}

			if (c == '/' && next == '/')
			{
				builder.Append(' ');
				builder.Append(' ');
				i++;
				inLineComment = true;
				continue;
			}
			if (c == '/' && next == '*')
			{
				builder.Append(' ');
				builder.Append(' ');
				i++;
				inBlockComment = true;
				continue;
			}
			if (c == '"')
			{
				inString = true;
				builder.Append(' ');
				continue;
			}
			if (c == '\'')
			{
				inChar = true;
				builder.Append(' ');
				continue;
			}

			builder.Append(c);
		}

		return builder.ToString();
	}

	private string ReadSourceTextCached(string filePath)
	{
		FileInfo fileInfo = new FileInfo(filePath);
		DateTime lastWriteUtc = fileInfo.LastWriteTimeUtc;
		long length = fileInfo.Length;
		if (_sourceTextCache.TryGetValue(filePath, out SourceTextCacheEntry? cached) &&
			cached.LastWriteUtc == lastWriteUtc &&
			cached.Length == length)
		{
			return cached.Text;
		}

		string text = ReadSourceText(filePath);
		_sourceTextCache[filePath] = new SourceTextCacheEntry(text, lastWriteUtc, length);
		return text;
	}

	private static string ReadSourceText(string filePath)
	{
		return ReadSourceText(filePath, out _);
	}

	private static string ReadSourceText(string filePath, out Encoding encoding)
	{
		byte[] bytes = File.ReadAllBytes(filePath);
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
		{
			encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
			return Encoding.UTF8.GetString(bytes);
		}
		if (bytes.Length >= 2)
		{
			if (bytes[0] == 0xFF && bytes[1] == 0xFE)
			{
				encoding = Encoding.Unicode;
				return Encoding.Unicode.GetString(bytes);
			}
			if (bytes[0] == 0xFE && bytes[1] == 0xFF)
			{
				encoding = Encoding.BigEndianUnicode;
				return Encoding.BigEndianUnicode.GetString(bytes);
			}
		}
		try
		{
			encoding = new UTF8Encoding(false, true);
			return encoding.GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			encoding = Encoding.GetEncoding("GB18030");
			return encoding.GetString(bytes);
		}
	}

	private static int FindMatchingBrace(string text, int openBrace)
	{
		if (openBrace < 0 || openBrace >= text.Length)
		{
			return -1;
		}
		int depth = 0;
		bool inLineComment = false;
		bool inBlockComment = false;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = openBrace; i < text.Length; i++)
		{
			char c = text[i];
			char next = i + 1 < text.Length ? text[i + 1] : '\0';
			if (inLineComment)
			{
				if (c == '\n')
				{
					inLineComment = false;
				}
				continue;
			}
			if (inBlockComment)
			{
				if (c == '*' && next == '/')
				{
					inBlockComment = false;
					i++;
				}
				continue;
			}
			if (inString || inChar)
			{
				if (escape)
				{
					escape = false;
					continue;
				}
				if (c == '\\')
				{
					escape = true;
					continue;
				}
				if (inString && c == '"')
				{
					inString = false;
				}
				else if (inChar && c == '\'')
				{
					inChar = false;
				}
				continue;
			}
			if (c == '/' && next == '/')
			{
				inLineComment = true;
				i++;
				continue;
			}
			if (c == '/' && next == '*')
			{
				inBlockComment = true;
				i++;
				continue;
			}
			if (c == '"')
			{
				inString = true;
				continue;
			}
			if (c == '\'')
			{
				inChar = true;
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

	private void ShowFunctionSource(FunctionSourceView sourceView, bool pushCurrent = false, bool clearForward = false, CodeViewSnapshot? restoreSnapshot = null)
	{
		if (pushCurrent)
		{
			CodeViewSnapshot? currentSnapshot = CaptureCodeViewSnapshot();
			if (currentSnapshot.HasValue)
			{
				_functionHistory.Push(currentSnapshot.Value);
			}
		}
		if (clearForward)
		{
			_functionForwardHistory.Clear();
		}
		FunctionSourceView? previousCodeView = _codeViewSource;
		FunctionSourceView nextCodeView = BuildFunctionCodeContextSource(sourceView);
		bool sameRenderedCodeContext = IsSameRenderedCodeContext(previousCodeView, nextCodeView);
		_currentFunctionSource = sourceView;
		int navigationVersion = unchecked(++_functionNavigationVersion);
		_codeViewSource = nextCodeView;
		_programTreeFocusedFunction = sourceView.FunctionName;
		_flowChart?.SetFocusedFunction(_programTreeFocusedFunction);
		_programTreeLocateTargetFunction = "";
		_lastProgramTreeHoverKey = "";
		_currentFunctionIdentifiers = BuildIdentifierSet(sourceView.Lines);
		_lastFunctionHoverContextName = sourceView.FunctionName;
		if (_activeProgramSearchLine > 0 && _activeProgramSearchLine < sourceView.StartLine)
		{
			_activeProgramSearchLine = sourceView.StartLine;
		}
		if (!sameRenderedCodeContext)
		{
			_lastFunctionCodeText = "";
			_lastDataCodeText = "";
		}
		_lastVisibleValuesText = "";
		_lastVisibleConditionSignature = "";
		_lastVisibleRangeSignature = "";
		_lastScopeHighlightSelectionStart = -1;
		_resetFunctionScrollOnNextRender = false;
		UpdateFunctionCodeTitle();
		_functionCodePanel.Visible = true;
		_functionCodePanel.BringToFront();
		_runtimeLocationLabel.Text = sourceView.FunctionName;
		UpdateFunctionNavButtons();
		ScheduleProgramTreeFocusRefresh(navigationVersion);
		bool restoreViewport = restoreSnapshot.HasValue;
		if (restoreViewport)
		{
			ProtectCodeViewport(260);
		}
		bool batchCodeRedraw = _functionCodeBox != null && _functionCodeBox.IsHandleCreated;
		bool oldBatchFunctionNavigationRedraw = _batchFunctionNavigationRedraw;
		if (batchCodeRedraw)
		{
			_batchFunctionNavigationRedraw = true;
			SendMessage(_functionCodeBox!.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
		}
		try
		{
			RenderFunctionSource(includeValues: false);
			if (restoreViewport)
			{
				ApplyCodeViewSnapshotViewport(restoreSnapshot!.Value, "show-function-restore");
			}
			else
			{
				SelectApproximateLineInFunction(sourceView.StartLine, scheduleViewportCorrection: false);
			}
			_codeEditor?.Update();
		}
		finally
		{
			_batchFunctionNavigationRedraw = oldBatchFunctionNavigationRedraw;
			if (batchCodeRedraw && _functionCodeBox != null && !_functionCodeBox.IsDisposed && _functionCodeBox.IsHandleCreated)
			{
				SendMessage(_functionCodeBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
				if (restoreViewport)
				{
					ApplyCodeViewSnapshotViewport(restoreSnapshot!.Value, "show-function-restore-redraw");
				}
				else
				{
					ScrollDisplayedLineNearTopForAbsoluteLine(sourceView.StartLine);
				}
				_functionCodeBox.Invalidate();
				if (!restoreViewport)
				{
					ScrollDisplayedLineNearTopOnNextUiTurn(sourceView.StartLine);
				}
			}
		}
		ScheduleDeferredFunctionNavigationWork(sourceView, navigationVersion, restoreSnapshot);
	}

	private async void ScheduleDeferredFunctionNavigationWork(FunctionSourceView sourceView, int navigationVersion, CodeViewSnapshot? restoreSnapshot = null)
	{
		try
		{
			await Task.Delay(120).ConfigureAwait(true);
			DateTime deferredWorkStartedAt = DateTime.UtcNow;
			if (IsDisposed ||
				navigationVersion != _functionNavigationVersion ||
				_currentFunctionSource == null ||
				!IsSameFunctionSource(_currentFunctionSource, sourceView))
			{
				return;
			}

			AutoWatchVariablesForFunction(sourceView);
			if (navigationVersion != _functionNavigationVersion || _currentFunctionSource == null || !IsSameFunctionSource(_currentFunctionSource, sourceView))
			{
				return;
			}

			RenderFunctionSource();
			if (restoreSnapshot.HasValue)
			{
				ApplyCodeViewSnapshotViewport(restoreSnapshot.Value, "deferred-function-restore");
			}
			RenderFunctionAnalysis(force: true);
			UpdateProgramInsightPanel(force: true);
			RefreshProgramTreeFromSnapshot();
			if (restoreSnapshot.HasValue)
			{
				ApplyCodeViewSnapshotViewport(restoreSnapshot.Value, "deferred-function-values");
			}
		}
		catch (Exception ex)
		{
			Log("刷新函数辅助信息失败：" + ex.Message);
		}
	}

	private void ScheduleProgramTreeFocusRefresh(int navigationVersion)
	{
		try
		{
			BeginInvoke((Action)(() =>
			{
				if (!IsDisposed && navigationVersion == _functionNavigationVersion)
				{
					RefreshProgramTreeFromSnapshot();
					if (!string.IsNullOrWhiteSpace(_programTreeFocusedFunction))
					{
						_flowChart?.ScrollFunctionIntoView(_programTreeFocusedFunction);
					}
				}
			}));
		}
		catch
		{
		}
	}

	private static bool IsSameFunctionSource(FunctionSourceView left, FunctionSourceView right)
	{
		return left.StartLine == right.StartLine &&
			left.FunctionName.Equals(right.FunctionName, StringComparison.OrdinalIgnoreCase) &&
			left.FilePath.Equals(right.FilePath, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsSameRenderedCodeContext(FunctionSourceView? left, FunctionSourceView? right)
	{
		if (left == null || right == null)
		{
			return false;
		}

		return left.StartLine == right.StartLine &&
			left.Lines.Count == right.Lines.Count &&
			left.FilePath.Equals(right.FilePath, StringComparison.OrdinalIgnoreCase);
	}

	private void ShowProgramGraph()
	{
		_currentFunctionSource = null;
		_codeViewSource = null;
		_programTreeFocusedFunction = "";
		_lastProgramTreeHoverKey = "";
		_currentFunctionIdentifiers.Clear();
		ClearFunctionHoverCache();
		_functionHistory.Clear();
		_functionForwardHistory.Clear();
		_lastFunctionCodeText = "";
		_lastDataCodeText = "";
		_lastFunctionAnalysisSignature = "";
		_lastVisibleValuesText = "";
		_lastVisibleConditionSignature = "";
		_lastVisibleRangeSignature = "";
		_functionCodeDirty = false;
		_dataCodeDirty = false;
		ClearPollPriority();
		if (_functionCodePanel != null)
		{
			_functionCodePanel.Visible = true;
		}
		if (_dataCodeTitle != null)
		{
			_dataCodeTitle.Text = "选择函数查看代码与实时值";
		}
		if (_dataCodeBox != null)
		{
			_dataCodeBox.Clear();
		}
		if (_visibleValuesLabel != null)
		{
			_visibleValuesLabel.Text = "可见变量：无";
		}
		if (_functionAnalysisTitle != null)
		{
			_functionAnalysisTitle.Text = _analysisInsightPanel != null && _analysisInsightPanel.Visible
				? "1 程序透视"
				: "4 函数位置";
		}
		if (_functionAnalysisSummaryBox != null)
		{
			_functionAnalysisSummaryBox.Text = "选择函数查看程序位置。";
		}
		if (_functionAnalysisChart != null)
		{
			_functionAnalysisChart.SetGraph(
				new[] { new FlowChartNode("empty", "进入函数后\n显示位置", new RectangleF(20, 20, 180, 64), 0, Kind: "business") },
				Array.Empty<FlowChartEdge>());
			_functionAnalysisChart.SetAnimationEnabled(false);
		}
		_flowChart?.SetAnimationEnabled(false);
		if (_lastTraceId == ushort.MaxValue || _lastTraceId == 0)
		{
			_runtimeLocationLabel.Text = GetProgramEntryDisplayName();
		}
		UpdateProgramInsightPanel(force: true);
	}

	private void NavigateFunctionBack()
	{
		if (_functionHistory.Count > 0)
		{
			CodeViewSnapshot previous = _functionHistory.Pop();
			CodeViewSnapshot? current = CaptureCodeViewSnapshot();
			if (current.HasValue)
			{
				_functionForwardHistory.Push(current.Value);
			}
			ShowFunctionSource(previous.Source, pushCurrent: false, clearForward: false, restoreSnapshot: previous);
			return;
		}

		ShowProgramGraph();
	}

	private void NavigateFunctionForward()
	{
		if (_functionForwardHistory.Count == 0)
		{
			return;
		}

		CodeViewSnapshot next = _functionForwardHistory.Pop();
		CodeViewSnapshot? current = CaptureCodeViewSnapshot();
		if (current.HasValue)
		{
			_functionHistory.Push(current.Value);
		}
		ShowFunctionSource(next.Source, pushCurrent: false, clearForward: false, restoreSnapshot: next);
	}

	private void UpdateFunctionNavButtons()
	{
		if (_functionBackButton != null)
		{
			_functionBackButton.Text = _functionHistory.Count > 0 ? "上级" : "返回";
		}

		if (_functionForwardButton != null)
		{
			_functionForwardButton.Enabled = _functionForwardHistory.Count > 0;
			_functionForwardButton.ForeColor = _functionForwardHistory.Count > 0 ? _ink : _muted;
		}
	}

	private void MarkFunctionCodeDirty(WatchItem? changedItem = null)
	{
		if (_currentFunctionSource == null || _functionCodePanel == null || !_functionCodePanel.Visible)
		{
			return;
		}
		if (changedItem != null &&
			!CurrentFunctionMentionsWatch(changedItem) &&
			!VisibleCodeMentionsWatch(changedItem))
		{
			return;
		}
		if (changedItem != null)
		{
			_forceDataCodeRtfRefresh = true;
		}
		if (ReferenceEquals(_functionCodeBox, _dataCodeBox))
		{
			_functionCodeDirty = true;
		}
		else
		{
			_dataCodeDirty = true;
		}
	}

	private void RestorePureCodeViewAfterMonitoringStateChanged()
	{
		if (IsDisposed)
		{
			return;
		}

		if (IsHandleCreated && InvokeRequired)
		{
			try
			{
				BeginInvoke((Action)RestorePureCodeViewAfterMonitoringStateChanged);
			}
			catch
			{
			}
			return;
		}

		_nextInlineValueFadeUtc = null;
		_lastCodeValueOverlaySignature = "";
		HideCodeValueOverlay();
		ClearScintillaValueDecorations();

		if (_currentFunctionSource == null ||
			_functionCodePanel == null ||
			!_functionCodePanel.Visible ||
			_functionCodeBox == null)
		{
			return;
		}

		_lastFunctionCodeText = "";
		_lastDataCodeText = "";
		_forceDataCodeRtfRefresh = false;
		_functionCodeDirty = false;
		_dataCodeDirty = false;
		RenderFunctionSource(includeValues: false);
	}

	private void MarkCodeValueRenderStateChanged()
	{
		_nextInlineValueFadeUtc = null;
		_lastCodeValueOverlaySignature = "";
		_lastFunctionCodeText = "";
		_lastDataCodeText = "";
		_forceDataCodeRtfRefresh = false;
		_functionCodeDirty = true;
		_dataCodeDirty = ReferenceEquals(_functionCodeBox, _dataCodeBox);
		HideCodeValueOverlay();
	}

	private void MarkUiWheelActivity()
	{
		DateTime now = DateTime.UtcNow;
		_lastFunctionWheelUtc = now;
		_lastUiWheelUtc = now;
	}

	private void SuppressCodeInteractionSideEffects(int milliseconds = 420)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(Math.Max(40, milliseconds));
		if (until > _suppressCodeInteractionSideEffectsUntilUtc)
		{
			_suppressCodeInteractionSideEffectsUntilUtc = until;
		}
		MarkUiWheelActivity();
	}

	private bool AreCodeInteractionSideEffectsSuppressed()
	{
		return DateTime.UtcNow < _suppressCodeInteractionSideEffectsUntilUtc;
	}

	private void ProtectCodeViewport(int milliseconds)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(Math.Max(80, milliseconds));
		if (until > _protectCodeViewportUntilUtc)
		{
			_protectCodeViewportUntilUtc = until;
		}
		SuppressCodeInteractionSideEffects(Math.Max(220, milliseconds));
	}

	private bool IsCodeViewportProtected()
	{
		return DateTime.UtcNow < _protectCodeViewportUntilUtc;
	}

	private CodeViewSnapshot? CaptureCodeViewSnapshot()
	{
		if (_currentFunctionSource == null)
		{
			return null;
		}

		int firstVisibleLine = 0;
		int currentPosition = 0;
		if (_codeEditor != null && !_codeEditor.IsDisposed && _codeEditor.TextLength > 0)
		{
			firstVisibleLine = _codeEditor.FirstVisibleLine;
			currentPosition = _codeEditor.CurrentPosition;
		}
		else if (_functionCodeBox != null && !_functionCodeBox.IsDisposed && _functionCodeBox.IsHandleCreated)
		{
			firstVisibleLine = GetFirstVisibleLineSafe(_functionCodeBox);
			currentPosition = _functionCodeBox.SelectionStart;
		}

		return new CodeViewSnapshot(_currentFunctionSource, firstVisibleLine, currentPosition);
	}

	private void ApplyCodeViewSnapshotViewport(CodeViewSnapshot value, string reason)
	{
		try
		{
			if (_codeEditor != null && !_codeEditor.IsDisposed && _codeEditor.Lines.Count > 0)
			{
				_codeEditor.FirstVisibleLine = Math.Clamp(value.FirstVisibleLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
				_codeEditor.XOffset = 0;
				CollapseScintillaSelection(Math.Clamp(value.CurrentPosition, 0, Math.Max(0, _codeEditor.TextLength)), reason);
				return;
			}

			if (_functionCodeBox != null && !_functionCodeBox.IsDisposed && _functionCodeBox.IsHandleCreated)
			{
				int targetLine = Math.Clamp(value.FirstVisibleLine, 0, Math.Max(0, _functionCodeBox.Lines.Length - 1));
				int currentLine = GetFirstVisibleLineSafe(_functionCodeBox);
				int delta = targetLine - currentLine;
				if (delta != 0)
				{
					SendMessage(_functionCodeBox.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(delta));
				}
				_functionCodeBox.SelectionStart = Math.Clamp(value.CurrentPosition, 0, _functionCodeBox.TextLength);
				_functionCodeBox.SelectionLength = 0;
			}
		}
		catch (Exception ex)
		{
			Log("恢复代码视口失败：" + ex.Message);
		}
	}

	private void RestoreCodeViewSnapshotLater(CodeViewSnapshot? snapshot, string reason)
	{
		if (!snapshot.HasValue)
		{
			return;
		}

		CodeViewSnapshot value = snapshot.Value;
		void Restore()
		{
			try
			{
				if (IsDisposed || !IsHandleCreated)
				{
					return;
				}

				FunctionSourceView source = value.Source;
				if (File.Exists(source.FilePath) &&
					TryLoadFunctionSourceFromFile(source.FilePath, source.FunctionName, out FunctionSourceView? refreshed) &&
					refreshed != null)
				{
					source = refreshed;
				}

				bool sameFunction = _currentFunctionSource != null &&
					_currentFunctionSource.FunctionName.Equals(source.FunctionName, StringComparison.OrdinalIgnoreCase) &&
					_currentFunctionSource.FilePath.Equals(source.FilePath, StringComparison.OrdinalIgnoreCase);
				if (!sameFunction)
				{
					ShowFunctionSource(source, pushCurrent: false, clearForward: false, restoreSnapshot: value);
					return;
				}

				ApplyCodeViewSnapshotViewport(value, reason);
			}
			catch (Exception ex)
			{
				Log("恢复代码视口失败：" + ex.Message);
			}
		}

		try
		{
			BeginInvoke((Action)Restore);
			_ = Task.Run(async () =>
			{
				foreach (int delay in new[] { 180, 460 })
				{
					await Task.Delay(delay).ConfigureAwait(false);
					if (!IsDisposed && IsHandleCreated)
					{
						BeginInvoke((Action)Restore);
					}
				}
			});
		}
		catch
		{
		}
	}

	private bool ShouldDeferFunctionCodeRefresh()
	{
		return AreCodeInteractionSideEffectsSuppressed() ||
			IsCodeViewportProtected() ||
			(DateTime.UtcNow - _lastFunctionWheelUtc).TotalMilliseconds < FunctionCodeWheelDeferMs;
	}

	private bool ShouldDeferUiValueRefresh()
	{
		return AreCodeInteractionSideEffectsSuppressed() ||
			IsCodeViewportProtected();
	}

	private bool CurrentFunctionMentionsWatch(WatchItem item)
	{
		if (_currentFunctionIdentifiers.Count == 0)
		{
			return false;
		}

		if (IdentifierSetContainsWatch(_currentFunctionIdentifiers, item.Name))
		{
			return true;
		}

		return item.IsChild && item.ParentName.Length > 0 && IdentifierSetContainsWatch(_currentFunctionIdentifiers, item.ParentName);
	}

	private bool VisibleRangeMentionsWatch(WatchItem item)
	{
		return VisibleRangeMentionsWatch(item, GetVisibleRawLines(DataMirrorPaddingLines));
	}

	private bool VisibleCodeMentionsWatch(WatchItem item)
	{
		FunctionSourceView? source = GetCodeViewSource();
		if (source == null)
		{
			return false;
		}

		return source.Lines.Any(line =>
			LineMentionsWatch(line, item.Name) ||
			(item.IsChild && item.ParentName.Length > 0 && LineMentionsWatch(line, item.ParentName)));
	}

	private bool VisibleRangeMentionsWatch(WatchItem item, IEnumerable<string> lines)
	{
		if (_currentFunctionSource == null)
		{
			return false;
		}

		foreach (string line in lines)
		{
			if (LineMentionsWatch(line, item.Name) ||
				(item.IsChild && item.ParentName.Length > 0 && LineMentionsWatch(line, item.ParentName)))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IdentifierSetContainsWatch(HashSet<string> identifiers, string watchName)
	{
		if (string.IsNullOrWhiteSpace(watchName))
		{
			return false;
		}

		return WatchIdentifierAliases(watchName).Any(identifiers.Contains);
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
		return text.Substring(0, end);
	}

	private static string GetIdentifierTail(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}

		string value = text.Trim();
		while (value.EndsWith("]", StringComparison.Ordinal))
		{
			int bracket = value.LastIndexOf('[');
			if (bracket < 0)
			{
				break;
			}
			value = value.Substring(0, bracket).TrimEnd();
		}

		for (int end = value.Length - 1; end >= 0; end--)
		{
			if (!IsIdentifierChar(value[end]))
			{
				continue;
			}

			int start = end;
			while (start > 0 && IsIdentifierChar(value[start - 1]))
			{
				start--;
			}
			string tail = value.Substring(start, end - start + 1);
			return IsIdentifierStart(tail[0]) ? tail : "";
		}

		return "";
	}

	private static IEnumerable<string> WatchIdentifierAliases(string watchName)
	{
		if (string.IsNullOrWhiteSpace(watchName))
		{
			yield break;
		}

		if (watchName.All(IsIdentifierChar))
		{
			yield return watchName;
		}

		string baseName = GetIdentifierBase(watchName);
		if (baseName.Length > 0)
		{
			yield return baseName;
		}

		string tailName = GetIdentifierTail(watchName);
		if (tailName.Length > 0 && !tailName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
		{
			yield return tailName;
		}
	}

	private static string GetWatchDisplayName(WatchItem item)
	{
		string tailName = GetIdentifierTail(item.Name);
		if (tailName.Length > 0)
		{
			return tailName;
		}

		string baseName = GetIdentifierBase(item.Name);
		return baseName.Length > 0 ? baseName : item.Name;
	}

	private static HashSet<string> BuildIdentifierSet(IEnumerable<string> lines)
	{
		return BuildIdentifierList(lines).ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private static List<string> BuildIdentifierList(IEnumerable<string> lines)
	{
		var identifiers = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in lines)
		{
			for (int i = 0; i < line.Length; i++)
			{
				if (!IsIdentifierStart(line[i]))
				{
					continue;
				}
				int start = i;
				i++;
				while (i < line.Length && IsIdentifierChar(line[i]))
				{
					i++;
				}
				string identifier = line.Substring(start, i - start);
				if (seen.Add(identifier))
				{
					identifiers.Add(identifier);
				}
				i--;
			}
		}
		return identifiers;
	}

	private static List<string> BuildFunctionAutoWatchIdentifiers(IReadOnlyList<string> lines)
	{
		string text = string.Join(Environment.NewLine, lines);
		var identifiers = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Add(string identifier)
		{
			if (string.IsNullOrWhiteSpace(identifier) ||
				IsCKeywordToken(identifier) ||
				!seen.Add(identifier))
			{
				return;
			}
			identifiers.Add(identifier);
		}

		foreach (string signal in EmbeddedCodeKnowledge.ExtractBusinessSignals(text))
		{
			Add(signal);
		}
		foreach (string identifier in BuildIdentifierList(lines))
		{
			Add(identifier);
		}
		return identifiers;
	}

	private void CodeBoxMouseDoubleClick(object? sender, MouseEventArgs e)
	{
		SuppressCodeInteractionSideEffects();
		if (sender is not RichTextBox codeBox || codeBox.TextLength == 0)
		{
			return;
		}
		if (!TryGetIdentifierAtCodePoint(codeBox, e.Location, out int start, out int length, out string identifier))
		{
			return;
		}
		codeBox.SelectionStart = start;
		codeBox.SelectionLength = length;

		if ((ModifierKeys & Keys.Control) != Keys.Control)
		{
			FillProgramSearchFromCode(identifier);
		}
	}

	private void CodeEditorMouseDoubleClick(object? sender, MouseEventArgs e)
	{
		SuppressCodeInteractionSideEffects();
		if (_codeEditor == null || _codeEditor.TextLength == 0)
		{
			return;
		}
		if (!TryGetIdentifierAtCodePoint(_codeEditor, e.Location, out int start, out int length, out string identifier))
		{
			return;
		}
		SelectScintillaIdentifierRange(start, length);
		FillProgramSearchFromCode(identifier);
		_codeEditor.Focus();
		SelectScintillaIdentifierRange(start, length);
	}

	private void CodeBoxMouseDown(object? sender, MouseEventArgs e)
	{
		if (sender is not RichTextBox codeBox)
		{
			return;
		}

		if (e.Button == MouseButtons.Left)
		{
			if (e.Clicks > 1 || AreCodeInteractionSideEffectsSuppressed())
			{
				SuppressCodeInteractionSideEffects();
				return;
			}
			UpdateProgramTreeFocusFromCodePoint(codeBox, e.Location);
			if (!TryGetIdentifierAtCodePoint(codeBox, e.Location, out _, out _, out _))
			{
				ClearFocusedVariableHighlight();
				BeginInvoke((Action)(() =>
				{
					if (!codeBox.IsDisposed)
					{
						codeBox.SelectionLength = 0;
					}
				}));
			}
			return;
		}

		if (e.Button == MouseButtons.Right)
		{
			ShowCodeWatchContextMenu(codeBox, e.Location);
		}
	}

	private void CodeEditorMouseDown(object? sender, MouseEventArgs e)
	{
		if (_codeEditor == null)
		{
			return;
		}

		if (e.Button == MouseButtons.Left)
		{
			if (e.Clicks > 1 || AreCodeInteractionSideEffectsSuppressed())
			{
				SuppressCodeInteractionSideEffects();
				return;
			}
			UpdateProgramTreeFocusFromCodePoint(_codeEditor, e.Location);
			if (!TryGetIdentifierAtCodePoint(_codeEditor, e.Location, out _, out _, out _))
			{
				ClearFocusedVariableStateOnly();
			}
			return;
		}

		if (e.Button == MouseButtons.Right)
		{
			ShowCodeWatchContextMenu(_codeEditor, e.Location);
		}
	}

	private void UpdateProgramTreeFocusFromCodePoint(RichTextBox codeBox, Point location)
	{
		if (_currentFunctionSource == null || codeBox.TextLength == 0)
		{
			return;
		}

		string focusFunction = ResolveProgramTreeFocusAtCodePoint(codeBox, location);
		SetProgramTreeFocusedFunction(focusFunction, allowStructureRefresh: true, scrollIntoView: true);
	}

	private void UpdateProgramTreeFocusFromCodePoint(Scintilla editor, Point location)
	{
		if (_currentFunctionSource == null || editor.TextLength == 0)
		{
			return;
		}

		CodeFunctionFocus focus = ResolveProgramTreeFocusContextAtCodePoint(editor, location);
		SetProgramTreeFocusedFunction(focus.ContainingFunction, allowStructureRefresh: true, scrollIntoView: true);
	}

	private void UpdateProgramTreeFocusFromCodePointThrottled(Scintilla editor, Point location)
	{
		if (_currentFunctionSource == null || editor.TextLength == 0)
		{
			return;
		}

		CodeFunctionFocus focus = ResolveProgramTreeFocusContextAtCodePoint(editor, location);
		string key = focus.ContainingFunction;
		if (key.Equals(_lastProgramTreeHoverKey, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		_lastProgramTreeHoverKey = key;
		SetProgramTreeFocusedFunction(focus.ContainingFunction, allowStructureRefresh: true, scrollIntoView: true);
	}

	private void UpdateProgramTreeFocusFromScintillaCaretThrottled()
	{
		if (_codeEditor == null || _currentFunctionSource == null || _codeEditor.TextLength == 0)
		{
			return;
		}
		if (AreCodeInteractionSideEffectsSuppressed() ||
			(DateTime.UtcNow - _lastUiWheelUtc).TotalMilliseconds <= 120)
		{
			return;
		}

		FunctionSourceView? codeView = GetCodeViewSource();
		if (codeView == null)
		{
			return;
		}

		int lineIndex = _codeEditor.LineFromPosition(Math.Clamp(_codeEditor.CurrentPosition, 0, Math.Max(0, _codeEditor.TextLength)));
		if (!TryGetFunctionNameAtCodeViewLine(codeView, lineIndex, out string containingFunction) ||
			string.IsNullOrWhiteSpace(containingFunction))
		{
			return;
		}

		string key = containingFunction.Trim();
		if (key.Equals(_lastProgramTreeHoverKey, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		_lastProgramTreeHoverKey = key;
		SetProgramTreeFocusedFunction(containingFunction, allowStructureRefresh: true, scrollIntoView: true);
	}

	private void SetProgramTreeFocusedFunction(string focusFunction, bool allowStructureRefresh, bool scrollIntoView = false)
	{
		SetProgramTreeFocusedFunctions(focusFunction, "", allowStructureRefresh, scrollIntoView);
	}

	private void SetProgramTreeFocusedFunctions(string focusFunction, string locateTargetFunction, bool allowStructureRefresh, bool scrollIntoView = false)
	{
		if (string.IsNullOrWhiteSpace(focusFunction) ||
			(focusFunction.Equals(_programTreeFocusedFunction, StringComparison.OrdinalIgnoreCase) &&
			 locateTargetFunction.Equals(_programTreeLocateTargetFunction, StringComparison.OrdinalIgnoreCase)))
		{
			if (scrollIntoView && allowStructureRefresh)
			{
				string scrollTarget = focusFunction;
				bool expandedPath = ExpandProgramTreePathToFunction(scrollTarget);
				if (expandedPath)
				{
					RefreshProgramTreeFromSnapshot();
					_flowChart?.SetFocusedFunction(focusFunction);
					_flowChart?.CenterFunctionInView(scrollTarget);
				}
			}
			return;
		}

		_programTreeFocusedFunction = focusFunction;
		_programTreeLocateTargetFunction = locateTargetFunction.Trim();
		bool didExpandPath = allowStructureRefresh && ExpandProgramTreePathToFunction(focusFunction);
		if (_flowChart == null)
		{
			return;
		}

		_flowChart.SetFocusedFunction(focusFunction);
		bool focusMissing = !_flowChart.ContainsFunction(focusFunction);
		bool locateMissing = !string.IsNullOrWhiteSpace(_programTreeLocateTargetFunction) &&
			!_flowChart.ContainsFunction(_programTreeLocateTargetFunction);
		if (allowStructureRefresh && (didExpandPath || focusMissing || locateMissing))
		{
			RefreshProgramTreeFromSnapshot();
			_flowChart.SetFocusedFunction(focusFunction);
		}
		if (scrollIntoView)
		{
			string scrollTarget = focusFunction;
			if (!_flowChart.CenterFunctionInView(scrollTarget) && allowStructureRefresh)
			{
				RefreshProgramTreeFromSnapshot();
				_flowChart.SetFocusedFunction(focusFunction);
				_flowChart.CenterFunctionInView(scrollTarget);
			}
		}
	}

	private bool ExpandProgramTreePathToFunction(string functionName)
	{
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null || snapshot.CallGraphNodes.Count == 0 || snapshot.CallGraphEdges.Count == 0 || string.IsNullOrWhiteSpace(functionName))
		{
			return false;
		}

		Dictionary<string, ProgramCallGraphNode> nodeById = snapshot.CallGraphNodes
			.GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
		List<string> targetIds = snapshot.CallGraphNodes
			.Where(node => node.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase))
			.Select(node => node.Id)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (targetIds.Count == 0)
		{
			return false;
		}

		Dictionary<string, List<string>> parentsByChild = snapshot.CallGraphEdges
			.Where(edge => nodeById.ContainsKey(edge.FromId) && nodeById.ContainsKey(edge.ToId))
			.GroupBy(edge => edge.ToId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.Select(edge => edge.FromId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
				StringComparer.OrdinalIgnoreCase);

		var ancestorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var stack = new Stack<string>(targetIds);
		while (stack.Count > 0)
		{
			string childId = stack.Pop();
			if (!visited.Add(childId) || !parentsByChild.TryGetValue(childId, out List<string>? parents))
			{
				continue;
			}

			foreach (string parentId in parents)
			{
				if (!nodeById.TryGetValue(parentId, out ProgramCallGraphNode? parent))
				{
					continue;
				}

				if (!parent.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase))
				{
					ancestorNames.Add(parent.Name.Trim());
				}
				stack.Push(parentId);
			}
		}

		bool changed = false;
		foreach (string ancestorName in ancestorNames)
		{
			if (_collapsedProgramTreeNodes.Remove(ancestorName))
			{
				changed = true;
			}
			if (_expandedProgramTreeNodes.Add(ancestorName))
			{
				changed = true;
			}
		}
		return changed;
	}

	private string ResolveProgramTreeFocusAtCodePoint(RichTextBox codeBox, Point location)
	{
		if (TryGetIdentifierAtCodePoint(codeBox, location, out _, out _, out string identifier) &&
			!identifier.Equals(_currentFunctionSource?.FunctionName ?? "", StringComparison.OrdinalIgnoreCase) &&
			IsKnownProjectFunction(identifier))
		{
			return identifier;
		}

		int index = codeBox.GetCharIndexFromPosition(location);
		int lineIndex = Math.Clamp(codeBox.GetLineFromCharIndex(Math.Clamp(index, 0, codeBox.TextLength)), 0, Math.Max(0, codeBox.Lines.Length - 1));

		FunctionSourceView? codeView = GetCodeViewSource();
		if (codeView != null &&
			TryGetFunctionNameAtCodeViewLine(codeView, lineIndex, out string containingFunction) &&
			!string.IsNullOrWhiteSpace(containingFunction))
		{
			return containingFunction;
		}

		return _currentFunctionSource?.FunctionName ?? "";
	}

	private string ResolveProgramTreeFocusAtCodePoint(Scintilla editor, Point location)
	{
		CodeFunctionFocus focus = ResolveProgramTreeFocusContextAtCodePoint(editor, location);
		return !string.IsNullOrWhiteSpace(focus.CalledFunction) ? focus.CalledFunction : focus.ContainingFunction;
	}

	private CodeFunctionFocus ResolveProgramTreeFocusContextAtCodePoint(Scintilla editor, Point location)
	{
		int lineIndex = GetScintillaLineFromPoint(editor, location);
		FunctionSourceView? codeView = GetCodeViewSource();
		string containingFunction = _currentFunctionSource?.FunctionName ?? "";
		if (codeView != null &&
			TryGetFunctionNameAtCodeViewLine(codeView, lineIndex, out string lineFunction) &&
			!string.IsNullOrWhiteSpace(lineFunction))
		{
			containingFunction = lineFunction;
		}

		string calledFunction = "";
		string lineText = codeView != null && lineIndex >= 0 && lineIndex < codeView.Lines.Count
			? codeView.Lines[lineIndex]
			: "";
		if (TryGetIdentifierAtCodePoint(editor, location, out int start, out int length, out string identifier) &&
			!identifier.Equals(containingFunction, StringComparison.OrdinalIgnoreCase) &&
			IsKnownProjectFunction(identifier) &&
			IsFunctionCallIdentifierAtPosition(editor.Text, start, length))
		{
			calledFunction = identifier;
		}
		else
		{
			calledFunction = FindFirstKnownFunctionCallInLine(lineText, containingFunction);
		}

		return new CodeFunctionFocus(containingFunction, calledFunction);
	}

	private bool IsKnownProjectFunction(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || IsCKeyword(name) || IsMonitorInternalFunctionName(name))
		{
			return false;
		}
		return TryLoadFunctionSource(_workDirectory, name, out FunctionSourceView? source) && source != null;
	}

	private void CodeBoxMouseUp(object? sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Right || sender is not RichTextBox codeBox)
		{
			return;
		}

		ShowCodeWatchContextMenu(codeBox, e.Location);
	}

	private void CodeEditorMouseUp(object? sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Right || _codeEditor == null)
		{
			return;
		}

		ShowCodeWatchContextMenu(_codeEditor, e.Location);
	}

	private ContextMenuStrip CreateCodeWatchContextMenu(Control codeControl)
	{
		ContextMenuStrip menu = new ContextMenuStrip();
		menu.Opening += delegate(object? sender, CancelEventArgs e)
		{
			e.Cancel = true;
			if (ShouldSuppressAutomaticWatchContextMenu(codeControl))
			{
				return;
			}
			ShowCodeWatchContextMenu(codeControl, codeControl.PointToClient(Cursor.Position));
		};
		return menu;
	}

	private void FunctionCodeBoxMouseClick(object? sender, MouseEventArgs e)
	{
		if (AreCodeInteractionSideEffectsSuppressed() ||
			e.Button != MouseButtons.Left ||
			(ModifierKeys & Keys.Control) != Keys.Control)
		{
			return;
		}
		TryEnterFunctionAtPoint(e.Location, selectIdentifier: true);
	}

	private void CodeEditorMouseClick(object? sender, MouseEventArgs e)
	{
		if (AreCodeInteractionSideEffectsSuppressed() ||
			e.Button != MouseButtons.Left ||
			(ModifierKeys & Keys.Control) != Keys.Control ||
			_codeEditor == null)
		{
			return;
		}
		TryEnterFunctionAtScintillaPoint(_codeEditor, e.Location, selectIdentifier: true);
	}

	private bool TryGetIdentifierAtCodePoint(RichTextBox codeBox, Point location, out int start, out int length, out string identifier)
	{
		start = 0;
		length = 0;
		identifier = "";
		if (codeBox.TextLength == 0)
		{
			return false;
		}

		int index = codeBox.GetCharIndexFromPosition(location);
		int line = codeBox.GetLineFromCharIndex(index);
		int lineStart = codeBox.GetFirstCharIndexFromLine(line);
		if (lineStart < 0)
		{
			return false;
		}

		int nextLineStart = line + 1 < codeBox.Lines.Length ? codeBox.GetFirstCharIndexFromLine(line + 1) : codeBox.TextLength;
		int lineEnd = Math.Clamp(nextLineStart, lineStart, codeBox.TextLength);
		while (lineEnd > lineStart && (codeBox.Text[lineEnd - 1] == '\r' || codeBox.Text[lineEnd - 1] == '\n'))
		{
			lineEnd--;
		}
		if (lineEnd <= lineStart)
		{
			return false;
		}

		Point endPoint = codeBox.GetPositionFromCharIndex(Math.Max(lineStart, lineEnd - 1));
		int charWidth = Math.Max(Ui(6), TextRenderer.MeasureText("W", codeBox.Font).Width);
		if (location.X > endPoint.X + charWidth * 2)
		{
			return false;
		}

		return TryGetIdentifierAt(codeBox.Text, index, out start, out length, out identifier);
	}

	private bool TryGetIdentifierAtCodePoint(Scintilla editor, Point location, out int start, out int length, out string identifier)
	{
		start = 0;
		length = 0;
		identifier = "";
		if (editor.TextLength == 0)
		{
			return false;
		}

		int index = GetScintillaPositionFromPoint(editor, location);
		if (index < 0 || index >= editor.TextLength)
		{
			return false;
		}

		int lineIndex = Math.Clamp(editor.LineFromPosition(index), 0, Math.Max(0, editor.Lines.Count - 1));
		ScintillaNET.Line line = editor.Lines[lineIndex];
		string lineText = line.Text;
		int visibleEndInLine = lineText.Length;
		while (visibleEndInLine > 0 && (lineText[visibleEndInLine - 1] == '\r' || lineText[visibleEndInLine - 1] == '\n'))
		{
			visibleEndInLine--;
		}
		int endPosition = Math.Clamp(line.Position + Math.Max(0, visibleEndInLine), 0, editor.TextLength);
		int endX = editor.PointXFromPosition(endPosition);
		if (location.X > endX + Ui(18))
		{
			return false;
		}

		return TryGetIdentifierAt(editor.Text, index, out start, out length, out identifier);
	}

	private static int GetScintillaPositionFromPoint(Scintilla editor, Point location)
	{
		int position = editor.CharPositionFromPointClose(location.X, location.Y);
		if (position >= 0)
		{
			return position;
		}

		position = editor.CharPositionFromPoint(location.X, location.Y);
		return position >= 0 ? position : Math.Clamp(editor.CurrentPosition, 0, Math.Max(0, editor.TextLength - 1));
	}

	private static int GetScintillaLineFromPoint(Scintilla editor, Point location)
	{
		int position = GetScintillaPositionFromPoint(editor, location);
		if (position < 0)
		{
			return 0;
		}
		return Math.Clamp(editor.LineFromPosition(position), 0, Math.Max(0, editor.Lines.Count - 1));
	}

	private void ShowCodeWatchContextMenu(Control owner, Point location)
	{
		if (owner is Scintilla editor)
		{
			ShowCodeWatchContextMenu(editor, location);
			return;
		}
		if (owner is RichTextBox codeBox)
		{
			ShowCodeWatchContextMenu(codeBox, location);
		}
	}

	private void ShowCodeWatchContextMenu(RichTextBox codeBox, Point location)
	{
		int index = codeBox.GetCharIndexFromPosition(location);
		if (!TryGetIdentifierAt(codeBox.Text, index, out int start, out int length, out string identifier) &&
			!TryGetNearestWatchIdentifier(codeBox.Text, index, out start, out length, out identifier))
		{
			ShowWatchContextMenu(null, "", codeBox, location);
			return;
		}

		codeBox.SelectionStart = start;
		codeBox.SelectionLength = length;
		FocusVariableAcrossPanels(identifier, updateSearchBox: true);
		TryResolveWatchContextTarget(identifier, null, addIfMissing: false, out WatchItem? item, out _);
		ShowWatchContextMenu(item, identifier, codeBox, location);
	}

	private void ShowCodeWatchContextMenu(Scintilla editor, Point location)
	{
		int index = GetScintillaPositionFromPoint(editor, location);
		if (!TryGetIdentifierAt(editor.Text, index, out int start, out int length, out string identifier) &&
			!TryGetNearestWatchIdentifier(editor.Text, index, out start, out length, out identifier))
		{
			ShowWatchContextMenu(null, "", editor, location);
			return;
		}

		CollapseScintillaSelection(start);
		FocusVariableAcrossPanels(identifier, updateSearchBox: true);
		TryResolveWatchContextTarget(identifier, null, addIfMissing: false, out WatchItem? item, out _);
		ShowWatchContextMenu(item, identifier, editor, location);
	}

	private bool TryGetNearestWatchIdentifier(string text, int index, out int start, out int length, out string identifier)
	{
		start = 0;
		length = 0;
		identifier = "";
		if (text.Length == 0)
		{
			return false;
		}

		int safeIndex = Math.Clamp(index, 0, text.Length - 1);
		int lineStart = text.LastIndexOf('\n', safeIndex);
		lineStart = lineStart < 0 ? 0 : lineStart + 1;
		int lineEnd = text.IndexOf('\n', safeIndex);
		lineEnd = lineEnd < 0 ? text.Length : lineEnd;
		if (lineEnd <= lineStart)
		{
			return false;
		}

		foreach (int candidateIndex in BuildNearbyIdentifierProbeOrder(safeIndex, lineStart, lineEnd))
		{
			if (!TryGetIdentifierAt(text, candidateIndex, out int candidateStart, out int candidateLength, out string candidate))
			{
				continue;
			}

			if (IsCKeyword(candidate))
			{
				continue;
			}

			if (FindWatchItemByIdentifier(candidate) != null || TryFindSymbolForSourceIdentifier(candidate, out _))
			{
				start = candidateStart;
				length = candidateLength;
				identifier = candidate;
				return true;
			}
		}

		return false;
	}

	private static IEnumerable<int> BuildNearbyIdentifierProbeOrder(int index, int lineStart, int lineEnd)
	{
		HashSet<int> yielded = new HashSet<int>();
		int left = Math.Clamp(index, lineStart, Math.Max(lineStart, lineEnd - 1));
		int right = left + 1;
		for (int step = 0; step < 96 && (left >= lineStart || right < lineEnd); step++)
		{
			if (left >= lineStart && yielded.Add(left))
			{
				yield return left;
				left--;
			}
			if (right < lineEnd && yielded.Add(right))
			{
				yield return right;
				right++;
			}
		}
	}

	private void ShowWatchContextMenu(WatchItem? item, string identifier, Control owner, Point location)
	{
		if (!TryBeginWatchContextMenu(owner))
		{
			return;
		}

		string displayName = item != null ? GetWatchDisplayName(item) : NormalizeFocusedVariableName(identifier);
		if (string.IsNullOrWhiteSpace(displayName))
		{
			displayName = string.IsNullOrWhiteSpace(identifier) ? "未选中变量" : identifier;
		}

		bool canWatch = !string.IsNullOrWhiteSpace(identifier) &&
			(item != null || TryResolveWatchContextTarget(identifier, null, addIfMissing: false, out _, out _));
		bool codeOwner = owner is Scintilla || owner is RichTextBox;
		bool debugMode = IsDebugContextActive();
		ContextMenuStrip menu = new ContextMenuStrip
		{
			BackColor = _surface,
			ForeColor = _ink,
			ShowImageMargin = false
		};
		menu.Closed += delegate
		{
			try
			{
				if (!IsDisposed && IsHandleCreated)
				{
					BeginInvoke((Action)(() => menu.Dispose()));
				}
				else
				{
					menu.Dispose();
				}
			}
			catch (InvalidOperationException)
			{
				menu.Dispose();
			}
		};

		ToolStripMenuItem titleItem = new ToolStripMenuItem((codeOwner ? "代码：" : "变量：") + displayName)
		{
			Enabled = false
		};
		menu.Items.Add(titleItem);
		menu.Items.Add(new ToolStripSeparator());

		ToolStripMenuItem copySelection = new ToolStripMenuItem("复制");
		copySelection.Enabled = !string.IsNullOrEmpty(GetSelectedCodeText(owner));
		copySelection.Click += delegate
		{
			string selected = GetSelectedCodeText(owner);
			if (!string.IsNullOrEmpty(selected))
			{
				Clipboard.SetText(selected);
			}
		};
		menu.Items.Add(copySelection);

		ToolStripMenuItem copyName = new ToolStripMenuItem("复制名称");
		copyName.Enabled = !string.IsNullOrWhiteSpace(identifier) || item != null;
		copyName.Click += delegate
		{
			if (!string.IsNullOrWhiteSpace(displayName) && !displayName.Equals("未选中变量", StringComparison.Ordinal))
			{
				Clipboard.SetText(displayName);
			}
		};
		menu.Items.Add(copyName);

		ToolStripMenuItem gotoDefinition = new ToolStripMenuItem("转到定义");
		gotoDefinition.Enabled = !string.IsNullOrWhiteSpace(identifier);
		gotoDefinition.Click += delegate
		{
			if (!TryGotoDefinitionFromIdentifier(identifier))
			{
				FillProgramSearchFromCode(identifier);
				RunProgramSearch();
			}
		};
		menu.Items.Add(gotoDefinition);

		ToolStripMenuItem addWatch = new ToolStripMenuItem("加入监控");
		addWatch.Enabled = !string.IsNullOrWhiteSpace(identifier);
		addWatch.Click += delegate
		{
			if (TryResolveWatchContextTarget(identifier, item, addIfMissing: true, out WatchItem? target, out string error) && target != null)
			{
				MarkFunctionCodeDirty(target);
			}
			else
			{
				Log("加入监控失败：" + error);
			}
		};
		menu.Items.Add(addWatch);

		if (debugMode)
		{
			menu.Items.Add(new ToolStripSeparator());
			ToolStripMenuItem writeOnce = new ToolStripMenuItem("写入一次...");
			writeOnce.Enabled = canWatch;
			writeOnce.Click += async delegate
			{
				if (TryResolveWatchContextTarget(identifier, item, addIfMissing: true, out WatchItem? target, out string error) && target != null)
				{
					await PromptAndWriteWatchValueAsync(target, force: false).ConfigureAwait(true);
				}
				else
				{
					Log("写入失败：" + error);
				}
			};
			menu.Items.Add(writeOnce);

			ToolStripMenuItem holdValue = new ToolStripMenuItem("保持为...");
			holdValue.Enabled = canWatch;
			holdValue.Click += async delegate
			{
				if (TryResolveWatchContextTarget(identifier, item, addIfMissing: true, out WatchItem? target, out string error) && target != null)
				{
					await PromptAndWriteWatchValueAsync(target, force: true).ConfigureAwait(true);
				}
				else
				{
					Log("保持失败：" + error);
				}
			};
			menu.Items.Add(holdValue);

			ToolStripMenuItem releaseValue = new ToolStripMenuItem("释放保持");
			releaseValue.Enabled = canWatch;
			releaseValue.Click += async delegate
			{
				if (TryResolveWatchContextTarget(identifier, item, addIfMissing: true, out WatchItem? target, out string error) && target != null)
				{
					await ReleaseWatchForceAsync(target).ConfigureAwait(true);
				}
				else
				{
					Log("释放失败：" + error);
				}
			};
			menu.Items.Add(releaseValue);
		}

		if (!canWatch)
		{
			ToolStripMenuItem unavailable = new ToolStripMenuItem("该变量不可监控")
			{
				Enabled = false
			};
			menu.Items.Add(unavailable);
		}

		menu.Show(owner, location);
	}

	private bool IsDebugContextActive()
	{
		return _running || _offlineSimulation || _adapter != null;
	}

	private static string GetSelectedCodeText(Control owner)
	{
		return owner switch
		{
			Scintilla editor => editor.SelectedText ?? "",
			RichTextBox box => box.SelectedText ?? "",
			TextBox box => box.SelectedText ?? "",
			_ => ""
		};
	}

	private bool TryGotoDefinitionFromIdentifier(string identifier)
	{
		string name = NormalizeFocusedVariableName(identifier);
		if (name.Length == 0 || string.IsNullOrWhiteSpace(_workDirectory) || !Directory.Exists(_workDirectory))
		{
			return false;
		}

		if (IsKnownProjectFunction(name) &&
			TryLoadFunctionSource(_workDirectory, name, out FunctionSourceView? functionSource) &&
			functionSource != null)
		{
			ShowFunctionSource(functionSource, pushCurrent: _currentFunctionSource != null, clearForward: true);
			SelectApproximateLineInFunction(functionSource.StartLine);
			return true;
		}

		if (TryFindIdentifierDefinitionInSource(name, out ProgramSearchResult result))
		{
			NavigateToProgramSearchResult(result, name, pushCurrent: _currentFunctionSource != null);
			return true;
		}

		return false;
	}

	private bool TryFindIdentifierDefinitionInSource(string identifier, out ProgramSearchResult result)
	{
		result = default!;
		if (identifier.Length == 0 || string.IsNullOrWhiteSpace(_workDirectory) || !Directory.Exists(_workDirectory))
		{
			return false;
		}

		string pattern = @"\b(?:(?:extern|static|volatile|const|unsigned|signed)\s+)*(?:[A-Za-z_][A-Za-z0-9_]*\s+)+[*\s]*" +
			Regex.Escape(identifier) +
			@"\b(?:\s*(?:=|;|,|\[))";
		foreach (string file in EnumerateSourceFilesForOpen(_workDirectory))
		{
			string text;
			try
			{
				text = ReadSourceText(file);
			}
			catch
			{
				continue;
			}

			string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string line = StripLineComment(lines[i]);
				if (line.IndexOf(identifier, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				if (!Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
				{
					continue;
				}

				string display = $"{GetRelativePathSafe(_workDirectory, file)}:{i + 1}    {lines[i].Trim()}";
				result = new ProgramSearchResult(display, file, i + 1, FindFunctionNameAtLine(text, i + 1));
				return true;
			}
		}

		return false;
	}

	private void NavigateToProgramSearchResult(ProgramSearchResult result, string keyword, bool pushCurrent)
	{
		_activeProgramSearchKeyword = keyword;
		_activeProgramSearchLine = result.LineNumber;
		if (!string.IsNullOrWhiteSpace(result.FunctionName) &&
			TryLoadFunctionSourceFromFile(result.FilePath, result.FunctionName, out FunctionSourceView? functionSource) &&
			functionSource != null)
		{
			ShowFunctionSource(functionSource, pushCurrent: pushCurrent, clearForward: pushCurrent);
			SelectApproximateLineInFunction(result.LineNumber);
			return;
		}
		if (TryLoadNearestFunctionSource(result.FilePath, result.LineNumber, out FunctionSourceView? nearest) && nearest != null)
		{
			ShowFunctionSource(nearest, pushCurrent: pushCurrent, clearForward: pushCurrent);
			SelectApproximateLineInFunction(result.LineNumber);
			return;
		}
		if (TryLoadSourceSnippet(result.FilePath, result.LineNumber, out FunctionSourceView? snippet) && snippet != null)
		{
			ShowFunctionSource(snippet, pushCurrent: pushCurrent, clearForward: pushCurrent);
			SelectApproximateLineInFunction(result.LineNumber);
		}
	}

	private bool TryBeginWatchContextMenu(Control owner)
	{
		DateTime now = DateTime.UtcNow;
		if (ReferenceEquals(_lastWatchContextMenuOwner, owner) &&
			(now - _lastWatchContextMenuShownUtc).TotalMilliseconds < 800)
		{
			return false;
		}

		_lastWatchContextMenuOwner = owner;
		_lastWatchContextMenuShownUtc = now;
		return true;
	}

	private bool ShouldSuppressAutomaticWatchContextMenu(Control owner)
	{
		return ReferenceEquals(_lastWatchContextMenuOwner, owner) &&
			(DateTime.UtcNow - _lastWatchContextMenuShownUtc).TotalMilliseconds < 800;
	}

	private bool TryResolveWatchContextTarget(string identifier, WatchItem? knownItem, bool addIfMissing, out WatchItem? item, out string error)
	{
		item = null;
		error = "";
		string name = NormalizeFocusedVariableName(identifier);
		if (knownItem != null)
		{
			if (!addIfMissing || _watchItems.Contains(knownItem))
			{
				item = knownItem;
				return true;
			}

			name = knownItem.Name;
		}

		if (name.Length == 0)
		{
			error = "没有选中变量";
			return false;
		}

		WatchItem? existing = FindWatchItemByIdentifier(name);
		if (existing != null)
		{
			item = existing;
			return true;
		}

		if (!TryFindSymbolForSourceIdentifier(name, out MapSymbol symbol))
		{
			error = "map 中没有该变量";
			return false;
		}

		if (!KeilMapParser.TryResolve(symbol.Name, _symbolLookup, out WatchItem resolved, out error))
		{
			return false;
		}

		if (!addIfMissing)
		{
			item = resolved;
			return true;
		}

		if (!EnsureWatchCapacityForCurrentContext(out int removed))
		{
			error = $"已达到 {MaxWatchItems} 个变量上限";
			return false;
		}

		resolved.AutoVisible = false;
		_watchItems.Add(resolved);
		UpdateCycleEstimate();
		SaveDefaultProfileQuietly();
		MarkFunctionCodeDirty(resolved);
		FocusVariableAcrossPanels(GetWatchDisplayName(resolved), updateSearchBox: true);
		Log("已加入监控：" + resolved.Name);
		item = resolved;
		return true;
	}

	private bool TryEnterFunctionAtPoint(Point location, bool selectIdentifier)
	{
		if (_functionCodeBox == null || _functionCodeBox.TextLength == 0)
		{
			return false;
		}
		int index = _functionCodeBox.GetCharIndexFromPosition(location);
		if (!TryGetIdentifierAt(_functionCodeBox.Text, index, out int start, out int length, out string identifier))
		{
			return false;
		}
		if (selectIdentifier)
		{
			_functionCodeBox.SelectionStart = start;
			_functionCodeBox.SelectionLength = length;
		}
		return TryEnterFunctionIdentifier(identifier);
	}

	private bool TryEnterFunctionAtScintillaPoint(Scintilla editor, Point location, bool selectIdentifier)
	{
		if (editor == null || editor.TextLength == 0)
		{
			return false;
		}
		if (!TryGetIdentifierAtCodePoint(editor, location, out int start, out int length, out string identifier))
		{
			return false;
		}
		if (selectIdentifier)
		{
			CollapseScintillaSelection(start);
		}
		return TryEnterFunctionIdentifier(identifier);
	}

	private bool TryEnterFunctionIdentifier(string identifier)
	{
		if (_currentFunctionSource == null ||
			identifier.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrWhiteSpace(_workDirectory) ||
			!Directory.Exists(_workDirectory) ||
			!TryLoadFunctionSource(_workDirectory, identifier, out FunctionSourceView? nestedSource) ||
			nestedSource == null)
		{
			return false;
		}

		ShowFunctionSource(nestedSource, pushCurrent: true, clearForward: true);
		Log($"已进入函数：{identifier}  {nestedSource.FilePath}:{nestedSource.StartLine}");
		return true;
	}

	private void FillProgramSearchFromCode(string identifier)
	{
		if (_programSearchBox == null || string.IsNullOrWhiteSpace(identifier))
		{
			return;
		}
		_programSearchBox.Text = identifier;
		_programSearchBox.SelectionStart = 0;
		_programSearchBox.SelectionLength = identifier.Length;
		FocusVariableAcrossPanels(identifier, updateSearchBox: false);
	}

	private void ClearFocusedVariable()
	{
		if (string.IsNullOrWhiteSpace(_focusedVariableName) &&
			string.IsNullOrWhiteSpace(_activeProgramSearchKeyword))
		{
			return;
		}

		_focusedVariableName = "";
		_activeProgramSearchKeyword = "";
		_activeProgramSearchLine = 0;
		_lastFunctionCodeText = "";
		_lastDataCodeText = "";
		if (_currentFunctionSource != null && _functionCodePanel != null && _functionCodePanel.Visible)
		{
			if (_codeEditor != null && !_codeEditor.IsDisposed)
			{
				ApplyProgramSearchHighlight();
			}
			else
			{
				RenderFunctionSource();
			}
		}
		else
		{
			ApplyProgramSearchHighlight();
		}
		UpdateProgramInsightPanel(force: true);
	}

	private void FocusVariableAcrossPanels(string identifier, bool updateSearchBox)
	{
		string variableName = NormalizeFocusedVariableName(identifier);
		if (variableName.Length == 0)
		{
			return;
		}

		bool changed = !_focusedVariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase);
		_focusedVariableName = variableName;
		_activeProgramSearchKeyword = variableName;
		_activeProgramSearchLine = 0;
		if (updateSearchBox && _programSearchBox != null)
		{
			_programSearchBox.Text = variableName;
			_programSearchBox.SelectionStart = 0;
			_programSearchBox.SelectionLength = variableName.Length;
		}
		if (_currentFunctionSource != null && _functionCodePanel != null && _functionCodePanel.Visible)
		{
			if (_codeEditor != null && !_codeEditor.IsDisposed)
			{
				ApplyProgramSearchHighlight();
			}
			else
			{
				_lastFunctionCodeText = "";
				_lastDataCodeText = "";
				RenderFunctionSource();
			}
		}
		else
		{
			ApplyProgramSearchHighlight();
		}
		UpdateProgramInsightPanel(changed);
	}

	private static string NormalizeFocusedVariableName(string identifier)
	{
		string value = identifier.Trim();
		if (value.Length == 0)
		{
			return "";
		}

		Match match = Regex.Match(value, @"[A-Za-z_][A-Za-z0-9_]*");
		return match.Success ? match.Value : "";
	}

	private void CodeEditorMouseMove(object? sender, MouseEventArgs e)
	{
		if (_codeEditor == null || _codeEditor.TextLength == 0)
		{
			return;
		}
		if (AreCodeInteractionSideEffectsSuppressed())
		{
			ClearScintillaFunctionHoverHighlight();
			_codeEditor.Cursor = Cursors.IBeam;
			return;
		}

		int index = GetScintillaPositionFromPoint(_codeEditor, e.Location);
		if (index == _lastFunctionHoverCharIndex)
		{
			_codeEditor.Cursor = _lastFunctionHoverNavigable && (ModifierKeys & Keys.Control) == Keys.Control ? Cursors.Hand : Cursors.IBeam;
			return;
		}

		_lastFunctionHoverCharIndex = index;
		_lastFunctionHoverIdentifier = "";
		_lastFunctionHoverNavigable = false;

		if (index >= 0 && TryGetIdentifierAtCodePoint(_codeEditor, e.Location, out int hoverStart, out int hoverLength, out string identifier))
		{
			_lastFunctionHoverIdentifier = identifier;
			_lastFunctionHoverNavigable =
				IsKnownFunctionName(identifier) &&
				(_currentFunctionSource == null || !identifier.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase));
			SetScintillaFunctionHoverHighlight(hoverStart, hoverLength, _lastFunctionHoverNavigable);
		}
		else
		{
			ClearScintillaFunctionHoverHighlight();
		}

		_codeEditor.Cursor = _lastFunctionHoverNavigable && (ModifierKeys & Keys.Control) == Keys.Control ? Cursors.Hand : Cursors.IBeam;
		if ((DateTime.UtcNow - _lastUiWheelUtc).TotalMilliseconds > 120)
		{
			UpdateProgramTreeFocusFromCodePointThrottled(_codeEditor, e.Location);
		}
	}

	private void CodeEditorMouseWheel(object? sender, MouseEventArgs e)
	{
		MarkUiWheelActivity();
		if ((ModifierKeys & Keys.Control) == Keys.Control)
		{
			AdjustFunctionCodeFont(GetCodeFontWheelDelta(e));
			return;
		}
		ScheduleVisibleDataRefreshAfterScroll();
	}

	private void CodeEditorKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Control && e.KeyCode == Keys.C && _codeEditor != null && !_codeEditor.IsDisposed)
		{
			string selectedText = _codeEditor.SelectedText;
			if (!string.IsNullOrEmpty(selectedText))
			{
				_codeEditor.Copy();
				e.SuppressKeyPress = true;
				e.Handled = true;
			}
			return;
		}

		if (e.Control && e.KeyCode == Keys.F)
		{
			_programSearchBox?.Focus();
			_programSearchBox?.SelectAll();
			e.SuppressKeyPress = true;
			e.Handled = true;
		}
	}

	private void CodeEditorUpdateUI(object? sender, ScintillaNET.UpdateUIEventArgs e)
	{
		if ((e.Change & ScintillaNET.UpdateChange.Selection) != 0)
		{
			UpdateScintillaScopeHighlight();
			UpdateProgramTreeFocusFromScintillaCaretThrottled();
		}

		ScintillaNET.UpdateChange scrollChanges = ScintillaNET.UpdateChange.VScroll | ScintillaNET.UpdateChange.HScroll;
		if ((e.Change & scrollChanges) == 0)
		{
			return;
		}

		MarkUiWheelActivity();
		ScheduleVisibleDataRefreshAfterScroll();
	}

	private void UpdateScintillaScopeHighlight(bool force = false)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		int caret = Math.Clamp(_codeEditor.CurrentPosition, 0, Math.Max(0, _codeEditor.TextLength - 1));
		if (!force && caret == _lastScintillaScopeCaret)
		{
			return;
		}

		_lastScintillaScopeCaret = caret;
		ClearLastScintillaScopeHighlight();
		if (_codeEditor.TextLength == 0)
		{
			return;
		}

		(int Open, int Close)? pair = FindBraceScopePairAt(_codeEditor.Text, caret);
		if (!pair.HasValue)
		{
			return;
		}

		_lastScintillaScopePair = pair.Value;
		_codeEditor.IndicatorCurrent = ScintillaIndicatorScopeBrace;
		if (pair.Value.Open >= 0 && pair.Value.Open < _codeEditor.TextLength)
		{
			_codeEditor.IndicatorFillRange(pair.Value.Open, 1);
		}
		if (pair.Value.Close >= 0 && pair.Value.Close < _codeEditor.TextLength)
		{
			_codeEditor.IndicatorFillRange(pair.Value.Close, 1);
		}
	}

	private void ClearLastScintillaScopeHighlight()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || !_lastScintillaScopePair.HasValue)
		{
			_lastScintillaScopePair = null;
			return;
		}

		_codeEditor.IndicatorCurrent = ScintillaIndicatorScopeBrace;
		(int open, int close) = _lastScintillaScopePair.Value;
		if (open >= 0 && open < _codeEditor.TextLength)
		{
			_codeEditor.IndicatorClearRange(open, 1);
		}
		if (close >= 0 && close < _codeEditor.TextLength)
		{
			_codeEditor.IndicatorClearRange(close, 1);
		}
		_lastScintillaScopePair = null;
	}

	private void CollapseProtectedScintillaSelection(string context)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		int start = _codeEditor.SelectionStart;
		int end = _codeEditor.SelectionEnd;
		int length = Math.Abs(end - start);
		if (length <= 1)
		{
			return;
		}
		if (ShouldPreserveScintillaUserTokenSelection(start, end))
		{
			return;
		}

		if (!AreCodeInteractionSideEffectsSuppressed() && !IsCodeViewportProtected())
		{
			return;
		}

		CollapseScintillaSelection(_codeEditor.CurrentPosition, context);
	}

	private bool ShouldPreserveScintillaUserTokenSelection(int start, int end)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return false;
		}

		int selectionStart = Math.Min(start, end);
		int selectionEnd = Math.Max(start, end);
		int length = selectionEnd - selectionStart;
		if (length <= 0 || length > 128)
		{
			return false;
		}

		int startLine = _codeEditor.LineFromPosition(selectionStart);
		int endLine = _codeEditor.LineFromPosition(Math.Max(selectionStart, selectionEnd - 1));
		if (startLine != endLine)
		{
			return false;
		}

		string selected = _codeEditor.SelectedText.Trim();
		return Regex.IsMatch(selected, @"^[A-Za-z_][A-Za-z0-9_]*$");
	}

	private void FunctionCodeBoxMouseMove(object? sender, MouseEventArgs e)
	{
		if (_functionCodeBox == null || _functionCodeBox.TextLength == 0)
		{
			return;
		}

		int index = _functionCodeBox.GetCharIndexFromPosition(e.Location);
		if (index == _lastFunctionHoverCharIndex)
		{
			_functionCodeBox.Cursor = _lastFunctionHoverNavigable ? Cursors.Hand : Cursors.IBeam;
			return;
		}

		_lastFunctionHoverCharIndex = index;
		_lastFunctionHoverIdentifier = "";
		_lastFunctionHoverNavigable = false;

		if (TryGetIdentifierAt(_functionCodeBox.Text, index, out _, out _, out string identifier))
		{
			_lastFunctionHoverIdentifier = identifier;
			_lastFunctionHoverNavigable =
				IsKnownFunctionName(identifier) &&
				(_currentFunctionSource == null || !identifier.Equals(_currentFunctionSource.FunctionName, StringComparison.OrdinalIgnoreCase));
		}

		_functionCodeBox.Cursor = _lastFunctionHoverNavigable ? Cursors.Hand : Cursors.IBeam;
	}

	private void UpdateActiveFunctionFromMousePosition(int charIndex)
	{
		if (_functionCodeBox == null || _functionCodeBox.TextLength == 0)
		{
			return;
		}

		FunctionSourceView? codeView = GetCodeViewSource();
		if (codeView == null)
		{
			return;
		}

		int lineIndex = Math.Clamp(_functionCodeBox.GetLineFromCharIndex(Math.Clamp(charIndex, 0, _functionCodeBox.TextLength)), 0, Math.Max(0, codeView.Lines.Count - 1));
		if (!TryGetFunctionNameAtCodeViewLine(codeView, lineIndex, out string functionName))
		{
			return;
		}
		if (_currentFunctionSource != null &&
			_currentFunctionSource.FunctionName.Equals(functionName, StringComparison.OrdinalIgnoreCase) &&
			_currentFunctionSource.FilePath.Equals(codeView.FilePath, StringComparison.OrdinalIgnoreCase))
		{
			_lastFunctionHoverContextName = functionName;
			return;
		}

		DateTime now = DateTime.UtcNow;
		if (_lastFunctionHoverContextName.Equals(functionName, StringComparison.OrdinalIgnoreCase) &&
			(now - _lastFunctionHoverContextUtc).TotalMilliseconds < FunctionHoverSwitchMinIntervalMs)
		{
			return;
		}
		_lastFunctionHoverContextName = functionName;
		_lastFunctionHoverContextUtc = now;

		if (!TryLoadFunctionSourceFromFile(codeView.FilePath, functionName, out FunctionSourceView? hoveredSource) ||
			hoveredSource == null)
		{
			return;
		}

		ApplyHoveredFunctionSource(hoveredSource);
	}

	private bool TryGetFunctionNameAtCodeViewLine(FunctionSourceView codeView, int lineIndex, out string functionName)
	{
		functionName = "";
		if (lineIndex < 0 || lineIndex >= codeView.Lines.Count)
		{
			return false;
		}

		string text;
		try
		{
			text = ReadSourceText(codeView.FilePath);
		}
		catch
		{
			text = string.Join("\n", codeView.Lines);
		}
		string? name = FindFunctionNameAtLine(text, codeView.StartLine + lineIndex);
		if (string.IsNullOrWhiteSpace(name) || IsMonitorInternalFunctionName(name))
		{
			return false;
		}

		functionName = name;
		return true;
	}

	private void ApplyHoveredFunctionSource(FunctionSourceView sourceView)
	{
		_currentFunctionSource = sourceView;
		_currentFunctionIdentifiers = BuildIdentifierSet(sourceView.Lines);
		_lastFunctionAnalysisSignature = "";
		_lastVisibleValuesText = "";
		_lastVisibleConditionSignature = "";
		_lastProgramInsightSignature = "";
		_forceDataCodeRtfRefresh = true;
		UpdateFunctionCodeTitle();
		if (_runtimeLocationLabel != null)
		{
			_runtimeLocationLabel.Text = sourceView.FunctionName;
		}
		AutoWatchVariablesForFunction(sourceView);
		UpdateFunctionNavButtons();
		RenderFunctionAnalysis(force: true);
		RenderDataFunctionMirror(resetScroll: false);
		UpdateProgramInsightPanel(force: true);
	}

	private void CodeBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Control && e.KeyCode == Keys.C && sender is RichTextBox codeBox)
		{
			if (!string.IsNullOrEmpty(codeBox.SelectedText))
			{
				Clipboard.SetText(codeBox.SelectedText);
				e.SuppressKeyPress = true;
				e.Handled = true;
			}
			return;
		}

		if (e.Control && e.KeyCode == Keys.F)
		{
			_programSearchBox?.Focus();
			_programSearchBox?.SelectAll();
			e.SuppressKeyPress = true;
			e.Handled = true;
		}
	}

	private void FunctionCodeBoxMouseWheel(object? sender, MouseEventArgs e)
	{
		if (_functionCodeBox == null || (ModifierKeys & Keys.Control) != Keys.Control)
		{
			return;
		}

		AdjustFunctionCodeFont(GetCodeFontWheelDelta(e));
	}

	private static float GetCodeFontWheelDelta(MouseEventArgs e)
	{
		float notches = e.Delta / 120f;
		if (Math.Abs(notches) < 0.01f)
		{
			notches = e.Delta >= 0 ? 1f : -1f;
		}
		notches = Math.Clamp(notches, -4f, 4f);
		return notches * 0.25f;
	}

	private void AdjustFunctionCodeFont(float delta)
	{
		if (_functionCodeBox == null)
		{
			return;
		}

		float nextSize = Math.Clamp(_functionCodeFontSize + delta, 8f, 22f);
		if (Math.Abs(nextSize - _functionCodeFontSize) < 0.01f)
		{
			return;
		}

		if (_codeEditor != null && !_codeEditor.IsDisposed)
		{
			int scintillaFirstVisibleLine = _codeEditor.FirstVisibleLine;
			int scintillaCurrentPosition = _codeEditor.CurrentPosition;
			_functionCodeFontSize = nextSize;
			ApplyScintillaTheme(_codeEditor);
			_lastFunctionCodeText = "";
			_lastDataCodeText = "";
			RenderFunctionSource();
			if (_codeEditor.TextLength > 0)
			{
				_codeEditor.CurrentPosition = Math.Clamp(scintillaCurrentPosition, 0, _codeEditor.TextLength);
				_codeEditor.AnchorPosition = _codeEditor.CurrentPosition;
				_codeEditor.FirstVisibleLine = Math.Clamp(scintillaFirstVisibleLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
			}
			SaveDefaultProfileQuietly();
			return;
		}

		int selectionStart = _functionCodeBox.SelectionStart;
		Point scrollPosition = Point.Empty;
		bool hasHandle = _functionCodeBox.IsHandleCreated;
		int firstVisibleLine = 0;
		if (hasHandle)
		{
			SendMessage(_functionCodeBox.Handle, EmGetScrollPos, IntPtr.Zero, ref scrollPosition);
			firstVisibleLine = GetFirstVisibleLineSafe(_functionCodeBox);
			SendMessage(_functionCodeBox.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
		}

		try
		{
			_functionCodeFontSize = nextSize;
			_functionCodeBox.Font = new Font("Consolas", _functionCodeFontSize);
			if (_dataCodeBox != null)
			{
				_dataCodeBox.Font = new Font("Consolas", _functionCodeFontSize);
			}
			_lastFunctionCodeText = "";
			_lastDataCodeText = "";
			RenderFunctionSource();
			_functionCodeBox.SelectionStart = Math.Min(selectionStart, _functionCodeBox.TextLength);
			_functionCodeBox.SelectionLength = 0;
			if (hasHandle)
			{
				RestoreCodeBoxViewport(_functionCodeBox, scrollPosition, firstVisibleLine);
			}
		}
		finally
		{
			if (hasHandle)
			{
				SendMessage(_functionCodeBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
				RestoreCodeBoxViewport(_functionCodeBox, scrollPosition, firstVisibleLine);
				RestoreCodeBoxViewportLater(_functionCodeBox, scrollPosition, firstVisibleLine);
			}
			_functionCodeBox.Invalidate();
		}

		SaveDefaultProfileQuietly();
	}

	private void FunctionCodeBoxSelectionChanged(object? sender, EventArgs e)
	{
		if (_suppressFunctionScopeHighlight || _functionCodeBox == null || _functionCodeBox.TextLength == 0)
		{
			return;
		}
		if (_lastScopeHighlightSelectionStart == _functionCodeBox.SelectionStart)
		{
			return;
		}
		_lastScopeHighlightSelectionStart = _functionCodeBox.SelectionStart;
		HighlightCurrentScope();
	}

	private void SelectApproximateLineInFunction(int absoluteLine, bool scheduleViewportCorrection = true)
	{
		NavigateCodeToLine(absoluteLine, scheduleViewportCorrection);
	}

	private void NavigateCodeToLine(int absoluteLine, bool scheduleViewportCorrection = true)
	{
		FunctionSourceView? codeView = GetCodeViewSource();
		if (codeView == null || _functionCodeBox == null)
		{
			return;
		}
		int relativeLine = Math.Clamp(absoluteLine - codeView.StartLine, 0, Math.Max(0, codeView.Lines.Count - 1));
		if (_codeEditor != null && !_codeEditor.IsDisposed && _codeEditor.TextLength > 0)
		{
			int lineIndex = Math.Clamp(relativeLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
			int position = _codeEditor.Lines[lineIndex].Position;
			CollapseScintillaSelection(position, "navigate-before-scroll");
			CollapseScintillaSelection(position);
			ScrollDisplayedLineNearTop(relativeLine);
			ApplyProgramSearchHighlight();
			if (!string.IsNullOrWhiteSpace(_activeProgramSearchKeyword))
			{
				SelectActiveSearchMatchOnLine();
			}
			CollapseScintillaSelection(_codeEditor.CurrentPosition, "navigate-after-highlight");
			if (scheduleViewportCorrection)
			{
				ScrollDisplayedLineNearTopOnNextUiTurn(absoluteLine);
			}
			return;
		}
		string text = _functionCodeBox.Text;
		int index = 0;
		for (int i = 0; i < relativeLine && index < text.Length; i++)
		{
			int next = text.IndexOf('\n', index);
			if (next < 0)
			{
				break;
			}
			index = next + 1;
		}
		bool oldSuppressScopeHighlight = _suppressFunctionScopeHighlight;
		_suppressFunctionScopeHighlight = true;
		try
		{
			_functionCodeBox.Focus();
			_functionCodeBox.SelectionStart = Math.Clamp(index, 0, _functionCodeBox.TextLength);
			_functionCodeBox.SelectionLength = 0;
			_lastScopeHighlightSelectionStart = _functionCodeBox.SelectionStart;
		}
		finally
		{
			_suppressFunctionScopeHighlight = oldSuppressScopeHighlight;
		}
		ScrollDisplayedLineNearTop(relativeLine);
		if (scheduleViewportCorrection)
		{
			ScrollDisplayedLineNearTopOnNextUiTurn(absoluteLine);
		}
		ApplyProgramSearchHighlight();
		if (!string.IsNullOrWhiteSpace(_activeProgramSearchKeyword))
		{
			SelectActiveSearchMatchOnLine();
		}
	}

	private void CollapseScintillaSelection(int position, string context = "collapse")
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		int caret = Math.Clamp(position, 0, Math.Max(0, _codeEditor.TextLength));
		try
		{
			_codeEditor.ClearSelections();
			_codeEditor.SetEmptySelection(caret);
			_codeEditor.CurrentPosition = caret;
			_codeEditor.AnchorPosition = caret;
			_codeEditor.SelectionStart = caret;
			_codeEditor.SelectionEnd = caret;
			_codeEditor.ClearSelections();
			_codeEditor.SetSelection(caret, caret);
			_codeEditor.SetEmptySelection(caret);
			_codeEditor.CurrentPosition = caret;
			_codeEditor.AnchorPosition = caret;
			LogScintillaSelectionLeakIfNeeded(context);
		}
		catch
		{
		}
	}

	private void SelectScintillaIdentifierRange(int start, int length)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || length <= 0)
		{
			return;
		}

		int selectionStart = Math.Clamp(start, 0, _codeEditor.TextLength);
		int selectionEnd = Math.Clamp(start + length, selectionStart, _codeEditor.TextLength);
		try
		{
			_codeEditor.ClearSelections();
			_codeEditor.SetSelection(selectionEnd, selectionStart);
			_codeEditor.AnchorPosition = selectionStart;
			_codeEditor.CurrentPosition = selectionEnd;
			_codeEditor.SelectionStart = selectionStart;
			_codeEditor.SelectionEnd = selectionEnd;
		}
		catch
		{
		}
	}

	private void CollapseScintillaSelectionLater(string context, int delayMs = 80)
	{
		try
		{
			BeginInvoke((Action)(() =>
			{
				if (_codeEditor != null && !_codeEditor.IsDisposed)
				{
					CollapseScintillaSelection(_codeEditor.CurrentPosition, context + "-ui");
				}
			}));
			_ = Task.Delay(Math.Max(10, delayMs)).ContinueWith(_ =>
			{
				if (IsDisposed)
				{
					return;
				}
				try
				{
					BeginInvoke((Action)(() =>
					{
						if (_codeEditor != null && !_codeEditor.IsDisposed)
						{
							CollapseScintillaSelection(_codeEditor.CurrentPosition, context + "-delayed");
						}
					}));
				}
				catch
				{
				}
			}, TaskScheduler.Default);
		}
		catch
		{
		}
	}

	private void LogScintillaSelectionLeakIfNeeded(string context)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		int start = _codeEditor.SelectionStart;
		int end = _codeEditor.SelectionEnd;
		if (Math.Abs(end - start) <= 1)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		if ((now - _lastScintillaSelectionLeakLogUtc).TotalSeconds < 2)
		{
			return;
		}

		_lastScintillaSelectionLeakLogUtc = now;
		WriteDiagnosticLog(
			$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Scintilla 选区未折叠：{context}，start={start}，end={end}，current={_codeEditor.CurrentPosition}，anchor={_codeEditor.AnchorPosition}。");
	}

	private void ScrollDisplayedLineNearTopForAbsoluteLine(int absoluteLine, int topPadding = 4)
	{
		FunctionSourceView? codeView = GetCodeViewSource();
		if (codeView == null)
		{
			return;
		}

		int relativeLine = Math.Clamp(absoluteLine - codeView.StartLine, 0, Math.Max(0, codeView.Lines.Count - 1));
		ScrollDisplayedLineNearTop(relativeLine, topPadding);
	}

	private void ScrollDisplayedLineNearTopOnNextUiTurn(int absoluteLine, int topPadding = 4)
	{
		try
		{
			int navigationVersion = _functionNavigationVersion;
			DateTime scheduledAt = DateTime.UtcNow;
			void CorrectViewport()
			{
				if (IsDisposed ||
					navigationVersion != _functionNavigationVersion ||
					_lastUiWheelUtc > scheduledAt ||
					_functionCodeBox == null ||
					_functionCodeBox.IsDisposed)
				{
					return;
				}

				ScrollDisplayedLineNearTopForAbsoluteLine(absoluteLine, topPadding);
				if (_codeEditor != null && !_codeEditor.IsDisposed)
				{
					CollapseScintillaSelection(_codeEditor.CurrentPosition);
				}
			}

			BeginInvoke((Action)CorrectViewport);
			_ = Task.Run(async () =>
			{
				await Task.Delay(70).ConfigureAwait(false);
				if (!IsDisposed)
				{
					BeginInvoke((Action)CorrectViewport);
				}
			});
		}
		catch
		{
		}
	}

	private void ScrollDisplayedLineNearTop(int relativeLine, int topPadding = 4)
	{
		if (_codeEditor != null && !_codeEditor.IsDisposed && _codeEditor.IsHandleCreated && _codeEditor.Lines.Count > 0)
		{
			int scintillaDesiredFirstLine = Math.Max(0, relativeLine - Math.Max(0, topPadding));
			_codeEditor.FirstVisibleLine = Math.Clamp(scintillaDesiredFirstLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
			_codeEditor.XOffset = 0;
			return;
		}

		if (_functionCodeBox == null || !_functionCodeBox.IsHandleCreated || _functionCodeBox.TextLength == 0)
		{
			return;
		}

		int desiredFirstLine = Math.Max(0, relativeLine - Math.Max(0, topPadding));
		int currentFirstLine = GetFirstVisibleLineSafe(_functionCodeBox);
		int delta = desiredFirstLine - currentFirstLine;
		if (delta != 0)
		{
			SendMessage(_functionCodeBox.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(delta));
		}
		ForceCodeBoxLeftAligned(_functionCodeBox);
	}

	private void ClearFocusedVariableHighlight()
	{
		if (string.IsNullOrWhiteSpace(_focusedVariableName) &&
			string.IsNullOrWhiteSpace(_activeProgramSearchKeyword) &&
			_activeProgramSearchLine <= 0)
		{
			return;
		}

		ClearProgramSearchHighlight();
	}

	private void ClearFocusedVariableStateOnly()
	{
		_focusedVariableName = "";
	}

	private void ClearProgramSearchHighlight()
	{
		ClearProgramSearchStateOnly();
		UpdateFunctionCodeTitle();
		if (_codeEditor != null &&
			!_codeEditor.IsDisposed &&
			_currentFunctionSource != null &&
			_functionCodeBox != null &&
			_functionCodePanel != null &&
			_functionCodePanel.Visible)
		{
			ClearScintillaSearchAndFocusDecorations();
			UpdateProgramInsightPanel(force: true);
			return;
		}
		if (_currentFunctionSource != null && _functionCodeBox != null && _functionCodePanel != null && _functionCodePanel.Visible)
		{
			_lastFunctionCodeText = "";
			_lastDataCodeText = "";
			RenderFunctionSource();
		}
		UpdateProgramInsightPanel(force: true);
	}

	private void ClearScintillaSearchAndFocusDecorations()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || _codeEditor.TextLength == 0)
		{
			return;
		}

		_codeEditor.MarkerDeleteAll(ScintillaMarkerSearchLine);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorFocus;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorSearch;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.Invalidate();
	}

	private void ClearProgramSearchStateOnly()
	{
		_activeProgramSearchKeyword = "";
		_activeProgramSearchLine = 0;
		_focusedVariableName = "";
		_programTreeLocateTargetFunction = "";
	}

	private void ApplyProgramSearchHighlight()
	{
		if (_codeEditor != null && !_codeEditor.IsDisposed)
		{
			FunctionSourceView? renderSource = GetCodeViewSource();
			if (renderSource == null)
			{
				return;
			}
			List<CodeLineRender> renderedLines = BuildFunctionRenderLines(
				renderSource,
				includeValues: true,
				out _,
				null,
				inlineValuesInText: ShouldShowInlineCodeValues());
			ApplyScintillaRuntimeHighlights(renderedLines, renderSource);
			return;
		}

		if (_functionCodeBox == null || _functionCodeBox.TextLength == 0)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(_activeProgramSearchKeyword) && _activeProgramSearchLine <= 0)
		{
			return;
		}

		int selectionStart = _functionCodeBox.SelectionStart;
		int selectionLength = _functionCodeBox.SelectionLength;
		Point scrollPosition = Point.Empty;
		bool hasHandle = _functionCodeBox.IsHandleCreated;
		int firstVisibleLine = 0;
		if (hasHandle)
		{
			SendMessage(_functionCodeBox.Handle, EmGetScrollPos, IntPtr.Zero, ref scrollPosition);
			firstVisibleLine = GetFirstVisibleLineSafe(_functionCodeBox);
			SendMessage(_functionCodeBox.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
		}

		_suppressFunctionScopeHighlight = true;
		try
		{
			Color lineColor = _programSearchLineBackColor;
			Color matchColor = _activeProgramSearchKeyword.Equals(_focusedVariableName, StringComparison.OrdinalIgnoreCase)
				? _codeFocusVariableBackColor
				: _programSearchMatchBackColor;
			(int Start, int Length)? lineRange = FindDisplayedLineRange(_activeProgramSearchLine);
			if (lineRange.HasValue)
			{
				_functionCodeBox.Select(lineRange.Value.Start, lineRange.Value.Length);
				_functionCodeBox.SelectionBackColor = lineColor;
				HighlightKeywordRanges(lineRange.Value.Start, lineRange.Value.Length, _activeProgramSearchKeyword, matchColor);
			}
			else if (!string.IsNullOrWhiteSpace(_activeProgramSearchKeyword))
			{
				HighlightKeywordRanges(0, _functionCodeBox.TextLength, _activeProgramSearchKeyword, matchColor);
			}

			_functionCodeBox.Select(Math.Min(selectionStart, _functionCodeBox.TextLength), Math.Min(selectionLength, Math.Max(0, _functionCodeBox.TextLength - selectionStart)));
			if (hasHandle)
			{
				RestoreCodeBoxViewport(_functionCodeBox, scrollPosition, firstVisibleLine);
			}
		}
		finally
		{
			_suppressFunctionScopeHighlight = false;
			if (hasHandle)
			{
				SendMessage(_functionCodeBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
				RestoreCodeBoxViewport(_functionCodeBox, scrollPosition, firstVisibleLine);
				RestoreCodeBoxViewportLater(_functionCodeBox, scrollPosition, firstVisibleLine);
			}
			_functionCodeBox.Invalidate();
		}
	}

	private void SelectActiveSearchMatchOnLine()
	{
		if (_codeEditor != null && !_codeEditor.IsDisposed && !string.IsNullOrWhiteSpace(_activeProgramSearchKeyword))
		{
			FunctionSourceView? searchCodeView = GetCodeViewSource();
			if (searchCodeView == null || _activeProgramSearchLine <= 0)
			{
				return;
			}
			int searchLineIndex = Math.Clamp(_activeProgramSearchLine - searchCodeView.StartLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
			string searchLineText = _codeEditor.Lines[searchLineIndex].Text;
			int searchIndex = FindNextKeywordMatch(searchLineText, _activeProgramSearchKeyword, 0, searchLineText.Length);
			if (searchIndex < 0)
			{
				return;
			}
			int start = _codeEditor.Lines[searchLineIndex].Position + searchIndex;
			CollapseScintillaSelection(start);
			ScrollDisplayedLineNearTop(searchLineIndex);
			return;
		}

		if (_functionCodeBox == null || string.IsNullOrWhiteSpace(_activeProgramSearchKeyword))
		{
			return;
		}

		(int Start, int Length)? lineRange = FindDisplayedLineRange(_activeProgramSearchLine);
		if (!lineRange.HasValue)
		{
			return;
		}

		string text = _functionCodeBox.Text;
		int index = FindNextKeywordMatch(text, _activeProgramSearchKeyword, lineRange.Value.Start, lineRange.Value.Start + lineRange.Value.Length);
		if (index < 0)
		{
			return;
		}
		_functionCodeBox.Select(index, _activeProgramSearchKeyword.Length);
		FunctionSourceView? codeView = GetCodeViewSource();
		if (codeView != null && _activeProgramSearchLine > 0)
		{
			ScrollDisplayedLineNearTop(Math.Clamp(_activeProgramSearchLine - codeView.StartLine, 0, Math.Max(0, codeView.Lines.Count - 1)));
		}
	}

	private (int Start, int Length)? FindDisplayedLineRange(int absoluteLine)
	{
		if (_functionCodeBox == null || absoluteLine <= 0)
		{
			return null;
		}
		string prefix = absoluteLine.ToString().PadLeft(5) + "  ";
		string text = _functionCodeBox.Text;
		int start = text.IndexOf(prefix, StringComparison.Ordinal);
		if (start < 0)
		{
			return null;
		}
		int end = text.IndexOf('\n', start);
		if (end < 0)
		{
			end = text.Length;
		}
		return (start, Math.Max(0, end - start));
	}

	private void HighlightKeywordRanges(int start, int length, string keyword, Color color)
	{
		if (_functionCodeBox == null || string.IsNullOrWhiteSpace(keyword) || length <= 0)
		{
			return;
		}
		string text = _functionCodeBox.Text;
		int end = Math.Min(text.Length, start + length);
		int index = start;
		while (index < end)
		{
			int match = FindNextKeywordMatch(text, keyword, index, end);
			if (match < 0)
			{
				break;
			}
			_functionCodeBox.Select(match, Math.Min(keyword.Length, end - match));
			_functionCodeBox.SelectionBackColor = color;
			index = match + Math.Max(1, keyword.Length);
		}
	}

	private static int FindNextKeywordMatch(string text, string keyword, int start, int end)
	{
		if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
		{
			return -1;
		}
		int boundedStart = Math.Clamp(start, 0, text.Length);
		int boundedEnd = Math.Clamp(end, boundedStart, text.Length);
		if (boundedEnd <= boundedStart || keyword.Length > boundedEnd - boundedStart)
		{
			return -1;
		}

		bool wholeIdentifierOnly = IsCIdentifier(keyword);
		int index = boundedStart;
		while (index < boundedEnd)
		{
			int match = text.IndexOf(keyword, index, boundedEnd - index, StringComparison.OrdinalIgnoreCase);
			if (match < 0)
			{
				return -1;
			}
			if (!wholeIdentifierOnly || IsWholeIdentifierMatch(text, match, keyword.Length))
			{
				return match;
			}
			index = match + Math.Max(1, keyword.Length);
		}
		return -1;
	}

	private static bool IsWholeIdentifierMatch(string text, int start, int length)
	{
		int left = start - 1;
		int right = start + length;
		return (left < 0 || !IsCIdentifierChar(text[left])) &&
			(right >= text.Length || !IsCIdentifierChar(text[right]));
	}

	private static bool IsCIdentifier(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || !IsCIdentifierStart(value[0]))
		{
			return false;
		}
		for (int i = 1; i < value.Length; i++)
		{
			if (!IsCIdentifierChar(value[i]))
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsCIdentifierStart(char ch)
	{
		return ch == '_' || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
	}

	private static bool IsCIdentifierChar(char ch)
	{
		return IsCIdentifierStart(ch) || (ch >= '0' && ch <= '9');
	}

	private void HighlightCurrentScope()
	{
		if (_functionCodeBox == null || _functionCodeBox.TextLength == 0)
		{
			return;
		}
		int selectionStart = _functionCodeBox.SelectionStart;
		int selectionLength = _functionCodeBox.SelectionLength;
		Point scrollPosition = Point.Empty;
		bool hasHandle = _functionCodeBox.IsHandleCreated;
		int firstVisibleLine = 0;
		if (hasHandle)
		{
			SendMessage(_functionCodeBox.Handle, EmGetScrollPos, IntPtr.Zero, ref scrollPosition);
			firstVisibleLine = GetFirstVisibleLineSafe(_functionCodeBox);
			SendMessage(_functionCodeBox.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
		}
		_suppressFunctionScopeHighlight = true;
		try
		{
			foreach ((int start, int length) in _functionScopeHighlights)
			{
				if (start >= 0 && start + length <= _functionCodeBox.TextLength)
				{
					_functionCodeBox.Select(start, length);
					_functionCodeBox.SelectionBackColor = _surface;
				}
			}
			_functionScopeHighlights.Clear();
			(int Open, int Close)? pair = FindScopePairAt(_functionCodeBox.Text, selectionStart);
			if (pair.HasValue)
			{
				Color highlight = Color.FromArgb(92, 77, 24);
				_functionScopeHighlights.Add((pair.Value.Open, 1));
				_functionScopeHighlights.Add((pair.Value.Close, 1));
				_functionCodeBox.Select(pair.Value.Open, 1);
				_functionCodeBox.SelectionBackColor = highlight;
				_functionCodeBox.Select(pair.Value.Close, 1);
				_functionCodeBox.SelectionBackColor = highlight;
			}
			_functionCodeBox.Select(Math.Min(selectionStart, _functionCodeBox.TextLength), Math.Min(selectionLength, Math.Max(0, _functionCodeBox.TextLength - selectionStart)));
			if (hasHandle)
			{
				RestoreCodeBoxViewport(_functionCodeBox, scrollPosition, firstVisibleLine);
			}
		}
		finally
		{
			_suppressFunctionScopeHighlight = false;
			if (hasHandle)
			{
				SendMessage(_functionCodeBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
				RestoreCodeBoxViewport(_functionCodeBox, scrollPosition, firstVisibleLine);
				RestoreCodeBoxViewportLater(_functionCodeBox, scrollPosition, firstVisibleLine);
			}
			_functionCodeBox.Invalidate();
		}
	}

	private static (int Open, int Close)? FindScopePairAt(string text, int caret)
	{
		List<(int Open, int Close)> pairs = BuildScopePairs(text);
		if (pairs.Count == 0)
		{
			return null;
		}
		int pos = Math.Clamp(caret, 0, Math.Max(0, text.Length - 1));
		return pairs
			.Where(p => p.Open == pos || p.Close == pos || p.Open == pos - 1 || p.Close == pos - 1 || (p.Open < pos && p.Close > pos))
			.OrderBy(p => p.Close - p.Open)
			.Cast<(int Open, int Close)?>()
			.FirstOrDefault();
	}

	private static (int Open, int Close)? FindBraceScopePairAt(string text, int caret)
	{
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}

		List<(int Open, int Close)> pairs = BuildScopePairs(text)
			.Where(pair =>
				pair.Open >= 0 &&
				pair.Open < text.Length &&
				text[pair.Open] == '{')
			.ToList();
		if (pairs.Count == 0)
		{
			return null;
		}

		int pos = Math.Clamp(caret, 0, Math.Max(0, text.Length - 1));
		return pairs
			.Where(p => p.Open == pos || p.Close == pos || p.Open == pos - 1 || p.Close == pos - 1 || (p.Open < pos && p.Close > pos))
			.OrderBy(p => p.Close - p.Open)
			.Cast<(int Open, int Close)?>()
			.FirstOrDefault();
	}

	private static List<(int Open, int Close)> BuildScopePairs(string text)
	{
		var pairs = new List<(int Open, int Close)>();
		var stack = new Stack<(char Ch, int Index)>();
		bool inLineComment = false;
		bool inBlockComment = false;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			char next = i + 1 < text.Length ? text[i + 1] : '\0';
			if (inLineComment)
			{
				if (c == '\n') inLineComment = false;
				continue;
			}
			if (inBlockComment)
			{
				if (c == '*' && next == '/')
				{
					inBlockComment = false;
					i++;
				}
				continue;
			}
			if (inString || inChar)
			{
				if (escape)
				{
					escape = false;
					continue;
				}
				if (c == '\\')
				{
					escape = true;
					continue;
				}
				if (inString && c == '"') inString = false;
				else if (inChar && c == '\'') inChar = false;
				continue;
			}
			if (c == '/' && next == '/')
			{
				inLineComment = true;
				i++;
				continue;
			}
			if (c == '/' && next == '*')
			{
				inBlockComment = true;
				i++;
				continue;
			}
			if (c == '"')
			{
				inString = true;
				continue;
			}
			if (c == '\'')
			{
				inChar = true;
				continue;
			}
			if (c == '(' || c == '{')
			{
				stack.Push((c, i));
			}
			else if (c == ')' || c == '}')
			{
				char open = c == ')' ? '(' : '{';
				if (stack.Count > 0 && stack.Peek().Ch == open)
				{
					var item = stack.Pop();
					pairs.Add((item.Index, i));
				}
			}
		}
		return pairs;
	}

	private bool IsKnownFunctionName(string identifier)
	{
		if (string.IsNullOrWhiteSpace(_workDirectory) || !Directory.Exists(_workDirectory))
		{
			return false;
		}

		EnsureFunctionIndex(_workDirectory);
		return _functionIndex.ContainsKey(identifier);
	}

	private void ProgramSearchBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Enter)
		{
			RunProgramSearch();
			e.SuppressKeyPress = true;
		}
	}

	private void SetProgramSearchResultsVisible(bool visible)
	{
		if (_programSearchResults == null || _programSearchResultsRow == null)
		{
			return;
		}
		_programSearchResults.Visible = visible;
		_programSearchResultsRow.Height = visible ? Ui(128) : 0f;
	}

	private void RunProgramSearch()
	{
		if (_programSearchBox == null || _programSearchResults == null)
		{
			return;
		}
		string keyword = _programSearchBox.Text.Trim();
		_programSearchResults.Items.Clear();
		if (keyword.Length == 0)
		{
			SetProgramSearchResultsVisible(false);
			ClearProgramSearchHighlight();
			return;
		}
		if (string.IsNullOrWhiteSpace(_workDirectory) || !Directory.Exists(_workDirectory))
		{
			Log("请先选择工作目录。");
			return;
		}

		FocusVariableAcrossPanels(keyword, updateSearchBox: false);
		foreach (ProgramSearchResult result in SearchProgramSource(_workDirectory, keyword).Take(80))
		{
			_programSearchResults.Items.Add(result);
		}
		SetProgramSearchResultsVisible(_programSearchResults.Items.Count > 0);
		Log(_programSearchResults.Items.Count == 0 ? "未找到：" + keyword : $"搜索到 {_programSearchResults.Items.Count} 项：" + keyword);
	}

	private IEnumerable<ProgramSearchResult> SearchProgramSource(string root, string keyword)
	{
		EnsureFunctionIndex(root);
		foreach (FunctionIndexEntry entry in _functionIndex.Values
			.Where(f => f.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f.Name)
			.Take(30))
		{
			yield return new ProgramSearchResult("函数  " + entry.Name + "    " + GetRelativePathSafe(root, entry.FilePath), entry.FilePath, 1, entry.Name);
		}

		foreach (string file in EnumerateSourceFilesForOpen(root))
		{
			string text;
			try
			{
				text = ReadSourceText(file);
			}
			catch
			{
				continue;
			}
			string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i];
				if (line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				string display = $"{GetRelativePathSafe(root, file)}:{i + 1}    {line.Trim()}";
				yield return new ProgramSearchResult(display, file, i + 1, FindFunctionNameAtLine(text, i + 1));
			}
		}
	}

	private void ProgramSearchResultsDoubleClick(object? sender, EventArgs e)
	{
		if (_programSearchResults?.SelectedItem is not ProgramSearchResult result)
		{
			return;
		}
		string keyword = _programSearchBox?.Text.Trim() ?? "";
		_activeProgramSearchKeyword = keyword;
		_activeProgramSearchLine = result.LineNumber;
		if (!string.IsNullOrWhiteSpace(result.FunctionName) &&
			TryLoadFunctionSourceFromFile(result.FilePath, result.FunctionName, out FunctionSourceView? functionSource) &&
			functionSource != null)
		{
			ShowFunctionSource(functionSource, pushCurrent: _currentFunctionSource != null, clearForward: true);
			SelectApproximateLineInFunction(result.LineNumber);
			return;
		}
		if (TryLoadNearestFunctionSource(result.FilePath, result.LineNumber, out FunctionSourceView? nearest) && nearest != null)
		{
			ShowFunctionSource(nearest, pushCurrent: _currentFunctionSource != null, clearForward: true);
			SelectApproximateLineInFunction(result.LineNumber);
			return;
		}
		if (TryLoadSourceSnippet(result.FilePath, result.LineNumber, out FunctionSourceView? snippet) && snippet != null)
		{
			ShowFunctionSource(snippet, pushCurrent: _currentFunctionSource != null, clearForward: true);
			SelectApproximateLineInFunction(result.LineNumber);
		}
	}

	private static string? FindFunctionNameAtLine(string text, int lineNumber)
	{
		string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
		string codeText = MaskCommentsAndLiteralsPreserveLength(normalized);
		int charIndex = 0;
		for (int i = 1; i < lineNumber && charIndex < normalized.Length; i++)
		{
			int next = normalized.IndexOf('\n', charIndex);
			if (next < 0)
			{
				break;
			}
			charIndex = next + 1;
		}
		Match? best = null;
		foreach (Match match in Regex.Matches(codeText, @"(?m)^[\t ]*(?:[A-Za-z_][A-Za-z0-9_\s\*\(\),\[\]]+?[\s\*]+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)", RegexOptions.IgnoreCase))
		{
			string name = match.Groups["name"].Value;
			if (IsCKeyword(name))
			{
				continue;
			}
			int afterSignature = SkipTriviaAndComments(normalized, match.Index + match.Length);
			if (afterSignature >= codeText.Length || codeText[afterSignature] != '{')
			{
				continue;
			}
			int close = FindMatchingBrace(codeText, afterSignature);
			if (match.Index <= charIndex && close >= charIndex)
			{
				best = match;
			}
		}
		return best?.Groups["name"].Value;
	}

	private bool TryLoadSourceSnippet(string file, int lineNumber, out FunctionSourceView? sourceView)
	{
		string text;
		try
		{
			text = ReadSourceText(file);
		}
		catch
		{
			sourceView = null;
			return false;
		}

		string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		if (lines.Length == 0)
		{
			sourceView = null;
			return false;
		}

		int targetLine = Math.Clamp(lineNumber, 1, lines.Length);
		int startLine = Math.Max(1, targetLine - 12);
		int endLine = Math.Min(lines.Length, targetLine + 28);
		List<string> snippetLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1).ToList();
		string title = Path.GetFileName(file);
		sourceView = new FunctionSourceView(title, file, startLine, snippetLines, 0, 0);
		return true;
	}

	private bool TryLoadNearestFunctionSource(string file, int lineNumber, out FunctionSourceView? sourceView)
	{
		string text;
		try
		{
			text = ReadSourceText(file);
		}
		catch
		{
			sourceView = null;
			return false;
		}
		string? functionName = FindFunctionNameAtLine(text, lineNumber);
		if (functionName != null)
		{
			return TryLoadFunctionSourceFromFile(file, functionName, out sourceView);
		}
		sourceView = null;
		return false;
	}

	private static bool IsIdentifierChar(char c)
	{
		return c == '_' || char.IsLetterOrDigit(c);
	}

	private static bool IsIdentifierStart(char c)
	{
		return c == '_' || char.IsLetter(c);
	}

	private static bool TryGetIdentifierAt(string text, int index, out int start, out int length, out string identifier)
	{
		start = 0;
		length = 0;
		identifier = "";
		if (index < 0 || index >= text.Length || !IsIdentifierChar(text[index]))
		{
			return false;
		}
		start = index;
		while (start > 0 && IsIdentifierChar(text[start - 1]))
		{
			start--;
		}
		int end = index;
		while (end + 1 < text.Length && IsIdentifierChar(text[end + 1]))
		{
			end++;
		}
		length = end - start + 1;
		identifier = text.Substring(start, length);
		return length > 0 && IsIdentifierStart(identifier[0]);
	}

	private void RenderFunctionSource(bool includeValues = true)
	{
		if (_currentFunctionSource == null || _functionCodeBox == null)
		{
			return;
		}
		bool renderValues = includeValues && ShouldShowInlineCodeValues();
		bool resetScroll = _resetFunctionScrollOnNextRender;
		_resetFunctionScrollOnNextRender = false;
		if (_forceDataCodeRtfRefresh && ReferenceEquals(_functionCodeBox, _dataCodeBox))
		{
			_lastFunctionCodeText = "";
			_forceDataCodeRtfRefresh = false;
		}
		RenderFunctionSourceToBox(_functionCodeBox, renderValues, ref _lastFunctionCodeText, resetScroll, applySearchHighlight: true);
		if (renderValues && !ReferenceEquals(_functionCodeBox, _dataCodeBox))
		{
			RenderDataFunctionMirror(resetScroll);
		}
		else if (!renderValues)
		{
			ClearScintillaValueDecorations();
			HideCodeValueOverlay();
			_nextInlineValueFadeUtc = null;
			RefreshScintillaVisibleConditionHighlights(force: true);
		}
	}

	private void RenderDataFunctionMirror(bool resetScroll)
	{
		if (_currentFunctionSource == null || _dataCodeBox == null)
		{
			return;
		}
		if (ReferenceEquals(_functionCodeBox, _dataCodeBox))
		{
			(int Start, int End) currentRange = GetVisibleSourceLineRange(DataMirrorPaddingLines);
			AutoWatchVariablesForVisibleRange(currentRange);
			CapturePollPriorityForVisibleRange(currentRange);
			UpdateVisibleValuesLabel(currentRange);
			_lastVisibleRangeSignature = BuildVisibleRangeSignature(currentRange);
			_lastVisibleConditionSignature = BuildVisibleConditionSignature(currentRange);
			_lastDataInlineRenderUtc = DateTime.UtcNow;
			RefreshScintillaVisibleRuntimeValues(force: true);
			UpdateProgramInsightPanel();
			return;
		}

		(int Start, int End) visibleRange = GetVisibleSourceLineRange(DataMirrorPaddingLines);
		bool resetDataScroll = resetScroll || _resetDataScrollOnNextRender;
		_resetDataScrollOnNextRender = false;
		AutoWatchVariablesForVisibleRange(visibleRange);
		CapturePollPriorityForVisibleRange(visibleRange);
		UpdateVisibleValuesLabel(visibleRange);
		if (_dataCodeTitle != null)
		{
			string relativePath = GetRelativePathSafe(_workDirectory, _currentFunctionSource.FilePath);
			_dataCodeTitle.Text = BuildDataCodeTitle(relativePath, visibleRange);
		}

		if (_forceDataCodeRtfRefresh)
		{
			_lastDataCodeText = "";
			_forceDataCodeRtfRefresh = false;
		}
		bool renderValues = ShouldShowInlineCodeValues();
		RenderFunctionSourceToBox(_dataCodeBox, renderValues, ref _lastDataCodeText, resetDataScroll, applySearchHighlight: false, visibleRange);
		_lastVisibleRangeSignature = BuildVisibleRangeSignature(visibleRange);
		_lastVisibleConditionSignature = BuildVisibleConditionSignature(visibleRange);
		_lastDataInlineRenderUtc = DateTime.UtcNow;
		if (renderValues)
		{
			HideCodeValueOverlay();
		}
		else
		{
			ClearScintillaValueDecorations();
			HideCodeValueOverlay();
			_nextInlineValueFadeUtc = null;
			RefreshScintillaVisibleConditionHighlights(force: true);
		}
		UpdateProgramInsightPanel();
	}

	private string BuildDataCodeTitle(string relativePath, (int Start, int End) visibleRange)
	{
		if (_currentFunctionSource == null)
		{
			return "选择函数查看代码";
		}

		return $"{_currentFunctionSource.FunctionName}    {relativePath}:{visibleRange.Start}-{visibleRange.End}";
	}

	private void HideCodeValueOverlay()
	{
		_lastCodeValueOverlaySignature = "";
		_codeValueOverlayEmptyRefreshCount = 0;
		_lastCodeValueOverlayRowsUtc = DateTime.MinValue;
		if (_codeValueOverlay != null)
		{
			if (_codeValueOverlay.Visible)
			{
				_codeValueOverlay.Parent?.Invalidate(_codeValueOverlay.Bounds, true);
			}
			_codeValueOverlay.Visible = false;
		}
		_codeValueOverlayWindow?.HideOverlay();
		ClearScintillaValueAnnotations();
	}

	private void UpdateCodeValueOverlay(bool force = false)
	{
		if (!ShouldShowCodeValueOverlay() ||
			_currentFunctionSource == null ||
			_codeEditor == null ||
			_codeEditor.IsDisposed ||
			!_codeEditor.IsHandleCreated ||
			_codeEditor.TextLength == 0 ||
			_functionCodePanel == null ||
			!_functionCodePanel.Visible)
		{
			HideCodeValueOverlay();
			return;
		}

		if (_scintillaValueEolTextByLine.Count > 0)
		{
			ClearScintillaValueAnnotations();
		}

		FunctionSourceView? source = GetCodeViewSource();
		if (source == null || source.Lines.Count == 0)
		{
			HideCodeValueOverlay();
			return;
		}
		(int Start, int End) visibleRange = GetVisibleSourceLineRange(DataMirrorPaddingLines);
		List<WatchItem> candidates = GetInlineWatchCandidates(visibleRange);
		var overlayRows = new List<CodeValueOverlayRow>();
		var occupiedByLine = new Dictionary<int, List<Rectangle>>();
		Font valueFont = _codeValueOverlayWindow?.Font ?? _codeValueOverlay?.Font ?? _codeEditor.Font;
		int rowHeight = Math.Max(16, _codeEditor.Lines[0].Height);
		int overlayWidth = _codeEditor.ClientSize.Width;
		int first = Math.Max(visibleRange.Start, source.StartLine);
		int last = Math.Min(visibleRange.End, GetSourceEndLine(source));
		for (int absoluteLine = first; absoluteLine <= last; absoluteLine++)
		{
			int lineIndex = absoluteLine - source.StartLine;
			if (lineIndex < 0 || lineIndex >= source.Lines.Count)
			{
				continue;
			}

			IReadOnlyList<InlineWatchValuePlacement> placements = BuildInlineWatchValuePlacements(source.Lines[lineIndex], candidates);
			if (placements.Count == 0)
			{
				continue;
			}

			if (lineIndex >= _codeEditor.Lines.Count)
			{
				continue;
			}

			if (!occupiedByLine.TryGetValue(lineIndex, out List<Rectangle>? occupied))
			{
				occupied = new List<Rectangle>();
				occupiedByLine[lineIndex] = occupied;
			}

			foreach (InlineWatchValuePlacement placement in placements)
			{
				Size textSize = TextRenderer.MeasureText(
					placement.Value,
					valueFont,
					new Size(10000, rowHeight),
					TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
				int width = Math.Max(8, textSize.Width);

				if (!TryGetInlineValueOverlayPoint(
					lineIndex,
					source.Lines[lineIndex],
					placement,
					width,
					overlayWidth,
					rowHeight,
					out Point valuePoint))
				{
					continue;
				}

				var rect = new Rectangle(valuePoint.X - Ui(2), valuePoint.Y + 1, width + Ui(4), Math.Max(14, rowHeight - 2));
				if (occupied.Any(existing => existing.IntersectsWith(rect)))
				{
					continue;
				}

				occupied.Add(rect);
				overlayRows.Add(new CodeValueOverlayRow(valuePoint.X, valuePoint.Y, placement.Value, placement.Fresh, false));
			}
		}

		string signature = $"{visibleRange.Start}:{visibleRange.End}:{_codeEditor.FirstVisibleLine}:{_codeEditor.XOffset}|" +
			string.Join("|", overlayRows.Select(row => $"{row.X}:{row.Y}:{row.Text}")) +
			$"|{_themeName}|{_functionCodeFontSize:0.0}";
		bool emptyRowsWhileVisible = overlayRows.Count == 0 && _codeValueOverlayWindow != null && _codeValueOverlayWindow.Visible;
		if (signature.Equals(_lastCodeValueOverlaySignature, StringComparison.Ordinal) && !force && !emptyRowsWhileVisible)
		{
			return;
		}

		_lastCodeValueOverlaySignature = signature;
		if (_codeValueOverlay == null)
		{
			return;
		}

		if (overlayRows.Count == 0)
		{
			_codeValueOverlayEmptyRefreshCount++;
			bool recentRows = (DateTime.UtcNow - _lastCodeValueOverlayRowsUtc).TotalMilliseconds < 1200;
			bool wheelActive = (DateTime.UtcNow - _lastUiWheelUtc).TotalMilliseconds < Math.Max(VisibleDataScrollDebounceMs * 3, 360);
			if (_codeValueOverlayWindow != null && _codeValueOverlayWindow.Visible && (_codeValueOverlayEmptyRefreshCount < 3 || recentRows || wheelActive))
			{
				_lastCodeValueOverlaySignature = signature;
				return;
			}

			if (_codeValueOverlay.Visible)
			{
				_codeValueOverlay.Parent?.Invalidate(_codeValueOverlay.Bounds, true);
			}
			_codeValueOverlay.Visible = false;
			_codeValueOverlayWindow?.HideOverlay();
			return;
		}

		_codeValueOverlayEmptyRefreshCount = 0;
		_lastCodeValueOverlayRowsUtc = DateTime.UtcNow;
		Color valueFore = PickCodeValueOverlayForeColor();
		_codeValueOverlay.Font = new Font("Consolas", Math.Max(8.5f, _functionCodeFontSize - 0.35f), FontStyle.Regular);
		_codeValueOverlay.Visible = false;
		_codeValueOverlayWindow ??= new CodeValueOverlayWindow();
		_codeValueOverlayWindow.Font = _codeValueOverlay.Font;
		Rectangle editorBounds = new Rectangle(_codeEditor.PointToScreen(Point.Empty), _codeEditor.ClientSize);
		_codeValueOverlayWindow.ShowRows(
			this,
			editorBounds,
			overlayRows,
			_codeEditor.BackColor,
			valueFore,
			rowHeight);
	}

	private bool ShouldShowCodeValueOverlay()
	{
		return false;
	}

	private bool TryGetInlineValueOverlayPoint(
		int lineIndex,
		string lineText,
		InlineWatchValuePlacement placement,
		int valueWidth,
		int overlayWidth,
		int rowHeight,
		out Point point)
	{
		point = Point.Empty;
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return false;
		}

		int tokenEndIndex = Math.Clamp(placement.TokenIndex + placement.Token.Length, 0, lineText.Length);
		int afterTokenPosition = GetScintillaLinePositionFromCharIndex(lineIndex, lineText, tokenEndIndex);
		int y = _codeEditor.PointYFromPosition(afterTokenPosition);
		if (y + rowHeight < 0 || y > _codeEditor.ClientSize.Height)
		{
			return false;
		}

		int desiredPadding = Ui(7);
		int safeRight = overlayWidth - Ui(8);
		int nextCodeIndex = FindNextNonWhiteSpaceIndex(lineText, tokenEndIndex);
		if (nextCodeIndex > tokenEndIndex)
		{
			int gapStartX = _codeEditor.PointXFromPosition(afterTokenPosition) + desiredPadding;
			int nextCodeX = _codeEditor.PointXFromPosition(GetScintillaLinePositionFromCharIndex(lineIndex, lineText, nextCodeIndex));
			if (gapStartX >= 0 && gapStartX + valueWidth <= nextCodeX - Ui(4) && gapStartX + valueWidth <= safeRight)
			{
				point = new Point(gapStartX, y);
				return true;
			}
		}

		int codeEndIndex = GetCodeVisualEndIndex(lineText);
		int endPosition = GetScintillaLinePositionFromCharIndex(lineIndex, lineText, codeEndIndex);
		int endX = _codeEditor.PointXFromPosition(endPosition);
		int x = endX + Ui(14);
		if (x >= 0 && x + valueWidth <= safeRight)
		{
			point = new Point(x, y);
			return true;
		}

		return false;
	}

	private static int FindNextNonWhiteSpaceIndex(string lineText, int startIndex)
	{
		for (int i = Math.Clamp(startIndex, 0, lineText.Length); i < lineText.Length; i++)
		{
			if (!char.IsWhiteSpace(lineText[i]))
			{
				return i;
			}
		}

		return lineText.Length;
	}

	private static int GetCodeVisualEndIndex(string lineText)
	{
		int end = lineText.Length;
		while (end > 0 && (lineText[end - 1] == '\r' || lineText[end - 1] == '\n' || char.IsWhiteSpace(lineText[end - 1])))
		{
			end--;
		}

		return end;
	}

	private int GetScintillaLinePositionFromCharIndex(int lineIndex, string lineText, int charIndex)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return 0;
		}

		int safeCharIndex = Math.Clamp(charIndex, 0, lineText.Length);
		int byteOffset = Encoding.UTF8.GetByteCount(lineText.Substring(0, safeCharIndex));
		int position = _codeEditor.Lines[lineIndex].Position + byteOffset;
		return Math.Clamp(position, 0, _codeEditor.TextLength);
	}

	private Color PickCodeValueOverlayForeColor()
	{
		Color[] candidates =
		{
			Color.FromArgb(185, 28, 28),
			Color.FromArgb(194, 65, 12),
			Color.FromArgb(161, 98, 7),
			Color.FromArgb(162, 28, 175),
			Color.FromArgb(14, 116, 144),
			Color.FromArgb(234, 179, 8),
			Color.FromArgb(34, 211, 238),
			Color.FromArgb(253, 224, 71)
		};

		return candidates
			.OrderByDescending(color =>
			{
				double contrast = ContrastRatio(color, _surface);
				int codeDistance = Math.Min(
					Math.Min(ColorDistance(color, _ink), ColorDistance(color, _codeCommentColor)),
					Math.Min(ColorDistance(color, _codeKeywordColor), ColorDistance(color, _codeFunctionColor)));
				return contrast * 100.0 + codeDistance;
			})
			.First();
	}

	private void ApplyScintillaValueAnnotations(IReadOnlyList<ScintillaValueAnnotationRow> rows, bool force = false)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		var eolDesired = new Dictionary<int, string>();
		foreach (ScintillaValueAnnotationRow row in rows)
		{
			if (row.LineIndex < 0 || row.LineIndex >= _codeEditor.Lines.Count || string.IsNullOrWhiteSpace(row.Text))
			{
				continue;
			}

			eolDesired[row.LineIndex] = "  " + row.Text;
		}

		foreach (int oldLine in _scintillaValueEolTextByLine.Keys.ToList())
		{
			if (eolDesired.ContainsKey(oldLine))
			{
				continue;
			}

			ClearScintillaEolAnnotation(oldLine);
			_scintillaValueEolTextByLine.Remove(oldLine);
		}

		foreach (KeyValuePair<int, string> pair in eolDesired)
		{
			if (!force &&
				_scintillaValueEolTextByLine.TryGetValue(pair.Key, out string? oldText) &&
				oldText.Equals(pair.Value, StringComparison.Ordinal))
			{
				continue;
			}

			SetScintillaEolAnnotation(pair.Key, pair.Value, ScintillaStyleValueStale);
			_scintillaValueEolTextByLine[pair.Key] = pair.Value;
		}

		_codeEditor.AnnotationVisible = ScintillaNET.Annotation.Hidden;
		SetScintillaEolAnnotationVisible(eolDesired.Count > 0);
		RequestScintillaValueAnnotationRepaint();
	}

	private void RequestScintillaValueAnnotationRepaint()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || !IsHandleCreated)
		{
			return;
		}

		if (Interlocked.Exchange(ref _pendingScintillaValueAnnotationRepaint, 1) != 0)
		{
			return;
		}

		try
		{
			BeginInvoke(new Action(async () =>
			{
				try
				{
					await Task.Delay(35).ConfigureAwait(true);
					ReapplyScintillaValueAnnotationsForPaint();
				}
				finally
				{
					Interlocked.Exchange(ref _pendingScintillaValueAnnotationRepaint, 0);
				}
			}));
		}
		catch
		{
			Interlocked.Exchange(ref _pendingScintillaValueAnnotationRepaint, 0);
		}
	}

	private void ReapplyScintillaValueAnnotationsForPaint()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || !_codeEditor.IsHandleCreated)
		{
			return;
		}

		foreach (KeyValuePair<int, string> pair in _scintillaValueEolTextByLine.ToList())
		{
			if (pair.Key < 0 || pair.Key >= _codeEditor.Lines.Count)
			{
				continue;
			}

			SetScintillaEolAnnotation(pair.Key, pair.Value, ScintillaStyleValueStale);
		}

		SetScintillaEolAnnotationVisible(_scintillaValueEolTextByLine.Count > 0);
		_codeEditor.Invalidate();
		_codeEditor.Update();
	}

	private void ClearScintillaValueAnnotations()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		_codeEditor.AnnotationClearAll();
		_codeEditor.AnnotationVisible = ScintillaNET.Annotation.Hidden;
		ClearAllScintillaEolAnnotations();
		_scintillaValueEolTextByLine.Clear();
	}

	private void SetScintillaEolAnnotation(int lineIndex, string text, int style)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return;
		}

		IntPtr textPtr = StringToHGlobalUtf8(text);
		try
		{
			_codeEditor.DirectMessage(SciEolAnnotationSetText, new IntPtr(lineIndex), textPtr);
			_codeEditor.DirectMessage(SciEolAnnotationSetStyle, new IntPtr(lineIndex), new IntPtr(style));
		}
		finally
		{
			Marshal.FreeHGlobal(textPtr);
		}
	}

	private void ClearScintillaEolAnnotation(int lineIndex)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return;
		}

		SetScintillaEolAnnotation(lineIndex, "", ScintillaStyleValueStale);
	}

	private void ClearAllScintillaEolAnnotations()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		_codeEditor.DirectMessage(SciEolAnnotationClearAll);
		SetScintillaEolAnnotationVisible(false);
	}

	private void SetScintillaEolAnnotationVisible(bool visible)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		_codeEditor.DirectMessage(
			SciEolAnnotationSetVisible,
			new IntPtr(visible ? SciEolAnnotationStandard : SciEolAnnotationHidden));
	}

	private static IntPtr StringToHGlobalUtf8(string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes((text ?? "") + "\0");
		IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
		Marshal.Copy(bytes, 0, ptr, bytes.Length);
		return ptr;
	}

	private static string BuildCompactCodeValueTag(IReadOnlyList<string> values)
	{
		if (values.Count == 0)
		{
			return "";
		}

		return string.Join("  ", values.Take(4).Select(ExtractPureInlineValueText));
	}

	private static string ExtractPureInlineValueText(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}

		int equalsIndex = text.IndexOf('=');
		if (equalsIndex >= 0 && equalsIndex + 1 < text.Length)
		{
			return NormalizeInlineRenderedValue(text.Substring(equalsIndex + 1));
		}

		return NormalizeInlineRenderedValue(text);
	}

	private bool LineHasFreshWatch(string line, IReadOnlyList<WatchItem> candidates)
	{
		if (!CodeValueBlinkEnabled)
		{
			return false;
		}

		foreach (WatchItem item in candidates)
		{
			if (FindWatchTokenInLine(line, item.Name).Length == 0)
			{
				continue;
			}

			if (IsWatchValueFresh(item, DateTime.Now))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsWatchValueFresh(WatchItem item, DateTime now)
	{
		return CodeValueBlinkEnabled &&
			item.Status.Equals("正常", StringComparison.Ordinal) &&
			item.LastUpdate.HasValue &&
			(now - item.LastUpdate.Value).TotalMilliseconds <= CodeValueFreshHighlightMs;
	}

	private void RenderFunctionAnalysis(bool force = false)
	{
		if (_currentFunctionSource == null || _functionAnalysisSummaryBox == null || _functionAnalysisChart == null)
		{
			return;
		}

		string signature = BuildFunctionAnalysisSignature(_currentFunctionSource);
		if (!force && signature.Equals(_lastFunctionAnalysisSignature, StringComparison.Ordinal))
		{
			return;
		}

		_lastFunctionAnalysisSignature = signature;
		FunctionLogicAnalysis analysis = AnalyzeFunctionLogic(_currentFunctionSource);
		string relativePath = GetRelativePathSafe(_workDirectory, _currentFunctionSource.FilePath);
		if (_functionAnalysisTitle != null && (_analysisFunctionPanel == null || _analysisFunctionPanel.Visible))
		{
			_functionAnalysisTitle.Text = $"4 函数位置    {_currentFunctionSource.FunctionName}";
		}

		_functionAnalysisSummaryBox.Text = BuildFunctionAnalysisSummaryText(_currentFunctionSource, relativePath, analysis);
		(IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) graph = BuildFunctionAnalysisFlowChart(_currentFunctionSource, analysis);
		_functionAnalysisChart.SetGraph(graph.nodes, graph.edges);
		_functionAnalysisChart.SetAnimationEnabled(false);
	}

	private string BuildFunctionAnalysisSignature(FunctionSourceView source)
	{
		int hash = 17;
		hash = unchecked(hash * 31 + source.FunctionName.GetHashCode(StringComparison.OrdinalIgnoreCase));
		hash = unchecked(hash * 31 + source.FilePath.GetHashCode(StringComparison.OrdinalIgnoreCase));
		hash = unchecked(hash * 31 + source.StartLine);
		hash = unchecked(hash * 31 + _symbols.Count);
		hash = unchecked(hash * 31 + _mapFilePath.GetHashCode(StringComparison.OrdinalIgnoreCase));
		hash = unchecked(hash * 31 + _businessDictionary.Signature.GetHashCode(StringComparison.OrdinalIgnoreCase));
		foreach (string line in source.Lines)
		{
			hash = unchecked(hash * 31 + line.GetHashCode(StringComparison.Ordinal));
		}

		return hash.ToString("X8");
	}

	private FunctionLogicAnalysis AnalyzeFunctionLogic(FunctionSourceView source)
	{
		var candidates = new List<(FunctionLogicStep Step, int Score)>();
		var seenSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int conditionCount = 0;
		int callCount = 0;
		int assignmentCount = 0;
		int loopCount = 0;
		int displayIndent = 0;
		bool inBlockComment = false;

		for (int lineIndex = 0; lineIndex < source.Lines.Count; lineIndex++)
		{
			string rawLine = source.Lines[lineIndex];
			int sourceLine = source.StartLine + lineIndex;
			string uncommentedLine = StripFunctionAnalysisComment(rawLine, ref inBlockComment);
			string line = FormatCodeLineForDisplay(uncommentedLine, ref displayIndent);
			string code = line.Trim();
			if (code.Length == 0 || code == "{" || code == "}" || code.StartsWith("#", StringComparison.Ordinal))
			{
				continue;
			}

			if (code.Contains(source.FunctionName + "(", StringComparison.OrdinalIgnoreCase) &&
				code.EndsWith("{", StringComparison.Ordinal))
			{
				continue;
			}

			FunctionLogicStep? step = null;
			int stepScore = 0;
			string stepKey = "";
			if (StartsWithFunctionAnalysisKeyword(code, "else if") || StartsWithFunctionAnalysisKeyword(code, "if"))
			{
				conditionCount++;
				string condition = ExtractParenthesizedPreview(code);
				AddFunctionAnalysisIdentifiers(condition, inputs);
				step = new FunctionLogicStep(
					StartsWithFunctionAnalysisKeyword(code, "else if") ? "否则如果" : "判断",
					condition.Length > 0 ? condition : ShortenAnalysisText(code, 44),
					"business",
					"",
					sourceLine);
				stepScore = 7 + GetFunctionAnalysisBusinessScore(condition);
				stepKey = "IF:" + condition;
			}
			else if (StartsWithFunctionAnalysisKeyword(code, "else"))
			{
				step = new FunctionLogicStep("否则", "前面条件不成立时进入", "business", "", sourceLine);
				stepScore = 3;
				stepKey = "ELSE";
			}
			else if (StartsWithFunctionAnalysisKeyword(code, "for") ||
				StartsWithFunctionAnalysisKeyword(code, "while") ||
				StartsWithFunctionAnalysisKeyword(code, "do"))
			{
				loopCount++;
				string condition = ExtractParenthesizedPreview(code);
				AddFunctionAnalysisIdentifiers(condition.Length > 0 ? condition : code, inputs);
				step = new FunctionLogicStep("循环", condition.Length > 0 ? condition : ShortenAnalysisText(code, 44), "timer", "", sourceLine);
				stepScore = 8 + GetFunctionAnalysisBusinessScore(condition.Length > 0 ? condition : code);
				stepKey = "LOOP:" + (condition.Length > 0 ? condition : code);
			}
			else if (StartsWithFunctionAnalysisKeyword(code, "switch"))
			{
				conditionCount++;
				string condition = ExtractParenthesizedPreview(code);
				AddFunctionAnalysisIdentifiers(condition, inputs);
				step = new FunctionLogicStep("选择分支", condition.Length > 0 ? condition : ShortenAnalysisText(code, 44), "business", "", sourceLine);
				stepScore = 8 + GetFunctionAnalysisBusinessScore(condition);
				stepKey = "SWITCH:" + condition;
			}
			else if (code.StartsWith("case ", StringComparison.Ordinal) || code.StartsWith("default", StringComparison.Ordinal))
			{
				step = new FunctionLogicStep("分支", ShortenAnalysisText(code.TrimEnd(':'), 44), "business", "", sourceLine);
				stepScore = 4 + GetFunctionAnalysisBusinessScore(code);
				stepKey = "CASE:" + code.TrimEnd(':');
			}
			else if (StartsWithFunctionAnalysisKeyword(code, "return"))
			{
				string value = code.Substring(Math.Min(code.Length, "return".Length)).Trim().TrimEnd(';');
				AddFunctionAnalysisIdentifiers(value, outputs);
				step = new FunctionLogicStep("返回", value.Length > 0 ? value : "结束当前函数", "storage", "", sourceLine);
				stepScore = 8 + GetFunctionAnalysisBusinessScore(value);
				stepKey = "RETURN:" + value;
			}
			else if (TryExtractFunctionAnalysisCall(code, out string callName, out string callDetail))
			{
				if (IsMonitorInternalFunctionName(callName))
				{
					continue;
				}

				callCount++;
				AddFunctionAnalysisIdentifiers(callDetail, inputs);
				AddFunctionAnalysisOutputsFromCall(callName, callDetail, outputs);
				bool knownFunction = IsKnownFunctionName(callName);
				step = new FunctionLogicStep(
					"调用",
					ShortenAnalysisText(callDetail, 48),
					ClassifyFunctionAnalysisKind(callName + " " + callDetail),
					knownFunction ? callName : "",
					sourceLine);
				stepScore = GetFunctionAnalysisCallScore(callName, callDetail, knownFunction);
				stepKey = "CALL:" + callName + ":" + callDetail;
			}
			else if (TryExtractFunctionAnalysisAssignment(code, out string target, out string expression))
			{
				assignmentCount++;
				if (TryFindSymbolForSourceIdentifier(target, out MapSymbol targetSymbol))
				{
					outputs.Add(targetSymbol.Name);
				}
				else
				{
					outputs.Add(target);
				}
				AddFunctionAnalysisIdentifiers(expression, inputs);
				step = new FunctionLogicStep(
					"赋值",
					ShortenAnalysisText(target + " = " + expression, 52),
					ClassifyFunctionAnalysisKind(target + " " + expression),
					"",
					sourceLine);
				stepScore = GetFunctionAnalysisAssignmentScore(target, expression);
				if (TryFindSymbolForSourceIdentifier(target, out targetSymbol))
				{
					stepScore += 8 + GetFunctionAnalysisBusinessScore(targetSymbol.Name + " " + targetSymbol.TypeName);
				}
				stepKey = "SET:" + NormalizeFunctionAnalysisIdentifier(target);
			}

			if (step == null)
			{
				continue;
			}

			step = step with { Detail = EnrichFunctionAnalysisDetail(step.Detail) };
			AddFunctionAnalysisCandidate(candidates, seenSteps, step, stepScore, stepKey);
		}

		string purpose = InferFunctionPurpose(source, conditionCount, callCount, assignmentCount, loopCount);
		List<FunctionSignal> signals = BuildFunctionSignals(source, inputs, outputs);
		return new FunctionLogicAnalysis(
			purpose,
			BuildCompactFunctionAnalysisSteps(candidates),
			BuildFunctionAnalysisIdentifierList(inputs, signals, writeSide: false),
			BuildFunctionAnalysisIdentifierList(outputs, signals, writeSide: true),
			signals);
	}

	private List<string> BuildFunctionAnalysisIdentifierList(HashSet<string> identifiers, IReadOnlyList<FunctionSignal> signals, bool writeSide)
	{
		var result = new List<string>();
		foreach (FunctionSignal signal in signals)
		{
			bool include = writeSide
				? signal.Direction.Contains("写", StringComparison.Ordinal)
				: signal.Direction.Contains("读", StringComparison.Ordinal);
			if (include && !result.Contains(signal.Name, StringComparer.OrdinalIgnoreCase))
			{
				result.Add(signal.Name);
			}
		}

		foreach (string identifier in identifiers.Where(IsBusinessIdentifier))
		{
			string name = ResolveFunctionAnalysisSymbolName(identifier);
			if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
			{
				result.Add(name);
			}
			if (result.Count >= 14)
			{
				break;
			}
		}

		return result.Take(14).ToList();
	}

	private List<FunctionSignal> BuildFunctionSignals(FunctionSourceView source, HashSet<string> inputs, HashSet<string> outputs)
	{
		var codeIdentifiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		bool inBlockComment = false;
		foreach (string rawLine in source.Lines)
		{
			string code = StripFunctionAnalysisComment(rawLine, ref inBlockComment);
			foreach (Match match in Regex.Matches(code, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
			{
				string identifier = match.Value;
				if (identifier.Length < 2 || IsCKeyword(identifier) || IsMonitorInternalFunctionName(identifier))
				{
					continue;
				}

				codeIdentifiers.TryGetValue(identifier, out int count);
				codeIdentifiers[identifier] = count + 1;
			}
		}

		HashSet<string> readSymbols = ResolveFunctionAnalysisSymbolNames(inputs);
		HashSet<string> writeSymbols = ResolveFunctionAnalysisSymbolNames(outputs);
		var accumulators = new Dictionary<string, FunctionSignalAccumulator>(StringComparer.OrdinalIgnoreCase);
		foreach (string identifier in codeIdentifiers.Keys.Concat(inputs).Concat(outputs).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!TryFindSymbolForSourceIdentifier(identifier, out MapSymbol symbol) || !IsFunctionAnalysisSignalCandidate(symbol))
			{
				continue;
			}

			if (!accumulators.TryGetValue(symbol.Name, out FunctionSignalAccumulator? accumulator))
			{
				accumulator = new FunctionSignalAccumulator { Symbol = symbol };
				accumulators[symbol.Name] = accumulator;
			}

			accumulator.Occurrences += Math.Max(1, codeIdentifiers.GetValueOrDefault(identifier, 0));
			accumulator.Read |= readSymbols.Contains(symbol.Name);
			accumulator.Write |= writeSymbols.Contains(symbol.Name);
			accumulator.Score += GetFunctionAnalysisSignalScore(symbol);
		}

		return accumulators.Values
			.Select(accumulator =>
			{
				string direction = accumulator.Read && accumulator.Write
					? "读写"
					: accumulator.Write
						? "写入"
						: "读取";
				int score = accumulator.Score +
					Math.Min(10, accumulator.Occurrences) +
					(accumulator.Write ? 8 : 0) +
					(accumulator.Read ? 4 : 0) +
					GetFunctionAnalysisBusinessScore(accumulator.Symbol.Name);
				return new FunctionSignal(
					accumulator.Symbol.Name,
					direction,
					DescribeFunctionSignalRole(accumulator.Symbol),
					accumulator.Symbol.TypeName,
					score,
					DescribeBusinessIdentifier(accumulator.Symbol.Name));
			})
			.OrderByDescending(signal => signal.Score)
			.ThenBy(signal => signal.Name, StringComparer.OrdinalIgnoreCase)
			.Take(12)
			.ToList();
	}

	private HashSet<string> ResolveFunctionAnalysisSymbolNames(IEnumerable<string> identifiers)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string identifier in identifiers)
		{
			if (TryFindSymbolForSourceIdentifier(identifier, out MapSymbol symbol) && IsFunctionAnalysisSignalCandidate(symbol))
			{
				names.Add(symbol.Name);
			}
		}

		return names;
	}

	private static bool IsFunctionAnalysisSignalCandidate(MapSymbol symbol)
	{
		return symbol.Name.Length >= 2 &&
			symbol.Size > 0 &&
			!IsMonitorInternalFunctionName(symbol.Name) &&
			!symbol.Name.Contains("CanMonitor", StringComparison.OrdinalIgnoreCase);
	}

	private static int GetFunctionAnalysisSignalScore(MapSymbol symbol)
	{
		int score = 0;
		if (symbol.Size <= 4)
		{
			score += 3;
		}
		if (!string.IsNullOrWhiteSpace(symbol.ObjectName))
		{
			score += 2;
		}
		score += GetFunctionAnalysisBusinessScore(symbol.Name + " " + symbol.TypeName + " " + symbol.ObjectName);
		return score;
	}

	private static string DescribeFunctionSignalRole(MapSymbol symbol)
	{
		string text = symbol.Name + " " + symbol.TypeName + " " + symbol.ObjectName;
		if (ContainsAny(text, "LCD", "Disp", "Display", "Page"))
		{
			return "显示数据";
		}
		if (ContainsAny(text, "CAN", "RBuf", "SBuf", "Rx", "Tx", "Recv", "Send"))
		{
			return "CAN数据";
		}
		if (ContainsAny(text, "AI_", "AO_", "Sensor", "Press", "Mpa", "gADV", "Pin"))
		{
			return "采样换算";
		}
		if (ContainsAny(text, "_DO", "DO_", "PWM", "Motor", "Pump", "Valve", "Relay", "KM"))
		{
			return "输出执行";
		}
		if (ContainsAny(text, "_DI", "DI_", "Remote", "Key", "Input"))
		{
			return "输入状态";
		}
		if (ContainsAny(text, "Dly", "Delay", "Time", "Count", "Cnt"))
		{
			return "计时状态";
		}
		if (ContainsAny(text, "Flg", "Flag", "Sta", "Status", "Err", "Fault", "Alarm", "Stop"))
		{
			return "状态标志";
		}
		if (ContainsAny(text, "Set", "Param", "Config", "Limit"))
		{
			return "设定参数";
		}
		return "业务变量";
	}

	private static void AddFunctionAnalysisCandidate(
		List<(FunctionLogicStep Step, int Score)> candidates,
		HashSet<string> seenSteps,
		FunctionLogicStep step,
		int score,
		string stepKey)
	{
		string key = string.IsNullOrWhiteSpace(stepKey)
			? step.Title + ":" + ShortenAnalysisText(step.Detail, 42)
			: stepKey;
		if (!seenSteps.Add(key) && score < 10)
		{
			return;
		}

		candidates.Add((step, score));
	}

	private List<FunctionLogicStep> BuildCompactFunctionAnalysisSteps(List<(FunctionLogicStep Step, int Score)> candidates)
	{
		const int maxMeaningfulSteps = 10;
		candidates = CompactSemanticCallCandidates(candidates);
		candidates = CompactAssignmentCandidates(candidates);
		candidates = CompactScatteredAssignmentCandidates(candidates);
		if (candidates.Count <= maxMeaningfulSteps)
		{
			return candidates.Select(candidate => candidate.Step).ToList();
		}

		var indexed = candidates
			.Select((candidate, index) => (candidate.Step, candidate.Score, Index: index))
			.ToList();
		var selected = indexed
			.Where(candidate => candidate.Score >= 7)
			.OrderByDescending(candidate => candidate.Score)
			.ThenBy(candidate => candidate.Index)
			.Take(maxMeaningfulSteps)
			.ToList();
		int minimum = Math.Min(5, indexed.Count);
		foreach (var candidate in indexed.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Index))
		{
			if (selected.Count >= minimum)
			{
				break;
			}
			if (selected.Any(item => item.Index == candidate.Index))
			{
				continue;
			}
			selected.Add(candidate);
		}

		selected = selected
			.OrderBy(candidate => candidate.Step.SourceLine)
			.ThenBy(candidate => candidate.Index)
			.Take(maxMeaningfulSteps)
			.ToList();
		return selected.Select(candidate => candidate.Step).ToList();
	}

	private List<(FunctionLogicStep Step, int Score)> CompactSemanticCallCandidates(List<(FunctionLogicStep Step, int Score)> candidates)
	{
		if (candidates.Count == 0)
		{
			return candidates;
		}

		var indexed = candidates
			.Select((candidate, index) => (candidate.Step, candidate.Score, Index: index, StageKey: ClassifySemanticCallStage(candidate.Step)))
			.ToList();
		var compactIndexes = new HashSet<int>();
		var summaries = new List<(FunctionLogicStep Step, int Score)>();
		foreach (var group in indexed
			.Where(item => item.StageKey.Length > 0)
			.GroupBy(item => item.StageKey, StringComparer.OrdinalIgnoreCase))
		{
			List<(FunctionLogicStep Step, int Score, int Index)> members = group
				.Select(item => (item.Step, item.Score, item.Index))
				.ToList();
			if (members.Count < GetSemanticCallStageMinimum(group.Key))
			{
				continue;
			}

			foreach ((_, _, int index) in members)
			{
				compactIndexes.Add(index);
			}
			summaries.Add(BuildSemanticCallStageSummary(group.Key, members.Select(member => (member.Step, member.Score)).ToList()));
		}

		if (summaries.Count == 0)
		{
			return candidates;
		}

		return indexed
			.Where(item => !compactIndexes.Contains(item.Index))
			.Select(item => (item.Step, item.Score))
			.Concat(summaries)
			.OrderBy(candidate => candidate.Step.SourceLine)
			.ThenByDescending(candidate => candidate.Score)
			.ToList();
	}

	private string ClassifySemanticCallStage(FunctionLogicStep step)
	{
		if (!step.Title.Equals("调用", StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}

		string text = step.Detail + " " + step.Kind + " " + step.FunctionName;
		if (IsMonitorInternalFunctionName(step.FunctionName) || text.Contains("CanMonitor_", StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}
		if (ContainsAny(text, "LCD_WR_Data", "LCD_WR_Data2B", "LCD_WR_2B", "LCD_Write", "Disp_", "Display"))
		{
			return "display-call";
		}
		if (ContainsAny(text, "CAN_RBuf", "CAN_SBuf", "CAN1_RBuf", "CAN1_SBuf", "RBuf", "SBuf", "Can_Send", "Can_Rcv", "CAN_Send", "CAN_Rcv"))
		{
			return "can-call";
		}
		if (ContainsAny(text, "Sensor_Logic", "AI_Pin", "gADV", "Mpa", "Press", "bar_mA", "ADC"))
		{
			return "sample-call";
		}
		if (ContainsAny(text, "_DO", "DO_", "PWM", "Motor", "Pump", "Valve", "Relay", "Output"))
		{
			return "output-call";
		}

		return "";
	}

	private static int GetSemanticCallStageMinimum(string stageKey)
	{
		return stageKey switch
		{
			"display-call" => 2,
			_ => 3
		};
	}

	private (FunctionLogicStep Step, int Score) BuildSemanticCallStageSummary(string stageKey, List<(FunctionLogicStep Step, int Score)> calls)
	{
		string action = stageKey switch
		{
			"display-call" => "刷新屏幕业务数据",
			"can-call" => "同步CAN业务数据",
			"sample-call" => "换算输入采样和工程量",
			"output-call" => "驱动输出和执行机构",
			_ => "执行一组业务调用"
		};
		string kind = stageKey switch
		{
			"display-call" => "disp",
			"can-call" => "can",
			"sample-call" or "output-call" => "io",
			_ => "business"
		};
		List<string> targets = calls
			.SelectMany(candidate => ExtractSemanticCallTargets(candidate.Step))
			.Select(identifier => FormatIdentifierForAnalysisTarget(identifier, 16))
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(5)
			.ToList();
		int totalTargets = calls
			.SelectMany(candidate => ExtractSemanticCallTargets(candidate.Step))
			.Select(ResolveFunctionAnalysisSymbolName)
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();
		string detail = targets.Count == 0
			? action
			: action + "：" + string.Join("、", targets);
		if (totalTargets > targets.Count)
		{
			detail += $" 等 {totalTargets} 项";
		}
		int firstLine = calls.Min(candidate => candidate.Step.SourceLine);
		int score = Math.Max(12, calls.Max(candidate => candidate.Score) + Math.Min(10, calls.Count));
		return (new FunctionLogicStep("阶段汇总", ShortenAnalysisText(detail, 76), kind, "", firstLine), score);
	}

	private static IEnumerable<string> ExtractSemanticCallTargets(FunctionLogicStep step)
	{
		string detail = step.Detail;
		int open = detail.IndexOf('(');
		int close = detail.LastIndexOf(')');
		string text = open >= 0 && close > open
			? detail.Substring(open + 1, close - open - 1)
			: detail;
		foreach (Match match in Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]*(?:\s*\[[^\]]+\])?\b"))
		{
			string token = Regex.Replace(match.Value, @"\s+", "");
			if (token.Length < 2 ||
				IsCKeyword(token) ||
				IsMonitorInternalFunctionName(token) ||
				token.Equals(step.FunctionName, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			yield return token;
		}
	}

	private List<(FunctionLogicStep Step, int Score)> CompactScatteredAssignmentCandidates(List<(FunctionLogicStep Step, int Score)> candidates)
	{
		List<(FunctionLogicStep Step, int Score)> assignmentLike = candidates
			.Where(candidate => IsFunctionAnalysisAssignmentLikeStep(candidate.Step))
			.ToList();
		if (assignmentLike.Count <= 3)
		{
			return candidates;
		}

		List<(FunctionLogicStep Step, int Score)> nonAssignments = candidates
			.Where(candidate => !IsFunctionAnalysisAssignmentLikeStep(candidate.Step))
			.ToList();
		int maxStages = nonAssignments.Count >= 5 ? 2 : 3;
		List<(FunctionLogicStep Step, int Score)> stages = assignmentLike
			.GroupBy(candidate => ClassifyAssignmentStageKey(candidate.Step), StringComparer.OrdinalIgnoreCase)
			.Select(group => BuildAssignmentStageSummary(group.Key, group.ToList()))
			.OrderByDescending(candidate => candidate.Score)
			.ThenBy(candidate => candidate.Step.SourceLine)
			.Take(maxStages)
			.ToList();

		return nonAssignments
			.Concat(stages)
			.OrderBy(candidate => candidate.Step.SourceLine)
			.ThenByDescending(candidate => candidate.Score)
			.ToList();
	}

	private List<(FunctionLogicStep Step, int Score)> CompactAssignmentCandidates(List<(FunctionLogicStep Step, int Score)> candidates)
	{
		if (candidates.Count == 0)
		{
			return candidates;
		}

		var compacted = new List<(FunctionLogicStep Step, int Score)>();
		for (int index = 0; index < candidates.Count;)
		{
			if (!IsFunctionAnalysisAssignmentStep(candidates[index].Step))
			{
				compacted.Add(candidates[index]);
				index++;
				continue;
			}

			int start = index;
			while (index < candidates.Count && IsFunctionAnalysisAssignmentStep(candidates[index].Step))
			{
				index++;
			}

			List<(FunctionLogicStep Step, int Score)> block = candidates
				.Skip(start)
				.Take(index - start)
				.ToList();
			if (block.Count < 2)
			{
				compacted.Add(block[0]);
				continue;
			}

			compacted.Add(BuildAssignmentBlockSummary(block));
		}

		return compacted;
	}

	private static bool IsFunctionAnalysisAssignmentStep(FunctionLogicStep step)
	{
		return step.Title.Equals("赋值", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsFunctionAnalysisAssignmentLikeStep(FunctionLogicStep step)
	{
		return step.Title.Equals("赋值", StringComparison.OrdinalIgnoreCase) ||
			step.Title.Equals("赋值汇总", StringComparison.OrdinalIgnoreCase) ||
			step.Title.Equals("阶段汇总", StringComparison.OrdinalIgnoreCase);
	}

	private string ClassifyAssignmentStageKey(FunctionLogicStep step)
	{
		string text = step.Title + " " + step.Detail + " " + step.Kind;
		if (IsResetAssignmentText(step.Detail))
		{
			return "reset";
		}
		if (ContainsAny(text, "LCD", "Disp", "Display", "Page", "显示"))
		{
			return "display";
		}
		if (ContainsAny(text, "CAN", "RBuf", "SBuf", "Tx", "Rx", "接收", "发送"))
		{
			return "can";
		}
		if (ContainsAny(text, "_DO", "DO_", "PWM", "Motor", "Pump", "Valve", "Relay", "KM", "输出", "执行"))
		{
			return "output";
		}
		if (ContainsAny(text, "_DI", "DI_", "AI_", "AO_", "Pin", "Press", "Sensor", "Mpa", "gADV", "输入", "采样"))
		{
			return "input";
		}
		if (ContainsAny(text, "Dly", "Delay", "Time", "Count", "Cnt", "Flg", "Flag", "Sta", "Status", "Err", "Fault", "Alarm", "Stop", "状态", "标志", "计时"))
		{
			return "state";
		}
		if (ContainsAny(text, "Set", "Param", "Config", "Limit", "设定", "参数"))
		{
			return "param";
		}
		return "business";
	}

	private (FunctionLogicStep Step, int Score) BuildAssignmentStageSummary(string stageKey, List<(FunctionLogicStep Step, int Score)> assignments)
	{
		string title = "阶段汇总";
		string action = stageKey switch
		{
			"reset" => "清零或复位状态",
			"display" => "整理显示数据",
			"can" => "整理CAN通信数据",
			"output" => "刷新输出执行状态",
			"input" => "整理输入采样和工程量",
			"state" => "更新状态标志和计时",
			"param" => "同步设定参数",
			_ => "批量更新业务变量"
		};
		List<string> targets = assignments
			.SelectMany(candidate => ExtractAssignmentStageTargets(candidate.Step))
			.Select(identifier => FormatIdentifierForAnalysisTarget(identifier, 16))
			.Where(target => !string.IsNullOrWhiteSpace(target))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(5)
			.ToList();
		int totalTargets = assignments
			.SelectMany(candidate => ExtractAssignmentStageTargets(candidate.Step))
			.Select(ResolveFunctionAnalysisSymbolName)
			.Where(target => !string.IsNullOrWhiteSpace(target))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();
		string detail = targets.Count == 0
			? action
			: action + "：" + string.Join("、", targets);
		if (totalTargets > targets.Count)
		{
			detail += $" 等 {totalTargets} 项";
		}
		string kind = stageKey switch
		{
			"display" => "disp",
			"can" => "can",
			"output" or "input" => "io",
			"state" => "timer",
			"param" or "reset" => "storage",
			_ => assignments
				.GroupBy(candidate => candidate.Step.Kind, StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(group => group.Count())
				.Select(group => group.Key)
				.FirstOrDefault() ?? "business"
		};
		int firstLine = assignments.Min(candidate => candidate.Step.SourceLine);
		int score = Math.Max(10, assignments.Max(candidate => candidate.Score) + Math.Min(8, assignments.Count));
		return (new FunctionLogicStep(title, ShortenAnalysisText(detail, 68), kind, "", firstLine), score);
	}

	private string FormatIdentifierForAnalysisTarget(string identifier, int maxDescriptionLength)
	{
		string name = ResolveFunctionAnalysisSymbolName(identifier);
		if (string.IsNullOrWhiteSpace(name))
		{
			return "";
		}
		if (TryDescribeBusinessIdentifier(name, out string description))
		{
			return name + "(" + ShortenBusinessDescription(description, maxDescriptionLength) + ")";
		}
		return name;
	}

	private static IEnumerable<string> ExtractAssignmentStageTargets(FunctionLogicStep step)
	{
		if (step.Title.Equals("赋值", StringComparison.OrdinalIgnoreCase))
		{
			string target = ExtractAssignmentTargetFromStep(step);
			if (target.Length > 0)
			{
				yield return target;
				yield break;
			}
		}

		foreach (Match match in Regex.Matches(step.Detail, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
		{
			string token = match.Value;
			if (token.Length >= 2 && !IsCKeyword(token) && !IsMonitorInternalFunctionName(token))
			{
				yield return token;
			}
		}
	}

	private static bool IsResetAssignmentText(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		if (ContainsAny(text, "清零", "复位", "reset"))
		{
			return true;
		}
		return Regex.IsMatch(text, @"=\s*(?:0|0x00|false|FALSE|NULL)\b");
	}

	private (FunctionLogicStep Step, int Score) BuildAssignmentBlockSummary(List<(FunctionLogicStep Step, int Score)> assignments)
	{
		List<string> targets = assignments
			.Select(candidate => ExtractAssignmentTargetFromStep(candidate.Step))
			.Select(identifier => FormatIdentifierForAnalysisTarget(identifier, 16))
			.Where(target => !string.IsNullOrWhiteSpace(target))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(5)
			.ToList();
		string action = DescribeAssignmentBlock(assignments);
		string targetText = targets.Count == 0
			? action
			: action + "：" + string.Join("、", targets);
		int distinctTargets = assignments
			.Select(candidate => ExtractAssignmentTargetFromStep(candidate.Step))
			.Select(ResolveFunctionAnalysisSymbolName)
			.Where(target => !string.IsNullOrWhiteSpace(target))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();
		if (distinctTargets > targets.Count)
		{
			targetText += $" 等 {distinctTargets} 项";
		}

		string kind = assignments
			.GroupBy(candidate => candidate.Step.Kind, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(group => group.Count())
			.Select(group => group.Key)
			.FirstOrDefault() ?? "business";
		int firstLine = assignments.Min(candidate => candidate.Step.SourceLine);
		int bestScore = Math.Max(9, assignments.Max(candidate => candidate.Score) + Math.Min(4, assignments.Count / 2));
		return (new FunctionLogicStep("赋值汇总", ShortenAnalysisText(targetText, 64), kind, "", firstLine), bestScore);
	}

	private string ResolveFunctionAnalysisSymbolName(string identifier)
	{
		string normalized = NormalizeFunctionAnalysisIdentifier(identifier);
		if (normalized.Length == 0)
		{
			return "";
		}

		return TryFindSymbolForSourceIdentifier(normalized, out MapSymbol symbol)
			? symbol.Name
			: normalized;
	}

	private static string DescribeAssignmentBlock(List<(FunctionLogicStep Step, int Score)> assignments)
	{
		string all = string.Join(" ", assignments.Select(candidate => candidate.Step.Detail));
		if (IsResetAssignmentBlock(assignments))
		{
			return "清零或复位一组状态";
		}
		if (ContainsAny(all, "LCD", "Disp", "Display", "Page"))
		{
			return "整理显示用变量";
		}
		if (ContainsAny(all, "CAN", "RBuf", "SBuf", "Tx", "Rx"))
		{
			return "整理CAN收发数据";
		}
		if (ContainsAny(all, "_DO", "DO_", "PWM", "Motor", "Pump", "Valve", "Relay"))
		{
			return "刷新输出和执行机构状态";
		}
		if (ContainsAny(all, "_DI", "DI_", "AI_", "AO_", "Pin", "Press", "Sensor"))
		{
			return "整理输入采样和工程量";
		}
		if (ContainsAny(all, "Dly", "Delay", "Time", "Count", "Cnt", "Flg", "Flag", "Sta", "Status", "Err"))
		{
			return "更新状态、标志和计时";
		}
		if (ContainsAny(all, "Set", "Param", "Config", "Limit"))
		{
			return "同步设定参数";
		}
		return "批量更新业务变量";
	}

	private static bool IsResetAssignmentBlock(List<(FunctionLogicStep Step, int Score)> assignments)
	{
		int resetCount = 0;
		foreach ((FunctionLogicStep step, _) in assignments)
		{
			string expression = ExtractAssignmentExpressionFromStep(step);
			if (expression.Equals("0", StringComparison.OrdinalIgnoreCase) ||
				expression.Equals("0x00", StringComparison.OrdinalIgnoreCase) ||
				expression.Equals("false", StringComparison.OrdinalIgnoreCase) ||
				expression.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
				expression.Equals("NULL", StringComparison.OrdinalIgnoreCase))
			{
				resetCount++;
			}
		}

		return assignments.Count >= 2 && resetCount * 2 >= assignments.Count;
	}

	private static string ExtractAssignmentTargetFromStep(FunctionLogicStep step)
	{
		int index = step.Detail.IndexOf('=');
		string target = index >= 0 ? step.Detail.Substring(0, index) : step.Detail;
		return NormalizeFunctionAnalysisIdentifier(target.Trim());
	}

	private static string ExtractAssignmentExpressionFromStep(FunctionLogicStep step)
	{
		int index = step.Detail.IndexOf('=');
		if (index < 0 || index + 1 >= step.Detail.Length)
		{
			return "";
		}

		return step.Detail.Substring(index + 1).Trim().TrimEnd(';', '…').Trim();
	}

	private int GetFunctionAnalysisCallScore(string callName, string callDetail, bool knownFunction)
	{
		int score = knownFunction ? 10 : 5;
		score += GetFunctionAnalysisBusinessScore(callName + " " + callDetail);
		if (callName.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase) ||
			callName.Contains("CAN", StringComparison.OrdinalIgnoreCase) ||
			callName.Contains("PWM", StringComparison.OrdinalIgnoreCase))
		{
			score += 4;
		}
		if (callName.Contains("Delay", StringComparison.OrdinalIgnoreCase) ||
			callName.Contains("Mem", StringComparison.OrdinalIgnoreCase) ||
			callName.Contains("Copy", StringComparison.OrdinalIgnoreCase))
		{
			score -= 3;
		}
		return score;
	}

	private static int GetFunctionAnalysisAssignmentScore(string target, string expression)
	{
		string cleanTarget = NormalizeFunctionAnalysisIdentifier(target);
		int score = 2 + GetFunctionAnalysisBusinessScore(cleanTarget) + GetFunctionAnalysisBusinessScore(expression);
		if (IsBusinessIdentifier(cleanTarget))
		{
			score += 5;
		}
		if (cleanTarget.Length <= 2 || cleanTarget.Equals("i", StringComparison.OrdinalIgnoreCase) ||
			cleanTarget.Equals("j", StringComparison.OrdinalIgnoreCase) ||
			cleanTarget.Equals("k", StringComparison.OrdinalIgnoreCase))
		{
			score -= 5;
		}
		return score;
	}

	private static int GetFunctionAnalysisBusinessScore(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return 0;
		}

		string upper = text.ToUpperInvariant();
		int score = 0;
		string[] strongWords =
		{
			"LCD_WR_DATA2B", "MYLOGIC", "WORK_LOGIC", "LOGIC", "APP_", "CAN", "PWM", "MOTOR",
			"VALVE", "PRESS", "AI_", "AO_", "DI", "DO", "DLY", "FLG", "SET", "ERR",
			"MAIN", "DFS", "KM", "SENSOR"
		};
		foreach (string word in strongWords)
		{
			if (upper.Contains(word, StringComparison.Ordinal))
			{
				score += word.Length <= 3 ? 2 : 3;
			}
		}
		return score;
	}

	private static bool IsMonitorInternalFunctionName(string name)
	{
		return name.StartsWith("CanMonitor_", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("CANMonitor_", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("CanMonitor_BusinessGate", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("CanMonitor_Process", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("CanMonitor_Trace", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeFunctionAnalysisIdentifier(string text)
	{
		string compact = Regex.Replace(text, @"\[[^\]]+\]", "");
		return GetIdentifierBase(compact.Trim());
	}

	private string InferFunctionPurpose(FunctionSourceView source, int conditionCount, int callCount, int assignmentCount, int loopCount)
	{
		ProgramCallGraphNode? node = FindGraphNodeForFunction(source.FunctionName, source.FilePath);
		if (node != null && !string.IsNullOrWhiteSpace(node.Summary) && !node.Summary.Equals(source.FunctionName, StringComparison.OrdinalIgnoreCase))
		{
			return node.Summary;
		}

		string name = source.FunctionName;
		if (name.Contains("CAN", StringComparison.OrdinalIgnoreCase))
		{
			return "处理 CAN 数据收发或报文业务映射";
		}
		if (name.Contains("Logic", StringComparison.OrdinalIgnoreCase) || name.Contains("logic", StringComparison.OrdinalIgnoreCase))
		{
			return "执行业务逻辑判断和状态流转";
		}
		if (name.Contains("PWM", StringComparison.OrdinalIgnoreCase))
		{
			return "根据业务变量计算或输出 PWM";
		}
		if (name.Contains("DI", StringComparison.OrdinalIgnoreCase) || name.Contains("DO", StringComparison.OrdinalIgnoreCase))
		{
			return "整理输入输出点位状态";
		}
		if (loopCount > 0)
		{
			return "按循环结构处理一组状态或数据";
		}
		if (conditionCount > 0)
		{
			return "根据条件分支决定后续业务动作";
		}
		if (callCount > assignmentCount)
		{
			return "串联调用多个下级业务函数";
		}
		return "处理当前函数内的业务赋值和调用顺序";
	}

	private string BuildFunctionAnalysisSummaryText(FunctionSourceView source, string relativePath, FunctionLogicAnalysis analysis)
	{
		string relation = BuildFunctionRelationText(source);
		return ShortenAnalysisText("位置：" + relation + "    文件：" + relativePath + ":" + source.StartLine, 150);
	}

	private string BuildFunctionPurposeText(FunctionSourceView source, FunctionLogicAnalysis analysis)
	{
		if (IsMonitorInternalFunctionName(source.FunctionName))
		{
			return "监控固件内部接口，不属于客户业务逻辑";
		}

		string name = source.FunctionName;
		string body = BuildActiveFunctionAnalysisText(source);
		string all = name + "\n" + body + "\n" + string.Join(" ", analysis.Inputs) + "\n" + string.Join(" ", analysis.Outputs);
		string signalNames = string.Join("、", analysis.Signals.Take(3).Select(signal => FormatFunctionSignalDisplay(signal, 22)));
		string engineeringPurpose = BuildEngineeringPurposeText(source, analysis, all);
		if (!string.IsNullOrWhiteSpace(engineeringPurpose))
		{
			return engineeringPurpose;
		}
		if (analysis.Signals.Count > 0)
		{
			if (analysis.Signals.Any(signal => signal.Role.Equals("显示数据", StringComparison.Ordinal)))
			{
				return "围绕" + signalNames + "整理显示数据，让屏幕反映当前业务状态";
			}
			if (analysis.Signals.Any(signal => signal.Role.Equals("CAN数据", StringComparison.Ordinal)))
			{
				return "围绕" + signalNames + "完成CAN数据解析、打包或业务映射";
			}
			if (analysis.Signals.Any(signal => signal.Role.Equals("采样换算", StringComparison.Ordinal)))
			{
				return "围绕" + signalNames + "把采样值换算成业务可用的工程量";
			}
			if (analysis.Signals.Any(signal => signal.Role.Equals("输出执行", StringComparison.Ordinal) && signal.Direction.Contains("写", StringComparison.Ordinal)))
			{
				return "根据业务状态刷新" + signalNames + "等输出执行变量";
			}
			if (analysis.Signals.Any(signal => signal.Role.Equals("状态标志", StringComparison.Ordinal) && signal.Direction.Contains("写", StringComparison.Ordinal)))
			{
				return "根据条件判断更新" + signalNames + "等状态标志";
			}
			if (analysis.Signals.Any(signal => signal.Direction.Contains("写", StringComparison.Ordinal)) &&
				analysis.Signals.Any(signal => signal.Direction.Contains("读", StringComparison.Ordinal)))
			{
				return "读取关键业务变量并更新" + signalNames + "等结果变量";
			}
		}
		if (ContainsAny(name, "10ms", "1ms", "Tick", "Loop", "Task", "Cycle"))
		{
			return "周期或任务入口，集中调度输入处理、状态判断和输出刷新";
		}
		if (ContainsAny(all, "LCD_WR_Data2B", "_LCD", "Disp", "Display", "Page"))
		{
			return "整理业务变量并刷新屏幕显示";
		}
		if (ContainsAny(all, "Sensor_Logic_V", "AI_Pin", "gADV_mV", "Mpa", "Press"))
		{
			return "把模拟量或压力信号换算成业务可用的工程量";
		}
		if (ContainsAny(all, "CAN_RBuf", "CAN1_RBuf", "RBuf", "Receive", "Recv", "Rcv"))
		{
			return "解析CAN接收数据并写入业务变量";
		}
		if (ContainsAny(all, "CAN_SBuf", "CAN1_SBuf", "SBuf", "Send", "Tx"))
		{
			return "把业务变量打包成CAN发送数据";
		}
		if (ContainsAny(all, "_DO", "DO_", "PWM", "Motor", "Pump", "Valve"))
		{
			return "根据业务状态刷新输出点、PWM或执行机构";
		}
		if (ContainsAny(all, "_DI", "DI_", "Remote", "Key"))
		{
			return "读取输入信号并转换成业务状态";
		}
		if (ContainsAny(all, "Alarm", "Fault", "Stop", "JiTing", "Protect", "Err"))
		{
			return "判断安全保护、急停或故障状态";
		}
		if (ContainsAny(name, "Logic", "logic", "Ctrl", "Control", "Work"))
		{
			return "按条件判断推进业务状态和控制流程";
		}
		if (analysis.Steps.Any(step => step.Title.Equals("赋值汇总", StringComparison.OrdinalIgnoreCase)))
		{
			return "集中更新一组业务变量，供后续显示、通信或输出使用";
		}
		if (analysis.Steps.Any(step => step.Title.Equals("调用", StringComparison.OrdinalIgnoreCase)))
		{
			return "串联调用下级业务函数，完成当前业务阶段";
		}
		if (!string.IsNullOrWhiteSpace(analysis.Summary))
		{
			return analysis.Summary;
		}
		return "处理当前函数内的业务判断和状态更新";
	}

	private string BuildEngineeringPurposeText(FunctionSourceView source, FunctionLogicAnalysis analysis, string activeText)
	{
		string name = source.FunctionName;
		ProgramCallGraphNode? node = FindGraphNodeForFunction(name, source.FilePath);
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		List<ProgramCallGraphNode> callers = node == null
			? new List<ProgramCallGraphNode>()
			: GetGraphLinkedNodes(node.Id, callers: true)
				.Where(n => !IsProgramGraphNoiseNode(n))
				.Take(3)
				.ToList();
		List<ProgramCallGraphNode> callees = node == null
			? new List<ProgramCallGraphNode>()
			: GetGraphLinkedNodes(node.Id, callers: false)
				.Where(n => !IsProgramGraphNoiseNode(n))
				.Take(4)
				.ToList();

		string layerText = "";
		if (node != null && snapshot != null)
		{
			Dictionary<string, int> levels = BuildCallHierarchyLevels(snapshot.CallGraphNodes, snapshot.CallGraphEdges);
			if (levels.TryGetValue(node.Id, out int level))
			{
				layerText = ChartGroupLabel(level);
			}
		}

		string role = InferEngineeringFunctionRole(source, analysis, activeText, callees.Count);
		if (string.IsNullOrWhiteSpace(role))
		{
			return "";
		}

		string prefix;
		if (callers.Count > 0)
		{
			string caller = callers[0].Name;
			prefix = string.IsNullOrWhiteSpace(layerText)
				? "在" + caller + "下"
				: "作为" + layerText + "，由" + caller + "进入";
		}
		else if (!string.IsNullOrWhiteSpace(layerText))
		{
			prefix = "作为" + layerText;
		}
		else
		{
			prefix = "在当前工程中";
		}

		string signalText = analysis.Signals.Count == 0
			? ""
			: "，围绕" + string.Join("、", analysis.Signals.Take(2).Select(signal => FormatFunctionSignalDisplay(signal, 18)));
		string childText = callees.Count >= 2 && role.Contains("调度", StringComparison.Ordinal)
			? "，继续调度" + string.Join("、", callees.Take(2).Select(n => n.Name)) + "等下级业务"
			: "";
		return ShortenAnalysisText(prefix + "，负责" + role + signalText + childText, 104);
	}

	private string InferEngineeringFunctionRole(FunctionSourceView source, FunctionLogicAnalysis analysis, string activeText, int calleeCount)
	{
		string name = source.FunctionName;
		if (ContainsAny(name, "10ms", "1ms", "Tick", "Loop", "Task", "Cycle"))
		{
			return "按周期组织输入处理、状态判断、输出刷新和显示通信";
		}

		bool hasDisplay = analysis.Signals.Any(signal => signal.Role.Equals("显示数据", StringComparison.Ordinal)) ||
			ContainsAny(activeText, "LCD_WR_Data2B", "_LCD", "Disp", "Display", "Page");
		bool hasCan = analysis.Signals.Any(signal => signal.Role.Equals("CAN数据", StringComparison.Ordinal)) ||
			ContainsAny(activeText, "CAN_RBuf", "CAN_SBuf", "RBuf", "SBuf", "Can", "CAN");
		bool hasSample = analysis.Signals.Any(signal => signal.Role.Equals("采样换算", StringComparison.Ordinal)) ||
			ContainsAny(activeText, "Sensor_Logic", "AI_Pin", "gADV", "Mpa", "Press", "Sensor");
		bool writesOutput = analysis.Signals.Any(signal => signal.Role.Equals("输出执行", StringComparison.Ordinal) && signal.Direction.Contains("写", StringComparison.Ordinal)) ||
			ContainsAny(activeText, "_DO", "DO_", "PWM", "Motor", "Pump", "Valve", "Relay", "KM");
		bool writesState = analysis.Signals.Any(signal => signal.Role.Equals("状态标志", StringComparison.Ordinal) && signal.Direction.Contains("写", StringComparison.Ordinal)) ||
			ContainsAny(activeText, "Flg", "Flag", "Sta", "Status", "Err", "Fault", "Alarm", "Stop", "Dly", "Delay", "Cnt", "Count");
		bool hasParam = analysis.Signals.Any(signal => signal.Role.Equals("设定参数", StringComparison.Ordinal)) ||
			ContainsAny(activeText, "Set", "Param", "Config", "Limit");
		bool hasDecision = analysis.Steps.Any(step =>
			step.Title.Equals("判断", StringComparison.OrdinalIgnoreCase) ||
			step.Title.Equals("否则如果", StringComparison.OrdinalIgnoreCase) ||
			step.Title.Equals("选择分支", StringComparison.OrdinalIgnoreCase));
		int callSteps = analysis.Steps.Count(step => step.Title.Equals("调用", StringComparison.OrdinalIgnoreCase));
		int stageSteps = analysis.Steps.Count(step => step.Title.Equals("阶段汇总", StringComparison.OrdinalIgnoreCase) || step.Title.Equals("赋值汇总", StringComparison.OrdinalIgnoreCase));

		if (calleeCount >= 2 && callSteps >= 2 && stageSteps <= 2)
		{
			return "调度当前业务阶段的下级流程";
		}
		if (hasSample && writesOutput)
		{
			return "把输入采样或工程量转换成输出执行状态";
		}
		if (hasSample)
		{
			return "把输入采样换算成后续逻辑可用的业务量";
		}
		if (writesOutput && hasDecision)
		{
			return "根据业务条件决定输出和执行机构动作";
		}
		if (writesOutput)
		{
			return "刷新输出点、PWM或执行机构状态";
		}
		if (hasCan)
		{
			return "完成CAN报文和业务变量之间的映射";
		}
		if (hasDisplay)
		{
			return "把业务状态整理成屏幕显示数据";
		}
		if (writesState && hasDecision)
		{
			return "维护状态机、保护条件和计时标志";
		}
		if (writesState)
		{
			return "更新状态标志、计时和故障保护变量";
		}
		if (hasParam)
		{
			return "同步设定参数和业务限制条件";
		}
		if (hasDecision)
		{
			return "进行业务条件判断并选择后续状态流向";
		}
		if (callSteps >= 2)
		{
			return "串联下级函数完成当前业务阶段";
		}
		if (stageSteps > 0)
		{
			return "集中整理当前阶段需要的业务变量";
		}
		return "";
	}

	private static string BuildActiveFunctionAnalysisText(FunctionSourceView source)
	{
		var builder = new StringBuilder();
		bool inBlockComment = false;
		foreach (string line in source.Lines)
		{
			string code = StripFunctionAnalysisComment(line, ref inBlockComment);
			if (!string.IsNullOrWhiteSpace(code))
			{
				builder.AppendLine(code);
			}
		}
		return builder.ToString();
	}

	private static bool ContainsAny(string text, params string[] tokens)
	{
		return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
	}

	private string BuildFunctionRelationText(FunctionSourceView source)
	{
		ProgramCallGraphNode? node = FindGraphNodeForFunction(source.FunctionName, source.FilePath);
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (node == null || snapshot == null)
		{
			return "等待工程图谱分析";
		}

		List<ProgramCallGraphNode> path = BuildPrimaryCallPath(node);
		if (path.Count > 1)
		{
			return string.Join(" -> ", path.Select(n => n.Name));
		}

		Dictionary<string, int> levels = BuildCallHierarchyLevels(snapshot.CallGraphNodes, snapshot.CallGraphEdges);
		string levelText = levels.TryGetValue(node.Id, out int level) ? ChartGroupLabel(level) : "链路函数";
		string callers = JoinRelationNames(GetGraphLinkedNodes(node.Id, callers: true).Where(n => !IsProgramGraphNoiseNode(n)).Take(3));
		string callees = JoinRelationNames(GetGraphLinkedNodes(node.Id, callers: false).Where(n => !IsProgramGraphNoiseNode(n)).Take(4));
		return $"{levelText}；上级：{callers}；下级：{callees}";
	}

	private static string JoinRelationNames(IEnumerable<ProgramCallGraphNode> nodes)
	{
		string text = string.Join("、", nodes.Select(n => n.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
		return string.IsNullOrWhiteSpace(text) ? "无" : text;
	}

	private (IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) BuildFunctionAnalysisFlowChart(FunctionSourceView source, FunctionLogicAnalysis analysis)
	{
		var nodes = new List<FlowChartNode>();
		var edges = new List<FlowChartEdge>();

		ProgramCallGraphNode? currentNode = FindGraphNodeForFunction(source.FunctionName, source.FilePath);
		if (currentNode == null)
		{
			currentNode = new ProgramCallGraphNode("current:" + source.FunctionName, source.FunctionName, source.FilePath, 1, 0, 0, InferFunctionNodeKind(source.FunctionName, analysis), 0);
		}

		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null || !snapshot.Success)
		{
			nodes.Add(new FlowChartNode(
				"L" + source.StartLine.ToString(CultureInfo.InvariantCulture) + ":current",
				source.FunctionName,
				new RectangleF(18, 18, 430, 30),
				currentNode.TraceId,
				source.FunctionName,
				"treeCurrent",
				"",
				1));
			return (nodes, edges);
		}

		List<ProgramCallGraphNode> graphNodes = GetAllGraphNodes(snapshot)
			.Where(n => !IsProgramGraphNoiseNode(n))
			.DistinctBy(n => n.Id)
			.ToList();
		List<ProgramCallGraphEdge> graphEdges = GetAllGraphEdges(snapshot)
			.Where(edge =>
				graphNodes.Any(n => n.Id.Equals(edge.FromId, StringComparison.OrdinalIgnoreCase)) &&
				graphNodes.Any(n => n.Id.Equals(edge.ToId, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		Dictionary<string, ProgramCallGraphNode> nodeById = graphNodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<ProgramCallGraphNode>> childrenById = graphEdges
			.Where(edge => nodeById.ContainsKey(edge.FromId) && nodeById.ContainsKey(edge.ToId))
			.GroupBy(edge => edge.FromId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.Select(edge => nodeById[edge.ToId])
					.DistinctBy(n => n.Id)
					.OrderByDescending(IsDisplayGraphNode)
					.ThenByDescending(GetBusinessNodeScore)
					.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
					.ToList(),
				StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> levels = BuildCallHierarchyLevels(graphNodes, graphEdges);

		const float rowLeft = 18f;
		const float rowTop = 14f;
		const float rowWidth = 520f;
		const float rowHeight = 30f;
		const int maxRows = 120;
		int row = 0;
		FlowChartNode? AddRow(string id, string text, ProgramCallGraphNode? node, int level, string kind)
		{
			if (row >= maxRows)
			{
				return null;
			}
			FlowChartNode chartNode = new FlowChartNode(
				id,
				text,
				new RectangleF(rowLeft, rowTop + row * rowHeight, rowWidth, rowHeight),
				node?.TraceId ?? 0,
				node?.Name ?? (kind.Equals("treeRoot", StringComparison.OrdinalIgnoreCase) ? "" : ExtractFunctionNameFromTreeText(text)),
				kind,
				"",
				level);
			nodes.Add(chartNode);
			row++;
			return chartNode;
		}

		void AddGap()
		{
			if (row > 0)
			{
				row++;
			}
		}

		void AddEdge(FlowChartNode? from, FlowChartNode? to, string label)
		{
			if (from == null || to == null)
			{
				return;
			}
			edges.Add(new FlowChartEdge(from.Id, to.Id, label));
		}

		FlowChartNode? AddHeader(string text, string id)
		{
			return AddRow(id, text, null, 0, "treeRoot");
		}

		FlowChartNode? AddFunctionRow(string idPrefix, ProgramCallGraphNode node, int depth, bool isCurrent, string prefix)
		{
			int level = levels.TryGetValue(node.Id, out int graphLevel) ? graphLevel : Math.Clamp(depth, 0, 6);
			string rowId = idPrefix + ":" + row.ToString(CultureInfo.InvariantCulture) + ":" + SanitizeChartId(node.Id);
			string name = prefix + node.Name;
			if (!string.IsNullOrWhiteSpace(node.Summary))
			{
				name += "  " + ShortenTreeSummary(node.Summary);
			}
			return AddRow(rowId, name, node, level, isCurrent ? "treeCurrent" : "treeNode");
		}

		bool IsCurrent(ProgramCallGraphNode node)
		{
			return currentNode != null && node.Id.Equals(currentNode.Id, StringComparison.OrdinalIgnoreCase);
		}

		void AddLinearChain(string header, string idPrefix, IReadOnlyList<ProgramCallGraphNode> chain)
		{
			if (chain.Count == 0)
			{
				return;
			}
			AddGap();
			FlowChartNode? previous = AddHeader(header, idPrefix + ":header");
			for (int i = 0; i < chain.Count; i++)
			{
				ProgramCallGraphNode node = chain[i];
				string prefix = i == 0 ? "  " : new string(' ', i * 3) + "└─ ";
				FlowChartNode? current = AddFunctionRow(idPrefix, node, i, IsCurrent(node), prefix);
				AddEdge(previous, current, i == 0 ? "入口" : "进入下级");
				previous = current;
			}
		}

		ProgramCallGraphNode? controlRoot = FindControlBusinessRoot(graphNodes);
		if (controlRoot != null && currentNode != null)
		{
			List<ProgramCallGraphNode> controlPath = BuildShortestCallPath(graphNodes, graphEdges, controlRoot.Id, currentNode.Id);
			if (controlPath.Count > 0)
			{
				HashSet<string> seen = controlPath.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
				List<ProgramCallGraphNode> forward = BuildPrimaryForwardCallPath(currentNode, seen, 5);
				AddLinearChain("控制/业务链", "control", controlPath.Concat(forward).DistinctBy(n => n.Id).ToList());
			}
		}

		IReadOnlyList<ProgramCallGraphNode> displayRoots = FindDisplayRoots(graphNodes);
		if (displayRoots.Count > 0)
		{
			AddGap();
			FlowChartNode? displayHeader = AddHeader(
				"显示输出集合",
				"display:header");
			var displayVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (ProgramCallGraphNode displayRoot in displayRoots)
			{
				AddDisplayBranch(displayRoot, displayHeader, 0, "", new HashSet<string>(displayVisited, StringComparer.OrdinalIgnoreCase));
				displayVisited.Add(displayRoot.Id);
			}
		}

		if (nodes.Count == 0 && currentNode != null)
		{
			AddRow("current:" + source.StartLine.ToString(CultureInfo.InvariantCulture), source.FunctionName, currentNode, 1, "treeCurrent");
		}

		return (nodes, edges);

		void AddDisplayBranch(
			ProgramCallGraphNode node,
			FlowChartNode? parent,
			int depth,
			string prefix,
			HashSet<string> visited)
		{
			if (row >= maxRows || depth > 6 || !visited.Add(node.Id))
			{
				return;
			}
			string connector = depth == 0 ? "  " : prefix + "└─ ";
			FlowChartNode? current = AddFunctionRow("display", node, depth, IsCurrent(node), connector);
			AddEdge(parent, current, depth == 0 ? "显示入口" : "界面分支");

			if (!childrenById.TryGetValue(node.Id, out List<ProgramCallGraphNode>? children))
			{
				return;
			}

			List<ProgramCallGraphNode> displayChildren = children
				.Where(child => ShouldShowDisplayBranchNode(child, depth + 1, childrenById, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase)))
				.Take(depth <= 1 ? 28 : 12)
				.ToList();
			string nextPrefix = prefix + "   ";
			foreach (ProgramCallGraphNode child in displayChildren)
			{
				AddDisplayBranch(child, current, depth + 1, nextPrefix, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
			}
		}
	}

	private static ProgramCallGraphNode? FindControlBusinessRoot(IReadOnlyList<ProgramCallGraphNode> graphNodes)
	{
		return graphNodes
			.Where(n => !IsProgramGraphNoiseNode(n))
			.Where(n => !n.Kind.Equals("driver", StringComparison.OrdinalIgnoreCase) && !n.Kind.Equals("storage", StringComparison.OrdinalIgnoreCase))
			.Where(n => n.Outgoing > 0)
			.OrderByDescending(n => ScoreOfflineRootCandidate(n, includeAnalysisSeeds: false))
			.ThenBy(n => n.Level)
			.ThenByDescending(n => n.Outgoing)
			.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault()
			?? FindPrimaryChartRoot(graphNodes);
	}

	private static IReadOnlyList<ProgramCallGraphNode> FindDisplayRoots(IReadOnlyList<ProgramCallGraphNode> graphNodes)
	{
		return graphNodes
			.Where(IsDisplayGraphNode)
			.Where(n => !IsProgramGraphNoiseNode(n))
			.OrderByDescending(n => n.Outgoing)
			.ThenBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
			.Take(36)
			.ToList();
	}

	private static bool IsDisplayGraphNode(ProgramCallGraphNode node)
	{
		string text = node.Name + " " + node.Kind + " " + node.Summary + " " + node.FilePath;
		return node.Kind.Equals("disp", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("Disp", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("LCD", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("Page", StringComparison.OrdinalIgnoreCase);
	}

	private static bool ShouldShowDisplayBranchNode(
		ProgramCallGraphNode node,
		int depth,
		Dictionary<string, List<ProgramCallGraphNode>> childrenById,
		HashSet<string> seen)
	{
		if (IsProgramGraphNoiseNode(node))
		{
			return false;
		}
		if (depth <= 1 || IsDisplayGraphNode(node))
		{
			return true;
		}
		if (!seen.Add(node.Id) || depth >= 5 || !childrenById.TryGetValue(node.Id, out List<ProgramCallGraphNode>? children))
		{
			return false;
		}
		return children.Any(child => ShouldShowDisplayBranchNode(child, depth + 1, childrenById, seen));
	}

	private int FindFunctionStartLineSafe(ProgramCallGraphNode node)
	{
		if (!string.IsNullOrWhiteSpace(node.FilePath) &&
			File.Exists(node.FilePath) &&
			TryLoadFunctionSourceFromFile(node.FilePath, node.Name, out FunctionSourceView? source) &&
			source != null)
		{
			return source.StartLine;
		}

		return 0;
	}

	private List<ProgramCallGraphNode> BuildPrimaryCallPath(ProgramCallGraphNode currentNode)
	{
		ProgramGraphSnapshot? snapshot = _programGraphSnapshot;
		if (snapshot == null)
		{
			return new List<ProgramCallGraphNode> { currentNode };
		}

		List<ProgramCallGraphNode> visibleNodes = GetAllGraphNodes(snapshot)
			.Where(n => !IsProgramGraphNoiseNode(n))
			.ToList();
		List<ProgramCallGraphEdge> visibleEdges = GetAllGraphEdges(snapshot)
			.Where(edge =>
				visibleNodes.Any(n => n.Id.Equals(edge.FromId, StringComparison.OrdinalIgnoreCase)) &&
				visibleNodes.Any(n => n.Id.Equals(edge.ToId, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		ProgramCallGraphNode? root = FindPrimaryChartRoot(visibleNodes);
		if (root != null)
		{
			List<ProgramCallGraphNode> shortest = BuildShortestCallPath(visibleNodes, visibleEdges, root.Id, currentNode.Id);
			if (shortest.Count > 0)
			{
				return shortest;
			}
		}

		return BuildCallerChainPath(currentNode);
	}

	private List<ProgramCallGraphNode> BuildCallerChainPath(ProgramCallGraphNode currentNode)
	{
		var path = new List<ProgramCallGraphNode>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		ProgramCallGraphNode node = currentNode;
		for (int i = 0; i < 4; i++)
		{
			if (!seen.Add(node.Id))
			{
				break;
			}
			path.Add(node);
			ProgramCallGraphNode? caller = GetGraphLinkedNodes(node.Id, callers: true)
				.Where(n => !IsProgramGraphNoiseNode(n))
				.OrderByDescending(IsPreferredGraphRoot)
				.ThenByDescending(GetBusinessNodeScore)
				.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
			if (caller == null)
			{
				break;
			}
			node = caller;
		}

		path.Reverse();
		return path.Count == 0 ? new List<ProgramCallGraphNode> { currentNode } : path;
	}

	private List<ProgramCallGraphNode> BuildPrimaryForwardCallPath(
		ProgramCallGraphNode currentNode,
		HashSet<string> seen,
		int maxDepth)
	{
		var path = new List<ProgramCallGraphNode>();
		ProgramCallGraphNode node = currentNode;
		for (int depth = 0; depth < maxDepth; depth++)
		{
			ProgramCallGraphNode? next = GetGraphLinkedNodes(node.Id, callers: false)
				.Where(n => !IsProgramGraphNoiseNode(n))
				.Where(n => !seen.Contains(n.Id))
				.OrderByDescending(GetBusinessNodeScore)
				.ThenByDescending(n => n.Outgoing)
				.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
			if (next == null || !seen.Add(next.Id))
			{
				break;
			}

			path.Add(next);
			node = next;
		}

		return path;
	}

	private static List<ProgramCallGraphNode> BuildShortestCallPath(
		IReadOnlyList<ProgramCallGraphNode> nodes,
		IReadOnlyList<ProgramCallGraphEdge> edges,
		string rootId,
		string targetId)
	{
		Dictionary<string, ProgramCallGraphNode> nodeById = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
		if (!nodeById.ContainsKey(rootId) || !nodeById.ContainsKey(targetId))
		{
			return new List<ProgramCallGraphNode>();
		}
		if (rootId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
		{
			return new List<ProgramCallGraphNode> { nodeById[targetId] };
		}

		Dictionary<string, List<string>> outgoing = edges
			.Where(edge => nodeById.ContainsKey(edge.FromId) && nodeById.ContainsKey(edge.ToId))
			.GroupBy(edge => edge.FromId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.Select(edge => edge.ToId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
				StringComparer.OrdinalIgnoreCase);
		var queue = new Queue<string>();
		var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
		queue.Enqueue(rootId);
		while (queue.Count > 0)
		{
			string id = queue.Dequeue();
			if (!outgoing.TryGetValue(id, out List<string>? children))
			{
				continue;
			}
			foreach (string child in children)
			{
				if (!seen.Add(child))
				{
					continue;
				}
				previous[child] = id;
				if (child.Equals(targetId, StringComparison.OrdinalIgnoreCase))
				{
					var path = new List<ProgramCallGraphNode>();
					string cursor = targetId;
					while (true)
					{
						path.Add(nodeById[cursor]);
						if (cursor.Equals(rootId, StringComparison.OrdinalIgnoreCase))
						{
							break;
						}
						if (!previous.TryGetValue(cursor, out cursor!))
						{
							return new List<ProgramCallGraphNode>();
						}
					}
					path.Reverse();
					return path;
				}
				queue.Enqueue(child);
			}
		}

		return new List<ProgramCallGraphNode>();
	}

	private List<FunctionLogicStep> BuildFunctionBusinessBlocks(
		FunctionSourceView source,
		FunctionLogicAnalysis analysis,
		ProgramCallGraphNode? currentNode,
		IReadOnlyList<ProgramCallGraphNode> callees)
	{
		var blocks = new List<FunctionLogicStep>();
		string activeText = BuildActiveFunctionAnalysisText(source);
		void AddBlock(string title, string detail, string kind, int line, string functionName = "")
		{
			if (string.IsNullOrWhiteSpace(detail))
			{
				return;
			}
			if (blocks.Any(block => block.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}
			blocks.Add(new FunctionLogicStep(title, ShortenAnalysisText(detail, 74), kind, functionName, Math.Max(1, line)));
		}

		if (callees.Count > 0 && (IsPreferredGraphRootName(source.FunctionName) || callees.Count >= 2))
		{
			string detail = string.Join("、", callees.Take(4).Select(node => node.Name));
			AddBlock("调度下级", detail, "period10", source.StartLine, callees[0].Name);
		}

		List<FunctionSignal> inputSignals = analysis.Signals
			.Where(signal => signal.Direction.Contains("读", StringComparison.Ordinal) &&
				(signal.Role.Equals("输入状态", StringComparison.Ordinal) ||
					signal.Role.Equals("采样换算", StringComparison.Ordinal) ||
					signal.Role.Equals("CAN数据", StringComparison.Ordinal) ||
					signal.Role.Equals("设定参数", StringComparison.Ordinal)))
			.Take(4)
			.ToList();
		if (inputSignals.Count > 0)
		{
			AddBlock(
				"输入来源",
				string.Join("、", inputSignals.Select(signal => FormatFunctionSignalDisplay(signal, 18))),
				ClassifyFunctionAnalysisKind(string.Join(" ", inputSignals.Select(signal => signal.Name))),
				FindFunctionStepLine(analysis, step => step.Title.Contains("调用", StringComparison.Ordinal) || step.Title.Contains("判断", StringComparison.Ordinal), source.StartLine));
		}
		else if (analysis.Inputs.Count > 0)
		{
			AddBlock("输入来源", string.Join("、", analysis.Inputs.Take(4)), "business", source.StartLine);
		}

		List<FunctionLogicStep> decisionSteps = analysis.Steps
			.Where(step =>
				step.Title.Equals("判断", StringComparison.OrdinalIgnoreCase) ||
				step.Title.Equals("否则如果", StringComparison.OrdinalIgnoreCase) ||
				step.Title.Equals("选择分支", StringComparison.OrdinalIgnoreCase) ||
				step.Title.Equals("循环", StringComparison.OrdinalIgnoreCase))
			.Take(3)
			.ToList();
		if (decisionSteps.Count > 0)
		{
			AddBlock(
				"条件判断",
				string.Join("；", decisionSteps.Select(step => step.Detail)),
				"business",
				decisionSteps[0].SourceLine);
		}

		List<FunctionSignal> stateSignals = analysis.Signals
			.Where(signal => signal.Direction.Contains("写", StringComparison.Ordinal) &&
				(signal.Role.Equals("状态标志", StringComparison.Ordinal) ||
					signal.Role.Equals("计时状态", StringComparison.Ordinal) ||
					signal.Role.Equals("业务变量", StringComparison.Ordinal) ||
					signal.Role.Equals("设定参数", StringComparison.Ordinal)))
			.Take(4)
			.ToList();
		if (stateSignals.Count > 0)
		{
			AddBlock(
				"状态计算",
				string.Join("、", stateSignals.Select(signal => FormatFunctionSignalDisplay(signal, 18))),
				"storage",
				FindFunctionStepLine(analysis, step => IsFunctionAnalysisAssignmentLikeStep(step), source.StartLine));
		}
		else
		{
			FunctionLogicStep? assignment = analysis.Steps.FirstOrDefault(IsFunctionAnalysisAssignmentLikeStep);
			if (assignment != null)
			{
				AddBlock("状态计算", assignment.Detail, assignment.Kind, assignment.SourceLine);
			}
		}

		List<FunctionSignal> outputSignals = analysis.Signals
			.Where(signal => signal.Direction.Contains("写", StringComparison.Ordinal) &&
				(signal.Role.Equals("输出执行", StringComparison.Ordinal) ||
					signal.Name.Contains("_DO", StringComparison.OrdinalIgnoreCase) ||
					signal.Name.Contains("PWM", StringComparison.OrdinalIgnoreCase)))
			.Take(4)
			.ToList();
		if (outputSignals.Count > 0)
		{
			AddBlock(
				"输出执行",
				string.Join("、", outputSignals.Select(signal => FormatFunctionSignalDisplay(signal, 18))),
				"io",
				FindFunctionStepLine(analysis, step => step.Kind.Equals("io", StringComparison.OrdinalIgnoreCase), source.StartLine));
		}

		if (ContainsAny(activeText, "LCD_WR_Data2B", "LCD_WR_Data", "Display", "Disp", "Page"))
		{
			AddBlock(
				"屏幕显示",
				"把业务状态写入屏幕变量区",
				"disp",
				FindFunctionStepLine(analysis, step => step.Detail.Contains("LCD", StringComparison.OrdinalIgnoreCase) || step.Kind.Equals("disp", StringComparison.OrdinalIgnoreCase), source.StartLine));
		}
		if (ContainsAny(activeText, "CAN_Send", "CAN_receive", "CAN_RBuf", "CAN_SBuf", "RBuf", "SBuf"))
		{
			AddBlock(
				"CAN交互",
				"完成报文收发和业务变量映射",
				"can",
				FindFunctionStepLine(analysis, step => step.Kind.Equals("can", StringComparison.OrdinalIgnoreCase), source.StartLine));
		}

		if (blocks.Count < 3)
		{
			foreach (FunctionLogicStep step in analysis.Steps.Where(step => !IsMonitorInternalFunctionName(step.FunctionName)).Take(5))
			{
				AddBlock(step.Title, step.Detail, step.Kind, step.SourceLine, step.FunctionName);
				if (blocks.Count >= 4)
				{
					break;
				}
			}
		}

		return blocks.Take(6).ToList();
	}

	private static int FindFunctionStepLine(FunctionLogicAnalysis analysis, Func<FunctionLogicStep, bool> predicate, int fallbackLine)
	{
		FunctionLogicStep? step = analysis.Steps.FirstOrDefault(predicate);
		return step == null || step.SourceLine <= 0 ? fallbackLine : step.SourceLine;
	}

	private static string InferFunctionNodeKind(string functionName, FunctionLogicAnalysis analysis)
	{
		if (IsPreferredGraphRootName(functionName))
		{
			return "period10";
		}
		if (functionName.Contains("LCD", StringComparison.OrdinalIgnoreCase) ||
			functionName.Contains("Disp", StringComparison.OrdinalIgnoreCase) ||
			analysis.Signals.Any(signal => signal.Role.Equals("显示数据", StringComparison.Ordinal)))
		{
			return "disp";
		}
		if (functionName.Contains("CAN", StringComparison.OrdinalIgnoreCase) ||
			analysis.Signals.Any(signal => signal.Role.Equals("CAN数据", StringComparison.Ordinal)))
		{
			return "can";
		}
		if (analysis.Signals.Any(signal => signal.Role.Equals("输出执行", StringComparison.Ordinal) || signal.Role.Equals("输入状态", StringComparison.Ordinal)))
		{
			return "io";
		}
		return "business";
	}

	private static string SanitizeChartId(string value)
	{
		return Regex.Replace(value, @"[^A-Za-z0-9_:\-.]+", "_");
	}

	private static bool StartsWithFunctionAnalysisKeyword(string code, string keyword)
	{
		if (!code.StartsWith(keyword, StringComparison.Ordinal))
		{
			return false;
		}

		return code.Length == keyword.Length || !IsIdentifierChar(code[keyword.Length]);
	}

	private static string StripFunctionAnalysisComment(string line)
	{
		bool inBlockComment = false;
		return StripFunctionAnalysisComment(line, ref inBlockComment);
	}

	private static string StripFunctionAnalysisComment(string line, ref bool inBlockComment)
	{
		if (string.IsNullOrEmpty(line))
		{
			return "";
		}

		var builder = new StringBuilder(line.Length);
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			char next = i + 1 < line.Length ? line[i + 1] : '\0';
			if (inBlockComment)
			{
				if (c == '*' && next == '/')
				{
					inBlockComment = false;
					i++;
				}
				continue;
			}
			if (inString || inChar)
			{
				builder.Append(c);
				if (escape)
				{
					escape = false;
					continue;
				}
				if (c == '\\')
				{
					escape = true;
					continue;
				}
				if (inString && c == '"')
				{
					inString = false;
				}
				else if (inChar && c == '\'')
				{
					inChar = false;
				}
				continue;
			}
			if (c == '/' && next == '/')
			{
				break;
			}
			if (c == '/' && next == '*')
			{
				inBlockComment = true;
				i++;
				continue;
			}
			if (c == '"')
			{
				inString = true;
			}
			else if (c == '\'')
			{
				inChar = true;
			}
			builder.Append(c);
		}

		return builder.ToString();
	}

	private static string ExtractParenthesizedPreview(string code)
	{
		int open = code.IndexOf('(');
		if (open < 0)
		{
			return "";
		}

		int depth = 0;
		for (int i = open; i < code.Length; i++)
		{
			if (code[i] == '(')
			{
				depth++;
			}
			else if (code[i] == ')')
			{
				depth--;
				if (depth == 0)
				{
					return ShortenAnalysisText(code.Substring(open + 1, i - open - 1).Trim(), 54);
				}
			}
		}

		return "";
	}

	private static bool TryExtractFunctionAnalysisCall(string code, out string callName, out string callDetail)
	{
		foreach (Match match in Regex.Matches(code, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
		{
			string name = match.Groups["name"].Value;
			if (IsCKeyword(name) || name.Equals("sizeof", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string args = ExtractCallArgumentPreview(code, match.Index + match.Length - 1);
			callName = name;
			callDetail = args.Length > 0 ? $"{name}({args})" : name;
			return true;
		}

		callName = "";
		callDetail = "";
		return false;
	}

	private static string ExtractCallArgumentPreview(string code, int openParenIndex)
	{
		if (openParenIndex < 0 || openParenIndex >= code.Length || code[openParenIndex] != '(')
		{
			return "";
		}

		int depth = 0;
		for (int i = openParenIndex; i < code.Length; i++)
		{
			if (code[i] == '(')
			{
				depth++;
			}
			else if (code[i] == ')')
			{
				depth--;
				if (depth == 0)
				{
					return ShortenAnalysisText(code.Substring(openParenIndex + 1, i - openParenIndex - 1).Trim(), 42);
				}
			}
		}

		return "";
	}

	private static bool TryExtractFunctionAnalysisAssignment(string code, out string target, out string expression)
	{
		target = "";
		expression = "";
		int index = FindAssignmentOperatorIndex(code);
		if (index < 0)
		{
			return false;
		}

		string left = code.Substring(0, index).Trim();
		string right = code.Substring(index + 1).Trim().TrimEnd(';');
		Match leftIdentifier = Regex.Match(left, @"(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\s*\[[^\]]+\])?)\s*$");
		if (!leftIdentifier.Success || right.Length == 0)
		{
			return false;
		}

		target = Regex.Replace(leftIdentifier.Groups["name"].Value, @"\s+", "");
		expression = right;
		return target.Length > 0;
	}

	private static int FindAssignmentOperatorIndex(string code)
	{
		for (int i = 0; i < code.Length; i++)
		{
			if (code[i] != '=')
			{
				continue;
			}
			char before = i > 0 ? code[i - 1] : '\0';
			char after = i + 1 < code.Length ? code[i + 1] : '\0';
			if (before == '=' || before == '!' || before == '<' || before == '>' || after == '=')
			{
				continue;
			}
			return i;
		}

		return -1;
	}

	private static void AddFunctionAnalysisIdentifiers(string text, HashSet<string> target)
	{
		foreach (Match match in Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
		{
			string value = match.Value;
			if (value.Length >= 2 && !IsCKeyword(value))
			{
				target.Add(value);
			}
		}
	}

	private static void AddFunctionAnalysisOutputsFromCall(string callName, string callDetail, HashSet<string> outputs)
	{
		if (callName.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase) ||
			callName.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
			callName.Contains("PWM", StringComparison.OrdinalIgnoreCase))
		{
			foreach (Match match in Regex.Matches(callDetail, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
			{
				string value = match.Value;
				if (value.Length >= 2 && !value.Equals(callName, StringComparison.OrdinalIgnoreCase) && !IsCKeyword(value))
				{
					outputs.Add(value);
				}
			}
		}
	}

	private static string ClassifyFunctionAnalysisKind(string text)
	{
		if (text.Contains("LCD", StringComparison.OrdinalIgnoreCase) || text.Contains("disp", StringComparison.OrdinalIgnoreCase))
		{
			return "disp";
		}
		if (text.Contains("CAN", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("Rcv", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("Receive", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("Send", StringComparison.OrdinalIgnoreCase))
		{
			return "can";
		}
		if (text.Contains("DI", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("DO", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("AI_", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("AO_", StringComparison.OrdinalIgnoreCase) ||
			text.Contains("PWM", StringComparison.OrdinalIgnoreCase))
		{
			return "io";
		}
		if (text.Contains("Delay", StringComparison.OrdinalIgnoreCase) || text.Contains("Dly", StringComparison.OrdinalIgnoreCase))
		{
			return "timer";
		}
		return "business";
	}

	private static string ShortenAnalysisText(string text, int maxLength)
	{
		text = Regex.Replace(text.Trim(), @"\s+", " ");
		if (text.Length <= maxLength)
		{
			return text;
		}
		return text.Substring(0, Math.Max(1, maxLength - 1)) + "…";
	}

	private void ScheduleNextInlineValueFade((int Start, int End) visibleRange)
	{
		if (!CodeValueBlinkEnabled)
		{
			_nextInlineValueFadeUtc = null;
			return;
		}

		DateTime now = DateTime.Now;
		DateTime? nextFade = null;
		foreach (WatchItem item in GetVisibleWatchItems(visibleRange))
		{
			if (!item.Status.Equals("正常", StringComparison.Ordinal) || !item.LastUpdate.HasValue)
			{
				continue;
			}

			DateTime fadeAt = item.LastUpdate.Value.AddMilliseconds(CodeValueFreshHighlightMs);
			if (fadeAt <= now)
			{
				continue;
			}

			if (!nextFade.HasValue || fadeAt < nextFade.Value)
			{
				nextFade = fadeAt;
			}
		}

		_nextInlineValueFadeUtc = nextFade;
	}

	private void ScheduleVisibleDataRefreshAfterScroll(bool immediate = false)
	{
		if (_currentFunctionSource == null || _dataCodeBox == null || _functionCodePanel == null || !_functionCodePanel.Visible)
		{
			return;
		}

		_visibleDataRefreshTimer.Stop();
		if (immediate)
		{
			RefreshVisibleDataAfterScroll();
			return;
		}
		_visibleDataRefreshTimer.Start();
	}

	private void RefreshVisibleDataAfterScroll()
	{
		if (_currentFunctionSource == null || _dataCodeBox == null)
		{
			return;
		}

		(int Start, int End) visibleRange = GetVisibleSourceLineRange(DataMirrorPaddingLines);
		bool addedWatch = AutoWatchVariablesForVisibleRange(visibleRange);
		CapturePollPriorityForVisibleRange(visibleRange);
		UpdateVisibleValuesLabel(visibleRange);
		string rangeSignature = BuildVisibleRangeSignature(visibleRange);
		string conditionSignature = BuildVisibleConditionSignature(visibleRange);
		bool rangeChanged = !rangeSignature.Equals(_lastVisibleRangeSignature, StringComparison.Ordinal);
		bool conditionChanged = !conditionSignature.Equals(_lastVisibleConditionSignature, StringComparison.Ordinal);
		_lastVisibleRangeSignature = rangeSignature;
		_lastVisibleConditionSignature = conditionSignature;
		if (ShouldShowInlineCodeValues())
		{
			_functionCodeDirty = true;
			_dataCodeDirty = ReferenceEquals(_functionCodeBox, _dataCodeBox);
		}
		else
		{
			RefreshScintillaVisibleRuntimeValues(force: true);
		}
		if (rangeChanged || conditionChanged || addedWatch)
		{
			UpdateProgramInsightPanel();
		}
	}

	private (int Start, int End) GetVisibleSourceLineRange(int padding = 1)
	{
		FunctionSourceView? source = GetCodeViewSource();
		if (source == null)
		{
			return (0, 0);
		}

		int firstLine = 0;
		int lastLine = Math.Min(source.Lines.Count - 1, 60);
		if (_codeEditor != null && !_codeEditor.IsDisposed && _codeEditor.IsHandleCreated && _codeEditor.TextLength > 0)
		{
			if (TryGetScintillaVisibleDocumentLineRange(out int firstDocumentLine, out int lastDocumentLine))
			{
				firstLine = Math.Clamp(firstDocumentLine, 0, Math.Max(0, source.Lines.Count - 1));
				lastLine = Math.Clamp(lastDocumentLine, firstLine, Math.Max(0, source.Lines.Count - 1));
			}
			else
			{
				firstLine = Math.Clamp(_codeEditor.FirstVisibleLine, 0, Math.Max(0, source.Lines.Count - 1));
				lastLine = Math.Clamp(firstLine + Math.Max(1, _codeEditor.LinesOnScreen), firstLine, Math.Max(0, source.Lines.Count - 1));
			}
		}
		else if (_functionCodeBox != null && _functionCodeBox.IsHandleCreated && _functionCodeBox.TextLength > 0)
		{
			firstLine = Math.Max(0, SendMessage(_functionCodeBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32());
			int bottomChar = _functionCodeBox.GetCharIndexFromPosition(new Point(Math.Max(1, _functionCodeBox.ClientSize.Width - 6), Math.Max(1, _functionCodeBox.ClientSize.Height - 6)));
			lastLine = Math.Max(firstLine, _functionCodeBox.GetLineFromCharIndex(bottomChar));
		}

		firstLine = Math.Clamp(firstLine - padding, 0, Math.Max(0, source.Lines.Count - 1));
		lastLine = Math.Clamp(lastLine + padding, firstLine, Math.Max(0, source.Lines.Count - 1));
		return (source.StartLine + firstLine, source.StartLine + lastLine);
	}

	private bool TryGetScintillaVisibleDocumentLineRange(out int firstLine, out int lastLine)
	{
		firstLine = 0;
		lastLine = 0;
		if (_codeEditor == null || _codeEditor.IsDisposed || !_codeEditor.IsHandleCreated || _codeEditor.TextLength == 0 || _codeEditor.Lines.Count == 0)
		{
			return false;
		}

		int lineCount = _codeEditor.Lines.Count;
		int textX = Math.Max(1, _codeEditor.Margins[0].Width + Ui(4));
		int topPos = _codeEditor.CharPositionFromPointClose(textX, 0);
		if (topPos < 0)
		{
			topPos = _codeEditor.CharPositionFromPoint(textX, 0);
		}

		if (topPos >= 0)
		{
			firstLine = Math.Clamp(_codeEditor.LineFromPosition(topPos), 0, lineCount - 1);
		}
		else
		{
			firstLine = Math.Clamp(_codeEditor.FirstVisibleLine, 0, lineCount - 1);
		}

		int bottomY = Math.Max(0, _codeEditor.ClientSize.Height - Ui(2));
		int bottomPos = _codeEditor.CharPositionFromPointClose(textX, bottomY);
		if (bottomPos < 0)
		{
			bottomPos = _codeEditor.CharPositionFromPoint(textX, bottomY);
		}

		if (bottomPos >= 0)
		{
			lastLine = Math.Clamp(_codeEditor.LineFromPosition(bottomPos), firstLine, lineCount - 1);
		}
		else
		{
			lastLine = Math.Clamp(firstLine + Math.Max(1, _codeEditor.LinesOnScreen), firstLine, lineCount - 1);
		}

		return true;
	}

	private IEnumerable<string> GetVisibleRawLines(int padding = 1)
	{
		if (GetCodeViewSource() == null)
		{
			yield break;
		}

		foreach (string line in GetRawLinesInSourceRange(GetVisibleSourceLineRange(padding)))
		{
			yield return line;
		}
	}

	private IEnumerable<string> GetRawLinesInSourceRange((int Start, int End) range)
	{
		FunctionSourceView? source = GetCodeViewSource();
		if (source == null)
		{
			yield break;
		}

		int first = Math.Max(0, range.Start - source.StartLine);
		int last = Math.Min(source.Lines.Count - 1, range.End - source.StartLine);
		for (int i = first; i <= last; i++)
		{
			yield return source.Lines[i];
		}
	}

	private List<WatchItem> GetVisibleWatchItems()
	{
		return GetVisibleWatchItems(GetVisibleSourceLineRange(DataMirrorPaddingLines));
	}

	private List<WatchItem> GetVisibleWatchItems((int Start, int End) range)
	{
		List<string> lines = GetRawLinesInSourceRange(range).ToList();
		if (lines.Count == 0)
		{
			return new List<WatchItem>();
		}

		return _watchItems
			.Where(item => item.Enabled && lines.Any(line =>
				LineMentionsWatch(line, item.Name) ||
				(item.IsChild && item.ParentName.Length > 0 && LineMentionsWatch(line, item.ParentName))))
			.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private bool AutoWatchVariablesForVisibleRange()
	{
		return AutoWatchVariablesForVisibleRange(GetVisibleSourceLineRange(DataMirrorPaddingLines));
	}

	private bool AutoWatchVariablesForVisibleRange((int Start, int End) range)
	{
		if (_symbolLookup.Count == 0)
		{
			return false;
		}

		List<string> lines = GetRawLinesInSourceRange(range).ToList();
		List<string> identifiers = BuildIdentifierList(lines)
			.Where(identifier => identifier.Length > 0 && !IsCKeywordToken(identifier))
			.ToList();
		if (identifiers.Count == 0)
		{
			return false;
		}

		var visibleIdentifiers = identifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
		int removed = 0;
		var existing = BuildExistingWatchAliasSet();
		int added = 0;
		foreach (string identifier in identifiers)
		{
			if (existing.Contains(identifier))
			{
				continue;
			}

			if (!TryResolveSourceIdentifierToWatchItem(identifier, existing, out WatchItem item, out string matchedName))
			{
				continue;
			}

			int removedNow = 0;
			if (IsWatchCapacityLimited() && !EnsureWatchCapacityForVisibleRange(visibleIdentifiers, lines, out removedNow))
			{
				break;
			}
			if (removedNow > 0)
			{
				removed += removedNow;
				existing = BuildExistingWatchAliasSet();
				if (existing.Contains(identifier))
				{
					continue;
				}
			}
			item.AutoVisible = true;
			_watchItems.Add(item);
			AddWatchAliases(existing, item.Name);
			existing.Add(matchedName);
			added++;
			if (IsWatchCapacityLimited() && GetVisibleWatchItems(range).Count >= VisibleWatchTargetLimit)
			{
				break;
			}
		}

		if (added > 0 || removed > 0)
		{
			UpdateCycleEstimate();
			_lastVisibleValuesText = "";
			_lastDataCodeText = "";
		}
		return added > 0 || removed > 0;
	}

	private int PruneWatchItemsToVisibleRange(HashSet<string> visibleIdentifiers, IReadOnlyList<string> visibleLines)
	{
		if (visibleIdentifiers.Count == 0 || visibleLines.Count == 0 || _watchItems.Count == 0)
		{
			return 0;
		}

		int removed = 0;
		int i = _watchItems.Count - 1;
		while (i >= 0)
		{
			WatchItem item = _watchItems[i];
			if (IsWatchVisibleInRange(item, visibleIdentifiers, visibleLines))
			{
				i--;
				continue;
			}

			int before = _watchItems.Count;
			RemoveWatchItem(item);
			removed += Math.Max(1, before - _watchItems.Count);
			i = Math.Min(i - 1, _watchItems.Count - 1);
		}
		return removed;
	}

	private bool EnsureWatchCapacityForCurrentContext(out int removed)
	{
		removed = 0;
		if (!IsWatchCapacityLimited())
		{
			return true;
		}
		if (_watchItems.Count < MaxWatchItems)
		{
			return true;
		}

		List<string> lines = _currentFunctionSource == null
			? new List<string>()
			: GetRawLinesInSourceRange(GetVisibleSourceLineRange(DataMirrorPaddingLines)).ToList();
		HashSet<string> identifiers = BuildIdentifierSet(lines);
		return EnsureWatchCapacityForVisibleRange(identifiers, lines, out removed);
	}

	private bool EnsureWatchCapacityForVisibleRange(HashSet<string> visibleIdentifiers, IReadOnlyList<string> visibleLines, out int removed)
	{
		removed = 0;
		if (!IsWatchCapacityLimited())
		{
			return true;
		}
		if (_watchItems.Count < MaxWatchItems)
		{
			return true;
		}

		removed += RemoveInvisibleWatchItems(visibleIdentifiers, visibleLines, item => item.AutoVisible);
		if (_watchItems.Count < MaxWatchItems)
		{
			return true;
		}

		removed += RemoveInvisibleWatchItems(visibleIdentifiers, visibleLines, item => !item.AutoVisible);
		return _watchItems.Count < MaxWatchItems;
	}

	private int RemoveInvisibleWatchItems(HashSet<string> visibleIdentifiers, IReadOnlyList<string> visibleLines, Predicate<WatchItem> isCandidate)
	{
		int removed = 0;
		for (int i = _watchItems.Count - 1; i >= 0 && _watchItems.Count >= MaxWatchItems; i--)
		{
			WatchItem item = _watchItems[i];
			if (!isCandidate(item) ||
				IsWatchVisibleInRange(item, visibleIdentifiers, visibleLines) ||
				CurrentFunctionMentionsWatch(item))
			{
				continue;
			}

			int before = _watchItems.Count;
			RemoveWatchItem(item);
			removed += Math.Max(1, before - _watchItems.Count);
		}
		return removed;
	}

	private static bool IsWatchVisibleInRange(WatchItem item, HashSet<string> visibleIdentifiers, IReadOnlyList<string> visibleLines)
	{
		if (IdentifierSetContainsWatch(visibleIdentifiers, item.Name))
		{
			return true;
		}
		if (item.IsChild && item.ParentName.Length > 0 && IdentifierSetContainsWatch(visibleIdentifiers, item.ParentName))
		{
			return true;
		}

		foreach (string line in visibleLines)
		{
			if (LineMentionsWatch(line, item.Name) ||
				(item.IsChild && item.ParentName.Length > 0 && LineMentionsWatch(line, item.ParentName)))
			{
				return true;
			}
		}
		return false;
	}

	private bool TryFindSymbolForSourceIdentifier(string identifier, out MapSymbol symbol)
	{
		if (_symbolLookup.TryGetValue(identifier, out MapSymbol? exact))
		{
			symbol = exact;
			return true;
		}

		if (_symbolBaseLookup.TryGetValue(identifier, out MapSymbol? byBase))
		{
			symbol = byBase;
			return true;
		}

		if (_symbolTailLookup.TryGetValue(identifier, out MapSymbol? byTail))
		{
			symbol = byTail;
			return true;
		}

		if (identifier.Length >= 4)
		{
			List<MapSymbol> strongMatches = FuzzyMatcher.Search(_symbols, identifier, limit: 8)
				.Where(candidate => IsStrongSourceSymbolMatch(identifier, candidate.Name))
				.Take(2)
				.ToList();
			if (strongMatches.Count == 1)
			{
				symbol = strongMatches[0];
				return true;
			}
		}

		symbol = null!;
		return false;
	}

	private bool TryResolveSourceIdentifierToWatchItem(string identifier, HashSet<string> existingAliases, out WatchItem item, out string matchedName)
	{
		item = null!;
		matchedName = "";
		if (identifier.Length == 0 || existingAliases.Contains(identifier))
		{
			return false;
		}

		if (!TryFindSymbolForSourceIdentifier(identifier, out MapSymbol symbol))
		{
			return false;
		}

		if (!KeilMapParser.TryResolve(symbol.Name, _symbolLookup, out WatchItem resolvedItem, out _))
		{
			return false;
		}

		if (_watchItems.Any(existing => existing.Name.Equals(resolvedItem.Name, StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}

		item = resolvedItem;
		matchedName = symbol.Name;
		return true;
	}

	private static bool IsStrongSourceSymbolMatch(string identifier, string symbolName)
	{
		if (identifier.Length == 0 || symbolName.Length == 0)
		{
			return false;
		}

		if (symbolName.Equals(identifier, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		string baseName = GetIdentifierBase(symbolName);
		if (baseName.Equals(identifier, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		string tailName = GetIdentifierTail(symbolName);
		return tailName.Equals(identifier, StringComparison.OrdinalIgnoreCase);
	}

	private HashSet<string> BuildExistingWatchAliasSet()
	{
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (WatchItem item in _watchItems)
		{
			AddWatchAliases(aliases, item.Name);
		}
		return aliases;
	}

	private static void AddWatchAliases(HashSet<string> aliases, string watchName)
	{
		foreach (string alias in WatchIdentifierAliases(watchName))
		{
			aliases.Add(alias);
		}
	}

	private void UpdateVisibleValuesLabel()
	{
		UpdateVisibleValuesLabel(GetVisibleSourceLineRange(DataMirrorPaddingLines));
	}

	private void UpdateVisibleValuesLabel((int Start, int End) range)
	{
		List<string> parts = GetVisibleWatchItems(range)
			.Select(item => GetWatchDisplayName(item) + "=" + FormatInlineWatchValue(item))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(32)
			.ToList();
		string text = parts.Count == 0 ? "可见变量：无" : "可见变量：" + string.Join("    ", parts);
		if (text.Equals(_lastVisibleValuesText, StringComparison.Ordinal))
		{
			return;
		}

		_lastVisibleValuesText = text;
		UpdateProgramInsightPanel();
	}

	private string BuildVisibleConditionSignature()
	{
		return BuildVisibleConditionSignature(GetVisibleSourceLineRange(DataMirrorPaddingLines));
	}

	private string BuildVisibleConditionSignature((int Start, int End) range)
	{
		List<WatchItem> candidates = GetVisibleWatchItems(range);
		if (candidates.Count == 0 && _watchItems.Count == 0)
		{
			return "";
		}

		StringBuilder builder = new StringBuilder();
		int lineNumber = range.Start;
		foreach (string line in GetRawLinesInSourceRange(range))
		{
			if (Regex.IsMatch(line, @"^\s*if\s*\("))
			{
				ConditionEval result = EvaluateIfCondition(line, candidates);
				builder.Append(lineNumber).Append(':').Append(result == ConditionEval.True ? '1' : result == ConditionEval.False ? '0' : '?').Append(';');
			}
			lineNumber++;
		}
		return builder.ToString();
	}

	private static string BuildVisibleRangeSignature((int Start, int End) range)
	{
		return range.Start.ToString() + ":" + range.End.ToString();
	}

	private List<WatchItem> GetCurrentFunctionWatchItems()
	{
		if (_currentFunctionIdentifiers.Count == 0)
		{
			return new List<WatchItem>();
		}

		return _watchItems
			.Where(item => item.Enabled && CurrentFunctionMentionsWatch(item))
			.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private void CapturePollPriorityForVisibleRange((int Start, int End) range)
	{
		if (_currentFunctionSource == null)
		{
			ClearPollPriority();
			return;
		}

		List<string> visibleNames = GetVisibleWatchItems(range)
			.Select(item => item.Name)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(VisibleWatchTargetLimit)
			.ToList();
		List<string> contextNames = GetCurrentFunctionWatchItems()
			.Select(item => item.Name)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(CurrentFunctionWatchTargetLimit)
			.ToList();
		lock (_pollPriorityLock)
		{
			_visiblePollPriorityNames = visibleNames;
			_contextPollPriorityNames = contextNames;
		}
		UpdateCycleEstimate();
	}

	private void ClearPollPriority()
	{
		lock (_pollPriorityLock)
		{
			_visiblePollPriorityNames = new List<string>();
			_contextPollPriorityNames = new List<string>();
		}
		UpdateCycleEstimate();
	}

	private void RenderFunctionSourceToBox(
		RichTextBox targetBox,
		bool includeValues,
		ref string lastText,
		bool resetScroll,
		bool applySearchHighlight,
		(int Start, int End)? lineRange = null)
	{
		if (_currentFunctionSource == null || targetBox == null)
		{
			return;
		}

		if (_codeEditor != null && ReferenceEquals(targetBox, _functionCodeBox))
		{
			RenderFunctionSourceToScintilla(includeValues, ref lastText, resetScroll, lineRange);
			return;
		}

		FunctionSourceView? renderSource = GetCodeViewSource();
		if (renderSource == null)
		{
			return;
		}

		List<CodeLineRender> renderedLines = BuildFunctionRenderLines(renderSource, includeValues, out string nextText, lineRange);
		if (nextText.Equals(lastText, StringComparison.Ordinal))
		{
			if (resetScroll)
			{
				ResetCodeBoxScroll(targetBox);
			}
			return;
		}

		lastText = nextText;
		int oldSelection = resetScroll ? 0 : targetBox.SelectionStart;
		Point scrollPosition = Point.Empty;
		bool hasHandle = targetBox.IsHandleCreated;
		int firstVisibleLine = 0;
		if (hasHandle)
		{
			if (!resetScroll)
			{
				SendMessage(targetBox.Handle, EmGetScrollPos, IntPtr.Zero, ref scrollPosition);
				firstVisibleLine = GetFirstVisibleLineSafe(targetBox);
				if (includeValues)
				{
					scrollPosition.X = 0;
				}
			}
			SendMessage(targetBox.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
		}
		bool keepRedrawSuppressed = _batchFunctionNavigationRedraw && ReferenceEquals(targetBox, _functionCodeBox);
		targetBox.SuspendLayout();
		bool oldSuppressScopeHighlight = _suppressFunctionScopeHighlight;
		_suppressFunctionScopeHighlight = true;
		try
		{
			targetBox.Rtf = BuildFunctionSourceRtf(renderedLines, targetBox);
			if (targetBox.TextLength > 0)
			{
				targetBox.SelectionStart = Math.Min(oldSelection, targetBox.TextLength);
				targetBox.SelectionLength = 0;
			}
			if (hasHandle && !resetScroll)
			{
				RestoreCodeBoxViewport(targetBox, scrollPosition, firstVisibleLine);
			}
			if (resetScroll)
			{
				targetBox.SelectionStart = 0;
				targetBox.SelectionLength = 0;
				targetBox.ScrollToCaret();
			}
			if (includeValues)
			{
				ForceCodeBoxLeftAligned(targetBox);
			}
			// Search and focused-variable highlights are emitted directly into RTF.
			// Calling RichTextBox.Select() during background value refresh can pull the
			// viewport back to an old caret location, which feels like the code window is
			// scrolling by itself.
		}
		finally
		{
			_suppressFunctionScopeHighlight = oldSuppressScopeHighlight;
			if (hasHandle)
			{
				if (!keepRedrawSuppressed)
				{
					SendMessage(targetBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
					if (!resetScroll)
					{
						RestoreCodeBoxViewport(targetBox, scrollPosition, firstVisibleLine);
						RestoreCodeBoxViewportLater(targetBox, scrollPosition, firstVisibleLine);
					}
				}
			}
			targetBox.ResumeLayout();
			if (!keepRedrawSuppressed)
			{
				targetBox.Invalidate();
			}
			if (includeValues && ReferenceEquals(targetBox, _functionCodeBox))
			{
				HideCodeValueOverlay();
			}
		}
	}

	private void RenderFunctionSourceToScintilla(
		bool includeValues,
		ref string lastText,
		bool resetScroll,
		(int Start, int End)? lineRange = null)
	{
		if (_currentFunctionSource == null || _codeEditor == null || _codeEditor.IsDisposed)
		{
			return;
		}

		FunctionSourceView? renderSource = GetCodeViewSource();
		if (renderSource == null)
		{
			return;
		}

		List<CodeLineRender> renderedLines = BuildFunctionRenderLines(
			renderSource,
			includeValues,
			out string nextText,
			lineRange,
			inlineValuesInText: includeValues,
			includeLineNumbersInText: false);
		int firstVisibleLine = _codeEditor.FirstVisibleLine;
		int currentPosition = Math.Clamp(_codeEditor.CurrentPosition, 0, Math.Max(0, _codeEditor.TextLength));
		int selectionStart = Math.Clamp(_codeEditor.SelectionStart, 0, Math.Max(0, _codeEditor.TextLength));
		int selectionEnd = Math.Clamp(_codeEditor.SelectionEnd, 0, Math.Max(0, _codeEditor.TextLength));
		bool preserveUserSelection = !resetScroll &&
			_codeEditor.Focused &&
			selectionStart != selectionEnd &&
			!AreCodeInteractionSideEffectsSuppressed() &&
			!IsCodeViewportProtected();
		if (!nextText.Equals(lastText, StringComparison.Ordinal))
		{
			lastText = nextText;
			bool wasReadOnly = _codeEditor.ReadOnly;
			_codeEditor.ReadOnly = false;
			try
			{
				_codeEditor.Text = nextText;
				_functionCodeBox.Text = nextText;
				_codeEditor.Margins.ClearAllText();
				_lastCodeValueOverlaySignature = "";
				_codeEditor.EmptyUndoBuffer();
				_codeEditor.SetSavePoint();
			}
			finally
			{
				_codeEditor.ReadOnly = wasReadOnly;
			}
		}
		else if (!_functionCodeBox.Text.Equals(nextText, StringComparison.Ordinal))
		{
			_functionCodeBox.Text = nextText;
		}

		if (includeValues)
		{
			ApplyScintillaRuntimeHighlights(renderedLines, renderSource);
			HideCodeValueOverlay();
		}
		else if (_codeValueOverlay != null)
		{
			ApplyScintillaRuntimeHighlights(renderedLines, renderSource);
			ClearScintillaValueDecorations();
			HideCodeValueOverlay();
		}
		RefreshScintillaVisibleConditionHighlights(force: true);
		if (resetScroll)
		{
			CollapseScintillaSelection(0, "render-reset-scroll");
			_codeEditor.FirstVisibleLine = 0;
			_codeEditor.XOffset = 0;
		}
		else if (preserveUserSelection && _codeEditor.TextLength > 0)
		{
			int safeStart = Math.Clamp(selectionStart, 0, _codeEditor.TextLength);
			int safeEnd = Math.Clamp(selectionEnd, 0, _codeEditor.TextLength);
			_codeEditor.SetSelection(safeEnd, safeStart);
			_codeEditor.FirstVisibleLine = Math.Clamp(firstVisibleLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
		}
		else if (_codeEditor.TextLength > 0)
		{
			int restoredPosition = Math.Clamp(currentPosition, 0, _codeEditor.TextLength);
			CollapseScintillaSelection(restoredPosition, "render-restore-position");
			_codeEditor.FirstVisibleLine = Math.Clamp(firstVisibleLine, 0, Math.Max(0, _codeEditor.Lines.Count - 1));
		}
		if (!preserveUserSelection)
		{
			CollapseScintillaSelection(_codeEditor.CurrentPosition, "render-final");
		}
		_lastScintillaScopeCaret = -1;
		UpdateScintillaScopeHighlight(force: true);

		ForceCodeBoxLeftAligned(targetBox: _functionCodeBox);
	}

	private void ApplyScintillaRuntimeHighlights(IReadOnlyList<CodeLineRender> renderedLines, FunctionSourceView renderSource)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || _codeEditor.TextLength == 0)
		{
			return;
		}

		_codeEditor.MarkerDeleteAll(ScintillaMarkerTrueLine);
		_codeEditor.MarkerDeleteAll(ScintillaMarkerSearchLine);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorFocus;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorSearch;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormal;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFresh;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormalBorder;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFreshBorder;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormalText;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFreshText;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorForceHold;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorForceHoldText;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorTrueCondition;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);

		for (int i = 0; i < renderedLines.Count && i < _codeEditor.Lines.Count; i++)
		{
			CodeLineRender renderedLine = renderedLines[i];
			if (renderedLine.IsTrueCondition)
			{
				ApplyScintillaTrueConditionIndicatorToLine(i);
			}
			if (renderedLine.LineNumber == _activeProgramSearchLine)
			{
				_codeEditor.Lines[i].MarkerAdd(ScintillaMarkerSearchLine);
			}

			ApplyScintillaInlineValueSpans(i, renderedLine);
			HighlightScintillaTokenInLine(i, _focusedVariableName, ScintillaIndicatorFocus);
			HighlightScintillaTokenInLine(i, _activeProgramSearchKeyword, ScintillaIndicatorSearch);
		}
	}

	private void ApplyScintillaInlineValueSpans(int lineIndex, CodeLineRender renderedLine)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			renderedLine.ValueSpans.Count == 0 ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return;
		}

		foreach (InlineValueSpan span in renderedLine.ValueSpans)
		{
			if (span.Start < 0 || span.Length <= 0 || span.Start >= renderedLine.Code.Length)
			{
				continue;
			}

			int safeLength = Math.Min(span.Length, renderedLine.Code.Length - span.Start);
			if (safeLength <= 0)
			{
				continue;
			}

			int start = GetScintillaLinePositionFromCharIndex(lineIndex, renderedLine.Code, span.Start);
			int length = Encoding.UTF8.GetByteCount(renderedLine.Code.Substring(span.Start, safeLength));
			if (length <= 0)
			{
				continue;
			}

			_codeEditor.IndicatorCurrent = span.Fresh ? ScintillaIndicatorValueFresh : ScintillaIndicatorValueNormal;
			_codeEditor.IndicatorFillRange(start, Math.Min(length, Math.Max(0, _codeEditor.TextLength - start)));
			_codeEditor.IndicatorCurrent = span.Fresh ? ScintillaIndicatorValueFreshBorder : ScintillaIndicatorValueNormalBorder;
			_codeEditor.IndicatorFillRange(start, Math.Min(length, Math.Max(0, _codeEditor.TextLength - start)));
			_codeEditor.IndicatorCurrent = span.Fresh ? ScintillaIndicatorValueFreshText : ScintillaIndicatorValueNormalText;
			_codeEditor.IndicatorFillRange(start, Math.Min(length, Math.Max(0, _codeEditor.TextLength - start)));
			ApplyScintillaInlineValueTextStyle(start, length, span.Fresh);
		}
	}

	private void ApplyScintillaInlineValueTextStyle(int start, int length, bool fresh)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			start < 0 || length <= 0 || start >= _codeEditor.TextLength)
		{
			return;
		}

		int safeLength = Math.Min(length, _codeEditor.TextLength - start);
		if (safeLength <= 0)
		{
			return;
		}

		try
		{
			_codeEditor.StartStyling(start);
			_codeEditor.SetStyling(safeLength, fresh ? ScintillaStyleValueFresh : ScintillaStyleValueStale);
		}
		catch (Exception ex)
		{
			DateTime now = DateTime.UtcNow;
			if ((now - _lastInlineValueStyleLogUtc).TotalSeconds >= 30)
			{
				_lastInlineValueStyleLogUtc = now;
				Log("数值样式刷新失败：" + ex.Message);
			}
		}
	}

	private void ClearScintillaValueDecorations()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || _codeEditor.TextLength == 0)
		{
			return;
		}

		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormal;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFresh;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormalBorder;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFreshBorder;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormalText;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFreshText;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorForceHold;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorForceHoldText;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
		ClearScintillaValueAnnotations();
	}

	private void RefreshScintillaVisibleRuntimeValues(bool force = false)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || _codeEditor.TextLength == 0)
		{
			return;
		}

		HideCodeValueOverlay();
		if (!ShouldShowInlineCodeValues())
		{
			ClearScintillaValueDecorations();
			_nextInlineValueFadeUtc = null;
			RefreshScintillaVisibleConditionHighlights(force: true);
			return;
		}

		FunctionSourceView? source = GetCodeViewSource();
		if (source == null || source.Lines.Count == 0)
		{
			return;
		}

		if (force)
		{
			_lastFunctionCodeText = "";
			if (!ReferenceEquals(_functionCodeBox, _dataCodeBox))
			{
				_lastDataCodeText = "";
			}
		}
		_functionCodeDirty = true;
		_dataCodeDirty = ReferenceEquals(_functionCodeBox, _dataCodeBox);
		RefreshScintillaVisibleConditionHighlights(force: true);
		_nextInlineValueFadeUtc = null;
	}

	private void RefreshScintillaVisibleConditionHighlights(bool force = false)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || _codeEditor.TextLength == 0)
		{
			return;
		}

		FunctionSourceView? source = GetCodeViewSource();
		if (source == null || source.Lines.Count == 0)
		{
			ClearScintillaConditionHighlights();
			_lastVisibleConditionSignature = "";
			return;
		}

		(int Start, int End) visibleRange = GetVisibleSourceLineRange(DataMirrorPaddingLines);
		string signature = BuildVisibleConditionSignature(visibleRange);
		if (!force && signature.Equals(_lastVisibleConditionSignature, StringComparison.Ordinal))
		{
			return;
		}
		_lastVisibleConditionSignature = signature;

		ClearScintillaConditionHighlights();
		if (signature.Length == 0)
		{
			return;
		}

		List<WatchItem> candidates = GetInlineWatchCandidates(visibleRange);
		int first = Math.Max(visibleRange.Start, source.StartLine);
		int last = Math.Min(visibleRange.End, GetSourceEndLine(source));
		for (int absoluteLine = first; absoluteLine <= last; absoluteLine++)
		{
			int sourceIndex = absoluteLine - source.StartLine;
			if (sourceIndex < 0 || sourceIndex >= source.Lines.Count || sourceIndex >= _codeEditor.Lines.Count)
			{
				continue;
			}

			string rawLine = source.Lines[sourceIndex];
			if (EvaluateIfCondition(rawLine, candidates) == ConditionEval.True)
			{
				ApplyScintillaTrueConditionIndicatorToLine(sourceIndex);
			}
		}
	}

	private void ClearScintillaConditionHighlights()
	{
		if (_codeEditor == null || _codeEditor.IsDisposed || _codeEditor.TextLength == 0)
		{
			return;
		}

		_codeEditor.MarkerDeleteAll(ScintillaMarkerTrueLine);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorTrueCondition;
		_codeEditor.IndicatorClearRange(0, _codeEditor.TextLength);
	}

	private void ApplyScintillaTrueConditionIndicatorToLine(int lineIndex)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return;
		}

		ScintillaNET.Line line = _codeEditor.Lines[lineIndex];
		if (!TryGetIfConditionDisplaySpan(line.Text, out int startInLine, out int length) || length <= 0)
		{
			return;
		}

		_codeEditor.IndicatorCurrent = ScintillaIndicatorTrueCondition;
		_codeEditor.IndicatorFillRange(line.Position + startInLine, length);
	}

	private static bool TryGetIfConditionDisplaySpan(string line, out int start, out int length)
	{
		start = 0;
		length = 0;
		Match match = Regex.Match(line, @"\bif\s*\(");
		if (!match.Success)
		{
			return false;
		}

		int open = line.IndexOf('(', match.Index + match.Length - 1);
		if (open < 0)
		{
			return false;
		}

		int depth = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = open; i < line.Length; i++)
		{
			char c = line[i];
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
					start = match.Index;
					length = i - match.Index + 1;
					return true;
				}
			}
		}

		return false;
	}

	private void ApplyScintillaValueColorToLine(int lineIndex, bool fresh)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return;
		}

		ScintillaNET.Line line = _codeEditor.Lines[lineIndex];
		string text = line.Text;
		const string marker = "//值:";
		const string legacyMarker = "// 值:";
		int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
		if (markerIndex < 0)
		{
			markerIndex = text.IndexOf(legacyMarker, StringComparison.Ordinal);
		}
		if (markerIndex < 0)
		{
			return;
		}

		int startInLine = markerIndex;

		int endInLine = text.Length;
		while (endInLine > startInLine && (text[endInLine - 1] == '\r' || text[endInLine - 1] == '\n'))
		{
			endInLine--;
		}

		int length = endInLine - startInLine;
		if (length <= 0)
		{
			return;
		}

		int start = line.Position + startInLine;
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormal;
		_codeEditor.IndicatorClearRange(start, length);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFresh;
		_codeEditor.IndicatorClearRange(start, length);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormalBorder;
		_codeEditor.IndicatorClearRange(start, length);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFreshBorder;
		_codeEditor.IndicatorClearRange(start, length);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueNormalText;
		_codeEditor.IndicatorClearRange(start, length);
		_codeEditor.IndicatorCurrent = ScintillaIndicatorValueFreshText;
		_codeEditor.IndicatorClearRange(start, length);
		_codeEditor.IndicatorCurrent = fresh ? ScintillaIndicatorValueFresh : ScintillaIndicatorValueNormal;
		_codeEditor.IndicatorFillRange(start, length);
		_codeEditor.IndicatorCurrent = fresh ? ScintillaIndicatorValueFreshBorder : ScintillaIndicatorValueNormalBorder;
		_codeEditor.IndicatorFillRange(start, length);
		_codeEditor.IndicatorCurrent = fresh ? ScintillaIndicatorValueFreshText : ScintillaIndicatorValueNormalText;
		_codeEditor.IndicatorFillRange(start, length);
	}

	private void ApplyScintillaForceHoldIndicatorsToLine(int lineIndex, string rawLine, IReadOnlyList<WatchItem> candidates)
	{
		if (_codeEditor == null || _codeEditor.IsDisposed ||
			lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count ||
			candidates.Count == 0)
		{
			return;
		}

		ScintillaNET.Line line = _codeEditor.Lines[lineIndex];
		string text = line.Text;
		int markerIndex = text.IndexOf("//值:", StringComparison.Ordinal);
		if (markerIndex < 0)
		{
			markerIndex = text.IndexOf("// 值:", StringComparison.Ordinal);
		}
		if (markerIndex < 0)
		{
			return;
		}

		foreach (WatchItem item in candidates.Where(item => item.ForceActive))
		{
			string token = FindWatchTokenInLine(rawLine, item.Name);
			if (token.Length == 0)
			{
				continue;
			}

			int valueStart = text.IndexOf(token + "=", markerIndex, StringComparison.OrdinalIgnoreCase);
			if (valueStart < 0)
			{
				continue;
			}

			int valueEnd = text.IndexOfAny(new[] { '，', '】', '\r', '\n' }, valueStart);
			if (valueEnd < 0)
			{
				valueEnd = text.Length;
			}
			int length = Math.Max(0, valueEnd - valueStart);
			if (length <= 0)
			{
				continue;
			}

			int start = line.Position + valueStart;
			_codeEditor.IndicatorCurrent = ScintillaIndicatorForceHold;
			_codeEditor.IndicatorFillRange(start, length);
			_codeEditor.IndicatorCurrent = ScintillaIndicatorForceHoldText;
			_codeEditor.IndicatorFillRange(start, length);
		}
	}

	private static string BuildCodeValueOverlayText(IReadOnlyList<string> values)
	{
		return BuildCompactCodeValueTag(values);
	}

	private void HighlightScintillaTokenInLine(int lineIndex, string token, int indicator)
	{
		if (_codeEditor == null || string.IsNullOrWhiteSpace(token) || lineIndex < 0 || lineIndex >= _codeEditor.Lines.Count)
		{
			return;
		}

		string text = _codeEditor.Lines[lineIndex].Text;
		int lineStart = _codeEditor.Lines[lineIndex].Position;
		int index = 0;
		while (index < text.Length)
		{
			int match = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
			if (match < 0)
			{
				return;
			}
			if (IsWholeIdentifierMatch(text, match, token.Length))
			{
				_codeEditor.IndicatorCurrent = indicator;
				_codeEditor.IndicatorFillRange(lineStart + match, token.Length);
			}
			index = match + Math.Max(1, token.Length);
		}
	}

	private List<CodeLineRender> BuildFunctionRenderLines(
		FunctionSourceView renderSource,
		bool includeValues,
		out string plainText,
		(int Start, int End)? lineRange = null,
		bool inlineValuesInText = true,
		bool includeLineNumbersInText = true)
	{
		List<CodeLineRender> renderedLines = new List<CodeLineRender>();
		StringBuilder plainBuilder = new StringBuilder();
		int displayIndent = 0;
		if (renderSource.Lines.Count == 0)
		{
			plainText = "";
			return renderedLines;
		}

		int firstIndex = 0;
		int lastIndex = renderSource.Lines.Count - 1;
		if (lineRange.HasValue)
		{
			firstIndex = Math.Clamp(lineRange.Value.Start - renderSource.StartLine, 0, Math.Max(0, renderSource.Lines.Count - 1));
			lastIndex = Math.Clamp(lineRange.Value.End - renderSource.StartLine, firstIndex, Math.Max(0, renderSource.Lines.Count - 1));
			for (int i = 0; i < firstIndex; i++)
			{
				_ = FormatCodeLineForDisplay(renderSource.Lines[i], ref displayIndent);
			}
		}

		List<WatchItem> inlineCandidates = includeValues ? GetInlineWatchCandidates(lineRange) : new List<WatchItem>();
		for (int i = firstIndex; i <= lastIndex; i++)
		{
			int lineNumber = renderSource.StartLine + i;
			string rawLine = renderSource.Lines[i];
			string line = FormatCodeLineForDisplay(rawLine, ref displayIndent);
			List<InlineValueSpan> valueSpans = new List<InlineValueSpan>();
			if (includeValues && inlineValuesInText)
			{
				line = InsertInlineWatchValues(line, inlineCandidates, out valueSpans);
			}
			List<string> values = includeValues ? BuildInlineWatchValues(rawLine, inlineCandidates) : new List<string>();
			bool isTrueCondition = includeValues && EvaluateIfCondition(rawLine, inlineCandidates) == ConditionEval.True;
			renderedLines.Add(new CodeLineRender(lineNumber, line, values, isTrueCondition, valueSpans));
			if (includeLineNumbersInText)
			{
				plainBuilder.Append(lineNumber.ToString().PadLeft(5)).Append("  ");
			}
			plainBuilder.Append(line);
			plainBuilder.AppendLine();
		}

		plainText = plainBuilder.ToString();
		return renderedLines;
	}

	private void ResetCodeBoxScroll(RichTextBox targetBox)
	{
		if (targetBox == null || targetBox.TextLength == 0)
		{
			return;
		}
		targetBox.SelectionStart = 0;
		targetBox.SelectionLength = 0;
		targetBox.ScrollToCaret();
		ForceCodeBoxLeftAligned(targetBox);
	}

	private void SyncDataCodeScrollFromProgram()
	{
		// The merged code/value view must never move because a background mirror refresh ran.
	}

	private static void ForceCodeBoxLeftAligned(RichTextBox targetBox)
	{
		if (targetBox == null || !targetBox.IsHandleCreated)
		{
			return;
		}

		var scrollPosition = Point.Empty;
		SendMessage(targetBox.Handle, EmGetScrollPos, IntPtr.Zero, ref scrollPosition);
		scrollPosition.X = 0;
		SendMessage(targetBox.Handle, EmSetScrollPos, IntPtr.Zero, ref scrollPosition);
	}

	private static int GetFirstVisibleLineSafe(RichTextBox targetBox)
	{
		if (targetBox == null || !targetBox.IsHandleCreated || targetBox.TextLength == 0)
		{
			return 0;
		}

		return Math.Max(0, SendMessage(targetBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32());
	}

	private static void RestoreCodeBoxViewport(RichTextBox targetBox, Point scrollPosition, int firstVisibleLine)
	{
		if (targetBox == null || targetBox.IsDisposed || !targetBox.IsHandleCreated)
		{
			return;
		}

		SendMessage(targetBox.Handle, EmSetScrollPos, IntPtr.Zero, ref scrollPosition);
		int currentFirstLine = GetFirstVisibleLineSafe(targetBox);
		int delta = firstVisibleLine - currentFirstLine;
		if (delta != 0)
		{
			SendMessage(targetBox.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(delta));
			SendMessage(targetBox.Handle, EmSetScrollPos, IntPtr.Zero, ref scrollPosition);
		}
	}

	private void RestoreCodeBoxViewportLater(RichTextBox targetBox, Point scrollPosition, int firstVisibleLine)
	{
		if (targetBox == null || targetBox.IsDisposed || !targetBox.IsHandleCreated)
		{
			return;
		}

		RestoreCodeBoxViewportOnUiTurn(targetBox, scrollPosition, firstVisibleLine);
		RestoreCodeBoxViewportAfterDelay(targetBox, scrollPosition, firstVisibleLine, 40);
		RestoreCodeBoxViewportAfterDelay(targetBox, scrollPosition, firstVisibleLine, 140);
	}

	private void RestoreCodeBoxViewportOnUiTurn(RichTextBox targetBox, Point scrollPosition, int firstVisibleLine)
	{
		try
		{
			BeginInvoke((Action)(() =>
			{
				if (!targetBox.IsDisposed && targetBox.IsHandleCreated)
				{
					RestoreCodeBoxViewport(targetBox, scrollPosition, firstVisibleLine);
				}
			}));
		}
		catch
		{
		}
	}

	private void RestoreCodeBoxViewportAfterDelay(RichTextBox targetBox, Point scrollPosition, int firstVisibleLine, int delayMs)
	{
		_ = Task.Delay(delayMs).ContinueWith(_ =>
		{
			if (IsDisposed || targetBox.IsDisposed || !targetBox.IsHandleCreated)
			{
				return;
			}
			RestoreCodeBoxViewportOnUiTurn(targetBox, scrollPosition, firstVisibleLine);
		}, TaskScheduler.Default);
	}

	private string BuildFunctionSourceRtf(IEnumerable<CodeLineRender> renderedLines, RichTextBox targetBox)
	{
		var builder = new StringBuilder();
		builder.Append(@"{\rtf1\ansi\deff0");
		builder.Append(@"{\fonttbl{\f0 Consolas;}}");
		builder.Append(@"{\colortbl ;");
		AppendRtfColor(builder, _muted);
		AppendRtfColor(builder, _ink);
		AppendRtfColor(builder, _accent);
		AppendRtfColor(builder, _codeCommentColor);
		AppendRtfColor(builder, _codeFunctionColor);
		AppendRtfColor(builder, _codeKeywordColor);
		AppendRtfColor(builder, _codeValueTagInactiveForeColor);
		AppendRtfColor(builder, _surface);
		AppendRtfColor(builder, _codeTrueLineBackColor);
		AppendRtfColor(builder, _codeFocusVariableForeColor);
		AppendRtfColor(builder, _codeFocusVariableBackColor);
		AppendRtfColor(builder, _surface);
		AppendRtfColor(builder, _programSearchLineBackColor);
		AppendRtfColor(builder, _programSearchMatchBackColor);
		builder.Append('}');
		builder.Append(@"\viewkind4\uc1\pard\f0\fs");
		builder.Append(Math.Max(16, (int)Math.Round(targetBox.Font.SizeInPoints * 2)));
		builder.Append(' ');

		bool inBlockComment = false;
		foreach (CodeLineRender renderedLine in renderedLines)
		{
			int lineHighlightIndex = renderedLine.LineNumber == _activeProgramSearchLine
				? 13
				: renderedLine.IsTrueCondition ? 9 : 0;
			if (lineHighlightIndex > 0)
			{
				builder.Append(@"\highlight").Append(lineHighlightIndex).Append(' ');
			}
			AppendRtfText(builder, renderedLine.LineNumber.ToString().PadLeft(5) + "  ", 1);
			AppendHighlightedCodeRtf(builder, renderedLine.Code, ref inBlockComment, lineHighlightIndex);
			if (lineHighlightIndex > 0)
			{
				builder.Append(@"\highlight0 ");
			}
			builder.Append(@"\par ");
		}
		builder.Append('}');
		return builder.ToString();
	}

	private static void AppendRtfColor(StringBuilder builder, Color color)
	{
		builder.Append(@"\red").Append(color.R)
			.Append(@"\green").Append(color.G)
			.Append(@"\blue").Append(color.B)
			.Append(';');
	}

	private static void AppendRtfText(StringBuilder builder, string text, int colorIndex)
	{
		AppendRtfText(builder, text, colorIndex, 0, 0);
	}

	private static void AppendRtfText(StringBuilder builder, string text, int colorIndex, int highlightIndex)
	{
		AppendRtfText(builder, text, colorIndex, highlightIndex, 0);
	}

	private static void AppendRtfText(StringBuilder builder, string text, int colorIndex, int highlightIndex, int restoreHighlightIndex)
	{
		builder.Append(@"\cf").Append(colorIndex).Append(' ');
		if (highlightIndex > 0)
		{
			builder.Append(@"\highlight").Append(highlightIndex).Append(' ');
		}
		foreach (char c in text)
		{
			switch (c)
			{
				case '\\':
					builder.Append(@"\\");
					break;
				case '{':
					builder.Append(@"\{");
					break;
				case '}':
					builder.Append(@"\}");
					break;
				case '\t':
					builder.Append(@"\tab ");
					break;
				default:
					if (c <= 0x7f)
					{
						builder.Append(c);
					}
					else
					{
						builder.Append(@"\u").Append(unchecked((short)c)).Append('?');
				}
				break;
			}
		}
		if (highlightIndex > 0)
		{
			builder.Append(@"\highlight").Append(restoreHighlightIndex).Append(' ');
		}
	}

	private void AppendHighlightedCodeRtf(StringBuilder builder, string text, ref bool inBlockComment, int lineHighlightIndex = 0)
	{
		int index = 0;
		while (index < text.Length)
		{
			if (inBlockComment)
			{
				int end = text.IndexOf("*/", index, StringComparison.Ordinal);
				if (end < 0)
				{
					AppendRtfText(builder, text.Substring(index), 4);
					return;
				}

				AppendRtfText(builder, text.Substring(index, end - index + 2), 4);
				index = end + 2;
				inBlockComment = false;
				continue;
			}

			CommentStart comment = FindNextCommentStart(text, index);
			int codeEnd = comment.Index >= 0 ? comment.Index : text.Length;
			if (codeEnd > index)
			{
				AppendCodeTokensRtf(builder, text.Substring(index, codeEnd - index), lineHighlightIndex);
			}

			if (comment.Index < 0)
			{
				return;
			}

			if (comment.IsLineComment)
			{
				AppendRtfText(builder, text.Substring(comment.Index), 4);
				return;
			}

			int blockEnd = text.IndexOf("*/", comment.Index + 2, StringComparison.Ordinal);
			if (blockEnd < 0)
			{
				AppendRtfText(builder, text.Substring(comment.Index), 4);
				inBlockComment = true;
				return;
			}

			AppendRtfText(builder, text.Substring(comment.Index, blockEnd - comment.Index + 2), 4);
			index = blockEnd + 2;
		}
	}

	private void AppendCodeTokensRtf(StringBuilder builder, string text, int lineHighlightIndex = 0)
	{
		int index = 0;
		while (index < text.Length)
		{
			if (text[index] == '[')
			{
				int close = text.IndexOf(']', index + 1);
				if (close > index && close - index <= 32)
				{
					int valueHighlightIndex = GetInlineValueHighlightIndex(text, index, lineHighlightIndex);
					int valueColorIndex = valueHighlightIndex == 8 || valueHighlightIndex == 12 ? 7 : 2;
					AppendRtfText(builder, text.Substring(index, close - index + 1), valueColorIndex, valueHighlightIndex, lineHighlightIndex);
					index = close + 1;
					continue;
				}
			}

			if (!IsIdentifierStart(text[index]))
			{
				int start = index;
				index++;
				while (index < text.Length && !IsIdentifierStart(text[index]) && text[index] != '[')
				{
					index++;
				}
				AppendRtfText(builder, text.Substring(start, index - start), 2);
				continue;
			}

			int tokenStart = index;
			index++;
			while (index < text.Length && IsIdentifierChar(text[index]))
			{
				index++;
			}

			string token = text.Substring(tokenStart, index - tokenStart);
			int color = 2;
			if (IsFocusedVariableToken(token))
			{
				AppendRtfText(builder, token, 10, 11, lineHighlightIndex);
				continue;
			}
			if (IsActiveProgramSearchToken(token))
			{
				AppendRtfText(builder, token, 2, 14, lineHighlightIndex);
				continue;
			}
			if (IsCKeywordToken(token))
			{
				color = 6;
			}
			else if (LooksLikeFunctionCall(text, index))
			{
				color = 5;
			}
			AppendRtfText(builder, token, color);
		}
	}

	private int GetInlineValueHighlightIndex(string text, int bracketIndex, int lineHighlightIndex)
	{
		string token = FindPreviousIdentifierBefore(text, bracketIndex);
		if (token.Length == 0)
		{
			return lineHighlightIndex;
		}

		WatchItem? item = FindWatchItemBySourceToken(token);
		if (item == null)
		{
			return lineHighlightIndex;
		}

		if (IsWatchValueFresh(item, DateTime.Now))
		{
			return 8;
		}

		return 12;
	}

	private WatchItem? FindWatchItemBySourceToken(string token)
	{
		if (token.Length == 0)
		{
			return null;
		}

		foreach (WatchItem item in _watchItems)
		{
			if (!item.Enabled)
			{
				continue;
			}

			foreach (string alias in WatchIdentifierAliases(item.Name))
			{
				if (alias.Equals(token, StringComparison.OrdinalIgnoreCase))
				{
					return item;
				}
			}
		}

		return null;
	}

	private static string FindPreviousIdentifierBefore(string text, int index)
	{
		int i = Math.Min(index - 1, text.Length - 1);
		while (i >= 0 && char.IsWhiteSpace(text[i]))
		{
			i--;
		}

		int end = i;
		while (i >= 0 && IsIdentifierChar(text[i]))
		{
			i--;
		}

		if (end < i + 1)
		{
			return "";
		}

		string token = text.Substring(i + 1, end - i);
		return token.Length > 0 && IsIdentifierStart(token[0]) ? token : "";
	}

	private bool IsFocusedVariableToken(string token)
	{
		return !string.IsNullOrWhiteSpace(_focusedVariableName) &&
			token.Equals(_focusedVariableName, StringComparison.OrdinalIgnoreCase);
	}

	private bool IsActiveProgramSearchToken(string token)
	{
		return !string.IsNullOrWhiteSpace(_activeProgramSearchKeyword) &&
			token.Equals(_activeProgramSearchKeyword, StringComparison.OrdinalIgnoreCase);
	}

	private void AppendCodeText(string text, Color color)
	{
		_functionCodeBox.SelectionColor = color;
		_functionCodeBox.AppendText(text);
	}

	private void AppendHighlightedCodeText(string text, ref bool inBlockComment)
	{
		int index = 0;
		while (index < text.Length)
		{
			if (inBlockComment)
			{
				int end = text.IndexOf("*/", index, StringComparison.Ordinal);
				if (end < 0)
				{
					AppendCodeText(text.Substring(index), _codeCommentColor);
					return;
				}

				AppendCodeText(text.Substring(index, end - index + 2), _codeCommentColor);
				index = end + 2;
				inBlockComment = false;
				continue;
			}

			CommentStart comment = FindNextCommentStart(text, index);
			int codeEnd = comment.Index >= 0 ? comment.Index : text.Length;
			if (codeEnd > index)
			{
				AppendCodeTokens(text.Substring(index, codeEnd - index));
			}

			if (comment.Index < 0)
			{
				return;
			}

			if (comment.IsLineComment)
			{
				AppendCodeText(text.Substring(comment.Index), _codeCommentColor);
				return;
			}

			int blockEnd = text.IndexOf("*/", comment.Index + 2, StringComparison.Ordinal);
			if (blockEnd < 0)
			{
				AppendCodeText(text.Substring(comment.Index), _codeCommentColor);
				inBlockComment = true;
				return;
			}

			AppendCodeText(text.Substring(comment.Index, blockEnd - comment.Index + 2), _codeCommentColor);
			index = blockEnd + 2;
		}
	}

	private void AppendCodeTokens(string text)
	{
		int index = 0;
		while (index < text.Length)
		{
			if (!IsIdentifierStart(text[index]))
			{
				AppendCodeText(text[index].ToString(), _ink);
				index++;
				continue;
			}

			int start = index;
			index++;
			while (index < text.Length && IsIdentifierChar(text[index]))
			{
				index++;
			}

			string token = text.Substring(start, index - start);
			Color color = _ink;
			if (IsCKeywordToken(token))
			{
				color = _codeKeywordColor;
			}
			else if (LooksLikeFunctionCall(text, index))
			{
				color = _codeFunctionColor;
			}

			AppendCodeText(token, color);
		}
	}

	private readonly record struct CommentStart(int Index, bool IsLineComment);

	private static CommentStart FindNextCommentStart(string text, int start)
	{
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = start; i + 1 < text.Length; i++)
		{
			char c = text[i];
			char next = text[i + 1];
			if (escape)
			{
				escape = false;
				continue;
			}
			if (inString || inChar)
			{
				if (c == '\\')
				{
					escape = true;
					continue;
				}
				if (inString && c == '"')
				{
					inString = false;
				}
				else if (inChar && c == '\'')
				{
					inChar = false;
				}
				continue;
			}
			if (c == '"')
			{
				inString = true;
				continue;
			}
			if (c == '\'')
			{
				inChar = true;
				continue;
			}
			if (c == '/' && next == '/')
			{
				return new CommentStart(i, true);
			}
			if (c == '/' && next == '*')
			{
				return new CommentStart(i, false);
			}
		}

		return new CommentStart(-1, false);
	}

	private static bool LooksLikeFunctionCall(string text, int index)
	{
		int i = index;
		while (i < text.Length && char.IsWhiteSpace(text[i]))
		{
			i++;
		}
		return i < text.Length && text[i] == '(';
	}

	private static bool IsFunctionCallIdentifierAtPosition(string text, int start, int length)
	{
		if (start < 0 || length <= 0 || start + length > text.Length)
		{
			return false;
		}

		int next = start + length;
		while (next < text.Length && char.IsWhiteSpace(text[next]))
		{
			next++;
		}
		if (next >= text.Length || text[next] != '(')
		{
			return false;
		}

		int lineStart = text.LastIndexOf('\n', start);
		lineStart = lineStart < 0 ? 0 : lineStart + 1;
		string prefix = text.Substring(lineStart, start - lineStart).TrimStart();
		return !Regex.IsMatch(prefix, @"^(?:static\s+|extern\s+|inline\s+|const\s+|volatile\s+|unsigned\s+|signed\s+)*(?:void|int|short|long|char|float|double|bool|uint\d*_t|int\d*_t|u\d+|s\d+|[A-Za-z_][A-Za-z0-9_]*\s*\*?)\s+[*\s]*$", RegexOptions.IgnoreCase);
	}

	private string FindFirstKnownFunctionCallInLine(string line, string containingFunction)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return "";
		}

		string code = StripLineComment(line);
		foreach (Match match in Regex.Matches(code, @"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("))
		{
			string name = match.Groups["name"].Value;
			if (IsCKeyword(name) ||
				name.Equals(containingFunction, StringComparison.OrdinalIgnoreCase) ||
				!IsKnownProjectFunction(name))
			{
				continue;
			}

			return name;
		}

		return "";
	}

	private static string FormatCodeLineForDisplay(string rawLine, ref int indent)
	{
		string expanded = ExpandTabs(rawLine).TrimEnd();
		string trimmed = expanded.Trim();
		if (trimmed.Length == 0)
		{
			return "";
		}

		int leadingCloseBraces = CountLeadingCloseBracesForDisplay(trimmed);
		if (leadingCloseBraces > 0)
		{
			indent = Math.Max(0, indent - leadingCloseBraces);
		}

		bool preprocessor = trimmed.StartsWith("#", StringComparison.Ordinal);
		bool switchLabel = trimmed.StartsWith("case ", StringComparison.Ordinal) || trimmed.StartsWith("default:", StringComparison.Ordinal);
		int lineIndent = preprocessor ? 0 : (switchLabel ? Math.Max(0, indent - 1) : indent);
		string result = new string(' ', lineIndent * 4) + trimmed;

		int delta = BraceDeltaForDisplay(trimmed, leadingCloseBraces);
		indent = Math.Max(0, indent + delta);
		return result;
	}

	private static int CountLeadingCloseBracesForDisplay(string line)
	{
		int count = 0;
		int index = 0;
		while (index < line.Length)
		{
			while (index < line.Length && char.IsWhiteSpace(line[index]))
			{
				index++;
			}
			if (index >= line.Length || line[index] != '}')
			{
				break;
			}
			count++;
			index++;
		}
		return count;
	}

	private static string ExpandTabs(string text)
	{
		var builder = new StringBuilder(text.Length + 16);
		int column = 0;
		foreach (char c in text)
		{
			if (c == '\t')
			{
				int spaces = 4 - (column % 4);
				builder.Append(' ', spaces);
				column += spaces;
			}
			else
			{
				builder.Append(c);
				column++;
			}
		}
		return builder.ToString();
	}

	private static int BraceDeltaForDisplay(string line, int skipLeadingCloseBraces = 0)
	{
		int delta = 0;
		int skippedLeadingCloseBraces = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			char next = i + 1 < line.Length ? line[i + 1] : '\0';
			if (!inString && !inChar && c == '/' && next == '/')
			{
				break;
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
				delta++;
			}
			else if (c == '}')
			{
				if (skippedLeadingCloseBraces < skipLeadingCloseBraces)
				{
					skippedLeadingCloseBraces++;
					continue;
				}
				delta--;
			}
		}
		return delta;
	}

	private List<WatchItem> GetInlineWatchCandidates((int Start, int End)? lineRange = null)
	{
		if (_watchItems.Count == 0)
		{
			return new List<WatchItem>();
		}

		List<WatchItem> visibleItems = lineRange.HasValue ? GetVisibleWatchItems(lineRange.Value) : GetVisibleWatchItems();
		if (visibleItems.Count > 0)
		{
			return visibleItems
				.OrderByDescending(x => x.Name.Length)
				.ToList();
		}

		return _watchItems
			.Where(x => x.Enabled)
			.OrderByDescending(x => x.Name.Length)
			.ToList();
	}

	private List<string> BuildInlineWatchValues(string line, IReadOnlyList<WatchItem> candidates)
	{
		if (candidates.Count == 0 || string.IsNullOrWhiteSpace(line))
		{
			return new List<string>();
		}
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (WatchItem item in candidates)
		{
			if (result.Count >= 6)
			{
				break;
			}
			string token = FindWatchTokenInLine(line, item.Name);
			if (token.Length == 0 || !seen.Add(token))
			{
				continue;
			}
			if (!TryFormatInlineWatchValue(item, out string value))
			{
				continue;
			}
			result.Add(token + "=" + value);
		}
		return result;
	}

	private IReadOnlyList<InlineWatchValuePlacement> BuildInlineWatchValuePlacements(string line, IReadOnlyList<WatchItem> candidates)
	{
		if (candidates.Count == 0 || string.IsNullOrWhiteSpace(line))
		{
			return Array.Empty<InlineWatchValuePlacement>();
		}

		string searchableLine = GetCodeSearchPortion(line);
		if (string.IsNullOrWhiteSpace(searchableLine))
		{
			return Array.Empty<InlineWatchValuePlacement>();
		}

		var result = new List<InlineWatchValuePlacement>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		DateTime now = DateTime.Now;
		foreach (WatchItem item in candidates)
		{
			(string Token, int Index) token = FindWatchTokenWithIndex(searchableLine, item.Name);
			if (token.Token.Length == 0 || !seen.Add(token.Token))
			{
				continue;
			}
			if (!TryFormatInlineWatchPureValue(item, out string value))
			{
				continue;
			}

			result.Add(new InlineWatchValuePlacement(item, token.Token, token.Index, value, IsWatchValueFresh(item, now)));
		}

		return result
			.OrderBy(x => x.TokenIndex)
			.ThenByDescending(x => x.Fresh)
			.ToList();
	}

	private static int FindSimpleAssignmentOperatorIndex(string line)
	{
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
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
			if (inString || inChar || c != '=')
			{
				continue;
			}

			char previous = i > 0 ? line[i - 1] : '\0';
			char next = i + 1 < line.Length ? line[i + 1] : '\0';
			if (previous == '=' || previous == '!' || previous == '<' || previous == '>' || next == '=')
			{
				continue;
			}

			return i;
		}

		return -1;
	}

	private string InsertInlineWatchValues(string line, IReadOnlyList<WatchItem> candidates, out List<InlineValueSpan> valueSpans)
	{
		valueSpans = new List<InlineValueSpan>();
		if (candidates.Count == 0 || string.IsNullOrWhiteSpace(line))
		{
			return line;
		}

		string trimmedStart = line.TrimStart();
		if (trimmedStart.StartsWith("//", StringComparison.Ordinal) ||
			trimmedStart.StartsWith("/*", StringComparison.Ordinal) ||
			trimmedStart.StartsWith("*", StringComparison.Ordinal))
		{
			return line;
		}

		IReadOnlyList<InlineWatchValuePlacement> placements = BuildInlineWatchValuePlacements(line, candidates);
		if (placements.Count == 0)
		{
			return line;
		}

		var builder = new StringBuilder(line);
		int insertedOffset = 0;
		foreach (InlineWatchValuePlacement placement in placements.OrderBy(x => x.TokenIndex))
		{
			string value = NormalizeInlineRenderedValue(placement.Value);
			if (value.Length == 0)
			{
				continue;
			}

			int tokenEnd = Math.Clamp(placement.TokenIndex + placement.Token.Length + insertedOffset, 0, builder.Length);
			string inlineText = " " + value + " ";
			builder.Insert(tokenEnd, inlineText);
			valueSpans.Add(new InlineValueSpan(tokenEnd + 1, value.Length, placement.Fresh));
			insertedOffset += inlineText.Length;
		}

		valueSpans = valueSpans
			.OrderBy(span => span.Start)
			.ToList();
		return builder.ToString();
	}

	private static string NormalizeInlineRenderedValue(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}

		value = Regex.Replace(value.Trim(), @"\s+", "");
		return value.Length <= 18 ? value : value.Substring(0, 18);
	}

	private static string GetCodeSearchPortion(string line)
	{
		if (string.IsNullOrEmpty(line))
		{
			return "";
		}

		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			char next = i + 1 < line.Length ? line[i + 1] : '\0';
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
			if (c == '/' && (next == '/' || next == '*'))
			{
				return line.Substring(0, i);
			}
		}

		return line;
	}

	private static bool TryFormatInlineWatchPureValue(WatchItem item, out string value)
	{
		if (!string.IsNullOrWhiteSpace(item.DisplayValue))
		{
			value = item.DisplayValue.Trim();
			return true;
		}
		if (!string.IsNullOrWhiteSpace(item.ValueDec))
		{
			value = item.ValueDec.Trim();
			return true;
		}
		if (!string.IsNullOrWhiteSpace(item.ValueHex))
		{
			value = item.ValueHex.Trim();
			return true;
		}

		value = "";
		return false;
	}

	private static bool TryFormatInlineWatchValue(WatchItem item, out string value)
	{
		value = "";
		if (!string.IsNullOrWhiteSpace(item.DisplayValue))
		{
			value = item.DisplayValue;
		}
		else if (!string.IsNullOrWhiteSpace(item.ValueDec))
		{
			value = item.ValueDec;
		}
		else if (!string.IsNullOrWhiteSpace(item.ValueHex))
		{
			value = item.ValueHex;
		}
		else
		{
			return false;
		}
		if (item.ForceActive)
		{
			value += " 保持";
		}
		return true;
	}

	private static string FormatInlineWatchValue(WatchItem item)
	{
		return TryFormatInlineWatchValue(item, out string value) ? value : "等待";
	}

	private ConditionEval EvaluateIfCondition(string line, IReadOnlyList<WatchItem> candidates)
	{
		return EvaluateIfConditionCore(line, candidates);
	}

	private ConditionEval EvaluateIfConditionCore(string line, IReadOnlyList<WatchItem> candidates)
	{
		if ((candidates.Count == 0 && _watchItems.Count == 0) || string.IsNullOrWhiteSpace(line))
		{
			return ConditionEval.Unknown;
		}

		string? expr = ExtractIfExpression(line);
		if (expr == null)
		{
			return ConditionEval.Unknown;
		}

		int comment = expr.IndexOf("//", StringComparison.Ordinal);
		if (comment >= 0)
		{
			expr = expr.Substring(0, comment);
		}

		return EvaluateSimpleBooleanExpression(expr, candidates);
	}

	private static string? ExtractIfExpression(string line)
	{
		Match match = Regex.Match(line, @"^\s*if\b");
		if (!match.Success)
		{
			return null;
		}

		int open = line.IndexOf('(', match.Index + match.Length);
		if (open < 0)
		{
			return null;
		}

		int depth = 0;
		bool inString = false;
		bool inChar = false;
		bool escape = false;
		for (int i = open; i < line.Length; i++)
		{
			char c = line[i];
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
					return line.Substring(open + 1, i - open - 1);
				}
			}
		}

		return null;
	}

	private ConditionEval EvaluateSimpleBooleanExpression(string expr, IReadOnlyList<WatchItem> candidates)
	{
		bool hasUnknown = false;
		string[] orParts = Regex.Split(expr, @"\|\|");
		foreach (string orPart in orParts)
		{
			bool andFailed = false;
			bool andUnknown = false;
			string[] andParts = Regex.Split(orPart, @"&&");
			foreach (string andPart in andParts)
			{
				ConditionEval part = EvaluateSimpleCondition(andPart, candidates);
				if (part == ConditionEval.False)
				{
					andFailed = true;
					break;
				}
				if (part == ConditionEval.Unknown)
				{
					andUnknown = true;
				}
			}
			if (!andFailed && !andUnknown)
			{
				return ConditionEval.True;
			}
			if (!andFailed && andUnknown)
			{
				hasUnknown = true;
			}
		}
		return hasUnknown ? ConditionEval.Unknown : ConditionEval.False;
	}

	private ConditionEval EvaluateSimpleCondition(string expression, IReadOnlyList<WatchItem> candidates)
	{
		string text = expression.Trim();
		if (text.Length == 0)
		{
			return ConditionEval.Unknown;
		}

		while (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal) && HasBalancedOuterParentheses(text))
		{
			text = text.Substring(1, text.Length - 2).Trim();
		}

		bool inverted = false;
		while (text.StartsWith("!", StringComparison.Ordinal))
		{
			inverted = !inverted;
			text = text.Substring(1).Trim();
		}

		ConditionEval value = EvaluateSimpleConditionCore(text, candidates);
		if (value == ConditionEval.Unknown)
		{
			return ConditionEval.Unknown;
		}
		if (!inverted)
		{
			return value;
		}
		return value == ConditionEval.True ? ConditionEval.False : ConditionEval.True;
	}

	private ConditionEval EvaluateSimpleConditionCore(string text, IReadOnlyList<WatchItem> candidates)
	{
		Match absCompare = Regex.Match(text, @"^abs\s*\((?<inner>.+)\)\s*(?<op>==|!=|>=|<=|>|<)\s*(?<right>-?(?:0x[0-9A-Fa-f]+|\d+))$");
		if (absCompare.Success)
		{
			if (!TryEvaluateSimpleIntegerExpression(absCompare.Groups["inner"].Value, candidates, out long inner) ||
				!TryParseIntegerLiteral(absCompare.Groups["right"].Value, out long right))
			{
				return ConditionEval.Unknown;
			}

			bool result = CompareIntegers(Math.Abs(inner), right, absCompare.Groups["op"].Value);
			return result ? ConditionEval.True : ConditionEval.False;
		}

		Match compare = Regex.Match(text, @"^(?<left>[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]+\]|\.[A-Za-z_][A-Za-z0-9_]*)?)\s*(?<op>==|!=|>=|<=|>|<)\s*(?<right>-?(?:0x[0-9A-Fa-f]+|\d+))$");
		if (compare.Success)
		{
			if (!TryGetWatchCurrentNumericValue(compare.Groups["left"].Value, candidates, out long left) ||
				!TryParseIntegerLiteral(compare.Groups["right"].Value, out long right))
			{
				return ConditionEval.Unknown;
			}

			bool result = CompareIntegers(left, right, compare.Groups["op"].Value);
			return result ? ConditionEval.True : ConditionEval.False;
		}

		Match symbol = Regex.Match(text, @"^[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]+\]|\.[A-Za-z_][A-Za-z0-9_]*)?$");
		if (!symbol.Success)
		{
			return ConditionEval.Unknown;
		}
		if (!TryGetWatchCurrentNumericValue(symbol.Value, candidates, out long value))
		{
			return ConditionEval.Unknown;
		}
		return value != 0 ? ConditionEval.True : ConditionEval.False;
	}

	private static bool CompareIntegers(long left, long right, string op)
	{
		return op switch
		{
			"==" => left == right,
			"!=" => left != right,
			">=" => left >= right,
			"<=" => left <= right,
			">" => left > right,
			"<" => left < right,
			_ => false
		};
	}

	private bool TryEvaluateSimpleIntegerExpression(string expression, IReadOnlyList<WatchItem> candidates, out long value)
	{
		value = 0;
		string text = expression.Trim();
		if (text.Length == 0)
		{
			return false;
		}

		int index = 0;
		int sign = 1;
		bool hasTerm = false;
		while (index < text.Length)
		{
			while (index < text.Length && char.IsWhiteSpace(text[index]))
			{
				index++;
			}
			if (index >= text.Length)
			{
				break;
			}

			if (text[index] == '+')
			{
				sign = 1;
				index++;
				continue;
			}
			if (text[index] == '-')
			{
				sign = -1;
				index++;
				continue;
			}

			int start = index;
			while (index < text.Length && IsSimpleIntegerExpressionChar(text[index]))
			{
				index++;
			}
			if (start == index)
			{
				return false;
			}

			string token = text.Substring(start, index - start).Trim();
			long term;
			if (TryParseIntegerLiteral(token, out long literal))
			{
				term = literal;
			}
			else if (TryGetWatchCurrentNumericValue(token, candidates, out long watchValue))
			{
				term = watchValue;
			}
			else
			{
				return false;
			}

			value += sign * term;
			sign = 1;
			hasTerm = true;
		}

		return hasTerm;
	}

	private static bool IsSimpleIntegerExpressionChar(char ch)
	{
		return char.IsLetterOrDigit(ch) || ch == '_' || ch == '[' || ch == ']' || ch == '.' || ch == 'x' || ch == 'X';
	}

	private static bool HasBalancedOuterParentheses(string text)
	{
		int depth = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '(')
			{
				depth++;
			}
			else if (text[i] == ')')
			{
				depth--;
				if (depth == 0 && i < text.Length - 1)
				{
					return false;
				}
			}
		}
		return depth == 0;
	}

	private bool TryGetWatchCurrentNumericValue(string name, IReadOnlyList<WatchItem> candidates, out long value)
	{
		value = 0;
		string baseName = GetIdentifierBase(name);
		if (baseName.Length == 0)
		{
			return false;
		}

		if (!TryFindCurrentNumericWatchItem(baseName, candidates, out WatchItem? item))
		{
			return false;
		}

		return TryReadWatchCurrentIntegerValue(item!, out value);
	}

	private bool TryFindCurrentNumericWatchItem(string baseName, IReadOnlyList<WatchItem> candidates, out WatchItem? item)
	{
		item = null;
		if (TryFindCurrentNumericWatchItemInList(baseName, candidates, out item))
		{
			return true;
		}

		if (!ReferenceEquals(candidates, _watchItems) &&
			TryFindCurrentNumericWatchItemInList(baseName, _watchItems, out item))
		{
			return true;
		}

		return false;
	}

	private static bool TryFindCurrentNumericWatchItemInList(string baseName, IEnumerable<WatchItem> source, out WatchItem? item)
	{
		item = null;
		List<WatchItem> exactMatches = source
			.Where(candidate =>
				candidate.Enabled &&
				IsExactWatchIdentifierMatch(candidate, baseName) &&
				TryReadWatchCurrentIntegerValue(candidate, out _))
			.Distinct()
			.OrderByDescending(candidate => candidate.LastUpdate ?? DateTime.MinValue)
			.ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (exactMatches.Count > 0)
		{
			item = exactMatches[0];
			return true;
		}

		List<WatchItem> aliasMatches = source
			.Where(candidate =>
				candidate.Enabled &&
				WatchIdentifierAliases(candidate.Name).Any(alias => alias.Equals(baseName, StringComparison.OrdinalIgnoreCase)) &&
				TryReadWatchCurrentIntegerValue(candidate, out _))
			.Distinct()
			.OrderByDescending(candidate => candidate.LastUpdate ?? DateTime.MinValue)
			.ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (aliasMatches.Count > 0)
		{
			item = aliasMatches[0];
			return true;
		}

		return false;
	}

	private static bool IsExactWatchIdentifierMatch(WatchItem item, string baseName)
	{
		return item.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
			GetWatchDisplayName(item).Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
			GetIdentifierBase(item.Name).Equals(baseName, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryReadWatchCurrentIntegerValue(WatchItem item, out long value)
	{
		value = 0;
		if (TryParseWatchIntegerText(item.ValueDec, out value))
		{
			return true;
		}
		if (TryParseWatchIntegerText(item.DisplayValue, out value))
		{
			return true;
		}
		if (!string.IsNullOrWhiteSpace(item.ValueHex) &&
			item.ValueHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
			long.TryParse(item.ValueHex.Substring(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
		{
			return true;
		}
		if (item.LastUpdate.HasValue)
		{
			int bytes = Math.Clamp(item.Size, 1, 4);
			uint masked = MaskRawValue(item.RawValue, bytes);
			value = IsSignedWatchItem(item) ? ToSignedValue(masked, bytes) : masked;
			return true;
		}
		return false;
	}

	private static bool TryParseWatchIntegerText(string text, out long value)
	{
		value = 0;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		string trimmed = text.Trim();
		if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return long.TryParse(trimmed.Substring(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
		}
		if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
			long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
		{
			return true;
		}
		if ((double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantValue) ||
			 double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out invariantValue)) &&
			double.IsFinite(invariantValue))
		{
			value = (long)Math.Round(invariantValue, MidpointRounding.AwayFromZero);
			return true;
		}

		Match leadingNumber = Regex.Match(trimmed, @"^-?\d+");
		return leadingNumber.Success &&
			long.TryParse(leadingNumber.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}

	private static bool TryParseIntegerLiteral(string text, out long value)
	{
		text = text.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return long.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value);
		}
		return long.TryParse(text, out value);
	}

	private static string FindWatchTokenInLine(string line, string watchName)
	{
		if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(watchName))
		{
			return "";
		}

		foreach (string alias in WatchIdentifierAliases(watchName).OrderByDescending(x => x.Length))
		{
			if (FindIdentifierIndex(line, alias) >= 0)
			{
				return alias;
			}
		}

		return "";
	}

	private static (string Token, int Index) FindWatchTokenWithIndex(string line, string watchName)
	{
		if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(watchName))
		{
			return ("", -1);
		}

		foreach (string alias in WatchIdentifierAliases(watchName).OrderByDescending(x => x.Length))
		{
			int index = FindIdentifierIndex(line, alias);
			if (index >= 0)
			{
				return (alias, index);
			}
		}

		return ("", -1);
	}

	private static bool LineMentionsWatch(string line, string watchName)
	{
		if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(watchName))
		{
			return false;
		}
		return WatchIdentifierAliases(watchName).Any(alias => ContainsIdentifier(line, alias));
	}

	private static bool ContainsIdentifier(string line, string identifier)
	{
		return FindIdentifierIndex(line, identifier) >= 0;
	}

	private static int FindIdentifierIndex(string line, string identifier)
	{
		if (identifier.Length == 0 || line.Length < identifier.Length)
		{
			return -1;
		}
		int index = 0;
		while (index <= line.Length - identifier.Length)
		{
			int found = line.IndexOf(identifier, index, StringComparison.OrdinalIgnoreCase);
			if (found < 0)
			{
				return -1;
			}
			int before = found - 1;
			int after = found + identifier.Length;
			bool leftOk = before < 0 || !IsIdentifierChar(line[before]);
			bool rightOk = after >= line.Length || !IsIdentifierChar(line[after]);
			if (leftOk && rightOk)
			{
				return found;
			}
			index = found + 1;
		}
		return -1;
	}

	private static string GetRelativePathSafe(string root, string filePath)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(root))
			{
				return Path.GetRelativePath(root, filePath);
			}
		}
		catch
		{
		}
		return filePath;
	}

	private static IEnumerable<string> EnumerateSourceFilesForOpen(string root)
	{
		var pending = new Stack<string>();
		pending.Push(root);
		string[] ignored = { ".git", ".svn", ".vs", "bin", "obj", "Debug", "Release", "Listings", "Objects", "RTE", "__pycache__" };
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
				if (!ignored.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
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
				if (extension.Equals(".c", StringComparison.OrdinalIgnoreCase) || extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
				{
					yield return file;
				}
			}
		}
	}

	private void UpdateItem(WatchItem item, int len, byte status, uint value)
	{
		QueueWatchUpdate(new PendingWatchUpdate(item, len, status, value, Timeout: false, Error: false));
	}

	private void MarkTimeout(WatchItem item)
	{
		QueueWatchUpdate(new PendingWatchUpdate(item, 0, 0, 0, Timeout: true, Error: false));
	}

	private void MarkError(WatchItem item, string message)
	{
		QueueWatchUpdate(new PendingWatchUpdate(item, 0, 0, 0, Timeout: false, Error: true));
	}

	private void QueueWatchUpdate(PendingWatchUpdate update)
	{
		lock (_pendingWatchLock)
		{
			_pendingWatchUpdates[update.Item] = update;
		}
	}

	private void FlushPendingWatchUpdates()
	{
		if (ShouldDeferUiValueRefresh())
		{
			return;
		}

		List<PendingWatchUpdate> updates;
		lock (_pendingWatchLock)
		{
			if (_pendingWatchUpdates.Count == 0)
			{
				return;
			}
			updates = _pendingWatchUpdates.Values.ToList();
			_pendingWatchUpdates.Clear();
		}

		bool sourceNeedsRefresh = false;
		bool insightNeedsRefresh = false;
		List<string>? visibleRefreshLines = null;
		DateTime now = DateTime.Now;
		_grid.SuspendLayout();
		try
		{
			foreach (PendingWatchUpdate update in updates)
			{
				WatchItem item = update.Item;
				if (_watchItems.IndexOf(item) < 0)
				{
					continue;
				}

				if (update.Timeout)
				{
					item.MissCount++;
					if (!item.LastUpdate.HasValue)
					{
						item.Status = "等待响应";
					}
					else
					{
						item.Status = "正常";
					}
					continue;
				}

				if (update.Error)
				{
					item.Status = "通信异常";
					continue;
				}

				item.MissCount = 0;
				if (update.Status == 0)
				{
					int valueBytes = EffectiveValueBytes(item, update.Len);
					uint rawValue = MaskRawValue(update.Value, valueBytes);
					string displayValue = item.DisplayValue;
					string previousStatus = item.Status;
					bool firstValue = !item.LastUpdate.HasValue;
					bool wasInlineStale = CodeValueBlinkEnabled &&
						(!item.LastUpdate.HasValue ||
						 (now - item.LastUpdate.Value).TotalMilliseconds > CodeValueFreshHighlightMs);
					uint previousRawValue = MaskRawValue(item.RawValue, valueBytes);
					item.RawValue = rawValue;
					item.ValueDec = FormatDecimalRawValue(item, rawValue, valueBytes);
					item.ValueHex = "0x" + rawValue.ToString("X" + HexDigitsForBytes(valueBytes));
					item.DisplayValue = FormatValue(item);
					bool valueChanged = firstValue ||
						previousRawValue != rawValue ||
						!displayValue.Equals(item.DisplayValue, StringComparison.Ordinal);
					if (valueChanged)
					{
						item.LastValueChange = now;
						insightNeedsRefresh |= WatchMatchesFocusedVariable(item);
						visibleRefreshLines ??= GetVisibleRawLines(DataMirrorPaddingLines).ToList();
						sourceNeedsRefresh |= VisibleRangeMentionsWatch(item, visibleRefreshLines);
					}
					if (!displayValue.Equals(item.DisplayValue, StringComparison.Ordinal))
					{
						RefreshValueCell(item);
					}
					item.Status = "正常";
					item.LastUpdate = now;
					visibleRefreshLines ??= GetVisibleRawLines(DataMirrorPaddingLines).ToList();
					bool visibleMentionsWatch = VisibleRangeMentionsWatch(item, visibleRefreshLines);
					sourceNeedsRefresh |= visibleMentionsWatch;
					if (wasInlineStale || !previousStatus.Equals("正常", StringComparison.Ordinal))
					{
						sourceNeedsRefresh |= visibleMentionsWatch;
					}
				}
				else
				{
					item.Status = update.Status switch
					{
						1 => "变量无效", 
						2 => "长度不支持", 
						_ => "读取失败", 
					};
				}
			}
		}
		finally
		{
			_grid.ResumeLayout();
		}

		if (sourceNeedsRefresh)
		{
			UpdateVisibleValuesLabel();
			string conditionSignature = BuildVisibleConditionSignature();
			_lastVisibleConditionSignature = conditionSignature;
			if (ShouldShowInlineCodeValues())
			{
				_functionCodeDirty = true;
				_dataCodeDirty = ReferenceEquals(_functionCodeBox, _dataCodeBox);
			}
			else
			{
				RefreshScintillaVisibleRuntimeValues(force: true);
			}
		}
		if (insightNeedsRefresh)
		{
			UpdateProgramInsightPanel();
		}
	}

	private void GridColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
	{
		if (e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].DataPropertyName != "DisplayValue")
		{
			return;
		}
		ToggleValueDisplayMode();
	}

	private void ToggleValueDisplayMode()
	{
		_showHexValue = !_showHexValue;
		if (_valueColumnIndex >= 0 && _valueColumnIndex < _grid.Columns.Count)
		{
			_grid.Columns[_valueColumnIndex].HeaderText = (_showHexValue ? "值(16)" : "值(10)");
		}
		foreach (WatchItem watchItem in _watchItems)
		{
			watchItem.DisplayValue = FormatValue(watchItem);
			RefreshValueCell(watchItem);
		}
		UpdateValueFormatButton();
		MarkFunctionCodeDirty();
		SaveDefaultProfileQuietly();
	}

	private void UpdateValueFormatButton()
	{
		if (_valueFormatButton == null || _valueFormatButton.IsDisposed)
		{
			return;
		}

		_valueFormatButton.Text = _showHexValue ? "16进制" : "10进制";
		_valueFormatButton.BackColor = _showHexValue ? _gridHeader : _button;
		_valueFormatButton.ForeColor = _ink;
		_valueFormatButton.FlatStyle = FlatStyle.Flat;
		_valueFormatButton.FlatAppearance.BorderColor = _gridHeader;
	}

	private string FormatValue(WatchItem item)
	{
		if (item.ValueDec.Length == 0 && item.ValueHex.Length == 0)
		{
			return "";
		}
		if (item.RawValue == 0 && uint.TryParse(item.ValueDec, out var result))
		{
			item.RawValue = result;
		}
		if (!_showHexValue)
		{
			if (item.ValueDec.Length > 0)
			{
				return item.ValueDec;
			}
			int valueBytes = EffectiveValueBytes(item, Math.Clamp(item.Size, 1, 4));
			return FormatDecimalRawValue(item, MaskRawValue(item.RawValue, valueBytes), valueBytes);
		}
		if (item.ValueHex.Length > 0)
		{
			return item.ValueHex;
		}
		int num = HexDigitsForBytes(Math.Clamp(item.Size, 1, 4));
		return "0x" + item.RawValue.ToString("X" + num);
	}

	private static int EffectiveValueBytes(WatchItem item, int reportedLen)
	{
		if (reportedLen > 0)
		{
			return Math.Clamp(reportedLen, 1, 4);
		}
		return Math.Clamp(item.Size, 1, 4);
	}

	private static uint MaskRawValue(uint rawValue, int bytes)
	{
		return Math.Clamp(bytes, 1, 4) switch
		{
			1 => rawValue & 0xFFu,
			2 => rawValue & 0xFFFFu,
			3 => rawValue & 0xFFFFFFu,
			_ => rawValue,
		};
	}

	private static string FormatDecimalRawValue(WatchItem item, uint rawValue, int bytes)
	{
		int valueBytes = EffectiveValueBytes(item, bytes);
		uint masked = MaskRawValue(rawValue, valueBytes);
		if (IsFloatWatchItem(item) && valueBytes == 4)
		{
			float value = BitConverter.UInt32BitsToSingle(masked);
			return float.IsFinite(value) ? FormatFloatValue(value) : "0";
		}

		if (!IsSignedWatchItem(item))
		{
			return masked.ToString();
		}

		return ToSignedValue(masked, valueBytes).ToString();
	}

	private static string FormatFloatValue(float value)
	{
		if (Math.Abs(value) >= 1000f || (Math.Abs(value) > 0f && Math.Abs(value) < 0.01f))
		{
			return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
		}

		return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
	}

	private static long ToSignedValue(uint rawValue, int bytes)
	{
		int bits = Math.Clamp(bytes, 1, 4) * 8;
		long value = rawValue & (bits == 32 ? 0xFFFFFFFFL : ((1L << bits) - 1L));
		long signBit = 1L << (bits - 1);
		if ((value & signBit) != 0)
		{
			value -= 1L << bits;
		}
		return value;
	}

	private static bool IsSignedWatchItem(WatchItem item)
	{
		string typeName = item.TypeName.Trim();
		if (typeName.Length == 0)
		{
			return false;
		}

		string t = Regex.Replace(typeName, @"\b(const|volatile|static|extern|register)\b", "", RegexOptions.IgnoreCase);
		t = Regex.Replace(t, @"\s+", " ").Trim().ToLowerInvariant();
		if (t.Length == 0)
		{
			return false;
		}

		if (Regex.IsMatch(t, @"\b(unsigned|uint|uint8_t|uint16_t|uint32_t|uint64_t|u8|u16|u32|u64|byte|word|dword|qword|bool|boolean)\b") ||
			t.Contains("uint", StringComparison.OrdinalIgnoreCase) ||
			t.Contains("uchar", StringComparison.OrdinalIgnoreCase) ||
			t.Contains("ushort", StringComparison.OrdinalIgnoreCase) ||
			t.Contains("ulong", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (Regex.IsMatch(t, @"\b(signed|int8_t|int16_t|int32_t|int64_t|sint8|sint16|sint32|s8|s16|s32|s64|short|int|long|enum)\b"))
		{
			return true;
		}

		return false;
	}

	private static bool IsFloatWatchItem(WatchItem item)
	{
		string typeName = item.TypeName.Trim();
		return typeName.Equals("float", StringComparison.OrdinalIgnoreCase) ||
			typeName.Equals("double", StringComparison.OrdinalIgnoreCase) ||
			Regex.IsMatch(typeName, @"\b(float|double)\b", RegexOptions.IgnoreCase);
	}

	private void ResetTransientWatchState(WatchItem item)
	{
		item.MissCount = 0;
		item.Status = "待读取";
		item.ForceActive = false;
		item.ForceText = "";
		item.DisplayValue = FormatValue(item);
	}

	private static int HexDigitsForBytes(int bytes)
	{
		return Math.Clamp(bytes, 1, 4) * 2;
	}

	private void RefreshValueCell(WatchItem item)
	{
		int num = _watchItems.IndexOf(item);
		if (num >= 0 && _valueColumnIndex >= 0 && num < _grid.Rows.Count)
		{
			DataGridViewCell dataGridViewCell = _grid.Rows[num].Cells[_valueColumnIndex];
			dataGridViewCell.Value = item.DisplayValue;
			_grid.InvalidateCell(dataGridViewCell);
		}
	}

	private void UpdateForceHoldReminder()
	{
		if (_forceHoldLabel == null || _forceHoldLabel.IsDisposed)
		{
			return;
		}

		_forceHoldLabel.Text = "";
		_forceHoldLabel.Visible = false;

		RefreshForceVisualsForAll();
	}

	private void RefreshForceVisualsForAll()
	{
		if (_grid == null || _grid.IsDisposed)
		{
			return;
		}

		foreach (DataGridViewRow row in _grid.Rows)
		{
			if (row.DataBoundItem is WatchItem item)
			{
				ApplyForceVisual(row, item);
			}
		}
	}

	private void ApplyForceVisual(DataGridViewRow row, WatchItem item)
	{
		Color baseBack = row.Index % 2 == 0 ? _surface : _surfaceAlt;
		row.DefaultCellStyle.BackColor = item.ForceActive ? MixColor(baseBack, ForceHoldBackColor, 0.18f) : baseBack;
		row.DefaultCellStyle.ForeColor = _ink;
		row.DefaultCellStyle.SelectionBackColor = item.ForceActive ? MixColor(_accent, ForceHoldBackColor, 0.25f) : _accent;
		row.DefaultCellStyle.SelectionForeColor = Color.White;
		if (_forceColumnIndex < 0 || _forceColumnIndex >= row.Cells.Count)
		{
			return;
		}

		DataGridViewCell cell = row.Cells[_forceColumnIndex];
		if (item.ForceActive)
		{
			cell.Style.BackColor = ForceHoldBackColor;
			cell.Style.ForeColor = ForceHoldForeColor;
			cell.Style.SelectionBackColor = ForceHoldBackColor;
			cell.Style.SelectionForeColor = ForceHoldForeColor;
			cell.ToolTipText = "保持中，右键选择“释放保持”";
		}
		else
		{
			cell.Style.BackColor = baseBack;
			cell.Style.ForeColor = _ink;
			cell.Style.SelectionBackColor = _accent;
			cell.Style.SelectionForeColor = Color.White;
			cell.ToolTipText = "";
		}
	}

	private void RefreshForceCell(WatchItem item)
	{
		int num = _watchItems.IndexOf(item);
		if (num >= 0 && _forceColumnIndex >= 0 && num < _grid.Rows.Count)
		{
			DataGridViewCell cell = _grid.Rows[num].Cells[_forceColumnIndex];
			cell.Value = item.ForceText;
			ApplyForceVisual(_grid.Rows[num], item);
			_grid.InvalidateCell(cell);
		}
		UpdateForceHoldReminder();
	}

	private void SaveProfileAs()
	{
		using SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = "变量列表 (*.json)|*.json",
			FileName = "monitor_profile.json"
		};
		if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
		{
			SaveProfile(saveFileDialog.FileName);
		}
	}

	private void LoadProfileFromFile()
	{
		using OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "变量列表 (*.json)|*.json"
		};
		if (openFileDialog.ShowDialog(this) == DialogResult.OK)
		{
			LoadProfile(openFileDialog.FileName);
		}
	}

	private void GenerateProgramGraph()
	{
		string text = GetWorkDirectoryFromUi();
		if (text.Length == 0 || !Directory.Exists(text))
		{
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
			{
				Description = "请选择 Keil 工程目录",
				UseDescriptionForTitle = true
			};
			if (folderBrowserDialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			text = folderBrowserDialog.SelectedPath;
			SetWorkDirectory(text, loadMap: true);
		}
		try
		{
			RefreshProgramGraphPanel(text);
		}
		catch (Exception ex)
		{
			Log("生成程序图谱失败：" + ex.Message);
		}
	}

	private void RefreshProgramGraphPanel(string directory, bool force = false)
	{
		if (_flowChart == null || _programSummaryLabel == null || _runtimeLocationLabel == null)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return;
		}

		if (force)
		{
			_activeProgramGraphDirectory = "";
		}
		_pendingProgramGraphDirectory = directory;
		_programGraphDebounceTimer.Stop();
		_programGraphDebounceTimer.Start();
	}

	private void StartProgramGraphAnalysis(string directory)
	{
		if (_flowChart == null || _programSummaryLabel == null || _runtimeLocationLabel == null)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return;
		}
		if (_activeProgramGraphDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		_activeProgramGraphDirectory = directory;
		int version = Interlocked.Increment(ref _programGraphAnalysisVersion);
		_runtimeLocationLabel.Text = "正在分析工程";
		_programSummaryLabel.Text = "正在生成程序框架...";
		Task.Run(() =>
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			DateTime latestWrite = GetLatestSourceWriteUtc(directory, out int sourceCount);
			ProgramGraphSnapshot snapshot = ProgramGraphGenerator.Analyze(directory);
			stopwatch.Stop();
			return (Directory: directory, LatestWrite: latestWrite, SourceCount: sourceCount, Snapshot: snapshot, ElapsedMs: stopwatch.ElapsedMilliseconds);
		}).ContinueWith(task =>
		{
			if (IsDisposed)
			{
				return;
			}
			try
			{
				BeginInvoke((Action)(() =>
				{
					if (_activeProgramGraphDirectory.Equals(task.Status == TaskStatus.RanToCompletion ? task.Result.Directory : directory, StringComparison.OrdinalIgnoreCase))
					{
						_activeProgramGraphDirectory = "";
					}
					if (version != _programGraphAnalysisVersion || task.Status != TaskStatus.RanToCompletion)
					{
						return;
					}
					ApplyProgramGraphSnapshot(task.Result.Directory, task.Result.LatestWrite, task.Result.SourceCount, task.Result.Snapshot, task.Result.ElapsedMs);
				}));
			}
			catch
			{
			}
		}, TaskScheduler.Default);
	}

	private void ApplyProgramGraphSnapshot(string directory, DateTime latestWrite, int sourceCount, ProgramGraphSnapshot programGraphSnapshot, long elapsedMs)
	{
		_programGraphLastSourceWrite = latestWrite;
		_programGraphSourceCount = sourceCount;
		_programGraphSnapshot = programGraphSnapshot;
		RefreshOfflineRootCandidatesUi(directory);
		ClearOfflineSimulationProgramCache();
		if (_offlineSimulation)
		{
			EnsureOfflineApplicationWatchItems();
		}
		_traceLabels.Clear();
		if (!programGraphSnapshot.Success)
		{
			_runtimeLocationLabel.Text = "等待工程分析";
			_programSummaryLabel.Text = programGraphSnapshot.Message;
			_flowChart?.SetGraph(
				new[] { new FlowChartNode("msg", "等待工程分析\n选择工作目录后自动生成", new RectangleF(24, 24, 220, 68), 0) },
				Array.Empty<FlowChartEdge>());
			_flowChart?.SetAnimationEnabled(false);
			Log(programGraphSnapshot.Message);
			UpdateProgramInsightPanel(force: true);
			return;
		}
		_lastTraceId = ushort.MaxValue;
		string entryName = string.IsNullOrWhiteSpace(programGraphSnapshot.StartFunction) ? "自动识别" : programGraphSnapshot.StartFunction;
		_runtimeLocationLabel.Text = GetProgramEntryDisplayName(programGraphSnapshot, entryName);
		_programSummaryLabel.Text = programGraphSnapshot.CallGraphNodes.Count > 0
			? $"业务入口 {entryName}    {programGraphSnapshot.CallGraphNodes.Count} 个链路函数"
			: $"入口 {entryName}    {programGraphSnapshot.FrameworkSteps.Count} 个节点";
		IReadOnlyList<ProgramFrameworkStep> frameworkSteps = programGraphSnapshot.FrameworkSteps;
		if (frameworkSteps.Count == 0)
		{
			frameworkSteps = programGraphSnapshot.FlowFunctions
				.Select(f => new ProgramFrameworkStep(f.Name, f.Summary, f.TraceId, f.Name, f.Summary))
				.ToList();
		}
		if (programGraphSnapshot.CallGraphNodes.Count > 0)
		{
			UpdateCallRelationChart(programGraphSnapshot.CallGraphNodes, programGraphSnapshot.CallGraphEdges);
		}
		else
		{
			UpdateFlowChart(frameworkSteps);
		}
		foreach (ProgramFrameworkStep step in frameworkSteps)
		{
			_traceLabels[step.TraceId] = step.Name;
		}
		foreach (ProgramCallGraphNode node in programGraphSnapshot.CallGraphNodes.Where(n => !IsProgramGraphNoiseNode(n)))
		{
			_traceLabels[node.TraceId] = node.Name;
		}
		foreach (ProgramFunctionInfo hotFunction in programGraphSnapshot.HotFunctions)
		{
			_traceLabels[hotFunction.TraceId] = hotFunction.Name;
		}
		WarmSourceTextCacheFromSnapshot(directory, programGraphSnapshot);
		Log($"{programGraphSnapshot.Message}，分析耗时 {elapsedMs} ms，内存 {GetProcessMemoryText()}。");
		UpdateProgramInsightPanel(force: true);
		ScheduleMemoryTrim();
	}

	private string GetProgramEntryDisplayName()
	{
		return GetProgramEntryDisplayName(_programGraphSnapshot, "");
	}

	private static string GetProgramEntryDisplayName(ProgramGraphSnapshot? snapshot, string fallback)
	{
		if (snapshot != null)
		{
			string preferred = FindPreferredEntryName(snapshot);
			if (!string.IsNullOrWhiteSpace(preferred))
			{
				return preferred;
			}
		}

		return string.IsNullOrWhiteSpace(fallback) || fallback.Equals("自动识别", StringComparison.OrdinalIgnoreCase)
			? "自动识别"
			: fallback;
	}

	private static string FindPreferredEntryName(ProgramGraphSnapshot snapshot)
	{
		if (!string.IsNullOrWhiteSpace(snapshot.StartFunction))
		{
			return snapshot.StartFunction;
		}

		return snapshot.FrameworkSteps.FirstOrDefault()?.Name
			?? snapshot.FlowFunctions.FirstOrDefault()?.Name
			?? snapshot.CallGraphNodes.FirstOrDefault()?.Name
			?? "";
	}

	private static void ScheduleMemoryTrim()
	{
		if (Interlocked.Exchange(ref _memoryTrimPending, 1) == 1)
		{
			return;
		}

		Task.Run(async () =>
		{
			try
			{
				await Task.Delay(900).ConfigureAwait(false);
				GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
			}
			finally
			{
				Interlocked.Exchange(ref _memoryTrimPending, 0);
			}
		});
	}

	private void UpdateFlowChart(IReadOnlyList<ProgramFrameworkStep> frameworkSteps)
	{
		if (_flowChart == null)
		{
			return;
		}
		(IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) graph =
			LooksLikeMainLoopProject(frameworkSteps) ? BuildMainLoopFlowChart(frameworkSteps) : BuildGenericFlowChart(frameworkSteps);
		_flowChart.SetGraph(graph.nodes, graph.edges);
		_flowChart.SetFocusedFunction(_programTreeFocusedFunction);
		_flowChart.SetAnimationEnabled(false);
		if (_lastTraceId != ushort.MaxValue)
		{
			_flowChart.SetHighlight(_lastTraceId);
		}
	}

	private void UpdateCallRelationChart(
		IReadOnlyList<ProgramCallGraphNode> graphNodes,
		IReadOnlyList<ProgramCallGraphEdge> graphEdges)
	{
		if (_flowChart == null)
		{
			return;
		}

		(IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) graph = BuildCallRelationFlowChart(graphNodes, graphEdges);
		_flowChart.SetGraph(graph.nodes, graph.edges);
		_flowChart.SetFocusedFunction(_programTreeFocusedFunction);
		_flowChart.SetAnimationEnabled(false);
		if (_lastTraceId != ushort.MaxValue)
		{
			_flowChart.SetHighlight(_lastTraceId);
		}
	}

	private (IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) BuildCallRelationFlowChart(
		IReadOnlyList<ProgramCallGraphNode> graphNodes,
		IReadOnlyList<ProgramCallGraphEdge> graphEdges)
	{
		List<ProgramCallGraphNode> visibleGraphNodes = graphNodes
			.Where(n => !IsProgramGraphNoiseNode(n))
			.Where(n => !n.Name.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
			.ToList();
		HashSet<string> visibleGraphNodeIds = visibleGraphNodes
			.Select(n => n.Id)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<ProgramCallGraphEdge> visibleGraphEdges = graphEdges
			.Where(e => visibleGraphNodeIds.Contains(e.FromId) && visibleGraphNodeIds.Contains(e.ToId))
			.ToList();

		if (visibleGraphNodes.Count == 0)
		{
			return (
				new[] { new FlowChartNode("empty", "未识别到函数调用关系\n选择工程目录后自动分析", new RectangleF(24, 24, 250, 72), 0) },
				Array.Empty<FlowChartEdge>());
		}

		Dictionary<string, int> edgeOrder = BuildFirstEdgeOrder(visibleGraphEdges);
		Dictionary<string, ProgramCallGraphNode> nodeById = visibleGraphNodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<ProgramCallGraphNode>> childrenById = visibleGraphEdges
			.Where(e => nodeById.ContainsKey(e.FromId) && nodeById.ContainsKey(e.ToId))
			.GroupBy(e => e.FromId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				g => g.Key,
				g => g.Select(e => nodeById[e.ToId])
					.DistinctBy(n => n.Id)
					.OrderBy(n => edgeOrder.TryGetValue(n.Id, out int order) ? order : int.MaxValue)
					.ThenByDescending(GetBusinessNodeScore)
					.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
					.ToList(),
				StringComparer.OrdinalIgnoreCase);

		var nodes = new List<FlowChartNode>();
		var edges = new List<FlowChartEdge>();
		Dictionary<string, int> hierarchyLevels = BuildCallHierarchyLevels(visibleGraphNodes, visibleGraphEdges);
		string focusedFunction = !string.IsNullOrWhiteSpace(_programTreeFocusedFunction)
			? _programTreeFocusedFunction
			: _currentFunctionSource?.FunctionName ?? "";
		string locateFunction = _programTreeLocateTargetFunction;

		int clientWidth = _flowChart?.ClientSize.Width > 0 ? _flowChart.ClientSize.Width : Ui(520);
		bool sideBySide = clientWidth >= Ui(470);
		float margin = Ui(12);
		float gap = Ui(18);
		float rowTop = Ui(42);
		float rowHeight = Ui(44);
		float columnWidth = sideBySide
			? Math.Max(Ui(205), (clientWidth - margin * 2 - gap) / 2f)
			: Math.Max(Ui(300), clientWidth - margin * 2);
		float controlLeft = margin;
		float displayLeft = sideBySide ? margin + columnWidth + gap : margin;
		int controlRow = 0;
		int displayRow = sideBySide ? 0 : 34;
		const int maxRowsPerColumn = 280;

		bool IsCurrent(ProgramCallGraphNode node)
		{
			return !string.IsNullOrWhiteSpace(focusedFunction) &&
				node.Name.Equals(focusedFunction, StringComparison.OrdinalIgnoreCase);
		}

		bool IsLocateHint(ProgramCallGraphNode node)
		{
			return !string.IsNullOrWhiteSpace(locateFunction) &&
				!node.Name.Equals(focusedFunction, StringComparison.OrdinalIgnoreCase) &&
				node.Name.Equals(locateFunction, StringComparison.OrdinalIgnoreCase);
		}

		string TreeKind(ProgramCallGraphNode node, int depth, bool hasChildren, bool expanded)
		{
			string currentPart = IsCurrent(node)
				? "Current"
				: (IsLocateHint(node) ? "Hint" : (depth >= 3 ? "Weak" : "Node"));
			if (!hasChildren)
			{
				return "tree" + currentPart;
			}
			return "tree" + currentPart + (expanded ? "Expanded" : "Collapsed");
		}

		FlowChartNode AddHeader(string id, string text, float left, ref int row)
		{
			FlowChartNode header = new FlowChartNode(
				id,
				text,
				new RectangleF(left, rowTop + row * rowHeight, columnWidth, rowHeight),
				0,
				"",
				"treeRoot",
				"",
				0);
			nodes.Add(header);
			row++;
			return header;
		}

		FlowChartNode? AddNode(
			string idPrefix,
			ProgramCallGraphNode node,
			int depth,
			string prefix,
			float left,
			ref int row,
			bool hasChildren,
			bool expanded)
		{
			if (row >= maxRowsPerColumn)
			{
				return null;
			}

			int level = hierarchyLevels.TryGetValue(node.Id, out int graphLevel) ? graphLevel : Math.Clamp(depth, 0, 6);
			string rowId = idPrefix + ":" + row.ToString(CultureInfo.InvariantCulture) + ":" + SanitizeChartId(node.Id);
			string text = prefix + BuildTreeFunctionLabel(node);
			FlowChartNode chartNode = new FlowChartNode(
				rowId,
				text,
				new RectangleF(left, rowTop + row * rowHeight, columnWidth, rowHeight),
				node.TraceId,
				node.Name,
				TreeKind(node, depth, hasChildren, expanded),
				"",
				level);
			nodes.Add(chartNode);
			row++;
			return chartNode;
		}

		void AddEdge(FlowChartNode? from, FlowChartNode? to)
		{
			if (from != null && to != null)
			{
				edges.Add(new FlowChartEdge(from.Id, to.Id));
			}
		}

		List<ProgramCallGraphNode> GetControlChildren(ProgramCallGraphNode node, int depth, HashSet<string> visited)
		{
			if (!childrenById.TryGetValue(node.Id, out List<ProgramCallGraphNode>? children))
			{
				return new List<ProgramCallGraphNode>();
			}

			int takeCount = depth <= 0 ? 96 : depth == 1 ? 72 : 48;
			return children
				.Where(child => !visited.Contains(child.Id))
				.Where(child => !IsProgramGraphNoiseNode(child))
				.Where(child => !child.Name.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
				.Where(child => depth <= 0 || !IsDisplayGraphNode(child))
				.Take(takeCount)
				.ToList();
		}

		List<ProgramCallGraphNode> GetDisplayChildren(ProgramCallGraphNode node, int depth, HashSet<string> visited)
		{
			if (!childrenById.TryGetValue(node.Id, out List<ProgramCallGraphNode>? children))
			{
				return new List<ProgramCallGraphNode>();
			}

			int takeCount = depth <= 1 ? 96 : 64;
			return children
				.Where(child => !visited.Contains(child.Id))
				.Where(child => !child.Name.Equals("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
				.Where(child => ShouldShowDisplayBranchNode(child, depth + 1, childrenById, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase)))
				.Take(takeCount)
				.ToList();
		}

		bool SubtreeContainsFocusedFunction(
			ProgramCallGraphNode node,
			int depth,
			HashSet<string> visited,
			Func<ProgramCallGraphNode, int, HashSet<string>, List<ProgramCallGraphNode>> childSelector)
		{
			if ((string.IsNullOrWhiteSpace(focusedFunction) && string.IsNullOrWhiteSpace(locateFunction)) || depth > 12 || !visited.Add(node.Id))
			{
				return false;
			}
			foreach (ProgramCallGraphNode child in childSelector(node, depth, visited))
			{
				if (child.Name.Equals(focusedFunction, StringComparison.OrdinalIgnoreCase) ||
					child.Name.Equals(locateFunction, StringComparison.OrdinalIgnoreCase) ||
					SubtreeContainsFocusedFunction(child, depth + 1, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase), childSelector))
				{
					return true;
				}
			}
			return false;
		}

		void AddTree(
			string idPrefix,
			ProgramCallGraphNode node,
			int depth,
			string ancestorPrefix,
			string connector,
			float left,
			ref int row,
			HashSet<string> visited,
			Func<ProgramCallGraphNode, int, HashSet<string>, List<ProgramCallGraphNode>> childSelector,
			FlowChartNode? parent)
		{
			if (row >= maxRowsPerColumn || depth > 12 || !visited.Add(node.Id))
			{
				return;
			}

			List<ProgramCallGraphNode> children = childSelector(node, depth, visited);
			bool hasChildren = children.Count > 0;
			bool expanded = hasChildren && (IsProgramTreeNodeExpanded(node, depth) ||
				SubtreeContainsFocusedFunction(node, depth, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase), childSelector));
			string displayPrefix = depth == 0 ? "" : ancestorPrefix + connector;
			FlowChartNode? current = AddNode(idPrefix, node, depth, displayPrefix, left, ref row, hasChildren, expanded);
			AddEdge(parent, current);

			if (!hasChildren || !expanded)
			{
				return;
			}

			string nextAncestorPrefix = depth == 0
				? "   "
				: ancestorPrefix + (connector == "└─ " ? "   " : "│  ");
			for (int i = 0; i < children.Count; i++)
			{
				string childConnector = i == children.Count - 1 ? "└─ " : "├─ ";
				AddTree(idPrefix, children[i], depth + 1, nextAncestorPrefix, childConnector, left, ref row, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase), childSelector, current);
			}
		}

		ProgramCallGraphNode? controlRoot = FindControlBusinessRoot(visibleGraphNodes);
		FlowChartNode controlHeader = AddHeader("control:header", "控制/业务链", controlLeft, ref controlRow);
		if (controlRoot != null)
		{
			AddTree("control", controlRoot, 0, "", "", controlLeft, ref controlRow, new HashSet<string>(StringComparer.OrdinalIgnoreCase), GetControlChildren, controlHeader);
		}
		else
		{
			foreach (ProgramCallGraphNode root in visibleGraphNodes
				.OrderByDescending(IsPreferredGraphRoot)
				.ThenByDescending(GetBusinessNodeScore)
				.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
				.Take(30))
			{
				AddTree("control", root, 0, "", "", controlLeft, ref controlRow, new HashSet<string>(StringComparer.OrdinalIgnoreCase), GetControlChildren, controlHeader);
			}
		}

		if (!sideBySide)
		{
			displayRow = controlRow + 2;
		}

		IReadOnlyList<ProgramCallGraphNode> displayRoots = FindDisplayRoots(visibleGraphNodes);
		if (displayRoots.Count > 0)
		{
			FlowChartNode displayHeader = AddHeader(
				"display:header",
				"显示输出集合",
				displayLeft,
				ref displayRow);
			foreach (ProgramCallGraphNode displayRoot in displayRoots.Take(80))
			{
				AddTree("display", displayRoot, 0, "", "", displayLeft, ref displayRow, new HashSet<string>(StringComparer.OrdinalIgnoreCase), GetDisplayChildren, displayHeader);
			}
		}

		if (nodes.Count == 0)
		{
			return (
				new[] { new FlowChartNode("empty", "未识别到业务调用树", new RectangleF(14, 20, 260, 28), 0, Kind: "treeRoot") },
				Array.Empty<FlowChartEdge>());
		}

		return (nodes, edges);
	}

	private static string BuildTreeFunctionLabel(ProgramCallGraphNode node)
	{
		string summary = ShortenTreeSummary(node.Summary);
		return string.IsNullOrWhiteSpace(summary)
			? node.Name
			: node.Name + "\n" + summary;
	}

	private static string ShortenTreeSummary(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		string clean = Regex.Replace(text.Trim(), @"\s+", " ");
		clean = Regex.Replace(clean, @"^(函数|业务|处理|执行)[:：]\s*", "", RegexOptions.IgnoreCase);
		return clean.Length <= 14 ? clean : clean[..14];
	}

	private static string ExtractFunctionNameFromTreeText(string text)
	{
		Match match = Regex.Match(text, @"[A-Za-z_][A-Za-z0-9_]*");
		return match.Success ? match.Value : "";
	}

	private static Dictionary<string, int> BuildCallHierarchyLevels(
		IReadOnlyList<ProgramCallGraphNode> graphNodes,
		IReadOnlyList<ProgramCallGraphEdge> graphEdges)
	{
		Dictionary<string, ProgramCallGraphNode> nodeById = graphNodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<string>> outgoing = graphEdges
			.Where(e => nodeById.ContainsKey(e.FromId) && nodeById.ContainsKey(e.ToId))
			.GroupBy(e => e.FromId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				g => g.Key,
				g => g.Select(e => e.ToId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
				StringComparer.OrdinalIgnoreCase);
		var levels = graphNodes.ToDictionary(
			n => n.Id,
			n => Math.Clamp(n.Level, 0, 6),
			StringComparer.OrdinalIgnoreCase);

		ProgramCallGraphNode? primaryRoot = FindPrimaryChartRoot(graphNodes);
		if (primaryRoot != null)
		{
			AssignHierarchy(primaryRoot.Id, 0, 4, levels, outgoing);
		}

		foreach (ProgramCallGraphNode root in graphNodes
			.Where(n => primaryRoot == null || !n.Id.Equals(primaryRoot.Id, StringComparison.OrdinalIgnoreCase))
			.Where(IsPreferredGraphRoot)
			.Where(n => outgoing.ContainsKey(n.Id))
			.OrderByDescending(n => n.Name.Contains("10ms", StringComparison.OrdinalIgnoreCase))
			.ThenByDescending(n => n.Outgoing)
			.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
		{
			AssignHierarchy(root.Id, 5, 6, levels, outgoing);
		}

		foreach (ProgramCallGraphNode root in graphNodes
			.Where(n => primaryRoot == null || !n.Id.Equals(primaryRoot.Id, StringComparison.OrdinalIgnoreCase))
			.Where(n => !IsPreferredGraphRoot(n))
			.Where(n => outgoing.ContainsKey(n.Id))
			.OrderBy(n => levels.TryGetValue(n.Id, out int level) ? level : 9)
			.ThenByDescending(n => n.Outgoing)
			.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
		{
			if (!levels.TryGetValue(root.Id, out int rootLevel) || rootLevel < 5)
			{
				continue;
			}
			AssignHierarchy(root.Id, 5, 6, levels, outgoing);
		}

		return levels;
	}

	private static ProgramCallGraphNode? FindPrimaryChartRoot(IReadOnlyList<ProgramCallGraphNode> graphNodes)
	{
		return graphNodes
			.Where(IsPreferredGraphRoot)
			.OrderByDescending(n => ScoreOfflineRootCandidate(n, includeAnalysisSeeds: false))
			.ThenBy(n => n.Level)
			.ThenByDescending(n => n.Outgoing)
			.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault()
			?? graphNodes
				.OrderBy(n => n.Level)
				.ThenByDescending(n => n.Outgoing)
				.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
	}

	private static void AssignHierarchy(
		string rootId,
		int rootLevel,
		int maxLevel,
		Dictionary<string, int> levels,
		Dictionary<string, List<string>> outgoing)
	{
		var queue = new Queue<(string Id, int Level)>();
		if (!levels.TryGetValue(rootId, out int oldLevel) || rootLevel < oldLevel)
		{
			levels[rootId] = rootLevel;
			queue.Enqueue((rootId, rootLevel));
		}

		while (queue.Count > 0)
		{
			(string id, int level) = queue.Dequeue();
			if (!outgoing.TryGetValue(id, out List<string>? children) || level >= maxLevel)
			{
				continue;
			}

			foreach (string childId in children)
			{
				int childLevel = Math.Min(maxLevel, level + 1);
				if (!levels.TryGetValue(childId, out int currentLevel) || childLevel < currentLevel)
				{
					levels[childId] = childLevel;
					queue.Enqueue((childId, childLevel));
				}
			}
		}
	}

	private static Dictionary<string, int> BuildFirstEdgeOrder(IReadOnlyList<ProgramCallGraphEdge> graphEdges)
	{
		var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < graphEdges.Count; i++)
		{
			order.TryAdd(graphEdges[i].ToId, i);
			order.TryAdd(graphEdges[i].FromId, i);
		}
		return order;
	}

	private static bool IsPreferredGraphRoot(ProgramCallGraphNode node)
	{
		return IsPreferredGraphRootName(node.Name) ||
			node.Kind.Equals("period10", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsPreferredGraphRootName(string name)
	{
		return name.Contains("10ms", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Loop", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Logic", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Task", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Tick", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Cycle", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Control", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Ctrl", StringComparison.OrdinalIgnoreCase);
	}

	private static int ChartColumnIndex(int hierarchyLevel)
	{
		return hierarchyLevel switch
		{
			0 => 0,
			1 => 1,
			2 => 2,
			3 => 3,
			4 => 4,
			5 => 5,
			6 => 6,
			_ => 7
		};
	}

	private static string ChartGroupLabel(int hierarchyLevel)
	{
		return hierarchyLevel switch
		{
			0 => "周期入口",
			1 => "一级业务",
			2 => "二级业务",
			3 => "三级业务",
			4 => "末端动作",
			5 => "其他周期",
			6 => "其他业务",
			_ => "其他业务"
		};
	}

	private static string ChartNodeRole(int hierarchyLevel)
	{
		return hierarchyLevel switch
		{
			0 => "入口",
			1 => "一级",
			2 => "二级",
			3 => "三级",
			4 => "末端",
			5 => "其他周期",
			6 => "其他",
			_ => "其他"
		};
	}

	private static string BuildCallEdgeLabel(ProgramCallGraphEdge edge, Dictionary<string, int> hierarchyLevels)
	{
		if (!hierarchyLevels.TryGetValue(edge.FromId, out int fromLevel) ||
			!hierarchyLevels.TryGetValue(edge.ToId, out int toLevel))
		{
			return "调用";
		}
		if (toLevel == fromLevel + 1)
		{
			return toLevel switch
			{
				1 => "进入一级",
				2 => "进入二级",
				3 => "进入三级",
				4 => "进入末端",
				6 => "其他调用",
				_ => "调用"
			};
		}
		if (toLevel == fromLevel)
		{
			return "关联调用";
		}
		return "调用";
	}

	private static bool LooksLikeMainLoopProject(IReadOnlyList<ProgramFrameworkStep> steps)
	{
		string[] names = steps.Select(s => s.FunctionName).ToArray();
		return names.Contains("App_JiTing", StringComparer.OrdinalIgnoreCase) ||
			names.Contains("app_Ctrl", StringComparer.OrdinalIgnoreCase) ||
			names.Contains("Usr_Can_Rcv", StringComparer.OrdinalIgnoreCase);
	}

	private static (IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) BuildMainLoopFlowChart(IReadOnlyList<ProgramFrameworkStep> steps)
	{
		var trace = steps
			.GroupBy(s => s.FunctionName, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First().TraceId, StringComparer.OrdinalIgnoreCase);
		ushort T(string name, ushort fallback)
		{
			return trace.TryGetValue(name, out ushort value) ? value : fallback;
		}
		FlowChartNode N(string id, string text, float x, float y, ushort traceId = 0, float w = 158, float h = 46, string functionName = "", string kind = "normal")
		{
			return new FlowChartNode(id, text, new RectangleF(x, y, w, h), traceId, functionName, kind);
		}

		var nodes = new List<FlowChartNode>
		{
			N("A", "上电启动", 420, 16, kind: "main"),
			N("B", "SystemInit", 420, 78, kind: "main"),
			N("C", "Sys_Usr_Init\nIO/CAN/UART/ADC/PWM", 420, 140, 0, 188, 50, kind: "main"),
			N("D", "Can1_RcvID_Cfg\n业务 CAN + 监控 ID", 420, 206, 0, 188, 50, kind: "can"),
			N("E", "while(1)\n主循环", 435, 276, 0, 158, 50, kind: "main"),
			N("F", "CAN 异常诊断\nCan_ask_rx", 28, 374, T("Can_ask_rx", 0x2140), functionName: "Can_ask_rx", kind: "can"),
			N("G", "1ms 标志\ngT0Flg", 230, 374, kind: "timer"),
			N("H", "急停/遥控/超时\n条件判断", 230, 442, 0, 168, 52),
			N("I", "App_JiTing\n急停处理", 118, 526, T("App_JiTing", 0x2101), functionName: "App_JiTing"),
			N("J", "app_Logic\n逻辑联锁", 118, 594, T("app_Logic", 0x2103), functionName: "app_Logic"),
			N("K", "app_Ctrl\n主业务控制", 332, 526, T("app_Ctrl", 0x2102), functionName: "app_Ctrl"),
			N("L", "app_Logic\n逻辑联锁", 332, 594, T("app_Logic", 0x2103), functionName: "app_Logic"),
			N("M", "app_ctrl_dfs\nDFS 控制", 332, 662, T("app_ctrl_dfs", 0x2104), functionName: "app_ctrl_dfs"),
			N("N", "App_PWM\nPWM 输出", 332, 730, T("App_PWM", 0x2105), functionName: "App_PWM"),
			N("O", "10ms 标志\ngT010msFlg", 540, 374, kind: "period10"),
			N("P", "Usr_Can_Rcv\n接收 CAN + 变量监控", 540, 442, T("Usr_Can_Rcv", 0x2110), 188, 52, "Usr_Can_Rcv", "can"),
			N("Q", "Usr_Can_Send\n发送业务 CAN", 540, 526, T("Usr_Can_Send", 0x2111), functionName: "Usr_Can_Send", kind: "can"),
			N("R", "Can_Rcv_Dly\n接收超时倒计时", 540, 594, T("Can_Rcv_Dly", 0x2112), 178, 50, "Can_Rcv_Dly", "period10"),
			N("S", "WDT_Feed / WDTFeed\n喂狗", 540, 662, T("WDT_Feed", 0x2113), 178, 50, "WDT_Feed"),
			N("T", "CAN1RxDone\n中断接收标志", 778, 374, kind: "can"),
			N("U", "Can_Prog_Rcv\nCAN 中断缓存处理", 778, 442, T("Can_Prog_Rcv", 0x2114), 178, 52, "Can_Prog_Rcv", "can"),
			N("V", "UART 接收", 28, 662),
			N("W", "Uart0_WL_Rcv\n无线遥控接收", 28, 730, T("Uart0_WL_Rcv", 0x2120), 178, 50, "Uart0_WL_Rcv"),
			N("X", "Uart0_WL_Send\n回发遥控器", 28, 798, T("Uart0_WL_Send", 0x2121), 178, 50, "Uart0_WL_Send"),
			N("Y", "秒标志\ngT0SFlg", 778, 594),
			N("Z", "Save_Info_Prog / wl_reset\n保存参数/无线恢复", 778, 662, T("Save_Info_Prog", 0x2130), 198, 52, "Save_Info_Prog")
		};
		var edges = new List<FlowChartEdge>
		{
			new("A", "B"), new("B", "C"), new("C", "D"), new("D", "E"),
			new("E", "F"), new("E", "G"), new("G", "H"),
			new("H", "I", "是"), new("I", "J"),
			new("H", "K", "否"), new("K", "L"), new("L", "M"), new("M", "N"),
			new("E", "O"), new("O", "P"), new("P", "Q"), new("Q", "R"), new("R", "S"),
			new("E", "T"), new("T", "U"),
			new("E", "V"), new("V", "W"), new("W", "X"),
			new("E", "Y"), new("Y", "Z")
		};
		nodes = LayoutMainLoopNodesVertically(nodes);
		return (nodes, edges);
	}

	private static List<FlowChartNode> LayoutMainLoopNodesVertically(List<FlowChartNode> nodes)
	{
		var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			["A"] = 0, ["B"] = 0, ["C"] = 0, ["D"] = 0, ["E"] = 0,
			["F"] = 1, ["G"] = 1, ["O"] = 1, ["T"] = 1, ["V"] = 1, ["Y"] = 1,
			["H"] = 2, ["P"] = 2, ["U"] = 2, ["W"] = 2, ["Z"] = 2,
			["I"] = 3, ["K"] = 3, ["Q"] = 3, ["X"] = 3,
			["J"] = 4, ["L"] = 4, ["R"] = 4,
			["M"] = 5, ["S"] = 5,
			["N"] = 6
		};
		const float baseX = 36f;
		const float topY = 26f;
		const float indentGap = 24f;
		const float rowGap = 86f;
		const float baseWidth = 430f;
		var result = new List<FlowChartNode>(nodes.Count);
		var seenGroups = new HashSet<int>();
		for (int i = 0; i < nodes.Count; i++)
		{
			FlowChartNode node = nodes[i];
			int level = levels.TryGetValue(node.Id, out int foundLevel) ? foundLevel : 6;
			float indent = Math.Min(level, 4) * indentGap;
			float width = Math.Max(300f, baseWidth - indent);
			float height = Math.Max(58f, node.Bounds.Height);
			string group = seenGroups.Add(level) ? ChartGroupLabel(level) : "";
			result.Add(node with
			{
				Bounds = new RectangleF(baseX + indent, topY + i * rowGap, width, height),
				Group = group,
				HierarchyLevel = level
			});
		}
		return result;
	}

	private static (IReadOnlyList<FlowChartNode> nodes, IReadOnlyList<FlowChartEdge> edges) BuildGenericFlowChart(IReadOnlyList<ProgramFrameworkStep> steps)
	{
		if (steps.Count == 0)
		{
			return (
				new[] { new FlowChartNode("empty", "未识别到业务流程\n选择工程目录后自动分析", new RectangleF(24, 24, 230, 72), 0) },
				Array.Empty<FlowChartEdge>());
		}

		var nodes = new List<FlowChartNode>();
		var edges = new List<FlowChartEdge>();
		int count = Math.Min(steps.Count, 24);
		for (int i = 0; i < count; i++)
		{
			ProgramFrameworkStep step = steps[i];
			string id = "N" + i;
			nodes.Add(new FlowChartNode(
				id,
				step.Name + "\n" + step.Detail,
				new RectangleF(80, 24 + i * 72, 230, 52),
				step.TraceId,
				step.FunctionName,
				ClassifyFlowNodeKind(step.FunctionName, step.Name, step.Detail)));
			if (i > 0)
			{
				edges.Add(new FlowChartEdge("N" + (i - 1), id));
			}
		}
		return (nodes, edges);
	}

	private static string ClassifyFlowNodeKind(string functionName, string name, string detail)
	{
		string combined = functionName + " " + name + " " + detail;
		if (combined.Contains("main", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("while", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("SystemInit", StringComparison.OrdinalIgnoreCase))
		{
			return "main";
		}
		if (combined.Contains("disp", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("display", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("lcd", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("LCD_WR_Data2B", StringComparison.OrdinalIgnoreCase))
		{
			return "disp";
		}
		if (combined.Contains("10ms", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("T010ms", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("tick", StringComparison.OrdinalIgnoreCase) ||
			combined.Contains("cycle", StringComparison.OrdinalIgnoreCase))
		{
			return "period10";
		}
		if (combined.Contains("CAN", StringComparison.OrdinalIgnoreCase))
		{
			return "can";
		}
		return "normal";
	}

	private async Task InstallFirmwareAgentAsync(Button installButton)
	{
		string text = GetWorkDirectoryFromUi();
		if (text.Length == 0 || !Directory.Exists(text))
		{
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
			{
				Description = "请选择工作目录",
				UseDescriptionForTitle = true
			};
			if (folderBrowserDialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			text = folderBrowserDialog.SelectedPath;
			SetWorkDirectory(text, loadMap: true);
		}
		string text2;
		try
		{
			text2 = BundledFirmwareAgent.WriteTempCopy();
		}
		catch (Exception ex)
		{
			Log("没有找到内置固件文件：" + ex.Message);
			return;
		}
		try
		{
			installButton.Enabled = false;
			installButton.Text = "安装中";
			ApplyButtonStyle(installButton, "working");
			Log("开始同步固件：" + text);
			FirmwareInstallResult firmwareInstallResult = await Task.Run(() => FirmwareInstaller.Install(text, text2));
			foreach (string message in firmwareInstallResult.Messages)
			{
				Log(message);
			}
			LoadLatestMapFromDirectory(text);
			RefreshProgramGraphPanel(text);
			UpdateFirmwareVersionDisplay();
			if (!firmwareInstallResult.Success)
			{
				Log("固件安装未完全完成，请查看上面的提示。");
			}
		}
		catch (Exception ex)
		{
			Log("同步固件失败：" + ex.Message);
		}
		finally
		{
			installButton.Text = "刷新";
			installButton.Enabled = true;
			ApplyButtonStyle(installButton, "firmware");
		}
	}

	private void CancelActiveDownload(bool waitForExit = false)
	{
		CancellationTokenSource? downloadCts = _downloadCts;
		if (downloadCts == null)
		{
			return;
		}

		try
		{
			downloadCts.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}

		Task? downloadTask = _downloadTask;
		if (waitForExit && downloadTask != null && !downloadTask.IsCompleted)
		{
			try
			{
				downloadTask.Wait(1200);
			}
			catch (AggregateException ex) when (ex.InnerExceptions.All((Exception x) => x is OperationCanceledException || x is TaskCanceledException))
			{
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	private async Task DownloadFirmwareAsync(Button downloadButton)
	{
		if (_downloadTask != null && !_downloadTask.IsCompleted)
		{
			Log("下载未开始：已有下载任务正在进行。");
			return;
		}

		string text = GetWorkDirectoryFromUi();
		if (text.Length == 0 || !Directory.Exists(text))
		{
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
			{
				Description = "请选择工作目录",
				UseDescriptionForTitle = true
			};
			if (folderBrowserDialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			text = folderBrowserDialog.SelectedPath;
			SetWorkDirectory(text, loadMap: true);
		}

		if (!IsWorkFirmwareVersionCurrent(text))
		{
			UpdateFirmwareVersionDisplay();
			Log("下载未开始：当前工程固件或 bin 未确认，请先刷新。");
			return;
		}

		CancellationTokenSource downloadCts = new CancellationTokenSource();
		CancellationToken token = downloadCts.Token;
		Task? workerTask = null;
		ICanAdapter? reusableAdapter = null;
		_downloadCts = downloadCts;

		try
		{
			downloadButton.Enabled = false;
			downloadButton.Text = "下载中";
			ApplyButtonStyle(downloadButton, "downloadBusy");
			Log("开始读取下载文件：" + text);
			StopPolling(waitForExit: true);
			reusableAdapter = _adapter;
			string preferredDownloadAdapter = reusableAdapter?.Name ?? _preferredAdapterName;
			_adapter = null;
			_monitorSessionOpen = false;
			ResetCanHealthCounters();
			_connectButton.Text = "连接";
			ApplyButtonStyle(_connectButton, "connect");
			_statusLabel.Text = "下载中";
			_statusLabel.BackColor = _accent;

			FirmwareBinaryResult binaryResult = await Task.Run(() => FirmwareInstaller.BuildBinaryForDownload(text), token);
			token.ThrowIfCancellationRequested();
			foreach (string message in binaryResult.Messages)
			{
				Log(message);
			}
			if (!binaryResult.Success)
			{
				Log("下载未开始：没有找到有效 bin 文件。");
				return;
			}

			_statusLabel.Text = "0%";
			IProgress<string> progress = new Progress<string>(UpdateDownloadProgress);
			ICanAdapter? firstDownloadAdapter = reusableAdapter;
			reusableAdapter = null;
			workerTask = Task.Run(() =>
			{
				string? deprioritizedAdapterName = null;
				Exception? lastError = null;
				int maxAttempts = firstDownloadAdapter == null ? 2 : 3;
				for (int attempt = 1; attempt <= maxAttempts; attempt++)
				{
					token.ThrowIfCancellationRequested();
					ICanAdapter? adapter = null;
					if (firstDownloadAdapter != null)
					{
						adapter = firstDownloadAdapter;
						firstDownloadAdapter = null;
						CanAdapterDiagnostics.Write("Download reusing opened CAN adapter: " + adapter.Name);
					}
					else
					{
						string? preferredName = attempt == 1 ? preferredDownloadAdapter : null;
						if (!CanAdapterFactory.TryOpenAvailable(out adapter, out string message, deprioritizedAdapterName, preferredName) || adapter == null)
						{
							throw new InvalidOperationException("未连接：" + message);
						}
					}

					using (adapter)
					{
						try
						{
							_preferredAdapterName = adapter.Name;
							CanFirmwareDownloader.Download(adapter, binaryResult.BinPath, text, progress, token);
							return;
						}
						catch (TimeoutException ex) when (attempt < 2 && !token.IsCancellationRequested)
						{
							lastError = ex;
							deprioritizedAdapterName = adapter.Name;
							CanAdapterDiagnostics.Write("Download timeout on " + adapter.Name + ", retrying another adapter.");
						}
					}
				}

				throw lastError ?? new TimeoutException("下载超时。");
			});
			_downloadTask = workerTask;
			await workerTask;
			token.ThrowIfCancellationRequested();
			_statusLabel.Text = "100%";
			_statusLabel.BackColor = _accent;
			Log("下载完成。");
			WriteConnectionState("下载完成");
		}
		catch (OperationCanceledException)
		{
			Log("下载已取消。");
			WriteConnectionState("下载取消");
			if (!IsDisposed && !Disposing)
			{
				_statusLabel.Text = "未连接";
				_statusLabel.BackColor = _statusOff;
			}
		}
		catch (Exception ex)
		{
			_statusLabel.Text = "未连接";
			_statusLabel.BackColor = _statusOff;
			Log("下载失败：" + ex.Message);
			WriteConnectionState("下载失败：" + ex);
		}
		finally
		{
			reusableAdapter?.Dispose();
			if (ReferenceEquals(_downloadCts, downloadCts))
			{
				_downloadCts = null;
			}
			if (ReferenceEquals(_downloadTask, workerTask))
			{
				_downloadTask = null;
			}
			downloadCts.Dispose();
			if (!IsDisposed && !Disposing && _adapter == null)
			{
				_connectButton.Text = "连接";
			}
			if (!IsDisposed && !Disposing)
			{
				UpdateDownloadButtonState();
			}
		}
	}

	private void UpdateDownloadProgress(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		string text = message.Trim();
		if (!text.StartsWith("下载进度：", StringComparison.Ordinal))
		{
			return;
		}

		string percentText = text.Substring("下载进度：".Length).Trim();
		if (_statusLabel != null)
		{
			_statusLabel.Text = percentText;
			_statusLabel.BackColor = _accent;
		}
		Log(text);
	}

	private static string? FindLatestMapFile(string root)
	{
		return Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
	}

	private static string? FindLatestAxfFile(string root)
	{
		return Directory.EnumerateFiles(root, "*.axf", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
	}

	private MonitorProfile? TryReadProfileQuietly(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				return JsonSerializer.Deserialize<MonitorProfile>(File.ReadAllText(path));
			}
		}
		catch
		{
		}
		return null;
	}

	private MonitorProfile BuildProfileSnapshot(MonitorProfile? existingProfile)
	{
		bool canSaveLayout = CanSaveLayoutToProfile();
		int currentDpi = Math.Max(96, _currentUiDpi);
		int layoutDpi = canSaveLayout ? currentDpi : GetProfileDpi(existingProfile);
		List<int> columnWidths = canSaveLayout
			? GetMonitorColumnWidths()
			: (existingProfile?.MonitorColumnWidths?.ToList() ?? _savedMonitorColumnWidths.ToList());
		string workDirectory = _workDirectory;
		string mapPath = _mapFilePath;
		if (workDirectory.Length == 0 && !string.IsNullOrWhiteSpace(existingProfile?.WorkDirectory))
		{
			workDirectory = existingProfile.WorkDirectory;
		}
		if (mapPath.Length == 0 && !string.IsNullOrWhiteSpace(existingProfile?.MapPath))
		{
			mapPath = existingProfile.MapPath;
		}

		return new MonitorProfile
		{
			WorkDirectory = workDirectory,
			MapPath = mapPath,
			Adapter = _adapter?.Name ?? "",
			ThemeName = _themeName,
			RequestId = 2032u,
			ResponseId = 2033u,
			PollIntervalMs = _targetCycleMs,
			ShowHexValue = _showHexValue,
			LeftPanelWidth = canSaveLayout ? _mainSplit.SplitterDistance : (existingProfile?.LeftPanelWidth > 0 ? existingProfile.LeftPanelWidth : _savedLeftPanelWidth),
			MonitorPanelWidth = canSaveLayout && _rightSplit != null ? _rightSplit.SplitterDistance : (existingProfile?.MonitorPanelWidth > 0 ? existingProfile.MonitorPanelWidth : _savedMonitorPanelWidth),
			UiDpi = layoutDpi,
			FunctionCodeFontSize = _functionCodeFontSize,
			ProgramTreeFontSize = _programTreeFontSize,
			MonitorColumnWidths = columnWidths,
			OfflineRootFunctions = GetConfiguredOfflineRootNames().ToList(),
			Variables = BuildProfileVariables()
		};
	}

	private void LoadDefaultProfile()
	{
		if (File.Exists(_defaultProfilePath))
		{
			LoadProfile(_defaultProfilePath);
			if (_workDirectory.Length == 0)
			{
				_profileLoaded = true;
				TryLoadStartupWorkDirectory();
			}
			return;
		}
		_profileLoaded = true;
		TryLoadStartupWorkDirectory();
	}

	private void TryLoadStartupWorkDirectory()
	{
		foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
		{
			string directory = arg.Trim('"', ' ');
			if (Directory.Exists(directory))
			{
				SetWorkDirectory(directory, loadMap: true);
				return;
			}
		}
		string workdirFile = Path.Combine(AppContext.BaseDirectory, "workdir.txt");
		if (!File.Exists(workdirFile))
		{
			return;
		}
		string text = File.ReadLines(workdirFile).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim('"', ' ') ?? "";
		if (text.Length == 0)
		{
			return;
		}
		if (!Path.IsPathRooted(text))
		{
			text = Path.Combine(AppContext.BaseDirectory, text);
		}
		if (Directory.Exists(text))
		{
			SetWorkDirectory(text, loadMap: true);
		}
	}

	private void SaveProfile(string path)
	{
		MonitorProfile value = BuildProfileSnapshot(TryReadProfileQuietly(path) ?? TryReadProfileQuietly(_defaultProfilePath));
		string? targetDirectory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(targetDirectory))
		{
			Directory.CreateDirectory(targetDirectory);
		}
		File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
		string? defaultDirectory = Path.GetDirectoryName(_defaultProfilePath);
		if (!string.IsNullOrWhiteSpace(defaultDirectory))
		{
			Directory.CreateDirectory(defaultDirectory);
		}
		File.WriteAllText(_defaultProfilePath, JsonSerializer.Serialize(value, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
		Log("变量列表已保存：" + path);
	}

	private void SaveDefaultProfileQuietly()
	{
		try
		{
			if (!_profileLoaded || _loadingProfile)
			{
				return;
			}
			MonitorProfile value = BuildProfileSnapshot(TryReadProfileQuietly(_defaultProfilePath));
			string? defaultDirectory = Path.GetDirectoryName(_defaultProfilePath);
			if (!string.IsNullOrWhiteSpace(defaultDirectory))
			{
				Directory.CreateDirectory(defaultDirectory);
			}
			File.WriteAllText(_defaultProfilePath, JsonSerializer.Serialize(value, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}

	private List<WatchItem> BuildProfileVariables()
	{
		return _watchItems.Select(CloneWatchItemForProfile).ToList();
	}

	private WatchItem CloneWatchItemForProfile(WatchItem item)
	{
		var clone = new WatchItem
		{
			Enabled = item.Enabled,
			Name = item.Name,
			Address = item.Address,
			Size = item.Size,
			TotalSize = item.TotalSize,
			TypeName = item.TypeName,
			IsExpandable = item.IsExpandable,
			IsChild = item.IsChild,
			ParentName = item.ParentName,
			ExpandMode = item.ExpandMode,
			ValueDec = item.ValueDec,
			ValueHex = item.ValueHex,
			RawValue = item.RawValue,
			LastUpdate = item.LastUpdate,
			Status = "待读取"
		};
		clone.DisplayValue = FormatValue(clone);
		return clone;
	}

	private void LoadProfile(string path)
	{
		bool loadedProfile = false;
		try
		{
			_loadingProfile = true;
			MonitorProfile monitorProfile = JsonSerializer.Deserialize<MonitorProfile>(File.ReadAllText(path));
			if (monitorProfile == null)
			{
				return;
			}
			loadedProfile = true;
			if (!string.IsNullOrWhiteSpace(monitorProfile.ThemeName))
			{
				ApplyTheme(monitorProfile.ThemeName);
				if (_themeBox != null)
				{
					_themeBox.SelectedItem = _themeName;
				}
				UpdateFirmwareVersionDisplay();
			}
			string text = monitorProfile.WorkDirectory;
			if (text.Length == 0 && File.Exists(monitorProfile.MapPath))
			{
				text = Path.GetDirectoryName(monitorProfile.MapPath) ?? "";
			}
			if (text.Length > 0 && Directory.Exists(text))
			{
				SetWorkDirectory(text, loadMap: false);
			}
			_intervalBox.Value = Math.Clamp(monitorProfile.PollIntervalMs, (int)_intervalBox.Minimum, (int)_intervalBox.Maximum);
			_targetCycleMs = (int)_intervalBox.Value;
			_showHexValue = monitorProfile.ShowHexValue;
			_offlineRootSelectionText = string.Join(", ", monitorProfile.OfflineRootFunctions
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Distinct(StringComparer.OrdinalIgnoreCase));
			ApplyOfflineRootSelectionToUi();
			_functionCodeFontSize = Math.Clamp(monitorProfile.FunctionCodeFontSize, 8f, 22f);
			_programTreeFontSize = Math.Clamp(monitorProfile.ProgramTreeFontSize <= 0 ? 15f : monitorProfile.ProgramTreeFontSize, 10f, 22f);
			if (_functionCodeBox != null)
			{
				_functionCodeBox.Font = new Font("Consolas", _functionCodeFontSize);
			}
			if (_dataCodeBox != null)
			{
				_dataCodeBox.Font = new Font("Consolas", _functionCodeFontSize);
			}
			if (_flowChart != null)
			{
				_flowChart.TreeFontSize = _programTreeFontSize;
			}
			int profileDpi = GetProfileDpi(monitorProfile);
			_savedLeftPanelWidth = ScaleProfileLayoutValue(monitorProfile.LeftPanelWidth, profileDpi);
			_savedMonitorPanelWidth = ScaleProfileLayoutValue(monitorProfile.MonitorPanelWidth, profileDpi);
			_savedMonitorColumnWidths = monitorProfile.MonitorColumnWidths?
				.Select(width => ScaleProfileLayoutValue(width, profileDpi))
				.ToList() ?? new List<int>();
			ApplySavedSplitterDistances();
			ApplySavedMonitorColumnWidths();
			if (_grid != null)
			{
				DataGridViewColumn dataGridViewColumn = _grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault((DataGridViewColumn x) => x.DataPropertyName == "DisplayValue");
				if (dataGridViewColumn != null)
				{
					dataGridViewColumn.HeaderText = (_showHexValue ? "值(16)" : "值(10)");
				}
			}
			UpdateValueFormatButton();
			_watchItems.Clear();
			foreach (WatchItem item in monitorProfile.Variables)
			{
				ResetTransientWatchState(item);
				_watchItems.Add(item);
			}
			UpdateCycleEstimate();
			if (File.Exists(monitorProfile.MapPath))
			{
				_mapFilePath = monitorProfile.MapPath;
				LoadMapFile(monitorProfile.MapPath);
			}
			else if (_workDirectory.Length > 0 && Directory.Exists(_workDirectory))
			{
				LoadLatestMapFromDirectory(_workDirectory);
			}
			if (_workDirectory.Length > 0 && Directory.Exists(_workDirectory))
			{
				RefreshProgramGraphPanel(_workDirectory);
			}
			Log("变量列表已加载：" + path);
		}
		catch (Exception ex)
		{
			Log("加载变量列表失败：" + ex.Message);
		}
		finally
		{
			_loadingProfile = false;
			_profileLoaded = true;
			if (loadedProfile)
			{
				SaveDefaultProfileQuietly();
			}
		}
	}

	private void Log(string text)
	{
		string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}";
		WriteDiagnosticLog(line);
		if (_logBox == null || _logBox.IsDisposed)
		{
			return;
		}

		void Append()
		{
			if (_logBox == null || _logBox.IsDisposed || !_logBox.Visible)
			{
				return;
			}
			_logBox.AppendText(line + Environment.NewLine);
			_uiLogLineCount++;
			TrimUiLogIfNeeded();
		}

		try
		{
			if (_logBox.InvokeRequired)
			{
				BeginInvoke((Action)Append);
			}
			else
			{
				Append();
			}
		}
		catch
		{
		}
	}

	private void LogPerformance(string text, Stopwatch stopwatch)
	{
		stopwatch.Stop();
		Log($"{text}，耗时 {stopwatch.ElapsedMilliseconds} ms，内存 {GetProcessMemoryText()}。");
	}

	private void LogPollPerformance(string mode, PollCycleStats stats, long elapsedMs, int targetCycleMs)
	{
		DateTime now = DateTime.UtcNow;
		if (mode.StartsWith("offline", StringComparison.OrdinalIgnoreCase))
		{
			if ((now - _lastOfflinePerfLogUtc).TotalMilliseconds < OfflinePerfLogIntervalMs)
			{
				return;
			}

			_lastOfflinePerfLogUtc = now;
			string displayMode = mode switch
			{
				"offline-loop" => "循环",
				"offline-step" => "单步",
				"offline-paused" => "暂停",
				_ => "离线"
			};
			WriteDiagnosticLog(
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 离线性能：{displayMode}，目标 {targetCycleMs} ms，耗时 {elapsedMs} ms，变量 {stats.Requested}，执行 {stats.Sent} 拍，刷新 {stats.Success}，跳过 {stats.Skipped}。");
			return;
		}

		if ((now - _lastPollPerfLogUtc).TotalMilliseconds < PollPerfLogIntervalMs)
		{
			return;
		}

		_lastPollPerfLogUtc = now;
		WriteDiagnosticLog(
			$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 监控性能：{mode}，目标 {targetCycleMs} ms，耗时 {elapsedMs} ms，变量 {stats.Requested}，发包 {stats.Sent}，成功 {stats.Success}，超时 {stats.Timeout}，跳过 {stats.Skipped}。");
	}

	private static string GetProcessMemoryText()
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			return (process.WorkingSet64 / 1024d / 1024d).ToString("F1") + " MB";
		}
		catch
		{
			return "未知";
		}
	}

	private void WriteDiagnosticLog(string line)
	{
		try
		{
			lock (_diagnosticLogLock)
			{
				string? dir = Path.GetDirectoryName(_diagnosticLogPath);
				if (!string.IsNullOrWhiteSpace(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.AppendAllText(_diagnosticLogPath, line + Environment.NewLine, Encoding.UTF8);
				TrimDiagnosticLogIfNeeded();
			}
		}
		catch (Exception ex)
		{
			LogPollLoopError(ex);
		}
	}

	private void TrimDiagnosticLogIfNeeded()
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastLogTrimUtc).TotalSeconds < 10)
		{
			return;
		}
		_lastLogTrimUtc = now;
		FileInfo fileInfo = new FileInfo(_diagnosticLogPath);
		if (!fileInfo.Exists || fileInfo.Length <= DiagnosticLogMaxBytes)
		{
			return;
		}

		string[] lines = File.ReadAllLines(_diagnosticLogPath, Encoding.UTF8);
		IEnumerable<string> tail = lines.Length > DiagnosticLogKeepLines
			? lines.Skip(lines.Length - DiagnosticLogKeepLines)
			: lines;
		File.WriteAllLines(_diagnosticLogPath, tail, Encoding.UTF8);
	}

	private void TrimUiLogIfNeeded()
	{
		if (_logBox == null || _uiLogLineCount <= UiLogMaxLines)
		{
			return;
		}

		string[] lines = _logBox.Lines;
		if (lines.Length <= UiLogMaxLines)
		{
			_uiLogLineCount = lines.Length;
			return;
		}

		string[] tail = lines.Skip(Math.Max(0, lines.Length - UiLogMaxLines)).ToArray();
		_logBox.Lines = tail;
		_uiLogLineCount = tail.Length;
		_logBox.SelectionStart = _logBox.TextLength;
		_logBox.ScrollToCaret();
	}

	private Panel CardPanel()
	{
		return new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = _panel,
			Padding = new Padding(Ui(1)),
			Margin = new Padding(0, 0, 0, Ui(10)),
			Tag = "card"
		};
	}

	private Button AccentButton(string text)
	{
		return new Button
		{
			Text = text,
			AutoSize = false,
			Size = new Size(Ui(92), Ui(30)),
			BackColor = _accent,
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
			Tag = "accent"
		};
	}

	private sealed class CodeValueOverlay : Control
	{
		private IReadOnlyList<CodeValueOverlayRow> _rows = Array.Empty<CodeValueOverlayRow>();
		private int _rowHeight = 18;

		public CodeValueOverlay()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
			BackColor = Color.Transparent;
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams cp = base.CreateParams;
				cp.ExStyle |= 0x20;
				return cp;
			}
		}

		public void SetRows(
			IReadOnlyList<CodeValueOverlayRow> rows,
			Color backColor,
			Color foreColor,
			Color valueBack,
			Color staleBack,
			Color trueBack,
			int rowHeight)
		{
			List<Rectangle> oldRects = _rows
				.Select(GetRowRectangle)
				.Where(rect => !rect.IsEmpty)
				.ToList();
			_rows = rows;
			BackColor = Color.Transparent;
			ForeColor = foreColor;
			_rowHeight = Math.Max(16, rowHeight);
			UpdateClippingRegion();
			foreach (Rectangle rect in oldRects)
			{
				Parent?.Invalidate(rect, true);
			}
			foreach (Rectangle rect in _rows.Select(GetRowRectangle).Where(rect => !rect.IsEmpty))
			{
				Parent?.Invalidate(rect, true);
			}
			Invalidate();
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			UpdateClippingRegion();
		}

		private void UpdateClippingRegion()
		{
			Region? nextRegion = null;
			foreach (CodeValueOverlayRow row in _rows)
			{
				Rectangle rect = GetRowRectangle(row);
				if (rect.IsEmpty)
				{
					continue;
				}

				if (nextRegion == null)
				{
					nextRegion = new Region(rect);
				}
				else
				{
					nextRegion.Union(rect);
				}
			}

			nextRegion ??= new Region(Rectangle.Empty);
			Region? oldRegion = Region;
			Region = nextRegion;
			oldRegion?.Dispose();
		}

		private Rectangle GetRowRectangle(CodeValueOverlayRow row)
		{
			if (row.Y > Height || row.Y + _rowHeight < 0 || Width <= 0)
			{
				return Rectangle.Empty;
			}

			Size textSize = TextRenderer.MeasureText(
				row.Text,
				Font,
				new Size(10000, _rowHeight),
				TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
			int width = Math.Max(8, textSize.Width);
			int x = Math.Clamp(row.X, 0, Math.Max(0, Width - 12));
			if (x + width > Width - 8)
			{
				return Rectangle.Empty;
			}

			return new Rectangle(x, row.Y + 1, width, Math.Max(14, _rowHeight - 2));
		}

		protected override void WndProc(ref Message m)
		{
			const int wmNcHitTest = 0x0084;
			const int htTransparent = -1;
			if (m.Msg == wmNcHitTest)
			{
				m.Result = new IntPtr(htTransparent);
				return;
			}

			base.WndProc(ref m);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			foreach (CodeValueOverlayRow row in _rows)
			{
				Rectangle rect = GetRowRectangle(row);
				if (rect.IsEmpty)
				{
					continue;
				}

				TextRenderer.DrawText(
					e.Graphics,
					row.Text,
					Font,
					rect,
					ForeColor,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
			}
		}
	}

	private sealed class CodeValueOverlayWindow : Form
	{
		private IReadOnlyList<CodeValueOverlayRow> _rows = Array.Empty<CodeValueOverlayRow>();
		private int _rowHeight = 18;
		private string _lastRenderSignature = "";

		public CodeValueOverlayWindow()
		{
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			BackColor = Color.Fuchsia;
			TransparencyKey = Color.Fuchsia;
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
		}

		protected override bool ShowWithoutActivation => true;

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams cp = base.CreateParams;
				cp.ExStyle |= 0x20;       // WS_EX_TRANSPARENT: let mouse reach Scintilla.
				cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: keep it out of Alt+Tab.
				cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE.
				return cp;
			}
		}

		public void ShowRows(
			Form owner,
			Rectangle screenBounds,
			IReadOnlyList<CodeValueOverlayRow> rows,
			Color backColor,
			Color foreColor,
			int rowHeight)
		{
			if (rows.Count == 0 || screenBounds.Width <= 0 || screenBounds.Height <= 0 || owner.WindowState == FormWindowState.Minimized)
			{
				HideOverlay();
				return;
			}

			string renderSignature = $"{screenBounds.Left}:{screenBounds.Top}:{screenBounds.Width}:{screenBounds.Height}|" +
				$"{foreColor.ToArgb()}|{Font.Name}:{Font.SizeInPoints:0.0}:{Font.Style}|{rowHeight}|" +
				string.Join("|", rows.Select(row => $"{row.X}:{row.Y}:{row.Text}"));
			if (Visible && renderSignature.Equals(_lastRenderSignature, StringComparison.Ordinal))
			{
				return;
			}

			_lastRenderSignature = renderSignature;
			_rows = rows;
			ForeColor = foreColor;
			_rowHeight = Math.Max(16, rowHeight);
			Bounds = screenBounds;
			UpdateClippingRegion();
			if (!Visible)
			{
				Show(owner);
			}
			Invalidate();
		}

		public void HideOverlay()
		{
			_rows = Array.Empty<CodeValueOverlayRow>();
			_lastRenderSignature = "";
			Region? oldRegion = Region;
			Region = null;
			oldRegion?.Dispose();
			if (Visible)
			{
				Hide();
			}
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			UpdateClippingRegion();
		}

		private void UpdateClippingRegion()
		{
			Region? nextRegion = null;
			foreach (CodeValueOverlayRow row in _rows)
			{
				Rectangle rect = GetRowRectangle(row);
				if (rect.IsEmpty)
				{
					continue;
				}

				if (nextRegion == null)
				{
					nextRegion = new Region(rect);
				}
				else
				{
					nextRegion.Union(rect);
				}
			}

			nextRegion ??= new Region(Rectangle.Empty);
			Region? oldRegion = Region;
			Region = nextRegion;
			oldRegion?.Dispose();
		}

		private Rectangle GetRowRectangle(CodeValueOverlayRow row)
		{
			if (row.Y > Height || row.Y + _rowHeight < 0 || Width <= 0)
			{
				return Rectangle.Empty;
			}

			Size textSize = TextRenderer.MeasureText(
				row.Text,
				Font,
				new Size(10000, _rowHeight),
				TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
			int width = Math.Max(8, textSize.Width);
			int x = Math.Clamp(row.X, 0, Math.Max(0, Width - 12));
			if (x + width > Width - 8)
			{
				return Rectangle.Empty;
			}

			return new Rectangle(x, row.Y + 1, width, Math.Max(14, _rowHeight - 2));
		}

		protected override void WndProc(ref Message m)
		{
			const int wmNcHitTest = 0x0084;
			const int htTransparent = -1;
			if (m.Msg == wmNcHitTest)
			{
				m.Result = new IntPtr(htTransparent);
				return;
			}

			base.WndProc(ref m);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			foreach (CodeValueOverlayRow row in _rows)
			{
				Rectangle rect = GetRowRectangle(row);
				if (rect.IsEmpty)
				{
					continue;
				}

				TextRenderer.DrawText(
					e.Graphics,
					row.Text,
					Font,
					rect,
					ForeColor,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
			}
		}
	}

	private sealed class ReadableButton : Button
	{
		private bool _hover;
		private bool _pressed;

		public ReadableButton()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			_hover = true;
			Invalidate();
			base.OnMouseEnter(e);
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			_hover = false;
			_pressed = false;
			Invalidate();
			base.OnMouseLeave(e);
		}

		protected override void OnMouseDown(MouseEventArgs mevent)
		{
			_pressed = true;
			Invalidate();
			base.OnMouseDown(mevent);
		}

		protected override void OnMouseUp(MouseEventArgs mevent)
		{
			_pressed = false;
			Invalidate();
			base.OnMouseUp(mevent);
		}

		protected override void OnPaint(PaintEventArgs pevent)
		{
			Color back = _pressed
				? FlatAppearance.MouseDownBackColor
				: (_hover ? FlatAppearance.MouseOverBackColor : BackColor);
			if (!Enabled)
			{
				back = BackColor;
			}

			Rectangle rect = ClientRectangle;
			using (SolidBrush brush = new SolidBrush(back))
			{
				pevent.Graphics.FillRectangle(brush, rect);
			}

			int borderSize = Math.Max(1, FlatAppearance.BorderSize);
			ControlPaint.DrawBorder(
				pevent.Graphics,
				rect,
				FlatAppearance.BorderColor,
				borderSize,
				ButtonBorderStyle.Solid,
				FlatAppearance.BorderColor,
				borderSize,
				ButtonBorderStyle.Solid,
				FlatAppearance.BorderColor,
				borderSize,
				ButtonBorderStyle.Solid,
				FlatAppearance.BorderColor,
				borderSize,
				ButtonBorderStyle.Solid);

			Rectangle textRect = Rectangle.Inflate(rect, -3, -1);
			TextRenderer.DrawText(
				pevent.Graphics,
				Text,
				Font,
				textRect,
				ForeColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

			if (Focused && ShowFocusCues)
			{
				ControlPaint.DrawFocusRectangle(pevent.Graphics, Rectangle.Inflate(rect, -4, -4));
			}
		}
	}

	private Button CommandButton(string text)
	{
		Font font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
		int width = Math.Max(Ui(52), Math.Min(Ui(96), TextRenderer.MeasureText(text, font).Width + Ui(22)));
		Button button = new ReadableButton
		{
			Text = text,
			AutoSize = false,
			Size = new Size(width, Ui(26)),
			BackColor = _accent,
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Font = font,
			Margin = new Padding(0),
			Cursor = Cursors.Hand,
			TextAlign = ContentAlignment.MiddleCenter,
			UseVisualStyleBackColor = false,
			Padding = Padding.Empty,
			Tag = "command"
		};
		button.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
		button.FlatAppearance.BorderSize = 2;
		button.FlatAppearance.MouseOverBackColor = _accent;
		button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_accent);
		return button;
	}

	private void MakeToolbarButton(Button button, string style)
	{
		button.Dock = DockStyle.None;
		button.Anchor = AnchorStyles.None;
		button.Margin = new Padding(0);
		button.MinimumSize = new Size(0, Ui(26));
		button.Height = Ui(26);
		ApplyButtonStyle(button, style);
	}

	private Button PlainButton(string text)
	{
		return new Button
		{
			Text = text,
			AutoSize = false,
			Size = new Size(Ui(104), Ui(30)),
			BackColor = _button,
			ForeColor = _ink,
			FlatStyle = FlatStyle.Flat,
			Tag = "plain"
		};
	}

	private void ApplyButtonStyle(Button button, string style)
	{
		if (IsProminentButtonStyle(style))
		{
			Color color = GetProminentButtonColor(style);
			button.BackColor = color;
			button.ForeColor = GetButtonTextColor();
			button.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
			button.FlatStyle = FlatStyle.Flat;
			button.Cursor = Cursors.Hand;
			button.UseVisualStyleBackColor = false;
			button.UseCompatibleTextRendering = false;
			button.Padding = Padding.Empty;
			button.FlatAppearance.BorderColor = GetProminentButtonBorderColor(style);
			button.FlatAppearance.BorderSize = 1;
			button.FlatAppearance.MouseOverBackColor = IsCurrentLightTheme() ? MixColor(color, _accent, 0.08f) : MixColor(color, Color.White, 0.08f);
			button.FlatAppearance.MouseDownBackColor = IsCurrentLightTheme() ? MixColor(color, Color.Black, 0.08f) : MixColor(color, Color.Black, 0.18f);
			button.Tag = style;
		}
		else if (style == "command")
		{
			button.BackColor = UsesDarkCommandButtons() ? _button : IsCurrentLightTheme() ? Color.FromArgb(221, 233, 228) : Color.FromArgb(51, 65, 85);
			button.ForeColor = GetButtonTextColor();
			button.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
			button.Cursor = Cursors.Hand;
			button.UseVisualStyleBackColor = false;
			button.UseCompatibleTextRendering = false;
			button.Padding = Padding.Empty;
			button.FlatAppearance.BorderColor = UsesDarkCommandButtons() ? ControlPaint.Light(_button, 0.25f) : IsCurrentLightTheme() ? Color.FromArgb(160, 184, 176) : Color.FromArgb(203, 213, 225);
			button.FlatAppearance.BorderSize = 1;
			button.FlatAppearance.MouseOverBackColor = UsesDarkCommandButtons() ? ControlPaint.Light(_button, 0.12f) : IsCurrentLightTheme() ? Color.FromArgb(209, 226, 219) : Color.FromArgb(71, 85, 105);
			button.FlatAppearance.MouseDownBackColor = UsesDarkCommandButtons() ? ControlPaint.Dark(_button, 0.12f) : IsCurrentLightTheme() ? Color.FromArgb(195, 214, 206) : Color.FromArgb(30, 41, 59);
			button.Tag = "command";
		}
		else if (style == "accent")
		{
			button.BackColor = _accent;
			button.ForeColor = Color.White;
			button.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
			button.Cursor = Cursors.Hand;
			button.UseVisualStyleBackColor = false;
			button.FlatAppearance.BorderColor = ControlPaint.Light(_accent);
			button.FlatAppearance.BorderSize = 1;
			button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_accent);
			button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_accent);
			button.Tag = "accent";
		}
		else if (style == "disabled")
		{
			button.BackColor = _gridHeader;
			button.ForeColor = _muted;
			button.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
			button.Cursor = Cursors.Default;
			button.UseVisualStyleBackColor = false;
			button.FlatAppearance.BorderColor = _gridHeader;
			button.FlatAppearance.BorderSize = 1;
			button.FlatAppearance.MouseOverBackColor = _gridHeader;
			button.FlatAppearance.MouseDownBackColor = _gridHeader;
			button.Tag = "disabled";
		}
		else
		{
			button.BackColor = _button;
			button.ForeColor = UsesDarkCommandButtons() ? GetButtonTextColor() : _ink;
			button.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
			button.Cursor = Cursors.Hand;
			button.UseVisualStyleBackColor = false;
			button.FlatAppearance.BorderColor = _gridHeader;
			button.FlatAppearance.BorderSize = 1;
			button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_button);
			button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_button);
			button.Tag = "plain";
		}
	}

	private static bool IsProminentButtonStyle(string style)
	{
		return style is "directory" or "refresh" or "firmware" or "blocked" or "download" or "downloadBusy" or "connect" or "disconnect" or "waiting" or "simulate" or "monitorStart" or "monitorStop" or "search" or "working";
	}

	private Color GetProminentButtonColor(string style)
	{
		if (UsesDarkCommandButtons())
		{
			return style switch
			{
				"waiting" or "working" or "simulate" or "downloadBusy" or "blocked" => ControlPaint.Light(_button, 0.08f),
				"disconnect" or "monitorStop" => ControlPaint.Dark(_button, 0.08f),
				_ => _button
			};
		}

		if (IsCurrentLightTheme())
		{
			return style switch
			{
				"waiting" or "working" or "simulate" or "downloadBusy" or "blocked" => Color.FromArgb(225, 230, 227),
				"disconnect" or "monitorStop" => Color.FromArgb(216, 225, 221),
				_ => Color.FromArgb(220, 233, 227)
			};
		}

		return style switch
		{
			"waiting" or "working" or "simulate" or "downloadBusy" or "blocked" => Color.FromArgb(71, 85, 105),
			"disconnect" or "monitorStop" => Color.FromArgb(63, 78, 101),
			_ => Color.FromArgb(51, 65, 85)
		};
	}

	private Color GetProminentButtonBorderColor(string style)
	{
		if (UsesDarkCommandButtons())
		{
			return ControlPaint.Light(_button, 0.24f);
		}

		if (IsCurrentLightTheme())
		{
			return style switch
			{
				"waiting" or "working" or "simulate" or "downloadBusy" or "blocked" => Color.FromArgb(178, 190, 185),
				_ => Color.FromArgb(132, 165, 154)
			};
		}

		return style switch
		{
			"waiting" or "working" or "simulate" or "downloadBusy" or "blocked" => Color.FromArgb(203, 213, 225),
			_ => Color.FromArgb(148, 163, 184)
		};
	}

	private static Color MixColor(Color from, Color to, float ratio)
	{
		ratio = Math.Max(0f, Math.Min(1f, ratio));
		int r = (int)Math.Round(from.R + (to.R - from.R) * ratio);
		int g = (int)Math.Round(from.G + (to.G - from.G) * ratio);
		int b = (int)Math.Round(from.B + (to.B - from.B) * ratio);
		return Color.FromArgb(r, g, b);
	}

	private void StyleTextBox(TextBox box)
	{
		box.BackColor = _surface;
		box.ForeColor = _ink;
		box.BorderStyle = BorderStyle.FixedSingle;
		box.Tag = "input";
	}

	private void StyleComboBox(ComboBox box)
	{
		box.BackColor = _surface;
		box.ForeColor = _ink;
		box.Tag = "input";
	}

	private Label SmallLabel(string text)
	{
		return new Label
		{
			Text = text,
			ForeColor = _muted,
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleLeft,
			Width = Math.Max(Ui(52), text.Length * Ui(12)),
			Height = Ui(30),
			Margin = new Padding(Ui(4), 0, Ui(4), 0),
			Tag = "small"
		};
	}

	private Label SectionLabel(string text)
	{
		return new Label
		{
			Text = text,
			ForeColor = _ink,
			Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			Tag = "section"
		};
	}

	private static NumericUpDown HexUpDown(int value)
	{
		return new NumericUpDown
		{
			Minimum = 0m,
			Maximum = 2047m,
			Value = value,
			Hexadecimal = true,
			Width = 76,
			BackColor = Color.FromArgb(15, 23, 42),
			ForeColor = Color.FromArgb(226, 232, 240),
			Tag = "input"
		};
	}

	private static void EnableDoubleBuffer(Control control)
	{
		try
		{
			typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(control, true, null);
		}
		catch
		{
		}
	}

	private void SplitContainerPaint(object? sender, PaintEventArgs e)
	{
		if (sender is not SplitContainer splitContainer)
		{
			return;
		}

		Rectangle splitter = splitContainer.SplitterRectangle;
		if (splitter.Width <= 0 || splitter.Height <= 0)
		{
			return;
		}

		Color color = GetSplitterColor(splitContainer.Tag);
		using SolidBrush fill = new SolidBrush(color);
		e.Graphics.FillRectangle(fill, splitter);

		using Pen border = new Pen(ControlPaint.Dark(color), 1f);
		e.Graphics.DrawRectangle(border, Rectangle.Inflate(splitter, -1, -1));

		Rectangle stripe = splitter;
		if (splitContainer.Orientation == Orientation.Vertical)
		{
			stripe.X = splitter.X + splitter.Width / 2 - 1;
			stripe.Width = 2;
		}
		else
		{
			stripe.Y = splitter.Y + splitter.Height / 2 - 1;
			stripe.Height = 2;
		}

		using SolidBrush stripeBrush = new SolidBrush(IsCurrentLightTheme() ? Color.FromArgb(105, 78, 96, 91) : Color.FromArgb(72, Color.White));
		e.Graphics.FillRectangle(stripeBrush, stripe);
	}

	private Color GetSplitterColor(object? tag)
	{
		if (IsCurrentLightTheme())
		{
			return object.Equals(tag, "split-right")
				? Color.FromArgb(178, 174, 154)
				: Color.FromArgb(154, 176, 170);
		}

		if (object.Equals(tag, "split-right"))
		{
			return MixColor(_gridHeader, _surfaceAlt, 0.35f);
		}

		return MixColor(_gridHeader, _surfaceAlt, 0.45f);
	}

	private bool IsCurrentLightTheme()
	{
		return _themeName.Equals("米黄灰工位", StringComparison.Ordinal) ||
			_themeName.Equals("Keil经典", StringComparison.Ordinal) ||
			_themeName.Equals("暖灰工位", StringComparison.Ordinal) ||
			_themeName.Equals("淡蓝工控", StringComparison.Ordinal) ||
			_themeName.Equals("冷白实验室", StringComparison.Ordinal);
	}

	private bool UsesDarkCommandButtons()
	{
		return _themeName.Equals("米黄灰工位", StringComparison.Ordinal) ||
			_themeName.Equals("淡蓝工控", StringComparison.Ordinal);
	}

	private Color GetButtonTextColor()
	{
		if (UsesDarkCommandButtons())
		{
			return _themeName.Equals("米黄灰工位", StringComparison.Ordinal)
				? Color.FromArgb(246, 235, 203)
				: Color.FromArgb(234, 248, 255);
		}

		return IsCurrentLightTheme() ? Color.FromArgb(24, 74, 66) : Color.White;
	}

	private static int ColorDistance(Color left, Color right)
	{
		int red = left.R - right.R;
		int green = left.G - right.G;
		int blue = left.B - right.B;
		return (int)Math.Sqrt(red * red + green * green + blue * blue);
	}

	private void ApplyTheme(string name)
	{
		ThemePalette theme = GetTheme(name);
		_themeName = theme.Name;
		_bg = theme.Bg;
		_header = theme.Header;
		_panel = theme.Panel;
		_surface = theme.Surface;
		_surfaceAlt = theme.SurfaceAlt;
		_gridHeader = theme.GridHeader;
		_ink = theme.Ink;
		_muted = theme.Muted;
		_accent = theme.Accent;
		_button = theme.Button;
		_statusOff = theme.StatusOff;
		ApplyCodePaletteForTheme(_themeName);
		_lastFunctionCodeText = "";
		_lastDataCodeText = "";
		_forceDataCodeRtfRefresh = true;
		BackColor = _bg;
		ApplyThemeToControl(this);
		RenderFunctionSource();
		UpdateValueFormatButton();
		UpdateForceHoldReminder();
		UpdateProgramInsightPanel(force: true);
		UpdateDownloadButtonState();
	}

	private void ApplyCodePaletteForTheme(string name)
	{
		if (name.Equals("Keil经典", StringComparison.Ordinal))
		{
			_codeCommentColor = Color.FromArgb(0, 128, 0);
			_codeFunctionColor = Color.FromArgb(75, 63, 166);
			_codeKeywordColor = Color.FromArgb(0, 0, 204);
			_codeValueColor = Color.FromArgb(29, 78, 216);
			_codeValueFreshColor = Color.FromArgb(22, 163, 74);
			_codeValueBackColor = Color.FromArgb(242, 216, 114);
			_codeValueStaleBackColor = Color.FromArgb(248, 231, 165);
			ApplyCodeValueTagPaletteForTheme(name);
			_codeTrueLineBackColor = Color.FromArgb(218, 238, 220);
			_codeFocusVariableForeColor = Color.FromArgb(31, 41, 51);
			_codeFocusVariableBackColor = Color.FromArgb(224, 232, 176);
			_programSearchLineBackColor = Color.FromArgb(255, 242, 188);
			_programSearchMatchBackColor = Color.FromArgb(245, 210, 86);
			return;
		}

		if (name.Equals("米黄灰工位", StringComparison.Ordinal) ||
			name.Equals("暖灰工位", StringComparison.Ordinal) ||
			name.Equals("淡蓝工控", StringComparison.Ordinal) ||
			name.Equals("冷白实验室", StringComparison.Ordinal))
		{
			bool paleBlue = name.Equals("淡蓝工控", StringComparison.Ordinal);
			_codeCommentColor = paleBlue ? Color.FromArgb(42, 129, 76) : Color.FromArgb(60, 124, 57);
			_codeFunctionColor = paleBlue ? Color.FromArgb(29, 120, 179) : Color.FromArgb(31, 102, 146);
			_codeKeywordColor = paleBlue ? Color.FromArgb(176, 69, 117) : Color.FromArgb(161, 58, 100);
			_codeValueColor = Color.FromArgb(29, 78, 216);
			_codeValueFreshColor = Color.FromArgb(22, 163, 74);
			_codeValueBackColor = paleBlue ? Color.FromArgb(191, 222, 240) : Color.FromArgb(202, 183, 126);
			_codeValueStaleBackColor = paleBlue ? Color.FromArgb(221, 237, 245) : Color.FromArgb(221, 211, 184);
			ApplyCodeValueTagPaletteForTheme(name);
			_codeTrueLineBackColor = paleBlue ? Color.FromArgb(224, 241, 231) : Color.FromArgb(232, 229, 203);
			_codeFocusVariableForeColor = Color.FromArgb(27, 49, 46);
			_codeFocusVariableBackColor = paleBlue ? Color.FromArgb(185, 220, 240) : Color.FromArgb(204, 183, 126);
			_programSearchLineBackColor = paleBlue ? Color.FromArgb(211, 233, 245) : Color.FromArgb(247, 238, 196);
			_programSearchMatchBackColor = paleBlue ? Color.FromArgb(163, 209, 234) : Color.FromArgb(236, 199, 99);
			return;
		}

		_codeCommentColor = Color.FromArgb(74, 222, 128);
		_codeFunctionColor = Color.FromArgb(96, 165, 250);
		_codeKeywordColor = Color.FromArgb(244, 114, 182);
		_codeValueColor = Color.FromArgb(29, 78, 216);
		_codeValueFreshColor = Color.FromArgb(34, 197, 94);
		_codeValueBackColor = Color.FromArgb(38, 92, 58);
		_codeValueStaleBackColor = Color.FromArgb(34, 48, 38);
		ApplyCodeValueTagPaletteForTheme(name);
		_codeTrueLineBackColor = Color.FromArgb(22, 49, 38);
		_codeFocusVariableForeColor = Color.FromArgb(2, 6, 23);
		_codeFocusVariableBackColor = Color.FromArgb(74, 222, 128);
		_programSearchLineBackColor = Color.FromArgb(64, 55, 26);
		_programSearchMatchBackColor = Color.FromArgb(135, 88, 18);
	}

	private void ApplyCodeValueTagPaletteForTheme(string name)
	{
		bool lightSurface = RelativeLuminance(_surface) >= 0.45;
		Color foreColor = PickInlineCodeValueForeColor(_surface);
		Color backColor = lightSurface ? Color.FromArgb(181, 235, 242) : Color.FromArgb(8, 74, 82);
		_codeValueTagBorderColor = lightSurface ? Color.FromArgb(14, 116, 144) : Color.FromArgb(34, 211, 238);
		_codeValueTagActiveBackColor = backColor;
		_codeValueTagInactiveBackColor = backColor;
		_codeValueTagActiveForeColor = foreColor;
		_codeValueTagInactiveForeColor = foreColor;
		_codeValueBackColor = backColor;
		_codeValueStaleBackColor = backColor;
	}

	private static Color PickInlineCodeValueForeColor(Color surface)
	{
		return RelativeLuminance(surface) >= 0.45
			? Color.FromArgb(8, 105, 122)
			: Color.FromArgb(34, 211, 238);
	}

	private Color PickHighContrastCodeValueForeColor(Color surface, string themeName)
	{
		bool lightSurface = RelativeLuminance(surface) >= 0.45;
		if (lightSurface)
		{
			return Color.FromArgb(180, 35, 0);
		}

		if (themeName.Equals("工业黑金", StringComparison.Ordinal) ||
			themeName.Equals("琥珀终端", StringComparison.Ordinal))
		{
			return Color.FromArgb(34, 211, 238);
		}

		return Color.FromArgb(253, 224, 71);
	}

	private static double ContrastRatio(Color a, Color b)
	{
		double l1 = RelativeLuminance(a);
		double l2 = RelativeLuminance(b);
		double lighter = Math.Max(l1, l2);
		double darker = Math.Min(l1, l2);
		return (lighter + 0.05) / (darker + 0.05);
	}

	private static double RelativeLuminance(Color color)
	{
		static double Channel(byte value)
		{
			double v = value / 255.0;
			return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}

		return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
	}

	private void ApplyThemeToControl(Control control)
	{
		if (control is SplitContainer splitContainer)
		{
			splitContainer.BackColor = GetSplitterColor(splitContainer.Tag);
			splitContainer.Invalidate();
		}
		else if (control is FlowChartView flowChart)
		{
			flowChart.SetPalette(_surface, _surfaceAlt, _ink, _muted, _accent, _gridHeader);
		}
		else if (control is Scintilla editor)
		{
			ApplyScintillaTheme(editor);
		}
		else if (!(control is DataGridView grid))
		{
			if (!(control is RichTextBox richTextBox))
			{
				if (!(control is TextBox textBox))
				{
					if (!(control is ComboBox comboBox))
					{
						if (!(control is NumericUpDown numericUpDown))
						{
							if (!(control is CheckBox checkBox))
							{
								if (!(control is Button button))
								{
									if (!(control is Label label))
									{
										if (!(control is ListBox listBox))
										{
											if (!(control is ListView listView))
											{
												if (!(control is TableLayoutPanel tableLayoutPanel))
												{
													if (!(control is FlowLayoutPanel flowLayoutPanel))
													{
														if (control is Panel panel)
														{
															panel.BackColor = object.Equals(panel.Tag, "header") ? _header : (object.Equals(panel.Tag, "surface") ? _surface : _panel);
														}
													}
													else
													{
														flowLayoutPanel.BackColor = Color.Transparent;
													}
												}
												else
												{
													tableLayoutPanel.BackColor = object.Equals(tableLayoutPanel.Tag, "searchBar")
														? _gridHeader
														: ((tableLayoutPanel.Parent == this) ? _bg : Color.Transparent);
												}
											}
											else
											{
												listView.BackColor = _surface;
												listView.ForeColor = _ink;
											}
										}
										else
										{
											listBox.BackColor = _surface;
											listBox.ForeColor = _ink;
										}
									}
									else if (label == _statusLabel)
									{
										label.ForeColor = Color.White;
										label.BackColor = _offlineSimulation ? _gridHeader : ((_adapter == null) ? _statusOff : _accent);
									}
									else if (object.Equals(label.Tag, "runtime"))
									{
										label.ForeColor = Color.White;
										label.BackColor = _accent;
									}
									else if (label == _visibleValuesLabel)
									{
										label.ForeColor = _codeValueTagInactiveForeColor;
										label.BackColor = _codeValueTagInactiveBackColor;
									}
									else if (object.Equals(label.Tag, "small"))
									{
										label.ForeColor = _muted;
										label.BackColor = Color.Transparent;
									}
									else if (object.Equals(label.Tag, "appVersion"))
									{
										label.ForeColor = _accent;
										label.BackColor = Color.Transparent;
									}
									else if (object.Equals(label.Tag, "surfaceAlt"))
									{
										label.ForeColor = _ink;
										label.BackColor = _surfaceAlt;
									}
									else
									{
										label.ForeColor = _ink;
										label.BackColor = Color.Transparent;
									}
								}
								else
								{
									ApplyButtonStyle(button, button.Tag as string ?? "plain");
								}
							}
							else
							{
								checkBox.BackColor = _panel;
								checkBox.ForeColor = _muted;
							}
						}
						else
						{
							numericUpDown.BackColor = _surface;
							numericUpDown.ForeColor = _ink;
						}
					}
					else
					{
						comboBox.BackColor = _surface;
						comboBox.ForeColor = _ink;
					}
				}
				else
				{
					textBox.BackColor = object.Equals(textBox.Tag, "searchInput") ? _surfaceAlt : _surface;
					textBox.ForeColor = _ink;
				}
			}
			else
			{
				richTextBox.BackColor = _surface;
				richTextBox.ForeColor = _ink;
			}
		}
		else
		{
			ApplyGridTheme(grid);
		}
		foreach (Control control2 in control.Controls)
		{
			ApplyThemeToControl(control2);
		}
	}

	private void ApplyGridTheme(DataGridView grid)
	{
		grid.BackgroundColor = _panel;
		grid.GridColor = _gridHeader;
		grid.ColumnHeadersDefaultCellStyle.BackColor = _gridHeader;
		grid.ColumnHeadersDefaultCellStyle.ForeColor = _ink;
		grid.DefaultCellStyle.BackColor = _surface;
		grid.DefaultCellStyle.ForeColor = _ink;
		grid.DefaultCellStyle.SelectionBackColor = _accent;
		grid.DefaultCellStyle.SelectionForeColor = Color.White;
		grid.AlternatingRowsDefaultCellStyle.BackColor = _surfaceAlt;
		grid.RowHeadersDefaultCellStyle.BackColor = _gridHeader;
		grid.RowHeadersDefaultCellStyle.ForeColor = _ink;
	}

	private static ThemePalette GetTheme(string name)
	{
		return name switch
		{
			"护眼暗绿" => new ThemePalette("护眼暗绿", Color.FromArgb(5, 16, 13), Color.FromArgb(8, 28, 23), Color.FromArgb(13, 37, 31), Color.FromArgb(8, 27, 23), Color.FromArgb(17, 48, 40), Color.FromArgb(23, 70, 57), Color.FromArgb(222, 246, 232), Color.FromArgb(135, 179, 158), Color.FromArgb(52, 211, 153), Color.FromArgb(18, 56, 47), Color.FromArgb(62, 86, 78)),
			"夜航蓝灰" => new ThemePalette("夜航蓝灰", Color.FromArgb(7, 11, 18), Color.FromArgb(12, 19, 30), Color.FromArgb(17, 26, 39), Color.FromArgb(12, 20, 32), Color.FromArgb(25, 36, 52), Color.FromArgb(42, 57, 78), Color.FromArgb(232, 240, 248), Color.FromArgb(145, 160, 178), Color.FromArgb(125, 211, 252), Color.FromArgb(31, 42, 58), Color.FromArgb(75, 85, 99)),
			"深蓝仪表盘" => new ThemePalette("深蓝仪表盘", Color.FromArgb(5, 18, 35), Color.FromArgb(7, 31, 58), Color.FromArgb(10, 35, 66), Color.FromArgb(8, 27, 52), Color.FromArgb(12, 44, 78), Color.FromArgb(18, 72, 118), Color.FromArgb(226, 246, 255), Color.FromArgb(125, 179, 214), Color.FromArgb(0, 180, 216), Color.FromArgb(20, 65, 105), Color.FromArgb(56, 83, 116)), 
			"极简暗色" => new ThemePalette("极简暗色", Color.FromArgb(3, 7, 12), Color.FromArgb(8, 13, 20), Color.FromArgb(12, 18, 28), Color.FromArgb(8, 12, 19), Color.FromArgb(18, 24, 34), Color.FromArgb(32, 39, 51), Color.FromArgb(229, 231, 235), Color.FromArgb(148, 163, 184), Color.FromArgb(125, 211, 252), Color.FromArgb(24, 31, 42), Color.FromArgb(71, 85, 105)), 
			"设备控制台" => new ThemePalette("设备控制台", Color.FromArgb(22, 31, 28), Color.FromArgb(30, 43, 38), Color.FromArgb(39, 51, 46), Color.FromArgb(23, 38, 33), Color.FromArgb(31, 49, 42), Color.FromArgb(64, 82, 73), Color.FromArgb(224, 238, 224), Color.FromArgb(154, 174, 158), Color.FromArgb(88, 190, 120), Color.FromArgb(56, 70, 62), Color.FromArgb(91, 105, 96)), 
			"低蓝墨绿" => new ThemePalette("低蓝墨绿", Color.FromArgb(9, 18, 17), Color.FromArgb(13, 29, 28), Color.FromArgb(18, 39, 37), Color.FromArgb(14, 31, 30), Color.FromArgb(25, 54, 51), Color.FromArgb(45, 80, 74), Color.FromArgb(218, 235, 225), Color.FromArgb(137, 160, 150), Color.FromArgb(110, 231, 183), Color.FromArgb(31, 58, 55), Color.FromArgb(75, 92, 88)),
			"浅色工程" => new ThemePalette("米黄灰工位", Color.FromArgb(216, 201, 169), Color.FromArgb(37, 40, 43), Color.FromArgb(230, 217, 187), Color.FromArgb(243, 232, 204), Color.FromArgb(210, 194, 159), Color.FromArgb(52, 50, 44), Color.FromArgb(33, 31, 26), Color.FromArgb(103, 95, 80), Color.FromArgb(47, 126, 132), Color.FromArgb(61, 66, 68), Color.FromArgb(91, 93, 87)),
			"暖灰工位" => new ThemePalette("米黄灰工位", Color.FromArgb(216, 201, 169), Color.FromArgb(37, 40, 43), Color.FromArgb(230, 217, 187), Color.FromArgb(243, 232, 204), Color.FromArgb(210, 194, 159), Color.FromArgb(52, 50, 44), Color.FromArgb(33, 31, 26), Color.FromArgb(103, 95, 80), Color.FromArgb(47, 126, 132), Color.FromArgb(61, 66, 68), Color.FromArgb(91, 93, 87)),
			"米黄灰工位" => new ThemePalette("米黄灰工位", Color.FromArgb(216, 201, 169), Color.FromArgb(37, 40, 43), Color.FromArgb(230, 217, 187), Color.FromArgb(243, 232, 204), Color.FromArgb(210, 194, 159), Color.FromArgb(52, 50, 44), Color.FromArgb(33, 31, 26), Color.FromArgb(103, 95, 80), Color.FromArgb(47, 126, 132), Color.FromArgb(61, 66, 68), Color.FromArgb(91, 93, 87)),
			"Keil经典" => new ThemePalette("Keil经典", Color.FromArgb(218, 216, 208), Color.FromArgb(200, 197, 189), Color.FromArgb(228, 225, 216), Color.FromArgb(255, 248, 231), Color.FromArgb(231, 226, 212), Color.FromArgb(185, 181, 170), Color.FromArgb(31, 41, 51), Color.FromArgb(107, 111, 118), Color.FromArgb(37, 99, 235), Color.FromArgb(214, 210, 200), Color.FromArgb(139, 139, 130)),
			"淡蓝工控" => new ThemePalette("淡蓝工控", Color.FromArgb(216, 236, 247), Color.FromArgb(22, 56, 74), Color.FromArgb(233, 246, 252), Color.FromArgb(246, 251, 254), Color.FromArgb(209, 234, 246), Color.FromArgb(36, 77, 98), Color.FromArgb(18, 48, 64), Color.FromArgb(85, 112, 131), Color.FromArgb(21, 126, 166), Color.FromArgb(44, 95, 120), Color.FromArgb(92, 135, 153)),
			"琥珀终端" => new ThemePalette("琥珀终端", Color.FromArgb(14, 10, 3), Color.FromArgb(26, 17, 4), Color.FromArgb(35, 25, 9), Color.FromArgb(23, 17, 7), Color.FromArgb(49, 35, 12), Color.FromArgb(79, 58, 22), Color.FromArgb(252, 236, 190), Color.FromArgb(190, 163, 104), Color.FromArgb(245, 158, 11), Color.FromArgb(63, 44, 14), Color.FromArgb(96, 78, 45)),
			"钢铁青" => new ThemePalette("钢铁青", Color.FromArgb(8, 15, 18), Color.FromArgb(12, 25, 30), Color.FromArgb(18, 34, 40), Color.FromArgb(12, 28, 34), Color.FromArgb(24, 49, 58), Color.FromArgb(43, 75, 86), Color.FromArgb(227, 242, 244), Color.FromArgb(137, 166, 171), Color.FromArgb(45, 212, 191), Color.FromArgb(31, 56, 64), Color.FromArgb(72, 91, 96)),
			"冷白实验室" => new ThemePalette("冷白实验室", Color.FromArgb(232, 237, 241), Color.FromArgb(215, 224, 231), Color.FromArgb(240, 244, 247), Color.FromArgb(249, 251, 252), Color.FromArgb(232, 239, 243), Color.FromArgb(204, 216, 224), Color.FromArgb(26, 38, 51), Color.FromArgb(88, 105, 119), Color.FromArgb(37, 99, 235), Color.FromArgb(221, 229, 235), Color.FromArgb(128, 144, 156)),
			"高对比黑" => new ThemePalette("高对比黑", Color.FromArgb(0, 0, 0), Color.FromArgb(8, 8, 8), Color.FromArgb(12, 12, 12), Color.FromArgb(4, 4, 4), Color.FromArgb(20, 20, 20), Color.FromArgb(42, 42, 42), Color.FromArgb(255, 255, 255), Color.FromArgb(190, 190, 190), Color.FromArgb(0, 220, 255), Color.FromArgb(28, 28, 28), Color.FromArgb(70, 70, 70)),
			_ => new ThemePalette("工业黑金", Color.FromArgb(11, 8, 4), Color.FromArgb(25, 17, 6), Color.FromArgb(31, 24, 13), Color.FromArgb(19, 15, 8), Color.FromArgb(45, 35, 18), Color.FromArgb(76, 58, 25), Color.FromArgb(247, 236, 210), Color.FromArgb(190, 166, 116), Color.FromArgb(222, 169, 47), Color.FromArgb(61, 45, 19), Color.FromArgb(94, 78, 48)), 
		};
	}
}
