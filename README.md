# ed-pdf

*[English version](README.en.md)* · Site : https://sungvadan.github.io/ed-pdf/

Éditeur PDF gratuit et libre pour macOS et Windows : lire, annoter, signer, réorganiser les pages… et **modifier le
texte existant d'un PDF comme dans un traitement de texte**, en gardant sa police, sa taille et sa couleur.

- **macOS** : app native en Swift (SwiftUI + PDFKit), avec [PDFium](https://pdfium.googlesource.com/pdfium/) pour
  l'édition du texte.
- **Windows** : app en C# (.NET 10 + [Avalonia](https://avaloniaui.net)) avec PDFium, mêmes fonctions principales.
- Interface en français, anglais, allemand, espagnol, italien, portugais (Brésil), japonais et chinois simplifié.
- Tout se passe sur l'ordinateur : ni compte, ni envoi de fichiers, ni connexion à Internet.

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
- **Export pour envoi** : aplatissement, mot de passe (macOS), compression, suppression du nom d'auteur ; export en
  images ; réduction de la taille
- Annuler / rétablir partout (⌘Z sur Mac, Ctrl+Z sous Windows)

## Installation

Les fichiers se trouvent dans les [*Releases*](../../releases) du dépôt.

### macOS

Télécharger le `.dmg`, l'ouvrir et glisser **ed-pdf** dans **Applications**.

L'app n'est pas signée par un compte développeur Apple : à la première ouverture, macOS la bloque. Il faut alors
aller dans **Réglages Système › Confidentialité et sécurité** et cliquer sur **« Ouvrir quand même »** (une seule fois).

Configuration : Mac à puce Apple, macOS 14 ou plus récent.

### Windows

Télécharger `ed-pdf-x64.exe` (PC Intel/AMD) ou `ed-pdf-arm64.exe` (PC ARM) et le lancer : rien à installer.

L'exe n'est pas signé : Windows SmartScreen affiche « Windows a protégé votre ordinateur ». Cliquer sur
**Informations complémentaires › Exécuter quand même** (une seule fois).

Configuration : Windows 10 ou 11, 64 bits (testé sur Windows 11).

## Compiler depuis les sources

### Prérequis

- Mac à puce Apple (arm64), macOS 14 ou plus récent
- Les *Command Line Tools* d'Apple (Swift 5.10 ou plus récent) ; Xcode n'est pas nécessaire :

  ```sh
  xcode-select --install
  swift --version        # vérifier que Swift est disponible
  ```

- Une connexion Internet pour la première compilation (téléchargement de PDFium)

### Compiler et lancer

Depuis le dossier du dépôt :

```sh
scripts/build-app.sh release --open
```

Le script :

1. télécharge PDFium dans `Vendor/pdfium` (version épinglée, empreinte SHA-256 vérifiée ; une seule fois) ;
2. compile l'app avec `swift build` ;
3. assemble le bundle `build/ed-pdf.app` (exécutable, `Info.plist`, icône, PDFium, licences) et le signe en ad hoc ;
4. l'ouvre si `--open` est passé.

Pour une compilation plus rapide pendant le développement :

```sh
scripts/build-app.sh debug --open
```

### Installer

```sh
scripts/install.sh
```

Compile en mode release, quitte l'app si elle est ouverte, remplace `/Applications/ed-pdf.app` et l'enregistre
auprès du Finder (menu « Ouvrir avec »). À relancer après chaque modification pour mettre à jour l'app installée :
`build/ed-pdf.app` seul ne remplace pas la version des Applications.

### Créer l'image disque

```sh
scripts/make-dmg.sh
```

Produit `build/ed-pdf.dmg` (nom fixe, pour le lien de téléchargement du site), avec un raccourci vers
Applications et un fichier « Lisez-moi », prêt à partager.

### Version Windows

Compilée sur le Mac (pas besoin de Windows) avec le SDK .NET 10 (`brew install dotnet`) :

```sh
scripts/build-windows.sh
```

Produit `build/windows/ed-pdf-x64.exe` et `build/windows/ed-pdf-arm64.exe` (fichiers uniques, PDFium et .NET
inclus). Pour l'essayer sur le Mac : `cd windows && dotnet run -- fichier.pdf`.

### Récapitulatif des commandes

| Commande | Résultat |
|---|---|
| `scripts/build-app.sh [debug\|release] [--open]` | `build/ed-pdf.app` (release par défaut) |
| `scripts/install.sh` | app installée dans `/Applications` |
| `scripts/make-dmg.sh` | `build/ed-pdf.dmg` |
| `scripts/build-windows.sh` | `build/windows/ed-pdf-x64.exe` et `ed-pdf-arm64.exe` |
| `scripts/fetch-pdfium.sh` | PDFium seul, dans `Vendor/pdfium` (appelé automatiquement) |
| `swift build --product textlab` | outil en ligne de commande du moteur de texte |

`swift build` utilisé directement ne produit pas d'app : il faut d'abord avoir téléchargé PDFium
(`scripts/fetch-pdfium.sh`), et l'exécutable n'a ni icône ni `Info.plist`. Passer par `scripts/build-app.sh`.

## Organisation du code

| Dossier | Contenu |
|---|---|
| `Sources/Folio` | L'app (SwiftUI/AppKit) : visionneuse, annotations, pages, signatures, export… |
| `Sources/FolioText` | Le moteur d'édition du texte, sans interface : analyse de la page, choix des polices, mise en page, réécriture du PDF |
| `Sources/textlab` | Outil en ligne de commande pour tester le moteur (`analyze`, `replace`, `render`) |
| `windows/` | La version Windows (C# + Avalonia), dont le portage du moteur de texte dans `windows/Pdf/Text` |
| `Resources/Localizations` | Traductions (le texte français sert de clé), partagées par les deux versions |
| `scripts/` | Compilation, installation, image disque, icône, PDF de test, suite de tests |
| `site/` | Le site web (`python3 site/build.py` → `build/site`, publié par `.github/workflows/pages.yml`) |
| `docs/text-editing.md` | Conception détaillée de l'édition du texte et pièges rencontrés |

`Folio` est le nom de code interne du projet.

### Comment fonctionne l'édition du texte

PDFKit ne sait pas modifier le texte d'une page. ed-pdf utilise PDFium pour repérer les paragraphes (police, taille,
couleur, position) et écrire le nouveau texte, puis retire l'ancien texte directement dans les instructions de dessin
de la page, opérateur par opérateur : le reste de la page reste identique au pixel près. Chaque modification est
relue et vérifiée avant d'être appliquée. Détails dans [docs/text-editing.md](docs/text-editing.md).

### Tests

```sh
swift build --product textlab
swift scripts/make-fixtures.swift      # génère des PDF de test dans build/fixtures
scripts/textlab-suite.sh               # scénarios d'édition du texte
build/ed-pdf.app/Contents/MacOS/Folio --selftest-edit build/fixtures/lettre.pdf "trois mois" "deux mois" /tmp/out.pdf
```

## Limites connues

- Édition du texte : pas encore sur les PDF protégés par mot de passe, le texte pivoté, ni les pages pivotées ; un
  paragraphe qui s'allonge peut chevaucher ce qui suit.
- Pas de signature numérique (certificat) : les signatures sont visuelles.
- Windows : pas de mot de passe à l'export (PDFium ne sait pas chiffrer) ; pendant l'édition du texte, le gras en
  ligne s'affiche en maigre (il est conservé à l'écriture).

