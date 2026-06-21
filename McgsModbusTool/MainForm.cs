using System.Globalization;

namespace McgsModbusTool;

internal sealed class MainForm : Form
{
    private readonly ModbusTcpClient _client = new();
    private readonly System.Windows.Forms.Timer _readTimer = new();
    private readonly System.Windows.Forms.Timer _writeTimer = new();

    private TextBox _hostTextBox = null!;
    private NumericUpDown _portBox = null!;
    private NumericUpDown _unitIdBox = null!;
    private NumericUpDown _timeoutBox = null!;
    private NumericUpDown _addressOffsetBox = null!;
    private NumericUpDown _readAddressBox = null!;
    private NumericUpDown _readQuantityBox = null!;
    private NumericUpDown _readIntervalBox = null!;
    private NumericUpDown _writeIntervalBox = null!;
    private ComboBox _readFunctionBox = null!;
    private DataGridView _receiveGrid = null!;
    private DataGridView _sendGrid = null!;
    private RichTextBox _logBox = null!;
    private Button _connectButton = null!;
    private Button _disconnectButton = null!;
    private Button _readButton = null!;
    private Button _readPollButton = null!;
    private Button _writeButton = null!;
    private Button _autoWriteButton = null!;
    private Button _addSendRowButton = null!;
    private Button _deleteSendRowButton = null!;
    private Label _statusLabel = null!;
    private bool _isReadPolling;
    private bool _isAutoWriting;
    private bool _readInFlight;
    private bool _writeInFlight;

    public MainForm()
    {
        Text = "CAN_TO_NET Modbus TCP 电脑端";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1240, 780);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();

