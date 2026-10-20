import SwiftUI

struct ContentView: View {
    @ObservedObject var document: PDFFileDocument
    @State private var viewer = ViewerModel()
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        NavigationSplitView {
            PageSidebar(viewer: viewer)
                .navigationSplitViewColumnWidth(min: 140, ideal: 170, max: 260)
        } detail: {
            PDFCanvas(viewer: viewer, document: document.pdf)
                .ignoresSafeArea(edges: .bottom)
                .safeAreaInset(edge: .top, spacing: 0) { AnnotationBar(viewer: viewer) }
        }
        .inspector(isPresented: inspectorShown) {
            if let annotation = viewer.selectedAnnotation {
                AnnotationInspector(viewer: viewer, annotation: annotation)
                    .id(ObjectIdentifier(annotation))
                    .inspectorColumnWidth(min: 220, ideal: 260, max: 360)
            }
        }
        .sheet(isPresented: $viewer.showsSignaturePad) {
            SignaturePad { signature in
                SignatureStore.shared.add(signature)
                viewer.placeSignature(signature)
            }
        }
        .sheet(isPresented: $viewer.showsStampEditor) {
            CustomStampEditor { stamp in
                CustomStampStore.shared.add(stamp)
                viewer.placeStamp(text: stamp.text, color: stamp.color)
            }
        }
        .onAppear { viewer.undoManager = undoManager }
        .onChange(of: undoManager) { _, manager in viewer.undoManager = manager }
        .toolbar { toolbar }
        .searchable(text: $viewer.searchText, placement: .toolbar, prompt: "Rechercher")
        .onSubmit(of: .search) { viewer.search() }
        .onChange(of: viewer.searchText) { _, text in
            if text.isEmpty { viewer.clearSearch() }
        }
        .focusedSceneValue(\.viewer, viewer)
        .frame(minWidth: 700, minHeight: 500)
    }

    private var inspectorShown: Binding<Bool> {
        Binding {
            viewer.selectedAnnotation != nil
        } set: { shown in
            if !shown { viewer.select(nil) }
        }
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .navigation) {
            Button(action: viewer.previousPage) { Image(systemName: "chevron.up") }
                .help("Page précédente")
            Button(action: viewer.nextPage) { Image(systemName: "chevron.down") }
                .help("Page suivante")
            Text(viewer.pageCount > 0 ? "\(viewer.pageIndex + 1) / \(viewer.pageCount)" : "–")
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }

        ToolbarItemGroup {
            ControlGroup {
                Button { viewer.rotate(viewer.targetPages, by: -90) } label: { Image(systemName: "rotate.left") }
                    .help("Pivoter à gauche")
                Button { viewer.rotate(viewer.targetPages, by: 90) } label: { Image(systemName: "rotate.right") }
                    .help("Pivoter à droite")
            }

            Picker("Affichage", selection: $viewer.layout) {
                ForEach(PageLayout.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.menu)
            .help("Mode d'affichage")

            ControlGroup {
                Button(action: viewer.zoomOut) { Image(systemName: "minus.magnifyingglass") }
                    .help("Zoom arrière")
                Button(action: viewer.zoomToFit) {
                    Text("\(Int((viewer.scale * 100).rounded())) %").monospacedDigit()
                }
                .help("Ajuster à la fenêtre")
                Button(action: viewer.zoomIn) { Image(systemName: "plus.magnifyingglass") }
                    .help("Zoom avant")
            }

            if !viewer.searchResults.isEmpty {
                ControlGroup {
                    Button(action: viewer.previousResult) { Image(systemName: "chevron.left") }
                    Text("\(viewer.searchIndex + 1)/\(viewer.searchResults.count)").monospacedDigit()
                    Button(action: viewer.nextResult) { Image(systemName: "chevron.right") }
                }
                .help("Résultats de recherche")
            }
        }
    }
}
