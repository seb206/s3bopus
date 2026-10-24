#!/bin/bash
# Compile S3bopus pour Mac et produit build/S3bopus.app (signature ad hoc).
# Usage : bash mac/build-app.sh [--dmg]
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(grep -m1 'static let version' Sources/S3bopus/Core.swift | sed 's/.*"\(.*\)".*/\1/')

# Binaire universel : un build par architecture (sans Xcode complet), puis lipo
for ARCH in arm64 x86_64; do
    swift build -c release --triple "$ARCH-apple-macosx13.0"
done
mkdir -p build
BIN=build/S3bopus-universal
lipo -create -output "$BIN" \
    "$(swift build -c release --triple arm64-apple-macosx13.0 --show-bin-path)/S3bopus" \
    "$(swift build -c release --triple x86_64-apple-macosx13.0 --show-bin-path)/S3bopus"

APP=build/S3bopus.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/S3bopus"

# Icône : le PNG de la version Linux
ICON=../linux/s3bopus.png
if [ -f "$ICON" ]; then
    SET=$(mktemp -d)/S3bopus.iconset
    mkdir -p "$SET"
    for s in 16 32 128 256 512; do
        sips -z $s $s "$ICON" --out "$SET/icon_${s}x${s}.png" >/dev/null
        sips -z $((s*2)) $((s*2)) "$ICON" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$SET" -o "$APP/Contents/Resources/S3bopus.icns"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>S3bopus</string>
  <key>CFBundleDisplayName</key><string>S3bopus</string>
  <key>CFBundleIdentifier</key><string>com.s3btify.s3bopus</string>
  <key>CFBundleExecutable</key><string>S3bopus</string>
  <key>CFBundleIconFile</key><string>S3bopus</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSAppleEventsUsageDescription</key><string>S3bopus demande à Finder d'afficher la fenêtre d'informations d'un fichier.</string>
</dict></plist>
PLIST

codesign --force --sign - "$APP"
echo "OK : $APP ($(du -sh "$APP" | cut -f1))"

if [ "${1:-}" = "--dmg" ]; then
    DMG="build/S3bopus-$VERSION-mac.dmg"
    rm -f "$DMG"
    hdiutil create -volname S3bopus -srcfolder "$APP" -ov -format UDZO "$DMG" >/dev/null
    echo "OK : $DMG"
fi
