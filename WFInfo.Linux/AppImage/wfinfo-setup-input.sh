#!/bin/bash
# WFInfo - setup/teardown for input device access.
# Run once to set up, run again to remove.
#
# Usage:  sudo ./WFInfo.AppImage --setup-input
#
# This is safer than adding your user to the 'input' group, which
# grants read-write access to ALL input devices.

set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    SELF="${APPIMAGE:-$0}"
    echo "This script must be run with sudo:"
    echo "  sudo ${SELF} --setup-input"
    exit 1
fi

REAL_USER="${SUDO_USER:-$USER}"
GROUP_NAME="wfinfo"
UDEV_RULE="/etc/udev/rules.d/99-wfinfo-input.rules"

# If user is in the group, remove everything (toggle behavior)
if id -nG "${REAL_USER}" 2>/dev/null | grep -qw "${GROUP_NAME}"; then
    echo "=== WFInfo Input Removal ==="
    echo ""

    rm -f "${UDEV_RULE}"
    echo "[+] Removed udev rule"

    gpasswd -d "${REAL_USER}" "${GROUP_NAME}" 2>/dev/null || true
    echo "[+] Removed user '${REAL_USER}' from group '${GROUP_NAME}'"

    groupdel "${GROUP_NAME}" 2>/dev/null || true
    echo "[+] Removed group '${GROUP_NAME}'"

    udevadm control --reload-rules
    echo "[+] Udev rules reloaded"

    echo ""
    echo "=== Removal complete ==="
    LINGER=$(loginctl show-user "${REAL_USER}" -p Linger --value 2>/dev/null) || true
    if [ "${LINGER}" = "yes" ]; then
        echo "Reboot for changes to take full effect (linger is enabled)."
    else
        echo "Log out and back in for changes to take full effect."
    fi
    exit 0
fi

echo "=== WFInfo Input Setup ==="
echo ""

# 1. Check setfacl first
if ! command -v setfacl >/dev/null 2>&1; then
    echo "[!] Error: 'setfacl' not found. Install the 'acl' package first."
    exit 1
fi
echo "[+] setfacl found"

# 2. Create group
if ! getent group "${GROUP_NAME}" >/dev/null 2>&1; then
    groupadd "${GROUP_NAME}"
    echo "[+] Created group '${GROUP_NAME}'"
fi

# 3. Add user to group
usermod -aG "${GROUP_NAME}" "${REAL_USER}"
echo "[+] Added user '${REAL_USER}' to group '${GROUP_NAME}'"

# 4. Install udev rule
cat > "${UDEV_RULE}" << 'EOF'
# WFInfo - grant keyboard+mouse read access to 'wfinfo' group via POSIX ACL.
SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_KEY}=="1", RUN+="/usr/bin/setfacl -m g:wfinfo:r /dev/input/$kernel"
SUBSYSTEM=="input", KERNEL=="event*", ENV{ID_INPUT_MOUSE}=="1", RUN+="/usr/bin/setfacl -m g:wfinfo:r /dev/input/$kernel"
EOF
echo "[+] Installed udev rule: ${UDEV_RULE}"

# 5. Reload udev and apply to existing devices
udevadm control --reload-rules
udevadm trigger --subsystem-match=input
echo "[+] Udev rules reloaded and applied"

echo ""
echo "=== Setup complete ==="
echo ""
LINGER=$(loginctl show-user "${REAL_USER}" -p Linger --value 2>/dev/null) || true
if [ "${LINGER}" = "yes" ]; then
    echo "Reboot for the group change to take effect (linger is enabled)."
else
    echo "Log out and back in for the group change to take effect."
fi
echo "Run this script again to remove the setup."
