using Microsoft.Win32;

namespace BluetoothCharge;

public static class StartupManager
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BluetoothCharge";
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string;
        }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
            if (!exe.EndsWith("BluetoothCharge.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请运行已发布的 BluetoothCharge.exe 后启用自启。");
            key.SetValue(Name, $"\"{exe}\" --background");
        }
        else key.DeleteValue(Name, false);
    }
}
