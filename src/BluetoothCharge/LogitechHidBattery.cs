using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BluetoothCharge;

public sealed record LogitechBatteryReading(string Name, int Percent, bool Connected, bool? Charging, string Detail);

/// <summary>
/// Read-only HID++ battery support for the physically identified 046D:C54D
/// LIGHTSPEED receiver. Commands are limited to ROOT.GetFeature, battery
/// capabilities/status, and DeviceName length/fragments. No pairing, configuration,
/// notification-enabling, firmware, voltage conversion, or HID feature reports.
/// </summary>
public static class LogitechHidBattery
{
    private const ushort Vendor = 0x046D, Product = 0xC54D;
    private const byte MouseSlot = 0x01, SoftwareId = 0x0C;
    private const string DefaultName = "罗技 LIGHTSPEED 鼠标";
    private const int RequestTimeoutMilliseconds = 800;
    private static readonly SemaphoreSlim ReadGate = new(1, 1);

    public static async Task<LogitechBatteryReading?> ReadAsync(string receiverInstanceId, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(receiverInstanceId)) return null;
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        bool acquired = false;
        try
        {
            await ReadGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            var candidates = await Task.Run(() => FindCollections(receiverInstanceId, deadline.Token), deadline.Token).ConfigureAwait(false);
            if (candidates.Count == 0) throw new IOException("C54D：未找到本接收器的 MI_02 / HID++ 报告集合。");
            var shortCandidate = candidates.FirstOrDefault(c => c.ReportLength == 7);
            if (shortCandidate is null) throw new IOException("C54D：缺少 Usage 1 / 7B 短报告集合，无法建立双输入只读通道。");
            if (!candidates.Any(c => c.ReportLength == 20)) throw new IOException("C54D：缺少 Usage 2 / 20B 长报告集合。");
            var failures = new List<string>();
            foreach (var candidate in candidates.Where(c => c.ReportLength == 20).Take(1))
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var handle = Native.CreateFile(candidate.Path, 0xC0000000, 0x00000003, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    failures.Add("HID 无法共享打开：" + Marshal.GetLastWin32Error());
                    continue;
                }
                if (!TryGetCaps(handle, out var caps) || caps.Usage != 2 || !IsSupportedCollection(Vendor, Product, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength))
                    continue;
                using var shortHandle = Native.CreateFile(shortCandidate.Path, 0xC0000000, 0x00000003, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (shortHandle.IsInvalid) throw new IOException("C54D 短报告通道无法共享打开：" + Marshal.GetLastWin32Error());
                if (!TryGetCaps(shortHandle, out var shortCaps) || shortCaps.Usage != 1
                    || !IsSupportedCollection(Vendor, Product, shortCaps.UsagePage, shortCaps.Usage, shortCaps.InputReportByteLength, shortCaps.OutputReportByteLength))
                    throw new IOException("C54D 短报告集合描述不符合只读适配器的白名单。");
                // The handle is FILE_FLAG_OVERLAPPED. FileStream owns the Windows
                // async I/O cancellation/completion and pinned-buffer lifetime.
                // A one-byte buffer avoids buffering separate input reports.
                await using var longStream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                await using var shortStream = new FileStream(shortHandle, FileAccess.ReadWrite, 1, isAsync: true);
                await using var session = new Session(shortStream, longStream, deadline.Token);
                try
                {
                    foreach (ushort featureId in new ushort[] { 0x1004, 0x1000 })
                    {
                        byte index = await session.GetFeatureAsync(featureId, deadline.Token).ConfigureAwait(false);
                        if (index == 0) continue;
                        byte capabilityFunction = featureId == 0x1004 ? (byte)0 : (byte)1;
                        var capabilities = await session.RequestAsync(index, capabilityFunction, [], deadline.Token).ConfigureAwait(false);
                        if (capabilities is null || !SupportsPercentage(featureId, capabilities))
                        {
                            session.Notes.Add($"0x{featureId:X4} 未声明百分比能力");
                            continue;
                        }
                        byte statusFunction = featureId == 0x1004 ? (byte)1 : (byte)0;
                        var status = await session.RequestAsync(index, statusFunction, [], deadline.Token).ConfigureAwait(false);
                        if (status is null || !TryDecodeBattery(featureId, status, true, out int percent, out bool? charging))
                        {
                            session.Notes.Add($"0x{featureId:X4} 电量未知或报告无效");
                            continue;
                        }
                        string name = DefaultName;
                        // A successful battery report must survive an optional name
                        // failure. Naming cannot change the validated battery value.
                        using var nameDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        nameDeadline.CancelAfter(TimeSpan.FromSeconds(1.5));
                        try { name = await session.ReadNameAsync(nameDeadline.Token).ConfigureAwait(false) ?? DefaultName; }
                        catch (IOException ex) { session.Notes.Add("名称读取：" + ex.Message); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { session.Notes.Add("名称读取超时，保留已验证电量。"); }
                        string detail = $"046D:C54D · USB MI_02 · HID++ 2+ / slot 1 · 双输入 7B/20B、短只读查询 · 0x{featureId:X4} / index 0x{index:X2} · "
                            + "接收器转发的鼠标百分比报告；未通过电压或粗电量档推算。"
                            + $" 原始电量参数：{Convert.ToHexString(status)}。"
                            + (session.Notes.Count == 0 ? "" : " " + string.Join("；", session.Notes.Take(4)));
                        return new LogitechBatteryReading(name, percent, true, charging, detail);
                    }
                    failures.AddRange(session.Notes.Take(4));
                }
                catch (IOException ex)
                {
                    // Do not issue another ROOT lookup after a timed-out request.
                    // With a fixed SWID an old ROOT response could otherwise match
                    // a different feature lookup. Close this stream and stop.
                    throw new IOException("C54D HID++：" + ex.Message, ex);
                }
            }
            throw new IOException("C54D：没有有效的鼠标百分比报告。" + string.Join("；", failures.Distinct().Take(4)));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("C54D HID++ 查询达到 8 秒总时限。");
        }
        finally { if (acquired) ReadGate.Release(); }
    }

