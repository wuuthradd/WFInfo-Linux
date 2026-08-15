# WFInfo Linux — Agent Guide

Linux port of WFInfo. Avalonia/.NET 10 desktop app for Warframe. OCR + market prices.

## Build & Run
- `dotnet build` (builds entire solution via `WFInfo.Linux.slnx`)
- `dotnet build WFInfo.Linux` (just the Linux app)
- `dotnet run --project WFInfo.Linux` for normal UI mode
- Entry point: `WFInfo.Linux/Program.cs` → `App.axaml.cs`
- Vulkan layer (C++): `make -C WFInfo.Linux/NativeOverlay` (builds `libwfinfo_vk.so` + `libwfinfo_overlay.so`)
- DBMON bridge: `make -C WFInfo.Linux/DBMon`

## Test Framework (headless OCR regression)
- `cd tests && ./run_tests.sh`
- Test data: `tests/data/<name>.json` + `<name>.png` pairs, listed in `tests/map.json`
- PNG files are not committed (too large) — use `FullScreenShot` PNGs from `~/.local/share/WFInfo/debug/`
- Exit codes: 0=all pass, 1=partial fail, 2=fatal error
- Real OCR pipeline (no mocks) — first run downloads market data from warframestat.us API

## Architecture
- **Entry** → `Program.Main()` → single-instance lock → signal handlers → Avalonia `App.OnFrameworkInitializationCompleted()`
- **Dependency Injection** via `Microsoft.Extensions.DependencyInjection` in `App.axaml.cs`
- **OCR** → `WFInfo.Core/OCR.cs`: screenshot → `ExtractPartBoxAutomatically` → Tesseract → Levenshtein `GetPartName()`
- **Data** → `WFInfo.Core/Data.cs`: JSON from `api.warframestat.us/wfinfo/prices`, JWT auth, WebSocket for warframe.market
- **Auto-mode** → `WFInfo.Linux/Services/FileLogCapture.cs`: launches DBMON bridge under Wine to capture `OutputDebugString` messages, triggers on `"Got rewards"`, detects whispers and completed trades. DBMON is the only log source (no EE.log tailing).
- **Trading** → `MyListingsWindow`, `PlaceOrderWindow`, `EditOrderWindow`, `TransactionHistoryWindow`, `TradeDoneWindow`: warframe.market order management, trade confirmation, transaction history
- **Notifications** → `DesktopNotificationService.cs`: whisper alerts via `notify-send`, `CrossPlatformSoundPlayer.cs`: selectable notification sounds
- **Screenshots + Overlay** → Vulkan implicit layer, two libraries. `libwfinfo_vk.so` is a thin stub (no cairo): swapchain hooks + screenshot capture. `libwfinfo_overlay.so` is dlopened only after Warframe is detected (Cairo/Pango panel + Snap-It). IPC with the .NET app is a Unix socket (`VulkanLayerService.cs`). App startup copies both `.so` files to `~/.local/share/WFInfo/` and writes `wfinfo_vk.json` to `~/.local/share/vulkan/implicit_layer.d/` (library_path points at the stub).
- **Input** → evdev `/dev/input/event*` or Unix socket IPC (`LinuxInputListener.cs`, `SocketCommandServer.cs`)
- **Languages** → `WFInfo.Core/LanguageProcessing/`: 15 processors (CJK, Cyrillic, Latin, Thai, Turkish, Polish, European)

## Project Structure
- `WFInfo.Core/` — Cross-platform core: OCR, data, settings, models, language processing
- `WFInfo.Linux/` — Avalonia frontend + Linux-specific services
- `WFInfo.Linux/NativeOverlay/` — Vulkan implicit layer: stub `libwfinfo_vk.so` + overlay plugin `libwfinfo_overlay.so`
- `WFInfo.Core/Services/IServices.cs` — platform seams (screenshot, window, process, log, input, sound, logger, tesseract); Linux impls live in `WFInfo.Linux/Services/`
- `WFInfo.Linux/DBMon/` — Win32 DBMON bridge (runs under Wine for game log capture)
- `tests/` — OCR regression test runner

## Key Tech Stack
- .NET 10, Avalonia 11.x, SkiaSharp 2.88.x
- Tesseract 5.2.0 (system `libtesseract` + `libleptonica` on Linux)
- Newtonsoft.Json, Microsoft.AspNetCore.DataProtection
- Cairo, Pango (overlay plugin only), Vulkan (implicit layer for overlay + capture)

## Quirks & Gotchas
- `AllowUnsafeBlocks=true` — Tesseract interop
- Tessdata (`.traineddata` files) downloaded on first run to `~/.local/share/WFInfo/tessdata/` (not `tesseract5/`)
- Debug logs at `~/.local/share/WFInfo/debug.log` (async queue, flushed every 250ms)
- Vulkan layer must be built separately (`make -C WFInfo.Linux/NativeOverlay`); produces two `.so` files that must ship together
- After updating the layer, restart Warframe so it remaps the stub (running game keeps the old inode)
- Vulkan layer activates when `WFINFO=1` is set (Steam: `WFINFO=1 %command%`). Manifest is a normal implicit layer under `~/.local/share/vulkan/implicit_layer.d/`
- Stub is mapped `RTLD_LOCAL` by the Vulkan loader; it promotes itself `RTLD_GLOBAL` then `dlopen`s `libwfinfo_overlay.so` from the same directory (or `~/.local/share/WFInfo/`)
- Vulkan layer works on any display server (X11, XWayland, native Wayland) via swapchain hooks
- Vulkan layer logs at `~/.local/share/WFInfo/vklayer.log` (auto-rotated after 12 hours)
- CI workflow at `.github/workflows/release.yml` builds AppImage + tarball on tag push; both packages must include `libwfinfo_overlay.so`
