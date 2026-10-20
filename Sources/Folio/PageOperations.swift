import AppKit
import PDFKit
import UniformTypeIdentifiers

/// Page management. Every structural change goes through `applyPages`, which makes it undoable.
extension ViewerModel {
    /// Selected pages in document order, or the current page when nothing is selected.
    var targetPages: [PDFPage] {
        let selected = pages.filter { selectedPages.contains($0) }
        if !selected.isEmpty { return selected }
        return pdfView.currentPage.map { [$0] } ?? []
    }

    func syncPages() {
        guard let doc = pdfView.document else { return }
        pages = (0..<doc.pageCount).compactMap { doc.page(at: $0) }
        pageCount = pages.count
        selectedPages.formIntersection(pages)
    }

    /// Makes the document contain exactly `target`, in that order, as one undoable action.
    func applyPages(_ target: [PDFPage], actionName: String) {
        guard let doc = pdfView.document, !target.isEmpty else { return }
        let old = pages
        let keep = Set(target.map(ObjectIdentifier.init))

        for index in stride(from: doc.pageCount - 1, through: 0, by: -1) {
            if let page = doc.page(at: index), !keep.contains(ObjectIdentifier(page)) {
                doc.removePage(at: index)
            }
        }
        for (index, page) in target.enumerated() {
            if index < doc.pageCount, doc.page(at: index) === page { continue }
            // exchangePage(at:withPageAt:) crashes on image-backed pages, so move by remove + insert.
            let current = doc.index(for: page)
            if current != NSNotFound { doc.removePage(at: current) }
            doc.insert(page, at: index)
        }

        if let annotation = selectedAnnotation, annotation.page.map({ !keep.contains(ObjectIdentifier($0)) }) ?? true {
            select(nil)
        }
        clearSearch()
        syncPages()
        pdfView.layoutDocumentView()
        revision += 1

        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.applyPages(old, actionName: actionName) }
        }
        undoManager?.setActionName(actionName)
    }

    // MARK: Operations

    func rotate(_ targets: [PDFPage], by degrees: Int) {
        guard !targets.isEmpty else { return }
        for page in targets {
            page.rotation = ((page.rotation + degrees) % 360 + 360) % 360
        }
        pdfView.layoutDocumentView()
        revision += 1
        let name = degrees < 0 ? "Pivoter à gauche" : "Pivoter à droite"
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.rotate(targets, by: -degrees) }
        }
        undoManager?.setActionName(name)
    }

    func deletePages(_ targets: [PDFPage]) {
        let remaining = pages.filter { !targets.contains($0) }
        guard !remaining.isEmpty else {
            NSSound.beep()
            return
        }
        applyPages(remaining, actionName: targets.count > 1 ? "Supprimer les pages" : "Supprimer la page")
    }

    func duplicatePages(_ targets: [PDFPage]) {
        guard let last = targets.last, let at = pages.firstIndex(of: last) else { return }
        let copies = targets.compactMap { $0.copy() as? PDFPage }
        var result = pages
        result.insert(contentsOf: copies, at: at + 1)
        applyPages(result, actionName: "Dupliquer")
        selectedPages = Set(copies)
    }

    func insertBlankPage() {
        let size = pdfView.currentPage?.bounds(for: .mediaBox).size ?? CGSize(width: 595, height: 842)
        let page = PDFFileDocument.blankPage(size: size)
        insert([page], at: insertionIndex, actionName: "Insérer une page vierge")
    }

    func movePages(from source: IndexSet, to destination: Int) {
        var result = pages
        result.move(fromOffsets: source, toOffset: destination)
        guard result != pages else { return }
        applyPages(result, actionName: "Déplacer les pages")
    }

    func insert(_ newPages: [PDFPage], at index: Int, actionName: String) {
        guard !newPages.isEmpty else { return }
        var result = pages
        result.insert(contentsOf: newPages, at: min(max(index, 0), result.count))
        applyPages(result, actionName: actionName)
        selectedPages = Set(newPages)
        if let first = newPages.first { pdfView.go(to: first) }
    }

    /// Just after the last targeted page.
    var insertionIndex: Int {
        guard let last = targetPages.last, let index = pages.firstIndex(of: last) else { return pages.count }
        return index + 1
    }

    // MARK: Files

    /// Pages from PDFs and images; PDF pages are copied so the source documents are untouched.
    /// Source documents are kept alive alongside their copied pages (see `retainedSources`).
    func pages(fromFiles urls: [URL]) -> [PDFPage] {
        urls.flatMap { url -> [PDFPage] in
            if let doc = PDFDocument(url: url) {
                retainedSources.append(doc)
                return (0..<doc.pageCount).compactMap { doc.page(at: $0)?.copy() as? PDFPage }
            }
            if let image = NSImage(contentsOf: url), let page = PDFPage(image: image) {
                return [page]
            }
            return []
        }
    }

    func insertFiles(_ urls: [URL], at index: Int) {
        let newPages = pages(fromFiles: urls)
        guard !newPages.isEmpty else { return NSSound.beep() }
        insert(newPages, at: index, actionName: "Insérer des pages")
    }

    func chooseFilesToInsert() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .image]
        panel.allowsMultipleSelection = true
        panel.message = "Choisissez des PDF ou des images à insérer après la page courante"
        panel.prompt = "Insérer"
        let index = insertionIndex
        runPanel(panel) { [weak self] in self?.insertFiles(panel.urls, at: index) }
    }

    func extractPages() {
        let targets = targetPages
        guard !targets.isEmpty else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.pdf]
        panel.nameFieldStringValue = "Pages extraites.pdf"
        panel.message = targets.count > 1 ? "Enregistrer les \(targets.count) pages sélectionnées" : "Enregistrer la page"
        runPanel(panel) {
            let output = PDFDocument()
            for (i, page) in targets.enumerated() {
                if let copy = page.copy() as? PDFPage { output.insert(copy, at: i) }
            }
            if let url = panel.url, !output.write(to: url) { NSSound.beep() }
        }
    }

    private func runPanel(_ panel: NSSavePanel, onOK: @escaping () -> Void) {
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            if response == .OK { onOK() }
        }
        if let window = pdfView.window {
            panel.beginSheetModal(for: window, completionHandler: handler)
        } else {
            handler(panel.runModal())
        }
    }
}
