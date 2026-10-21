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
    /// Password required to open the copy; empty for none.
    var password = ""
    /// Recompress images as JPEG optimized for screen.
    var compress = false

    var writeOptions: [PDFDocumentWriteOption: Any] {
        var options: [PDFDocumentWriteOption: Any] = [:]
        if !password.isEmpty {
            options[.userPasswordOption] = password
            options[.ownerPasswordOption] = password
        }
        if compress {
            options[.saveImagesAsJPEGOption] = true
            options[.optimizeImagesForScreenOption] = true
        }
        return options
    }
}

/// Builds the copy to send. Always works on a copy: the open document is never changed.
enum SendExporter {
    static let imageMaxPixels: CGFloat = 600

    /// An independent, unlocked copy of the document to work on.
    static func snapshot(of document: PDFDocument, password: String?) -> PDFDocument? {
        guard let data = document.dataRepresentation(), let copy = PDFDocument(data: data) else { return nil }
        if copy.isLocked, !(password.map(copy.unlock(withPassword:)) ?? false) { return nil }
        copy.delegate = AnnotationClassProvider.shared
        return copy
    }

    static func export(_ document: PDFDocument, password: String?, options: SendOptions) -> Data? {
        guard let copy = snapshot(of: document, password: password) else { return nil }

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
        attributes[PDFDocumentAttribute.creatorAttribute] = "pdf-svd"
        copy.documentAttributes = attributes

        let output = options.flatten ? flattened(copy) : copy
        return output?.dataRepresentation(options: options.writeOptions)
    }

    /// Redraws the pages at `indexes` (all by default) with their annotations into a new document,
    /// one page per index. Page text stays real text; web links are kept, internal links and
    /// bookmarks are lost. Mutates `document`, so pass a snapshot.
    static func flattened(_ document: PDFDocument, indexes: [Int]? = nil) -> PDFDocument? {
        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data as CFMutableData),
              let context = CGContext(consumer: consumer, mediaBox: nil, nil) else { return nil }

        var rotations: [Int] = [], cropBoxes: [CGRect] = [], links: [[PDFAnnotation]] = []
        let pages = (indexes ?? Array(0..<document.pageCount)).compactMap { document.page(at: $0) }
        for page in pages {
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
        return flat
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
            guard let data = SendExporter.export(document, password: documentPassword, options: options) else {
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

extension ViewerModel {
    /// Draws the annotations and form fields of `targets` into their page content, as one undoable action.
    func flattenAnnotations(of targets: [PDFPage]) {
        guard let document = pdfView.document,
              let snapshot = SendExporter.snapshot(of: document, password: documentPassword) else { return NSSound.beep() }
        let indexes = targets.compactMap { pages.firstIndex(of: $0) }
        guard !indexes.isEmpty, let flat = SendExporter.flattened(snapshot, indexes: indexes) else { return NSSound.beep() }
        retainedSources.append(flat)
        var result = pages
        for (k, index) in indexes.enumerated() {
            if let page = flat.page(at: k)?.copy() as? PDFPage { result[index] = page }
        }
        select(nil)
        applyPages(result, actionName: "Aplatir")
    }
}

struct SendExportSheet: View {
    let viewer: ViewerModel
    @Environment(\.dismiss) private var dismiss
    @State private var options = SendOptions()
    @State private var protect = false
    @State private var password = ""
    @State private var confirmation = ""

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
                    "Retirer les données d'édition de pdf-svd", isOn: $options.stripEditingData,
                    detail: "Supprime la copie de l'image conservée pour la rééditer dans pdf-svd. L'image reste visible."
                )
            }
            option(
                "Réduire la taille du fichier", isOn: $options.compress,
                detail: "Recompresse les images en JPEG adapté à l'écran. Utile pour l'envoi par e-mail ; un peu moins net à l'impression."
            )
            option(
                "Protéger par un mot de passe", isOn: $protect,
                detail: "Le destinataire devra saisir ce mot de passe pour ouvrir le document. Communiquez-le par un autre moyen que le fichier."
            )
            if protect {
                VStack(alignment: .leading, spacing: 6) {
                    SecureField("Mot de passe", text: $password)
                    SecureField("Confirmer le mot de passe", text: $confirmation)
                    if !confirmation.isEmpty && password != confirmation {
                        Text("Les mots de passe ne correspondent pas.")
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                }
                .padding(.leading, 20)
            }

            HStack {
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Exporter…") {
                    var options = options
                    options.password = protect ? password : ""
                    if viewer.exportForSending(options) { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(protect && (password.isEmpty || password != confirmation))
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
