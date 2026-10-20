# Folio

Éditeur PDF macOS (type PDF Expert). Swift 5 mode + SwiftUI + PDFKit, SwiftPM uniquement (pas de projet Xcode : seulement les Command Line Tools sont installés).

- Build : `scripts/build-app.sh [debug|release] [--open]` → `build/Folio.app` (bundle assemblé à la main, Info.plist dans `Resources/`, signature ad hoc).
- Cible : macOS 14+. UI en français.
- Architecture :
  - `PDFFileDocument` : `ReferenceFileDocument` encapsulant un `PDFDocument` (modifié en place).
  - `ViewerModel` (@Observable, @MainActor) : possède le `PDFView` de la fenêtre et expose son état ; partagé avec les menus via `focusedSceneValue(\.viewer)`.
  - `PDFKitViews.swift` : wrappers NSViewRepresentable (canvas, vignettes).
  - `FolioPDFView` : sous-classe de PDFView qui route la souris vers l'outil actif (dessin, déplacement, surlignage).
  - `Annotations.swift` : outils, `AnnotationFactory`, édition avec undo (`add`/`remove`/`update`/`registerChange` sur ViewerModel). Toute modification passe par ces méthodes pour être annulable et marquer le document modifié. Pour forcer le redessin PDFKit : `annotation.refresh()` (retire/rajoute).
  - `FreeTextAnnotation` : sous-classe chargée pour toutes les FreeText via `PDFDocumentDelegate.class(forAnnotationType:)` (délégué = `PDFFileDocument`). Dessine elle-même le texte si `/FolioStyle` est présent, sinon rendu natif.
  - `PageOperations.swift` / `PageSidebar.swift` : gestion des pages. Toute modification de structure passe par `applyPages(_:actionName:)` (état cible complet, undo = état précédent). Ne pas utiliser `exchangePage` (plante sur les pages issues d'images) ; une page copiée garde `page.document` de l'original → utiliser `doc.index(for:)`.
  - `ImageAnnotation` (Stamp), `Signatures.swift` (signatures, tampons, placement), `Forms.swift` (instantanés des champs pour l'undo ; `FolioPDFView.commitFormChange()`).
  - Tests manuels hors UI : compiler les fichiers concernés avec `swiftc` dans un dossier temporaire (pas de cible de test pour l'instant).
  - `AnnotationViews.swift` : barre d'outils d'annotation et inspecteur.
- Avancement et étapes suivantes : voir `ROADMAP.md` (avancer une étape à la fois, commiter à la fin de chacune).
