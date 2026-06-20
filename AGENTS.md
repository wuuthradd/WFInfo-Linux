# WFInfo Linux — Agent Guide

Linux port of WFInfo. Avalonia/.NET 10 desktop app for Warframe. OCR + market prices.

## Build & Run
- `dotnet build` (builds entire solution via `WFInfo.Linux.slnx`)
- `dotnet build WFInfo.Linux` (just the Linux app)
- `dotnet run --project WFInfo.Linux` for normal UI mode
- Entry point: `WFInfo.Linux/Program.cs` → `App.axaml.cs`
- Vulkan layer (C++): `make -C WFInfo.Linux/NativeOverlay`
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
- **Auto-mode** → `WFInfo.Linux/Services/FileLogCapture.cs`: launches DBMON bridge under Wine to capture `OutputDebugString` messages, triggers on `"Got rewards"`
- **Screenshots + Overlay** → Vulkan implicit layer (`WFInfo.Linux/NativeOverlay/libwfinfo_vk.so`), hooks swapchain for capture and composites overlays via graphics pipeline. Cairo/Pango for panel rendering. Communicates with .NET app via Unix socket (`VulkanLayerService.cs`)
- **Input** → evdev `/dev/input/event*` or Unix socket IPC (`LinuxInputListener.cs`, `SocketCommandServer.cs`)
- **Languages** → `WFInfo.Core/LanguageProcessing/`: 15 processors (CJK, Cyrillic, Latin, Thai, Turkish, Polish, European)

## Project Structure
- `WFInfo.Core/` — Cross-platform core: OCR, data, settings, models, language processing
- `WFInfo.Linux/` — Avalonia frontend + Linux-specific services
- `WFInfo.Linux/NativeOverlay/` — Vulkan implicit layer (libwfinfo_vk.so), screenshot capture + overlay compositing
- `WFInfo.Linux/DBMon/` — Win32 DBMON bridge (runs under Wine for game log capture)
- `tests/` — OCR regression test runner

## Key Tech Stack
- .NET 10, Avalonia 11.x, SkiaSharp 2.88.x
- Tesseract 5.2.0 (system `libtesseract` + `libleptonica` on Linux)
- Newtonsoft.Json, Microsoft.AspNetCore.DataProtection
- Cairo, Pango, Vulkan (implicit layer for overlay + capture)

## Quirks & Gotchas
- `AllowUnsafeBlocks=true` — Tesseract interop
- Tessdata (`.traineddata` files) downloaded from GitHub on first run to `~/.local/share/WFInfo/tesseract5/`
- Debug logs at `~/.local/share/WFInfo/debug.log` (async queue, flushed every 250ms)
- Vulkan layer must be built separately (`make -C WFInfo.Linux/NativeOverlay`)
- Vulkan layer activates when `WFINFO=1` env var is set (Steam launch options: `WFINFO=1 %command%`)
- Vulkan layer works on any display server (X11, XWayland, native Wayland) via swapchain hooks
- Vulkan layer logs at `~/.local/share/WFInfo/vklayer.log` (auto-rotated after 12 hours)
- CI workflow at `.github/workflows/release.yml` builds AppImage on tag push
