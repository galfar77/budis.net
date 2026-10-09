#!/bin/bash
# Sestaví Release a složí z něj BudisCommander.app do složky build/.
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release
BIN="$(swift build -c release --show-bin-path)/BudisCommander"
APP="build/BudisCommander.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/BudisCommander"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Budis Commander</string>
  <key>CFBundleIdentifier</key><string>net.budis.commander</string>
  <key>CFBundleExecutable</key><string>BudisCommander</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>Budis Commander hledá počítače a sdílené disky v místní síti.</string>
  <key>NSBonjourServices</key><array><string>_smb._tcp</string></array>
</dict></plist>
PLIST

codesign --force --sign - "$APP" 2>/dev/null || true
echo "Hotovo: $APP"
