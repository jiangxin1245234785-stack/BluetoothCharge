using System.Collections;
using System.Reflection;
using BluetoothCharge;

internal static class ScannerChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Exercise collector outcomes without starting WinRT enumeration or opening a radio.
    // Reflection keeps the production collector's implementation details private.
    public static int Run()
    {
        var count = 0;
        void Check(bool outcome, string name)
        {
            if (!outcome) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            count++;
        }
        int? Percent(object? raw) => (int?)Call("ValidPercent", raw);
        string? Address(string? raw) => (string?)Call("NormalizeAddress", raw);
        var firstContainer = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var secondContainer = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var unrelatedContainer = Guid.Parse("30000000-0000-0000-0000-000000000003");
        const string mac = "AABBCCDDEEFF";

        var separate = Groups(
            [Endpoint("first", "Same Name", false, true, firstContainer),
             Endpoint("second", "Same Name", true, false, secondContainer)], []);
        Check(separate.Length == 2 && separate.Select(Id).Distinct().Count() == 2,
            "same-name devices with distinct containers stay separate");

        var classic = Endpoint("classic", "Headset", false, false, firstContainer, mac);
        var ble = Endpoint("ble", "Headset LE", true, true, firstContainer, mac);
        var merged = Groups([classic, ble], []);
        Check(merged.Length == 1, "Classic and BLE endpoints in one container merge");
        Check(Connected(merged.Single()), "one connected endpoint makes the physical device connected");
        Check(Id(merged.Single()) == "container:" + firstContainer.ToString("D"),
            "container identity is used for a physical device");
        Check(Id(Groups([ble, classic], []).Single()) == Id(merged.Single()),
            "container identity is stable when endpoint enumeration order changes");

        var offline = Groups(
            [Endpoint("offline-classic", "Offline", false, false, firstContainer, mac),
             Endpoint("offline-ble", "Offline LE", true, false, firstContainer, mac)], []);
        Check(offline.Length == 1 && !Connected(offline.Single()),
            "all-offline endpoints remain offline");

        var child = Pnp("BTHLEDEVICE\\battery-child", secondContainer, mac, 42);
        var matched = Groups([classic], [child]);
        Check(matched.Length == 1 && Samples(matched.Single()).SequenceEqual([42]),
            "PnP battery child maps by exact address despite a different container");
        Check(!Connected(matched.Single()), "a PnP node does not invent a Bluetooth connection");
        var otherAddress = Pnp("BTHLEDEVICE\\other-child", secondContainer, "AABBCCDDEEFE", 1);
        Check(Samples(Groups([classic], [otherAddress]).Single(x => Id(x) == "container:" + firstContainer.ToString("D"))).Length == 0,
            "a neighboring device address cannot contribute its battery");

        var laptop = Pnp("ACPI\\PNP0C0A\\0", unrelatedContainer, null, 1);
        var withLaptop = Groups([ble], [laptop]);
        Check(withLaptop.Length == 1 && Samples(withLaptop.Single()).Length == 0,
            "unrelated laptop battery is excluded from Bluetooth readings");
        Check(Groups([], [laptop]).Length == 0,
            "PnP batteries alone cannot create Bluetooth devices");

        var addressClassic = Endpoint("address-classic", "Dual Mode", false, true, null, mac);
        var addressBle = Endpoint("address-ble", "Dual Mode LE", true, false, null, mac);
        var byAddress = Groups([addressClassic, addressBle], []).Single();
        Check(Id(byAddress) == "address:" + mac &&
              Id(Groups([addressBle, addressClassic], []).Single()) == Id(byAddress),
            "address identity without a container is stable under reordered endpoints");

        Check(Percent((byte)0) == 0 && Percent((uint)0) == 0,
            "typed real zero battery is preserved");
        Check(Percent((byte)100) == 100 && Percent((uint)100) == 100,
            "typed full battery is accepted");
        Check(Percent((byte)101) is null && Percent((byte)255) is null && Percent((uint)101) is null,
            "unknown battery sentinels 101 and 255 are rejected");
        Check(Percent(20) is null && Percent((short)20) is null && Percent((sbyte)20) is null && Percent("20") is null,
            "signed and string battery values are rejected instead of coerced");
        Check(Percent(null) is null && Percent(true) is null && Percent(new byte[] { 20 }) is null,
            "missing and unrelated value types remain unknown");
        Check(Address(" aa:bb:cc:dd:ee:ff ") == mac && Address("aa-bb-cc-dd-ee-ff") == mac,
            "standard Bluetooth address formatting normalizes to one identity");
        Check(Address("000000000000") is null && Address("AA:BB:CC") is null &&
              Address("ZZBBCCDDEEFF") is null && Address(null) is null,
            "zero malformed and missing addresses cannot create identities");
        Check((string?)Call("AddressFromInstance", "BTHLEDEVICE\\{service}_DEV_AABBCCDDEEFF") == mac &&
              (string?)Call("AddressFromInstance", "USB\\VID_AABB&PID_CCDD\\EEFF") is null,
            "remote DEV address marker maps while USB vendor/product IDs do not");

        var nativeMouse = Set(Pnp("BTHLE\\DEV_AABBCCDDEEFF", firstContainer, mac, 18), "Name", "Bluetooth mouse fixture");
        var nativeGroup = Groups([], [nativeMouse]).Single();
        Check(Field<string>(nativeGroup, "Name") == "Bluetooth mouse fixture" && Samples(nativeGroup).SequenceEqual([18]),
            "native Bluetooth registration stays visible when WinRT inventory is incomplete");
        Check(!Connected(nativeGroup) && !Field<bool>(nativeGroup, "ConnectionKnown"),
            "native-only Bluetooth registration has unknown connection status instead of connected");
        using var scanner = new DeviceScanner();
        var nativeReading = Metadata(scanner, nativeGroup, new(Id(nativeGroup), "Mouse", false, 18, "Windows 设备属性", "fixture", DateTimeOffset.UtcNow));
        Check(nativeReading.Transport == "蓝牙（PnP）" && !nativeReading.ConnectionKnown && nativeReading.RequiresBatteryMonitoring,
            "native Bluetooth metadata distinguishes inventory fallback from a confirmed connection");

        var usbMouse = Set(Set(Set(Set(Pnp("HID\\VID_046D&PID_C08B", secondContainer, null, 1),
            "Name", "Logitech G502 HERO"), "UsbParent", "USB\\VID_046D&PID_C08B\\fixture"), "UsbMouse", true), "NoBattery", true);
        var usbGroup = Groups([], [usbMouse]).Single();
        Check(Field<bool>(usbGroup, "Usb") && Field<bool>(usbGroup, "NoBattery") && Connected(usbGroup) && Samples(usbGroup).Length == 0,
            "known wired USB mouse is present without importing any battery sample");
        var usbReading = Metadata(scanner, usbGroup, new(Id(usbGroup), "G502 HERO", true, null, "不适用", "fixture", DateTimeOffset.UtcNow));
        Check(usbReading.Transport == "USB" && usbReading.ConnectionKnown && !usbReading.RequiresBatteryMonitoring,
            "wired USB mouse is marked not applicable for charging reminders");
        var usbReceiver = Set(Set(Set(Pnp("HID\\VID_046D&PID_C54D", unrelatedContainer, null, 1),
            "Name", "USB receiver fixture"), "UsbParent", "USB\\VID_046D&PID_C54D\\fixture"), "UsbMouse", true);
        var receiverGroup = Groups([], [usbReceiver]).Single();
        var receiverReading = Metadata(scanner, receiverGroup, new(Id(receiverGroup), "Receiver", true, null, "未知", "fixture", DateTimeOffset.UtcNow));
        Check(receiverReading.Transport == "USB" && receiverReading.RequiresBatteryMonitoring && Samples(receiverGroup).Length == 0,
            "unknown USB receiver does not inherit the known wired model's no-battery classification");
        Check(!receiverReading.Connected && !receiverReading.ConnectionKnown && receiverReading.ConnectionDescription == "接收器已接入",
            "attached C54D receiver cannot turn failed battery retrieval into a confirmed wireless connection");
        var hidReading = Metadata(scanner, receiverGroup, new(Id(receiverGroup), "PRO X 2", true, 42, "罗技 HID++", "fixture", DateTimeOffset.UtcNow)
            { ConnectionKnown = true, ConnectionDescription = "鼠标已连接" });
        Check(hidReading.Percent == 42 && hidReading.Connected && hidReading.ConnectionKnown && hidReading.ConnectionDescription == "鼠标已连接",
            "successful C54D HID battery and confirmed mouse connection survive USB metadata decoration");
        var offlineHid = Metadata(scanner, receiverGroup, new(Id(receiverGroup), "PRO X 2", false, null, "罗技 HID++", "fixture", DateTimeOffset.UtcNow)
            { ConnectionKnown = true, ConnectionDescription = "鼠标未连接（接收器已接入）" });
        Check(!offlineHid.Connected && offlineHid.ConnectionKnown && offlineHid.Percent is null && offlineHid.ConnectionDescription!.StartsWith("鼠标未连接"),
            "confirmed offline HID mouse stays offline while its USB receiver remains attached");
        object Receiver(string parent) => Groups([], [Set(Set(Set(Pnp("HID\\fixture", unrelatedContainer, null, 1),
            "Name", "Receiver fixture"), "UsbParent", parent), "UsbMouse", true)]).Single();
        Check((string?)Call("SupportedLogitechReceiver", Receiver("USB\\vid_046d&pid_c54d&REV_0001\\fixture")) is not null &&
              Call("SupportedLogitechReceiver", Receiver("USB\\VID_046D&PID_C54D0\\fixture")) is null &&
              Call("SupportedLogitechReceiver", Receiver("USB\\VID_046E&PID_C54D\\fixture")) is null &&
              Call("SupportedLogitechReceiver", usbGroup) is null,
            "HID receiver allowlist accepts exact C54D identity and rejects adjacent IDs and wired G502");
        var genericReceiverGroup = Receiver("USB\\VID_046D&PID_C54E\\fixture");
        var genericReceiver = Metadata(scanner, genericReceiverGroup, new(Id(genericReceiverGroup), "Receiver", false, null, "未知", "fixture", DateTimeOffset.UtcNow)
            { ConnectionKnown = false });
        Check(genericReceiver.Transport == "USB" && genericReceiver.Connected && genericReceiver.ConnectionKnown && genericReceiver.RequiresBatteryMonitoring,
            "unsupported USB receiver retains its existing attachment metadata semantics");

        var connectedBluetooth = new DeviceReading("connected-bluetooth", "Keyboard", true, null,
            "未知", "fixture", DateTimeOffset.UtcNow);
        Check(connectedBluetooth.IsWireless,
            "existing Bluetooth readings default to wireless without changing their constructor contract");
        Check(nativeReading.IsWireless,
            "native Bluetooth inventory fallback remains wireless despite unknown connection state");
        Check(!usbReading.IsWireless,
            "wired G502 metadata overrides the record wireless default so it cannot enter the wireless hover list");
        Check(receiverReading.IsWireless && !receiverReading.Connected && !receiverReading.ConnectionKnown,
            "supported C54D is classified wireless while receiver-only attachment stays unconfirmed");
        Check(hidReading.IsWireless && hidReading.Connected && hidReading.ConnectionKnown,
            "confirmed C54D wireless mouse remains eligible alongside Bluetooth devices");
        Check(!genericReceiver.IsWireless,
            "unsupported USB receiver is excluded from wireless discovery even when USB attachment is known");
        var hoverCandidates = new[] { connectedBluetooth, nativeReading, usbReading, receiverReading,
            hidReading, offlineHid, genericReceiver,
            connectedBluetooth with { Id = "unknown-connection", ConnectionKnown = false },
            connectedBluetooth with { Id = "offline-bluetooth", Connected = false } };
        var hoverIds = hoverCandidates.Where(x => x.IsWireless && x.Connected && x.ConnectionKnown)
            .Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(hoverIds.SetEquals([connectedBluetooth.Id, hidReading.Id]),
            "wireless hover metadata includes connected Bluetooth with unknown battery and live HID mouse while excluding wired offline and receiver-only rows");

        var absentBleGroup = Groups([ble], []).Single();
        var lateBle = Metadata(scanner, absentBleGroup, new(Id(absentBleGroup), "Headset", true, 5, "BLE Battery Service", "fixture", DateTimeOffset.UtcNow));
        Check(!lateBle.Connected && !lateBle.ConnectionKnown && lateBle.Percent is null,
            "late GATT success cannot notify after its device disappears from the live inventory");
        var absentSystemGroup = Groups([ble], []).Single();
        var lateSystem = Metadata(scanner, absentSystemGroup, new(Id(absentSystemGroup), "Headset", true, 5, "Windows 设备属性", "fixture", DateTimeOffset.UtcNow));
        Check(!lateSystem.Connected && !lateSystem.ConnectionKnown && lateSystem.Percent == 5 && lateSystem.Source.Contains("缓存"),
            "system battery retained after disappearance is clearly cached with no confirmed connection");
        var tombstones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        bool Apply(string id, long started, long floor = 0) => (bool)Call("ShouldApplySnapshotEntry", id, started, floor, tombstones, versions)!;
        Check(Apply("late-discovered", 2), "late snapshot can add a newly discovered device without restarting the inventory");
        versions["live-added"] = 7; versions["live-updated"] = 8;
        Check(!Apply("live-added", 2) && !Apply("LIVE-UPDATED", 2),
            "older snapshot cannot overwrite a device added or updated by the live watcher");
        Check(Apply("live-updated", 8), "a current snapshot can refresh an unchanged watcher entry");
        tombstones.Add("removed"); versions["removed"] = 9;
        Check(!Apply("REMOVED", 2) && !Apply("removed", 20), "removed device cannot be resurrected by any later snapshot");
        tombstones.Remove("removed"); versions["removed"] = 21;
        Check(!Apply("removed", 20) && Apply("removed", 21), "live re-add can restore a removed device while superseding older snapshots");
        Check(!Apply("previous-generation", 29, 30) && Apply("current-generation", 30, 30),
            "watcher restart rejects the previous generation's late snapshot and accepts the current generation");
        bool Changed(object? previous, object current) => (bool)Call("EndpointChanged", previous, current)!;
        var unchanged = Endpoint("ble", "Headset LE", true, true, firstContainer, mac);
        Check(!Changed(ble, unchanged),
            "unchanged endpoint with a separately allocated identity set does not trigger a scan");
        Check(Changed(ble, Endpoint("ble", "Headset LE", true, false, firstContainer, mac)),
            "an explicit Bluetooth disconnect triggers an inventory refresh");
        var knownOffline = Endpoint("ble", "Headset LE", true, false, firstContainer, mac);
        Check(Changed(knownOffline, Set(Endpoint("ble", "Headset LE", true, false, firstContainer, mac), "ConnectionKnown", false)),
            "unknown connection replacing a known offline state triggers an inventory refresh");
        var internalUsb = Set(usbMouse, "Container", Guid.Parse("00000000-0000-0000-ffff-ffffffffffff"));
        Check(Groups([], [internalUsb]).Length == 0,
            "computer-internal USB mouse collection is excluded from the charging device list");
        Console.WriteLine($"{count} hardware-free scanner checks passed.");
        return count;
    }

    private static object? Call(string name, params object?[] arguments) =>
        (typeof(DeviceScanner).GetMethod(name, PrivateStatic) ?? throw new MissingMethodException(name))
        .Invoke(null, arguments);

    private static Type Inner(string name) => typeof(DeviceScanner).GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new MissingMemberException(name);

    private static object Make(string name, params object?[] arguments) =>
        Activator.CreateInstance(Inner(name), Instance, null, arguments, null)
        ?? throw new InvalidOperationException("Cannot construct fixture " + name);

    private static IList List(string name, IEnumerable<object> items)
    {
        var list = (IList)(Activator.CreateInstance(typeof(List<>).MakeGenericType(Inner(name)))
            ?? throw new InvalidOperationException("Cannot construct fixture list"));
        foreach (var item in items) list.Add(item);
        return list;
    }

    private static object Endpoint(string id, string name, bool ble, bool connected, Guid? container, string? address = null) =>
        Make("Endpoint", id, name, ble, connected,
            container is Guid value ? new HashSet<Guid> { value } : new HashSet<Guid>(), address, null);

    private static object Pnp(string id, Guid? container, string? address, int percent) =>
        Make("PnpNode", id, container, address, List("BatterySample", [Make("BatterySample", percent, "fixture")]));

    private static object[] Groups(object[] endpoints, object[] nodes) =>
        ((IEnumerable)(Call("GroupDevices", List("Endpoint", endpoints), List("PnpNode", nodes))
            ?? throw new InvalidOperationException("Missing group result"))).Cast<object>().ToArray();

    private static object Set(object fixture, string property, object? value)
    {
        fixture.GetType().GetProperty(property, Instance)!.SetValue(fixture, value);
        return fixture;
    }
    private static T Field<T>(object fixture, string name) => (T)fixture.GetType().GetField(name, Instance)!.GetValue(fixture)!;
    private static DeviceReading Metadata(DeviceScanner scanner, object group, DeviceReading reading) =>
        (DeviceReading)typeof(DeviceScanner).GetMethod("WithMetadata", Instance)!.Invoke(scanner, [reading, group])!;

    private static string Id(object group) => (string)(group.GetType().GetField("Id", Instance)!.GetValue(group)!);
    private static bool Connected(object group) => (bool)group.GetType().GetField("Connected", Instance)!.GetValue(group)!;
    private static int[] Samples(object group) =>
        ((IEnumerable)group.GetType().GetProperty("Samples", Instance)!.GetValue(group)!).Cast<object>()
        .Select(sample => (int)sample.GetType().GetProperty("Percent", Instance)!.GetValue(sample)!).ToArray();
}
