import AppKit
import PDFKit

/// PDFView that routes mouse input to the active annotation tool.
final class FolioPDFView: PDFView {
    weak var viewer: ViewerModel?

    private enum Drag {
        /// A drawing in progress: the temporary annotation and the points collected so far.
        case create(PDFAnnotation, PDFPage, points: [CGPoint])
        case move(PDFAnnotation, start: CGPoint, original: AnnotationState)
        case resize(PDFAnnotation, corner: Corner, original: AnnotationState)
    }

    private var drag: Drag?
    private let selectionOverlay = SelectionOverlay()
    private var observers: [NSObjectProtocol] = []
    /// Form values before the current edit; compared after PDFKit handles a field.
    var formSnapshot: FormState?

    override init(frame: NSRect) {
        super.init(frame: frame)
        selectionOverlay.isHidden = true
        addSubview(selectionOverlay)
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSView.boundsDidChangeNotification, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self, let clip = note.object as? NSClipView, clip.isDescendant(of: self) else { return }
                self.updateSelectionOverlay()
            }
        })
        // Text typed in form fields goes through a field editor inside this view.
        for name in [NSText.didChangeNotification, NSText.didEndEditingNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                MainActor.assumeIsolated {
                    guard let self, let view = note.object as? NSView, view.isDescendant(of: self) else { return }
                    self.commitFormChange()
                }
            })
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
            if let selected = viewer.selectedAnnotation, let corner = handleCorner(at: convert(event.locationInWindow, from: nil)) {
                drag = .resize(selected, corner: corner, original: AnnotationState(selected))
            } else if let annotation = page.annotation(at: point), annotation.subtype == "Widget" {
                viewer.select(nil)
                if formSnapshot == nil, let document { formSnapshot = FormState(document) }
                super.mouseDown(with: event)
                commitFormChange()
            } else if let annotation = page.annotation(at: point), annotation.isUserEditable {
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
        case .resize(let annotation, let corner, let original):
            guard let page = annotation.page else { return }
            let point = convert(convert(event.locationInWindow, from: nil), to: page)
            // The opposite corner stays put; the dragged corner never crosses it.
            let old = original.bounds
            let anchor = corner.opposite.point(in: old)
            var width = max(abs(point.x - anchor.x), 8), height = max(abs(point.y - anchor.y), 8)
            if AnnotationState.keepsAspectRatio(annotation) {
                let scale = max(width / max(old.width, 1), height / max(old.height, 1))
                width = old.width * scale
                height = old.height * scale
            } else if annotation.isFreeText {
                width = max(width, 30)
                height = old.height
            }
            let rect = CGRect(
                x: corner.isRight ? anchor.x : anchor.x - width,
                y: corner.isTop ? anchor.y : anchor.y - height,
                width: width, height: height
            )
            original.apply(resizedTo: rect, to: annotation)
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
        case .resize(let annotation, _, let original):
            if annotation.bounds != original.bounds {
                viewer?.registerChange(of: annotation, from: original, actionName: "Redimensionner")
            }
        }
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        guard let tool = viewer?.tool else { return }
        if tool == .select, let corner = handleCorner(at: convert(event.locationInWindow, from: nil)) {
            corner.cursor.set()
        } else if tool.isDrawing || tool == .note || tool == .text {
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

    /// Compares form values with the last snapshot and records any edit for undo.
    func commitFormChange() {
        guard let document, let viewer else { return }
        let current = FormState(document)
        guard let before = formSnapshot else {
            formSnapshot = current
            return
        }
        if current != before {
            viewer.registerFormChange(from: before, to: current)
            formSnapshot = current
        }
    }

    /// The resize handle of the selected annotation under `location` (view space), if any.
    private func handleCorner(at location: CGPoint) -> Corner? {
        guard let annotation = viewer?.selectedAnnotation, annotation.isMovable, let page = annotation.page else { return nil }
        let rect = convert(annotation.bounds, from: page)
        let hit = SelectionOverlay.handleHitSize
        return Corner.allCases.first { corner in
            let p = corner.point(in: rect)
            return CGRect(x: p.x - hit / 2, y: p.y - hit / 2, width: hit, height: hit).contains(location)
        }
    }

    func updateSelectionOverlay() {
        guard let annotation = viewer?.selectedAnnotation, let page = annotation.page else {
            selectionOverlay.isHidden = true
            return
        }
        if subviews.last !== selectionOverlay {
            addSubview(selectionOverlay, positioned: .above, relativeTo: nil)
        }
        selectionOverlay.show(around: convert(annotation.bounds, from: page), handles: annotation.isMovable)
    }

    private func pagePoint(for event: NSEvent) -> (PDFPage, CGPoint)? {
        let location = convert(event.locationInWindow, from: nil)
        guard let page = page(for: location, nearest: false) else { return nil }
        return (page, convert(location, to: page))
    }
}

enum Corner: CaseIterable {
    case bottomLeft, bottomRight, topLeft, topRight

    var isRight: Bool { self == .bottomRight || self == .topRight }
    var isTop: Bool { self == .topLeft || self == .topRight }

    var opposite: Corner {
        switch self {
        case .bottomLeft: .topRight
        case .bottomRight: .topLeft
        case .topLeft: .bottomRight
        case .topRight: .bottomLeft
        }
    }

    /// The corner of `rect` in a y-up space (page space, or this unflipped view).
    func point(in rect: CGRect) -> CGPoint {
        CGPoint(x: isRight ? rect.maxX : rect.minX, y: isTop ? rect.maxY : rect.minY)
    }

    var cursor: NSCursor {
        if #available(macOS 15.0, *) {
            let position: NSCursor.FrameResizePosition = switch self {
            case .bottomLeft: .bottomLeft
            case .bottomRight: .bottomRight
            case .topLeft: .topLeft
            case .topRight: .topRight
            }
            return .frameResize(position: position, directions: .all)
        }
        return .crosshair
    }
}

/// Dashed outline and corner handles around the selected annotation; transparent to clicks.
private final class SelectionOverlay: NSView {
    static let handleHitSize: CGFloat = 18
    private static let margin: CGFloat = 8
    private static let handleSize: CGFloat = 9

    private let border = CAShapeLayer()
    private let handles = Corner.allCases.map { _ in CALayer() }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        border.strokeColor = NSColor.controlAccentColor.cgColor
        border.fillColor = nil
        border.lineWidth = 1.5
        border.lineDashPattern = [4, 3]
        layer?.addSublayer(border)
        for handle in handles {
            handle.backgroundColor = NSColor.white.cgColor
            handle.borderColor = NSColor.controlAccentColor.cgColor
            handle.borderWidth = 1.5
            handle.cornerRadius = 2
            layer?.addSublayer(handle)
        }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    /// Positions the outline around `rect` (superview space) with handles centered on its corners.
    func show(around rect: CGRect, handles showsHandles: Bool) {
        let margin = Self.margin
        frame = rect.insetBy(dx: -margin, dy: -margin)
        let inner = CGRect(x: margin, y: margin, width: rect.width, height: rect.height)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        border.path = CGPath(rect: inner.insetBy(dx: -3, dy: -3), transform: nil)
        let size = Self.handleSize
        for (corner, handle) in zip(Corner.allCases, handles) {
            let p = corner.point(in: inner)
            handle.frame = CGRect(x: p.x - size / 2, y: p.y - size / 2, width: size, height: size)
            handle.isHidden = !showsHandles
        }
        CATransaction.commit()
        isHidden = false
    }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
