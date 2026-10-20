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
