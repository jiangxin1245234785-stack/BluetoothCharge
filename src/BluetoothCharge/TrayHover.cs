namespace BluetoothCharge;

// Ordinary lists use the Windows tooltip. A long list gets an unfocused,
// scrollable card instead of silently losing connected devices to szTip's limit.
internal sealed class TrayHoverController : IDisposable
{
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly TrayHoverForm popup = new();
    private TraySummary summary = new([]);
    private Rectangle anchorArea;
    private Point anchor;
    private DateTime enteredAt;
    private DateTime? leftAt;
    private bool armed;
    private bool disposed;

    public TrayHoverController(NotifyIcon tray)
    {
        this.tray = tray;
        tray.MouseMove += MouseMove;
        tray.MouseDown += MouseDown;
        if (tray.ContextMenuStrip is { } menu) menu.Opening += MenuOpening;
        timer.Tick += Tick;
    }

    public void Update(TraySummary value)
    {
        summary = value;
        tray.Text = summary.NativeText;
        if (!summary.NeedsPopup) Hide();
        else if (popup.Visible) popup.Present(summary, anchor);
    }

    private void MouseMove(object? sender, MouseEventArgs e)
    {
        if (!summary.NeedsPopup || tray.ContextMenuStrip?.Visible == true || Control.MouseButtons != MouseButtons.None) return;
        // NotifyIcon MouseEventArgs have no usable coordinates. Keep the initial
        // screen anchor still while moving between the icon and the card.
        if (!armed)
        {
            anchor = Control.MousePosition;
            anchorArea = new Rectangle(anchor.X - 24, anchor.Y - 24, 48, 48);
            enteredAt = DateTime.UtcNow; leftAt = null; armed = true;
        }
        timer.Start();
    }

    private void MouseDown(object? sender, MouseEventArgs e) => Hide();
    private void MenuOpening(object? sender, System.ComponentModel.CancelEventArgs e) => Hide();

    private void Tick(object? sender, EventArgs e)
    {
        if (!armed || !summary.NeedsPopup || tray.ContextMenuStrip?.Visible == true)
        {
            Hide(); return;
        }
        if (Control.MouseButtons != MouseButtons.None)
        {
            if (!popup.Visible) Hide();
            else leftAt = null; // Keep the card alive while dragging its scrollbar.
            return;
        }
        var mouse = Control.MousePosition;
        var cardArea = popup.Bounds;
        cardArea.Inflate(12, 12);
        bool inside = anchorArea.Contains(mouse) || (popup.Visible && cardArea.Contains(mouse));
        if (inside)
        {
            leftAt = null;
            if (!popup.Visible && DateTime.UtcNow - enteredAt >= TimeSpan.FromMilliseconds(400)) popup.Present(summary, anchor);
        }
        else
        {
            leftAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - leftAt.Value >= TimeSpan.FromMilliseconds(300)) Hide();
        }
    }

    private void Hide()
    {
        timer.Stop(); armed = false; leftAt = null;
        popup.Hide();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tray.MouseMove -= MouseMove; tray.MouseDown -= MouseDown;
        if (tray.ContextMenuStrip is { } menu) menu.Opening -= MenuOpening;
        timer.Dispose(); popup.Dispose();
    }
}

internal sealed class TrayHoverForm : Form
{
    private readonly Label title = new() { AutoSize = true, Padding = new Padding(12, 10, 12, 8), Dock = DockStyle.Top };
    private readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly FlowLayoutPanel rows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Location = new Point(10, 0) };
    private TraySummary? current;

    public TrayHoverForm()
    {
        Text = "无线设备电量";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        Padding = new Padding(1);
        BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
        Controls.Add(scroll); Controls.Add(title); scroll.Controls.Add(rows);
        Paint += (_, e) => e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, Width - 1, Height - 1);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x80; return value; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE: preserve scrollbar clicks.
        base.WndProc(ref m);
    }

    internal void Present(TraySummary summary, Point point)
    {
        var area = Screen.FromPoint(point).WorkingArea;
        int width = Math.Min(Math.Max(320, Font.Height * 25), area.Width);
        int maxHeight = Math.Min(Math.Max(150, Math.Min(Font.Height * 18, area.Height * 2 / 3)), area.Height);
        Width = width;
        if (!Visible) scroll.AutoScrollPosition = Point.Empty;
        int rowWidth = Math.Max(1, width - 24 - SystemInformation.VerticalScrollBarWidth);
        SuspendLayout(); rows.SuspendLayout();
        try
        {
            if (!ReferenceEquals(summary, current))
            {
                scroll.AutoScrollPosition = Point.Empty;
                foreach (Control old in rows.Controls.Cast<Control>().ToArray()) old.Dispose();
                current = summary;
                title.Text = summary.Title;
                title.MaximumSize = new Size(width - 2, 0);
                foreach (var device in summary.Devices)
                {
                    var row = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(rowWidth, 0),
                        Text = $"{device.Name}：{device.BatteryText}", Margin = new Padding(0, 0, 0, 9) };
                    rows.Controls.Add(row);
                }
            }
            foreach (Control row in rows.Controls) row.MaximumSize = new Size(rowWidth, 0);
        }
        finally { rows.ResumeLayout(true); ResumeLayout(true); }
        rows.Width = rowWidth;
        rows.PerformLayout();
        rows.Height = Math.Max(1, rows.Controls.Cast<Control>().Sum(row => row.Height + row.Margin.Vertical));
        scroll.AutoScrollMinSize = new Size(0, rows.Height + 8);
        int contentHeight = title.Height + rows.Height + 10;
        Height = Math.Min(maxHeight, Math.Max(title.Height + 45, contentHeight));
        Location = Place(area, Size, point);
        if (!Visible) Show();
        // Showing a form on another display may change its DPI and physical size.
        maxHeight = Math.Min(Math.Max(150, Math.Min(Font.Height * 18, area.Height * 2 / 3)), area.Height);
        if (Width > area.Width || Height > maxHeight) Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, maxHeight));
        Location = Place(area, Size, point);
    }

    internal static Point Place(Rectangle area, Size size, Point anchor)
    {
        int x = Math.Clamp(anchor.X - size.Width / 2, area.Left, Math.Max(area.Left, area.Right - size.Width));
        int y = anchor.Y - size.Height - 10;
        if (y < area.Top) y = anchor.Y + 22;
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        return new Point(x, y);
    }
}
