#!/bin/bash
# Builds ed-pdf in release mode and packages it as a drag-and-drop disk image: build/ed-pdf-<version>.dmg
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build-app.sh release
VERSION=$(defaults read "$PWD/build/ed-pdf.app/Contents/Info.plist" CFBundleShortVersionString)
DMG="build/ed-pdf-$VERSION.dmg"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

cp -R build/ed-pdf.app "$STAGING/"
ln -s /Applications "$STAGING/Applications"
cat > "$STAGING/Lisez-moi.txt" <<'TXT'
Installation d'ed-pdf
=======================

1. Glissez « ed-pdf » sur le dossier « Applications ».
2. Éjectez cette image disque.

Première ouverture
------------------
ed-pdf n'est pas signé par un développeur enregistré chez Apple, donc macOS le bloque la
première fois (« Apple n'a pas pu vérifier… »). Pour l'autoriser :

  - Ouvrez ed-pdf une fois (il sera refusé), puis allez dans
    Réglages Système › Confidentialité et sécurité, descendez jusqu'au message sur ed-pdf
    et cliquez sur « Ouvrir quand même ». Confirmez avec votre mot de passe.

C'est à faire une seule fois. Ensuite, ed-pdf s'ouvre normalement.

Configuration requise : Mac avec puce Apple (M1 ou plus récent), macOS 14 ou plus récent.

Licence
-------
ed-pdf est un logiciel libre sous licence MIT, fourni « tel quel », sans aucune garantie.
Gardez une copie de vos fichiers originaux. Vous êtes seul responsable des documents que vous
modifiez : ne modifiez que vos propres documents ou ceux que vous êtes autorisé à modifier.
Licences des composants tiers : ed-pdf.app/Contents/Resources/Licenses.
TXT

rm -f "$DMG"
hdiutil create -volname "ed-pdf" -srcfolder "$STAGING" -fs HFS+ -format UDZO -imagekey zlib-level=9 "$DMG" >/dev/null
echo "Créé : $DMG ($(du -h "$DMG" | cut -f1))"
