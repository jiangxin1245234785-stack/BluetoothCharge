using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BluetoothCharge;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly Label _deviceName = new();
    private readonly Label _deviceRuleInfo = new();
    private readonly NumericUpDown _defaultThreshold = Number(1, 100);
    private readonly NumericUpDown _defaultCooldown = Number(1, 10080);
    private readonly NumericUpDown _scanInterval = Number(30, 86400);
    private readonly NumericUpDown _deviceThreshold = Number(1, 100);
    private readonly NumericUpDown _deviceCooldown = Number(1, 10080);
    private readonly CheckBox _enableBle = new() { Text = "读取已连接设备的 BLE 标准电量服务", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "登录 Windows 后启动", AutoSize = true };
    private readonly CheckBox _ignore = new() { Text = "忽略此设备的充电提醒", AutoSize = true };
    private readonly Button _saveDevice = Button("保存此设备", "SaveDeviceButton");
    private readonly Button _restoreDevice = Button("恢复默认", "RestoreDeviceButton");
    private readonly Button _scan = Button("重新扫描设备", "RefreshButton");
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _layout = new();
    private readonly TableLayoutPanel _body = new();
    private readonly GroupBox _deviceGroup = new();
    private readonly TableLayoutPanel _devicePanel = new();
    private readonly FlowLayoutPanel _deviceActions = new();
    private readonly GroupBox _defaultsGroup = new();
    private readonly TableLayoutPanel _defaultsPanel = new();
    private readonly FlowLayoutPanel _defaultFields = new();
    private readonly FlowLayoutPanel _startupOptions = new();
    private readonly FlowLayoutPanel _actions = new();
    private readonly Label _note = new();
    private readonly Label _startupNote = new();
    private readonly Dictionary<string, DeviceReading> _readings = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedId;
    private bool _updatingTable;
    private bool _layoutReady;
    private bool _reflowing;
    private bool _selectedMonitoring;

    public event Action? ScanRequested;
    public event Action? TestNotificationRequested;
    public event Action? ExportRequested;
    public event Action? ConfigurationChanged;

    public SettingsForm(AppConfig config)
    {
        _config = config;
        Name = "SettingsForm";
        Text = "BluetoothCharge · 蓝牙充电提醒";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 560);
        Size = new Size(1340, 800);
        BuildLayout();
        LoadDefaults();
        ReadStartupState();
        _grid.SelectionChanged += (_, _) => { if (!_updatingTable) LoadSelectedDevice(); };
        _saveDevice.Click += (_, _) => SaveSelectedDevice();
        _restoreDevice.Click += (_, _) => RestoreSelectedDevice();
        _scan.Click += (_, _) => RaiseSafely(ScanRequested, "刷新失败");
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is string id && _readings.TryGetValue(id, out var reading))
                MessageBox.Show(this, Convert.ToString(reading.Detail) ?? "没有更多详情。", reading.Name + " · 电量详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        _viewport.ClientSizeChanged += (_, _) => ReflowLayout();
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            // Screen dimensions are physical pixels. Keep the first window usable on
            // a high-DPI screen without changing the user's chosen font size.
            MinimumSize = new Size(Math.Min(800, area.Width), Math.Min(560, area.Height));
            if (Width > area.Width || Height > area.Height)
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            if (Left < area.Left || Right > area.Right || Top < area.Top || Bottom > area.Bottom)
                Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
            ReflowLayout();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        LoadSelectedDevice();
        _layoutReady = true;
        ReflowLayout();
    }

    public void SetReadings(IReadOnlyList<DeviceReading> readings, string status)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetReadings(readings, status)));
            return;
        }
        string? previousId = _selectedId;
        _updatingTable = true;
        try
        {
            _readings.Clear();
            _grid.Rows.Clear();
            foreach (var reading in readings)
            {
                _readings[reading.Id] = reading;
                string battery = !reading.RequiresBatteryMonitoring ? "无需充电" : reading.Percent is int value ? $"{value}%{(reading.Connected ? "" : "（缓存）")}" : "未知";
                string time = reading.Percent is null ? "—" : FormatObservedAt(reading.ObservedAt);
                int index = _grid.Rows.Add(reading.Name, reading.Transport,
                    reading.ConnectionDescription ?? (!reading.ConnectionKnown ? "连接状态未知" : reading.Connected ? "已连接" : "离线 / 未连接"), battery,
                    Convert.ToString(reading.Source) ?? "—", time,
                    FormatRule(reading.Id), Convert.ToString(reading.Detail) ?? "");
                _grid.Rows[index].Tag = reading.Id;
                foreach (DataGridViewCell cell in _grid.Rows[index].Cells)
                    cell.ToolTipText = Convert.ToString(cell.Value) ?? "";
            }
            _grid.ClearSelection();
            var selected = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => string.Equals(r.Tag as string, previousId, StringComparison.OrdinalIgnoreCase))
                ?? _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
            if (selected is not null)
            {
                selected.Selected = true;
                _grid.CurrentCell = selected.Cells[0];
            }
        }
        finally { _updatingTable = false; }

        string? nextId = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as string : null;
        if (!string.Equals(previousId, nextId, StringComparison.OrdinalIgnoreCase)) LoadSelectedDevice();
        else if (nextId is not null && _readings.TryGetValue(nextId, out var updated))
        {
            if (updated.RequiresBatteryMonitoring != _selectedMonitoring) LoadSelectedDevice();
            else _deviceName.Text = updated.Name;
        }
        _status.Text = status;
        ReflowLayout();
    }

    public void SetBusy(bool busy)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetBusy(busy)));
            return;
        }
        _scan.Enabled = !busy;
        _scan.Text = busy ? "正在扫描…" : "重新扫描设备";
        ReflowLayout();
    }

    private void BuildLayout()
    {
        Controls.Add(_viewport);
        _layout.ColumnCount = 1;
        _layout.RowCount = 5;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 1; i < 5; i++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _viewport.Controls.Add(_layout);

        _body.Dock = DockStyle.Fill;
        _body.ColumnCount = 2;
        _body.RowCount = 1;
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.Controls.Add(_body, 0, 0);

        _grid.Name = "DeviceGrid";
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ScrollBars = ScrollBars.Both;
        _grid.ShowCellToolTips = true;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        AddColumn("DeviceName", "设备", "Keychron K3 Version 3");
        AddColumn("Transport", "连接方式", "蓝牙 / USB");
        AddColumn("Connection", "状态", "离线 / 未连接");
        AddColumn("Battery", "电量", "100%（缓存）");
        AddColumn("BatterySource", "来源", "Windows 电量属性");
        AddColumn("ObservedAt", "读取时间", "12-31 23:59:59");
        AddColumn("DeviceSettings", "提醒设置", "<100% / 10080 分钟");
        AddColumn("Details", "详情（双击查看）", "双击设备查看完整详情");
        _body.Controls.Add(_grid, 0, 0);

        _deviceGroup.Text = "选中设备";
        _deviceGroup.Dock = DockStyle.Fill;
        var deviceScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _deviceGroup.Controls.Add(deviceScroll);
        ConfigureAutoTable(_devicePanel, 8);
        deviceScroll.Controls.Add(_devicePanel);
        _deviceName.AutoSize = true;
        _deviceName.Font = new Font(Font, FontStyle.Bold);
        _deviceName.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _deviceRuleInfo.AutoSize = true;
        _deviceRuleInfo.ForeColor = SystemColors.GrayText;
        _devicePanel.Controls.Add(_deviceName, 0, 0);
        _devicePanel.Controls.Add(_deviceRuleInfo, 0, 1);
        _devicePanel.Controls.Add(Label("提醒阈值（%）"), 0, 2);
        _deviceThreshold.Dock = DockStyle.Top;
        _devicePanel.Controls.Add(_deviceThreshold, 0, 3);
        _devicePanel.Controls.Add(Label("冷却时间（分钟）"), 0, 4);
        _deviceCooldown.Dock = DockStyle.Top;
        _devicePanel.Controls.Add(_deviceCooldown, 0, 5);
        _devicePanel.Controls.Add(_ignore, 0, 6);
        ConfigureFlow(_deviceActions);
        _deviceActions.Controls.Add(_saveDevice);
        _deviceActions.Controls.Add(_restoreDevice);
        _devicePanel.Controls.Add(_deviceActions, 0, 7);
        _body.Controls.Add(_deviceGroup, 1, 0);

        _note.Text = "重新扫描会重新枚举设备。系统电量可能是驱动缓存；离线不提醒，未知不等于 0%。双击设备可查看完整详情。";
        _note.AutoSize = true;
        _note.ForeColor = SystemColors.GrayText;
        _layout.Controls.Add(_note, 0, 1);

        _defaultsGroup.Text = "默认设置";
        _defaultsGroup.Dock = DockStyle.Fill;
        ConfigureAutoTable(_defaultsPanel, 3);
        _defaultsGroup.Controls.Add(_defaultsPanel);
        ConfigureFlow(_defaultFields);
        _defaultFields.Controls.Add(Field("阈值（%）", _defaultThreshold));
        _defaultFields.Controls.Add(Field("冷却（分钟）", _defaultCooldown));
        _defaultFields.Controls.Add(Field("扫描间隔（秒）", _scanInterval));
        var saveDefaults = Button("保存默认设置", "SaveDefaultsButton");
        saveDefaults.Click += (_, _) => SaveDefaults();
        _defaultFields.Controls.Add(saveDefaults);
        _defaultsPanel.Controls.Add(_defaultFields, 0, 0);
        _defaultsPanel.Controls.Add(_enableBle, 0, 1);
        ConfigureFlow(_startupOptions);
        _startupOptions.Controls.Add(_startup);
        var applyStartup = Button("应用自启设置", "ApplyStartupButton");
        applyStartup.Click += (_, _) => ApplyStartup();
        _startupOptions.Controls.Add(applyStartup);
        _startupNote.Text = "点击按钮后生效。";
        _startupNote.AutoSize = true;
        _startupNote.ForeColor = SystemColors.GrayText;
        _startupOptions.Controls.Add(_startupNote);
        _defaultsPanel.Controls.Add(_startupOptions, 0, 2);
        _layout.Controls.Add(_defaultsGroup, 0, 2);

        ConfigureFlow(_actions);
        _actions.Controls.Add(_scan);
        var test = Button("测试通知", "TestNotificationButton");
        test.Click += (_, _) => RaiseSafely(TestNotificationRequested, "测试通知失败");
        _actions.Controls.Add(test);
        var export = Button("导出诊断", "ExportDiagnosticsButton");
        export.Click += (_, _) => RaiseSafely(ExportRequested, "导出诊断失败");
        _actions.Controls.Add(export);
        var data = Button("打开数据文件夹", "OpenDataButton");
        data.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppStorage.DataDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("无法打开数据文件夹", ex); }
        };
        _actions.Controls.Add(data);
        _layout.Controls.Add(_actions, 0, 3);
        _status.AutoSize = true;
        _status.Text = "等待首次扫描。关闭此窗口后程序仍在托盘运行。";
        _layout.Controls.Add(_status, 0, 4);
        _status.TextChanged += (_, _) => ReflowLayout();
        _deviceName.TextChanged += (_, _) => ReflowLayout();
        _deviceRuleInfo.TextChanged += (_, _) => ReflowLayout();
    }

    private static void ConfigureAutoTable(TableLayoutPanel table, int rows)
    {
        table.AutoSize = true;
        table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        table.Dock = DockStyle.Top;
        table.ColumnCount = 1;
        table.RowCount = rows;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    private static void ConfigureFlow(FlowLayoutPanel flow)
    {
        flow.AutoSize = true;
        flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        flow.Dock = DockStyle.Top;
        flow.FlowDirection = FlowDirection.LeftToRight;
        flow.WrapContents = true;
    }

    private static TableLayoutPanel Field(string text, NumericUpDown number)
    {
        var pair = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1 };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pair.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = Label(text);
        label.Anchor = AnchorStyles.Left;
        number.Dock = DockStyle.None;
        number.Anchor = AnchorStyles.Left;
        pair.Controls.Add(label, 0, 0);
        pair.Controls.Add(number, 1, 0);
        return pair;
    }

    private void AddColumn(string name, string title, string sample)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, Tag = sample, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_layoutReady) _deviceName.Font = new Font(Font, FontStyle.Bold);
        ReflowLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ReflowLayout();
        if (IsHandleCreated) BeginInvoke(new Action(ReflowLayout));
    }

    private int Logical(int value) => (int)Math.Ceiling(value * DeviceDpi / 96d);

    private void ReflowLayout()
    {
        if (!_layoutReady || _reflowing || IsDisposed || _viewport.ClientSize.Width <= 0) return;
        _reflowing = true;
        try
        {
            int gap = Logical(8);
            _layout.Padding = new Padding(Logical(12));
            _layout.Margin = System.Windows.Forms.Padding.Empty;
            _body.Margin = System.Windows.Forms.Padding.Empty;
            _grid.Margin = System.Windows.Forms.Padding.Empty;
            _deviceGroup.Padding = new Padding(gap);
            _deviceGroup.Margin = new Padding(gap, 0, 0, 0);
            _defaultsGroup.Padding = new Padding(gap);
            _defaultsGroup.Margin = new Padding(0, gap, 0, gap);
            _devicePanel.Margin = System.Windows.Forms.Padding.Empty;
            _defaultsPanel.Margin = System.Windows.Forms.Padding.Empty;
            foreach (Control control in AllControls(this))
            {
                if (control is Button button)
                {
                    button.Padding = new Padding(Logical(8), Logical(5), Logical(8), Logical(5));
                    button.Margin = new Padding(Logical(3), Logical(3), gap, Logical(3));
                    button.MinimumSize = System.Drawing.Size.Empty;
                }
                else if (control is NumericUpDown number)
                {
                    number.Margin = new Padding(Logical(3), Logical(3), gap, Logical(3));
                    number.Width = TextRenderer.MeasureText("86,400", number.Font).Width + Logical(35);
                    number.Height = number.PreferredHeight;
                }
                else if (control is Label || control is CheckBox)
                    control.Margin = new Padding(Logical(3), Logical(4), gap, Logical(4));
            }
            _ignore.MaximumSize = System.Drawing.Size.Empty;
            int buttonRowWidth = _saveDevice.GetPreferredSize(System.Drawing.Size.Empty).Width
                + _restoreDevice.GetPreferredSize(System.Drawing.Size.Empty).Width + Logical(8) + gap;
            int requiredDeviceTextWidth = Math.Max(_ignore.GetPreferredSize(System.Drawing.Size.Empty).Width, buttonRowWidth);
            int deviceWidth = Math.Max(Logical(220), requiredDeviceTextWidth
                + _deviceGroup.Margin.Horizontal + _deviceGroup.Padding.Horizontal + SystemInformation.VerticalScrollBarWidth + gap * 2);
            _body.ColumnStyles[1].Width = deviceWidth;
            int deviceTextWidth = Math.Max(1, deviceWidth - _deviceGroup.Margin.Horizontal - _deviceGroup.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - gap * 2);
            _deviceName.MaximumSize = new Size(deviceTextWidth, 0);
            _deviceRuleInfo.MaximumSize = new Size(deviceTextWidth, 0);
            _ignore.MaximumSize = new Size(deviceTextWidth, 0);
            foreach (Control control in _devicePanel.Controls)
                control.Margin = new Padding(Logical(2), Logical(2), Logical(2), Logical(2));
            _deviceActions.Margin = System.Windows.Forms.Padding.Empty;
            _saveDevice.Margin = new Padding(Logical(2));
            _restoreDevice.Margin = new Padding(Logical(2));
            _deviceActions.MaximumSize = new Size(deviceTextWidth, 0);
            _deviceActions.MinimumSize = new Size(0, FlowHeight(_deviceActions, deviceTextWidth));
            _note.Margin = new Padding(Logical(3), Logical(2), gap, Logical(2));
            _status.Margin = new Padding(Logical(3), Logical(2), gap, Logical(2));

            ResizeGridColumns();
            for (int pass = 0; pass < 2; pass++)
            {
                int width = Math.Max(Logical(630), _viewport.ClientSize.Width - (_viewport.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
                _enableBle.MaximumSize = System.Drawing.Size.Empty;
                width = Math.Max(width, _enableBle.GetPreferredSize(System.Drawing.Size.Empty).Width + _layout.Padding.Horizontal + _defaultsGroup.Padding.Horizontal + gap * 2);
                int innerWidth = Math.Max(1, width - _layout.Padding.Horizontal);
                int defaultsWidth = Math.Max(1, innerWidth - _defaultsGroup.Padding.Horizontal - Logical(4));
                _defaultFields.MaximumSize = new Size(defaultsWidth, 0);
                _startupOptions.MaximumSize = new Size(defaultsWidth, 0);
                _actions.MaximumSize = new Size(innerWidth, 0);
                _defaultFields.MinimumSize = new Size(0, FlowHeight(_defaultFields, defaultsWidth));
                _startupOptions.MinimumSize = new Size(0, FlowHeight(_startupOptions, defaultsWidth));
                _actions.MinimumSize = new Size(0, FlowHeight(_actions, innerWidth));
                // Explanations must fit one visible page even when the device/field
                // area needs horizontal scrolling at a small size or a large font.
                int visibleWidth = Math.Min(innerWidth, _viewport.ClientSize.Width
                    - (_viewport.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - _layout.Padding.Horizontal);
                _note.MaximumSize = new Size(Math.Max(1, visibleWidth - _note.Margin.Horizontal), 0);
                _status.MaximumSize = new Size(Math.Max(1, visibleWidth - _status.Margin.Horizontal), 0);
                int defaultsHeight = _defaultFields.MinimumSize.Height + _startupOptions.MinimumSize.Height
                    + _enableBle.GetPreferredSize(new Size(defaultsWidth, 0)).Height + _enableBle.Margin.Vertical
                    + _defaultsGroup.Padding.Vertical + _defaultsGroup.Font.Height + Logical(4);
                _defaultsGroup.MinimumSize = new Size(0, defaultsHeight);
                int bodyHeight = Math.Max(Logical(180), _grid.ColumnHeadersHeight + _grid.RowTemplate.Height * 3 + Logical(16));
                int deviceHeight = _devicePanel.GetPreferredSize(new Size(deviceTextWidth, 0)).Height
                    + _deviceGroup.Padding.Vertical + _deviceGroup.Font.Height + Logical(8);
                bodyHeight = Math.Max(bodyHeight, deviceHeight);
                _body.MinimumSize = new Size(0, bodyHeight);
                int minimumHeight = _layout.Padding.Vertical + bodyHeight
                    + _note.GetPreferredSize(new Size(_note.MaximumSize.Width, 0)).Height + _note.Margin.Vertical
                    + defaultsHeight + _defaultsGroup.Margin.Vertical
                    + _actions.MinimumSize.Height + _actions.Margin.Vertical
                    + _status.GetPreferredSize(new Size(_status.MaximumSize.Width, 0)).Height + _status.Margin.Vertical;
                _layout.SetBounds(_layout.Left, _layout.Top, width, Math.Max(_viewport.ClientSize.Height, minimumHeight), BoundsSpecified.Size);
                _layout.PerformLayout();
                _viewport.PerformLayout();
            }
        }
        finally { _reflowing = false; }
    }

    private void ResizeGridColumns()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            int width = TextRenderer.MeasureText(column.HeaderText, _grid.Font).Width;
            width = Math.Max(width, TextRenderer.MeasureText(Convert.ToString(column.Tag) ?? "", _grid.Font).Width);
            foreach (DataGridViewRow row in _grid.Rows)
                width = Math.Max(width, TextRenderer.MeasureText(Convert.ToString(row.Cells[column.Name].Value) ?? "", _grid.Font).Width);
            int cap = column.Name is "DeviceName" or "Details" ? Logical(360) : Logical(280);
            column.MinimumWidth = TextRenderer.MeasureText(column.HeaderText, _grid.Font).Width + Logical(16);
            column.Width = Math.Max(column.MinimumWidth, Math.Min(cap, width + Logical(18)));
        }
        _grid.RowTemplate.Height = _grid.Font.Height + Logical(10);
        foreach (DataGridViewRow row in _grid.Rows) row.Height = _grid.RowTemplate.Height;
    }

    private static int FlowHeight(FlowLayoutPanel flow, int width)
    {
        int total = flow.Padding.Vertical, rowWidth = 0, rowHeight = 0;
        int available = Math.Max(1, width - flow.Padding.Horizontal);
        foreach (Control child in flow.Controls)
        {
            Size preferred = child.GetPreferredSize(System.Drawing.Size.Empty);
            int w = preferred.Width + child.Margin.Horizontal, h = preferred.Height + child.Margin.Vertical;
            if (rowWidth > 0 && rowWidth + w > available)
            {
                total += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }
            rowWidth += w;
            rowHeight = Math.Max(rowHeight, h);
        }
        return total + rowHeight;
    }

    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in AllControls(child)) yield return descendant;
        }
    }


    private void LoadDefaults()
    {
        SetNumber(_defaultThreshold, _config.DefaultThreshold);
        SetNumber(_defaultCooldown, _config.DefaultCooldownMinutes);
        SetNumber(_scanInterval, _config.ScanIntervalSeconds);
        _enableBle.Checked = _config.EnableBleFallback;
    }

    private void LoadSelectedDevice()
    {
        _selectedId = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as string : null;
        bool selected = _selectedId is not null && _readings.TryGetValue(_selectedId, out _);
        _selectedMonitoring = selected && _readings[_selectedId!].RequiresBatteryMonitoring;
        _deviceThreshold.Enabled = _selectedMonitoring;
        _deviceCooldown.Enabled = _selectedMonitoring;
        _ignore.Enabled = _selectedMonitoring;
        _saveDevice.Enabled = _selectedMonitoring;
        _restoreDevice.Enabled = _selectedMonitoring;
        if (!selected)
        {
            _deviceName.Text = "请先选择一个设备";
            _deviceRuleInfo.Text = "扫描完成后，可为每个设备设置独立的提醒规则。";
            return;
        }
        var reading = _readings[_selectedId!];
        _config.Devices.TryGetValue(_selectedId!, out var rule);
        _deviceName.Text = reading.Name;
        SetNumber(_deviceThreshold, rule?.Threshold ?? _config.DefaultThreshold);
        SetNumber(_deviceCooldown, rule?.CooldownMinutes ?? _config.DefaultCooldownMinutes);
        _ignore.Checked = rule?.Ignore ?? false;
        _deviceRuleInfo.Text = !reading.RequiresBatteryMonitoring ? "此设备无需充电提醒。\n连接方式：" + reading.Transport
            : rule is null ? "当前跟随默认设置。\n保存后启用独立规则。" : "当前使用独立规则。\n恢复默认会清除此规则。";
    }

    private void SaveDefaults()
    {
        var old = (_config.DefaultThreshold, _config.DefaultCooldownMinutes, _config.ScanIntervalSeconds, _config.EnableBleFallback);
        _config.DefaultThreshold = (int)_defaultThreshold.Value;
        _config.DefaultCooldownMinutes = (int)_defaultCooldown.Value;
        _config.ScanIntervalSeconds = (int)_scanInterval.Value;
        _config.EnableBleFallback = _enableBle.Checked;
        try { AppStorage.SaveConfig(_config); }
        catch (Exception ex)
        {
            (_config.DefaultThreshold, _config.DefaultCooldownMinutes, _config.ScanIntervalSeconds, _config.EnableBleFallback) = old;
            ShowError("默认设置未保存", ex);
            return;
        }
        RefreshRuleCells();
        LoadSelectedDevice();
        _status.Text = "默认设置已保存。独立设备设置仍按各自的规则生效。";
        RaiseSafely(ConfigurationChanged, "设置已保存，但重新应用失败");
    }

    private void SaveSelectedDevice()
    {
        if (_selectedId is null || !_selectedMonitoring) return;
        string id = _selectedId;
        _config.Devices.TryGetValue(id, out var old);
        _config.Devices[id] = new DeviceRule { Threshold = (int)_deviceThreshold.Value, CooldownMinutes = (int)_deviceCooldown.Value, Ignore = _ignore.Checked };
        try { AppStorage.SaveConfig(_config); }
        catch (Exception ex)
        {
            if (old is null) _config.Devices.Remove(id); else _config.Devices[id] = old;
            ShowError("设备设置未保存", ex);
            return;
        }
        RefreshRuleCells();
        LoadSelectedDevice();
        _status.Text = "设备设置已保存。";
        RaiseSafely(ConfigurationChanged, "设置已保存，但重新应用失败");
    }

    private void RestoreSelectedDevice()
    {
        if (_selectedId is null || !_selectedMonitoring) return;
        string id = _selectedId;
        _config.Devices.TryGetValue(id, out var old);
        _config.Devices.Remove(id);
        try { AppStorage.SaveConfig(_config); }
        catch (Exception ex)
        {
            if (old is not null) _config.Devices[id] = old;
            ShowError("默认规则未恢复", ex);
            return;
        }
        RefreshRuleCells();
        LoadSelectedDevice();
        _status.Text = "此设备已恢复默认规则，忽略选项已清除。";
        RaiseSafely(ConfigurationChanged, "设置已保存，但重新应用失败");
    }

    private string FormatRule(string id)
    {
        if (_readings.TryGetValue(id, out var reading) && !reading.RequiresBatteryMonitoring) return "无需充电";
        if (!_config.Devices.TryGetValue(id, out var rule)) return $"默认：<{_config.DefaultThreshold}%";
        if (rule.Ignore) return "已忽略提醒";
        return $"<{rule.Threshold ?? _config.DefaultThreshold}% / {rule.CooldownMinutes ?? _config.DefaultCooldownMinutes} 分钟";
    }

    private void RefreshRuleCells()
    {
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is string id)
            {
                row.Cells["DeviceSettings"].Value = FormatRule(id);
                row.Cells["DeviceSettings"].ToolTipText = FormatRule(id);
            }
        ReflowLayout();
    }

    private void ReadStartupState()
    {
        try { _startup.Checked = StartupManager.Enabled; }
        catch (Exception ex) { ShowError("无法读取自启设置", ex); }
    }

    private void ApplyStartup()
    {
        try
        {
            StartupManager.SetEnabled(_startup.Checked);
            _startup.Checked = StartupManager.Enabled;
            _status.Text = _startup.Checked ? "已启用登录后启动。" : "已关闭登录后启动。";
        }
        catch (Exception ex)
        {
            ShowError("自启设置未应用", ex);
            ReadStartupState();
        }
    }

    private void RaiseSafely(Action? action, string title)
    {
        try { action?.Invoke(); }
        catch (Exception ex) { ShowError(title, ex); }
    }

    private void ShowError(string title, Exception ex) => MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    private static string FormatObservedAt(object? observed) => observed switch
    {
        DateTimeOffset offset => offset.ToLocalTime().ToString("MM-dd HH:mm:ss"),
        DateTime time => time.ToLocalTime().ToString("MM-dd HH:mm:ss"),
        null => "—",
        _ => Convert.ToString(observed) ?? "—"
    };
    private static void SetNumber(NumericUpDown control, int value) => control.Value = Math.Clamp((decimal)value, control.Minimum, control.Maximum);
    private static NumericUpDown Number(int minimum, int maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, DecimalPlaces = 0, ThousandsSeparator = true,
        Margin = new Padding(3)
    };
    private static Label Label(string text) => new() { Text = text, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true };
    private static Button Button(string text, string name) => new() { Text = text, Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
}
