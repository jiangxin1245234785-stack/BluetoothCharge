# Validation and limitations

The first public release is v0.1.3. The application is written in C# with .NET 10, WinForms, WinRT and Win32 device APIs. Windows x64 is the tested distribution target.

## Automated checks

Run on Windows:

```powershell
dotnet run --project tests/BluetoothCharge.Tests -c Release
```

The v0.1.3 suite contains 176 checks: 20 reminder/storage checks, 50 device inventory/metadata checks, 36 settings layout checks, 42 HID++ protocol checks and 28 tray summary/card checks. They do not require Bluetooth hardware and do not submit real battery notifications.

Checks cover unknown versus true zero battery, disconnected and uncertain connections, threshold equality, ignored devices, persisted cooldowns, damaged settings recovery, stable device identity, delayed inventory updates, HID++ response matching, text length boundaries, and scrolling through a synthetic 100-device hover card. Layout tests exercise several window sizes and font sizes; they do not replace testing every display configuration.

## Hardware observations

- Windows property reads have been observed on a Bluetooth keyboard, a game controller and a phone. The phone result is a Windows-reported value; it does not establish universal phone support or real-time accuracy.
- A Logitech MR0097 / PRO X SUPERLIGHT 2 using receiver VID `046D`, PID `C54D`, slot 1 has returned device-reported percentages through HID++ feature `0x1004`. The adapter resolves feature indexes dynamically and also implements the percentage-capable `0x1000` path.
- Both portable distributions have started, scanned, loaded their embedded icons, rendered the settings window and exited successfully using isolated data directories on Windows 11 x64.
- Toast submission has been exercised. Submission success is not proof that Windows displayed a banner or played a sound; use the app's explicitly labelled test notification to check your own settings.

These are limited observations, not a promise that all devices of these categories work. Firmware, drivers, adapter and Windows version affect results. No identifying device captures or personal configuration are included in this repository.

## Unverified or incomplete coverage

- A clean Windows 10 or Windows 11 virtual machine with no development tools.
- ARM64 binaries and all supported Windows builds.
- Standard BLE Battery Service fallback end to end when no system battery property exists.
- Other Logitech receiver PIDs, multiple receiver slots, other proprietary wireless protocols, and all G HUB versions.
- Every sleep/resume, unplug/replug, pairing and multi-display DPI sequence.
- Actual mouse hover, scrollbar dragging and inactive-window wheel delivery across all desktop configurations. Overflow cards include a clickable scrollbar so access does not depend solely on wheel routing.

Transient HID no-response conditions have occurred; a later query succeeded. Such failures leave battery/connection unknown and must not trigger a low-battery alert. Their cause has not been fully established.

## Optional local diagnostic run

The normal application reads and stores data locally. To keep a diagnostic run separate from everyday settings:

```powershell
$env:BLUETOOTHCHARGE_DATA_DIR = Join-Path $env:TEMP ('BluetoothCharge-verification-' + [guid]::NewGuid())
.\BluetoothCharge.exe --smoke-test
```

This scans actual devices, writes `smoke-test.json` and a settings image, and exits without applying real battery notification rules. It may still request BLE access and contains private device names and identifiers. These files are ignored by Git, but must also be reviewed before being attached to an issue. Adding `--test-toast` deliberately submits a labelled test notification.

See [compatibility and API details](COMPATIBILITY.md) before interpreting missing or cached battery values.
