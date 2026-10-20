import Observation
import PDFKit

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
    let pdfView = PDFView()

    private(set) var pageIndex = 0
    private(set) var pageCount = 0
    private(set) var scale: CGFloat = 1

    var layout: PageLayout = .continuous {
        didSet { pdfView.displayMode = layout.displayMode }
    }

    var searchText = ""
    private(set) var searchResults: [PDFSelection] = []
    private(set) var searchIndex = 0

    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    init() {
        pdfView.autoScales = true
        pdfView.displayMode = layout.displayMode
        pdfView.displaysPageBreaks = true
        pdfView.backgroundColor = .underPageBackgroundColor

        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: .PDFViewPageChanged, object: pdfView, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.syncPage() }
        })
        observers.append(center.addObserver(forName: .PDFViewScaleChanged, object: pdfView, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.scale = self?.pdfView.scaleFactor ?? 1 }
        })
    }

    func attach(_ document: PDFDocument) {
        guard pdfView.document !== document else { return }
        pdfView.document = document
        syncPage()
    }

    private func syncPage() {
        guard let doc = pdfView.document else { return }
        pageCount = doc.pageCount
        if let page = pdfView.currentPage {
            pageIndex = doc.index(for: page)
        }
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
