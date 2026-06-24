#!/bin/bash
# Build WFInfo as a self-contained Linux AppImage
# Usage: ./build-appimage.sh
# Requirements: dotnet 10+ SDK, appimagetool (or linuxdeploy)
# Runtime deps: tesseract (+ leptonica), downloaded automatically as tessdata on first run

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="${SCRIPT_DIR}/WFInfo.Linux"
BUILD_DIR="${SCRIPT_DIR}/build-appimage"
APPDIR="${BUILD_DIR}/AppDir"

echo "=== WFInfo AppImage Builder ==="
echo ""

# Clean previous build
rm -rf "${BUILD_DIR}"
mkdir -p "${APPDIR}/usr/bin"

# 1. Publish self-contained single-file for Linux x64
echo "[1/5] Publishing .NET application..."
dotnet publish "${PROJECT_DIR}/WFInfo.Linux.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishTrimmed=true \
    -p:TrimMode=partial \
    -o "${APPDIR}/usr/bin/"

# 2. Build DBMON bridge (native Win32 exe for OutputDebugString capture via Wine)
echo "[2/5] Building DBMON bridge..."
if [ -f "${PROJECT_DIR}/DBMon/dbmon.c" ]; then
    make -C "${PROJECT_DIR}/DBMon" clean
    make -C "${PROJECT_DIR}/DBMon"
    cp "${PROJECT_DIR}/DBMon/WFInfo.DbMon.exe" "${APPDIR}/usr/bin/"
    echo "  Bundled WFInfo.DbMon.exe (native C, ~15KB)"
else
    echo "  Warning: WFInfo.DbMon/dbmon.c not found - DBMON bridge will not be available"
fi

# 3. Build Vulkan layer (implicit layer for screenshot capture + overlay compositing)
echo "[3/5] Building Vulkan layer..."
if pkg-config --exists cairo pangocairo fontconfig 2>/dev/null; then
    make -C "${PROJECT_DIR}/NativeOverlay" clean
    make -C "${PROJECT_DIR}/NativeOverlay"
    install -m 755 "${PROJECT_DIR}/NativeOverlay/libwfinfo_vk.so" "${APPDIR}/usr/bin/"
    cp "${PROJECT_DIR}/NativeOverlay/wfinfo_vk.json" "${APPDIR}/usr/bin/"
    echo "  Built and bundled libwfinfo_vk.so + wfinfo_vk.json"
else
    echo "  Warning: cairo/pangocairo/fontconfig not found, Vulkan layer won't be built"
fi

# 4. Set up AppImage metadata
echo "[4/5] Setting up AppImage metadata..."
cp "${PROJECT_DIR}/AppImage/AppRun" "${APPDIR}/AppRun"
chmod +x "${APPDIR}/AppRun"
cp "${PROJECT_DIR}/AppImage/wfinfo-setup-input.sh" "${APPDIR}/usr/bin/wfinfo-setup-input.sh"
chmod +x "${APPDIR}/usr/bin/wfinfo-setup-input.sh"
cp "${PROJECT_DIR}/AppImage/WFInfo.desktop" "${APPDIR}/WFInfo.desktop"

# Copy icon (convert if needed, AppImage needs PNG)
if [ -f "${PROJECT_DIR}/Resources/WFLogo.png" ]; then
    cp "${PROJECT_DIR}/Resources/WFLogo.png" "${APPDIR}/WFInfo.png"
    # Also place in standard icon path
    mkdir -p "${APPDIR}/usr/share/icons/hicolor/256x256/apps"
    cp "${PROJECT_DIR}/Resources/WFLogo.png" "${APPDIR}/usr/share/icons/hicolor/256x256/apps/WFInfo.png"
fi

# 5. Create AppImage
echo "[5/5] Creating AppImage..."
APPIMAGETOOL=$(command -v appimagetool 2>/dev/null || true)

if [ -z "$APPIMAGETOOL" ]; then
    echo "  appimagetool not found. Downloading..."
    APPIMAGETOOL="${BUILD_DIR}/appimagetool"
    wget -q "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" \
        -O "$APPIMAGETOOL"
    chmod +x "$APPIMAGETOOL"
fi

ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "${BUILD_DIR}/WFInfo.AppImage"

echo ""
echo "=== Build complete ==="
echo "Output: ${BUILD_DIR}/WFInfo.AppImage"
echo ""
echo "To run:"
echo "  chmod +x ${BUILD_DIR}/WFInfo.AppImage"
echo "  ./${BUILD_DIR}/WFInfo.AppImage"