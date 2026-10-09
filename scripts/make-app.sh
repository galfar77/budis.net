#!/bin/bash
# Sestaví Release a složí z něj BudisCommander.app do složky build/.
set -euo pipefail
cd "$(dirname "$0")/.."

# Univerzální binárka (Apple Silicon + Intel)
swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/BudisCommander"
APP="build/BudisCommander.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/BudisCommander"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Budis Commander</string>
  <key>CFBundleIdentifier</key><string>net.budis.commander</string>
  <key>CFBundleExecutable</key><string>BudisCommander</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>Budis Commander hledá počítače a sdílené disky v místní síti.</string>
  <key>NSBonjourServices</key><array><string>_smb._tcp</string></array>
</dict></plist>
PLIST

# Podepsání: bez proměnných ad hoc (stačí pro běh na vlastním Macu).
# S placeným účtem Apple Developer:
#   SIGN_IDENTITY="Developer ID Application: Jméno (TEAMID)" ./scripts/make-app.sh
# a navíc notarizace (jednou: xcrun notarytool store-credentials budis-notary ...):
#   NOTARY_PROFILE=budis-notary SIGN_IDENTITY="..." ./scripts/make-app.sh
if [ -n "${SIGN_IDENTITY:-}" ]; then
  codesign --force --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP"
else
  codesign --force --sign - "$APP" 2>/dev/null || true
fi

# ZIP pro přenos / sdílení (ditto zachová atributy)
ditto -c -k --keepParent "$APP" build/BudisCommander.zip

if [ -n "${SIGN_IDENTITY:-}" ] && [ -n "${NOTARY_PROFILE:-}" ]; then
  echo "Notarizace (může trvat několik minut)…"
  xcrun notarytool submit build/BudisCommander.zip --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm -f build/BudisCommander.zip
  ditto -c -k --keepParent "$APP" build/BudisCommander.zip
fi

echo "Hotovo: $APP a build/BudisCommander.zip"
