using CommunityToolkit.WinUI.Notifications;
using Microsoft.Win32;

namespace BluetoothCharge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppStorage.Log("UI 错误：" + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppStorage.Log("未处理错误：" + e.ExceptionObject);
        try
        {
            if (args.Contains("--unregister"))
            {
                StartupManager.SetEnabled(false);
                ToastNotificationManagerCompat.Uninstall();
                return;
            }
            if (args.Contains("--probe"))
            {
                using var runner = new ProbeContext(args.Contains("--no-ble"));
                Application.Run(runner);
                return;
            }
            var mutexName = args.Contains("--smoke-test") ? @"Local\BluetoothCharge.Smoke." + Guid.NewGuid().ToString("N")
                : @"Local\BluetoothCharge.CurrentUser";
            using var single = new Mutex(true, mutexName, out var created);
            if (!created) return;
            using var context = new TrayContext(!args.Contains("--background"), args.Contains("--smoke-test"));
            Application.Run(context);
            GC.KeepAlive(single);
        }
        catch (Exception ex)
        {
            AppStorage.Log("启动失败：" + ex);
            Environment.ExitCode = 1;
            MessageBox.Show("程序无法启动：" + ex.Message + "\n日志目录：" + AppStorage.DataDirectory,
                "蓝牙充电提醒", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class ProbeContext : ApplicationContext
{
    private readonly Form dispatcher = new() { ShowInTaskbar = false, Opacity = 0 };
    public ProbeContext(bool noBle)
    {
        dispatcher.Load += async (_, _) =>
        {
            try
            {
                using var scanner = new DeviceScanner(AppStorage.Log) { EnableBleFallback = !noBle };
                var readings = await scanner.ScanAsync(CancellationToken.None);
                AppStorage.Save("diagnostics.json", new { generatedAt = DateTimeOffset.Now, warnings = scanner.LastWarnings, readings });
            }
            catch (Exception ex) { AppStorage.Log("Probe 失败：" + ex); Environment.ExitCode = 1; }
            finally { dispatcher.Close(); ExitThread(); }
        };
        dispatcher.Show();
    }
    protected override void Dispose(bool disposing) { if (disposing) dispatcher.Dispose(); base.Dispose(disposing); }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig config;
    private readonly ReminderState state;
    private readonly SettingsForm settings;
    private readonly NotifyIcon tray;
    private Icon trayIcon;
    private readonly Icon applicationIcon;
    private readonly TrayHoverController trayHover;
    private readonly ContextMenuStrip trayMenu;
    private readonly Notifier notifier;
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly System.Windows.Forms.Timer inventoryTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource cancellation = new();
    private readonly DeviceScanner scanner;
    private readonly bool smokeTest;
    private bool busy;
    private bool disposed;
    private bool scanPending;
    private List<DeviceReading> readings = [];
    public TrayContext(bool showSettings, bool smokeTest)
    {
        this.smokeTest = smokeTest;
        config = AppStorage.LoadConfig();
        state = AppStorage.LoadState();
        settings = new SettingsForm(config);
        applicationIcon = AppIcon.CreateApplicationIcon();
        settings.Icon = applicationIcon;
        _ = settings.Handle; // Creates a WinForms STA dispatcher even in background mode.
        scanner = new DeviceScanner(AppStorage.Log);
        scanner.InventoryChanged += () => Dispatch(() =>
        {
            inventoryTimer.Stop(); inventoryTimer.Start();
        });
        inventoryTimer.Tick += async (_, _) => { inventoryTimer.Stop(); await ScanAsync(); };
        trayIcon = AppIcon.CreateTrayIcon();
        tray = new NotifyIcon { Text = "正在读取无线设备电量…", Icon = trayIcon, Visible = true };
        var menu = trayMenu = new ContextMenuStrip();
        menu.Items.Add("设备与设置", null, (_, _) => OpenSettings());
        menu.Items.Add("立即扫描", null, async (_, _) => await ScanAsync());
        menu.Items.Add("测试通知", null, (_, _) => notifier!.Show("测试设备", 10, 20, true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        trayHover = new TrayHoverController(tray);
        tray.DoubleClick += (_, _) => OpenSettings();
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        notifier = new Notifier(tray, () => Dispatch(OpenSettings), Dispatch);
        settings.ScanRequested += async () => await ScanAsync();
        settings.TestNotificationRequested += () => notifier.Show("测试设备", 10, 20, true);
        settings.ExportRequested += Export;
        settings.ConfigurationChanged += () =>
        {
            timer.Interval = checked(config.ScanIntervalSeconds * 1000);
            settings.SetReadings(readings, "设置已保存；下次扫描生效。");
        };
        timer.Interval = checked(config.ScanIntervalSeconds * 1000);
        timer.Tick += async (_, _) => await ScanAsync();
        timer.Start();
        if (showSettings && !smokeTest) OpenSettings();
        settings.BeginInvoke(async () => await ScanAsync());
    }
    private void OpenSettings()
    {
        settings.Show(); settings.WindowState = FormWindowState.Normal; settings.Activate();
    }
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatch(() =>
    {
        var next = AppIcon.CreateTrayIcon();
        tray.Icon = next;
        var previous = trayIcon; trayIcon = next; previous.Dispose();
    });
    private void Dispatch(Action action)
    {
        if (disposed || settings.IsDisposed || settings.Disposing || !settings.IsHandleCreated) return;
        try { settings.BeginInvoke(action); }
        catch (InvalidOperationException) { } // Exit can dispose the handle between checking and posting.
    }
    private async Task ScanAsync()
    {
        if (cancellation.IsCancellationRequested) return;
        if (busy) { scanPending = true; return; }
        busy = true;
        settings.SetBusy(true);
        try
        {
            scanner.EnableBleFallback = config.EnableBleFallback;
            readings = await scanner.ScanAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var traySummary = TraySummary.FromReadings(readings);
            trayHover.Update(traySummary);
            var now = DateTimeOffset.Now;
            if (smokeTest)
            {
                settings.SetReadings(readings, "运行验证：扫描和设置界面加载成功。");
                settings.SetBusy(false);
                settings.Opacity = 0; settings.ShowInTaskbar = false; settings.Show();
                await Task.Delay(150);
                using var image = new Bitmap(settings.Width, settings.Height);
                settings.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                Directory.CreateDirectory(AppStorage.DataDirectory);
                image.Save(Path.Combine(AppStorage.DataDirectory, "settings-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                var toastSubmitted = Environment.GetCommandLineArgs().Contains("--test-toast")
                    ? notifier.Show("测试设备", 10, 20, true) : (bool?)null;
                AppStorage.Save("smoke-test.json", new { result = scanner.LastWarnings.Count == 0 ? "passed" : "partial",
                    generatedAt = now, devices = readings.Count, toastSubmitted, warnings = scanner.LastWarnings, readings,
                    trayText = tray.Text, trayDevices = traySummary.Devices, trayPopupRequired = traySummary.NeedsPopup,
                    icon = "Lucide battery-charging" });
                // Allow asynchronous Toast failure callbacks a short UI message-loop turn.
                await Task.Delay(toastSubmitted is null ? 100 : 2000);
                Quit();
                return;
            }
            var changed = false;
            foreach (var item in readings)
            {
                if (!item.ConnectionKnown || !item.RequiresBatteryMonitoring) continue;
                if (!ReminderPolicy.ShouldNotify(item.Id, item.Connected, item.Percent, config, state, now)) continue;
                config.Devices.TryGetValue(item.Id, out var rule);
                if (notifier.Show(item.Name, item.Percent!.Value, rule?.Threshold ?? config.DefaultThreshold))
                {
                    state.LastNotified[item.Id] = now;
                    changed = true;
                }
            }
            if (changed) AppStorage.SaveState(state);
            var status = $"最近扫描：{now:HH:mm:ss} · {readings.Count} 个设备 · 读取时间不代表系统值的采样时间";
            if (scanner.LastWarnings.Count > 0) status += $" · {scanner.LastWarnings.Count} 项扫描警告，详见日志";
            if (AppStorage.RecoveryMessage is { } message) status += " · " + message;
            settings.SetReadings(readings, status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppStorage.Log("扫描失败：" + ex);
            if (!settings.IsDisposed) settings.SetReadings(readings, "扫描失败；请查看日志：" + ex.Message);
            if (smokeTest) { Environment.ExitCode = 1; Quit(); }
        }
        finally
        {
            busy = false;
            if (!settings.IsDisposed) settings.SetBusy(false);
            if (scanPending && !disposed && !cancellation.IsCancellationRequested)
            {
                scanPending = false;
                settings.BeginInvoke(async () => await ScanAsync());
            }
        }
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "BluetoothCharge-diagnostics.json" };
        if (dialog.ShowDialog(settings) != DialogResult.OK) return;
        try
        {
            var report = new { generatedAt = DateTimeOffset.Now, os = Environment.OSVersion.ToString(), config, readings, warnings = scanner.LastWarnings,
                note = "包含设备名称/ID/地址等隐私数据；分享前请检查。系统电量采样时间未知。" };
            File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(report, AppStorage.JsonOptions), System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { MessageBox.Show(settings, ex.Message, "导出失败"); }
    }
    private void Quit()
    {
        cancellation.Cancel(); timer.Stop(); inventoryTimer.Stop(); tray.Visible = false; ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            SystemEvents.UserPreferenceChanged -= ThemeChanged;
            cancellation.Cancel(); inventoryTimer.Dispose(); timer.Dispose(); scanner.Dispose(); trayHover.Dispose();
            tray.Dispose(); trayMenu.Dispose(); settings.Dispose(); trayIcon.Dispose(); applicationIcon.Dispose(); cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
