import AppKit
import PDFKit
import SwiftUI

enum AnnotationTool: String, CaseIterable, Identifiable {
    case select, editText, highlight, underline, strikeOut, note, text, ink, rectangle, ellipse, line, arrow, redact

    var id: Self { self }

    var label: String {
        switch self {
        case .select: "Sélection"
        case .highlight: "Surligner"
        case .underline: "Souligner"
        case .strikeOut: "Barrer"
        case .note: "Note"
        case .text: "Zone de texte"
        case .ink: "Dessin"
        case .rectangle: "Rectangle"
        case .ellipse: "Ellipse"
        case .line: "Ligne"
        case .arrow: "Flèche"
        case .redact: "Caviarder"
        case .editText: "Modifier le texte"
        }
    }

    var symbol: String {
        switch self {
        case .select: "cursorarrow"
        case .highlight: "highlighter"
        case .underline: "underline"
        case .strikeOut: "strikethrough"
        case .note: "note.text"
        case .text: "character.textbox"
        case .ink: "scribble"
        case .rectangle: "rectangle"
        case .ellipse: "circle"
        case .line: "line.diagonal"
        case .arrow: "arrow.up.right"
        case .redact: "rectangle.inset.filled"
        case .editText: "character.cursor.ibeam"
        }
    }

    var defaultColor: Color {
        switch self {
        case .select: .accentColor
        case .highlight, .note: .yellow
        case .underline: .blue
        case .text, .redact, .editText: .black
        case .strikeOut, .ink, .rectangle, .ellipse, .line, .arrow: .red
        }
    }

    var isTextMarkup: Bool { [.highlight, .underline, .strikeOut].contains(self) }
    var isDrawing: Bool { [.ink, .rectangle, .ellipse, .line, .arrow].contains(self) }
    var usesLineWidth: Bool { isDrawing }
    var usesColor: Bool { ![.select, .redact, .editText].contains(self) }
}

extension PDFAnnotation {
    var subtype: String { type ?? "" }
    var isTextMarkup: Bool { ["Highlight", "Underline", "StrikeOut"].contains(subtype) }
    var isFreeText: Bool { subtype == "FreeText" }
    var hasStroke: Bool { ["Ink", "Square", "Circle", "Line"].contains(subtype) && !isRedactionMark }
    var hasEditableText: Bool { ["FreeText", "Text"].contains(subtype) || isTextMarkup }
    /// Links, form widgets and popups are not handled by the annotation tools.
    var isUserEditable: Bool { !["Link", "Widget", "Popup"].contains(subtype) }
    var isMovable: Bool { isUserEditable && !isTextMarkup }

    var displayName: String {
        if isRedactionMark { return "Zone à caviarder" }
        return switch subtype {
        case "Highlight": "Surlignage"
        case "Underline": "Soulignement"
        case "StrikeOut": "Texte barré"
        case "Text": "Note"
        case "FreeText": "Zone de texte"
        case "Ink": "Dessin"
        case "Square": "Rectangle"
        case "Circle": "Ellipse"
        case "Line": "Ligne"
        case "Stamp": "Image"
        case "Widget": "Champ de formulaire"
        default: subtype
        }
    }

    /// Forces PDFKit to redraw the annotation after a geometry change.
    func refresh() {
        guard let page else { return }
        page.removeAnnotation(self)
        page.addAnnotation(self)
    }

    /// Grows or shrinks a free text box vertically to fit its contents, keeping its top edge.
    func fitFreeTextHeight() {
        guard isFreeText else { return }
        let text = (self as? FreeTextAnnotation)?.attributedText
            ?? NSAttributedString(string: contents ?? "", attributes: [.font: font ?? NSFont.systemFont(ofSize: 14)])
        let inset = FreeTextAnnotation.padding * 2
        let rect = text.boundingRect(
            with: CGSize(width: bounds.width - inset, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading]
        )
        let height = max(ceil(rect.height) + inset + 2, 20)
        bounds = CGRect(x: bounds.minX, y: bounds.maxY - height, width: bounds.width, height: height)
    }
}

/// The mutable properties of an annotation, captured for undo.
struct AnnotationState {
    var bounds: CGRect
    var color: NSColor
    var lineWidth: CGFloat?
    var contents: String?
    var font: NSFont?
    var fontColor: NSColor?
    var alignment: NSTextAlignment
    var folioStyle: String?
    var paths: [NSBezierPath]?
    var startPoint: CGPoint
    var endPoint: CGPoint

