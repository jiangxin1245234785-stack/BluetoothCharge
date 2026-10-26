# Changelog

## 0.1.3 — First public release

- Tray hover lists all confirmed connected wireless devices, including the supported Logitech USB wireless mouse. Unknown battery remains visible; long lists use a complete scrollable hover card.
- Embedded Lucide charging-battery icons for the tray, settings window and executable, with light/dark taskbar variants.
- Per-device battery thresholds, ignore rules and persistent reminder cooldowns; periodic scans, Windows notifications and optional login startup.
- Bluetooth inventory updates through DeviceWatcher, with full refresh and delayed-result handling; Windows battery properties and connected-device BLE Battery Service fallback.
- Read-only HID++ percentage queries for Logitech receiver `046D:C54D`, slot 1. Other receiver models remain unsupported by this adapter.
- Public documentation, compatibility issue forms, MIT project license and Windows build artifacts.
