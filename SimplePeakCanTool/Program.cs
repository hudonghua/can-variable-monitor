using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Collections.Concurrent;

namespace SimplePeakCanTool;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly ComboBox _baudBox = new();
    private readonly Button _connectButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _saveButton = new();
    private readonly CheckBox _pauseDisplayBox = new();
    private readonly SplitContainer _mainSplit = new();
    private readonly ListView _rxList = new BufferedListView();
    private readonly TextBox _idBox = new();
    private readonly ComboBox _frameTypeBox = new();
    private readonly NumericUpDown _lengthBox = new();
    private readonly NumericUpDown _periodBox = new();
    private readonly TextBox[] _byteBoxes = new TextBox[8];
    private readonly Button _sendButton = new();
    private readonly Button _addSendButton = new();
    private readonly Button _removeSendButton = new();
    private readonly Button _sendSelectedButton = new();
    private readonly Button _periodSendButton = new();
    private readonly ListView _txList = new BufferedListView();
    private readonly TextBox _txCellEditor = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _rxTimer = new();
    private readonly System.Windows.Forms.Timer _uiRefreshTimer = new();
    private readonly System.Windows.Forms.Timer _periodSendTimer = new();
    private readonly System.Windows.Forms.Timer _startupConnectTimer = new();
    private readonly System.Windows.Forms.Timer _settingsSaveTimer = new();
    private readonly List<CanLogRow> _logRows = new();
    private readonly Dictionary<string, FrameSummary> _summaries = new();
    private readonly HashSet<string> _dirtySummaryKeys = new();
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly string _legacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakSimpleCanTool",
        "settings.json");
    private int _savedSendPanelHeight;

    private ICanAdapter? _adapter;
    private bool _connected;
    private bool _loadingSettings = true;
    private bool _loadingEditorFromList;
    private bool _committingTxCellEdit;
    private ListViewItem? _editingTxItem;
    private int _editingTxColumn = -1;
    private int _editingTxByteIndex = -1;
    private int _startupConnectAttempts;
    private ulong _rxCount;
    private ulong _txCount;
    private DateTime _lastStatusUpdate = DateTime.MinValue;

    private const int MaxStartupConnectAttempts = 3;
    private const int TxByteStartColumn = 4;
    private const int TxByteCount = 8;
    private const int TxCountColumn = TxByteStartColumn + TxByteCount;
    private const int TxByteColumnWidth = 54;
    private static readonly Color RxDataBackColor = Color.FromArgb(220, 248, 226);
    private static readonly Color RxDataForeColor = Color.FromArgb(18, 82, 48);

    public MainForm()
    {
        Text = "PEAK简易CAN工具";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        Width = 980;
        Height = 760;
        MinimumSize = new Size(860, 620);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(242, 246, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(root);

        root.Controls.Add(BuildTopPanel(), 0, 0);
        root.Controls.Add(BuildMainSplit(), 0, 1);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = "未连接。关闭其它CAN软件后，选波特率并点连接。";
        root.Controls.Add(_status, 0, 2);

        _rxTimer.Interval = 20;
        _rxTimer.Tick += (_, _) => PollReceive();
        _uiRefreshTimer.Interval = 300;
        _uiRefreshTimer.Tick += (_, _) => RefreshDirtySummaries();
        _periodSendTimer.Tick += (_, _) => SendPeriodFrame(showErrors: false);
        _startupConnectTimer.Interval = 800;
        _startupConnectTimer.Tick += (_, _) => TryStartupConnect();
        _settingsSaveTimer.Interval = 300;
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveSettings();
        };
        _lengthBox.ValueChanged += (_, _) => UpdateByteBoxState();
        UpdateByteBoxState();
        LoadSettings();
        ApplySavedSendPanelHeight();
        WireEditorAutoSave();
        _loadingSettings = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke(new Action(TryStartupConnect));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _startupConnectTimer.Stop();
        _settingsSaveTimer.Stop();
        SaveSettings();
        Disconnect();
        base.OnFormClosing(e);
    }

    private Control BuildTopPanel()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        top.Controls.Add(new Label { Text = "波特率", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        _baudBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _baudBox.Width = 110;
        _baudBox.Items.AddRange(new object[] { "250K", "500K", "1M", "125K", "100K", "50K" });
        _baudBox.SelectedIndex = 0;
        top.Controls.Add(_baudBox);

        _connectButton.Text = "连接";
        _connectButton.Width = 90;
        _connectButton.Height = 32;
        _connectButton.Click += (_, _) => ToggleConnection();
        top.Controls.Add(_connectButton);

        _clearButton.Text = "清空";
        _clearButton.Width = 90;
        _clearButton.Height = 32;
        _clearButton.Click += (_, _) =>
        {
            _rxList.Items.Clear();
            _logRows.Clear();
            _summaries.Clear();
            _dirtySummaryKeys.Clear();
            _rxCount = 0;
            _txCount = 0;
            SetStatus(_connected ? "已清空，继续记录" : "已清空");
        };
        top.Controls.Add(_clearButton);

        _saveButton.Text = "保存CSV";
        _saveButton.Width = 100;
        _saveButton.Height = 32;
        _saveButton.Click += (_, _) => SaveCsv();
        top.Controls.Add(_saveButton);

        _pauseDisplayBox.Text = "暂停显示";
        _pauseDisplayBox.AutoSize = true;
        _pauseDisplayBox.Padding = new Padding(10, 7, 0, 0);
        top.Controls.Add(_pauseDisplayBox);

        foreach (Button button in top.Controls.OfType<Button>())
        {
            StyleButton(button);
        }

        return top;
    }

    private Control BuildMainSplit()
    {
        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.Orientation = Orientation.Horizontal;
        _mainSplit.SplitterWidth = 7;
        _mainSplit.Panel1MinSize = 180;
        _mainSplit.Panel2MinSize = 150;
        _mainSplit.BackColor = Color.FromArgb(218, 224, 232);
        _mainSplit.Panel1.Controls.Add(BuildReceiveList());
        _mainSplit.Panel2.Controls.Add(BuildSendPanel());
        _mainSplit.SplitterMoved += (_, _) => SaveSettings();
        _mainSplit.HandleCreated += (_, _) => ApplySavedSendPanelHeight();
        return _mainSplit;
    }

    private Control BuildReceiveList()
    {
        _rxList.Dock = DockStyle.Fill;
        _rxList.View = View.Details;
        _rxList.FullRowSelect = true;
        _rxList.GridLines = true;
        _rxList.BorderStyle = BorderStyle.FixedSingle;
        _rxList.BackColor = Color.FromArgb(246, 250, 255);
        _rxList.ForeColor = Color.FromArgb(32, 42, 54);
        _rxList.Columns.Add("时间", 130);
        _rxList.Columns.Add("方向", 60);
        _rxList.Columns.Add("ID", 100);
        _rxList.Columns.Add("类型", 70);
        _rxList.Columns.Add("长度", 60);
        _rxList.Columns.Add("周期ms", 80);
        _rxList.Columns.Add("次数", 70);
        _rxList.Columns.Add("数据", 430);
        return _rxList;
    }

    private Control BuildSendPanel()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(238, 246, 244),
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.FromArgb(238, 246, 244),
        };
        outer.Controls.Add(row1, 0, 0);

        row1.Controls.Add(new Label { Text = "ID", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        _idBox.Width = 90;
        _idBox.Text = "123";
        row1.Controls.Add(_idBox);

        row1.Controls.Add(new Label { Text = "类型", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _frameTypeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _frameTypeBox.Width = 90;
        _frameTypeBox.Items.AddRange(new object[] { "标准帧", "扩展帧" });
        _frameTypeBox.SelectedIndex = 0;
        row1.Controls.Add(_frameTypeBox);

        row1.Controls.Add(new Label { Text = "长度", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _lengthBox.Minimum = 0;
        _lengthBox.Maximum = 8;
        _lengthBox.Value = 8;
        _lengthBox.Width = 60;
        row1.Controls.Add(_lengthBox);

        row1.Controls.Add(new Label { Text = "周期ms", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _periodBox.Minimum = 10;
        _periodBox.Maximum = 60000;
        _periodBox.Value = 1000;
        _periodBox.Increment = 10;
        _periodBox.Width = 80;
        row1.Controls.Add(_periodBox);

        _sendButton.Text = "发送";
        _sendButton.Width = 90;
        _sendButton.Height = 32;
        _sendButton.Enabled = false;
        _sendButton.Click += (_, _) => SendFrame(showErrors: true);
        row1.Controls.Add(_sendButton);

        _addSendButton.Text = "加入列表";
        _addSendButton.Width = 90;
        _addSendButton.Height = 32;
        _addSendButton.Click += (_, _) => AddCurrentToSendList();
        row1.Controls.Add(_addSendButton);

        _sendSelectedButton.Text = "发送选中";
        _sendSelectedButton.Width = 90;
        _sendSelectedButton.Height = 32;
        _sendSelectedButton.Enabled = false;
        _sendSelectedButton.Click += (_, _) => SendSelectedFrame();
        row1.Controls.Add(_sendSelectedButton);

        _periodSendButton.Text = "周期发送";
        _periodSendButton.Width = 100;
        _periodSendButton.Height = 32;
        _periodSendButton.Enabled = false;
        _periodSendButton.Click += (_, _) => TogglePeriodSend();
        row1.Controls.Add(_periodSendButton);

        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.FromArgb(238, 246, 244),
        };
        outer.Controls.Add(row2, 0, 1);

        row2.Controls.Add(new Label { Text = "数据", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        for (int i = 0; i < _byteBoxes.Length; i++)
        {
            var box = new TextBox
            {
                Width = 42,
                MaxLength = 2,
                Text = "00",
                TextAlign = HorizontalAlignment.Center,
                CharacterCasing = CharacterCasing.Upper,
                Tag = i,
                BackColor = Color.FromArgb(255, 253, 232),
                ForeColor = Color.FromArgb(44, 54, 66),
            };
            box.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            box.Leave += (_, _) => NormalizeByteBox(box);
            box.TextChanged += (_, _) => AutoAdvanceByteBox(box);
            box.KeyPress += (_, e) => FilterHexKey(e);
            _byteBoxes[i] = box;
            row2.Controls.Add(box);
        }

        row2.Controls.Add(new Label { Text = " 每格1字节，16进制", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });

        var row3 = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 2, 0, 0),
            BackColor = Color.FromArgb(238, 246, 244),
        };
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        outer.Controls.Add(row3, 0, 2);

        _txList.Dock = DockStyle.Fill;
        _txList.View = View.Details;
        _txList.FullRowSelect = true;
        _txList.GridLines = true;
        _txList.MultiSelect = false;
        _txList.BorderStyle = BorderStyle.FixedSingle;
        _txList.BackColor = Color.FromArgb(248, 254, 251);
        _txList.ForeColor = Color.FromArgb(32, 42, 54);
        _txList.Columns.Add("ID", 90);
        _txList.Columns.Add("类型", 70);
        _txList.Columns.Add("长度", 55);
        _txList.Columns.Add("周期ms", 70);
        for (int i = 0; i < TxByteCount; i++)
        {
            _txList.Columns.Add("B" + i, TxByteColumnWidth);
        }

        _txList.Columns.Add("次数", 60);
        _txList.SelectedIndexChanged += (_, _) => OnTxSelectionChanged();
        _txList.MouseUp += OnTxListMouseUp;
        _txList.MouseDoubleClick += OnTxListMouseDoubleClick;
        row3.Controls.Add(_txList, 0, 0);

        _txCellEditor.Visible = false;
        _txCellEditor.BorderStyle = BorderStyle.FixedSingle;
        _txCellEditor.BackColor = Color.FromArgb(255, 253, 232);
        _txCellEditor.ForeColor = Color.FromArgb(32, 42, 54);
        _txCellEditor.KeyDown += OnTxCellEditorKeyDown;
        _txCellEditor.KeyPress += OnTxCellEditorKeyPress;
        _txCellEditor.TextChanged += OnTxCellEditorTextChanged;
        _txCellEditor.Leave += (_, _) => CommitTxCellEdit();
        _txList.Controls.Add(_txCellEditor);

        var txButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(238, 246, 244),
        };
        row3.Controls.Add(txButtons, 1, 0);

        _removeSendButton.Text = "删除选中";
        _removeSendButton.Width = 90;
        _removeSendButton.Height = 30;
        _removeSendButton.Click += (_, _) => RemoveSelectedSendRows();
        txButtons.Controls.Add(_removeSendButton);

        foreach (Button button in row1.Controls.OfType<Button>().Concat(txButtons.Controls.OfType<Button>()))
        {
            StyleButton(button);
        }
        return outer;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(170, 184, 200);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 235, 246);
        button.BackColor = Color.FromArgb(246, 249, 252);
        button.ForeColor = Color.FromArgb(28, 42, 58);
    }

    private ICanAdapter CreateAutoAdapter()
    {
        List<string> errors = new();
        foreach (Func<ICanAdapter> factory in new Func<ICanAdapter>[] { () => new PeakCanAdapter(), () => new SysCanAdapter(), () => new GcCanAdapter() })
        {
            ICanAdapter adapter = factory();
            try
            {
                adapter.Open(_baudBox.Text);
                return adapter;
            }
            catch (Exception ex)
            {
                errors.Add(adapter.Name + "：" + ex.Message);
                adapter.Dispose();
            }
        }

        throw new InvalidOperationException("没有打开可用 CAN 工具：" + string.Join("；", errors));
    }

    private void ToggleConnection()
    {
        _startupConnectTimer.Stop();
        if (_connected)
        {
            Disconnect();
            return;
        }

        try
        {
            Connect();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus("连接失败：" + ex.Message);
        }
    }

    private void TryStartupConnect()
    {
        if (_connected)
        {
            _startupConnectTimer.Stop();
            return;
        }

        if (_startupConnectAttempts >= MaxStartupConnectAttempts)
        {
            _startupConnectTimer.Stop();
            return;
        }

        _startupConnectAttempts++;
        try
        {
            SetStatus(_startupConnectAttempts == 1
                ? "正在自动连接CAN工具..."
                : $"正在重试自动连接CAN工具（{_startupConnectAttempts}/{MaxStartupConnectAttempts}）...");
            Connect();
            _startupConnectTimer.Stop();
        }
        catch (Exception ex)
        {
            if (_startupConnectAttempts < MaxStartupConnectAttempts)
            {
                SetStatus($"自动连接失败，稍后重试：{ex.Message}");
                _startupConnectTimer.Start();
                return;
            }

            _startupConnectTimer.Stop();
            SetStatus("自动连接失败：" + ex.Message + "；插好CAN工具后可手动点连接。");
        }
    }

    private void Connect()
    {
        ICanAdapter adapter = CreateAutoAdapter();
        _adapter = adapter;
        _connected = true;
        _connectButton.Text = "断开";
        _sendButton.Enabled = true;
        _periodSendButton.Enabled = true;
        _sendSelectedButton.Enabled = _txList.SelectedItems.Count > 0;
        _baudBox.Enabled = false;
        _rxTimer.Start();
        _uiRefreshTimer.Start();
        SetStatus($"已连接CAN工具，波特率 {_baudBox.Text}");
    }

    private void Disconnect()
    {
        StopPeriodSend();
        _rxTimer.Stop();
        _uiRefreshTimer.Stop();
        if (_connected)
        {
            _adapter?.Close();
        }

        _connected = false;
        _adapter?.Dispose();
        _adapter = null;
        _connectButton.Text = "连接";
        _sendButton.Enabled = false;
        _periodSendButton.Enabled = false;
        _sendSelectedButton.Enabled = false;
        _baudBox.Enabled = true;
        SetStatus("已断开");
    }

    private void SendFrame(bool showErrors)
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            SyncSelectedSendRowFromEditor();
            SendTxFrame(ReadEditorFrame());
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show(ex.Message, "发送失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                StopPeriodSend();
                SetStatus("周期发送已停止：" + ex.Message);
            }
        }
    }

    private void TogglePeriodSend()
    {
        if (_periodSendTimer.Enabled)
        {
            StopPeriodSend();
            SetStatus("周期发送已停止");
            return;
        }

        _periodSendTimer.Interval = _txList.Items.Count > 0 ? 20 : Math.Max(10, (int)_periodBox.Value);
        foreach (ListViewItem item in _txList.Items)
        {
            if (item.Tag is CanTxFrame frame)
            {
                frame.NextDueTime = DateTime.MinValue;
            }
        }
        _periodSendTimer.Start();
        _periodSendButton.Text = "停止周期";
        SendPeriodFrame(showErrors: true);
        SetStatus(_txList.Items.Count > 0
            ? $"周期发送列表：{_txList.Items.Count} 条，各按自己的周期ms"
            : $"周期发送当前编辑帧：{_periodBox.Value} ms");
    }

    private void StopPeriodSend()
    {
        _periodSendTimer.Stop();
        _periodSendButton.Text = "周期发送";
    }

    private void SendPeriodFrame(bool showErrors)
    {
        try
        {
            if (_txList.Items.Count == 0)
            {
                SendTxFrame(ReadEditorFrame());
                return;
            }

            DateTime now = DateTime.Now;
            foreach (ListViewItem item in _txList.Items)
            {
                if (item.Tag is CanTxFrame frame && now >= frame.NextDueTime)
                {
                    SendTxFrame(frame);
                    frame.NextDueTime = now.AddMilliseconds(Math.Max(10, frame.PeriodMs));
                    UpdateTxListItem(item, frame);
                }
            }
        }
        catch (Exception ex)
        {
            StopPeriodSend();
            if (showErrors)
            {
                MessageBox.Show(ex.Message, "周期发送失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            SetStatus("周期发送已停止：" + ex.Message);
        }
    }

    private void AddCurrentToSendList()
    {
        try
        {
            var frame = ReadEditorFrame();
            var item = AddFrameToSendList(frame);
            item.Selected = true;
            item.EnsureVisible();
            SaveSettings();
            SetStatus("已加入发送列表：" + FormatId(frame.Id, frame.Extended));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "加入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private ListViewItem AddFrameToSendList(CanTxFrame frame)
    {
        var item = new ListViewItem(FormatId(frame.Id, frame.Extended));
        item.SubItems.Add(frame.Extended ? "扩展" : "标准");
        item.SubItems.Add(frame.Data.Length.ToString());
        item.SubItems.Add(frame.PeriodMs.ToString());
        for (int i = 0; i < TxByteCount; i++)
        {
            item.SubItems.Add(GetTxByteText(frame, i));
        }

        item.SubItems.Add(frame.SendCount.ToString());
        item.Tag = frame;
        _txList.Items.Add(item);
        return item;
    }

    private void RemoveSelectedSendRows()
    {
        if (_txList.SelectedItems.Count == 0)
        {
            return;
        }

        foreach (ListViewItem item in _txList.SelectedItems)
        {
            _txList.Items.Remove(item);
        }

        SaveSettings();
    }

    private void OnTxSelectionChanged()
    {
        bool hasSelection = _txList.SelectedItems.Count > 0;
        _sendSelectedButton.Enabled = _connected && hasSelection;
        _removeSendButton.Enabled = hasSelection;

        if (hasSelection)
        {
            LoadSelectedToEditor();
        }
    }

    private void SyncSelectedSendRowFromEditor()
    {
        if (_loadingSettings || _loadingEditorFromList || _txList.SelectedItems.Count == 0)
        {
            return;
        }

        try
        {
            ListViewItem item = _txList.SelectedItems[0];
            CanTxFrame frame = ReadEditorFrame();
            if (item.Tag is CanTxFrame oldFrame)
            {
                frame.SendCount = oldFrame.SendCount;
                frame.NextDueTime = oldFrame.NextDueTime == DateTime.MinValue
                    ? DateTime.MinValue
                    : DateTime.Now.AddMilliseconds(Math.Max(10, frame.PeriodMs));
            }

            item.Tag = frame;
            UpdateTxListItem(item, frame);
        }
        catch
        {
        }
    }

    private void OnTxListMouseUp(object? sender, MouseEventArgs e)
    {
        ListViewHitTestInfo hit = _txList.HitTest(e.Location);
        ListViewItem? item = hit.Item ?? GetTxItemAtY(e.Y);
        if (item == null)
        {
            return;
        }

        int column = GetTxColumnAtPoint(item, e.Location);
        if (column < 0 || column == TxCountColumn)
        {
            return;
        }

        item.Selected = true;
        if (column == 1)
        {
            return;
        }

        BeginTxCellEdit(item, column);
    }

    private void OnTxListMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        ListViewHitTestInfo hit = _txList.HitTest(e.Location);
        ListViewItem? item = hit.Item ?? GetTxItemAtY(e.Y);
        if (item == null)
        {
            return;
        }

        int column = GetTxColumnAtPoint(item, e.Location);
        item.Selected = true;
        if (column == 1)
        {
            ToggleTxFrameType(item);
            return;
        }

        LoadSelectedToEditor();
    }

    private ListViewItem? GetTxItemAtY(int y)
    {
        foreach (ListViewItem item in _txList.Items)
        {
            Rectangle bounds = item.Bounds;
            if (y >= bounds.Top && y < bounds.Bottom)
            {
                return item;
            }
        }

        return null;
    }

    private int GetTxColumnAtPoint(ListViewItem item, Point location)
    {
        int left = item.Bounds.Left;
        for (int i = 0; i < _txList.Columns.Count; i++)
        {
            int width = _txList.Columns[i].Width;
            if (location.X >= left && location.X < left + width)
            {
                return i;
            }

            left += width;
        }

        return -1;
    }

    private Rectangle GetTxCellBounds(ListViewItem item, int column)
    {
        Rectangle bounds = item.Bounds;
        int left = bounds.Left;
        for (int i = 0; i < column; i++)
        {
            left += _txList.Columns[i].Width;
        }

        return new Rectangle(left + 1, bounds.Top + 1, Math.Max(20, _txList.Columns[column].Width - 2), Math.Max(18, bounds.Height - 2));
    }

    private static bool IsTxByteColumn(int column) => column >= TxByteStartColumn && column < TxCountColumn;

    private static string GetTxByteText(CanTxFrame frame, int byteIndex)
        => byteIndex >= 0 && byteIndex < frame.Data.Length ? frame.Data[byteIndex].ToString("X2") : "";

    private void BeginTxCellEdit(ListViewItem item, int column)
    {
        CommitTxCellEdit();

        _editingTxItem = item;
        _editingTxColumn = column;
        bool isByteColumn = IsTxByteColumn(column);
        _editingTxByteIndex = isByteColumn ? column - TxByteStartColumn : -1;
        _txCellEditor.Bounds = GetTxCellBounds(item, column);
        _txCellEditor.Text = isByteColumn && item.Tag is CanTxFrame frame
            ? GetTxByteText(frame, _editingTxByteIndex)
            : item.SubItems[column].Text;
        _txCellEditor.MaxLength = isByteColumn ? 2 : column == 0 ? 10 : 32767;
        _txCellEditor.Visible = true;
        _txCellEditor.BringToFront();
        _txCellEditor.Focus();
        _txCellEditor.SelectAll();
    }

    private void OnTxCellEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            CommitTxCellEdit();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            CancelTxCellEdit();
        }
    }

    private void OnTxCellEditorKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (IsTxByteColumn(_editingTxColumn))
        {
            FilterHexKey(e);
        }
    }

    private void OnTxCellEditorTextChanged(object? sender, EventArgs e)
    {
        if (_committingTxCellEdit || !_txCellEditor.Visible || !IsTxByteColumn(_editingTxColumn))
        {
            return;
        }

        if (_txCellEditor.TextLength >= 2)
        {
            CommitTxCellEdit(autoAdvance: true);
        }
    }

    private void CancelTxCellEdit()
    {
        _txCellEditor.Visible = false;
        _editingTxItem = null;
        _editingTxColumn = -1;
        _editingTxByteIndex = -1;
    }

    private void CommitTxCellEdit(bool autoAdvance = false)
    {
        if (_committingTxCellEdit || !_txCellEditor.Visible)
        {
            return;
        }

        _committingTxCellEdit = true;
        try
        {
            ListViewItem? item = _editingTxItem;
            int column = _editingTxColumn;
            int byteIndex = _editingTxByteIndex;
            string text = _txCellEditor.Text.Trim();
            CancelTxCellEdit();

            if (item != null && column >= 0)
            {
                bool committed = ApplyTxCellEdit(item, column, byteIndex, text);
                if (autoAdvance && committed && IsTxByteColumn(column) && byteIndex >= 0 && byteIndex + 1 < TxByteCount)
                {
                    BeginTxCellEdit(item, column + 1);
                }
            }
        }
        finally
        {
            _committingTxCellEdit = false;
        }
    }

    private void ToggleTxFrameType(ListViewItem item)
    {
        if (item.Tag is not CanTxFrame oldFrame)
        {
            return;
        }

        bool extended = !oldFrame.Extended;
        if (!extended && oldFrame.Id > 0x7FF)
        {
            SetStatus("标准帧ID最大是 7FF，当前ID只能用扩展帧");
            return;
        }

        ReplaceTxFrame(item, new CanTxFrame(oldFrame.Id, extended, oldFrame.Data, oldFrame.PeriodMs), oldFrame);
    }

    private bool ApplyTxCellEdit(ListViewItem item, int column, int byteIndex, string text)
    {
        if (item.Tag is not CanTxFrame oldFrame)
        {
            return false;
        }

        try
        {
            uint id = oldFrame.Id;
            bool extended = oldFrame.Extended;
            byte[] data = oldFrame.Data.ToArray();
            int periodMs = oldFrame.PeriodMs;

            if (column == 0)
            {
                id = ParseId(text);
                if (!extended && id > 0x7FF)
                {
                    extended = true;
                }
            }
            else if (column == 2)
            {
                if (!int.TryParse(text, out int length))
                {
                    throw new InvalidOperationException("长度必须是 0-8");
                }

                data = ResizeData(data, Math.Clamp(length, 0, 8));
            }
            else if (column == 3)
            {
                if (!int.TryParse(text, out periodMs))
                {
                    throw new InvalidOperationException("周期必须是数字");
                }

                periodMs = Math.Clamp(periodMs, 10, 60000);
            }
            else if (IsTxByteColumn(column))
            {
                int index = byteIndex >= 0 ? byteIndex : column - TxByteStartColumn;
                if (index < 0 || index >= TxByteCount)
                {
                    throw new InvalidOperationException("字节位置超出范围");
                }

                if (index >= data.Length)
                {
                    data = ResizeData(data, index + 1);
                }

                string value = string.IsNullOrWhiteSpace(text) ? "00" : text;
                data[index] = Convert.ToByte(value.PadLeft(2, '0'), 16);
            }

            if (!extended && id > 0x7FF)
            {
                throw new InvalidOperationException("标准帧ID最大是 7FF");
            }

            ReplaceTxFrame(item, new CanTxFrame(id, extended, data, periodMs), oldFrame);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("列表编辑失败：" + ex.Message);
            return false;
        }
    }

    private void ReplaceTxFrame(ListViewItem item, CanTxFrame frame, CanTxFrame oldFrame)
    {
        frame.SendCount = oldFrame.SendCount;
        frame.NextDueTime = oldFrame.NextDueTime == DateTime.MinValue
            ? DateTime.MinValue
            : DateTime.Now.AddMilliseconds(Math.Max(10, frame.PeriodMs));
        item.Tag = frame;
        UpdateTxListItem(item, frame);
        if (item.Selected)
        {
            LoadSelectedToEditor();
        }

        SaveSettings();
        SetStatus("发送列表已更新：" + FormatId(frame.Id, frame.Extended));
    }

    private static byte[] ResizeData(byte[] data, int length)
    {
        byte[] resized = new byte[length];
        Array.Copy(data, resized, Math.Min(data.Length, length));
        return resized;
    }

    private static byte[] ParseDataBytes(string text)
    {
        string compact = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ")
            .Replace(";", " ")
            .Replace("-", " ")
            .Trim();

        string[] parts = compact.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Length > 2)
        {
            string merged = parts[0];
            if (merged.Length % 2 != 0)
            {
                merged = "0" + merged;
            }

            parts = Enumerable.Range(0, merged.Length / 2)
                .Select(i => merged.Substring(i * 2, 2))
                .ToArray();
        }

        if (parts.Length > 8)
        {
            throw new InvalidOperationException("数据最多 8 个字节");
        }

        byte[] data = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            data[i] = Convert.ToByte(parts[i], 16);
        }

        return data;
    }

    private void SendSelectedFrame()
    {
        if (!_connected || _txList.SelectedItems.Count == 0)
        {
            return;
        }

        try
        {
            SyncSelectedSendRowFromEditor();
            ListViewItem item = _txList.SelectedItems[0];
            if (item.Tag is CanTxFrame frame)
            {
                SendTxFrame(frame);
                UpdateTxListItem(item, frame);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "发送失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadSelectedToEditor()
    {
        if (_txList.SelectedItems.Count == 0 || _txList.SelectedItems[0].Tag is not CanTxFrame frame)
        {
            return;
        }

        _loadingEditorFromList = true;
        try
        {
            _idBox.Text = frame.Id.ToString(frame.Extended ? "X8" : "X3");
            _frameTypeBox.SelectedIndex = frame.Extended ? 1 : 0;
            _lengthBox.Value = frame.Data.Length;
            _periodBox.Value = Math.Clamp(frame.PeriodMs, (int)_periodBox.Minimum, (int)_periodBox.Maximum);
            for (int i = 0; i < _byteBoxes.Length; i++)
            {
                _byteBoxes[i].Text = i < frame.Data.Length ? frame.Data[i].ToString("X2") : "00";
            }
        }
        finally
        {
            _loadingEditorFromList = false;
        }
    }

    private CanTxFrame ReadEditorFrame()
    {
        uint id = ParseId(_idBox.Text);
        byte[] data = BuildSendData();
        bool extended = _frameTypeBox.SelectedIndex == 1;
        if (!extended && id > 0x7FF)
        {
            throw new InvalidOperationException("标准帧ID最大是 7FF。要发更大的ID，请选择扩展帧。");
        }

        return new CanTxFrame(id, extended, data, (int)_periodBox.Value);
    }

    private void SendTxFrame(CanTxFrame frame)
    {
        ICanAdapter adapter = _adapter ?? throw new InvalidOperationException("CAN 未连接。");
        adapter.Send(frame);

        frame.SendCount++;
        _txCount++;
        AddRow("发送", frame.Id, frame.Extended, frame.Data);
        SetStatus($"已连接，收 {_rxCount}，发 {_txCount}，记录 {_logRows.Count}");
    }

    private static void UpdateTxListItem(ListViewItem item, CanTxFrame frame)
    {
        item.SubItems[0].Text = FormatId(frame.Id, frame.Extended);
        item.SubItems[1].Text = frame.Extended ? "扩展" : "标准";
        item.SubItems[2].Text = frame.Data.Length.ToString();
        item.SubItems[3].Text = frame.PeriodMs.ToString();
        for (int i = 0; i < TxByteCount; i++)
        {
            item.SubItems[TxByteStartColumn + i].Text = GetTxByteText(frame, i);
        }

        item.SubItems[TxCountColumn].Text = frame.SendCount.ToString();
    }

    private void PollReceive()
    {
        ICanAdapter? adapter = _adapter;
        if (!_connected || adapter == null)
        {
            return;
        }

        for (int i = 0; i < 200; i++)
        {
            if (!adapter.TryReceive(out CanRxFrame frame))
            {
                break;
            }

            _rxCount++;
            AddRow("接收", frame.Id, frame.Extended, frame.Data);
        }

        if (_rxCount + _txCount > 0 && (DateTime.Now - _lastStatusUpdate).TotalMilliseconds >= 500)
        {
            SetStatus($"已连接，收 {_rxCount}，发 {_txCount}，记录 {_logRows.Count}");
            _lastStatusUpdate = DateTime.Now;
        }
    }

    private void AddRow(string direction, uint id, bool extended, byte[] data)
    {
        DateTime now = DateTime.Now;
        string key = MakeKey(direction, id, extended);
        bool changed = true;
        double cycleMs = 0;

        if (_summaries.TryGetValue(key, out FrameSummary? summary))
        {
            cycleMs = (now - summary.LastTime).TotalMilliseconds;
            summary.Count++;
            summary.LastTime = now;
            summary.CycleMs = cycleMs;
            changed = !data.SequenceEqual(summary.Data);
            if (changed)
            {
                summary.Data = data.ToArray();
                summary.ChangeCount++;
            }
        }
        else
        {
            summary = new FrameSummary(direction, id, extended, data.ToArray(), now);
            _summaries.Add(key, summary);
        }

        if (changed)
        {
            _logRows.Add(new CanLogRow(now, direction, id, extended, data, cycleMs, summary.Count));
        }

        if (_pauseDisplayBox.Checked)
        {
            return;
        }

        _dirtySummaryKeys.Add(key);
    }

    private void RefreshDirtySummaries()
    {
        if (_pauseDisplayBox.Checked || _dirtySummaryKeys.Count == 0)
        {
            return;
        }

        string[] keys = _dirtySummaryKeys.ToArray();
        _dirtySummaryKeys.Clear();

        _rxList.BeginUpdate();
        try
        {
            foreach (string key in keys)
            {
                if (!_summaries.TryGetValue(key, out FrameSummary? summary))
                {
                    continue;
                }

                if (summary.Item == null)
                {
                    summary.Item = CreateSummaryItem(summary);
                    InsertSummaryItem(summary.Item, summary.Id, summary.Direction);
                }
                else
                {
                    UpdateSummaryItem(summary.Item, summary);
                }
            }
        }
        finally
        {
            _rxList.EndUpdate();
        }
    }

    private static ListViewItem CreateSummaryItem(FrameSummary summary)
    {
        var item = new ListViewItem(summary.LastTime.ToString("HH:mm:ss.fff"));
        item.UseItemStyleForSubItems = false;
        item.SubItems.Add(summary.Direction);
        item.SubItems.Add("0x" + summary.Id.ToString(summary.Extended ? "X8" : "X3"));
        item.SubItems.Add(summary.Extended ? "扩展" : "标准");
        item.SubItems.Add(summary.Data.Length.ToString());
        item.SubItems.Add(summary.CycleMs <= 0 ? "" : summary.CycleMs.ToString("0"));
        item.SubItems.Add(summary.Count.ToString());
        item.SubItems.Add(BytesToHex(summary.Data));
        StyleRxDataSubItem(item);
        return item;
    }

    private static void UpdateSummaryItem(ListViewItem item, FrameSummary summary)
    {
        item.SubItems[0].Text = summary.LastTime.ToString("HH:mm:ss.fff");
        item.SubItems[4].Text = summary.Data.Length.ToString();
        item.SubItems[5].Text = summary.CycleMs <= 0 ? "" : summary.CycleMs.ToString("0");
        item.SubItems[6].Text = summary.Count.ToString();
        item.SubItems[7].Text = BytesToHex(summary.Data);
        StyleRxDataSubItem(item);
    }

    private static void StyleRxDataSubItem(ListViewItem item)
    {
        if (item.SubItems.Count <= 7)
        {
            return;
        }

        item.SubItems[7].BackColor = RxDataBackColor;
        item.SubItems[7].ForeColor = RxDataForeColor;
    }

    private void InsertSummaryItem(ListViewItem item, uint id, string direction)
    {
        int insertAt = _rxList.Items.Count;
        for (int i = 0; i < _rxList.Items.Count; i++)
        {
            ListViewItem current = _rxList.Items[i];
            uint currentId = ParseDisplayedId(current.SubItems[2].Text);
            int cmp = id.CompareTo(currentId);
            if (cmp < 0 || (cmp == 0 && string.Compare(direction, current.SubItems[1].Text, StringComparison.Ordinal) < 0))
            {
                insertAt = i;
                break;
            }
        }

        _rxList.Items.Insert(insertAt, item);
    }

    private void SaveCsv()
    {
        if (_logRows.Count == 0)
        {
            MessageBox.Show("还没有CAN数据。", "保存CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "保存CAN数据",
            Filter = "CSV文件 (*.csv)|*.csv",
            FileName = "CAN记录_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("时间,方向,ID,类型,长度,周期ms,次数,数据");
        foreach (CanLogRow row in _logRows)
        {
            sb.Append(Csv(row.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))).Append(',');
            sb.Append(Csv(row.Direction)).Append(',');
            sb.Append(Csv("0x" + row.Id.ToString(row.Extended ? "X8" : "X3"))).Append(',');
            sb.Append(Csv(row.Extended ? "扩展帧" : "标准帧")).Append(',');
            sb.Append(row.Data.Length).Append(',');
            sb.Append(row.CycleMs <= 0 ? "" : row.CycleMs.ToString("0")).Append(',');
            sb.Append(row.Count).Append(',');
            sb.AppendLine(Csv(BytesToHex(row.Data)));
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        SetStatus("已保存CSV：" + dialog.FileName);
    }

    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        try
        {
            var settings = new AppSettings
            {
                Baud = _baudBox.Text,
                SendPanelHeight = _mainSplit.IsHandleCreated ? _mainSplit.Panel2.Height : _savedSendPanelHeight,
                Editor = BuildEditorSettings(),
                SendList = _txList.Items
                    .Cast<ListViewItem>()
                    .Select(item => item.Tag)
                    .OfType<CanTxFrame>()
                    .Select(ToSettings)
                    .ToList(),
            };

            string? dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            SetStatus("保存配置失败：" + ex.Message);
        }
    }

    private void ScheduleSaveSettings()
    {
        if (_loadingSettings || IsDisposed)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void OnEditorChanged()
    {
        if (_loadingSettings || _loadingEditorFromList)
        {
            return;
        }

        SyncSelectedSendRowFromEditor();
        ScheduleSaveSettings();
    }

    private void WireEditorAutoSave()
    {
        _idBox.TextChanged += (_, _) => OnEditorChanged();
        _frameTypeBox.SelectedIndexChanged += (_, _) => OnEditorChanged();
        _lengthBox.ValueChanged += (_, _) => OnEditorChanged();
        _periodBox.ValueChanged += (_, _) => OnEditorChanged();
        foreach (TextBox box in _byteBoxes)
        {
            box.TextChanged += (_, _) => OnEditorChanged();
        }
    }

    private void LoadSettings()
    {
        try
        {
            string loadPath = File.Exists(_settingsPath) ? _settingsPath : _legacySettingsPath;
            if (!File.Exists(loadPath))
            {
                return;
            }

            string json = File.ReadAllText(loadPath, Encoding.UTF8);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string baud = ReadJsonString(root, nameof(AppSettings.Baud), "250K");
            if (!string.IsNullOrWhiteSpace(baud) && _baudBox.Items.Contains(baud))
            {
                _baudBox.SelectedItem = baud;
            }

            if (root.TryGetProperty(nameof(AppSettings.Editor), out JsonElement editorElement)
                && editorElement.ValueKind == JsonValueKind.Object)
            {
                ApplyFrameSettingsToEditor(ReadFrameSettings(editorElement));
            }

            _savedSendPanelHeight = ReadJsonInt(root, nameof(AppSettings.SendPanelHeight), 220);

            _txList.Items.Clear();
            if (root.TryGetProperty(nameof(AppSettings.SendList), out JsonElement sendListElement)
                && sendListElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in sendListElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    AddFrameToSendList(FromSettings(ReadFrameSettings(item)));
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("读取配置失败：" + ex.Message);
        }
    }

    private static CanTxFrameSettings ReadFrameSettings(JsonElement element)
    {
        return new CanTxFrameSettings
        {
            Id = ReadJsonString(element, nameof(CanTxFrameSettings.Id), "0x123"),
            Extended = ReadJsonBool(element, nameof(CanTxFrameSettings.Extended), false),
            Length = ReadJsonInt(element, nameof(CanTxFrameSettings.Length), 8),
            Data = ReadJsonStringArray(element, nameof(CanTxFrameSettings.Data)),
            PeriodMs = ReadJsonInt(element, nameof(CanTxFrameSettings.PeriodMs), 1000),
        };
    }

    private static string ReadJsonString(JsonElement element, string name, string fallback)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static bool ReadJsonBool(JsonElement element, string name, bool fallback)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static int ReadJsonInt(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : fallback;
    }

    private static string[] ReadJsonStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "00")
                .ToArray();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
        }

        return Array.Empty<string>();
    }

    private void ApplySavedSendPanelHeight()
    {
        if (!_mainSplit.IsHandleCreated || _savedSendPanelHeight <= 0 || _mainSplit.Height <= 0)
        {
            return;
        }

        int minTop = _mainSplit.Panel1MinSize;
        int minBottom = _mainSplit.Panel2MinSize;
        int available = Math.Max(minTop + minBottom, _mainSplit.Height - _mainSplit.SplitterWidth);
        int bottomHeight = Math.Clamp(_savedSendPanelHeight, minBottom, Math.Max(minBottom, available - minTop));
        _mainSplit.SplitterDistance = Math.Max(minTop, available - bottomHeight);
    }

    private CanTxFrameSettings BuildEditorSettings()
    {
        try
        {
            return ToSettings(ReadEditorFrame());
        }
        catch
        {
            return new CanTxFrameSettings
            {
                Id = _idBox.Text.Trim(),
                Extended = _frameTypeBox.SelectedIndex == 1,
                Data = _byteBoxes.Select(box => box.Text.Trim()).ToArray(),
                Length = (int)_lengthBox.Value,
                PeriodMs = (int)_periodBox.Value,
            };
        }
    }

    private void ApplyFrameSettingsToEditor(CanTxFrameSettings frame)
    {
        _idBox.Text = string.IsNullOrWhiteSpace(frame.Id) ? "123" : frame.Id;
        _frameTypeBox.SelectedIndex = frame.Extended ? 1 : 0;
        _lengthBox.Value = Math.Clamp(frame.Length, (int)_lengthBox.Minimum, (int)_lengthBox.Maximum);
        _periodBox.Value = Math.Clamp(frame.PeriodMs <= 0 ? 1000 : frame.PeriodMs, (int)_periodBox.Minimum, (int)_periodBox.Maximum);

        for (int i = 0; i < _byteBoxes.Length; i++)
        {
            _byteBoxes[i].Text = frame.Data.Length > i && !string.IsNullOrWhiteSpace(frame.Data[i])
                ? frame.Data[i]
                : "00";
        }

        UpdateByteBoxState();
    }

    private void UpdateByteBoxState()
    {
        int len = (int)_lengthBox.Value;
        for (int i = 0; i < _byteBoxes.Length; i++)
        {
            _byteBoxes[i].Enabled = i < len;
        }
    }

    private void AutoAdvanceByteBox(TextBox box)
    {
        if (!box.Focused || box.TextLength < 2 || box.Tag is not int index)
        {
            return;
        }

        int len = (int)_lengthBox.Value;
        for (int i = index + 1; i < len && i < _byteBoxes.Length; i++)
        {
            if (_byteBoxes[i].Enabled)
            {
                _byteBoxes[i].Focus();
                _byteBoxes[i].SelectAll();
                return;
            }
        }
    }

    private static void FilterHexKey(KeyPressEventArgs e)
    {
        if (char.IsControl(e.KeyChar))
        {
            return;
        }

        bool isHex = e.KeyChar is >= '0' and <= '9'
            || e.KeyChar is >= 'a' and <= 'f'
            || e.KeyChar is >= 'A' and <= 'F';
        e.Handled = !isHex;
    }

    private byte[] BuildSendData()
    {
        int len = (int)_lengthBox.Value;
        var data = new byte[len];
        for (int i = 0; i < len; i++)
        {
            NormalizeByteBox(_byteBoxes[i]);
            data[i] = Convert.ToByte(_byteBoxes[i].Text, 16);
        }

        return data;
    }

    private static void NormalizeByteBox(TextBox box)
    {
        string text = box.Text.Trim();
        if (text.Length == 0)
        {
            box.Text = "00";
            return;
        }

        byte value = Convert.ToByte(text, 16);
        box.Text = value.ToString("X2");
    }

    private void SetStatus(string text) => _status.Text = text;

    private ushort GetSelectedBaud() => _baudBox.Text switch
    {
        "1M" => 0x0014,
        "500K" => 0x001C,
        "250K" => 0x011C,
        "125K" => 0x031C,
        "100K" => 0x432F,
        "50K" => 0x472F,
        _ => 0x011C,
    };

    private static uint ParseId(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return Convert.ToUInt32(text, 16);
    }

    private static uint ParseDisplayedId(string text) => ParseId(text);

    private static string MakeKey(string direction, uint id, bool extended)
        => direction + "|" + (extended ? "E" : "S") + "|" + id.ToString("X8");

    private static string FormatId(uint id, bool extended) => "0x" + id.ToString(extended ? "X8" : "X3");

    private static CanTxFrameSettings ToSettings(CanTxFrame frame) => new()
    {
        Id = FormatId(frame.Id, frame.Extended),
        Extended = frame.Extended,
        Length = frame.Data.Length,
        Data = frame.Data.Select(value => value.ToString("X2")).ToArray(),
        PeriodMs = frame.PeriodMs,
    };

    private static CanTxFrame FromSettings(CanTxFrameSettings settings)
    {
        uint id = ParseId(settings.Id);
        int len = Math.Clamp(settings.Length, 0, 8);
        byte[] data = new byte[len];
        for (int i = 0; i < len; i++)
        {
            string value = settings.Data.Length > i ? settings.Data[i] : "00";
            data[i] = Convert.ToByte(string.IsNullOrWhiteSpace(value) ? "00" : value, 16);
        }

        bool extended = settings.Extended;
        if (!extended && id > 0x7FF)
        {
            extended = true;
        }

        return new CanTxFrame(id, extended, data, settings.PeriodMs <= 0 ? 1000 : settings.PeriodMs);
    }

    private static string BytesToHex(byte[] data, string separator = " ") => string.Join(separator, data.Select(value => value.ToString("X2")));

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}

internal sealed record CanLogRow(DateTime Time, string Direction, uint Id, bool Extended, byte[] Data, double CycleMs, ulong Count);

internal sealed class CanTxFrame
{
    internal CanTxFrame(uint id, bool extended, byte[] data, int periodMs)
    {
        Id = id;
        Extended = extended;
        Data = data.ToArray();
        PeriodMs = Math.Max(10, periodMs);
    }

    internal uint Id { get; }
    internal bool Extended { get; }
    internal byte[] Data { get; }
    internal int PeriodMs { get; }
    internal ulong SendCount { get; set; }
    internal DateTime NextDueTime { get; set; } = DateTime.MinValue;
}

internal sealed class AppSettings
{
    public string Baud { get; set; } = "250K";
    public int SendPanelHeight { get; set; } = 220;
    public CanTxFrameSettings? Editor { get; set; }
    public List<CanTxFrameSettings> SendList { get; set; } = new();
}

internal sealed class CanTxFrameSettings
{
    public string Id { get; set; } = "0x123";
    public bool Extended { get; set; }
    public int Length { get; set; } = 8;
    public string[] Data { get; set; } = Array.Empty<string>();
    public int PeriodMs { get; set; } = 1000;
}

internal sealed class FrameSummary
{
    internal FrameSummary(string direction, uint id, bool extended, byte[] data, DateTime now)
    {
        Direction = direction;
        Id = id;
        Extended = extended;
        Data = data;
        FirstTime = now;
        LastTime = now;
    }

    internal string Direction { get; }
    internal uint Id { get; }
    internal bool Extended { get; }
    internal byte[] Data { get; set; }
    internal DateTime FirstTime { get; }
    internal DateTime LastTime { get; set; }
    internal double CycleMs { get; set; }
    internal ulong Count { get; set; } = 1;
    internal ulong ChangeCount { get; set; } = 1;
    internal ListViewItem? Item { get; set; }
}

internal sealed class BufferedListView : ListView
{
    internal BufferedListView()
    {
        DoubleBuffered = true;
        ResizeRedraw = false;
    }
}

internal readonly record struct CanRxFrame(uint Id, bool Extended, byte[] Data);

internal interface ICanAdapter : IDisposable
{
    string Name { get; }
    void Open(string baud);
    void Close();
    void Send(CanTxFrame frame);
    bool TryReceive(out CanRxFrame frame);
}

internal static class CanBaud
{
    internal static ushort ToPcan(string baud) => baud switch
    {
        "1M" => 0x0014,
        "500K" => 0x001C,
        "250K" => 0x011C,
        "125K" => 0x031C,
        "100K" => 0x432F,
        "50K" => 0x472F,
        _ => 0x011C,
    };

    internal static (byte Timing0, byte Timing1) ToGc(string baud) => baud switch
    {
        "1M" => (0x00, 0x14),
        "500K" => (0x00, 0x1C),
        "250K" => (0x01, 0x1C),
        "125K" => (0x03, 0x1C),
        "100K" => (0x04, 0x1C),
        "50K" => (0x09, 0x1C),
        _ => (0x01, 0x1C),
    };

    internal static string ToSysEnumName(string baud) => baud switch
    {
        "1M" => "USBCAN_BAUD_1MBit",
        "500K" => "USBCAN_BAUD_500kBit",
        "250K" => "USBCAN_BAUD_250kBit",
        "125K" => "USBCAN_BAUD_125kBit",
        "100K" => "USBCAN_BAUD_100kBit",
        "50K" => "USBCAN_BAUD_50kBit",
        _ => "USBCAN_BAUD_250kBit",
    };
}

internal sealed class PeakCanAdapter : ICanAdapter
{
    private ushort _channel;
    private bool _opened;

    public string Name => "PEAK PCAN-USB";

    public void Open(string baud)
    {
        ushort pcanBaud = CanBaud.ToPcan(baud);
        foreach (ushort channel in Pcan.UsbChannels)
        {
            uint status = Pcan.Initialize(channel, pcanBaud, 0, 0, 0);
            if (status == Pcan.ErrorOk)
            {
                _channel = channel;
                _opened = true;
                Pcan.Reset(_channel);
                return;
            }
        }

        throw new InvalidOperationException("没有打开 PEAK PCAN-USB。");
    }

    public void Close()
    {
        if (_opened)
        {
            Pcan.Uninitialize(_channel);
            _opened = false;
            _channel = 0;
        }
    }

    public void Dispose() => Close();

    public void Send(CanTxFrame frame)
    {
        EnsureOpen();
        var msg = new Pcan.Msg
        {
            Id = frame.Id,
            MsgType = frame.Extended ? Pcan.MessageExtended : Pcan.MessageStandard,
            Len = (byte)frame.Data.Length,
            Data = new byte[8],
        };
        Buffer.BlockCopy(frame.Data, 0, msg.Data, 0, frame.Data.Length);

        uint status = Pcan.Write(_channel, ref msg);
        if (status != Pcan.ErrorOk)
        {
            throw new InvalidOperationException("发送失败，错误码 0x" + status.ToString("X"));
        }
    }

    public bool TryReceive(out CanRxFrame frame)
    {
        EnsureOpen();
        uint status = Pcan.Read(_channel, out Pcan.Msg msg, out _);
        if (status == Pcan.ErrorQrcvEmpty || status != Pcan.ErrorOk)
        {
            frame = default;
            return false;
        }

        int len = Math.Min(msg.Len, (byte)8);
        byte[] data = new byte[len];
        if (msg.Data != null && len > 0)
        {
            Buffer.BlockCopy(msg.Data, 0, data, 0, len);
        }

        frame = new CanRxFrame(msg.Id, (msg.MsgType & Pcan.MessageExtended) != 0, data);
        return true;
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("PEAK 未连接。");
        }
    }
}

internal sealed class GcCanAdapter : ICanAdapter
{
    private const int ReceiveBatchSize = 128;
    private static readonly uint[] DeviceTypes = { 4, 3, 21, 20 };
    private readonly ConcurrentQueue<CanRxFrame> _rx = new();
    private readonly GcNative.VCI_CAN_OBJ[] _receiveBuffer = CreateBuffer(ReceiveBatchSize);
    private readonly GcNative.VCI_CAN_OBJ[] _sendBuffer = CreateBuffer(1);
    private uint _deviceType;
    private readonly uint _deviceIndex = 0;
    private readonly uint _channel = 0;
    private bool _opened;

    public string Name => "GC";

    public void Open(string baud)
    {
        string requiredDll = Environment.Is64BitProcess ? "ECanVci64.dll" : "ECanVci.dll";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, requiredDll)))
        {
            throw new FileNotFoundException("缺少 " + requiredDll);
        }

        GcNative.SetDllDirectory(AppContext.BaseDirectory);
        (byte timing0, byte timing1) = CanBaud.ToGc(baud);
        var config = new GcNative.VCI_INIT_CONFIG
        {
            AccCode = 0,
            AccMask = 0xFFFFFFFF,
            Reserved = 0,
            Filter = 1,
            Timing0 = timing0,
            Timing1 = timing1,
            Mode = 0
        };

        foreach (uint deviceType in DeviceTypes)
        {
            GcNative.CloseDevice(deviceType, _deviceIndex);
            if (GcNative.OpenDevice(deviceType, _deviceIndex, 0) != 1)
            {
                continue;
            }

            if (GcNative.InitCAN(deviceType, _deviceIndex, _channel, ref config) != 1)
            {
                ReleaseDevice(deviceType);
                continue;
            }

            if (GcNative.StartCAN(deviceType, _deviceIndex, _channel) != 1)
            {
                ReleaseDevice(deviceType);
                continue;
            }

            GcNative.ClearBuffer(deviceType, _deviceIndex, _channel);
            _deviceType = deviceType;
            _opened = true;
            ClearLocalQueue();
            return;
        }

        throw new InvalidOperationException("未找到广成 GC CAN 适配器。");
    }

    public void Close()
    {
        if (_opened)
        {
            ReleaseDevice(_deviceType);
            _opened = false;
            ClearLocalQueue();
        }
    }

    public void Dispose() => Close();

    public void Send(CanTxFrame frame)
    {
        EnsureOpen();
        GcNative.VCI_CAN_OBJ obj = _sendBuffer[0];
        obj.ID = frame.Id;
        obj.SendType = 0;
        obj.RemoteFlag = 0;
        obj.ExternFlag = frame.Extended ? (byte)1 : (byte)0;
        obj.DataLen = (byte)Math.Min(frame.Data.Length, 8);
        Array.Clear(obj.Data, 0, obj.Data.Length);
        Buffer.BlockCopy(frame.Data, 0, obj.Data, 0, Math.Min(frame.Data.Length, 8));
        _sendBuffer[0] = obj;

        if (GcNative.Transmit(_deviceType, _deviceIndex, _channel, _sendBuffer, 1) != 1)
        {
            throw new InvalidOperationException("广成 GC CAN 发送失败。");
        }
    }

    public bool TryReceive(out CanRxFrame frame)
    {
        EnsureOpen();
        if (_rx.TryDequeue(out frame))
        {
            return true;
        }

        uint count = GcNative.Receive(_deviceType, _deviceIndex, _channel, _receiveBuffer, (uint)_receiveBuffer.Length, 0);
        if (count == 0)
        {
            frame = default;
            return false;
        }

        int frameCount = (int)Math.Min(count, (uint)_receiveBuffer.Length);
        frame = CopyFrame(_receiveBuffer[0]);
        for (int i = 1; i < frameCount; i++)
        {
            _rx.Enqueue(CopyFrame(_receiveBuffer[i]));
        }

        return true;
    }

    private static GcNative.VCI_CAN_OBJ[] CreateBuffer(int length)
    {
        var buffer = new GcNative.VCI_CAN_OBJ[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i].Data = new byte[8];
            buffer[i].Reserved = new byte[3];
        }

        return buffer;
    }

    private static CanRxFrame CopyFrame(GcNative.VCI_CAN_OBJ obj)
    {
        int len = Math.Min(obj.DataLen, (byte)8);
        byte[] data = new byte[len];
        if (obj.Data != null && len > 0)
        {
            Buffer.BlockCopy(obj.Data, 0, data, 0, len);
        }

        return new CanRxFrame(obj.ID, obj.ExternFlag != 0, data);
    }

    private void ReleaseDevice(uint deviceType)
    {
        try { GcNative.ClearBuffer(deviceType, _deviceIndex, _channel); } catch { }
        try { GcNative.ResetCAN(deviceType, _deviceIndex, _channel); } catch { }
        try { GcNative.CloseDevice(deviceType, _deviceIndex); } catch { }
    }

    private void ClearLocalQueue()
    {
        while (_rx.TryDequeue(out _))
        {
        }
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("广成 GC 未连接。");
        }
    }
}

