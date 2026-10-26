using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using BluetoothCharge;

internal static class TrayChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name); count++;
        }
        var mouse = new DeviceReading("usb-mouse", "PRO X 2", true, 20, "罗技 HID++", "", DateTimeOffset.Now) { Transport = "USB", IsWireless = true };
        var keyboard = mouse with { Id = "keyboard", Name = "Keychron K3 Version 3", Transport = "蓝牙", Percent = 79 };
        var unknown = keyboard with { Id = "headset", Name = "无线耳机", Percent = null };
        var offline = keyboard with { Id = "offline", Name = "Xbox", Connected = false, Percent = 100 };
        var receiver = mouse with { Id = "receiver", Name = "Receiver", Connected = false, ConnectionKnown = false, Percent = null };
        var wired = mouse with { Id = "wired", Name = "G502 HERO", IsWireless = false, RequiresBatteryMonitoring = false };
        var uncertain = keyboard with { Id = "uncertain", ConnectionKnown = false };
        var summary = TraySummary.FromReadings([mouse, keyboard, unknown, offline, receiver, wired, uncertain]);
        Check(summary.Devices.Count == 3, "hover includes all confirmed connected wireless devices only");
        Check(summary.Devices.Any(d => d.Id == mouse.Id), "hover includes USB receiver wireless mouse");
        Check(summary.Devices.Any(d => d.Id == unknown.Id && d.BatteryText == "电量未知"), "connected unknown battery remains visible");
        Check(!summary.FullText.Contains("Xbox") && !summary.FullText.Contains("Receiver"), "offline and receiver-only rows excluded from hover");
        Check(!summary.FullText.Contains("G502"), "wired mouse excluded from hover");
        Check(summary.FullText.Contains("PRO X 2：20%") && summary.FullText.Contains("Keychron K3 Version 3：79%"), "hover shows names and complete battery values");
        Check(summary.Title.Contains("3") && !summary.NeedsPopup, "typical connected list uses native tooltip");
        Check(TraySummary.FromReadings([mouse with { Percent = 0 }]).FullText.Contains("：0%"), "hover preserves real zero percent");
        Check(TraySummary.FromReadings([mouse with { Percent = 101 }]).FullText.Contains("电量未知"), "hover invalid battery never shown as valid percent");
        Check(TraySummary.FromReadings([mouse with { Name = "  鼠标\r\n\0  名称\t " }]).Devices[0].Name == "鼠标 名称", "device name cannot inject extra tooltip rows");
        Check(TraySummary.FromReadings([mouse with { Name = "键盘 🔋" }]).Devices[0].Name == "键盘 🔋", "hover retains full Unicode device names");
        Check(TraySummary.FromReadings([mouse with { Name = "\0\n" }]).Devices[0].Name == "未命名设备", "empty name has readable fallback");
        var none = TraySummary.FromReadings([offline, wired, uncertain]);
        Check(none.Devices.Count == 0 && none.NativeText == "暂无已连接无线设备", "empty connected list has explicit status");
        var duplicate = TraySummary.FromReadings([mouse, mouse with { Id = "second" }]);
        Check(duplicate.Devices.Count == 2, "same-name devices are not merged in hover");
        int nameLength = TraySummary.NativeTextLimit - new TraySummary([new TrayDevice("id", "", 1)]).FullText.Length;
        var boundary = TraySummary.FromReadings([mouse with { Name = new string('字', nameLength), Percent = 1 }]);
        Check(boundary.NativeText.Length == 127 && !boundary.NeedsPopup, "native tooltip accepts exact 127 UTF16 units");
        var overflow = TraySummary.FromReadings([mouse with { Name = new string('字', nameLength + 1), Percent = 1 }]);
        Check(overflow.NeedsPopup && overflow.NativeText.Length == 0, "overflow disables native tooltip to prevent double popups");
        Check(overflow.Devices[0].Name.Length == nameLength + 1, "overflow keeps full name instead of truncating");
        var many = TraySummary.FromReadings(Enumerable.Range(1, 100).Select(i => mouse with { Id = i.ToString(), Name = $"已连接无线设备 {i} · 完整名称", Percent = i % 101 }));
        Check(many.NeedsPopup && many.Devices.Count == 100 && many.FullText.Contains("设备 100"), "large hover list preserves every connected device");

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var popup = new TrayHoverForm { Opacity = 0 };
                var area = Screen.PrimaryScreen!.WorkingArea;
                popup.Present(many, new Point(area.Right - 10, area.Bottom - 10));
                for (int i = 0; i < 4; i++) Application.DoEvents();
                var rows = Field<FlowLayoutPanel>(popup, "rows");
                var scroll = Field<Panel>(popup, "scroll");
                Check(rows.Controls.Count == 100, "overflow card renders all connected rows");
                Check(scroll.VerticalScroll.Visible, "overflow card provides a real scrollbar");
                Check(area.Contains(popup.Bounds), "hover card stays inside current screen work area");
                Check(!popup.ShowInTaskbar && popup.FormBorderStyle == FormBorderStyle.None, "hover card does not add a taskbar window");
                var withoutActivation = (bool)typeof(TrayHoverForm).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup)!;
                Check(withoutActivation, "hover card shows without requesting focus");
                var last = rows.Controls[99];
                scroll.ScrollControlIntoView(last);
                for (int i = 0; i < 4; i++) Application.DoEvents();
                Check(scroll.ClientRectangle.IntersectsWith(scroll.RectangleToClient(last.RectangleToScreen(last.ClientRectangle))), "last wireless device is reachable by scrolling");
                popup.Hide(); popup.Present(many, new Point(area.Right - 10, area.Bottom - 10));
                Check(scroll.AutoScrollPosition == Point.Empty, "new hover starts at first connected device");
                popup.Present(summary, new Point(area.Left + 10, area.Top + 10));
                Check(rows.Controls.Count == 3 && rows.Controls[0].Text.Contains("20%"), "live card update replaces obsolete device rows");
                Check(rows.Controls.Cast<Label>().All(row => !row.UseMnemonic), "ampersands in device names are rendered literally");
                if (Environment.GetEnvironmentVariable("BLUETOOTHCHARGE_UI_PREVIEW_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var image = new Bitmap(popup.Width, popup.Height);
                    popup.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(Path.Combine(directory, "hover-card.png"));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Tray UI regression failed", error);
        var negative = new Rectangle(-1920, -100, 1920, 1080);
        var placed = TrayHoverForm.Place(negative, new Size(400, 300), new Point(-20, 970));
        Check(negative.Contains(new Rectangle(placed, new Size(400, 300))), "hover placement supports negative monitor coordinates");
        Console.WriteLine($"{count} tray checks passed.");
        return count;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
