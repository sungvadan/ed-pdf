import PDFKit
import SwiftUI
import UniformTypeIdentifiers

/// Page thumbnails: multi-selection, drag to reorder, drop files to insert, context menu.
struct PageSidebar: View {
    @Bindable var viewer: ViewerModel

    var body: some View {
        List(selection: $viewer.selectedPages) {
            ForEach(Array(viewer.pages.enumerated()), id: \.element) { index, page in
                PageThumbnail(page: page, number: index + 1, revision: viewer.revision)
                    .tag(page)
            }
            .onMove { viewer.movePages(from: $0, to: $1) }
            .onInsert(of: [.fileURL]) { index, providers in
                loadURLs(providers) { viewer.insertFiles($0, at: index) }
            }
        }
        .contextMenu(forSelectionType: PDFPage.self) { selection in
            if !selection.isEmpty {
                PageMenuItems(viewer: viewer)
            }
        }
        .onDeleteCommand { viewer.deletePages(viewer.targetPages) }
        .onChange(of: viewer.selectedPages) { _, selection in
            if selection.count == 1, let page = selection.first, page !== viewer.pdfView.currentPage {
                viewer.pdfView.go(to: page)
            }
        }
    }

    private func loadURLs(_ providers: [NSItemProvider], completion: @escaping ([URL]) -> Void) {
        let group = DispatchGroup()
        var urls: [URL] = []
        for provider in providers {
            group.enter()
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                DispatchQueue.main.async {
                    if let url { urls.append(url) }
                    group.leave()
                }
            }
        }
        group.notify(queue: .main) { completion(urls) }
    }
}

private struct PageThumbnail: View {
    let page: PDFPage
    let number: Int
    let revision: Int
    @State private var image: NSImage?

    var body: some View {
        VStack(spacing: 4) {
            Group {
                if let image {
                    Image(nsImage: image)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                } else {
                    Rectangle().fill(.white)
                        .aspectRatio(0.7, contentMode: .fit)
                }
            }
            .frame(maxHeight: 160)
            .shadow(color: .black.opacity(0.25), radius: 2, y: 1)

            Text("\(number)")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 4)
        .task(id: revision) {
            image = page.thumbnail(of: CGSize(width: 240, height: 320), for: .cropBox)
        }
    }
}

/// Page actions shared by the context menu and the Pages menu.
struct PageMenuItems: View {
    let viewer: ViewerModel

    var body: some View {
        Button("Pivoter à gauche") { viewer.rotate(viewer.targetPages, by: -90) }
        Button("Pivoter à droite") { viewer.rotate(viewer.targetPages, by: 90) }
        Divider()
        Button("Dupliquer") { viewer.duplicatePages(viewer.targetPages) }
        Button("Insérer une page vierge") { viewer.insertBlankPage() }
        Button("Insérer depuis un fichier…") { viewer.chooseFilesToInsert() }
        Button("Extraire…") { viewer.extractPages() }
        Button("Exporter en images…") { viewer.showsImageExport = true }
        Button("Aplatir les annotations") { viewer.flattenAnnotations(of: viewer.targetPages) }
        Divider()
        Button("Supprimer", role: .destructive) { viewer.deletePages(viewer.targetPages) }
    }
}
