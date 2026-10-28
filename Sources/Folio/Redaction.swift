import AppKit
import CoreText
import PDFKit

/// Redaction happens in two steps: the user places marks (square annotations tagged `/FolioRedact`),
/// then applies them. Applying rebuilds each marked page as a 300 ppi image with black boxes,
/// plus invisible text for every word outside the marks, so the hidden content is really gone
/// while the rest of the page stays searchable and selectable.
enum Redactor {
    static let markKey = PDFAnnotationKey(rawValue: "/FolioRedact")
    static let dpi: CGFloat = 300

    static func mark(bounds: CGRect) -> PDFAnnotation {
        let mark = PDFAnnotation(bounds: bounds, forType: .square, withProperties: nil)
        mark.color = .systemRed
        mark.interiorColor = NSColor.systemRed.withAlphaComponent(0.25)
        mark.border = AnnotationFactory.border(1)
        mark.setValue("1", forAnnotationKey: markKey)
        return mark
    }

    static func marks(for selection: PDFSelection) -> [(PDFAnnotation, PDFPage)] {
        selection.selectionsByLine().flatMap { line in
            line.pages.compactMap { page -> (PDFAnnotation, PDFPage)? in
                let bounds = line.bounds(for: page)
                guard bounds.width > 0, bounds.height > 0 else { return nil }
                return (mark(bounds: bounds.insetBy(dx: -1, dy: -1)), page)
            }
        }
    }

    /// A one-page document replacing `page` (from a snapshot, which this mutates) with `areas` blacked out.
    static func redacted(_ page: PDFPage, areas: [CGRect]) -> PDFDocument? {
        let words = words(on: page).filter { word in !areas.contains { covers($0, word.bounds) } }

        let rotation = page.rotation
        let cropBox = page.bounds(for: .cropBox)
        page.rotation = 0
        page.annotations.filter(\.isRedactionMark).forEach { page.removeAnnotation($0) }
        var mediaBox = page.bounds(for: .mediaBox)

        guard let image = rasterize(page, mediaBox: mediaBox, blackout: areas) else { return nil }

        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data as CFMutableData),
              let context = CGContext(consumer: consumer, mediaBox: &mediaBox, nil) else { return nil }
        context.beginPDFPage(nil)
        context.draw(image, in: mediaBox)
        context.setTextDrawingMode(.invisible)
        for word in words { draw(word, in: context) }
        context.endPDFPage()
        context.closePDF()

