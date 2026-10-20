import AppKit
import PDFKit
import SwiftUI
import UniformTypeIdentifiers

/// A PDF document backed by PDFKit. PDFKit mutates `pdf` in place, so this is a reference document.
final class PDFFileDocument: ReferenceFileDocument {
    static var readableContentTypes: [UTType] { [.pdf] }

    let pdf: PDFDocument

    /// New documents start with a single blank A4 page.
    init() {
        pdf = PDFDocument()
        pdf.delegate = AnnotationClassProvider.shared
        pdf.insert(Self.blankPage(), at: 0)
    }

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents,
              let pdf = PDFDocument(data: data) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        self.pdf = pdf
        pdf.delegate = AnnotationClassProvider.shared
    }

    func snapshot(contentType: UTType) throws -> Data {
        guard let data = pdf.dataRepresentation() else {
            throw CocoaError(.fileWriteUnknown)
        }
        return data
    }

    func fileWrapper(snapshot: Data, configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: snapshot)
    }

    static func blankPage(size: CGSize = CGSize(width: 595, height: 842)) -> PDFPage {
        let image = NSImage(size: size, flipped: false) { rect in
            NSColor.white.setFill()
            rect.fill()
            return true
        }
        return PDFPage(image: image) ?? PDFPage()
    }
}

/// Loads free text boxes and stamps as Folio's subclasses so they keep their custom rendering.
/// PDFDocument holds its delegate weakly, hence the shared instance.
final class AnnotationClassProvider: NSObject, PDFDocumentDelegate {
    static let shared = AnnotationClassProvider()

    func `class`(forAnnotationType annotationType: String) -> AnyClass {
        switch annotationType {
        case "FreeText": FreeTextAnnotation.self
        case "Stamp": ImageAnnotation.self
        default: PDFAnnotation.self
        }
    }
}