## Avertissement

ed-pdf est fourni **« tel quel », sans aucune garantie**, comme le prévoit sa [licence](LICENSE). Ses auteurs ne
peuvent être tenus responsables d'aucun dommage lié à son utilisation, notamment une perte ou une altération de
données.

- **Gardez une copie de vos fichiers originaux** et vérifiez le résultat avant de l'envoyer.
- **Vous êtes seul responsable de l'usage que vous faites du logiciel et des documents que vous modifiez.** ed-pdf
  sert à corriger et compléter vos propres documents, ou ceux que vous êtes autorisé à modifier. Modifier un document
  sans en avoir le droit (contrat, facture, bulletin de paie, diplôme, pièce officielle…) ou utiliser un document
  ainsi modifié peut constituer un faux, puni par la loi (en France, articles 441-1 et suivants du Code pénal).
- **Les signatures sont des images** posées sur la page : ce ne sont pas des signatures électroniques au sens du
  règlement européen eIDAS (ni avancées, ni qualifiées) et elles ne garantissent ni l'identité du signataire ni
  l'intégrité du document.
- **Caviardage** : le texte caviardé est supprimé du fichier, mais vérifiez toujours le document produit (recherche
  du texte masqué, copier-coller) avant de le diffuser.

## Contribuer

Les signalements de problèmes et les propositions de modification sont les bienvenus. En proposant une
contribution, vous acceptez qu'elle soit publiée sous la même licence MIT.

## Licence

Code sous licence [MIT](LICENSE) : vous pouvez l'utiliser, le modifier et le redistribuer librement, y compris dans un
projet commercial, à condition de conserver la mention de copyright et le texte de la licence. Il n'est assorti
d'aucune garantie.

ed-pdf embarque [PDFium](https://pdfium.googlesource.com/pdfium/) (licence BSD) compilé par
[pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), qui contient d'autres bibliothèques (FreeType,
libjpeg-turbo, zlib, ICU…), et, sous Windows, le runtime .NET, Avalonia et SkiaSharp (licence MIT). Tous sont sous
licence permissive ; la liste complète est dans [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) et leurs licences
sont livrées avec l'app (macOS : `Contents/Resources/Licenses` ; Windows : intégrées à l'exe).

Lors d'une modification du texte, ed-pdf peut incorporer dans le PDF une police installée sur l'ordinateur, comme le
font les traitements de texte ; les polices dont la licence interdit l'incorporation ne sont jamais utilisées.

PDF est une marque d'Adobe ; Apple, Mac et macOS sont des marques d'Apple Inc. ; Windows est une marque de Microsoft.
ed-pdf est un projet indépendant, sans lien avec ces sociétés.
