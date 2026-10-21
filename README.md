# pdf-svd

Éditeur PDF natif pour macOS, écrit en Swift avec SwiftUI et PDFKit.

```sh
scripts/build-app.sh release --open
```

## Installer dans /Applications

```sh
scripts/install.sh
```

## Partager (image disque à glisser-déposer)

```sh
scripts/make-dmg.sh   # → build/pdf-svd-<version>.dmg
```

L'app n'étant pas signée par un compte développeur Apple, le destinataire doit l'autoriser une fois
(Réglages Système › Confidentialité et sécurité › « Ouvrir quand même »). Mac à puce Apple uniquement.

(Le code interne garde le nom de module `Folio` ; l'app s'appelle pdf-svd.)
