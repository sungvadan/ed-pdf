import AppKit
import PDFKit

/// Stamp annotation showing an image. The PNG is kept in a custom `/FolioImage` key so Folio
/// can redraw it after reopening; other readers use the appearance stream PDFKit bakes on save.
final class ImageAnnotation: PDFAnnotation {
    static let imageKey = PDFAnnotationKey(rawValue: "/FolioImage")
    static let maxPixels: CGFloat = 1200

    private var cachedImage: NSImage?

    var image: NSImage? {
        if cachedImage == nil, let encoded = value(forAnnotationKey: Self.imageKey) as? String,
           let data = Data(base64Encoded: encoded) {
            cachedImage = NSImage(data: data)
        }
        return cachedImage
    }

    convenience init(image: NSImage, bounds: CGRect) {
        self.init(bounds: bounds, forType: .stamp, withProperties: nil)
        let scaled = Self.downscaled(image)
        if let png = scaled.pngData {
            setValue(png.base64EncodedString(), forAnnotationKey: Self.imageKey)
        }
        cachedImage = scaled
    }

    override func draw(with box: PDFDisplayBox, in context: CGContext) {
        guard let image else { return super.draw(with: box, in: context) }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        image.draw(in: bounds)
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func downscaled(_ image: NSImage) -> NSImage {
        let size = image.size
        let scale = min(1, maxPixels / max(size.width, size.height, 1))
        guard scale < 1 else { return image }
        let target = CGSize(width: size.width * scale, height: size.height * scale)
        return NSImage(size: target, flipped: false) { rect in
            image.draw(in: rect)
            return true
        }
    }
}

extension NSImage {
    var pngData: Data? {
        guard let tiff = tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
