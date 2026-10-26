# Windows 蓝牙电量兼容性与实现边界

本工具采用 **.NET 10、WinForms、WinRT 蓝牙 API 与 SetupAPI/PnP 属性查询**。它能够提醒 Windows 已报告电量或公开标准 BLE Battery Service 的设备，但无法为没有提供电量协议的设备凭空计算百分比。设备可以已配对或已连接；本版本仅对系统报告为已连接的 BLE 设备主动读取 GATT，避免反复唤醒、重连离线设备。支持结果依赖设备固件、蓝牙适配器、驱动和 Windows 版本，设备类型本身不能作为兼容性保证。

## 为什么选择 .NET

Windows 官方将 `Windows.Devices.Bluetooth` 提供给 UWP 和桌面应用。现代 .NET 通过 Windows 版本限定的 Target Framework Moniker 引入 WinRT 投影；WinForms 同时提供托盘和设置界面，Win32 属性查询也可直接调用。选择 C# 是为了减少 Python WinRT 投影和外部 PowerShell 进程之间的集成层，并不意味着 C# 能访问设备未提供的信息。[桌面应用调用 WinRT](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance)、[Bluetooth 命名空间](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth)。

## 分层获取策略

| 层级 | 数据来源 | 本版本行为及限制 |
| --- | --- | --- |
| 设备发现 | WinRT 经典蓝牙和 BLE 设备选择器、DeviceWatcher、Association Endpoint 属性与PnP登记 | 持续接收已配对/已连接设备增删与状态变化；全量刷新保留晚到结果。WinRT暂缺但PnP已登记的蓝牙设备也显示，连接状态未知时不提醒。设备存在或PnP状态正常不等于无线连接。 |
| Windows 电量 | `System.Devices.BatteryLife`；SetupAPI/PnP 电量属性 | 优先利用 Windows 已报告的值，开销较小。是否存在、是否更新取决于系统及驱动。 |
| 标准 BLE | Battery Service `0x180F`、Battery Level `0x2A19` | 系统层没有有效值时，对已连接的 BLE 设备尝试 Uncached 读取。设备缺少服务、拒绝访问、暂不可达时保持未知。 |
| 厂商协议 | 私有 GATT 特征、HID 报告、厂商 SDK | 0.1.2增加精确046D:C54D罗技LIGHTSPEED接收器的只读HID++百分比读取，槽位1；其他型号仍需专门适配。没有通用厂商HID解析。 |
| 手机伴随应用 | 手机端公开 BAS 或经认证的本地同步接口 | 作为后续扩展方向；仅蓝牙配对通常不足以获得手机百分比。 |

设备身份应使用系统设备标识、蓝牙地址或容器标识，而不是友好名称。同名设备、一个设备产生多个 PnP 节点、经典蓝牙与 BLE 双模式均可能导致重复记录。Windows 提供按配对状态及连接状态生成的选择器；查询时应保留传输方式和源标识，便于定位兼容性问题。[BluetoothDevice API](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothdevice)、[按配对状态选择设备](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothdevice.getdeviceselectorfrompairingstate)。

设备枚举可以分批或延迟完成，单次等待超时不代表系统只知道原来的设备。本版本以持续事件监听维护设备清单，并在手动/定时扫描时重新查询；旧快照不能覆盖较新的增删事件。它监控已经配对/连接的设备，不自动搜索或配对附近陌生设备。[微软设备枚举指南](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/enumerate-devices)、[DeviceWatcher](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcher)。

