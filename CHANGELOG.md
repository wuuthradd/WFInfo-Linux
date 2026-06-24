# Changelog

## 9.8.1.4

- Fix Tesseract failing to load on Fedora 39+ and other modern glibc systems (libdl.so merged into libc)
- Fix Tesseract and Leptonica library discovery on distros with non-standard soname versions
- AppImage now supports both FUSE2 and FUSE3
- Fix Vulkan layer log missing for some game sessions

## 9.8.1.3

### Vulkan layer migration
- Replaced X11/Wayland overlay and screenshot systems with a Vulkan layer that hooks the game's swapchain for screenshot capture and overlay compositing. You have to pass WFINFO=1 env to whatever launcher you are using, add WFINFO=1 %command% to launch options for steam.
- Overlays now work in fullscreen, borderless and windowed modes on any display server and any compositor
- HDR support: Warframe in game HDR is now supported. Recommended to use custom themes if you want best results. In game paperwhite setting can affect filtering so your filter wont work for different margin values, same as WPF version.
- Handles DXVK multi-device init (extra devices get passthrough dispatch)

### UI and window changes
- Update dialogue: proper markdown rendering for release notes
- Various layout fixes across windows (Equipment, Relics, SearchIt, PlusOne, Login, Settings, Welcome, ListingHelper)
- Theme adjuster and settings window bug fixes

### Other
- Removed EE.log tailing fallback, DBMON bridge is now the only log capture method
- Cursor tracking now done via DBMON bridge
- Theme adjuster: added Import/Export JSON, also filter preset system to save and load them. Filters saved to $XDG_DATA_HOME/WFInfo/filters/ (default ~/.local/share/WFInfo/filters/)
- Migrated to C++ for better Vulkan layer implementation

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