internal sealed class SysCanAdapter : ICanAdapter
{
    private Type? _serverType;
    private object? _device;
    private Type? _msgType;
    private byte _ch0;
    private byte _anyChannel;
    private bool _opened;
    private readonly ConcurrentQueue<CanRxFrame> _rx = new();

    public string Name => "SYS";

    public void Open(string baud)
    {
        string appDir = AppContext.BaseDirectory;
        SysNative.SetDllDirectory(appDir);
        string ucanPath = Path.Combine(appDir, "UcanDotNET.dll");
        string usbCanPath = Path.Combine(appDir, "usbcan32.dll");
        if (!File.Exists(ucanPath))
        {
            throw new FileNotFoundException("缺少 UcanDotNET.dll", ucanPath);
        }
        if (!File.Exists(usbCanPath))
        {
            throw new FileNotFoundException("缺少 usbcan32.dll", usbCanPath);
        }

        Assembly asm = Assembly.LoadFrom(ucanPath);
        _serverType = asm.GetType("UcanDotNET.USBcanServer", true)!;
        _device = Activator.CreateInstance(_serverType);
        _msgType = asm.GetType("UcanDotNET.USBcanServer+tCanMsgStruct", true)!;

        Type channelType = asm.GetType("UcanDotNET.USBcanServer+eUcanChannel", true)!;
        Type baudType = asm.GetType("UcanDotNET.USBcanServer+eUcanBaudrate", true)!;
        Type baudExType = asm.GetType("UcanDotNET.USBcanServer+eUcanBaudrateEx", true)!;
        Type modeType = asm.GetType("UcanDotNET.USBcanServer+tUcanMode", true)!;
        Type resetType = asm.GetType("UcanDotNET.USBcanServer+eUcanResetFlags", true)!;

        byte anyModule = Convert.ToByte(_serverType.GetField("USBCAN_ANY_MODULE")!.GetValue(null));
        _ch0 = Convert.ToByte(Enum.Parse(channelType, "USBCAN_CHANNEL_CH0"));
        _anyChannel = Convert.ToByte(Enum.Parse(channelType, "USBCAN_CHANNEL_ANY"));
        short baudValue = Convert.ToInt16(Enum.Parse(baudType, CanBaud.ToSysEnumName(baud)));
        int baudEx = Convert.ToInt32(Enum.Parse(baudExType, "USBCAN_BAUDEX_USE_BTR01"));
        byte normalMode = Convert.ToByte(Enum.Parse(modeType, "kUcanModeNormal"));
        int resetFlags = Convert.ToInt32(Enum.Parse(resetType, "USBCAN_RESET_ONLY_ALL_BUFF"));
        int amrAll = Convert.ToInt32(_serverType.GetField("USBCAN_AMR_ALL")!.GetValue(null));
        int acrAll = Convert.ToInt32(_serverType.GetField("USBCAN_ACR_ALL")!.GetValue(null));

        byte initHardwareResult = Convert.ToByte(_serverType.GetMethod("InitHardware")!.Invoke(_device, new object[] { anyModule }));
        if (initHardwareResult != 0)
        {
            throw new InvalidOperationException("未检测到可用 SYS 适配器。");
        }

        byte initCanResult = Convert.ToByte(_serverType.GetMethod("InitCan")!.Invoke(_device, new object[] { _ch0, baudValue, baudEx, amrAll, acrAll, normalMode, (byte)0x1A }));
        if (initCanResult != 0)
        {
            throw new InvalidOperationException("未能初始化 SYS CAN。");
        }

        _serverType.GetMethod("ResetCan")!.Invoke(_device, new object[] { _ch0, resetFlags });
        _opened = true;
        DrainReceiveQueue();
    }

