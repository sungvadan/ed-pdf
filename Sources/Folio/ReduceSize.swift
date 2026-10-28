import AppKit
import FolioText
import PDFKit

extension ViewerModel {
    /// Fichier > Réduire la taille… : saves a smaller copy (images at 150 dpi in JPEG, clean-up,
    /// compressed structure). The open document is not changed.
    func reduceFileSize() {
        guard let document = pdfView.document, let original = document.dataRepresentation() else { return NSSound.beep() }
        let password = documentPassword
        let pageCount = document.pageCount
        let title = pdfView.window?.title ?? "Document"
        let base = title.hasSuffix(".pdf") ? String(title.dropLast(4)) : title
        Task {
            let reduced = await Task.detached { () -> Data? in
                guard let data = try? PDFCompressor.compress(original, password: password),
                      PDFDocument(data: data)?.pageCount == pageCount else { return nil }
                return data
            }.value
            guard let reduced else {
                return showAlert("Impossible de réduire ce document.", detail: "Sa structure n'est pas prise en charge.")
            }
            let before = document.documentURL.flatMap { try? $0.resourceValues(forKeys: [.fileSizeKey]).fileSize } ?? original.count
            guard Double(reduced.count) < Double(before) * 0.95 else {
                return showAlert("Ce document est déjà compact.", detail: "Aucune réduction significative n'est possible.")
            }

            let format = ByteCountFormatter()
            let percent = Int((1 - Double(reduced.count) / Double(before)) * 100)
            let panel = NSSavePanel()
            panel.allowedContentTypes = [.pdf]
            panel.nameFieldStringValue = tr("%@ (réduit)", base) + ".pdf"
            panel.message = tr("Taille : %@ → %@ (−%d %%)", format.string(fromByteCount: Int64(before)), format.string(fromByteCount: Int64(reduced.count)), percent)
                + (password == nil ? "" : " — " + tr("la copie ne sera pas protégée par mot de passe"))
            let save: (NSApplication.ModalResponse) -> Void = { response in
                guard response == .OK, let url = panel.url else { return }
                do {
                    try reduced.write(to: url, options: .atomic)
                } catch {
                    NSAlert(error: error).runModal()
                }
            }
            if let window = pdfView.window { panel.beginSheetModal(for: window, completionHandler: save) } else { save(panel.runModal()) }
        }
    }

    private func showAlert(_ message: String, detail: String) {
        let alert = NSAlert()
        alert.messageText = tr(message)
        alert.informativeText = tr(detail)
        if let window = pdfView.window { alert.beginSheetModal(for: window) } else { alert.runModal() }
    }
}
