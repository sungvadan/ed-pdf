# pdf-svd (nom de code interne : Folio)

Éditeur PDF macOS complet (lecture, annotation, signature, édition du texte). Swift 5 mode + SwiftUI + PDFKit, SwiftPM uniquement (pas de projet Xcode : seulement les Command Line Tools sont installés).

- Nom affiché : pdf-svd (Info.plist, bundle `build/pdf-svd.app`), identifiant `local.svd.pdf-svd` (projet personnel).
  Installation : `scripts/install.sh`.
- Build : `scripts/build-app.sh [debug|release] [--open]` → `build/pdf-svd.app` (bundle assemblé à la main, Info.plist dans `Resources/`, signature ad hoc).
- Cible : macOS 14+. UI en français (langue source) traduite en en, de, es, it, pt-BR, ja, zh-Hans.
  Les textes français sont les clés : littéraux SwiftUI (`Text("…")`, `Button("…")`, `.help("…")`) traduits
  automatiquement ; ailleurs (String, NSAlert, panneaux, enums) `tr("…")`, avec `%d`/`%@` au lieu d'interpolations.
  Noms d'actions d'annulation traduits par `setActionName(tr(…))`. Traductions dans
  `Resources/Localizations/<lang>.lproj/Localizable.strings` ; après tout texte ajouté ou modifié, lancer
  `scripts/l10n.py` (signale clés manquantes/inutilisées, formats différents, interpolations) et compléter
  chaque langue. `// l10n-ignore` en fin de ligne pour un littéral qui n'est pas du texte d'interface.
- Architecture :
  - `PDFFileDocument` : `ReferenceFileDocument` encapsulant un `PDFDocument` (modifié en place).
  - `ViewerModel` (@Observable, @MainActor) : possède le `PDFView` de la fenêtre et expose son état ; partagé avec les menus via `focusedSceneValue(\.viewer)`.
  - `PDFKitViews.swift` : wrappers NSViewRepresentable (canvas, vignettes).
  - `FolioPDFView` : sous-classe de PDFView qui route la souris vers l'outil actif (dessin, déplacement, surlignage).
  - `Annotations.swift` : outils, `AnnotationFactory`, édition avec undo (`add`/`remove`/`update`/`registerChange` sur ViewerModel). Toute modification passe par ces méthodes pour être annulable et marquer le document modifié. Pour forcer le redessin PDFKit : `annotation.refresh()` (retire/rajoute).
  - `FreeTextAnnotation` : sous-classe chargée pour toutes les FreeText via `PDFDocumentDelegate.class(forAnnotationType:)` (délégué partagé `AnnotationClassProvider.shared`, à poser sur toute copie de document). Dessine elle-même le texte si `/FolioStyle` est présent, sinon rendu natif.
  - `PageOperations.swift` / `PageSidebar.swift` : gestion des pages. Toute modification de structure passe par `applyPages(_:actionName:)` (état cible complet, undo = état précédent). Ne pas utiliser `exchangePage` (plante sur les pages issues d'images) ; une page copiée garde `page.document` de l'original → utiliser `doc.index(for:)`.
  - `ImageAnnotation` (Stamp), `Signatures.swift` (signatures, tampons, placement), `Forms.swift` (instantanés des champs pour l'undo ; `FolioPDFView.commitFormChange()`).
  - `SendExport.swift` : export pour envoi. Aplatir = redessiner chaque page dans un `CGContext` PDF avec `rotation = 0` puis réappliquer la rotation (dessiner une page pivotée la déforme).
  - `Redaction.swift` : position des caractères = `page.selection(for: NSRange(i,1)).bounds` (ne PAS utiliser `characterBounds(at:)`, décalé après chaque saut de ligne). Les PDF produits par `cupsfilter` ont des métriques fausses : tester avec un PDF généré par CoreText.
  - Pages remplacées par des pages d'un document temporaire (aplatir, caviarder, insérer) : garder ce document dans `retainedSources`.
  - Tests manuels hors UI : compiler les fichiers concernés avec `swiftc` dans un dossier temporaire (pas de cible de test pour l'instant).
  - `AnnotationViews.swift` : barre d'outils d'annotation et inspecteur.
- Édition du texte : module `Sources/FolioText` (PDFium + parseur PDF maison), outil `textlab`, conception et pièges dans
  `docs/text-editing.md` — À LIRE avant d'y toucher. Tests : `swift scripts/make-fixtures.swift && scripts/textlab-suite.sh`
  et `Folio --selftest-edit`.
- Version Windows : `windows/` (C# .NET 10 + Avalonia 11.3 + PDFium via P/Invoke, `Pdf/Pdfium.cs` ; tout appel PDFium
  sous `Pdfium.Lock`). Textes : `Tr("…")` (clés françaises, mêmes fichiers `.strings`, vérifiés par `scripts/l10n.py`),
  jamais de texte dans les .axaml. Build : `scripts/build-windows.sh` (licences tierces rassemblées par
  `scripts/collect-licenses-windows.py` et intégrées à l'exe ; toute nouvelle dépendance → `THIRD-PARTY-NOTICES.md`) ; essai sur le Mac :
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec windows/bin/Debug/net10.0/pdf-svd fichier.pdf` (après `dotnet build`),
  `PDFSVD_LANG=en` pour une autre langue. L'utilisateur teste dans une VM Windows 11 ARM (VMware) par glisser-déposer de l'exe.
- Avancement et étapes suivantes : voir `ROADMAP.md` (avancer une étape à la fois, commiter à la fin de chacune).
