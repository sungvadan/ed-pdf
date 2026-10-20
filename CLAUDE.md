# Folio

Éditeur PDF macOS (type PDF Expert). Swift 5 mode + SwiftUI + PDFKit, SwiftPM uniquement (pas de projet Xcode : seulement les Command Line Tools sont installés).

- Build : `scripts/build-app.sh [debug|release] [--open]` → `build/Folio.app` (bundle assemblé à la main, Info.plist dans `Resources/`, signature ad hoc).
- Cible : macOS 14+. UI en français.
- Architecture :
  - `PDFFileDocument` : `ReferenceFileDocument` encapsulant un `PDFDocument` (modifié en place).
  - `ViewerModel` (@Observable, @MainActor) : possède le `PDFView` de la fenêtre et expose son état ; partagé avec les menus via `focusedSceneValue(\.viewer)`.
  - `PDFKitViews.swift` : wrappers NSViewRepresentable (canvas, vignettes).
- Avancement et étapes suivantes : voir `ROADMAP.md` (avancer une étape à la fois, commiter à la fin de chacune).
