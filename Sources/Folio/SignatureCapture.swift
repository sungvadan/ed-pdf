import AVFoundation
import AppKit
import CoreImage
import Observation
import SwiftUI
import UniformTypeIdentifiers

/// Extracts the ink of a signature written on paper into a transparent image.
enum SignatureExtractor {
    static let maxSide: CGFloat = 1600

    /// `sensitivity` in 0...1: higher keeps lighter strokes (and more noise).
    static func extract(from image: NSImage, sensitivity: Double, color: NSColor) -> NSImage? {
        guard let source = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }

        // Grayscale, downscaled, and a heavily blurred copy approximating the paper under each pixel,
        // so shadows and uneven lighting do not count as ink.
        var input = CIImage(cgImage: source)
        let scale = min(1, maxSide / max(input.extent.width, input.extent.height))
        input = input.applyingFilter("CILanczosScaleTransform", parameters: [kCIInputScaleKey: scale])
            .applyingFilter("CIColorControls", parameters: [kCIInputSaturationKey: 0])
        let extent = input.extent.integral
        let radius = max(extent.width, extent.height) / 30
        let paper = input.clampedToExtent()
            .applyingFilter("CIGaussianBlur", parameters: [kCIInputRadiusKey: radius])
            .cropped(to: extent)

        let width = Int(extent.width), height = Int(extent.height)
        guard width > 0, height > 0 else { return nil }
        let ink = render(input, extent: extent), background = render(paper, extent: extent)

        // A pixel is ink when noticeably darker than the paper around it; edges fade smoothly.
        let threshold = 0.95 - 0.35 * (1 - sensitivity)
        let softness = 0.12
        let rgb = (color.usingColorSpace(.sRGB) ?? .black)
        let (r, g, b) = (rgb.redComponent, rgb.greenComponent, rgb.blueComponent)

        var output = [UInt8](repeating: 0, count: width * height * 4)
        var minX = width, minY = height, maxX = -1, maxY = -1
        for y in 0..<height {
            for x in 0..<width {
                let i = (y * width + x) * 4
                let ratio = Double(ink[i]) / max(Double(background[i]), 1)
                let alpha = min(max((threshold - ratio) / softness, 0), 1)
                guard alpha > 0 else { continue }
                output[i] = UInt8(r * alpha * 255)
                output[i + 1] = UInt8(g * alpha * 255)
                output[i + 2] = UInt8(b * alpha * 255)
                output[i + 3] = UInt8(alpha * 255)
                if alpha > 0.3 {
                    minX = min(minX, x); maxX = max(maxX, x)
                    minY = min(minY, y); maxY = max(maxY, y)
                }
            }
        }
        guard maxX >= minX, maxY >= minY else { return nil }

        guard let provider = CGDataProvider(data: Data(output) as CFData),
              let full = CGImage(
                  width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                  bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                  provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent
              ) else { return nil }

        // Rows come out top-down, matching CGImage cropping coordinates.
        let pad = 6
        let crop = CGRect(x: minX - pad, y: minY - pad, width: maxX - minX + 2 * pad, height: maxY - minY + 2 * pad)
            .intersection(CGRect(x: 0, y: 0, width: width, height: height))
        guard let cropped = full.cropping(to: crop) else { return nil }
        return NSImage(cgImage: cropped, size: CGSize(width: cropped.width, height: cropped.height))
    }

    private static let context = CIContext(options: [.workingColorSpace: NSNull()])

    /// RGBA8 pixels, top row first.
    private static func render(_ image: CIImage, extent: CGRect) -> [UInt8] {
        let width = Int(extent.width), height = Int(extent.height)
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        context.render(image, toBitmap: &pixels, rowBytes: width * 4, bounds: extent, format: .RGBA8, colorSpace: nil)
        return pixels
    }
}

// MARK: - Camera

@MainActor
@Observable
final class CameraController: NSObject {
    let session = AVCaptureSession()
    private let output = AVCapturePhotoOutput()
    private var onPhoto: ((NSImage) -> Void)?
    private(set) var isReady = false
    private(set) var message: String?

    func start() async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined:
            guard await AVCaptureDevice.requestAccess(for: .video) else { return deny() }
        case .authorized:
            break
        default:
            return deny()
        }
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device) else {
            message = "Aucune caméra disponible. Choisissez plutôt une image."
            return
        }
        session.beginConfiguration()
        session.sessionPreset = .photo
        if session.canAddInput(input) { session.addInput(input) }
        if session.canAddOutput(output) { session.addOutput(output) }
        session.commitConfiguration()
        let session = self.session
        await Task.detached { session.startRunning() }.value
        isReady = true
    }

    func stop() {
        let session = self.session
        Task.detached { session.stopRunning() }
        isReady = false
    }

    func capture(_ completion: @escaping (NSImage) -> Void) {
        onPhoto = completion
        output.capturePhoto(with: AVCapturePhotoSettings(), delegate: self)
    }

    private func deny() {
        message = "L'accès à la caméra est refusé. Autorisez Folio dans Réglages Système › Confidentialité et sécurité › Caméra, ou choisissez une image."
    }
}

