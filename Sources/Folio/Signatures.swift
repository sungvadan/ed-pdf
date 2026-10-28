import AppKit
import Observation
import PDFKit
import SwiftUI

/// A saved signature: either strokes drawn in Folio (y-up space, bounding box at the origin)
/// or a transparent PNG extracted from paper.
struct Signature: Codable, Identifiable, Equatable {
    var id = UUID()
    var strokes: [[CGPoint]]
    var imagePNG: Data?

    var image: NSImage? { imagePNG.flatMap(NSImage.init(data:)) }

    init(imagePNG: Data) {
        strokes = []
        self.imagePNG = imagePNG
    }

    var size: CGSize {
        if let image { return image.size }
        let all = strokes.flatMap { $0 }
        return CGSize(width: all.map(\.x).max() ?? 0, height: all.map(\.y).max() ?? 0)
    }

    /// Normalizes strokes drawn in a y-down view space.
    init(viewStrokes: [[CGPoint]]) {
        let all = viewStrokes.flatMap { $0 }
        let minX = all.map(\.x).min() ?? 0, maxY = all.map(\.y).max() ?? 0
        strokes = viewStrokes.map { $0.map { CGPoint(x: $0.x - minX, y: maxY - $0.y) } }
    }

    func preview(height: CGFloat = 24) -> NSImage {
        let size = self.size
        let scale = height / max(size.height, 1)
        let imageSize = CGSize(width: max(size.width * scale, 1) + 4, height: height + 4)
        let source = self.image
        let image = NSImage(size: imageSize, flipped: false) { rect in
            if let source {
                source.draw(in: rect.insetBy(dx: 2, dy: 2))
                return true
            }
            NSColor.labelColor.setStroke()
            for stroke in strokes {
                let path = NSBezierPath()
                path.lineWidth = 1.2
                path.lineCapStyle = .round
                path.lineJoinStyle = .round
                for (i, p) in stroke.enumerated() {
                    let point = CGPoint(x: p.x * scale + 2, y: p.y * scale + 2)
                    if i == 0 { path.move(to: point) } else { path.line(to: point) }
                }
                path.stroke()
            }
            return true
        }
        image.isTemplate = true
        return image
    }
}

/// Saved signatures, persisted in user defaults.
@MainActor
@Observable
final class SignatureStore {
    static let shared = SignatureStore()
    private static let defaultsKey = "savedSignatures"

    private(set) var signatures: [Signature] = []

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.defaultsKey),
           let saved = try? JSONDecoder().decode([Signature].self, from: data) {
            signatures = saved
        }
    }

    func add(_ signature: Signature) {
        signatures.append(signature)
        save()
    }

    func remove(_ signature: Signature) {
        signatures.removeAll { $0.id == signature.id }
        save()
    }

    private func save() {
        if let data = try? JSONEncoder().encode(signatures) {
            UserDefaults.standard.set(data, forKey: Self.defaultsKey)
        }
    }
}

/// Sheet where the user draws a new signature.
struct SignaturePad: View {
    let onDone: (Signature) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var strokes: [[CGPoint]] = []
    @State private var current: [CGPoint] = []

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Nouvelle signature").font(.headline)
            Text("Signez dans le cadre avec la souris ou le trackpad.")
                .foregroundStyle(.secondary)

            Canvas { context, _ in
                for stroke in strokes + [current] where !stroke.isEmpty {
                    var path = Path()
                    path.addLines(stroke)
                    context.stroke(path, with: .color(.primary), style: StrokeStyle(lineWidth: 2.5, lineCap: .round, lineJoin: .round))
                }
            }
            .frame(width: 520, height: 200)
            .background(Color(nsColor: .textBackgroundColor))
            .overlay(alignment: .bottom) {
                Rectangle().fill(.secondary.opacity(0.4)).frame(height: 1).padding(.horizontal, 30).padding(.bottom, 50)
            }
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(.separator))
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { current.append($0.location) }
                    .onEnded { _ in
                        strokes.append(current)
                        current = []
                    }
            )

            HStack {
                Button("Effacer") { strokes = [] }
                    .disabled(strokes.isEmpty)
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Enregistrer et placer") {
                    onDone(Signature(viewStrokes: strokes))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(strokes.isEmpty)
            }
        }
        .padding(20)
    }
}

