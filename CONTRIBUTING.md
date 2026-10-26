# Contributing / 参与贡献

欢迎使用中文或英文提交问题、兼容性记录和改进。Please keep reports specific, reproducible and free of personal device identifiers.

## Report a problem

- Use the device compatibility issue form for missing devices, unknown/stale battery, connection classification, or a successful model-specific result.
- Use the bug report form for application behavior such as settings, tray display, startup, notifications or crashes.
- Include the app/package version, Windows version/build, model, connection method, expected behavior and actual behavior. Distinguish a missing inventory row from an existing row with unknown battery.
- For notification problems, distinguish app submission from a visible banner and mention Windows notification / Do Not Disturb settings.

Do not infer support from a matching brand name. A Bluetooth device, a USB receiver and a wired USB version of a similarly named product can require different implementations.

## Protect diagnostic data

Export only the information necessary to reproduce the problem. Diagnostics and logs may include device names, Bluetooth/MAC addresses, ContainerIds, complete USB/HID instance paths, serial numbers, Windows usernames and local paths.

Make a **sharing copy**, then remove or consistently replace personal identifiers. Keep JSON valid and preserve relationships between anonymous device aliases where needed. Include a USB VID:PID such as `046D:C54D` when useful, but do not paste an entire instance path containing a serial number. Inspect raw HID payloads before sharing them as well. Do not upload your complete application data folder or alter your active `config.json` / `state.json` for redaction.

The application does not upload data. Posting an issue or attaching diagnostics is a separate, public action.

## Set up and verify

Use Windows 10 build 19041+ or Windows 11 and a stable .NET 10 SDK matching [global.json](global.json). No Python or Bluetooth/HID hardware is needed for the automated checks. Dependency restore requires network access.

From PowerShell in the repository root:

```powershell
dotnet run --project .\tests\BluetoothCharge.Tests\BluetoothCharge.Tests.csproj -c Release
.\build.ps1 -Dotnet dotnet
```

The tests are a custom console runner, not a `dotnet test` project. The build script also runs them before publishing the default self-contained package to `dist\win-x64`. Use `-FrameworkDependent` for the runtime-dependent build.

Keep ordinary CI hardware-free. Do not make tests enumerate the contributor's Bluetooth devices, open real HID interfaces, show a notification, enable startup or change normal application data. Existing tests use controlled fixtures and isolated data. Test counts may change; describe what a check establishes rather than treating a fixed count as a compatibility guarantee.

Manual testing with `--probe`, `--probe --no-ble` or `--smoke-test` accesses real devices. Run it separately and use `BLUETOOTHCHARGE_DATA_DIR` to select an isolated data directory where appropriate. `--no-ble` disables BLE fallback, not the supported Logitech HID++ adapter. Record model/firmware, transport, app version, Windows build, driver context and the observed outcome. Do not claim that a successful scan certifies other models or that a submitted notification proves a visible banner.

## Change collection and vendor support carefully

- Retain the distinction between inventory presence, an attached USB receiver, and a confirmed wireless connection. PnP presence is not proof of a live Bluetooth link.
- Keep unknown or invalid battery values unknown. Do not replace them with zero or estimate percentages from voltage/coarse battery bands without a separate, justified product requirement.
- Scope new vendor adapters to explicit devices/interfaces. Provide primary protocol references and anonymized request/reply fixtures. The current Logitech adapter is limited to receiver `046D:C54D`, wireless slot 1.
- Battery queries must not alter pairing, button mappings, DPI, firmware or other device configuration. Sending a HID report for a logical read query is different from modifying device settings; document the actual command.
- Bound operation time and handle cancellation, partial replies, protocol errors, disconnects and resource disposal. Do not start duplicate late operations merely because the caller stopped waiting.
- Preserve device identity using container/address/instance relationships, not display names. Avoid unrelated local-computer batteries and duplicate component rows.
- Add regression checks for behavior that could fail: identity collisions, invalid data, offline suppression, stale results, or parsing boundaries. A mock success is not a hardware compatibility result.

Check licensing before adding code, protocol material or assets. Preserve existing notices and add any new required attribution to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Submit a pull request

Keep the change focused. Explain the concrete problem and resulting behavior, list the verification commands you ran, and state any hardware or platform testing you did not perform. Add screenshots for visible UI changes, using synthetic or anonymized device data. Update compatibility documentation when support scope changes.

Do not commit generated `bin`, `obj`, `dist`, release archives, personal logs, diagnostics, local SDKs or machine-specific settings. Project contributions are distributed under the [MIT license](LICENSE), with applicable third-party licenses retained.
