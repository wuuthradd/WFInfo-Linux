# WFInfo Linux — Agent Guide

Linux port of WFInfo. Avalonia/.NET 10 desktop app for Warframe. OCR + market prices.

## Build & Run
- `dotnet build` (builds entire solution via `WFInfo.Linux.slnx`)
- `dotnet build WFInfo.Linux` (just the Linux app)
- `dotnet run --project WFInfo.Linux` for normal UI mode
- Entry point: `WFInfo.Linux/Program.cs` → `App.axaml.cs`
- Native overlay: `make -C WFInfo.Linux/NativeOverlay`
- DBMON bridge: `make -C WFInfo.Linux/DBMon`

## Test Framework (headless OCR regression)
- `cd tests && ./run_tests.sh`
- Test data: `tests/data/<name>.json` + `<name>.png` pairs, listed in `tests/map.json`
- PNG files are not committed (too large) — create manually from Warframe screenshots
- Exit codes: 0=all pass, 1=partial fail, 2=fatal error
- Real OCR pipeline (no mocks) — first run downloads market data from warframestat.us API

## Architecture
- **Entry** → `Program.Main()` → single-instance lock → signal handlers → Avalonia `App.OnFrameworkInitializationCompleted()`
- **Dependency Injection** via `Microsoft.Extensions.DependencyInjection` in `App.axaml.cs`
- **OCR** → `WFInfo.Core/OCR.cs`: screenshot → `ExtractPartBoxAutomatically` → Tesseract → Levenshtein `GetPartName()`
- **Data** → `WFInfo.Core/Data.cs`: JSON from `api.warframestat.us/wfinfo/prices`, JWT auth, WebSocket for warframe.market
- **Auto-mode** → `WFInfo.Linux/Services/FileLogCapture.cs`: tails `EE.log` from Wine prefix or via DBMON bridge, triggers on `"Got rewards"`
- **Screenshots** → X11/XWayland via XShm with XGetImage fallback (`LinuxScreenshotService.cs`)
- **Overlay** → Native C process (`WFInfo.Linux/NativeOverlay/`), communicates via stdin JSON, renders with Cairo/Pango. X11 backend (XRender) and Wayland backend (wlr-layer-shell)
- **Input** → evdev `/dev/input/event*` or Unix socket IPC (`LinuxInputListener.cs`, `SocketCommandServer.cs`)
- **Languages** → `WFInfo.Core/LanguageProcessing/`: 15 processors (CJK, Cyrillic, Latin, Thai, Turkish, Polish, European)

## Project Structure
- `WFInfo.Core/` — Cross-platform core: OCR, data, settings, models, language processing
- `WFInfo.Linux/` — Avalonia frontend + Linux-specific services
- `WFInfo.Linux/NativeOverlay/` — C overlay process (X11 + Wayland backends)
- `WFInfo.Linux/DBMon/` — Win32 DBMON bridge (runs under Wine for game log capture)
- `tests/` — OCR regression test runner

## Key Tech Stack
- .NET 10, Avalonia 11.x, SkiaSharp 2.88.x
- Tesseract 5.2.0 (system `libtesseract` + `libleptonica` on Linux)
- Newtonsoft.Json, Microsoft.AspNetCore.DataProtection
- Cairo, Pango, X11/XCB, Wayland (native overlay)

## Quirks & Gotchas
- `AllowUnsafeBlocks=true` — Tesseract and X11 interop
- Tessdata (`.traineddata` files) downloaded from GitHub on first run to `~/.local/share/WFInfo/tesseract5/`
- Debug logs at `~/.local/share/WFInfo/debug.log` (async queue, flushed every 250ms)
- Native overlay binary must be built separately (`make -C WFInfo.Linux/NativeOverlay`)
- Only X11 and XWayland supported — no pure Wayland
- CI workflow at `.github/workflows/release.yml` builds AppImage on tag push
