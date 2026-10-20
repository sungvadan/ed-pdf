import AppKit
import PDFKit
import SwiftUI

/// Privacy options for a copy meant to be sent to someone else.
struct SendOptions {
    /// Draw annotations and form fields into the page content.
    var flatten = true
    var downscaleImages = true
    var removeAuthor = true
    /// Drop the `/FolioImage` copy kept to re-edit images (only matters when not flattening).
    var stripEditingData = true
}

/// Builds the copy to send. Always works on a copy: the open document is never changed.
enum SendExporter {
    static let imageMaxPixels: CGFloat = 600

    static func export(_ document: PDFDocument, options: SendOptions) -> Data? {
        guard let data = document.dataRepresentation(), let copy = PDFDocument(data: data) else { return nil }
        copy.delegate = AnnotationClassProvider.shared

        for page in (0..<copy.pageCount).compactMap({ copy.page(at: $0) }) {
            for annotation in page.annotations {
                if options.removeAuthor { annotation.userName = nil }
                if let imageAnnotation = annotation as? ImageAnnotation, let image = imageAnnotation.image,
                   options.downscaleImages || options.stripEditingData {
                    let replacement = ImageAnnotation(
                        image: image, bounds: annotation.bounds,
                        maxPixels: options.downscaleImages ? imageMaxPixels : ImageAnnotation.maxPixels
                    )
                    replacement.userName = options.removeAuthor ? nil : annotation.userName
                    // The image stays cached, so the saved appearance still shows it.
                    if options.stripEditingData { replacement.removeValue(forAnnotationKey: ImageAnnotation.imageKey) }
                    page.removeAnnotation(annotation)
                    page.addAnnotation(replacement)
                }
            }
        }

        var attributes = copy.documentAttributes ?? [:]
        if options.removeAuthor { attributes.removeValue(forKey: PDFDocumentAttribute.authorAttribute) }
        attributes[PDFDocumentAttribute.creatorAttribute] = "Folio"
        copy.documentAttributes = attributes

        return options.flatten ? flattened(copy) : copy.dataRepresentation()
    }

    /// Redraws every page with its annotations into a new PDF. Page text stays real text;
    /// web links are kept, internal links and bookmarks are lost.
    private static func flattened(_ document: PDFDocument) -> Data? {
        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data as CFMutableData),
              let context = CGContext(consumer: consumer, mediaBox: nil, nil) else { return nil }

        var rotations: [Int] = [], cropBoxes: [CGRect] = [], links: [[PDFAnnotation]] = []
        for page in (0..<document.pageCount).compactMap({ document.page(at: $0) }) {
            // Drawing a rotated page mangles it, so draw upright and restore the rotation afterwards.
            rotations.append(page.rotation)
            page.rotation = 0
            cropBoxes.append(page.bounds(for: .cropBox))
            let webLinks = page.annotations.filter { $0.subtype == "Link" && $0.url != nil }
            links.append(webLinks)
            page.annotations.filter { $0.subtype == "Link" }.forEach { page.removeAnnotation($0) }

            var mediaBox = page.bounds(for: .mediaBox)
            context.beginPage(mediaBox: &mediaBox)
            page.draw(with: .mediaBox, to: context)
            context.endPage()
        }
        context.closePDF()

        guard let flat = PDFDocument(data: data as Data) else { return nil }
        for index in 0..<flat.pageCount {
            guard let page = flat.page(at: index) else { continue }
            page.rotation = rotations[index]
            page.setBounds(cropBoxes[index], for: .cropBox)
            links[index].forEach { page.addAnnotation($0) }
        }
        flat.documentAttributes = document.documentAttributes
        return flat.dataRepresentation()
    }
}

extension ViewerModel {
    /// Asks where to save, then writes the copy to send. Returns false if cancelled.
    @discardableResult
    func exportForSending(_ options: SendOptions) -> Bool {
        guard let document = pdfView.document else { return false }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.pdf]
        let title = pdfView.window?.title ?? "Document"
        let base = title.hasSuffix(".pdf") ? String(title.dropLast(4)) : title
        panel.nameFieldStringValue = "\(base) (envoi).pdf"
        panel.message = "Enregistrer la copie à envoyer"
        guard panel.runModal() == .OK, let url = panel.url else { return false }

        do {
            guard let data = SendExporter.export(document, options: options) else {
                throw CocoaError(.fileWriteUnknown)
            }
            try data.write(to: url, options: .atomic)
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } catch {
            NSAlert(error: error).runModal()
        }
        return true
    }
}

struct SendExportSheet: View {
    let viewer: ViewerModel
    @Environment(\.dismiss) private var dismiss
    @State private var options = SendOptions()

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Exporter pour envoi").font(.headline)
            Text("Crée une copie prête à envoyer. Le document ouvert n'est pas modifié.")
                .foregroundStyle(.secondary)

            option(
                "Aplatir les annotations et les formulaires", isOn: $options.flatten,
                detail: "Signatures, tampons, notes et champs sont intégrés à la page : ils ne peuvent plus être sélectionnés, déplacés ni modifiés. Le texte des zones de texte devient sélectionnable."
            )
            option(
                "Réduire la résolution des signatures et images", isOn: $options.downscaleImages,
                detail: "Limite les images à \(Int(SendExporter.imageMaxPixels)) pixels : suffisant à l'écran et à l'impression, moins exploitable si quelqu'un l'extrait."
            )
            option(
                "Retirer mon nom d'auteur", isOn: $options.removeAuthor,
                detail: "macOS inscrit le nom de votre compte dans chaque annotation et dans les propriétés du document."
            )
            if !options.flatten {
                option(
                    "Retirer les données d'édition de Folio", isOn: $options.stripEditingData,
                    detail: "Supprime la copie de l'image conservée pour la rééditer dans Folio. L'image reste visible."
                )
            }

            HStack {
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Exporter…") {
                    if viewer.exportForSending(options) { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 480)
    }

    private func option(_ title: String, isOn: Binding<Bool>, detail: String) -> some View {
        Toggle(isOn: isOn) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}
