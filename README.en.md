# BluetoothCharge · Wireless device battery reminders for Windows

[简体中文](README.md) · [Download](https://github.com/jiangxin1245234785-stack/BluetoothCharge/releases/latest) · [Compatibility](docs/COMPATIBILITY.md) · [Changelog](CHANGELOG.md)

A Windows tray application that shows battery levels for confirmed connected wireless devices and reminds you when they fall below a chosen threshold. It uses battery information reported by Windows, standard BLE Battery Service, and a narrowly scoped Logitech USB wireless mouse adapter.

**A working connection does not guarantee an available battery percentage.** Missing data stays unknown; the app does not estimate percentages or turn unknown values into 0%.

![Anonymous synthetic example of the tray icon and hover list](docs/images/tray-preview.png)

*This is an anonymous synthetic example. Names and values are illustrative.*

## Features

- Hover over the tray icon to see confirmed connected wireless devices, including Bluetooth and supported USB wireless mice. Unknown battery levels remain visible; long lists expand into a scrollable card.
- Configure thresholds, reminder cooldowns and ignore rules per device, or inherit the global defaults. Ignoring reminders does not hide a connected device from the hover list.
- Scan periodically, watch Bluetooth inventory and connection changes, and refresh manually.
- Receive Windows notifications, retain cooldown history across restarts, and optionally start after Windows sign-in.
- Keep configuration and diagnostics locally. The app does not upload data or depend on a cloud service.

Defaults: scan every **5 minutes**, notify below **20%**, and wait at least **4 hours** between reminders for the same device. The comparison is strictly below: a 20% threshold notifies at 19%, not at 20%.

## Download and run

The target is **Windows 10 version 2004 / build 19041 or later, or Windows 11**. Release packages target **Windows x64**. Normal use requires neither administrator privileges, Python nor a .NET SDK.

1. Open [Releases](https://github.com/jiangxin1245234785-stack/BluetoothCharge/releases/latest) and choose the `win-x64` **self-contained ZIP**, recommended for most users.
2. Extract the entire archive to a permanent folder and run `BluetoothCharge.exe`. Keep the DLLs and subfolders; copying only the EXE is insufficient.
3. The device/settings window opens on first launch. Closing it leaves the app in the tray. Double-click the tray icon for settings; right-click to scan, test notifications or exit.
4. Select a device, edit its threshold, cooldown or ignore rule, and save it. To enable startup, select the Windows sign-in startup option and apply it.

The smaller **light / framework-dependent** package requires [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). The plain .NET Runtime or .NET 8 alone is insufficient. The self-contained package includes its runtime.

Startup applies to the current user's sign-in. Keep the executable at the configured location, or reapply startup after moving it. Exit from the tray before updating, and replace the complete release folder. Configuration lives separately from the program files.

Before deleting a portable copy, exit it and run `.\BluetoothCharge.exe --unregister` from its folder to remove its startup and notification registration. Then delete the program folder. You can retain or remove its local data separately.

## Compatibility boundaries

| Path | Scope and limitations |
| --- | --- |
| Windows device properties | Preferred source when Windows or the driver reports battery information. Available for some keyboards, mice, headsets and other devices; a property may be missing or cached. |
| Standard BLE | If system battery information is missing, the app can try Battery Service `180F` / Battery Level `2A19` on a currently connected BLE device. The device must expose the service and permit access. This fallback still needs broader on-device validation. |
| Logitech USB HID++ | Dedicated support is limited to **receiver `046D:C54D`, wireless slot 1**, where percentage reading has been verified. This does not establish support for other Logitech models, receivers, slots or every G HUB version. |
| Other USB receivers | An inventory row may be available while battery remains unknown. Model-specific vendor protocol support is required. An attached receiver does not establish that its wireless mouse is online. |
| Phones and multi-battery headsets | Bluetooth pairing alone does not guarantee battery access. Phones may need a companion app; earbud, left/right and charging-case batteries may use private protocols. |

The recognized wired G502 HERO is marked as not requiring charging and excluded from the wireless hover list. This classification does not apply to similarly named wireless models.

Reminders require a **confirmed connection and a valid battery level from the current scan**. Offline cached values, unknown battery, unconfirmed connections and receiver-only attachment do not trigger low-battery reminders. Windows properties do not provide the device's measurement timestamp: the displayed read time is the app's query time, and a connected device's system value may still be stale. Conflicting component/property percentages use the minimum, with the values and sources retained in details.

The app uses C# / .NET 10, WinForms, WinRT and SetupAPI, without starting PowerShell for background scans. HID++ sends read queries without changing pairing, buttons, DPI or firmware. BLE access checks the connection first, but Windows may still reconnect if the device disconnects immediately after that check.

See [COMPATIBILITY.md](docs/COMPATIBILITY.md) for API research, protocol sources and model restrictions. [VALIDATION.md](docs/VALIDATION.md) describes bounded verification and untested areas; automated tests are not evidence of universal hardware compatibility.

## Troubleshooting

1. Check the device name, pairing and connection state in Windows Bluetooth settings. Make sure its selected connection channel belongs to this computer. The app does not automatically pair nearby devices.
2. Refresh the device list. A missing row and a listed device with unknown battery are different problems; delayed Windows enumeration results continue to join the inventory.
3. Check the transport, source and error in device details. A USB receiver needs USB/vendor-protocol investigation rather than assumptions about the PC's Bluetooth connection.
4. Use the test notification and check Windows notification / Do Not Disturb settings. Successful submission does not prove a banner was shown.
5. Report remaining problems using the [device compatibility form](https://github.com/jiangxin1245234785-stack/BluetoothCharge/issues/new?template=device-compatibility.yml) or [bug report form](https://github.com/jiangxin1245234785-stack/BluetoothCharge/issues/new?template=bug-report.yml), with the app version, model, connection method and reproduction steps.

## Local data and privacy

Data lives in `%LOCALAPPDATA%\BluetoothCharge`: `config.json` stores rules, `state.json` stores reminder cooldowns, and `app.log` is a rotating log. Settings can open this folder or export a diagnostic JSON file.

**Diagnostics may contain personal device names, Bluetooth/MAC addresses, ContainerIds, USB/HID instance paths and serial numbers.** Before posting publicly, redact a sharing copy, including Windows usernames and local paths. Use consistent anonymous replacements and include only what is needed. Do not upload the entire data folder or change the live configuration merely to sanitize a report.

For a single real-device diagnostic scan, run from the program folder:

```powershell
.\BluetoothCharge.exe --probe
# Disable BLE fallback; supported Logitech HID++ devices are still queried:
.\BluetoothCharge.exe --probe --no-ble
```

The app writes `diagnostics.json` to its data folder and exits. These commands access actual devices and are not hardware-free tests.

## Build from source

Use Windows and a stable **.NET 10 SDK** allowed by [global.json](global.json), currently based on `10.0.401`. The first build needs network access to restore dependencies.

From PowerShell in the repository root:

```powershell
.\build.ps1 -Dotnet dotnet
```

The script runs automated checks, then publishes a self-contained build to `dist\win-x64`. Use `.\build.ps1 -Dotnet dotnet -FrameworkDependent` for a smaller runtime-dependent build. `-Runtime win-arm64` is a build target, but has not been validated on ARM64 hardware.

Run the hardware-free checks separately:

```powershell
dotnet run --project .\tests\BluetoothCharge.Tests\BluetoothCharge.Tests.csproj -c Release
```

Tests use a custom console runner, hence `dotnet run`. Windows CI checks the build and these tests; it does not certify real Bluetooth communication, notification banners, every Windows version or every vendor device.

To produce both distributable ZIPs, including documentation, licenses and SHA-256 checksums:

```powershell
.\scripts\package-release.ps1 -Dotnet dotnet
```

Output is written to `artifacts`. Use this packaging entry point for redistribution so required third-party notices stay with the binaries.

## Contributing and license

Issues and contributions in Chinese or English are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first. New vendor support needs an explicit model / VID:PID scope, protocol evidence, failure handling and hardware-free regression checks.

Project code is released under the [MIT license](LICENSE). Bundled runtimes, the notification library and icons retain their respective licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
