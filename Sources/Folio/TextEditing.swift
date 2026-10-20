import AppKit
import FolioText
import PDFKit

extension NSAttributedString.Key {
    /// Index of the original run style a character belongs to (typed text inherits it).
    static let folioRunStyle = NSAttributedString.Key("pdf-svd.runStyle")
}

/// Paragraphs of one page, analyzed with PDFium on a snapshot of the document.
struct PageTextLayout {
    let pageIndex: Int
    let revision: Int
    let data: Data
    let blocks: [TextBlock]
}

/// An in-place editor laid exactly over a paragraph of the page.
@MainActor
final class TextEditSession: NSObject, NSTextViewDelegate {
    let layout: PageTextLayout
    let blockIndex: Int
    let styles: [RunStyle]
    let geometry: ParagraphGeometry
    let textView = NSTextView()
    let label = NSTextField(labelWithString: "")
    let originalText: String
    weak var pdfView: FolioPDFView?

    var block: TextBlock { layout.blocks[blockIndex] }

    init(layout: PageTextLayout, blockIndex: Int, scale: CGFloat, pdfView: FolioPDFView) {
        self.layout = layout
        self.blockIndex = blockIndex
        self.pdfView = pdfView
        let block = layout.blocks[blockIndex]
        geometry = ParagraphGeometry(block: block)

        var styles: [RunStyle] = []
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = switch geometry.alignment {
        case .left: .left
        case .center: .center
        case .right: .right
        case .justified: .justified
        }
        paragraph.minimumLineHeight = geometry.leading * scale
        paragraph.maximumLineHeight = geometry.leading * scale

        let text = NSMutableAttributedString()
        for segment in block.styledSegments {
            let index = styles.firstIndex(of: segment.style) ?? {
                styles.append(segment.style)
                return styles.count - 1
            }()
            let c = segment.style.color
            text.append(NSAttributedString(string: segment.text, attributes: [
                .font: TextDisplay.font(for: segment.style, size: segment.style.fontSize * scale) as NSFont,
                .foregroundColor: NSColor(srgbRed: c.red, green: c.green, blue: c.blue, alpha: c.alpha),
                .paragraphStyle: paragraph,
                .folioRunStyle: index,
            ]))
        }
        // The last line has no separator to the next one; drop a dangling space.
        while text.string.hasSuffix(" ") { text.deleteCharacters(in: NSRange(location: text.length - 1, length: 1)) }
        self.styles = styles
        originalText = text.string
        super.init()

        textView.textStorage?.setAttributedString(text)
        textView.typingAttributes = text.length > 0 ? text.attributes(at: 0, effectiveRange: nil) : [:]
        textView.delegate = self
        textView.isRichText = true
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.drawsBackground = true
        textView.backgroundColor = .white
        textView.textContainerInset = .zero
        textView.textContainer?.lineFragmentPadding = 0
        textView.isVerticallyResizable = false
        textView.isHorizontallyResizable = false
        textView.focusRingType = .none
        textView.wantsLayer = true
        textView.layer?.borderColor = NSColor.controlAccentColor.cgColor
        textView.layer?.borderWidth = 1
        if block.lines.count == 1 {
            textView.textContainer?.size = CGSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
            textView.textContainer?.widthTracksTextView = false
        } else {
            // The paragraph width comes from ink bounds, slightly narrower than glyph advances.
            textView.textContainer?.size = CGSize(width: (geometry.width + (block.dominantRun?.fontSize ?? 10) * 0.3) * scale, height: .greatestFiniteMagnitude)
            textView.textContainer?.widthTracksTextView = false
        }

        label.font = .systemFont(ofSize: 10, weight: .medium)
        label.textColor = .white
        label.drawsBackground = true
        label.backgroundColor = NSColor.controlAccentColor
        label.stringValue = Self.describe(styles: styles, block: block)
        label.sizeToFit()
    }

    private static func describe(styles: [RunStyle], block: TextBlock) -> String {
        guard let main = block.dominantRun.map(RunStyle.init(run:)) else { return "" }
        let size = String(format: "%g", (main.fontSize * 10).rounded() / 10)
        let displayFont = TextDisplay.font(for: main, size: 12)
        let display = CTFontCopyFamilyName(displayFont) as String
        let exact = (CTFontCopyPostScriptName(displayFont) as String).caseInsensitiveCompare(main.font.postScriptName) == .orderedSame
        let shown = exact ? display : "\(main.font.postScriptName) (affichée en \(display))"
        let more = styles.count > 1 ? " · \(styles.count) styles" : ""
        return "  \(shown) · \(size) pt\(more)  "
    }

