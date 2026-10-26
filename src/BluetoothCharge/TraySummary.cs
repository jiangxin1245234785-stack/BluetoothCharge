using System.Text;

namespace BluetoothCharge;

public sealed record TrayDevice(string Id, string Name, int? Percent)
{
    public string BatteryText => Percent is >= 0 and <= 100 ? $"{Percent}%" : "电量未知";
}

public sealed record TraySummary(IReadOnlyList<TrayDevice> Devices)
{
    public const int NativeTextLimit = 127;
    public string Title => Devices.Count == 0 ? "暂无已连接无线设备" : $"已连接无线设备（{Devices.Count}）";
    public string FullText => string.Join("\n", new[] { Title }.Concat(Devices.Select(d => $"{d.Name}：{d.BatteryText}")));
    public bool NeedsPopup => FullText.Length > NativeTextLimit;
    public string NativeText => NeedsPopup ? string.Empty : FullText;

    public static TraySummary FromReadings(IEnumerable<DeviceReading> readings) => new(readings
        .Where(d => d.IsWireless && d.Connected && d.ConnectionKnown)
        .Select(d => new TrayDevice(d.Id, CleanName(d.Name), d.Percent is >= 0 and <= 100 ? d.Percent : null))
        .ToArray());

    private static string CleanName(string value)
    {
        var result = new StringBuilder();
        bool space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune)) { space = result.Length > 0; continue; }
            if (space) result.Append(' ');
            result.Append(rune); space = false;
        }
        return result.Length == 0 ? "未命名设备" : result.ToString();
    }
}