USB鼠标补充行用于说明实际传输方式。只有明确识别的有线G502 HERO（VID046D/PIDC08B）标为无需充电；这不适用于名称相似的G502无线型号，也不代表支持其他罗技型号的厂商电量协议。0.1.2的C54D专用支持见下文。[罗技G502 HERO官方规格](https://support.logi.com/hc/en-001/articles/360023303534-Specification-G502-HERO-Gaming-Mouse)。

## Windows 属性：公开定义与经验性接口

`System.Devices.BatteryLife` 是有正式文档的 Shell 属性，格式 ID 为 `{49CD1F76-5626-4B17-A4E8-18B4AA1A2213}`，属性 ID 为 `10`，类型为 Byte，含义为剩余电量百分比；`101` 及以上对应未知状态。属性定义存在并不保证每个蓝牙设备或每种设备枚举对象都填充这个属性。[System.Devices.BatteryLife 定义](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife)。

Windows 蓝牙设备上常用的 PnP 电量键是 **`{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`**。本版本将它作为经验性兼容路径读取，并仅接受明确的有效数值。该键与上述 Shell PKEY 不同；本次调查没有找到将该键定义为稳定公开蓝牙 API 的正式 SDK 文档，因此不能承诺所有 Windows 版本、驱动和设备均有该值。接口读取失败或属性缺失是正常兼容性结果。[SetupDiGetDevicePropertyW 属性读取机制](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw)。

**属性读取时间不是设备采样时间。** PnP 属性可能保存系统或驱动最近一次收到的电量；定时读取不保证设备每次都重新测量或发送。界面中的“最近读取”表示本工具获取该值的时间。离线设备的上次已知值只用于显示，不应作为实时低电量提醒依据。没有电量的设备显示“未知”，不会被转换为 `0%`。

## 标准 BLE 电量与运行约束

Bluetooth SIG 为 Battery Service 分配 `0x180F`，为 Battery Level 分配 `0x2A19`。标准电量范围为 0–100%；多电池设备可以提供多个 BAS 实例，耳机左右单元、充电盒等也可能使用私有结构，因此一个百分比不必然代表全部电池。[SIG 分配表](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Assigned_Numbers/out/en/index-en.html)、[Battery Service 1.1](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/BAS_v1.1/out/en/index-en.html)。

WinRT GATT 的 `Cached` 使用系统共享缓存，`Uncached` 向蓝牙设备取值。本版本对已连接 BLE 设备使用后者，仍需检查通信状态、长度及范围。Windows 报告“已连接”与实际读取之间存在状态变化，休眠、离开范围或固件限制均可能导致读取失败；若设备在连接检查后立即断开，Windows 底层也可能尝试重连。因此“仅读取已连接设备”是访问前的检查策略，不能保证系统绝不会重连。[BluetoothCacheMode](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothcachemode)、[GATT 连接行为](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-client)。

官方说明 `BluetoothLEDevice.FromIdAsync` 可能请求用户同意，应从 UI 线程调用。服务发现或读取会涉及连接过程，等待时间可能从瞬间延长到数分钟；应用超时并不证明底层连接请求已取消。因此需要避免扫描重叠，控制同一设备的未完成请求，并释放 GATT 服务和设备对象。低频轮询及优先使用系统值也有助于减小设备耗电。[Windows GATT Client](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-client)。

## 不同设备为什么可能读不到

| 设备类别 | 常见可用路径 | 读不到时的原因与 fallback |
| --- | --- | --- |
| BLE 键盘、鼠标 | Windows 属性或标准 BAS | 固件未开放 BAS、设备休眠、驱动未报告电量。优先检查 Windows 是否显示百分比；厂商私有协议需要逐型号适配。 |
| 经典蓝牙耳机 | Windows 蓝牙栈收到 HFP 电量后发布系统属性；部分型号另有 BAS | 音频能工作不保证电量可见。只有厂商应用可见时，可研究厂商 SDK 或私有 GATT；本工具不接管耳机音频会话。 |
| 老式键鼠、厂商无线接收器 | 型号专用 HID 报告或厂商 SDK | 蓝牙标准路径可能不存在；2.4 GHz 接收器也不等于蓝牙设备。需要专用适配，不能通用地读取任意报告并解释为百分比。 |
| 手机 | 设备主动公开电量服务，或配套同步软件 | 蓝牙配对并不自动开放系统电量。可开发手机伴随应用，通过 BAS 或带身份验证的局域网通道同步；手机端权限及后台执行限制需另行处理。 |

微软的配件指南说明：经典蓝牙 BR/EDR 配件可以使用 HFP 的 HF Indicators 传递电量，LE 配件使用 BAS。Windows 11 24H2 起支持 BAS 1.1 配件主动报告低电量或危急电量；符合条件的设备可能同时显示 Windows 原生提醒和本工具的自定义阈值提醒。[微软电量协议与提醒指南](https://learn.microsoft.com/en-us/windows-hardware/design/accessory-guidelines/bluetooth-accessory-guidelines/bluetooth-accessory-guidelines-battery-profiles-services)。

厂商 HID 适配不是无限制访问。WinRT HID API 不开放键盘、鼠标主输入 collection，Win32 输入 collection 也可能被系统独占。扩展时应优先使用厂商定义的 collection 和公开协议，并按型号识别报文。例如 Logitech 官方发布了 HID++ 2.0 文档，包括电池相关功能；它是可研究的专用协议示例，不代表本版本已经支持所有 Logitech 设备。[WinRT HID 访问限制](https://learn.microsoft.com/en-us/uwp/api/windows.devices.humaninterfacedevice)、[HID 架构](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hid-architecture)、[Logitech HID++ 2.0](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)。

HID++适配已在MR0097及USB接收器046D:C54D组合上实测，当前仅支持无线槽位1。罗技官方规格将MR0097列为PRO X SUPERLIGHT 2，接收器协议为USB HID。先动态查询ROOT功能索引，再查询1004/1000百分比能力及电量；报告通过无线设备槽位、功能和Software ID匹配。1000的含糊零值保持未知，1004在明确支持百分比时允许真实0%。只使用厂商FF00集合，不访问键鼠主输入集合，不发送配置、配对或升级请求。[罗技型号规格](https://support.logi.com/hc/en-us/articles/15235304069783-Specification-G-PRO-X-Superlight-2-Lightspeed-Gaming-Mouse)、[ROOT协议](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/features/0x0000-IRoot.rst)、[Linux HID++电量实现](https://github.com/torvalds/linux/blob/v6.12/drivers/hid/hid-logitech-hidpp.c)。

## PowerShell、WMI 的诊断角色

`Get-PnpDevice` 和 `Get-PnpDeviceProperty` 可帮助定位设备实例及驱动发布的属性；属性键支持 `{GUID} PID` 格式。它们适合诊断和交叉核对，不会让没有电量协议的设备产生新数据。本工具后台扫描无需每轮启动 PowerShell。[Get-PnpDeviceProperty](https://learn.microsoft.com/en-us/powershell/module/pnpdevice/get-pnpdeviceproperty)。

`Win32_Battery`、`Win32_PortableBattery` 与系统电池驱动主要面向计算机电池及 UPS，不能作为蓝牙耳机、鼠标、手机的通用电量 API。WMI 的 PnP 属性包装仍依赖驱动已提供的数据，不能绕过设备固件的限制。[Win32_Battery](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-battery)、[Win32_PortableBattery](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-portablebattery)、[Windows 电池驱动体系](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/battery-and-charging)。

实际排查顺序是：确认设备已配对且正在连接；查看 Windows 设置是否显示电量；检查本工具显示的数据来源和读取状态；必要时检查设备 PnP 属性或标准 BAS。若系统没有电量且设备没有 BAS，下一步需要明确型号、固件和厂商协议。降低扫描间隔或改成管理员运行通常无法解决“设备没有公开电量”的问题。
