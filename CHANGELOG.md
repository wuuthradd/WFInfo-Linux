# Changelog

## 9.8.1.2

- Use dedicated X11 display connection for screenshot captures, fixing XShm crashes and XGetWindowAttributes failures caused by race issues on the shared connection
- Re-enable XShm on XWayland for faster screenshot captures (previously was disabled because causing crashes which "fixed" now)
- Fix game window not being re-discovered after Warframe restarts (XID retry)
- Fix screenshot capture failing on compositors where XGetWindowAttributes is unavailable (XWayland fallback using screen bounds)
- Fix reward detection and theme detection failing on ultrawide monitors due to incorrect screen scaling calculation

## 9.8.1.1

- Fix verify count window not stealing focus on subsequent scans, fix layout to match WPF, remember window position within session
- Added self update for both AppImage and tarball installations, fixed previous layout issues on update dialog window, added check for updates button in settings.
- Layout correction on main window and some small fixes.

## 9.8.1 - Initial Release

Linux port of [WFInfo](https://github.com/WFCD/WFInfo). Full port from WPF/.NET Framework 4.8 to Avalonia/.NET 10 with Linux-native backends.
