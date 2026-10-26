using BluetoothCharge;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
var config = new AppConfig();
var state = new ReminderState();
var now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
bool Notify(int? p, bool connected = true) => ReminderPolicy.ShouldNotify("device", connected, p, config, state, now);
Check(Notify(19), "below threshold");
Check(Notify(0), "real zero is valid");
Check(!Notify(20), "threshold equality does not alert");
Check(!Notify(100), "full battery");
Check(!Notify(null), "unknown battery never treated as zero");
Check(!Notify(-1) && !Notify(101), "invalid values rejected");
Check(!Notify(1, false), "offline cached battery never alerts");
state.LastNotified["device"] = now.AddMinutes(-239);
Check(!Notify(1), "cooldown held");
state.LastNotified["device"] = now.AddMinutes(-240);
Check(Notify(1), "cooldown boundary");
state.LastNotified["device"] = now.AddHours(1);
Check(!Notify(1), "clock moved backwards holds cooldown");
state.LastNotified.Clear();
config.Devices["device"] = new DeviceRule { Threshold = 50, CooldownMinutes = 10 };
Check(Notify(49) && !Notify(50), "device threshold override");
state.LastNotified["device"] = now.AddMinutes(-10);
Check(Notify(49), "device cooldown override");
config.Devices["device"].Ignore = true;
Check(!Notify(0), "ignored devices never alert");
config.Devices.Clear(); config.Validate();
var data = Path.Combine(Path.GetTempPath(), "BluetoothCharge-test-" + Guid.NewGuid());
Environment.SetEnvironmentVariable("BLUETOOTHCHARGE_DATA_DIR", data);
try
{
    var loaded = AppStorage.LoadConfig();
    Check(loaded.DefaultThreshold == 20, "first run defaults persisted");
    loaded.Devices["device"] = new DeviceRule { Threshold = 35, Ignore = true };
    AppStorage.SaveConfig(loaded);
    Check(AppStorage.LoadConfig().Devices["DEVICE"].Ignore, "UTF8 JSON roundtrip and insensitive IDs");
    state.LastNotified["device"] = now; AppStorage.SaveState(state);
    Check(AppStorage.LoadState().LastNotified["DEVICE"] == now, "cooldown survives restart");
    File.WriteAllText(Path.Combine(data, "config.json"), "{broken");
    Check(AppStorage.LoadConfig().DefaultThreshold == 20 && Directory.GetFiles(data, "config.json.invalid-*").Length == 1,
        "corrupt config backed up and recovered");
    File.WriteAllText(Path.Combine(data, "config.json"), "{\"scanIntervalSeconds\":0}");
    Check(AppStorage.LoadConfig().ScanIntervalSeconds == 300, "invalid interval cannot busy-loop");
    File.WriteAllText(Path.Combine(data, "config.json"), "{\"devices\":null}");
    Check(AppStorage.LoadConfig().Devices.Count == 0, "null rules recovered");
    loaded.DefaultThreshold = 101;
    try { AppStorage.SaveConfig(loaded); throw new Exception("expected validation failure"); }
    catch (InvalidDataException) { Check(true, "invalid configuration cannot be saved"); }
}
finally
{
    // A uniquely created test directory only; all normal app data remains untouched.
    Directory.Delete(data, true);
}
Console.WriteLine($"{count} policy/storage checks passed.");
var scannerCount = ScannerChecks.Run();
var uiCount = UiChecks.Run();
var hidCount = HidChecks.Run();
var trayCount = TrayChecks.Run();
Console.WriteLine($"{count + scannerCount + uiCount + hidCount + trayCount} total checks passed.");
