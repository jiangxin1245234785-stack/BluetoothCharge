using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using BluetoothCharge;

internal static class UiChecks
{
    public static int Run()
    {
        int count = 0;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                void Check(bool condition, string name)
                {
                    if (!condition) throw new Exception("FAIL: " + name);
                    Console.WriteLine("PASS: " + name); count++;
                }
                var first = new DeviceReading("one", "测试蓝牙鼠标 · 长名称也应换行并可查看完整详情", true, 19,
                    "Windows 设备属性", "原始电量来源与完整设备详情", DateTimeOffset.Now);
                var second = first with { Id = "two", Name = "新连接的蓝牙键盘", Percent = 80 };
                var wired = first with { Id = "wired", Name = "Logitech G502 HERO", Percent = null,
                    Transport = "USB", RequiresBatteryMonitoring = false };
                foreach (var sample in new[] { (1340, 800, 9f), (1060, 700, 9f), (1340, 800, 12f), (1060, 700, 12f), (800, 560, 18f) })
                {
                    using var form = new SettingsForm(new AppConfig()) { Opacity = 0, ShowInTaskbar = false };
                    form.Show();
                    form.Font = new Font("Microsoft YaHei UI", sample.Item3);
                    form.Size = new Size(sample.Item1, sample.Item2);
                    form.SetReadings([first, wired], "最近扫描：12:34:56 · 2 个设备 · 读取时间不代表系统值的采样时间");
                    Pump();
                    string suffix = $" ({sample.Item1}x{sample.Item2}, {sample.Item3}pt)";
                    var leaves = Descendants(form).Where(c => c is Button or CheckBox or Label or NumericUpDown).ToArray();
                    Check(leaves.All(c => c.Height >= c.GetPreferredSize(new Size(c.Width, 0)).Height), "controls retain preferred text height" + suffix);
                    Check(leaves.OfType<Button>().All(c => c.Width >= c.GetPreferredSize(Size.Empty).Width), "button captions fit" + suffix);
                    Check(Descendants(form).OfType<FlowLayoutPanel>().All(flow => flow.Controls.Cast<Control>().All(c =>
                        c.Top >= flow.Padding.Top && c.Bottom <= flow.ClientSize.Height - flow.Padding.Bottom)), "wrapped action rows fit their panel" + suffix);
                    var grid = Field<DataGridView>(form, "_grid");
                    Check(grid.Columns.Cast<DataGridViewColumn>().All(c => c.Width >= TextRenderer.MeasureText(c.HeaderText, grid.Font).Width + 8), "column titles remain readable" + suffix);
                    var deviceScroll = Field<TableLayoutPanel>(form, "_devicePanel").Parent as ScrollableControl ?? throw new Exception("device scroll missing");
                    var restore = Field<Button>(form, "_restoreDevice");
                    if (sample == (1340, 800, 9f))
                        Check(deviceScroll.ClientRectangle.Contains(deviceScroll.RectangleToClient(restore.RectangleToScreen(restore.ClientRectangle))),
                            "default window shows both device actions without scrolling");
                    deviceScroll.ScrollControlIntoView(restore); Pump();
                    var restoreBounds = deviceScroll.RectangleToClient(restore.RectangleToScreen(restore.ClientRectangle));
                    Check(deviceScroll.ClientRectangle.Contains(restoreBounds), "device save/restore actions reachable by scrolling" + suffix);
                    var viewport = Field<Panel>(form, "_viewport");
                    var status = Field<Label>(form, "_status");
                    viewport.ScrollControlIntoView(status); Pump();
                    Check(viewport.ClientRectangle.Contains(viewport.RectangleToClient(status.RectangleToScreen(status.ClientRectangle))), "bottom status reachable without overlapping actions" + suffix);
                    if (sample == (1340, 800, 12f) && Environment.GetEnvironmentVariable("BLUETOOTHCHARGE_UI_PREVIEW_DIR") is { Length: > 0 } dir)
                    {
                        Directory.CreateDirectory(dir);
                        viewport.AutoScrollPosition = Point.Empty; deviceScroll.AutoScrollPosition = Point.Empty; Pump();
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(dir, "settings-large-font.png"));
                    }
                }
                using var live = new SettingsForm(new AppConfig()) { Opacity = 0, ShowInTaskbar = false };
                live.Show(); live.SetReadings([first], "first scan"); Pump();
                Field<NumericUpDown>(live, "_deviceThreshold").Value = 33;
                live.SetReadings([first with { Percent = 18 }, second, wired], "new inventory"); Pump();
                Check(Field<DataGridView>(live, "_grid").Rows.Count == 3, "refresh adds newly discovered device rows");
                Check(Field<NumericUpDown>(live, "_deviceThreshold").Value == 33, "inventory refresh preserves unsaved selected rule");
                var liveGrid = Field<DataGridView>(live, "_grid");
                liveGrid.ClearSelection(); liveGrid.Rows[2].Selected = true; liveGrid.CurrentCell = liveGrid.Rows[2].Cells[0]; Pump();
                Check(!Field<Button>(live, "_saveDevice").Enabled && !Field<NumericUpDown>(live, "_deviceThreshold").Enabled,
                    "wired battery-free device has no charge rule controls");
                Check(liveGrid.Rows[2].Cells["Battery"].Value?.ToString() == "无需充电", "wired battery-free row is explicit");
                int refreshes = 0;
                live.ScanRequested += () => refreshes++;
                Field<Button>(live, "_scan").PerformClick();
                Check(refreshes == 1, "rescan action requests a scan");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("UI regression failed", error);
        return count;
    }
    private static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new Exception("Missing field " + name));
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            // Native editor children and grid internals are not settings layout controls.
            if (child is NumericUpDown or DataGridView) continue;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