    public void Close()
    {
        if (!_opened && _device == null)
        {
            return;
        }

        try
        {
            _serverType?.GetMethod("Shutdown")?.Invoke(_device, new object[] { _ch0, true });
        }
        catch
        {
        }

        try
        {
            if (_device is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                _serverType?.GetMethod("Dispose")?.Invoke(_device, Array.Empty<object>());
            }
        }
        catch
        {
        }

        _opened = false;
        _device = null;
        _serverType = null;
        _msgType = null;
        ClearLocalQueue();
    }

    public void Dispose() => Close();

    public void Send(CanTxFrame frame)
    {
        EnsureOpen();
        Array msgArray = Array.CreateInstance(_msgType!, 1);
        object msg = _msgType!.GetMethod("CreateInstance")!.Invoke(null, new object[] { (int)frame.Id, frame.Extended ? (byte)1 : (byte)0 })!;
        _msgType.GetField("m_bDLC")!.SetValue(msg, (byte)Math.Min(frame.Data.Length, 8));
        byte[] payload = (byte[])_msgType.GetField("m_bData")!.GetValue(msg)!;
        Buffer.BlockCopy(frame.Data, 0, payload, 0, Math.Min(frame.Data.Length, 8));
        msgArray.SetValue(msg, 0);
        object[] args = { _ch0, msgArray, 0 };
        object ret = _serverType!.GetMethod("WriteCanMsg")!.Invoke(_device, args)!;
        if (Convert.ToByte(ret) != 0)
        {
            throw new InvalidOperationException("SYS CAN 发送失败。");
        }
    }

