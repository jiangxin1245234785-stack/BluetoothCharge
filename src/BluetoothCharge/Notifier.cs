using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;

namespace BluetoothCharge;

public sealed class Notifier
{
    private readonly NotifyIcon tray;
    private readonly Action<Action> dispatch;
    public Notifier(NotifyIcon tray, Action openSettings, Action<Action> dispatch)
    {
        this.tray = tray;
        this.dispatch = dispatch;
        // Toolkit registers the unpackaged app's own AUMID/COM activation; no borrowed PowerShell identity.
        ToastNotificationManagerCompat.OnActivated += _ => openSettings();
    }
    public bool Show(string device, int percent, int threshold, bool test = false)
    {
        var title = test ? "蓝牙充电提醒：测试通知" : "蓝牙设备需要充电";
        var body = test ? "通知功能测试；这条消息不代表设备实际低电量。" : $"{device}：电量 {percent}%，低于设置的 {threshold}%。";
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                AppStorage.Log("Toast 被 Windows 通知设置禁用，使用托盘提醒。");
                return Balloon(title, body);
            }
            new ToastContentBuilder().AddText(title).AddText(body).AddArgument("action", "settings")
                .Show(toast =>
                {
                    toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(30);
                    toast.Failed += (_, e) =>
                    {
                        AppStorage.Log("Toast 异步失败：" + e.ErrorCode);
                        dispatch(() => Balloon(title, body));
                    };
                });
            return true; // Accepted by Windows, not proof a banner was seen (DND can suppress it).
        }
        catch (Exception ex)
        {
            AppStorage.Log("Toast 失败，使用托盘提醒：" + ex.Message);
            return Balloon(title, body);
        }
    }
    private bool Balloon(string title, string body)
    {
        try { tray.ShowBalloonTip(10000, title, body, ToolTipIcon.Warning); return true; }
        catch (Exception ex) { AppStorage.Log("托盘提醒失败：" + ex.Message); return false; }
    }
}
