#!/bin/bash
# Builds pdf-svd in release mode and installs it in /Applications (replacing a previous install).
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build-app.sh release
DEST="/Applications/pdf-svd.app"
if [ -d "$DEST" ]; then
    osascript -e 'quit app "pdf-svd"' 2>/dev/null || true
    rm -rf "$DEST"
fi
cp -R build/pdf-svd.app "$DEST"
# Make Finder's "Open with" menu aware of the new app right away.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
echo "Installé dans $DEST"
