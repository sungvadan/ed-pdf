import AppKit
import Observation
import PDFKit
import SwiftUI

enum PageLayout: String, CaseIterable, Identifiable {
    case single = "Page unique"
    case continuous = "Défilement continu"
    case twoUp = "Deux pages"

    var id: Self { self }

    var displayMode: PDFDisplayMode {
        switch self {
        case .single: .singlePage
        case .continuous: .singlePageContinuous
        case .twoUp: .twoUpContinuous
        }
    }
}

/// Owns the PDFView of one window and exposes its state to SwiftUI.
@MainActor
@Observable
final class ViewerModel {
    let pdfView = FolioPDFView(frame: .zero)

    private(set) var pageIndex = 0
    var pageCount = 0
    var pages: [PDFPage] = []
    var selectedPages: Set<PDFPage> = []
    private(set) var scale: CGFloat = 1

    var layout: PageLayout = .continuous {
        didSet { pdfView.displayMode = layout.displayMode }
    }

    var searchText = ""
    private(set) var searchResults: [PDFSelection] = []
    private(set) var searchIndex = 0

    // MARK: Annotation state

    var tool: AnnotationTool = .select {
        didSet {
            guard tool != oldValue else { return }
            if oldValue == .editText {
                pdfView.endTextEditing(commit: true)
                pdfView.hoverOverlay.isHidden = true
            }
            if tool != .select { select(nil) }
            pdfView.applyMarkupFromSelection()
        }
    }

    var toolColors: [AnnotationTool: Color] = [:]
    var lineWidth: CGFloat = 2
    var showsSignaturePad = false
    var showsSignatureCapture = false
    var showsStampEditor = false
    var showsSendExport = false
    var showsImageExport = false
    var sidebarMode = SidebarMode.pages
    /// Password used to unlock an encrypted document, needed to make working copies of it.
    @ObservationIgnored var documentPassword: String?
    /// Temporary documents whose pages were copied into this one. Copied pages share the source's
    /// CoreGraphics data; keeping the sources alive avoids freeing it while pages still use it.
    @ObservationIgnored var retainedSources: [PDFDocument] = []
    @ObservationIgnored var textLayoutCache: PageTextLayout?
    var selectedAnnotation: PDFAnnotation?
    /// Bumped whenever annotation properties change, so views reading PDFKit objects refresh.
    var revision = 0

    @ObservationIgnored weak var undoManager: UndoManager?
    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    var toolColor: Color {
        get { toolColors[tool] ?? tool.defaultColor }
        set { toolColors[tool] = newValue }
    }

    init() {
        pdfView.viewer = self
        pdfView.autoScales = true
        pdfView.displayMode = layout.displayMode
        pdfView.displaysPageBreaks = true
        pdfView.backgroundColor = .underPageBackgroundColor

        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: .PDFViewPageChanged, object: pdfView, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.syncPage()
                self?.pdfView.updateSelectionOverlay()
            }
        })
        observers.append(center.addObserver(forName: .PDFViewScaleChanged, object: pdfView, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.scale = self?.pdfView.scaleFactor ?? 1
                // The editor's fonts are sized for one zoom level.
                self?.pdfView.endTextEditing(commit: true)
                self?.pdfView.updateSelectionOverlay()
            }
        })
    }

    func attach(_ document: PDFDocument) {
        guard pdfView.document !== document else { return }
        pdfView.document = document
        syncPages()
        syncPage()
    }

    private func syncPage() {
        guard let doc = pdfView.document, let page = pdfView.currentPage else { return }
        pageIndex = doc.index(for: page)
        // Follow scrolling in the sidebar unless the page is part of a multi-selection.
        if !selectedPages.contains(page) { selectedPages = [page] }
    }

    // MARK: Navigation

    func goTo(page index: Int) {
        guard let doc = pdfView.document, (0..<doc.pageCount).contains(index),
              let page = doc.page(at: index) else { return }
        pdfView.go(to: page)
    }

    func nextPage() { pdfView.goToNextPage(nil) }
    func previousPage() { pdfView.goToPreviousPage(nil) }
    func firstPage() { pdfView.goToFirstPage(nil) }
    func lastPage() { pdfView.goToLastPage(nil) }

    // MARK: Zoom

    func zoomIn() { pdfView.zoomIn(nil) }
    func zoomOut() { pdfView.zoomOut(nil) }
    func actualSize() { pdfView.scaleFactor = 1 }
    func zoomToFit() { pdfView.autoScales = true }

    // MARK: Search

    func search() {
        guard let doc = pdfView.document else { return }
        let query = searchText.trimmingCharacters(in: .whitespaces)
        searchResults = query.isEmpty ? [] : doc.findString(query, withOptions: [.caseInsensitive, .diacriticInsensitive])
        searchIndex = 0
        pdfView.highlightedSelections = searchResults.isEmpty ? nil : searchResults
        showCurrentResult()
    }

    func nextResult() { stepResult(by: 1) }
    func previousResult() { stepResult(by: -1) }

    func clearSearch() {
        searchText = ""
        searchResults = []
        pdfView.highlightedSelections = nil
        pdfView.clearSelection()
    }

    private func stepResult(by delta: Int) {
        guard !searchResults.isEmpty else { return }
        searchIndex = (searchIndex + delta + searchResults.count) % searchResults.count
        showCurrentResult()
    }

    private func showCurrentResult() {
        guard searchResults.indices.contains(searchIndex) else { return }
        let selection = searchResults[searchIndex]
        pdfView.setCurrentSelection(selection, animate: false)
        pdfView.go(to: selection)
    }
}
