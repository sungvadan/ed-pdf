# pdf-svd

Éditeur PDF natif pour macOS : lire, annoter, signer, réorganiser les pages… et **modifier le texte existant d'un PDF
comme dans un traitement de texte**, en gardant sa police, sa taille et sa couleur.

Écrit en Swift (SwiftUI + PDFKit), avec [PDFium](https://pdfium.googlesource.com/pdfium/) pour l'édition du texte.
Interface en français.

## Fonctionnalités

- **Lecture** : miniatures, zoom, page unique / continu / deux pages, recherche, signets
- **Annotations** : surligner, souligner, barrer, notes, zones de texte mises en forme, dessin, formes, flèches
- **Pages** : pivoter, supprimer, dupliquer, réordonner par glisser-déposer, insérer des PDF ou des images, extraire
- **Formulaires** : remplissage des champs, annulable
- **Signatures** : dessinées, ou photographiées sur papier (l'encre est détourée automatiquement) ; tampons
  prédéfinis ou personnalisés ; images
- **Modifier le texte** : clic dans un paragraphe, édition sur place, reflux des lignes, styles conservés ; le reste
  de la page n'est pas touché
- **Caviardage** : le texte masqué est réellement supprimé du fichier
- **Export pour envoi** : aplatissement, mot de passe, compression, suppression du nom d'auteur ; export en images
- Annuler / rétablir partout (⌘Z)

## Installation

Télécharger le `.dmg` (voir les *Releases* du dépôt), l'ouvrir et glisser **pdf-svd** dans **Applications**.

L'app n'est pas signée par un compte développeur Apple : à la première ouverture, macOS la bloque. Il faut alors
aller dans **Réglages Système › Confidentialité et sécurité** et cliquer sur **« Ouvrir quand même »** (une seule fois).

Configuration : Mac à puce Apple, macOS 14 ou plus récent.

## Compiler depuis les sources

Seuls les *Command Line Tools* sont nécessaires (pas besoin de Xcode) :

```sh
xcode-select --install          # si besoin
scripts/build-app.sh release --open   # télécharge PDFium (version épinglée, SHA-256 vérifié) puis compile
scripts/install.sh              # installe dans /Applications
scripts/make-dmg.sh             # crée build/pdf-svd-<version>.dmg à partager
```

## Organisation du code

| Dossier | Contenu |
|---|---|
| `Sources/Folio` | L'app (SwiftUI/AppKit) : visionneuse, annotations, pages, signatures, export… |
| `Sources/FolioText` | Le moteur d'édition du texte, sans interface : analyse de la page, choix des polices, mise en page, réécriture du PDF |
| `Sources/textlab` | Outil en ligne de commande pour tester le moteur (`analyze`, `replace`, `render`) |
| `scripts/` | Compilation, installation, image disque, icône, PDF de test, suite de tests |
| `docs/text-editing.md` | Conception détaillée de l'édition du texte et pièges rencontrés |

`Folio` est le nom de code interne du projet.

### Comment fonctionne l'édition du texte

PDFKit ne sait pas modifier le texte d'une page. pdf-svd utilise PDFium pour repérer les paragraphes (police, taille,
couleur, position) et écrire le nouveau texte, puis retire l'ancien texte directement dans les instructions de dessin
de la page, opérateur par opérateur : le reste de la page reste identique au pixel près. Chaque modification est
relue et vérifiée avant d'être appliquée. Détails dans [docs/text-editing.md](docs/text-editing.md).

### Tests

```sh
swift build --product textlab
swift scripts/make-fixtures.swift      # génère des PDF de test dans build/fixtures
scripts/textlab-suite.sh               # scénarios d'édition du texte
build/pdf-svd.app/Contents/MacOS/Folio --selftest-edit build/fixtures/lettre.pdf "trois mois" "deux mois" /tmp/out.pdf
```

## Limites connues

- Édition du texte : pas encore sur les PDF protégés par mot de passe, le texte pivoté, ni les pages pivotées ; un
  paragraphe qui s'allonge peut chevaucher ce qui suit.
- Pas de signature numérique (certificat) : les signatures sont visuelles.

## Licence

Code sous licence [MIT](LICENSE).

pdf-svd embarque [PDFium](https://pdfium.googlesource.com/pdfium/) (licence BSD) compilé par
[pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), qui contient d'autres bibliothèques (FreeType,
libjpeg-turbo, zlib, ICU…). Leurs licences sont livrées dans l'app (`Contents/Resources/Licenses`).

Lors d'une modification du texte, pdf-svd peut incorporer dans le PDF une police installée sur le Mac, comme le font
les traitements de texte ; les polices dont la licence interdit l'incorporation ne sont jamais utilisées.