    public bool TryReceive(out CanRxFrame frame)
    {
        EnsureOpen();
        if (_rx.TryDequeue(out frame))
        {
            return true;
        }

        Array msgArray = Array.CreateInstance(_msgType!, 64);
        for (int i = 0; i < msgArray.Length; i++)
        {
            object msg = _msgType!.GetMethod("CreateInstance")!.Invoke(null, new object[] { 0, (byte)0 })!;
            msgArray.SetValue(msg, i);
        }

        object[] args = { _anyChannel, msgArray, 0 };
        object retObj = _serverType!.GetMethod("ReadCanMsg")!.Invoke(_device, args)!;
        int count = Convert.ToInt32(args[2]);
        if (Convert.ToByte(retObj) != 0 || count <= 0)
        {
            frame = default;
            return false;
        }

        for (int i = 0; i < count && i < msgArray.Length; i++)
        {
            object msg = msgArray.GetValue(i)!;
            uint id = Convert.ToUInt32(_msgType!.GetField("m_dwID")!.GetValue(msg));
            byte dlc = Convert.ToByte(_msgType.GetField("m_bDLC")!.GetValue(msg));
            byte[] source = (byte[])_msgType.GetField("m_bData")!.GetValue(msg)!;
            int len = Math.Min(dlc, (byte)8);
            byte[] data = new byte[len];
            Buffer.BlockCopy(source, 0, data, 0, len);
            byte type = Convert.ToByte(_msgType.GetField("m_bFF")?.GetValue(msg) ?? (byte)0);
            _rx.Enqueue(new CanRxFrame(id, type != 0, data));
        }

        return _rx.TryDequeue(out frame);
    }

