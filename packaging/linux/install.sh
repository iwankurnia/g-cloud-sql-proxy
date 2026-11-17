#!/bin/sh
# Puts Cloud SQL Proxy Manager in your user profile: no root, no packages.
#   ~/.local/bin/CloudSqlProxyManager            the program
#   ~/.local/share/applications/...desktop       the app-menu entry
#   ~/.local/share/icons/hicolor/256x256/apps/   the icon
# Run again to update; run with --uninstall to remove all three.
set -e
here=$(cd "$(dirname "$0")" && pwd)
bin="$HOME/.local/bin"
apps="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor/256x256/apps"

if [ "$1" = "--uninstall" ]; then
    rm -f "$bin/CloudSqlProxyManager" "$apps/cloudsql-proxy-manager.desktop" "$icons/cloudsql-proxy-manager.png"
    echo "Removed. Settings in ~/.config/CloudSqlProxyManager and the unpacked proxy in ~/.local/share/CloudSqlProxyManager were left alone."
    exit 0
fi

mkdir -p "$bin" "$apps" "$icons"
install -m 755 "$here/CloudSqlProxyManager" "$bin/CloudSqlProxyManager"
install -m 644 "$here/cloudsql-proxy-manager.png" "$icons/cloudsql-proxy-manager.png"
sed "s|^Exec=.*|Exec=$bin/CloudSqlProxyManager|" "$here/cloudsql-proxy-manager.desktop" > "$apps/cloudsql-proxy-manager.desktop"
chmod 644 "$apps/cloudsql-proxy-manager.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$HOME/.local/share/icons/hicolor" 2>/dev/null || true

echo "Installed. Find 'Cloud SQL Proxy Manager' in your app menu, or run: $bin/CloudSqlProxyManager"
case ":$PATH:" in *":$bin:"*) ;; *) echo "Note: $bin is not on your PATH; the app menu entry works regardless." ;; esac
