using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BluetoothCharge;

public sealed record DeviceReading(
    string Id, string Name, bool Connected, int? Percent, string Source,
    string Detail, DateTimeOffset ObservedAt)
{
    public string Transport { get; init; } = "蓝牙";
    public bool ConnectionKnown { get; init; } = true;
    public bool RequiresBatteryMonitoring { get; init; } = true;
    public bool IsWireless { get; init; } = true;
    public string? ConnectionDescription { get; init; }
}

/// <summary>
/// Read-only collector. Call ScanAsync from the WinForms STA/UI thread: WinRT
/// BluetoothLEDevice.FromIdAsync can request user consent and requires that thread.
/// PnP presence deliberately does not count as a Bluetooth connection.
/// </summary>
public sealed class DeviceScanner : IDisposable
{
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.Aep.ContainerId", "System.Devices.ContainerId",
        "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired",
        "System.Devices.BatteryLife"
    ];
    private static readonly Guid BatteryService = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevel = new("00002a19-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromSeconds(3);
    private static readonly Guid ComputerContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<string> _warnings = new();
    public IReadOnlyList<string> LastWarnings => _warnings.ToArray();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    // A timed-out Windows connection/read may keep running: retain the task and
    // its resources until its own finally runs, and never start another read of it.
    private readonly Dictionary<string, Task<BleResult>> _pendingBle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<DeviceInformationCollection>> _pendingEnumeration = new();
    private readonly Dictionary<string, WatcherInventory> _inventories = new();
    private volatile bool _disposed;
    public event Action? InventoryChanged;

    public DeviceScanner(Action<string>? log = null) => _log = log;

    public bool EnableBleFallback { get; set; } = true;

    public async Task<List<DeviceReading>> ScanAsync(CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _warnings.Clear();
            foreach (var key in _pendingBle.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
            {
                // Observe and release late probes even if a device was unpaired.
                _ = _pendingBle[key].Exception;
                _pendingBle.Remove(key);
            }
            var endpoints = new List<Endpoint>();
            // Returning to the captured UI context is intentional; do not add
            // ConfigureAwait(false) before calling the BLE factory below.
            await EnumerateAsync(false, endpoints, cancellationToken);
            await EnumerateAsync(true, endpoints, cancellationToken);
            var pnp = await Task.Run(() => ReadPnp(cancellationToken), cancellationToken);
            var groups = GroupDevices(endpoints, pnp);
            var output = new List<DeviceReading>(groups.Count);
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    output.Add(WithMetadata(await ReadGroupAsync(group, cancellationToken), group));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    Log($"Device {group.Id}: {exception.GetType().Name}: {exception.Message}");
                    output.Add(WithMetadata(new(group.Id, group.Name, group.Connected, null, "未知",
                        $"读取失败：{exception.Message}", DateTimeOffset.UtcNow), group));
                }
            }
            return output.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { _scanGate.Release(); }
    }

    private async Task EnumerateAsync(bool ble, List<Endpoint> endpoints, CancellationToken cancellationToken)
    {
        var transport = ble ? "BLE" : "Classic";
        WatcherInventory? inventory = null;
        try
        {
            if (!_inventories.TryGetValue(transport, out inventory))
            {
                var paired = ble ? BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
                    : BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var connected = ble ? BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
                    : BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                inventory = new(this, ble, $"({paired}) OR ({connected})");
                _inventories.Add(transport, inventory);
            }
            inventory.Start();
            if (_pendingEnumeration.TryGetValue(transport, out var old))
            {
                if (!old.IsCompleted)
                {
                    // The watcher supplies incremental results while the previous
                    // snapshot is still pending. Never discard its late results.
                    return;
                }
                _ = old.Exception;
                _pendingEnumeration.Remove(transport);
            }
            // Every refresh requests a new snapshot when no previous query is in
            // flight; live watcher results remain available throughout the query.
            var operation = SnapshotAsync(inventory);
            _pendingEnumeration[transport] = operation;
            await operation.WaitAsync(SnapshotWait, cancellationToken);
            _pendingEnumeration.Remove(transport);
        }
        catch (TimeoutException) { /* The watcher and late snapshot keep discovering devices. */ }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Log($"{transport} enumeration: {exception.GetType().Name}: {exception.Message}");
            // The other transport can still provide useful readings.
        }
        finally
        {
            if (inventory is not null) endpoints.AddRange(inventory.Snapshot());
        }
    }

    private async Task<DeviceInformationCollection> SnapshotAsync(WatcherInventory inventory)
    {
        var revision = inventory.Revision;
        var devices = await DeviceInformation.FindAllAsync(inventory.Selector, RequestedProperties,
            DeviceInformationKind.AssociationEndpoint);
        if (!_disposed) inventory.MergeSnapshot(devices, revision);
        return devices;
    }

    private static Endpoint ToEndpoint(DeviceInformation device, bool ble)
    {
        var containers = new HashSet<Guid>();
        foreach (var property in new[] { "System.Devices.Aep.ContainerId", "System.Devices.ContainerId" })
            if (device.Properties.TryGetValue(property, out var value) && TryGuid(value, out var container) && container != ComputerContainer)
                containers.Add(container);
        device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var address);
        device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connected);
        device.Properties.TryGetValue("System.Devices.BatteryLife", out var battery);
        return new(device.Id, device.Name, ble, connected is true, containers, NormalizeAddress(address as string), ValidPercent(battery))
        { ConnectionKnown = connected is bool };
    }

    private DeviceReading WithMetadata(DeviceReading reading, DeviceGroup group)
    {
        if (group.Endpoints.Count > 0 && !group.Usb)
        {
            // A watcher may report a disconnect/removal while an awaited GATT
            // operation is running. Recheck before publishing or issuing alerts.
            var ids = group.Endpoints.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var live = _inventories.Values.SelectMany(x => x.Snapshot()).Where(x => ids.Contains(x.Id)
                || x.Containers.Overlaps(group.Containers) || (x.Address is not null && group.Addresses.Contains(x.Address))).ToList();
            group.Connected = reading.Connected && live.Any(x => x.Connected);
            group.ConnectionKnown = group.Connected || (live.Count > 0 && live.All(x => x.ConnectionKnown));
            if (!group.Connected && reading.Percent is not null)
            {
                reading = reading.Source == "BLE Battery Service" ? reading with
                {
                    Percent = null, Source = "未知", Detail = "蓝牙连接在本轮读取过程中断开或移除；本轮电量不用于提醒。"
                } : reading with { Source = "Windows 设备属性（缓存）" };
            }
        }
        var logitechReceiver = group.Usb && SupportedLogitechReceiver(group) is not null;
        var hidConnection = logitechReceiver && reading.Source == "罗技 HID++";
        return reading with
        {
            Connected = logitechReceiver ? hidConnection && reading.Connected : group.Connected,
            Transport = group.Usb ? "USB" : group.Endpoints.Count == 0 ? "蓝牙（PnP）" : "蓝牙",
            ConnectionKnown = logitechReceiver ? hidConnection && reading.ConnectionKnown : group.ConnectionKnown,
            RequiresBatteryMonitoring = !group.NoBattery,
            IsWireless = !group.Usb || logitechReceiver,
            ConnectionDescription = reading.ConnectionDescription ?? (group.Usb && !group.NoBattery ? "接收器已接入" : null)
        };
    }

    private static string? SupportedLogitechReceiver(DeviceGroup group)
        => group.PnpNodes.Select(x => x.UsbParent).OfType<string>()
            .Where(x => Regex.IsMatch(x, @"^USB\\VID_046D&PID_C54D(?:&|\\)", RegexOptions.IgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private async Task<DeviceReading> ReadGroupAsync(DeviceGroup group, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (group.Usb)
        {
            var receiver = SupportedLogitechReceiver(group);
            if (receiver is not null)
            {
                try
                {
                    var reading = await LogitechHidBattery.ReadAsync(receiver, cancellationToken);
                    if (reading is not null && reading.Percent is >= 0 and <= 100)
                        return new(group.Id, string.IsNullOrWhiteSpace(reading.Name) ? "罗技 LIGHTSPEED 鼠标" : reading.Name,
                            reading.Connected, reading.Connected ? reading.Percent : null, "罗技 HID++", reading.Detail,
                            DateTimeOffset.UtcNow)
                        {
                            ConnectionKnown = true,
                            IsWireless = true,
                            ConnectionDescription = reading.Connected ? "鼠标已连接" : "鼠标未连接（接收器已接入）"
                        };
                    return new(group.Id, "罗技 LIGHTSPEED 鼠标", false, null, "未知",
                        "046D:C54D 接收器已接入；未取得有效的罗技 HID++ 电量响应，鼠标无线连接未确认。此设备需要专用 HID++ 适配，不使用电压估算。", now)
                    { ConnectionKnown = false, IsWireless = true, ConnectionDescription = "接收器已接入" };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    Log($"Logitech HID++ {receiver}: {exception.Message}");
                    return new(group.Id, "罗技 LIGHTSPEED 鼠标", false, null, "未知",
                        $"046D:C54D 接收器已接入；罗技 HID++ 专用读取失败，鼠标无线连接及电量未确认：{exception.Message}", now)
                    { ConnectionKnown = false, IsWireless = true, ConnectionDescription = "接收器已接入" };
                }
            }
            return new(group.Id, group.Name, true, null, group.NoBattery ? "不适用" : "未知",
                group.NoBattery ? "G502 HERO（VID 046D / PID C08B）是有线 USB 鼠标，没有需要充电的电池。"
                    : "USB 鼠标/接收器；USB 接收器不等同于电脑蓝牙配对。无线设备的电量需要厂商 HID 协议，当前未实现。", now)
            { IsWireless = false };
        }
        if (group.Samples.Count > 0)
        {
            var values = group.Samples.Select(x => x.Percent).Distinct().Order().ToArray();
            var detail = "Windows 本轮返回的设备属性；驱动可能提供缓存值，系统没有提供实际测量时间。";
            if (values.Length > 1)
                detail += $" 多个属性或组件报告 {string.Join("/", values)}%，采用最低值。";
            if (!group.Connected) detail += group.ConnectionKnown ? " 当前未连接，仅展示系统缓存；不会提醒。"
                : " 蓝牙连接状态未确认，仅展示系统缓存；不会提醒。";
            detail += " 来源：" + string.Join("；", group.Samples.Select(x => x.Source).Distinct());
            return new(group.Id, group.Name, group.Connected, values[0],
                group.Connected ? "Windows 设备属性" : "Windows 设备属性（缓存）", detail, now);
        }
        if (!group.Connected)
            return new(group.Id, group.Name, false, null, "未知", group.ConnectionKnown
                ? "设备未连接，系统无电量属性；本轮未主动访问 BLE。"
                : "Windows PnP 已登记此蓝牙设备，电量和实际连接状态未确认；不会主动连接、配对或提醒。", now);
        if (!EnableBleFallback)
            return new(group.Id, group.Name, true, null, "未知", "系统未公开电量；设置中已关闭 BLE 标准电量读取。", now);
        var bleEndpoints = group.Endpoints.Where(x => x.Ble && x.Connected).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (bleEndpoints.Count == 0)
            return new(group.Id, group.Name, true, null, "未知",
                "系统未公开电量；没有当前已连接的 BLE 端点可读取标准 Battery Service。Classic/厂商协议需专用适配。", now);
        foreach (var endpoint in bleEndpoints)
        {
            if (_pendingBle.TryGetValue(endpoint.Id, out var previous))
            {
                if (!previous.IsCompleted)
                    return new(group.Id, group.Name, true, null, "未知",
                        "上次 BLE 操作仍由 Windows 处理；本轮跳过，防止重复连接/读取。", now);
                _ = previous.Exception;
                _pendingBle.Remove(endpoint.Id);
            }
        }
        // One physical device, one probe. Never guess identity from a display name.
        var selected = bleEndpoints[0];
        var probe = ReadBleAsync(selected.Id);
        _pendingBle[selected.Id] = probe;
        try
        {
            var result = await probe.WaitAsync(OperationTimeout, cancellationToken);
            _pendingBle.Remove(selected.Id);
            var physicallyConnected = result.Connected || group.Endpoints.Any(x => !x.Ble && x.Connected);
            return new(group.Id, group.Name, physicallyConnected, result.Percent,
                result.Percent is null ? "未知" : "BLE Battery Service", result.Detail, DateTimeOffset.UtcNow);
        }
        catch (TimeoutException)
        {
            Log($"BLE read timed out after {OperationTimeout.TotalSeconds}s: {group.Name}");
            return new(group.Id, group.Name, true, null, "未知",
                "BLE 读取超过 12 秒；Windows 底层操作可能继续，完成前不再重复读取。", now);
        }
        // Cancellation only stops waiting; ReadBleAsync owns and eventually disposes
        // the objects. Windows cannot reliably cancel a connection already queued.
    }

    private async Task<BleResult> ReadBleAsync(string id)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(id);
            if (device is null) return new(null, false, "Windows 未能打开 BLE 设备（可能没有访问权限）。");
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return new(null, false, "BLE 端点已断开，跳过访问；不主动连接或配对。");
            // Uncached calls are gated by live connection status. Windows can still
            // reconnect if the remote device disconnects immediately after this check.
            var result = await device.GetGattServicesForUuidAsync(BatteryService, BluetoothCacheMode.Uncached);
            var services = result.Services.ToArray();
            try
            {
                if (result.Status != GattCommunicationStatus.Success)
                    return new(null, device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                        $"BLE Battery Service 查询失败：{result.Status}。");
                if (services.Length == 0)
                    return new(null, device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                        "设备未公开标准 BLE Battery Service（180F）；需厂商协议或伴随应用。");
                var values = new List<int>();
                var failures = new List<string>();
                foreach (var service in services)
                {
                    if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                        return new(null, false, "BLE 设备在读取前断开，本轮电量未知。");
                    var characteristics = await service.GetCharacteristicsForUuidAsync(BatteryLevel, BluetoothCacheMode.Uncached);
                    if (characteristics.Status != GattCommunicationStatus.Success)
                    {
                        failures.Add($"特征查询 {characteristics.Status}");
                        continue;
                    }
                    foreach (var characteristic in characteristics.Characteristics)
                    {
                        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                        {
                            failures.Add("Battery Level 不允许读取");
                            continue;
                        }
                        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                            return new(null, false, "BLE 设备在读取前断开，本轮电量未知。");
                        var reading = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (reading.Status != GattCommunicationStatus.Success)
                        {
                            failures.Add($"特征读取 {reading.Status}");
                            continue;
                        }
                        // Standard Battery Level is exactly one unsigned byte.
                        if (reading.Value is null || reading.Value.Length != 1)
                        {
                            failures.Add("Battery Level 格式不是单字节");
                            continue;
                        }
                        using var reader = DataReader.FromBuffer(reading.Value);
                        var percent = reader.ReadByte();
                        if (percent <= 100) values.Add(percent);
                        else failures.Add($"设备返回无效电量 {percent}");
                    }
                }
                var connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                if (!connected) return new(null, false, "BLE 设备在读取过程中断开，本轮电量未知。");
                if (values.Count == 0)
                    return new(null, true, "未读取到有效 Battery Level（2A19）。" + string.Join("；", failures.Distinct()));
                var distinct = values.Distinct().Order().ToArray();
                var detail = "本轮读取标准 Battery Level（2A19），使用 Uncached 模式。";
                if (distinct.Length > 1)
                    detail += $" 多个 Battery Service 组件报告 {string.Join("/", distinct)}%，采用最低值。";
                if (failures.Count > 0) detail += " 部分组件失败：" + string.Join("；", failures.Distinct());
                return new(values.Min(), true, detail);
            }
            finally
            {
                foreach (var service in services) service.Dispose();
            }
        }
        catch (Exception exception)
        {
            Log($"BLE {id}: {exception.GetType().Name}: {exception.Message}");
            return new(null, true, $"BLE 读取失败：{exception.Message}");
        }
    }

    private List<PnpNode> ReadPnp(CancellationToken cancellationToken)
    {
        var output = new List<PnpNode>();
        var handle = Native.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, Native.DigcfAllClasses | Native.DigcfPresent);
        if (handle == new IntPtr(-1))
        {
            Log($"PnP enumeration failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return output;
        }
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new Native.SpDevinfoData { Size = (uint)Marshal.SizeOf<Native.SpDevinfoData>() };
                if (!Native.SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != Native.ErrorNoMoreItems) Log($"PnP enumeration stopped: Windows error {error}");
                    break;
                }
                // Missing properties are normal, especially for sleeping/disconnected
                // devices. Never substitute Win32_Battery (the computer's battery).
                var container = ReadGuidProperty(handle, ref data, Native.ContainerId);
                var official = ReadBatteryProperty(handle, ref data, Native.BatteryLife);
                var empirical = ReadBatteryProperty(handle, ref data, Native.BluetoothBatteryPercent);
                var instance = GetInstanceId(handle, ref data);
                var address = AddressFromInstance(instance);
                var mouse = data.ClassGuid == Native.MouseClass;
                if (address is null && (official is not null || empirical is not null || mouse))
                    address = AddressFromParents(data.DevInst);
                var usb = mouse && address is null ? UsbMouseParent(data.DevInst) : null;
                // Exclude local computer batteries and unrelated components. PnP
                // remote Bluetooth DEV nodes remain useful even without a battery.
                if (address is null && usb is null && !(instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
                    && (official is not null || empirical is not null))) continue;
                var samples = new List<BatterySample>();
                if (usb is null && official is not null) samples.Add(new(official.Value, $"PnP System.Devices.BatteryLife [{instance}]"));
                if (usb is null && empirical is not null) samples.Add(new(empirical.Value,
                    $"PnP Bluetooth 属性 {{104EA319-6EE2-4701-BD47-8DDBF425BBE5}}/2（经验性）[{instance}]"));
                if (usb?.Container is not null) container = usb.Container;
                if (container == ComputerContainer) container = null;
                var name = ReadStringProperty(handle, ref data, Native.FriendlyName)
                    ?? ReadStringProperty(handle, ref data, Native.DeviceDescription) ?? "";
                output.Add(new(instance, container, address, samples)
                {
                    Name = usb?.KnownG502 is true ? "Logitech G502 HERO" : !string.IsNullOrWhiteSpace(usb?.Name) ? usb!.Name : name,
                    UsbParent = usb?.InstanceId, UsbMouse = usb is not null, NoBattery = usb?.KnownG502 is true
                });
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(handle); }
        return output;
    }

    private static List<DeviceGroup> GroupDevices(List<Endpoint> endpoints, List<PnpNode> pnp)
    {
        var count = endpoints.Count + pnp.Count;
        var parents = Enumerable.Range(0, count).ToArray();
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
        void Link(int index, IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                if (aliases.TryGetValue(key, out var previous)) parents[Find(index)] = Find(previous);
                else aliases[key] = index;
            }
        }
        for (var index = 0; index < endpoints.Count; index++)
        {
            var endpoint = endpoints[index];
            var keys = endpoint.Containers.Select(x => $"container:{x:D}").Append("aep:" + endpoint.Id);
            if (endpoint.Address is not null) keys = keys.Append("address:" + endpoint.Address);
            Link(index, keys);
        }
        for (var index = 0; index < pnp.Count; index++)
        {
            var node = pnp[index];
            if (node.UsbMouse && node.Container == ComputerContainer) continue;
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(node.InstanceId)) keys.Add("pnp:" + node.InstanceId);
            if (node.Container is not null && node.Container != ComputerContainer) keys.Add($"container:{node.Container:D}");
            if (node.Address is not null) keys.Add("address:" + node.Address);
            if (node.UsbParent is not null) keys.Add("usb:" + node.UsbParent);
            Link(endpoints.Count + index, keys);
        }
        var groups = new Dictionary<int, DeviceGroup>();
        for (var index = 0; index < endpoints.Count; index++)
        {
            var key = Find(index);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new();
            var endpoint = endpoints[index];
            group.Endpoints.Add(endpoint);
            group.Containers.UnionWith(endpoint.Containers);
            if (endpoint.Address is not null) group.Addresses.Add(endpoint.Address);
            if (endpoint.Percent is not null) group.Samples.Add(new(endpoint.Percent.Value, "AEP System.Devices.BatteryLife"));
        }
        for (var index = 0; index < pnp.Count; index++)
        {
            var node = pnp[index];
            if (node.UsbMouse && node.Container == ComputerContainer) continue;
            var key = Find(endpoints.Count + index);
            if (!groups.TryGetValue(key, out var group))
            {
                if (node.Address is null && !node.UsbMouse) continue;
                groups[key] = group = new();
            }
            group.PnpNodes.Add(node);
            if (node.Container is not null && node.Container != ComputerContainer) group.Containers.Add(node.Container.Value);
            if (node.Address is not null) group.Addresses.Add(node.Address);
            group.Samples.AddRange(node.Samples);
        }
        foreach (var group in groups.Values)
        {
            group.Id = group.Containers.Count > 0 ? "container:" + group.Containers.Order().First().ToString("D")
                : group.Addresses.Count > 0 ? "address:" + group.Addresses.Order(StringComparer.Ordinal).First()
                : group.Endpoints.Count > 0 ? "aep:" + group.Endpoints.Select(x => x.Id).Order(StringComparer.Ordinal).First()
                : "usb:" + group.PnpNodes.Select(x => x.UsbParent ?? x.InstanceId).Order(StringComparer.Ordinal).First();
            group.Name = group.Endpoints.Where(x => !string.IsNullOrWhiteSpace(x.Name)).OrderBy(x => x.Ble)
                .ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Name).FirstOrDefault()
                ?? group.PnpNodes.Where(x => !string.IsNullOrWhiteSpace(x.Name)).OrderByDescending(x => x.NoBattery)
                    .ThenByDescending(x => Regex.IsMatch(x.InstanceId, @"^BTH(?:ENUM|LE)\\DEV_", RegexOptions.IgnoreCase))
                    .ThenBy(x => x.InstanceId, StringComparer.Ordinal).Select(x => x.Name).FirstOrDefault() ?? "蓝牙设备";
            group.Connected = group.Endpoints.Any(x => x.Connected);
            group.Usb = group.Endpoints.Count == 0 && group.PnpNodes.Any(x => x.UsbMouse);
            group.NoBattery = group.Usb && group.PnpNodes.Any(x => x.NoBattery);
            group.ConnectionKnown = group.Usb || group.Connected
                || (group.Endpoints.Count > 0 && group.Endpoints.All(x => x.ConnectionKnown));
            if (group.Usb) { group.Connected = true; group.Samples.Clear(); }
        }
        return groups.Values.ToList();
    }

    private static int? ValidPercent(object? value) => value switch
    {
        byte number when number <= 100 => number,
        uint number when number <= 100 => (int)number,
        _ => null
    };

    private static bool TryGuid(object? value, out Guid guid)
    {
        guid = value is Guid typed ? typed : value is string text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
        return guid != Guid.Empty;
    }

    private static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = value.Replace(":", "").Replace("-", "").Trim();
        return compact.Length == 12 && compact.All(Uri.IsHexDigit) && compact != "000000000000" ? compact.ToUpperInvariant() : null;
    }

    private static string? AddressFromInstance(string instance)
    {
        // BTHENUM\DEV_AABBCCDDEEFF and BTHLEDEVICE\..._DEV_AABBCCDDEEFF.
        // Match a remote DEV marker exactly; never use a display name or a USB VID/PID.
        if (!instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(instance, @"(?:\\|_)DEV_([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeAddress(match.Groups[1].Value) : null;
    }

    private static string? AddressFromParents(uint instance)
    {
        for (var depth = 0; depth < 12 && Native.CM_Get_Parent(out var parent, instance, 0) == 0; depth++)
        {
            var buffer = new StringBuilder(4096);
            if (Native.CM_Get_Device_IDW(parent, buffer, buffer.Capacity, 0) == 0)
            {
                var address = AddressFromInstance(buffer.ToString());
                if (address is not null) return address;
            }
            instance = parent;
        }
        return null;
    }

    private static UsbParentInfo? UsbMouseParent(uint instance)
    {
        UsbParentInfo? first = null;
        string? devicePrefix = null;
        for (var depth = 0; depth < 12; depth++)
        {
            var buffer = new StringBuilder(4096);
            if (Native.CM_Get_Device_IDW(instance, buffer, buffer.Capacity, 0) != 0) break;
            var id = buffer.ToString();
            if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return null; // Do not label a Bluetooth radio ancestor USB.
            var match = Regex.Match(id, @"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?=&|\\)", RegexOptions.IgnoreCase);
            if (match.Success && (devicePrefix is null || match.Value.Equals(devicePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                devicePrefix ??= match.Value;
                var containerProperty = ReadDevnodeProperty(instance, Native.ContainerId);
                Guid? container = containerProperty is { Type: Native.DevpropGuid, Data.Length: 16 }
                    ? new Guid(containerProperty.Data) : null;
                // A USB mouse interface within the computer's own container can
                // belong to an internal composite device rather than a peripheral.
                if (container == ComputerContainer) return null;
                if (container == Guid.Empty) container = null;
                var name = StringValue(ReadDevnodeProperty(instance, Native.FriendlyName))
                    ?? StringValue(ReadDevnodeProperty(instance, Native.DeviceDescription)) ?? "USB 鼠标/接收器";
                var known = Regex.IsMatch(id, @"^USB\\VID_046D&PID_C08B(?:&|\\)", RegexOptions.IgnoreCase);
                var candidate = new UsbParentInfo(id, container, name, known);
                first ??= candidate;
                if (!id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            if (Native.CM_Get_Parent(out var parent, instance, 0) != 0) break;
            instance = parent;
        }
        return first;
    }

    private static NativeProperty? ReadDevnodeProperty(uint instance, Native.Devpropkey key)
    {
        var buffer = new byte[4096];
        uint length = (uint)buffer.Length;
        return Native.CM_Get_DevNode_PropertyW(instance, ref key, out var type, buffer, ref length, 0) == 0 && length <= buffer.Length
            ? new(type, buffer.AsSpan(0, (int)length).ToArray()) : null;
    }

    private static string? StringValue(NativeProperty? property)
    {
        if (property is not { Type: Native.DevpropString } || property.Data.Length % 2 != 0) return null;
        var text = Encoding.Unicode.GetString(property.Data).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ReadStringProperty(IntPtr handle, ref Native.SpDevinfoData data, Native.Devpropkey key)
        => StringValue(ReadProperty(handle, ref data, key));

    private static string GetInstanceId(IntPtr handle, ref Native.SpDevinfoData data)
    {
        var text = new StringBuilder(4096);
        return Native.SetupDiGetDeviceInstanceIdW(handle, ref data, text, (uint)text.Capacity, out _) ? text.ToString() : "";
    }

    private static Guid? ReadGuidProperty(IntPtr handle, ref Native.SpDevinfoData data, Native.Devpropkey key)
    {
        var property = ReadProperty(handle, ref data, key);
        if (property is not { Type: Native.DevpropGuid, Data.Length: 16 }) return null;
        var guid = new Guid(property.Data);
        return guid == Guid.Empty ? null : guid;
    }

    private static int? ReadBatteryProperty(IntPtr handle, ref Native.SpDevinfoData data, Native.Devpropkey key)
    {
        var property = ReadProperty(handle, ref data, key);
        if (property is null) return null;
        // Reject signed integers, string coercions, arrays and out-of-range unknown
        // sentinels such as 101/255. Accept only an actual typed byte or uint32.
        if (property.Type == Native.DevpropByte && property.Data.Length == 1)
            return ValidPercent(property.Data[0]);
        if (property.Type == Native.DevpropUInt32 && property.Data.Length == 4)
            return ValidPercent(BitConverter.ToUInt32(property.Data));
        return null;
    }

    private static NativeProperty? ReadProperty(IntPtr handle, ref Native.SpDevinfoData data, Native.Devpropkey key)
    {
        var buffer = new byte[64];
        if (!Native.SetupDiGetDevicePropertyW(handle, ref data, ref key, out var type, buffer, (uint)buffer.Length,
                out var needed, 0))
        {
            if (Marshal.GetLastWin32Error() != Native.ErrorInsufficientBuffer || needed == 0 || needed > 65536) return null;
            buffer = new byte[needed];
            if (!Native.SetupDiGetDevicePropertyW(handle, ref data, ref key, out type, buffer, (uint)buffer.Length,
                    out needed, 0)) return null;
        }
        if (needed > buffer.Length) return null;
        return new(type, buffer.AsSpan(0, (int)needed).ToArray());
    }

    private void Log(string message)
    {
        _warnings.Enqueue(message);
        try { _log?.Invoke(message); }
        catch { /* Logging must never interrupt a device scan. */ }
    }
    private void NotifyInventoryChanged()
    {
        if (_disposed) return;
        try { InventoryChanged?.Invoke(); }
        catch (Exception exception) { Log($"Inventory notification: {exception.Message}"); }
    }

    private static bool ShouldApplySnapshotEntry(string id, long startedAt, long snapshotFloor,
        HashSet<string> removed, Dictionary<string, long> versions)
        => startedAt >= snapshotFloor && !removed.Contains(id)
            && (!versions.TryGetValue(id, out var revision) || revision <= startedAt);

    private static bool EndpointChanged(Endpoint? previous, Endpoint current)
        => previous is null || previous.Id != current.Id || previous.Name != current.Name
            || previous.Connected != current.Connected || previous.ConnectionKnown != current.ConnectionKnown
            || previous.Percent != current.Percent || previous.Address != current.Address
            || previous.Ble != current.Ble || !previous.Containers.SetEquals(current.Containers);

    public void Dispose()
    {
        _disposed = true;
        foreach (var inventory in _inventories.Values) inventory.Dispose();
        _inventories.Clear();
        // In-flight GATT operations keep ownership of their objects until they
        // finish. Disposing them prematurely here could race a Windows operation.
    }

    private sealed class WatcherInventory : IDisposable
    {
        private readonly DeviceScanner _owner;
        private readonly bool _ble;
        private readonly DeviceWatcher _watcher;
        private readonly object _sync = new();
        private readonly Dictionary<string, DeviceInformation> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
        private long _revision;
        private long _snapshotFloor;
        private bool _hasStarted;
        public string Selector { get; }
        public long Revision { get { lock (_sync) return _revision; } }
        public WatcherInventory(DeviceScanner owner, bool ble, string selector)
        {
            _owner = owner;
            _ble = ble;
            Selector = selector;
            _watcher = DeviceInformation.CreateWatcher(selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += Added;
            _watcher.Updated += Updated;
            _watcher.Removed += Removed;
        }
        public void Start()
        {
            if (_watcher.Status is DeviceWatcherStatus.Created or DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
            {
                lock (_sync)
                {
                    if (_hasStarted)
                    {
                        _devices.Clear();
                        _versions.Clear();
                        _removed.Clear();
                        _snapshotFloor = ++_revision;
                    }
                    _hasStarted = true;
                }
                _watcher.Start();
            }
        }
        private void Added(DeviceWatcher sender, DeviceInformation device)
        {
            lock (_sync)
            {
                if (_owner._disposed) return;
                _devices[device.Id] = device;
                _removed.Remove(device.Id);
                _versions[device.Id] = ++_revision;
            }
            _owner.NotifyInventoryChanged();
        }
        private void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            try
            {
                var changed = false;
                lock (_sync)
                {
                    if (_owner._disposed) return;
                    if (_devices.TryGetValue(update.Id, out var device))
                    {
                        var previous = ToEndpoint(device, _ble);
                        device.Update(update);
                        changed = EndpointChanged(previous, ToEndpoint(device, _ble));
                    }
                    else changed = !_versions.ContainsKey(update.Id); // One refresh for a newly observed identity.
                    _versions[update.Id] = ++_revision;
                }
                if (changed) _owner.NotifyInventoryChanged();
            }
            catch (Exception exception) { _owner.Log($"Watcher update: {exception.Message}"); }
        }
        private void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (_sync)
            {
                if (_owner._disposed) return;
                _devices.Remove(update.Id);
                _removed.Add(update.Id);
                // Tombstone prevents an older snapshot resurrecting a removed device.
                _versions[update.Id] = ++_revision;
            }
            _owner.NotifyInventoryChanged();
        }
        public void MergeSnapshot(DeviceInformationCollection snapshot, long startedAt)
        {
            var changed = false;
            lock (_sync)
            {
                if (_owner._disposed || startedAt < _snapshotFloor) return;
                foreach (var device in snapshot)
                {
                    if (!ShouldApplySnapshotEntry(device.Id, startedAt, _snapshotFloor, _removed, _versions)) continue;
                    var newer = ToEndpoint(device, _ble);
                    var previous = _devices.TryGetValue(device.Id, out var old) ? ToEndpoint(old, _ble) : null;
                    changed |= EndpointChanged(previous, newer);
                    _devices[device.Id] = device;
                    _versions[device.Id] = ++_revision;
                }
            }
            if (changed) _owner.NotifyInventoryChanged();
        }
        public List<Endpoint> Snapshot()
        {
            lock (_sync) return _devices.Values.Select(x => ToEndpoint(x, _ble)).ToList();
        }
        public void Dispose()
        {
            _watcher.Added -= Added;
            _watcher.Updated -= Updated;
            _watcher.Removed -= Removed;
            try
            {
                if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
            }
            catch (Exception exception) { _owner.Log($"Watcher stop: {exception.Message}"); }
            lock (_sync) _devices.Clear();
        }
    }

    private sealed record Endpoint(string Id, string Name, bool Ble, bool Connected, HashSet<Guid> Containers, string? Address, int? Percent)
    {
        public bool ConnectionKnown { get; init; } = true;
    }
    private sealed record BatterySample(int Percent, string Source);
    private sealed record PnpNode(string InstanceId, Guid? Container, string? Address, List<BatterySample> Samples)
    {
        public string Name { get; init; } = "";
        public string? UsbParent { get; init; }
        public bool UsbMouse { get; init; }
        public bool NoBattery { get; init; }
    }
    private sealed record UsbParentInfo(string InstanceId, Guid? Container, string Name, bool KnownG502);
    private sealed record BleResult(int? Percent, bool Connected, string Detail);
    private sealed record NativeProperty(uint Type, byte[] Data);
    private sealed class DeviceGroup
    {
        public string Id = "";
        public string Name = "";
        public bool Connected;
        public bool ConnectionKnown;
        public bool Usb;
        public bool NoBattery;
        public List<Endpoint> Endpoints { get; } = [];
        public HashSet<Guid> Containers { get; } = [];
        public HashSet<string> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<BatterySample> Samples { get; } = [];
        public List<PnpNode> PnpNodes { get; } = [];
    }

    private static class Native
    {
        public const uint DigcfAllClasses = 4, DigcfPresent = 2;
        public const int ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122;
        public const uint DevpropByte = 3, DevpropUInt32 = 7, DevpropGuid = 13, DevpropString = 18;
        public static readonly Guid MouseClass = new("4D36E96F-E325-11CE-BFC1-08002BE10318");
        public static readonly Devpropkey ContainerId = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
        public static readonly Devpropkey FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
        public static readonly Devpropkey DeviceDescription = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
        public static readonly Devpropkey BatteryLife = new(new("49CD1F76-5626-4B17-A4E8-18B4AA1A2213"), 10);
        // Empirical Windows Bluetooth driver property, not a public cross-device
        // battery API contract. It is read as an optional fallback only.
        public static readonly Devpropkey BluetoothBatteryPercent = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
        [StructLayout(LayoutKind.Sequential)]
        public struct SpDevinfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct Devpropkey(Guid formatId, uint propertyId)
        {
            public Guid FormatId = formatId;
            public uint PropertyId = propertyId;
        }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr handle, uint index, ref SpDevinfoData data);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr handle, ref SpDevinfoData data,
            StringBuilder text, uint textSize, out uint requiredSize);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr handle, ref SpDevinfoData data, ref Devpropkey key,
            out uint type, [Out] byte[] buffer, uint bufferSize, out uint requiredSize, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        public static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint CM_Get_Device_IDW(uint instance, StringBuilder buffer, int length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint CM_Get_DevNode_PropertyW(uint instance, ref Devpropkey key,
            out uint type, [Out] byte[] buffer, ref uint length, uint flags);
    }
}