        _readTimer.Tick += async (_, _) => await ReadTimerTickAsync();
        _writeTimer.Tick += async (_, _) => await WriteTimerTickAsync();
        FormClosing += (_, _) => _client.Dispose();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildConnectionPanel(), 0, 0);

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9F)
        };

        var mainLogSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 500,
            SplitterWidth = 8,
            Panel1MinSize = 260,
            Panel2MinSize = 80,
            BorderStyle = BorderStyle.FixedSingle
        };
        mainLogSplit.Panel1.Controls.Add(BuildMainPanel());
        mainLogSplit.Panel2.Controls.Add(_logBox);
        root.Controls.Add(mainLogSplit, 0, 1);
    }

    private Control BuildConnectionPanel()
    {
        var group = new GroupBox
        {
            Text = "网络连接",
            Dock = DockStyle.Fill
        };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 14, 10, 8),
            WrapContents = true
        };
        group.Controls.Add(panel);

        _hostTextBox = new TextBox { Width = 140, Text = "192.168.0.105" };
        _portBox = CreateNumberBox(1, 65535, 503, 76);
        _unitIdBox = CreateNumberBox(0, 255, 255, 66);
        _timeoutBox = CreateNumberBox(20, 10000, 100, 82);
        _addressOffsetBox = CreateNumberBox(0, 10, 1, 66);

        _connectButton = new Button { Text = "连接", Width = 76, Height = 30 };
        _disconnectButton = new Button { Text = "断开", Width = 76, Height = 30, Enabled = false };
        _statusLabel = new Label
        {
            AutoSize = false,
            Width = 390,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "状态：未连接"
        };

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => Disconnect();
        AddLabeled(panel, "CAN_TO_NET IP", _hostTextBox);
        AddLabeled(panel, "端口", _portBox);
        AddLabeled(panel, "站号", _unitIdBox);
        AddLabeled(panel, "超时(ms)", _timeoutBox);
        AddLabeled(panel, "地址偏移", _addressOffsetBox);
        panel.Controls.Add(_connectButton);
        panel.Controls.Add(_disconnectButton);
        panel.Controls.Add(_statusLabel);

        return group;
    }

    private Control BuildMainPanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 535,
            SplitterWidth = 8,
            BorderStyle = BorderStyle.FixedSingle
        };
        split.Panel1.Controls.Add(BuildReceivePanel());
        split.Panel2.Controls.Add(BuildSendPanel());
        return split;
    }

    private Control BuildReceivePanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 92,
            SplitterWidth = 8,
            Panel1MinSize = 62,
            Panel2MinSize = 120,
            BorderStyle = BorderStyle.FixedSingle
        };

        var group = new GroupBox
        {
            Text = "CAN_TO_NET -> 电脑：读取数据",
            Dock = DockStyle.Fill
        };
        split.Panel1.Controls.Add(group);

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 14, 10, 8),
            WrapContents = true
        };
        group.Controls.Add(controls);

        _readFunctionBox = new ComboBox { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
        _readFunctionBox.Items.AddRange(["03 读保持寄存器", "04 读输入寄存器"]);
        _readFunctionBox.SelectedIndex = 0;

        _readAddressBox = CreateNumberBox(0, 65535, 61, 80);
        _readQuantityBox = CreateNumberBox(1, 125, 12, 70);
        _readIntervalBox = CreateNumberBox(20, 60000, 100, 84);

        _readButton = new Button { Text = "读一次", Width = 82, Height = 30 };
        _readPollButton = new Button { Text = "自动读", Width = 86, Height = 30 };
        _readButton.Click += async (_, _) => await ReadOnceAsync(showSuccessLog: true, showMessage: true, setBusy: true);
        _readPollButton.Click += (_, _) => ToggleReadPolling();

        AddLabeled(controls, "功能", _readFunctionBox);
        AddLabeled(controls, "CAN_TO_NET起始地址", _readAddressBox);
        AddLabeled(controls, "数量", _readQuantityBox);
        AddLabeled(controls, "周期(ms)", _readIntervalBox);
        controls.Controls.Add(_readButton);
        controls.Controls.Add(_readPollButton);

        _receiveGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _receiveGrid.Columns.Add("ScreenAddress", "CAN_TO_NET地址");
        _receiveGrid.Columns.Add("Unsigned", "无符号值");
        _receiveGrid.Columns.Add("Signed", "有符号值");
        _receiveGrid.Columns.Add("Hex", "十六进制");
        split.Panel2.Controls.Add(_receiveGrid);

        return split;
    }

    private Control BuildSendPanel()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var group = new GroupBox
        {
            Text = "电脑 -> CAN_TO_NET：发送编辑窗口",
            Dock = DockStyle.Fill
        };
        table.Controls.Add(group, 0, 0);

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 14, 10, 8),
            WrapContents = true
        };
        group.Controls.Add(controls);

        _writeIntervalBox = CreateNumberBox(20, 60000, 100, 84);
        _writeButton = new Button { Text = "发送一次", Width = 86, Height = 30 };
        _autoWriteButton = new Button { Text = "自动发送", Width = 90, Height = 30 };
        _addSendRowButton = new Button { Text = "加一行", Width = 76, Height = 30 };
        _deleteSendRowButton = new Button { Text = "删选中", Width = 76, Height = 30 };

        _writeButton.Click += async (_, _) => await WriteEnabledRowsAsync(showMessage: true, setBusy: true);
        _autoWriteButton.Click += (_, _) => ToggleAutoWriting();
        _addSendRowButton.Click += (_, _) => AddSendRow();
        _deleteSendRowButton.Click += (_, _) => DeleteSelectedSendRows();

        AddLabeled(controls, "周期(ms)", _writeIntervalBox);
        controls.Controls.Add(_writeButton);
        controls.Controls.Add(_autoWriteButton);
        controls.Controls.Add(_addSendRowButton);
        controls.Controls.Add(_deleteSendRowButton);

        _sendGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            EditMode = DataGridViewEditMode.EditOnEnter
        };
        _sendGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "启用",
            Width = 48,
            FillWeight = 42
        });
        _sendGrid.Columns.Add("Name", "名称");
        _sendGrid.Columns.Add("Address", "CAN_TO_NET地址");
        _sendGrid.Columns.Add("Value", "发送值");
        _sendGrid.Columns.Add("Hex", "十六进制");
        _sendGrid.Columns.Add("Status", "最近状态");
        _sendGrid.Columns["Status"]!.ReadOnly = true;
        _sendGrid.Columns["Hex"]!.ReadOnly = true;
        _sendGrid.CellEndEdit += (_, args) => UpdateSendRowHex(args.RowIndex);
        _sendGrid.UserAddedRow += (_, _) => PrepareLastEditableRow();
        table.Controls.Add(_sendGrid, 0, 1);

        AddSendRow("发送1", 61, 12);
        AddSendRow("发送2", 62, 13);
        AddSendRow("发送3", 63, 14);

        return table;
    }

    private async Task ConnectAsync()
    {
        await ExecuteOperationAsync("连接", async token =>
        {
            await _client.ConnectAsync(_hostTextBox.Text.Trim(), (int)_portBox.Value, (int)_timeoutBox.Value, token);
            SetConnectedState(true);
            Log($"已连接 {_hostTextBox.Text.Trim()}:{_portBox.Value}，站号 {(byte)_unitIdBox.Value}");
        }, showMessage: true, setBusy: true);
    }

    private void Disconnect()
    {
        StopReadPolling();
        StopAutoWriting();
        _client.Disconnect();
        SetConnectedState(false);
        Log("已断开连接");
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_client.IsConnected)
        {
            return;
        }

        await _client.ConnectAsync(_hostTextBox.Text.Trim(), (int)_portBox.Value, (int)_timeoutBox.Value, token);
        SetConnectedState(true);
        Log("已自动重连");
    }

    private async Task ReadOnceAsync(bool showSuccessLog, bool showMessage, bool setBusy)
    {
        if (_readInFlight)
        {
            return;
        }

        _readInFlight = true;
        try
        {
            await ExecuteOperationAsync("读取", async token =>
            {
                await EnsureConnectedAsync(token);

                var unitId = (byte)_unitIdBox.Value;
                var screenAddress = (int)_readAddressBox.Value;
                var address = ScreenToProtocolAddress(screenAddress);
                var quantity = (ushort)_readQuantityBox.Value;
                var timeout = (int)_timeoutBox.Value;
                ushort[] values;

                if (_readFunctionBox.SelectedIndex == 0)
                {
                    values = await _client.ReadHoldingRegistersAsync(unitId, address, quantity, timeout, token);
                }
                else
                {
                    values = await _client.ReadInputRegistersAsync(unitId, address, quantity, timeout, token);
                }

                ShowReceivedRegisters(address, values);
                if (showSuccessLog)
                {
                    Log($"读取成功：CAN_TO_NET地址 {screenAddress}，数量 {quantity}");
                }
            }, showMessage, setBusy);
        }
        finally
        {
            _readInFlight = false;
        }
    }

    private async Task WriteEnabledRowsAsync(bool showMessage, bool setBusy)
    {
        if (_writeInFlight)
        {
            return;
        }

        _writeInFlight = true;
        try
        {
            await ExecuteOperationAsync("写入", async token =>
            {
                await EnsureConnectedAsync(token);

                var items = CollectWriteItems();
                if (items.Count == 0)
                {
                    throw new InvalidOperationException("发送编辑窗口里没有启用的数据行。");
                }

                var unitId = (byte)_unitIdBox.Value;
                var timeout = (int)_timeoutBox.Value;
                var segments = BuildContiguousSegments(items);

                foreach (var segment in segments)
                {
                    if (segment.Values.Count == 1)
                    {
                        await _client.WriteSingleRegisterAsync(unitId, segment.StartAddress, segment.Values[0], timeout, token);
                    }
                    else
                    {
                        await _client.WriteMultipleRegistersAsync(unitId, segment.StartAddress, segment.Values, timeout, token);
                    }

                    MarkRows(segment.RowIndexes, $"OK {DateTime.Now:HH:mm:ss.fff}");
                }

                Log($"发送成功：{items.Count} 个寄存器，{segments.Count} 包");
            }, showMessage, setBusy);
        }
        catch
        {
            MarkRows(CollectWriteItems().Select(item => item.RowIndex), $"失败 {DateTime.Now:HH:mm:ss.fff}");
            throw;
        }
        finally
        {
            _writeInFlight = false;
        }
    }

    private void ToggleReadPolling()
    {
        if (_isReadPolling)
        {
            StopReadPolling();
            return;
        }

        _readTimer.Interval = (int)_readIntervalBox.Value;
        _isReadPolling = true;
        _readPollButton.Text = "停止读";
        Log($"开始自动读：{_readTimer.Interval}ms");
        _readTimer.Start();
    }

    private void ToggleAutoWriting()
    {
        if (_isAutoWriting)
        {
            StopAutoWriting();
            return;
        }

        _writeTimer.Interval = (int)_writeIntervalBox.Value;
        _isAutoWriting = true;
        _autoWriteButton.Text = "停止发送";
        Log($"开始自动发送：{_writeTimer.Interval}ms");
        _writeTimer.Start();
    }

    private void StopReadPolling()
    {
        _readTimer.Stop();
        _isReadPolling = false;
        if (_readPollButton is not null)
        {
            _readPollButton.Text = "自动读";
        }
    }

    private void StopAutoWriting()
    {
        _writeTimer.Stop();
        _isAutoWriting = false;
        if (_autoWriteButton is not null)
        {
            _autoWriteButton.Text = "自动发送";
        }
    }

    private async Task ReadTimerTickAsync()
    {
        _readTimer.Stop();
        try
        {
            await ReadOnceAsync(showSuccessLog: false, showMessage: false, setBusy: false);
        }
        finally
        {
            if (_isReadPolling)
            {
                _readTimer.Interval = (int)_readIntervalBox.Value;
                _readTimer.Start();
            }
        }
    }

    private async Task WriteTimerTickAsync()
    {
        _writeTimer.Stop();
        try
        {
            await WriteEnabledRowsAsync(showMessage: false, setBusy: false);
        }
        finally
        {
            if (_isAutoWriting)
            {
                _writeTimer.Interval = (int)_writeIntervalBox.Value;
                _writeTimer.Start();
            }
        }
    }

    private void ShowReceivedRegisters(ushort startAddress, IReadOnlyList<ushort> values)
    {
        int? selectedScreenAddress = null;
        var selectedColumnIndex = _receiveGrid.CurrentCell?.ColumnIndex ?? 0;
        if (_receiveGrid.CurrentRow is not null && TryParseNumber(_receiveGrid.CurrentRow.Cells["ScreenAddress"].Value, out var selectedAddress))
        {
            selectedScreenAddress = selectedAddress;
        }

        while (_receiveGrid.Rows.Count < values.Count)
        {
            _receiveGrid.Rows.Add();
        }

        while (_receiveGrid.Rows.Count > values.Count)
        {
            _receiveGrid.Rows.RemoveAt(_receiveGrid.Rows.Count - 1);
        }

        for (var i = 0; i < values.Count; i++)
        {
            var address = startAddress + i;
            var screenAddress = ProtocolToScreenAddress(address);
            var value = values[i];
            var row = _receiveGrid.Rows[i];
            row.Cells["ScreenAddress"].Value = screenAddress.ToString(CultureInfo.InvariantCulture);
            row.Cells["Unsigned"].Value = value.ToString(CultureInfo.InvariantCulture);
            row.Cells["Signed"].Value = unchecked((short)value).ToString(CultureInfo.InvariantCulture);
            row.Cells["Hex"].Value = $"0x{value:X4}";
        }

        RestoreReceiveSelection(selectedScreenAddress, selectedColumnIndex);
    }

    private void RestoreReceiveSelection(int? selectedScreenAddress, int selectedColumnIndex)
    {
        if (selectedScreenAddress is null)
        {
            _receiveGrid.ClearSelection();
            return;
        }

        foreach (DataGridViewRow row in _receiveGrid.Rows)
        {
            if (TryParseNumber(row.Cells["ScreenAddress"].Value, out var screenAddress) && screenAddress == selectedScreenAddress)
            {
                var columnIndex = Math.Clamp(selectedColumnIndex, 0, _receiveGrid.Columns.Count - 1);
                _receiveGrid.CurrentCell = row.Cells[columnIndex];
                row.Selected = true;
                return;
            }
        }
    }

    private void AddSendRow()
    {
        var nextAddress = ProtocolToScreenAddress(60);
        foreach (DataGridViewRow row in _sendGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            if (TryParseNumber(row.Cells["Address"].Value, out var address))
            {
                nextAddress = Math.Max(nextAddress, address + 1);
            }
        }

        AddSendRow($"发送{_sendGrid.Rows.Count}", nextAddress, 0);
    }

    private void AddSendRow(string name, int address, int value)
    {
        var index = _sendGrid.Rows.Add(true, name, address, value, ToRegisterHex(ToRegisterValue(value)), string.Empty);
        _sendGrid.Rows[index].Cells["Enabled"].Value = true;
    }

    private void DeleteSelectedSendRows()
    {
        foreach (DataGridViewRow row in _sendGrid.SelectedRows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToList())
        {
            _sendGrid.Rows.Remove(row);
        }
    }

    private void PrepareLastEditableRow()
    {
        foreach (DataGridViewRow row in _sendGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            row.Cells["Enabled"].Value ??= true;
        }
    }

    private void UpdateSendRowHex(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _sendGrid.Rows.Count)
        {
            return;
        }

        var row = _sendGrid.Rows[rowIndex];
        if (row.IsNewRow)
        {
            return;
        }

        if (TryParseNumber(row.Cells["Value"].Value, out var value))
        {
            row.Cells["Hex"].Value = ToRegisterHex(ToRegisterValue(value));
        }

    }

    private List<WriteItem> CollectWriteItems()
    {
        var result = new List<WriteItem>();
        foreach (DataGridViewRow row in _sendGrid.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }

            var enabled = row.Cells["Enabled"].Value is true || row.Cells["Enabled"].Value?.ToString() == "True";
            if (!enabled)
            {
                continue;
            }

            var screenAddress = ParseNumber(row.Cells["Address"].Value, "CAN_TO_NET地址");
            var address = ScreenToProtocolAddress(screenAddress);
            var value = ParseNumber(row.Cells["Value"].Value, "发送值");

            var registerValue = ToRegisterValue(value);
            row.Cells["Hex"].Value = ToRegisterHex(registerValue);
            result.Add(new WriteItem(row.Index, address, registerValue));
        }

        var duplicate = result.GroupBy(item => item.Address).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"发送表里地址重复：{duplicate.Key}");
        }

        return result.OrderBy(item => item.Address).ToList();
    }

    private static List<WriteSegment> BuildContiguousSegments(IReadOnlyList<WriteItem> items)
    {
        var segments = new List<WriteSegment>();
        WriteSegment? current = null;

        foreach (var item in items)
        {
            if (current is null || item.Address != current.StartAddress + current.Values.Count)
            {
                current = new WriteSegment(item.Address);
                segments.Add(current);
            }

            current.Values.Add(item.Value);
            current.RowIndexes.Add(item.RowIndex);
        }

        return segments;
    }

    private void MarkRows(IEnumerable<int> rowIndexes, string status)
    {
        foreach (var rowIndex in rowIndexes)
        {
            if (rowIndex >= 0 && rowIndex < _sendGrid.Rows.Count)
            {
                _sendGrid.Rows[rowIndex].Cells["Status"].Value = status;
            }
        }
    }

    private async Task ExecuteOperationAsync(string operationName, Func<CancellationToken, Task> operation, bool showMessage, bool setBusy)
    {
        if (setBusy)
        {
            SetBusy(true);
        }

        using var cts = new CancellationTokenSource((int)_timeoutBox.Value + 20);

        try
        {
            await operation(cts.Token);
            _statusLabel.Text = _client.IsConnected ? "状态：已连接" : "状态：未连接";
        }
        catch (OperationCanceledException)
        {
            SetConnectedState(false);
            Log($"{operationName}超时");
            if (showMessage)
            {
                MessageBox.Show($"{operationName}超时，请检查网线/IP/端口/CAN_TO_NET服务。", "Modbus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            SetConnectedState(false);
            Log($"{operationName}失败：{ex.Message}");
            if (showMessage)
            {
                MessageBox.Show(ex.Message, $"{operationName}失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (setBusy)
            {
                SetBusy(false);
            }
        }
    }

    private void SetConnectedState(bool connected)
    {
        _connectButton.Enabled = !connected;
        _disconnectButton.Enabled = connected;
        _statusLabel.Text = connected ? "状态：已连接" : "状态：未连接";
    }

    private void SetBusy(bool busy)
    {
        _connectButton.Enabled = !busy && !_client.IsConnected;
        _disconnectButton.Enabled = !busy && _client.IsConnected;
        _readButton.Enabled = !busy;
        _writeButton.Enabled = !busy;
        _readPollButton.Enabled = !busy || _isReadPolling;
        _autoWriteButton.Enabled = !busy || _isAutoWriting;
        _addSendRowButton.Enabled = !busy;
        _deleteSendRowButton.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Log(string message)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        _logBox.ScrollToCaret();
    }

    private static NumericUpDown CreateNumberBox(decimal minimum, decimal maximum, decimal value, int width)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = width
        };
    }

    private static void AddLabeled(FlowLayoutPanel parent, string labelText, Control control)
    {
        var wrapper = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 12, 8)
        };
        wrapper.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = false,
            Width = Math.Max(52, labelText.Length * 16),
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft
        });
        control.Height = 28;
        wrapper.Controls.Add(control);
        parent.Controls.Add(wrapper);
    }

    private static int ParseNumber(object? value, string fieldName)
    {
        if (!TryParseNumber(value, out var number))
        {
            throw new FormatException($"{fieldName}格式不正确：{value}");
        }

        return number;
    }

    private static bool TryParseNumber(object? value, out int number)
    {
        number = 0;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number);
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private ushort ScreenToProtocolAddress(int screenAddress)
    {
        var protocolAddress = screenAddress - (int)_addressOffsetBox.Value;
        if (protocolAddress is < 0 or > 65535)
        {
            throw new FormatException($"CAN_TO_NET地址 {screenAddress} 超出范围。当前地址偏移：{_addressOffsetBox.Value}");
        }

        return (ushort)protocolAddress;
    }

    private bool TryScreenToProtocolAddress(int screenAddress, out ushort protocolAddress)
    {
        protocolAddress = 0;
        var converted = screenAddress - (int)_addressOffsetBox.Value;
        if (converted is < 0 or > 65535)
        {
            return false;
        }

        protocolAddress = (ushort)converted;
        return true;
    }

    private int ProtocolToScreenAddress(int protocolAddress)
    {
        return protocolAddress + (int)_addressOffsetBox.Value;
    }

    private static ushort ToRegisterValue(int value)
    {
        if (value is < short.MinValue or > ushort.MaxValue)
        {
            throw new FormatException($"发送值超出 16 位寄存器范围：{value}");
        }

        return value < 0 ? unchecked((ushort)(short)value) : (ushort)value;
    }

    private static string ToRegisterHex(ushort value)
    {
        return $"0x{value:X4}";
    }

    private sealed record WriteItem(int RowIndex, ushort Address, ushort Value);

    private sealed class WriteSegment(ushort startAddress)
    {
        public ushort StartAddress { get; } = startAddress;
        public List<ushort> Values { get; } = [];
        public List<int> RowIndexes { get; } = [];
    }
}
