#!/bin/bash
# Builds ed-pdf in release mode and installs it in /Applications (replacing a previous install).
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build-app.sh release
DEST="/Applications/ed-pdf.app"
if [ -d "$DEST" ]; then
    osascript -e 'quit app "ed-pdf"' 2>/dev/null || true
    rm -rf "$DEST"
fi
# The app was called pdf-svd before.
if [ -d /Applications/pdf-svd.app ]; then
    osascript -e 'quit app "pdf-svd"' 2>/dev/null || true
    rm -rf /Applications/pdf-svd.app
fi
cp -R build/ed-pdf.app "$DEST"
# Make Finder's "Open with" menu aware of the new app right away.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
echo "Installé dans $DEST"
