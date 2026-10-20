import AppKit
import PDFKit

/// PDFView that routes mouse input to the active annotation tool.
final class FolioPDFView: PDFView {
    weak var viewer: ViewerModel?

    private enum Drag {
        /// A drawing in progress: the temporary annotation and the points collected so far.
        case create(PDFAnnotation, PDFPage, points: [CGPoint])
        case move(PDFAnnotation, start: CGPoint, original: AnnotationState)
    }

    private var drag: Drag?
    private let selectionOverlay = SelectionOverlay()
    private var clipObserver: NSObjectProtocol?

    override init(frame: NSRect) {
        super.init(frame: frame)
        selectionOverlay.isHidden = true
        addSubview(selectionOverlay)
        clipObserver = NotificationCenter.default.addObserver(
            forName: NSView.boundsDidChangeNotification, object: nil, queue: .main
        ) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self, let clip = note.object as? NSClipView, clip.isDescendant(of: self) else { return }
                self.updateSelectionOverlay()
            }
        }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    // MARK: Mouse

    override func mouseDown(with event: NSEvent) {
        guard let viewer, let (page, point) = pagePoint(for: event) else { return super.mouseDown(with: event) }
        let tool = viewer.tool
        let color = NSColor(viewer.toolColor)

        switch tool {
        case .select:
            if let annotation = page.annotation(at: point), annotation.isUserEditable {
                window?.makeFirstResponder(self)
                viewer.select(annotation)
                if annotation.isMovable {
                    drag = .move(annotation, start: point, original: AnnotationState(annotation))
                }
            } else {
                viewer.select(nil)
                super.mouseDown(with: event)
            }
        case .highlight, .underline, .strikeOut:
            viewer.select(nil)
            super.mouseDown(with: event)
            applyMarkupFromSelection()
        case .note, .text:
            guard let annotation = AnnotationFactory.point(tool, at: point, color: color) else { return }
            viewer.add(annotation, to: page, actionName: tool.label)
            viewer.tool = .select
            viewer.select(annotation)
        case .ink, .rectangle, .ellipse, .line, .arrow:
            window?.makeFirstResponder(self)
            viewer.select(nil)
            guard let annotation = AnnotationFactory.drawing(tool, points: [point], color: color, lineWidth: viewer.lineWidth) else { return }
            page.addAnnotation(annotation)
            drag = .create(annotation, page, points: [point])
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let drag, let viewer else { return super.mouseDragged(with: event) }
        switch drag {
        case .create(let temporary, let page, var points):
            let point = convert(convert(event.locationInWindow, from: nil), to: page)
            if viewer.tool == .ink { points.append(point) } else { points = [points[0], point] }
            page.removeAnnotation(temporary)
            guard let annotation = AnnotationFactory.drawing(
                viewer.tool, points: points, color: NSColor(viewer.toolColor), lineWidth: viewer.lineWidth
            ) else { return }
            page.addAnnotation(annotation)
            self.drag = .create(annotation, page, points: points)
        case .move(let annotation, let start, let original):
            guard let page = annotation.page else { return }
            let point = convert(convert(event.locationInWindow, from: nil), to: page)
            annotation.bounds = original.bounds.offsetBy(dx: point.x - start.x, dy: point.y - start.y)
            annotation.refresh()
            updateSelectionOverlay()
        }
    }

    override func mouseUp(with event: NSEvent) {
        guard let drag else {
            super.mouseUp(with: event)
            applyMarkupFromSelection()
            return
        }
        self.drag = nil
        switch drag {
        case .create(let annotation, let page, let points):
            page.removeAnnotation(annotation)
            let extent = AnnotationFactory.boundingRect(points)
            // Ink keeps single dots; shapes need a real drag.
            if viewer?.tool == .ink || extent.width + extent.height >= 4 {
                viewer?.add(annotation, to: page, actionName: viewer?.tool.label ?? "Dessin")
            }
        case .move(let annotation, _, let original):
            if annotation.bounds != original.bounds {
                viewer?.registerChange(of: annotation, from: original, actionName: "Déplacer")
            }
        }
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        guard let tool = viewer?.tool else { return }
        if tool.isDrawing || tool == .note || tool == .text {
            NSCursor.crosshair.set()
        } else if tool.isTextMarkup {
            NSCursor.iBeam.set()
        }
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 51 where viewer?.selectedAnnotation != nil, 117 where viewer?.selectedAnnotation != nil: // delete, forward delete
            viewer?.deleteSelection()
        case 53: // escape
            viewer?.tool = .select
            viewer?.select(nil)
        default:
            super.keyDown(with: event)
        }
    }

    // MARK: Helpers

    func applyMarkupFromSelection() {
        guard let viewer, viewer.tool.isTextMarkup, let selection = currentSelection,
              !(selection.string ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        viewer.addMarkup(for: selection)
        clearSelection()
    }

    func updateSelectionOverlay() {
        guard let annotation = viewer?.selectedAnnotation, let page = annotation.page else {
            selectionOverlay.isHidden = true
            return
        }
        if subviews.last !== selectionOverlay {
            addSubview(selectionOverlay, positioned: .above, relativeTo: nil)
        }
        selectionOverlay.frame = convert(annotation.bounds, from: page).insetBy(dx: -3, dy: -3)
        selectionOverlay.isHidden = false
    }

    private func pagePoint(for event: NSEvent) -> (PDFPage, CGPoint)? {
        let location = convert(event.locationInWindow, from: nil)
        guard let page = page(for: location, nearest: false) else { return nil }
        return (page, convert(location, to: page))
    }
}

/// Dashed outline drawn around the selected annotation; transparent to clicks.
private final class SelectionOverlay: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        let border = CAShapeLayer()
        border.strokeColor = NSColor.controlAccentColor.cgColor
        border.fillColor = nil
        border.lineWidth = 1.5
        border.lineDashPattern = [4, 3]
        layer?.addSublayer(border)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func layout() {
        super.layout()
        (layer?.sublayers?.first as? CAShapeLayer)?.path = CGPath(rect: bounds.insetBy(dx: 1, dy: 1), transform: nil)
    }

    override var frame: NSRect {
        didSet { needsLayout = true }
    }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
