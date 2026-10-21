#!/bin/bash
# Builds pdf-svd.app into ./build (executable and modules keep the internal name Folio). Usage: scripts/build-app.sh [debug|release] [--open]
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-release}"
scripts/fetch-pdfium.sh >/dev/null
swift build -c "$CONFIG" --product Folio
BIN="$(swift build -c "$CONFIG" --show-bin-path)/Folio"

APP="build/pdf-svd.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
cp "$BIN" "$APP/Contents/MacOS/Folio"
cp Resources/Info.plist "$APP/Contents/Info.plist"
[ -f Resources/AppIcon.icns ] && cp Resources/AppIcon.icns "$APP/Contents/Resources/"
cp Vendor/pdfium/lib/libpdfium.dylib "$APP/Contents/Frameworks/"
cp Vendor/pdfium/LICENSE "$APP/Contents/Resources/PDFium-LICENSE.txt"
codesign --force --sign - "$APP/Contents/Frameworks/libpdfium.dylib" >/dev/null
codesign --force --sign - "$APP" >/dev/null

echo "Built $APP"
[[ "${2:-}" == "--open" ]] && open "$APP"
exit 0