    init(_ annotation: PDFAnnotation) {
        bounds = annotation.bounds
        color = annotation.color
        lineWidth = annotation.border?.lineWidth
        contents = annotation.contents
        font = annotation.font
        fontColor = annotation.fontColor
        alignment = annotation.alignment
        folioStyle = (annotation as? FreeTextAnnotation)?.style
        paths = annotation.paths?.compactMap { $0.copy() as? NSBezierPath }
        startPoint = annotation.startPoint
        endPoint = annotation.endPoint
    }

    func apply(to annotation: PDFAnnotation) {
        annotation.bounds = bounds
        annotation.color = color
        if let lineWidth, let border = annotation.border {
            border.lineWidth = lineWidth
            annotation.border = border
        }
        annotation.contents = contents
        if annotation.isFreeText {
            annotation.font = font
            annotation.fontColor = fontColor
            annotation.alignment = alignment
            (annotation as? FreeTextAnnotation)?.style = folioStyle
        }
        if annotation.subtype == "Ink", let paths {
            annotation.paths?.forEach { annotation.remove($0) }
            paths.forEach { annotation.add($0.copy() as! NSBezierPath) }
        }
        if annotation.subtype == "Line" {
            annotation.startPoint = startPoint
            annotation.endPoint = endPoint
        }
    }

    /// Applies this state scaled into `rect` (top-left anchored), redrawing ink and lines to fit.
    func apply(resizedTo rect: CGRect, to annotation: PDFAnnotation) {
        let sx = rect.width / max(bounds.width, 1), sy = rect.height / max(bounds.height, 1)
        let transform = AffineTransform(scaleByX: sx, byY: sy)
        annotation.bounds = rect
        switch annotation.subtype {
        case "Ink":
            annotation.paths?.forEach { annotation.remove($0) }
            for path in paths ?? [] {
                let scaled = path.copy() as! NSBezierPath
                scaled.transform(using: transform)
                annotation.add(scaled)
            }
        case "Line":
            annotation.startPoint = CGPoint(x: startPoint.x * sx, y: startPoint.y * sy)
            annotation.endPoint = CGPoint(x: endPoint.x * sx, y: endPoint.y * sy)
        case "FreeText":
            annotation.fitFreeTextHeight()
        default:
            break
        }
    }

    /// Ink drawings and images keep their aspect ratio when resized.
    static func keepsAspectRatio(_ annotation: PDFAnnotation) -> Bool {
        ["Ink", "Stamp"].contains(annotation.subtype)
    }
}

/// Builds annotations for the annotation tools.
enum AnnotationFactory {
    static func border(_ width: CGFloat) -> PDFBorder {
        let border = PDFBorder()
        border.lineWidth = width
        return border
    }

    static func markups(for selection: PDFSelection, tool: AnnotationTool, color: NSColor) -> [(PDFAnnotation, PDFPage)] {
        let subtype: PDFAnnotationSubtype = switch tool {
        case .underline: .underline
        case .strikeOut: .strikeOut
        default: .highlight
        }
        return selection.selectionsByLine().flatMap { line in
            line.pages.compactMap { page -> (PDFAnnotation, PDFPage)? in
                let bounds = line.bounds(for: page)
                guard bounds.width > 0, bounds.height > 0 else { return nil }
                let annotation = PDFAnnotation(bounds: bounds, forType: subtype, withProperties: nil)
                annotation.color = subtype == .highlight ? color.withAlphaComponent(0.5) : color
                let w = bounds.width, h = bounds.height
                annotation.quadrilateralPoints = [
                    NSPoint(x: 0, y: h), NSPoint(x: w, y: h), NSPoint(x: 0, y: 0), NSPoint(x: w, y: 0),
                ].map { NSValue(point: $0) }
                return (annotation, page)
            }
        }
    }

    static func point(_ tool: AnnotationTool, at p: CGPoint, color: NSColor) -> PDFAnnotation? {
        switch tool {
        case .note:
            let annotation = PDFAnnotation(bounds: CGRect(x: p.x - 12, y: p.y - 12, width: 24, height: 24), forType: .text, withProperties: nil)
            annotation.iconType = .comment
            annotation.color = color
            annotation.contents = ""
            return annotation
        case .text:
            let annotation = FreeTextAnnotation(bounds: CGRect(x: p.x, y: p.y - 22, width: 200, height: 22), forType: .freeText, withProperties: nil)
            annotation.adoptFolioStyle()
            annotation.contents = "Texte"
            annotation.font = NSFont(name: "Helvetica", size: 14) ?? .systemFont(ofSize: 14)
            annotation.fontColor = color
            annotation.color = .clear
            annotation.border = border(0)
            annotation.fitFreeTextHeight()
            return annotation
        default:
            return nil
        }
    }

