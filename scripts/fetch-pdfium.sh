#!/bin/bash
# Downloads the pinned PDFium build (bblanchon/pdfium-binaries) into Vendor/pdfium and checks its SHA-256.
set -euo pipefail
cd "$(dirname "$0")/.."

TAG="chromium/8086"
case "$(uname -m)" in
    arm64) ASSET="pdfium-mac-arm64.tgz"; SHA="e98679e052c07edbb5a627980902abb823d4b3f35744d877bd21668bd9fc13ab" ;;
    *) echo "Architecture non prise en charge : $(uname -m)" >&2; exit 1 ;;
esac

DEST="Vendor/pdfium"
if [ -f "$DEST/.tag" ] && [ "$(cat "$DEST/.tag")" = "$TAG" ]; then
    echo "PDFium $TAG déjà présent"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "https://github.com/bblanchon/pdfium-binaries/releases/download/$TAG/$ASSET" -o "$TMP/$ASSET"
echo "$SHA  $TMP/$ASSET" | shasum -a 256 -c - >/dev/null || { echo "Empreinte SHA-256 incorrecte" >&2; exit 1; }

rm -rf "$DEST"
mkdir -p "$DEST"
tar -xzf "$TMP/$ASSET" -C "$DEST"
# Load from the app bundle's Frameworks folder.
install_name_tool -id "@rpath/libpdfium.dylib" "$DEST/lib/libpdfium.dylib"
echo "$TAG" > "$DEST/.tag"
echo "PDFium $TAG installé dans $DEST"
