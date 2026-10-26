using System.Text.Json;

namespace BluetoothCharge;

public sealed class DeviceRule
{
    public int? Threshold { get; set; }
    public int? CooldownMinutes { get; set; }
    public bool Ignore { get; set; }
}

public sealed class AppConfig
{
    public int DefaultThreshold { get; set; } = 20;
    public int DefaultCooldownMinutes { get; set; } = 240;
    public int ScanIntervalSeconds { get; set; } = 300;
    public bool EnableBleFallback { get; set; } = true;
    public Dictionary<string, DeviceRule> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (DefaultThreshold is < 1 or > 100) throw new InvalidDataException("默认阈值须为 1–100%。");
        if (DefaultCooldownMinutes is < 1 or > 10080) throw new InvalidDataException("默认冷却须为 1–10080 分钟。");
        if (ScanIntervalSeconds is < 30 or > 86400) throw new InvalidDataException("扫描间隔须为 30–86400 秒。");
        if (Devices == null) throw new InvalidDataException("devices 不能为 null。");
        foreach (var (id, rule) in Devices)
        {
            if (string.IsNullOrWhiteSpace(id) || rule == null) throw new InvalidDataException("设备规则无效。");
            if (rule.Threshold is < 1 or > 100 || rule.CooldownMinutes is < 1 or > 10080)
                throw new InvalidDataException("设备阈值或冷却超出范围。");
        }
        Devices = new(Devices, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class ReminderState
{
    public Dictionary<string, DateTimeOffset> LastNotified { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ReminderPolicy
{
    // Record only after notification submission succeeds. Missing battery is never zero.
    public static bool ShouldNotify(string id, bool connected, int? percent, AppConfig config,
        ReminderState state, DateTimeOffset now)
    {
        if (!connected || percent is null or < 0 or > 100) return false;
        config.Devices.TryGetValue(id, out var rule);
        if (rule?.Ignore == true) return false;
        var threshold = rule?.Threshold ?? config.DefaultThreshold;
        if (percent.Value >= threshold) return false;
        var cooldown = rule?.CooldownMinutes ?? config.DefaultCooldownMinutes;
        return !state.LastNotified.TryGetValue(id, out var last) || now - last >= TimeSpan.FromMinutes(cooldown);
    }
}

public static class AppStorage
{
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("BLUETOOTHCHARGE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothCharge");
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    public static string? RecoveryMessage { get; private set; }
    public static AppConfig LoadConfig()
    {
        var result = Load("config.json", () => new AppConfig(), c => c.Validate());
        if (!File.Exists(Path.Combine(DataDirectory, "config.json"))) SaveConfig(result);
        return result;
    }
    public static ReminderState LoadState() => Load("state.json", () => new ReminderState(), s =>
    {
        if (s.LastNotified == null) throw new InvalidDataException("提醒状态无效。");
        s.LastNotified = new(s.LastNotified, StringComparer.OrdinalIgnoreCase);
    });
    private static T Load<T>(string file, Func<T> empty, Action<T> validate)
    {
        Directory.CreateDirectory(DataDirectory);
        var full = Path.Combine(DataDirectory, file);
        if (!File.Exists(full)) return empty();
        try
        {
            var result = JsonSerializer.Deserialize<T>(File.ReadAllText(full, System.Text.Encoding.UTF8), JsonOptions)
                ?? throw new InvalidDataException("JSON 内容为空。");
            validate(result);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var backup = full + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
            File.Move(full, backup);
            RecoveryMessage = $"{file} 无效，已备份为 {Path.GetFileName(backup)} 并恢复默认值。";
            Log(RecoveryMessage + " " + ex.Message);
            return empty();
        }
    }
    public static void SaveConfig(AppConfig config) { config.Validate(); Save("config.json", config); }
    public static void SaveState(ReminderState state) => Save("state.json", state);
    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(DataDirectory);
        var full = Path.Combine(DataDirectory, name);
        var temporary = full + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new System.Text.UTF8Encoding(false));
        File.Move(temporary, full, true);
    }
    private static readonly object LogLock = new();
    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDirectory);
                var file = Path.Combine(DataDirectory, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.Move(file, file + ".1", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", System.Text.Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