/// Text stamps built on styled free text boxes.
enum Stamp: CaseIterable, Identifiable {
    case approved, rejected, confidential, draft, paid, date

    var id: Self { self }

    var text: String {
        switch self {
        case .approved: tr("APPROUVÉ")
        case .rejected: tr("REJETÉ")
        case .confidential: tr("CONFIDENTIEL")
        case .draft: tr("BROUILLON")
        case .paid: tr("PAYÉ")
        case .date: Date.now.formatted(date: .numeric, time: .omitted)
        }
    }

    var label: String { self == .date ? tr("Date du jour (%@)", text) : text.capitalized }

    var color: NSColor {
        switch self {
        case .approved, .paid: .systemGreen
        case .rejected, .confidential: .systemRed
        case .draft: .systemOrange
        case .date: .systemBlue
        }
    }
}

/// A stamp defined by the user.
struct CustomStamp: Codable, Identifiable, Equatable {
    var id = UUID()
    var text: String
    /// sRGB components, alpha last.
    var rgba: [CGFloat]

    init(text: String, color: NSColor) {
        self.text = text
        let c = color.usingColorSpace(.sRGB) ?? .systemRed
        rgba = [c.redComponent, c.greenComponent, c.blueComponent, c.alphaComponent]
    }

    var color: NSColor {
        guard rgba.count == 4 else { return .systemRed }
        return NSColor(srgbRed: rgba[0], green: rgba[1], blue: rgba[2], alpha: rgba[3])
    }
}

/// Saved custom stamps, persisted in user defaults.
@MainActor
@Observable
final class CustomStampStore {
    static let shared = CustomStampStore()
    private static let defaultsKey = "customStamps"

    private(set) var stamps: [CustomStamp] = []

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.defaultsKey),
           let saved = try? JSONDecoder().decode([CustomStamp].self, from: data) {
            stamps = saved
        }
    }

    func add(_ stamp: CustomStamp) {
        stamps.append(stamp)
        save()
    }

    func remove(_ stamp: CustomStamp) {
        stamps.removeAll { $0.id == stamp.id }
        save()
    }

    private func save() {
        if let data = try? JSONEncoder().encode(stamps) {
            UserDefaults.standard.set(data, forKey: Self.defaultsKey)
        }
    }
}

/// Sheet where the user creates a custom stamp.
struct CustomStampEditor: View {
    let onDone: (CustomStamp) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var color = Color.red

    private var trimmed: String { text.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Nouveau tampon").font(.headline)

            Form {
                TextField("Texte", text: $text, prompt: Text("Ex. : VU ET VÉRIFIÉ"))
                ColorPicker("Couleur", selection: $color, supportsOpacity: false)
            }

            Text(trimmed.isEmpty ? tr("APERÇU") : trimmed.uppercased())
                .font(.custom("Helvetica-Bold", size: 20))
                .foregroundStyle(color)
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .overlay(Rectangle().stroke(color, lineWidth: 2.5))
                .frame(maxWidth: .infinity)
                .opacity(trimmed.isEmpty ? 0.3 : 1)

            HStack {
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Enregistrer et placer") {
                    onDone(CustomStamp(text: trimmed.uppercased(), color: NSColor(color)))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(trimmed.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 380)
    }
}

// MARK: - Placing signatures, stamps and images

extension ViewerModel {
    /// Center of the visible part of the current page, in page space.
    private var visibleCenter: (PDFPage, CGPoint)? {
        let center = CGPoint(x: pdfView.bounds.midX, y: pdfView.bounds.midY)
        guard let page = pdfView.page(for: center, nearest: true) else { return nil }
        return (page, pdfView.convert(center, to: page))
    }

