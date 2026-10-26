using System.Drawing;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace BluetoothCharge;

/// <summary>Embedded Lucide charging-battery icons. Returned icons are owned by the caller.</summary>
public static class AppIcon
{
    private const string TrayOnLightResource = "BluetoothCharge.Assets.TrayOnLight.ico";
    private const string TrayOnDarkResource = "BluetoothCharge.Assets.TrayOnDark.ico";
    private const string ApplicationResource = "BluetoothCharge.Assets.Application.ico";

    /// <summary>Creates an independent icon matching the Windows taskbar background.</summary>
    public static Icon CreateTrayIcon()
        => CloneResource(TaskbarUsesLightTheme() ? TrayOnLightResource : TrayOnDarkResource,
            System.Windows.Forms.SystemInformation.SmallIconSize);

    /// <summary>Creates an independent blue application icon; the caller must dispose it.</summary>
    public static Icon CreateApplicationIcon()
        => CloneResource(ApplicationResource, System.Windows.Forms.SystemInformation.IconSize);

    private static Icon CloneResource(string resourceName, Size preferredSize)
    {
        using Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("缺少内嵌图标资源：" + resourceName);
        using var source = new Icon(stream, preferredSize);
        return (Icon)source.Clone();
    }

    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            // The taskbar follows the system theme. The application theme is only
            // a fallback for Windows versions without the system-theme setting.
            object? value = key?.GetValue("SystemUsesLightTheme") ?? key?.GetValue("AppsUseLightTheme");
            return value switch { int number => number != 0, long number => number != 0, _ => false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
