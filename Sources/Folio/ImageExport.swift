import AppKit
import PDFKit
import SwiftUI
import UniformTypeIdentifiers

enum ImageFormat: String, CaseIterable, Identifiable {
    case png = "PNG", jpeg = "JPEG"
    var id: Self { self }
    var fileExtension: String { self == .png ? "png" : "jpg" }
}

extension ViewerModel {
    /// Renders `targets` to image files in a folder chosen by the user.
    func exportImages(of targets: [PDFPage], format: ImageFormat, dpi: CGFloat) -> Bool {
        guard !targets.isEmpty else { return false }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.prompt = tr("Exporter ici")
        panel.message = tr("Choisissez le dossier où enregistrer les images")
        guard panel.runModal() == .OK, let folder = panel.url else { return false }

        let title = pdfView.window?.title ?? tr("Page")
        let base = title.hasSuffix(".pdf") ? String(title.dropLast(4)) : title
        let digits = max(String(pages.count).count, 2)
        var written: [URL] = []
        for page in targets {
            let number = (pages.firstIndex(of: page) ?? 0) + 1
            let name = tr("%@ - page %@", base, String(format: "%0\(digits)d", number)) + "." + format.fileExtension
            let url = folder.appendingPathComponent(name)
            guard let data = Self.render(page, format: format, dpi: dpi) else { continue }
            do {
                try data.write(to: url, options: .atomic)
                written.append(url)
            } catch {
                NSAlert(error: error).runModal()
                return true
            }
        }
        NSWorkspace.shared.activateFileViewerSelecting(written)
        return true
    }

    static func render(_ page: PDFPage, format: ImageFormat, dpi: CGFloat) -> Data? {
        // thumbnail(of:for:) honours the page rotation and returns exactly the requested pixels.
        let bounds = page.bounds(for: .cropBox)
        let points = page.rotation % 180 == 0 ? bounds.size : CGSize(width: bounds.height, height: bounds.width)
        let scale = dpi / 72
        let size = CGSize(width: (points.width * scale).rounded(), height: (points.height * scale).rounded())
        let image = page.thumbnail(of: size, for: .cropBox)
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        let rep = NSBitmapImageRep(cgImage: cgImage)
        rep.size = points
        return format == .png
            ? rep.representation(using: .png, properties: [:])
            : rep.representation(using: .jpeg, properties: [.compressionFactor: 0.9])
    }
}

struct ImageExportSheet: View {
    let viewer: ViewerModel
    @Environment(\.dismiss) private var dismiss
    @State private var format = ImageFormat.png
    @State private var dpi: CGFloat = 150
    @State private var onlySelection = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Exporter en images").font(.headline)

            Form {
                Picker("Format", selection: $format) {
                    ForEach(ImageFormat.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)

                Picker("Résolution", selection: $dpi) {
                    Text("72 ppp (écran)").tag(CGFloat(72))
                    Text("150 ppp (standard)").tag(CGFloat(150))
                    Text("300 ppp (impression)").tag(CGFloat(300))
                }

                Picker("Pages", selection: $onlySelection) {
                    Text(tr("Toutes (%d)", viewer.pages.count)).tag(false)
                    Text(tr("Sélection (%d)", viewer.targetPages.count)).tag(true)
                }
                .pickerStyle(.radioGroup)
            }

            HStack {
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Exporter…") {
                    let targets = onlySelection ? viewer.targetPages : viewer.pages
                    if viewer.exportImages(of: targets, format: format, dpi: dpi) { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 400)
    }
}
