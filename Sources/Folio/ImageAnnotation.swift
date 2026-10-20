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

    convenience init(image: NSImage, bounds: CGRect, maxPixels: CGFloat = ImageAnnotation.maxPixels) {
        self.init(bounds: bounds, forType: .stamp, withProperties: nil)
        let scaled = Self.downscaled(image, maxPixels: maxPixels)
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

    private static func downscaled(_ image: NSImage, maxPixels: CGFloat) -> NSImage {
        let size = image.pixelSize
        let scale = min(1, maxPixels / max(size.width, size.height, 1))
        guard scale < 1 else { return image }
        // Draw into a bitmap of exact pixel size so the result does not depend on the screen scale.
        let width = Int(size.width * scale), height = Int(size.height * scale)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ) else { return image }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image.draw(in: CGRect(x: 0, y: 0, width: width, height: height))
        NSGraphicsContext.restoreGraphicsState()
        let result = NSImage(size: CGSize(width: width, height: height))
        result.addRepresentation(rep)
        return result
    }
}

extension NSImage {
    /// Size in pixels of the best bitmap representation, falling back to the point size.
    var pixelSize: CGSize {
        let pixels = representations.map { CGSize(width: $0.pixelsWide, height: $0.pixelsHigh) }
            .filter { $0.width > 0 && $0.height > 0 }
            .max { $0.width < $1.width }
        return pixels ?? size
    }

    var pngData: Data? {
        guard let tiff = tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