    // Pure protocol helpers support deterministic tests without any HID I/O.
    internal static bool IsSupportedCollection(ushort vid, ushort pid, ushort usagePage, ushort usage, int inputLength, int outputLength)
        => vid == Vendor && pid == Product && usagePage == 0xFF00
            && (usage == 0x0001 && inputLength == 7 && outputLength == 7 || usage == 0x0002 && inputLength == 20 && outputLength == 20);

    internal static bool IsSupportedReceiverInstance(string? instanceId)
    {
        if (instanceId is null) return false;
        const string prefix = "USB\\VID_046D&PID_C54D";
        if (!instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || instanceId.Length <= prefix.Length
            || instanceId[prefix.Length] is not ('\\' or '&')) return false;
        int end = instanceId.IndexOf('\\', 4);
        string hardware = end < 0 ? instanceId[4..] : instanceId[4..end];
        return instanceId.StartsWith("USB\\VID_046D&PID_C54D", StringComparison.OrdinalIgnoreCase)
            && !hardware.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SupportsPercentage(ushort featureId, ReadOnlySpan<byte> capabilities)
        => capabilities.Length >= 2 && (capabilities[1] & 0x02) != 0
            && (featureId == 0x1004 || featureId == 0x1000 && capabilities[0] >= 10);

    internal static bool TryDecodeBattery(ushort featureId, ReadOnlySpan<byte> payload, bool supportsPercentage, out int percent, out bool? charging)
    {
        percent = 0;
        charging = null;
        if (!supportsPercentage || payload.Length < (featureId == 0x1004 ? 4 : 3) || payload[0] > 100) return false;
        byte state = payload[2];
        if (featureId == 0x1000)
        {
            // 0x1000 explicitly uses zero for unknown, even while charging/full.
            if (payload[0] == 0 || state >= 5) return false;
            charging = state switch { 0 => false, 1 or 2 or 3 or 4 => true, _ => null };
        }
        else if (featureId == 0x1004)
        {
            // Percentage capability distinguishes an actual 0% from the coarse
            // level-only representation. No mapping of payload[1] is performed.
            if (state >= 4) return false;
            charging = state switch { 0 => false, 1 or 2 or 3 => true, _ => null };
        }
        else return false;
        percent = payload[0];
        return true;
    }

    internal static bool MatchesResponse(ReadOnlySpan<byte> report, byte deviceIndex, byte featureIndex, byte functionId, byte softwareId)
    {
        int length = LogicalReportLength(report);
        return length != 0 && report.Length >= length && softwareId is > 0 and <= 15 && functionId <= 15
            && report[1] == deviceIndex && report[2] == featureIndex && report[3] == (byte)((functionId << 4) | softwareId);
    }

    internal static bool TryGetProtocolError(ReadOnlySpan<byte> report, byte deviceIndex, byte featureIndex, byte functionId, byte softwareId, out byte error)
    {
        error = 0;
        int length = LogicalReportLength(report);
        if (length == 0 || report.Length < length || report[1] != deviceIndex || report[2] is not (0xFF or 0x8F)
            || report[3] != featureIndex || report[4] != (byte)((functionId << 4) | softwareId)) return false;
        error = report[5];
        return true;
    }

    private static int LogicalReportLength(ReadOnlySpan<byte> report)
        => report.Length == 0 ? 0 : report[0] switch { 0x10 => 7, 0x11 => 20, _ => 0 };

    private sealed class Session : IAsyncDisposable
    {
        private readonly FileStream _shortStream;
        private readonly FileStream _longStream;
        private readonly CancellationTokenSource _readLifetime;
        private Task<byte[]> _pendingShort;
        private Task<byte[]> _pendingLong;
        private int _shortReports, _longReports;
        private bool _preferLong = true;
        private readonly List<string> _headers = [];
        public List<string> Notes { get; } = [];

        public Session(FileStream shortStream, FileStream longStream, CancellationToken token)
        {
            _shortStream = shortStream;
            _longStream = longStream;
            _readLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Windows gives each top-level collection its own input queue. A reply
            // may use a different report ID than its request, so both queues are
            // continuously read. Each stream has exactly one tracked pending read.
            _pendingShort = ReadReportAsync(_shortStream, 7, _readLifetime.Token);
            _pendingLong = ReadReportAsync(_longStream, 20, _readLifetime.Token);
        }

        public async Task<byte> GetFeatureAsync(ushort featureId, CancellationToken token)
        {
            if (featureId is not (0x1000 or 0x1004 or 0x0005)) throw new InvalidOperationException("不允许查询此 HID++ 功能。");
            var response = await RequestAsync(0, 0, [(byte)(featureId >> 8), (byte)featureId], token).ConfigureAwait(false);
            return response is { Length: >= 3 } && response[0] is > 0 and < 0xFF ? response[0] : (byte)0;
        }

        public async Task<byte[]?> RequestAsync(byte featureIndex, byte functionId, byte[] parameters, CancellationToken token)
        {
            if (functionId > 1 || parameters.Length > 2 || featureIndex == 0 && functionId != 0)
                throw new InvalidOperationException("只允许 HID++ 只读查询。");
            var request = new byte[7];
            request[0] = 0x10;
            request[1] = MouseSlot;
            request[2] = featureIndex;
            request[3] = (byte)((functionId << 4) | SoftwareId);
            parameters.CopyTo(request, 4);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeoutMilliseconds);
            bool written = false;
            try
            {
                await _shortStream.WriteAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                written = true;
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    // WaitAsync bounds this transaction's wait, not the lifetime
                    // of either underlying read. The unfinished read remains in
                    // its field and is consumed by the next transaction.
                    Task<byte[]> completed = _pendingShort.IsCompleted && _pendingLong.IsCompleted
                        ? (_preferLong ? _pendingLong : _pendingShort)
                        : await Task.WhenAny(_pendingShort, _pendingLong).WaitAsync(timeout.Token).ConfigureAwait(false);
                    byte[] report = await completed.ConfigureAwait(false);
                    if (ReferenceEquals(completed, _pendingShort))
                    {
                        _preferLong = true;
                        _shortReports++;
                        _pendingShort = ReadReportAsync(_shortStream, 7, _readLifetime.Token);
                    }
                    else
                    {
                        _preferLong = false;
                        _longReports++;
                        _pendingLong = ReadReportAsync(_longStream, 20, _readLifetime.Token);
                    }
                    string header = Convert.ToHexString(report.AsSpan(0, Math.Min(4, report.Length)));
                    if (_headers.Count < 2 && !_headers.Contains(header)) _headers.Add(header);
                    if (TryGetProtocolError(report, MouseSlot, featureIndex, functionId, SoftwareId, out byte error))
                    {
                        Notes.Add($"index 0x{featureIndex:X2}/fn {functionId} 错误 0x{error:X2}");
                        return null;
                    }
                    if (!MatchesResponse(report, MouseSlot, featureIndex, functionId, SoftwareId)) continue;
                    int length = LogicalReportLength(report);
                    return report.AsSpan(4, length - 4).ToArray();
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException($"index 0x{featureIndex:X2}/fn {functionId} 查询超时（800 毫秒；请求 0x10/7B，"
                    + (written ? "写入完成" : "写入未完成")
                    + $"；7B 输入 {_shortReports} / 20B 输入 {_longReports}；最多两条4字节头 {string.Join(",", _headers)}）。");
            }
            catch (UnauthorizedAccessException ex) { throw new IOException("HID 通道访问被拒绝。", ex); }
        }

        private static async Task<byte[]> ReadReportAsync(FileStream stream, int length, CancellationToken token)
        {
            byte[] buffer = new byte[length];
            int count;
            try { count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); }
            catch (UnauthorizedAccessException ex) { throw new IOException("HID 输入通道访问被拒绝。", ex); }
            if (count == 0) throw new IOException("HID 输入通道已关闭。");
            return count == length ? buffer : buffer.AsSpan(0, count).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _readLifetime.Cancel();
            // Completion must be observed before either FileStream/handle closes;
            // cancellation itself is not evidence that native I/O has completed.
            try { await Task.WhenAll(_pendingShort, _pendingLong).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            finally { _readLifetime.Dispose(); }
        }

        public async Task<string?> ReadNameAsync(CancellationToken token)
        {
            byte index = await GetFeatureAsync(0x0005, token).ConfigureAwait(false);
            if (index == 0) return null;
            var size = await RequestAsync(index, 0, [], token).ConfigureAwait(false);
            if (size is null || size[0] is 0 or > 64) return null;
            int length = size[0];
            byte[] bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                var fragment = await RequestAsync(index, 1, [(byte)offset], token).ConfigureAwait(false);
                if (fragment is null || fragment.Length == 0) return null;
                int count = Math.Min(fragment.Length, length - offset);
                fragment.AsSpan(0, count).CopyTo(bytes.AsSpan(offset));
                offset += count;
            }
            try
            {
                string name = new UTF8Encoding(false, true).GetString(bytes).Trim();
                return name.Length > 0 && !name.Any(char.IsControl) ? name : null;
            }
            catch (DecoderFallbackException) { return null; }
        }
    }

    private sealed record Collection(string Path, int ReportLength);

    private static List<Collection> FindCollections(string instanceId, CancellationToken token)
    {
        if (Native.CM_Locate_DevNode(out uint requested, instanceId, 0) != 0) return [];
        uint? receiver = FindReceiver(requested);
        if (receiver is null) return [];
        Native.HidD_GetHidGuid(out var hidGuid);
        IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new IOException("无法枚举 HID 接口：" + Marshal.GetLastWin32Error());
        var found = new List<string>();
        try
        {
            for (uint index = 0; index < 512; index++)
            {
                token.ThrowIfCancellationRequested();
                var iface = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new IOException("HID 接口枚举失败：" + Marshal.GetLastWin32Error());
                }
                var device = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, ref device);
                if (required < 8 || required > 65536) continue;
                IntPtr buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, ref device)) continue;
                    if (FindReceiver(device.DevInst) != receiver) continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (path is not null && path.Contains("vid_046d&pid_c54d&mi_02", StringComparison.OrdinalIgnoreCase)) found.Add(path);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        // Metadata checks avoid ever sending data to keyboard/mouse collections.
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            token.ThrowIfCancellationRequested();
            using var metadata = Native.CreateFile(path, 0, 0x00000003, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (metadata.IsInvalid) return null;
            var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
            return Native.HidD_GetAttributes(metadata, ref attributes) && TryGetCaps(metadata, out var caps)
                && IsSupportedCollection(attributes.VendorId, attributes.ProductId, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength)
                ? new Collection(path, caps.InputReportByteLength) : null;
        }).Where(c => c is not null).Select(c => c!).ToList();
    }

    private static uint? FindReceiver(uint node)
    {
        for (int i = 0; i < 24; i++)
        {
            var id = new StringBuilder(512);
            if (Native.CM_Get_Device_ID(node, id, id.Capacity, 0) == 0 && IsSupportedReceiverInstance(id.ToString())) return node;
            if (Native.CM_Get_Parent(out uint parent, node, 0) != 0 || parent == node) break;
            node = parent;
        }
        return null;
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HidCaps caps)
    {
        caps = default;
        if (!Native.HidD_GetPreparsedData(handle, out var data)) return false;
        try { return Native.HidP_GetCaps(data, out caps) == 0x00110000; }
        finally { Native.HidD_FreePreparsedData(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData iface, IntPtr detail, uint size, out uint required, ref DeviceInfoData info);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] public static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] public static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
        [DllImport("cfgmgr32.dll")] public static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    }
}