    private void DrainReceiveQueue()
    {
        for (int i = 0; i < 8; i++)
        {
            if (!TryReceive(out _))
            {
                break;
            }
        }
    }

    private void ClearLocalQueue()
    {
        while (_rx.TryDequeue(out _))
        {
        }
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("SYS 未连接。");
        }
    }
}

internal static class SysNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string lpPathName);
}

internal static class GcNative
{
    internal static bool SetDllDirectory(string lpPathName) => GcKernel.SetDllDirectory(lpPathName);
    internal static uint OpenDevice(uint deviceType, uint deviceIndex, uint reserved) => Environment.Is64BitProcess
        ? Gc64.OpenDevice(deviceType, deviceIndex, reserved)
        : Gc32.OpenDevice(deviceType, deviceIndex, reserved);
    internal static uint CloseDevice(uint deviceType, uint deviceIndex) => Environment.Is64BitProcess
        ? Gc64.CloseDevice(deviceType, deviceIndex)
        : Gc32.CloseDevice(deviceType, deviceIndex);
    internal static uint InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref VCI_INIT_CONFIG config) => Environment.Is64BitProcess
        ? Gc64.InitCAN(deviceType, deviceIndex, canIndex, ref config)
        : Gc32.InitCAN(deviceType, deviceIndex, canIndex, ref config);
    internal static uint StartCAN(uint deviceType, uint deviceIndex, uint canIndex) => Environment.Is64BitProcess
        ? Gc64.StartCAN(deviceType, deviceIndex, canIndex)
        : Gc32.StartCAN(deviceType, deviceIndex, canIndex);
    internal static uint ResetCAN(uint deviceType, uint deviceIndex, uint canIndex) => Environment.Is64BitProcess
        ? Gc64.ResetCAN(deviceType, deviceIndex, canIndex)
        : Gc32.ResetCAN(deviceType, deviceIndex, canIndex);
    internal static uint ClearBuffer(uint deviceType, uint deviceIndex, uint canIndex) => Environment.Is64BitProcess
        ? Gc64.ClearBuffer(deviceType, deviceIndex, canIndex)
        : Gc32.ClearBuffer(deviceType, deviceIndex, canIndex);
    internal static uint Receive(uint deviceType, uint deviceIndex, uint canIndex, VCI_CAN_OBJ[] receive, uint length, int waitTime) => Environment.Is64BitProcess
        ? Gc64.Receive(deviceType, deviceIndex, canIndex, receive, length, waitTime)
        : Gc32.Receive(deviceType, deviceIndex, canIndex, receive, length, waitTime);
    internal static uint Transmit(uint deviceType, uint deviceIndex, uint canIndex, VCI_CAN_OBJ[] send, uint length) => Environment.Is64BitProcess
        ? Gc64.Transmit(deviceType, deviceIndex, canIndex, send, length)
        : Gc32.Transmit(deviceType, deviceIndex, canIndex, send, length);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VCI_INIT_CONFIG
    {
        public uint AccCode;
        public uint AccMask;
        public uint Reserved;
        public byte Filter;
        public byte Timing0;
        public byte Timing1;
        public byte Mode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VCI_CAN_OBJ
    {
        public uint ID;
        public uint TimeStamp;
        public byte TimeFlag;
        public byte SendType;
        public byte RemoteFlag;
        public byte ExternFlag;
        public byte DataLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
    }

    private static class GcKernel
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string lpPathName);
    }

    private static class Gc32
    {
        [DllImport("ECanVci.dll", EntryPoint = "OpenDevice", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint OpenDevice(uint deviceType, uint deviceIndex, uint reserved);
        [DllImport("ECanVci.dll", EntryPoint = "CloseDevice", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint CloseDevice(uint deviceType, uint deviceIndex);
        [DllImport("ECanVci.dll", EntryPoint = "InitCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref VCI_INIT_CONFIG initConfig);
        [DllImport("ECanVci.dll", EntryPoint = "StartCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint StartCAN(uint deviceType, uint deviceIndex, uint canIndex);
        [DllImport("ECanVci.dll", EntryPoint = "ResetCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ResetCAN(uint deviceType, uint deviceIndex, uint canIndex);
        [DllImport("ECanVci.dll", EntryPoint = "ClearBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ClearBuffer(uint deviceType, uint deviceIndex, uint canIndex);
        [DllImport("ECanVci.dll", EntryPoint = "Receive", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Receive(uint deviceType, uint deviceIndex, uint canIndex, [In, Out] VCI_CAN_OBJ[] receive, uint length, int waitTime);
        [DllImport("ECanVci.dll", EntryPoint = "Transmit", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Transmit(uint deviceType, uint deviceIndex, uint canIndex, [In] VCI_CAN_OBJ[] send, uint length);
    }

    private static class Gc64
    {
        [DllImport("ECanVci64.dll", EntryPoint = "OpenDevice", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint OpenDevice(uint deviceType, uint deviceIndex, uint reserved);
        [DllImport("ECanVci64.dll", EntryPoint = "CloseDevice", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint CloseDevice(uint deviceType, uint deviceIndex);
        [DllImport("ECanVci64.dll", EntryPoint = "InitCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref VCI_INIT_CONFIG initConfig);
        [DllImport("ECanVci64.dll", EntryPoint = "StartCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint StartCAN(uint deviceType, uint deviceIndex, uint canIndex);
        [DllImport("ECanVci64.dll", EntryPoint = "ResetCAN", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ResetCAN(uint deviceType, uint deviceIndex, uint canIndex);
        [DllImport("ECanVci64.dll", EntryPoint = "ClearBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ClearBuffer(uint deviceType, uint deviceIndex, uint canIndex);
        [DllImport("ECanVci64.dll", EntryPoint = "Receive", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Receive(uint deviceType, uint deviceIndex, uint canIndex, [In, Out] VCI_CAN_OBJ[] receive, uint length, int waitTime);
        [DllImport("ECanVci64.dll", EntryPoint = "Transmit", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Transmit(uint deviceType, uint deviceIndex, uint canIndex, [In] VCI_CAN_OBJ[] send, uint length);
    }
}

internal static class Pcan
{
    internal const uint ErrorOk = 0x00000;
    internal const uint ErrorQrcvEmpty = 0x00020;
    internal const byte MessageStandard = 0x00;
    internal const byte MessageExtended = 0x02;
    internal static readonly ushort[] UsbChannels = Enumerable.Range(0, 16).Select(i => (ushort)(0x51 + i)).ToArray();

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        internal uint Id;
        internal byte MsgType;
        internal byte Len;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        internal byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Timestamp
    {
        internal uint Millis;
        internal ushort MillisOverflow;
        internal ushort Micros;
    }

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Initialize")]
    internal static extern uint Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt);

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Uninitialize")]
    internal static extern uint Uninitialize(ushort channel);

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Reset")]
    internal static extern uint Reset(ushort channel);

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Write")]
    internal static extern uint Write(ushort channel, ref Msg message);

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Read")]
    internal static extern uint Read(ushort channel, out Msg message, out Timestamp timestamp);
}