    /// Positions the editor so its first baseline sits on the paragraph's first baseline.
    func layoutEditor() {
        guard let pdfView, let page = pdfView.document?.page(at: layout.pageIndex),
              let layoutManager = textView.layoutManager, let container = textView.textContainer else { return }
        layoutManager.ensureLayout(for: container)
        let used = layoutManager.usedRect(for: container)
        let scale = pdfView.scaleFactor
        let width = block.lines.count == 1 ? max(used.width, block.bounds.width * scale) : geometry.width * scale
        let height = max(used.height, geometry.leading * scale)

        var baselineOffset = (textView.font?.ascender ?? geometry.leading * scale * 0.8)
        if layoutManager.numberOfGlyphs > 0 {
            let fragment = layoutManager.lineFragmentRect(forGlyphAt: 0, effectiveRange: nil)
            baselineOffset = fragment.minY + layoutManager.location(forGlyphAt: 0).y
        }
        let baseline = pdfView.convert(geometry.origin, from: page)
        textView.frame = CGRect(x: baseline.x, y: baseline.y + baselineOffset - height, width: width + 2, height: height)
        label.frame.origin = CGPoint(x: textView.frame.minX, y: textView.frame.maxY + 3)
    }

    /// The edited text as styled segments, mapped back to the original run styles.
    func segments() -> [StyledSegment] {
        guard let storage = textView.textStorage else { return [] }
        var result: [StyledSegment] = []
        var lastIndex = 0
        storage.enumerateAttribute(.folioRunStyle, in: NSRange(location: 0, length: storage.length)) { value, range, _ in
            let index = (value as? Int).flatMap { styles.indices.contains($0) ? $0 : nil } ?? lastIndex
            lastIndex = index
            let text = (storage.string as NSString).substring(with: range)
            if let last = result.last, last.style == styles[index] {
                result[result.count - 1].text += text
            } else {
                result.append(StyledSegment(text: text, style: styles[index]))
            }
        }
        return result
    }

    var hasChanges: Bool { textView.string != originalText }

    // MARK: NSTextViewDelegate

    func textDidChange(_ notification: Notification) {
        layoutEditor()
    }

    func textView(_ textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        if selector == #selector(NSResponder.cancelOperation(_:)) {
            pdfView?.endTextEditing(commit: false)
            return true
        }
        return false
    }
}

// MARK: - PDF view

extension FolioPDFView {
    /// Hover frame over the editable paragraph under the cursor.
    func updateTextHover(at location: CGPoint) {
        guard let viewer, let page = page(for: location, nearest: false), page.rotation == 0,
              let layout = viewer.textLayout(for: page) else {
            hoverOverlay.isHidden = true
            return
        }
        let point = convert(location, to: page)
        let editing = textSession.map { $0.layout.pageIndex == layout.pageIndex ? $0.blockIndex : -1 }
        guard let index = layout.blocks.firstIndex(where: { $0.isEditable && $0.bounds.insetBy(dx: -3, dy: -3).contains(point) }),
              index != editing else {
            hoverOverlay.isHidden = true
            return
        }
        if subviews.last !== hoverOverlay { addSubview(hoverOverlay, positioned: .above, relativeTo: nil) }
        hoverOverlay.show(around: convert(layout.blocks[index].bounds, from: page).insetBy(dx: -2, dy: -2), handles: false)
        NSCursor.iBeam.set()
    }

    func handleTextEditClick(_ event: NSEvent, page: PDFPage, point: CGPoint) {
        // A click outside the open editor validates it.
        if textSession != nil {
            endTextEditing(commit: true)
            return
        }
        guard let viewer else { return }
        guard page.rotation == 0 else {
            NSSound.beep()
            return
        }
        guard let layout = viewer.textLayout(for: page),
              let index = layout.blocks.firstIndex(where: { $0.bounds.insetBy(dx: -3, dy: -3).contains(point) }) else { return }
        guard layout.blocks[index].isEditable else {
            NSSound.beep()
            return
        }
        hoverOverlay.isHidden = true
        let session = TextEditSession(layout: layout, blockIndex: index, scale: scaleFactor, pdfView: self)
        textSession = session
        addSubview(session.textView, positioned: .above, relativeTo: nil)
        addSubview(session.label, positioned: .above, relativeTo: nil)
        session.layoutEditor()
        window?.makeFirstResponder(session.textView)
        let inText = session.textView.convert(event.locationInWindow, from: nil)
        session.textView.setSelectedRange(NSRange(location: session.textView.characterIndexForInsertion(at: inText), length: 0))
    }