        guard let document = PDFDocument(data: data as Data), let result = document.page(at: 0) else { return nil }
        result.rotation = rotation
        result.setBounds(cropBox, for: .cropBox)
        return document
    }

    // MARK: Text

    struct Word {
        var text: String
        var bounds: CGRect
    }

    /// Words of the page with their bounds, in page space.
    static func words(on page: PDFPage) -> [Word] {
        var words: [Word] = []
        var current: Word?

        func flush() {
            if let word = current, !word.text.isEmpty { words.append(word) }
            current = nil
        }

        // characterBounds(at:) drifts after each line break, so take each character's box from a
        // one-character selection, which uses the same indexing as page.string.
        let string = (page.string ?? "") as NSString
        for index in 0..<string.length {
            let character = string.substring(with: NSRange(location: index, length: 1))
            let isBlank = character.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            let bounds = isBlank ? .null : (page.selection(for: NSRange(location: index, length: 1))?.bounds(for: page) ?? .null)
            if isBlank || bounds.isEmpty {
                flush()
                continue
            }
            // A jump back, or a glyph not vertically overlapping the word, starts a new word even without a space.
            if let word = current,
               bounds.minY >= word.bounds.maxY || bounds.maxY <= word.bounds.minY || bounds.midX < word.bounds.minX {
                flush()
            }
            if var word = current {
                word.text += character
                word.bounds = word.bounds.union(bounds)
                current = word
            } else {
                current = Word(text: character, bounds: bounds)
            }
        }
        flush()
        return words
    }

    /// Whether a word is hidden by a marked area. Words only partly covered are removed entirely,
    /// but merely touching an area does not count.
    private static func covers(_ area: CGRect, _ word: CGRect) -> Bool {
        let overlap = area.intersection(word)
        return !overlap.isNull && overlap.width > min(1, word.width * 0.2) && overlap.height > word.height * 0.3
    }

    /// Draws a word with invisible text, stretched to its original bounds.
    private static func draw(_ word: Word, in context: CGContext) {
        let size = max(word.bounds.height * 0.85, 1)
        let font = CTFontCreateWithName("Helvetica" as CFString, size, nil)
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: word.text, attributes: [.font: font]))
        let width = CTLineGetTypographicBounds(line, nil, nil, nil)
        guard width > 0 else { return }
        context.saveGState()
        context.textMatrix = .identity
        context.translateBy(x: word.bounds.minX, y: word.bounds.minY + word.bounds.height * 0.2)
        context.scaleBy(x: word.bounds.width / width, y: 1)
        context.textPosition = .zero
        CTLineDraw(line, context)
        context.restoreGState()
    }

    // MARK: Image

    private static func rasterize(_ page: PDFPage, mediaBox: CGRect, blackout areas: [CGRect]) -> CGImage? {
        let scale = dpi / 72
        let width = Int((mediaBox.width * scale).rounded()), height = Int((mediaBox.height * scale).rounded())
        let thumbnail = page.thumbnail(of: CGSize(width: width, height: height), for: .mediaBox)
        guard let rendered = thumbnail.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let context = CGContext(
                  data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
              ) else { return nil }
        context.setFillColor(.white)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.draw(rendered, in: CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(.black)
        for area in areas {
            context.fill(CGRect(
                x: (area.minX - mediaBox.minX) * scale, y: (area.minY - mediaBox.minY) * scale,
                width: area.width * scale, height: area.height * scale
            ))
        }
        return context.makeImage()
    }
}

extension PDFAnnotation {
    var isRedactionMark: Bool { value(forAnnotationKey: Redactor.markKey) != nil }
}

// MARK: - Viewer

extension ViewerModel {
    var redactionMarkCount: Int {
        _ = revision
        return pages.reduce(0) { $0 + $1.annotations.filter(\.isRedactionMark).count }
    }

    func addRedactionMarks(for selection: PDFSelection) {
        for (mark, page) in Redactor.marks(for: selection) {
            add(mark, to: page, actionName: "Marquer pour caviardage")
        }
    }

    func applyRedactions() {
        let affected = pages.enumerated().compactMap { index, page -> (Int, [CGRect])? in
            let areas = page.annotations.filter(\.isRedactionMark).map(\.bounds)
            return areas.isEmpty ? nil : (index, areas)
        }
        guard !affected.isEmpty, let document = pdfView.document else { return }

        let alert = NSAlert()
        alert.messageText = tr("Appliquer le caviardage ?")
        alert.informativeText = tr(
            "Le contenu sous les %d zones marquées sera supprimé du document. Les %d pages concernées sont reconstruites : leur texte restant reste sélectionnable, mais leurs annotations et liens sont intégrés à la page. Vous pouvez annuler (⌘Z) tant que le document est ouvert ; une fois enregistré, le contenu caviardé est perdu.",
            redactionMarkCount, affected.count
        )
        alert.addButton(withTitle: tr("Appliquer"))
        alert.addButton(withTitle: tr("Annuler"))
        alert.buttons.first?.hasDestructiveAction = true
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        guard let snapshot = SendExporter.snapshot(of: document, password: documentPassword) else { return NSSound.beep() }
        var result = pages
        for (index, areas) in affected {
            guard let page = snapshot.page(at: index), let redacted = Redactor.redacted(page, areas: areas),
                  let newPage = redacted.page(at: 0) else { return NSSound.beep() }
            retainedSources.append(redacted)
            result[index] = newPage
        }
        select(nil)
        clearSearch()
        applyPages(result, actionName: "Caviarder")
    }
}
