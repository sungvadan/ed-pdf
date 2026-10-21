#!/bin/bash
# Builds pdf-svd in release mode and packages it as a drag-and-drop disk image: build/pdf-svd-<version>.dmg
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build-app.sh release
VERSION=$(defaults read "$PWD/build/pdf-svd.app/Contents/Info.plist" CFBundleShortVersionString)
DMG="build/pdf-svd-$VERSION.dmg"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

cp -R build/pdf-svd.app "$STAGING/"
ln -s /Applications "$STAGING/Applications"
cat > "$STAGING/Lisez-moi.txt" <<'TXT'
Installation de pdf-svd
=======================

1. Glissez « pdf-svd » sur le dossier « Applications ».
2. Éjectez cette image disque.

Première ouverture
------------------
pdf-svd n'est pas signé par un développeur enregistré chez Apple, donc macOS le bloque la
première fois (« Apple n'a pas pu vérifier… »). Pour l'autoriser :

  - Ouvrez pdf-svd une fois (il sera refusé), puis allez dans
    Réglages Système › Confidentialité et sécurité, descendez jusqu'au message sur pdf-svd
    et cliquez sur « Ouvrir quand même ». Confirmez avec votre mot de passe.

C'est à faire une seule fois. Ensuite, pdf-svd s'ouvre normalement.

Configuration requise : Mac avec puce Apple (M1 ou plus récent), macOS 14 ou plus récent.
TXT

rm -f "$DMG"
hdiutil create -volname "pdf-svd" -srcfolder "$STAGING" -fs HFS+ -format UDZO -imagekey zlib-level=9 "$DMG" >/dev/null
echo "Créé : $DMG ($(du -h "$DMG" | cut -f1))"