    private func place(_ annotation: PDFAnnotation, on page: PDFPage, actionName: String) {
        add(annotation, to: page, actionName: actionName)
        tool = .select
        select(annotation)
    }

    func placeSignature(_ signature: Signature, width: CGFloat = 180) {
        guard let (page, center) = visibleCenter else { return }
        let size = signature.size
        if let image = signature.image {
            let height = width * size.height / max(size.width, 1)
            let rect = CGRect(x: center.x - width / 2, y: center.y - height / 2, width: width, height: height)
            place(ImageAnnotation(image: image, bounds: rect), on: page, actionName: "Signature")
            return
        }
        let scale = width / max(size.width, 1)
        let lineWidth: CGFloat = 1.5
        let rect = CGRect(
            x: center.x - size.width * scale / 2, y: center.y - size.height * scale / 2,
            width: size.width * scale, height: size.height * scale
        ).insetBy(dx: -lineWidth, dy: -lineWidth)

        let annotation = PDFAnnotation(bounds: rect, forType: .ink, withProperties: nil)
        for stroke in signature.strokes {
            let path = NSBezierPath()
            path.lineCapStyle = .round
            path.lineJoinStyle = .round
            for (i, p) in stroke.enumerated() {
                let point = CGPoint(x: p.x * scale + lineWidth, y: p.y * scale + lineWidth)
                if i == 0 { path.move(to: point) } else { path.line(to: point) }
            }
            if stroke.count == 1 { path.line(to: path.currentPoint) }
            annotation.add(path)
        }
        annotation.color = .black
        annotation.border = AnnotationFactory.border(lineWidth)
        place(annotation, on: page, actionName: "Signature")
    }

    func placeStamp(_ stamp: Stamp) {
        placeStamp(text: stamp.text, color: stamp.color)
    }

    func placeStamp(text: String, color: NSColor) {
        guard let (page, center) = visibleCenter else { return }
        let annotation = FreeTextAnnotation(bounds: .zero, forType: .freeText, withProperties: nil)
        annotation.adoptFolioStyle()
        annotation.contents = text
        annotation.font = NSFont(name: "Helvetica-Bold", size: 20) ?? .boldSystemFont(ofSize: 20)
        annotation.fontColor = color
        annotation.color = .clear
        annotation.alignment = .center
        annotation.border = AnnotationFactory.border(2.5)
        let width = annotation.attributedText.size().width + FreeTextAnnotation.padding * 2 + 20
        annotation.bounds = CGRect(x: center.x - width / 2, y: center.y, width: width, height: 0)
        annotation.fitFreeTextHeight()
        place(annotation, on: page, actionName: "Tampon")
    }

    func chooseImageToPlace() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.message = tr("Choisissez une image à placer sur la page")
        panel.prompt = tr("Placer")
        let handler: (NSApplication.ModalResponse) -> Void = { [weak self] response in
            guard response == .OK, let url = panel.url, let image = NSImage(contentsOf: url) else { return }
            self?.placeImage(image)
        }
        if let window = pdfView.window {
            panel.beginSheetModal(for: window, completionHandler: handler)
        } else {
            handler(panel.runModal())
        }
    }

    func placeImage(_ image: NSImage, maxSide: CGFloat = 200) {
        guard let (page, center) = visibleCenter, image.size.width > 0, image.size.height > 0 else { return }
        let scale = maxSide / max(image.size.width, image.size.height)
        let size = CGSize(width: image.size.width * scale, height: image.size.height * scale)
        let rect = CGRect(x: center.x - size.width / 2, y: center.y - size.height / 2, width: size.width, height: size.height)
        place(ImageAnnotation(image: image, bounds: rect), on: page, actionName: "Image")
    }
}
