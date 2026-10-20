import AppKit
import PDFKit

/// Free text box drawn by Folio so it can carry underline / strikethrough, which plain
/// FreeText annotations lack. PDFKit bakes this drawing into the saved appearance stream,
/// and the decorations are kept in a custom `/FolioStyle` key so they stay editable.
/// Boxes without that key (created by other apps) keep PDFKit's native rendering.
final class FreeTextAnnotation: PDFAnnotation {
    static let styleKey = PDFAnnotationKey(rawValue: "/FolioStyle")
    static let padding: CGFloat = 2

    static let fontFamilies = [
        "Helvetica", "Arial", "Times New Roman", "Georgia", "Courier", "Verdana", "Avenir Next", "Menlo",
    ]

    /// Raw value of `/FolioStyle`: "none" or any of "u" (underline) and "s" (strikethrough).
    var style: String? {
        get { value(forAnnotationKey: Self.styleKey) as? String }
        set {
            if let newValue {
                setValue(newValue, forAnnotationKey: Self.styleKey)
            } else {
                removeValue(forAnnotationKey: Self.styleKey)
            }
        }
    }

    var isUnderlined: Bool {
        get { style?.contains("u") ?? false }
        set { setDecoration("u", newValue) }
    }

    var isStruckThrough: Bool {
        get { style?.contains("s") ?? false }
        set { setDecoration("s", newValue) }
    }

    var isBold: Bool { font?.fontDescriptor.symbolicTraits.contains(.bold) ?? false }
    var isItalic: Bool { font?.fontDescriptor.symbolicTraits.contains(.italic) ?? false }

    var attributedText: NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = alignment
        var attributes: [NSAttributedString.Key: Any] = [
            .font: font ?? NSFont.systemFont(ofSize: 14),
            .foregroundColor: fontColor ?? NSColor.black,
            .paragraphStyle: paragraph,
        ]
        if isUnderlined { attributes[.underlineStyle] = NSUnderlineStyle.single.rawValue }
        if isStruckThrough { attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
        return NSAttributedString(string: contents ?? "", attributes: attributes)
    }

    override func draw(with box: PDFDisplayBox, in context: CGContext) {
        guard style != nil else { return super.draw(with: box, in: context) }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        if color.alphaComponent > 0 {
            color.setFill()
            bounds.fill()
        }
        if let lineWidth = border?.lineWidth, lineWidth > 0 {
            (fontColor ?? .black).setStroke()
            let frame = NSBezierPath(rect: bounds.insetBy(dx: lineWidth / 2, dy: lineWidth / 2))
            frame.lineWidth = lineWidth
            frame.stroke()
        }
        attributedText.draw(in: bounds.insetBy(dx: Self.padding, dy: Self.padding))
        NSGraphicsContext.restoreGraphicsState()
    }

    /// Opts the box into Folio's rendering, e.g. when the user first styles it.
    func adoptFolioStyle() {
        if style == nil { style = "none" }
    }

    private func setDecoration(_ flag: Character, _ on: Bool) {
        var flags = Set((style ?? "").filter { $0 == "u" || $0 == "s" })
        if on { flags.insert(flag) } else { flags.remove(flag) }
        style = flags.isEmpty ? "none" : String(flags.sorted())
    }
}
