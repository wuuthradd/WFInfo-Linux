# WFInfo for Linux

Linux port of [WFInfo](https://github.com/WFCD/WFInfo), the Warframe companion app that uses OCR to read your reward screens and show real time platinum/ducat prices.

The original is a Windows only WPF/.NET Framework 4.8 app. This port replaces WPF with Avalonia and adds Linux specific backends for screenshots, input listening, process detection and overlay rendering while keeping the same core OCR and market data logic.

## How it works on Linux

- **Process detection**: Scans `/proc` for `Warframe.x64.exe` running under Proton.
- **Log capture**: A tiny Win32 exe (DBMON bridge) runs under Proton's Wine to capture `OutputDebugString` in real time, same mechanism as the Windows version.
- **Screenshots + Overlay**: A thin Vulkan layer hooks the game's swapchain to capture frames. Overlay panels are drawn by a separate plugin (Cairo/Pango) and composited onto the rendered image before present. Works in fullscreen, borderless and windowed modes on any display server and any compositor without needing specific protocols.
- **OCR**: System-installed Tesseract via the managed NuGet wrapper.
- **Input listening**: Reads keyboard and mouse events directly from `/dev/input/event*` via evdev. Requires a one-time setup to grant read access (see [Setup](#setup)). Also listens on a Unix domain socket (`$XDG_RUNTIME_DIR/wfinfo.sock`) for commands, allowing desktop environment shortcuts to trigger actions without evdev access.

## Installation

### Option 1: AppImage

Download `WFInfo.AppImage`, make it executable, and run:

```bash
chmod +x WFInfo.AppImage
./WFInfo.AppImage
```

Requires FUSE (`libfuse2` or `libfuse3`) and `tesseract` (see [Dependencies](#dependencies) below).

### Option 2: Tarball

Extract and run. Requires `tesseract` (see [Dependencies](#dependencies) below).

```bash
tar xzf WFInfo-linux-x64.tar.gz
cd WFInfo-linux-x64
./WFInfo
```

## Dependencies

WFInfo.Linux needs Tesseract OCR and a few graphics libraries at runtime. Most are already installed on any desktop Linux system, you likely only need to install Tesseract.

**Required:**

| Library | What for |
|---|---|
| `tesseract` + `leptonica` | OCR engine |
| `cairo`, `pango`, `fontconfig` | Overlay panel rendering (used by the overlay plugin, not the layer stub) |
| Vulkan loader (`libvulkan`) | Already present if you're running Proton games |

**Install commands** :

| Distro | Command |
|---|---|
| Arch | `sudo pacman -S tesseract` |
| Ubuntu / Debian / Mint | `sudo apt install tesseract-ocr` |
| Fedora | `sudo dnf install tesseract leptonica` |

## Setup

### Steam launch options (required)

WFInfo uses a Vulkan layer to capture screenshots and render overlays directly in the game's swapchain. You need to enable it by adding an environment variable to your Steam launch options.

Right-click Warframe in Steam → Properties → General → Launch Options and add:

```
WFINFO=1 %command%
```

Without this, WFInfo cannot capture the screen or show overlays. If you already have launch options, just add this before `%command%`. If you are using Lutris or other platforms, add this as environment variable or game argument without `%command%`.

### Global hotkeys (required for manual activation)

If you are already in the `input` group or similar, this step is not needed. 

WFInfo needs read access to input devices for global hotkeys. Run the included setup script once:

```bash
# AppImage:
sudo ./WFInfo.AppImage --setup-input

# Tarball:
sudo ./WFInfo --setup-input
```

This creates a `wfinfo` system group with read-only access to keyboard and mouse devices via a udev rule. Log out and back in after running it (reboot instead if your user has `loginctl linger` enabled).

If you skip this step, WFInfo still works with auto mode (triggered by game log) but manual activation keys won't respond. You can still use all features via desktop environment shortcuts (see below). The AFK idle timer for warframe.market status is also disabled without input access, your status will only change to invisible when Warframe closes, not after being idle.

### To undo

Run the setup script again, it detects the existing setup and removes it:

```bash
# AppImage:
sudo ./WFInfo.AppImage --setup-input

# Tarball:
sudo ./WFInfo --setup-input
```

Log out and back in after removal (reboot instead if you have `loginctl linger` enabled).

### Alternative: Desktop environment shortcuts (no evdev required)

WFInfo listens on a Unix socket for commands. You can trigger all actions by binding shortcuts in your desktop environment:

```bash
# Example using socat:
echo activate | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/wfinfo.sock

# Available commands: activate, snapit, searchit, masterit
```

**KDE Plasma:** System Settings → Shortcuts → Custom Shortcuts → Add new → Command/URL. Set your key combo and the socat command above.

**GNOME:** Settings → Keyboard → Custom Shortcuts. Add a shortcut with the socat command.

**Sway/i3:** Add `bindsym $mod+F8 exec echo activate | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/wfinfo.sock` to your config.

The socket path is also shown in Settings when evdev is not available. You can go into settings and choose a command to copy for shortcuts.

## Updates

WFInfo checks for updates on startup by querying GitHub Releases. If a newer version is available, a dialogue shows the release notes with two options:

- **Update** - downloads and installs the update automatically then relaunches. If auto update fails, the error is shown and you can retry or close and download manually from the [releases page](https://github.com/wuuthradd/WFInfo-Linux/releases).
- **Skip** - suppresses the prompt until an even newer version is released

Closing the window dismisses for this session (will prompt again next launch).

## Known limitations

### Reward window mode

When using the reward window display mode, the window will appear behind a fullscreen game because of Linux limitations. To fix this, set the window to stay above others using your compositor's window rules. On KDE for instance, use "Detect Window Properties" in window rules at system settings to get the exact window class(full) and window title for matching. If you leave the window title empty the rule will apply to every WFInfo window. Then add a property as layer, choose popup or higher so it can show above. You can do this for any window you want, instead of exact match you can choose regular expressions to give a value as (Auto Add|Relics) so you can add multiple windows to show above always.

You can't interact with the reward window while the game stays focused.

## Reporting Bugs & Feature Requests

If you run into a problem, [open an issue](../../issues/new/choose) using the bug report template. For feature requests, [open a feature request](../../issues/new/choose). Only Linux-specific requests belong here. General WFInfo feature requests should go to the upstream [WFCD/WFInfo](https://github.com/WFCD/WFInfo) repo, this is a port and will inherit upstream features sooner or later.

Create a debug zip from WFInfo Settings → "Create debug zip" and attach it to your issue or share a link. GitHub has a 25 MB attachment limit, if the zip is too large remove the large `FullScreenShot` PNGs from it or upload to a file sharing service and paste the link.

## Building from source

Requires .NET 10+ SDK, a C++ compiler (g++, clang++, zig c++, etc.) and native development libraries.

```bash
# Build the .NET app (all projects)
dotnet build

# Or build a specific project
dotnet build WFInfo.Linux/WFInfo.Linux.csproj

# Build the Vulkan layer (libwfinfo_vk.so + libwfinfo_overlay.so)
make -C WFInfo.Linux/NativeOverlay

# Build the DBMON bridge (Win32 exe for game log capture via Wine)
make -C WFInfo.Linux/DBMon

# Build the AppImage (includes all of the above)
./build-appimage.sh
```

### Running

```bash
dotnet run --project WFInfo.Linux
```

### Build dependencies

- **.NET app**: .NET 10+ SDK. At runtime, requires `tesseract` and `leptonica` (see [Dependencies](#dependencies)).
- **Vulkan layer**: `pkg-config`, Vulkan headers (`vulkan-headers`), `glslangValidator` (shader compilation, from `glslang`), `xxd` (from `vim` or `xxd`). Overlay plugin also needs development packages for `cairo`, `pangocairo`, `fontconfig`.
- **DBMON bridge**: A C cross-compiler targeting Windows. The Makefile uses `zig cc -target x86_64-windows-gnu` by default. Alternatively, `x86_64-w64-mingw32-gcc` works.
- **AppImage**: `appimagetool` (auto-downloaded if not found).
