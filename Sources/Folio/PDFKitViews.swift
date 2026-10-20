import PDFKit
import SwiftUI

/// Hosts the PDFView owned by a `ViewerModel`.
struct PDFCanvas: NSViewRepresentable {
    let viewer: ViewerModel
    let document: PDFDocument

    func makeNSView(context: Context) -> PDFView {
        viewer.attach(document)
        return viewer.pdfView
    }

    func updateNSView(_ view: PDFView, context: Context) {
        viewer.attach(document)
    }
}

/// Native page thumbnails bound to the viewer's PDFView.
struct ThumbnailSidebar: NSViewRepresentable {
    let viewer: ViewerModel

    func makeNSView(context: Context) -> PDFThumbnailView {
        let view = PDFThumbnailView()
        view.pdfView = viewer.pdfView
        view.thumbnailSize = CGSize(width: 120, height: 160)
        view.backgroundColor = .clear
        return view
    }

    func updateNSView(_ view: PDFThumbnailView, context: Context) {}
}