extension CameraController: AVCapturePhotoCaptureDelegate {
    nonisolated func photoOutput(_ output: AVCapturePhotoOutput, didFinishProcessingPhoto photo: AVCapturePhoto, error: Error?) {
        guard let data = photo.fileDataRepresentation(), let image = NSImage(data: data) else { return }
        Task { @MainActor in
            self.onPhoto?(image)
            self.onPhoto = nil
        }
    }
}

private struct CameraPreview: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspect
        // Show the paper as it really reads, not mirrored like a selfie.
        if let connection = layer.connection, connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = false
        }
        view.layer = layer
        view.wantsLayer = true
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {}
}

// MARK: - Sheet

/// Captures a signature from the camera, an image file or an iPhone, and extracts the ink.
struct SignatureCaptureSheet: View {
    let onDone: (Signature) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var camera = CameraController()
    @State private var photo: NSImage?
    @State private var extracted: NSImage?
    @State private var sensitivity = 0.5
    @State private var inkColor = InkColor.black
    @State private var isProcessing = false

    enum InkColor: String, CaseIterable, Identifiable {
        case black = "Noir", blue = "Bleu"
        var id: Self { self }
        var color: NSColor { self == .black ? .black : NSColor(srgbRed: 0.05, green: 0.15, blue: 0.55, alpha: 1) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Signature depuis le papier").font(.headline)

            if photo == nil {
                capture
            } else {
                review
            }
        }
        .padding(20)
        .frame(width: 560)
        .importsItemProviders([.image]) { providers in
            guard let provider = providers.first else { return false }
            _ = provider.loadDataRepresentation(for: .image) { data, _ in
                guard let data, let image = NSImage(data: data) else { return }
                Task { @MainActor in use(image) }
            }
            return true
        }
        .task { await camera.start() }
        .onDisappear { camera.stop() }
        .task(id: TaskKey(photo: photo.map(ObjectIdentifier.init), sensitivity: sensitivity, color: inkColor)) {
            await process()
        }
    }

    private var capture: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Signez sur une feuille blanche, puis présentez-la à la caméra en la tenant bien droite.")
                .foregroundStyle(.secondary)

            ZStack {
                Color.black
                if camera.isReady {
                    CameraPreview(session: camera.session)
                } else if let message = camera.message {
                    Text(message).foregroundStyle(.white).multilineTextAlignment(.center).padding()
                } else {
                    ProgressView().controlSize(.large)
                }
            }
            .frame(height: 300)
            .clipShape(RoundedRectangle(cornerRadius: 8))

            Text("Astuce : menu Fichier › « Importer depuis l'iPhone » › Prendre une photo, pour utiliser votre iPhone.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Button("Choisir une image…", action: chooseFile)
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Prendre la photo") { camera.capture(use) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!camera.isReady)
            }
        }
    }

    private var review: some View {
        VStack(alignment: .leading, spacing: 12) {
            ZStack {
                Color.white
                if let extracted {
                    Image(nsImage: extracted)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .padding()
                } else if !isProcessing {
                    Text("Aucune signature détectée. Augmentez la sensibilité ou reprenez la photo.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding()
                }
                if isProcessing { ProgressView() }
            }
            .frame(height: 220)
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(.separator))

            LabeledContent("Sensibilité") {
                Slider(value: $sensitivity, in: 0...1) {
                    EmptyView()
                } minimumValueLabel: {
                    Text("Moins")
                } maximumValueLabel: {
                    Text("Plus")
                }
            }
            Picker("Couleur de l'encre", selection: $inkColor) {
                ForEach(InkColor.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)

            HStack {
                Button("Reprendre") {
                    photo = nil
                    extracted = nil
                    Task { await camera.start() }
                }
                Spacer()
                Button("Annuler", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Enregistrer et placer") {
                    guard let png = extracted?.pngData else { return }
                    onDone(Signature(imagePNG: png))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(extracted == nil || isProcessing)
            }
        }
    }

    private struct TaskKey: Equatable {
        let photo: ObjectIdentifier?
        let sensitivity: Double
        let color: InkColor
    }

    private func use(_ image: NSImage) {
        camera.stop()
        photo = image
    }

    private func process() async {
        guard let photo else { return }
        // Let the slider settle before running the extraction.
        try? await Task.sleep(for: .milliseconds(150))
        guard !Task.isCancelled else { return }
        isProcessing = true
        let (sensitivity, color) = (sensitivity, inkColor.color)
        let result = await Task.detached { SignatureExtractor.extract(from: photo, sensitivity: sensitivity, color: color) }.value
        guard !Task.isCancelled else { return }
        extracted = result
        isProcessing = false
    }

    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image, .pdf]
        panel.message = "Choisissez une photo ou un scan de votre signature"
        guard panel.runModal() == .OK, let url = panel.url, let image = NSImage(contentsOf: url) else { return }
        use(image)
    }
}
