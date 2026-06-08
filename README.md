# WFInfo for Linux

Linux port of [WFInfo](https://github.com/WFCD/WFInfo), the Warframe companion app that uses OCR to read your reward screens and show real time platinum/ducat prices.

The original is a Windows only WPF/.NET Framework 4.8 app. This port replaces WPF with Avalonia and adds Linux specific backends for screenshots, input listening, process detection and overlay rendering while keeping the same core OCR and market data logic.

## How it works on Linux

- **Process detection**: Scans `/proc` for `Warframe.x64.exe` running under Proton, finds the XWayland window ID via X11 tree search.
- **Log capture**: A tiny Win32 exe (DBMON bridge) runs under Proton's Wine to capture `OutputDebugString` in real time, same mechanism as the Windows version. Falls back to tailing `EE.log` from the Proton Wine prefix if DBMON is unavailable (slower, worst case for auto mode).
- **Screenshots**: X11/XWayland screen capture via XShm with XGetImage as fallback.
- **OCR**: System-installed Tesseract via the managed NuGet wrapper.
- **Overlay**: A native C/cairo helper process. On Wayland it uses `wlr-layer-shell` to render above fullscreen games with full transparency. On X11 it uses override-redirect windows with ARGB visuals (transparency is lost when a fullscreen game suspends compositing, see [Known limitations](#known-limitations)).
- **Input listening**: Reads keyboard and mouse events directly from `/dev/input/event*` via evdev. Requires a one-time setup to grant read access (see [Setup](#setup)). Also listens on a Unix domain socket (`$XDG_RUNTIME_DIR/wfinfo.sock`) for commands, allowing desktop environment shortcuts to trigger actions without evdev access.

## Installation

### Option 1: AppImage

Download `WFInfo.AppImage`, make it executable, and run:

```bash
chmod +x WFInfo.AppImage
./WFInfo.AppImage
```

Requires `libfuse2` and `tesseract` (see [Dependencies](#dependencies) below).

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
| `cairo`, `pango`, `fontconfig` | Overlay text rendering |
| `libX11`, `libXext`, `libXrender`, `libXfixes`, `libXi`, `libXrandr`, `libxcb` | X11/XWayland screenshot and overlay |
| `libwayland-client`, `libwayland-cursor` | Wayland overlay |

**Install commands** :

| Distro | Command |
|---|---|
| Arch / Manjaro | `sudo pacman -S tesseract` |
| Ubuntu / Debian / Mint | `sudo apt install libtesseract-dev libleptonica-dev` |
| Fedora | `sudo dnf install tesseract leptonica` |
| openSUSE | `sudo zypper install tesseract-ocr leptonica-devel` |

## Setup

### Global hotkeys (required for manual activation)

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

- **Download** - opens the releases page in your browser
- **Skip** - suppresses the prompt until an even newer version is released

Closing the window dismisses for this session (will prompt again next launch).

## Known limitations

### X11: Overlay transparency

On X11, overlay panels appear as opaque black rectangles when a fullscreen Proton game is running. This is a fundamental X11 limitation. All compositors (KWin, Picom, Mutter, xfwm4, Compiz) suspend or unredirect compositing for fullscreen games, so ARGB alpha blending is ignored by the X server. The overlays still show correct content, positioning and sizing, only the semi-transparent background becomes fully opaque.

On Wayland, overlays render with proper transparency at all times.

### GNOME Wayland overlay

The overlay uses the `wlr-layer-shell` Wayland protocol to render above fullscreen games. GNOME's Mutter compositor does not implement this protocol and has no alternative. This affects GNOME Wayland sessions, the overlay cannot appear above the game.

Workarounds for GNOME users:
- **Use an Xorg session** instead of Wayland, overlays work normally (with the transparency limitation above)
- **Run Warframe through Gamescope** - `gamescope -- %command%` as a Steam launch option. Gamescope is a wlroots-based nested compositor that supports `wlr-layer-shell`
- **Auto mode still works** - reward detection and clipboard/notification output function without the overlay

Every other major compositor supports `wlr-layer-shell`: KDE (KWin 6.6+), Sway, Hyprland, COSMIC, Cinnamon (Muffin 6.6+), Budgie (labwc), River, niri, Labwc, Wayfire.

### Pure Wayland (not yet supported)

Some Proton/Wine versions offer a native Wayland backend for games (e.g. `proton-ge` with the Wayland launch option). WFInfo.Linux does not support this yet. The code skeleton for pure Wayland screenshots and pointer tracking is in place, but the implementation is incomplete. Currently all screenshot and input paths require X11/XWayland.

Pure Wayland support is planned. For now, use XWayland (the default for Proton). If you're on a Wayland desktop, Proton already runs the game through XWayland automatically, no action needed. Only users who explicitly enable experimental native Wayland in Wine/Proton are affected.

## Reporting Bugs & Feature Requests

If you run into a problem, [open an issue](../../issues/new/choose) using the bug report template. For feature requests, [open a feature request](../../issues/new/choose). Only Linux-specific requests belong here. General WFInfo feature requests should go to the upstream [WFCD/WFInfo](https://github.com/WFCD/WFInfo) repo, this is a port and will inherit upstream features sooner or later.

Create a debug zip from WFInfo Settings → "Create debug zip" and attach it to your issue or share a link. GitHub has a 25 MB attachment limit, if the zip is too large remove the large `FullScreenShot` PNGs from it or upload to a file sharing service and paste the link.

## Building from source

Requires .NET 10+ SDK, a C compiler (gcc, clang, zig cc, etc.) and native development libraries.

```bash
# Build the .NET app (all projects)
dotnet build

# Or build a specific project
dotnet build WFInfo.Linux/WFInfo.Linux.csproj

# Build the native overlay
make -C WFInfo.Linux/NativeOverlay

# Build the DBMON bridge (Win32 exe for game log capture via Wine)
make -C WFInfo.Linux/DBMon

# Build the AppImage (includes all of the above)
./build-appimage.sh
```

### Running

```bash
dotnet run --project WFInfo.Linux/WFInfo.Linux.csproj
```

### Build dependencies

- **.NET app**: .NET 10+ SDK. At runtime, requires `tesseract` and `leptonica` (see [Dependencies](#dependencies)).
- **Native overlay**: `pkg-config` and development packages for `wayland-client`, `wayland-cursor`, `x11`, `x11-xcb`, `xcb`, `xfixes`, `xrender`, `xext`, `xi`, `xrandr`, `cairo`, `pangocairo`, `fontconfig`.
- **DBMON bridge**: A C cross-compiler targeting Windows. The Makefile uses `zig cc -target x86_64-windows-gnu` by default. Alternatively, `x86_64-w64-mingw32-gcc` works.
- **AppImage**: `appimagetool` (auto-downloaded if not found) and `libfuse2`.