    /// Closes the editor; when committing, rewrites the paragraph in the PDF.
    func endTextEditing(commit: Bool) {
        guard let session = textSession else { return }
        textSession = nil
        session.textView.removeFromSuperview()
        session.label.removeFromSuperview()
        window?.makeFirstResponder(self)
        guard commit, session.hasChanges, let viewer else { return }
        viewer.applyTextEdit(layout: session.layout, blockIndex: session.blockIndex, segments: session.segments())
    }
}

// MARK: - Viewer

extension ViewerModel {
    /// Paragraph layout of a page for the current document state (cached per revision).
    func textLayout(for page: PDFPage) -> PageTextLayout? {
        guard let document = pdfView.document else { return nil }
        let index = document.index(for: page)
        if let cached = textLayoutCache, cached.pageIndex == index, cached.revision == revision { return cached }
        guard let data = document.dataRepresentation(),
              let pdfium = PDFiumDocument(data: data, password: documentPassword), let pdfiumPage = pdfium.page(at: index) else { return nil }
        let layout = PageTextLayout(pageIndex: index, revision: revision, data: data, blocks: TextLayout.blocks(from: pdfiumPage.textRuns()))
        textLayoutCache = layout
        return layout
    }

    /// Rewrites a paragraph and swaps in the edited page, as one undoable action.
    func applyTextEdit(layout: PageTextLayout, blockIndex: Int, segments: [StyledSegment]) {
        do {
            let result = try TextEditor.replace(blockAt: blockIndex, onPage: layout.pageIndex, in: layout.data, password: documentPassword) { _ in segments }
            guard let edited = PDFDocument(data: result.data) else { throw TextEditError.writeFailed }
            edited.delegate = AnnotationClassProvider.shared
            if edited.isLocked, let documentPassword { edited.unlock(withPassword: documentPassword) }
            guard let page = edited.page(at: layout.pageIndex) else { throw TextEditError.writeFailed }
            retainedSources.append(edited)
            var newPages = pages
            newPages[layout.pageIndex] = page
            select(nil)
            applyPages(newPages, actionName: "Modifier le texte")
        } catch {
            let log = Self.logFailure(error, segments: segments)
            let alert = NSAlert()
            alert.messageText = "Le texte n'a pas pu être modifié"
            alert.informativeText = Self.explain(error) + " Le document n'a pas été changé."
                + (log.map { "\n\nDétails : \($0.path)" } ?? "")
            alert.runModal()
        }
    }

    /// Appends the failure to ~/Library/Logs/Folio/text-edit.log, to send with a bug report.
    private static func logFailure(_ error: Error, segments: [StyledSegment]) -> URL? {
        guard let logs = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first?
            .appendingPathComponent("Logs/Folio") else { return nil }
        try? FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        let url = logs.appendingPathComponent("text-edit.log")
        let fonts = Set(segments.map { "\($0.style.font.baseName) \(String(format: "%.1f", Double($0.style.fontSize)))pt embedded=\($0.style.font.isEmbedded)" })
        var entry = "=== \(Date.now.formatted(.iso8601))\nerror: \(error)\nfonts: \(fonts.sorted().joined(separator: "; "))\n"
        if case TextEditError.verificationFailed(let expected, let found) = error {
            let missing = TextEditor.missingCharacters(expected: expected, found: found)
            entry += "missing: \(missing.unicodeScalars.map { String(format: "U+%04X", $0.value) }.joined(separator: " "))\n"
        }
        entry += "\n"
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            handle.write(Data(entry.utf8))
            try? handle.close()
        } else {
            try? Data(entry.utf8).write(to: url)
        }
        return url
    }

    private static func explain(_ error: Error) -> String {
        switch error {
        case TextEditError.verificationFailed(let expected, let found):
            let missing = TextEditor.missingCharacters(expected: expected, found: found)
            return missing.isEmpty
                ? "Folio n'a pas pu écrire ce texte fidèlement avec les polices disponibles."
                : "Aucune police disponible ne contient ces caractères : « \(missing.map(String.init).joined(separator: " ")) »."
        case PDFFileError.unsupported(let what) where what.contains("chiffré"):
            return "La modification du texte n'est pas encore possible sur un document protégé par mot de passe."
        case TextEditError.notEditable:
            return "Ce paragraphe ne peut pas être modifié (texte pivoté ou imbriqué)."
        case is TextSurgeryError, is PDFFileError:
            return "Ce PDF utilise une structure que Folio ne sait pas encore modifier."
        default:
            return "Erreur inattendue (\(error))."
        }
    }
}
