import PDFKit
import SwiftUI

/// Tree wrapper so SwiftUI can display a PDF outline.
struct OutlineNode: Identifiable {
    let outline: PDFOutline
    var id: ObjectIdentifier { ObjectIdentifier(outline) }

    var children: [OutlineNode]? {
        guard outline.numberOfChildren > 0 else { return nil }
        return (0..<outline.numberOfChildren).compactMap { outline.child(at: $0) }.map(OutlineNode.init)
    }
}

enum SidebarMode: String, CaseIterable, Identifiable {
    case pages = "Pages", bookmarks = "Signets"
    var id: Self { self }
}

// MARK: - Editing with undo

extension ViewerModel {
    var bookmarkNodes: [OutlineNode] {
        _ = revision
        return OutlineNode(outline: pdfView.document?.outlineRoot ?? PDFOutline()).children ?? []
    }

    func go(to outline: PDFOutline) {
        if let destination = outline.destination ?? (outline.action as? PDFActionGoTo)?.destination {
            pdfView.go(to: destination)
        }
    }

    /// Bookmarks the top of the visible part of the current page.
    func addBookmark() {
        guard let document = pdfView.document, let page = pdfView.currentPage else { return }
        let root = document.outlineRoot ?? {
            let root = PDFOutline()
            document.outlineRoot = root
            return root
        }()
        let top = pdfView.convert(CGPoint(x: 0, y: pdfView.bounds.maxY), to: page)
        let bookmark = PDFOutline()
        bookmark.label = tr("Page %d", document.index(for: page) + 1)
        bookmark.destination = PDFDestination(page: page, at: CGPoint(x: kPDFDestinationUnspecifiedValue, y: min(top.y, page.bounds(for: .mediaBox).maxY)))
        insert(bookmark, into: root, at: root.numberOfChildren)
        sidebarMode = .bookmarks
    }

    func removeBookmark(_ bookmark: PDFOutline) {
        guard let parent = bookmark.parent,
              let index = (0..<parent.numberOfChildren).first(where: { parent.child(at: $0) === bookmark }) else { return }
        bookmark.removeFromParent()
        revision += 1
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.insert(bookmark, into: parent, at: index) }
        }
        undoManager?.setActionName(tr("Supprimer le signet"))
    }

    func renameBookmark(_ bookmark: PDFOutline, to label: String) {
        let old = bookmark.label ?? ""
        guard label != old else { return }
        bookmark.label = label
        revision += 1
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.renameBookmark(bookmark, to: old) }
        }
        undoManager?.setActionName(tr("Renommer le signet"))
    }

    private func insert(_ bookmark: PDFOutline, into parent: PDFOutline, at index: Int) {
        parent.insertChild(bookmark, at: min(index, parent.numberOfChildren))
        revision += 1
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.removeBookmark(bookmark) }
        }
        undoManager?.setActionName(tr("Ajouter un signet"))
    }
}

// MARK: - Views

/// Sidebar with a Pages / Bookmarks switch.
struct Sidebar: View {
    @Bindable var viewer: ViewerModel

    var body: some View {
        VStack(spacing: 0) {
            Picker("Barre latérale", selection: $viewer.sidebarMode) {
                ForEach(SidebarMode.allCases) { Text(tr($0.rawValue)).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(8)

            switch viewer.sidebarMode {
            case .pages: PageSidebar(viewer: viewer)
            case .bookmarks: BookmarkSidebar(viewer: viewer)
            }
        }
    }
}

private struct BookmarkSidebar: View {
    let viewer: ViewerModel
    @State private var editing: ObjectIdentifier?
    @State private var draft = ""

    var body: some View {
        let nodes = viewer.bookmarkNodes
        VStack(spacing: 0) {
            if nodes.isEmpty {
                ContentUnavailableView(
                    "Aucun signet", systemImage: "bookmark",
                    description: Text("Ajoutez un signet pour retrouver la page affichée.")
                )
            } else {
                List {
                    OutlineGroup(nodes, children: \.children) { node in
                        row(node)
                    }
                }
            }
            Divider()
            HStack {
                Button { viewer.addBookmark() } label: {
                    Label("Ajouter un signet", systemImage: "plus")
                }
                .buttonStyle(.borderless)
                .help("Ajouter un signet pour la page affichée (⌘B)")
                Spacer()
            }
            .padding(8)
        }
    }

    @ViewBuilder
    private func row(_ node: OutlineNode) -> some View {
        if editing == node.id {
            TextField("Nom du signet", text: $draft)
                .onSubmit {
                    viewer.renameBookmark(node.outline, to: draft)
                    editing = nil
                }
                .onExitCommand { editing = nil }
        } else {
            Text(node.outline.label ?? tr("Sans titre"))
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { startEditing(node) }
                .onTapGesture { viewer.go(to: node.outline) }
                .contextMenu {
                    Button("Renommer") { startEditing(node) }
                    Button("Supprimer", role: .destructive) { viewer.removeBookmark(node.outline) }
                }
        }
    }

    private func startEditing(_ node: OutlineNode) {
        draft = node.outline.label ?? ""
        editing = node.id
    }
}