    /// `points` holds every point for ink, or the start and current points for shapes.
    static func drawing(_ tool: AnnotationTool, points: [CGPoint], color: NSColor, lineWidth: CGFloat) -> PDFAnnotation? {
        guard let first = points.first, let last = points.last else { return nil }
        let annotation: PDFAnnotation
        switch tool {
        case .ink:
            let rect = boundingRect(points).insetBy(dx: -lineWidth, dy: -lineWidth)
            annotation = PDFAnnotation(bounds: rect, forType: .ink, withProperties: nil)
            let path = NSBezierPath()
            path.move(to: offset(first, by: rect.origin))
            for p in points.dropFirst() { path.line(to: offset(p, by: rect.origin)) }
            if points.count == 1 { path.line(to: offset(first, by: rect.origin)) }
            path.lineCapStyle = .round
            path.lineJoinStyle = .round
            annotation.add(path)
        case .redact:
            return Redactor.mark(bounds: boundingRect([first, last]))
        case .rectangle, .ellipse:
            let rect = boundingRect([first, last])
            annotation = PDFAnnotation(bounds: rect, forType: tool == .rectangle ? .square : .circle, withProperties: nil)
        case .line, .arrow:
            let rect = boundingRect([first, last]).insetBy(dx: -(lineWidth * 4 + 6), dy: -(lineWidth * 4 + 6))
            annotation = PDFAnnotation(bounds: rect, forType: .line, withProperties: nil)
            annotation.startPoint = offset(first, by: rect.origin)
            annotation.endPoint = offset(last, by: rect.origin)
            annotation.endLineStyle = tool == .arrow ? .openArrow : .none
        default:
            return nil
        }
        annotation.color = color
        annotation.border = border(lineWidth)
        return annotation
    }

    static func boundingRect(_ points: [CGPoint]) -> CGRect {
        let xs = points.map(\.x), ys = points.map(\.y)
        let minX = xs.min() ?? 0, minY = ys.min() ?? 0
        return CGRect(x: minX, y: minY, width: (xs.max() ?? 0) - minX, height: (ys.max() ?? 0) - minY)
    }

    private static func offset(_ p: CGPoint, by origin: CGPoint) -> CGPoint {
        CGPoint(x: p.x - origin.x, y: p.y - origin.y)
    }
}

// MARK: - Editing with undo

extension ViewerModel {
    func select(_ annotation: PDFAnnotation?) {
        selectedAnnotation = annotation
        pdfView.updateSelectionOverlay()
    }

    func add(_ annotation: PDFAnnotation, to page: PDFPage, actionName: String = "Ajouter une annotation") {
        annotation.userName = AppSettings.authorName
        page.addAnnotation(annotation)
        revision += 1
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.remove(annotation, actionName: actionName) }
        }
        undoManager?.setActionName(actionName)
    }

    func remove(_ annotation: PDFAnnotation, actionName: String = "Supprimer l'annotation") {
        guard let page = annotation.page else { return }
        if selectedAnnotation === annotation { select(nil) }
        page.removeAnnotation(annotation)
        revision += 1
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated { model.add(annotation, to: page, actionName: actionName) }
        }
        undoManager?.setActionName(actionName)
    }

    func deleteSelection() {
        if let selectedAnnotation { remove(selectedAnnotation) }
    }

    /// Applies `change` to the annotation as one undoable action.
    func update(_ annotation: PDFAnnotation, actionName: String, _ change: (PDFAnnotation) -> Void) {
        let old = AnnotationState(annotation)
        change(annotation)
        annotation.fitFreeTextHeight()
        registerChange(of: annotation, from: old, actionName: actionName)
    }

    /// Records a change already applied to the annotation, restoring `old` on undo.
    func registerChange(of annotation: PDFAnnotation, from old: AnnotationState, actionName: String) {
        annotation.refresh()
        revision += 1
        pdfView.updateSelectionOverlay()
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated {
                let current = AnnotationState(annotation)
                old.apply(to: annotation)
                model.registerChange(of: annotation, from: current, actionName: actionName)
            }
        }
        undoManager?.setActionName(actionName)
    }

    func addMarkup(for selection: PDFSelection) {
        let color = NSColor(toolColor)
        for (annotation, page) in AnnotationFactory.markups(for: selection, tool: tool, color: color) {
            add(annotation, to: page, actionName: tool.label)
        }
    }
}
