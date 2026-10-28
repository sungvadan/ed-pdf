#!/bin/bash
# Downloads the pinned PDFium builds for Windows (bblanchon/pdfium-binaries) into Vendor/pdfium-windows/<rid>
# and checks their SHA-256. Same version as scripts/fetch-pdfium.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

TAG="chromium/8086"
DEST="Vendor/pdfium-windows"
if [ -f "$DEST/.tag" ] && [ "$(cat "$DEST/.tag")" = "$TAG" ]; then
    echo "PDFium Windows $TAG déjà présent"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
rm -rf "$DEST"
for entry in \
    "win-arm64 1799b8034e6d64946fec0ae79f3edfc8ba58ccd80c70a46194803b1eba408344" \
    "win-x64 1fd8af952832dbb0eb16d9249f68fe09e5f5ebf7c3dd9f6066ea2720cc28487d"; do
    read -r RID SHA <<< "$entry"
    curl -fsSL "https://github.com/bblanchon/pdfium-binaries/releases/download/$TAG/pdfium-$RID.tgz" -o "$TMP/$RID.tgz"
    echo "$SHA  $TMP/$RID.tgz" | shasum -a 256 -c - >/dev/null || { echo "Empreinte SHA-256 incorrecte ($RID)" >&2; exit 1; }
    mkdir -p "$TMP/$RID" "$DEST/$RID"
    tar -xzf "$TMP/$RID.tgz" -C "$TMP/$RID"
    cp "$TMP/$RID/bin/pdfium.dll" "$DEST/$RID/"
    [ -d "$DEST/licenses" ] || { cp -R "$TMP/$RID/licenses" "$DEST/licenses"; cp "$TMP/$RID/LICENSE" "$DEST/licenses/pdfium-binaries.txt"; }
done
echo "$TAG" > "$DEST/.tag"
echo "PDFium Windows $TAG installé dans $DEST"
