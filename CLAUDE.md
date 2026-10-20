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
  - `AnnotationViews.swift` : barre d'outils d'annotation et inspecteur.
- Avancement et étapes suivantes : voir `ROADMAP.md` (avancer une étape à la fois, commiter à la fin de chacune).
