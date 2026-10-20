// Draws the Folio app icon and writes Resources/AppIcon.icns.
// Usage: swift scripts/make-icon.swift
import AppKit

func drawIcon(size: CGFloat) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8, samplesPerPixel: 4,
        hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = size / 1024

    // Background: rounded square with a warm gradient (macOS icon grid: 824pt body, 100pt margin).
    let body = NSRect(x: 100 * s, y: 100 * s, width: 824 * s, height: 824 * s)
    let background = NSBezierPath(roundedRect: body, xRadius: 185 * s, yRadius: 185 * s)
    NSShadow().apply { $0.shadowColor = .black.withAlphaComponent(0.3); $0.shadowBlurRadius = 20 * s; $0.shadowOffset = NSSize(width: 0, height: -8 * s) }
    NSGradient(starting: NSColor(srgbRed: 0.98, green: 0.36, blue: 0.27, alpha: 1),
               ending: NSColor(srgbRed: 0.80, green: 0.10, blue: 0.25, alpha: 1))!.draw(in: background, angle: -90)
    NSShadow().set()

    // Page with folded corner.
    let page = NSRect(x: 285 * s, y: 215 * s, width: 454 * s, height: 594 * s)
    let fold: CGFloat = 130 * s
    let sheet = NSBezierPath()
    sheet.move(to: NSPoint(x: page.minX, y: page.minY))
    sheet.line(to: NSPoint(x: page.maxX, y: page.minY))
    sheet.line(to: NSPoint(x: page.maxX, y: page.maxY - fold))
    sheet.line(to: NSPoint(x: page.maxX - fold, y: page.maxY))
    sheet.line(to: NSPoint(x: page.minX, y: page.maxY))
    sheet.close()
    NSShadow().apply { $0.shadowColor = .black.withAlphaComponent(0.25); $0.shadowBlurRadius = 24 * s; $0.shadowOffset = NSSize(width: 0, height: -10 * s) }
    NSColor.white.setFill()
    sheet.fill()
    NSShadow().set()
    let corner = NSBezierPath()
    corner.move(to: NSPoint(x: page.maxX, y: page.maxY - fold))
    corner.line(to: NSPoint(x: page.maxX - fold, y: page.maxY - fold))
    corner.line(to: NSPoint(x: page.maxX - fold, y: page.maxY))
    corner.close()
    NSColor(white: 0.85, alpha: 1).setFill()
    corner.fill()

    // Text lines, one highlighted.
    for (i, width) in [300.0, 340, 260, 320, 200].enumerated() {
        let y = page.maxY - fold - 60 * s - CGFloat(i) * 70 * s
        if i == 2 {
            NSColor(srgbRed: 1, green: 0.85, blue: 0.2, alpha: 0.9).setFill()
            NSBezierPath(roundedRect: NSRect(x: page.minX + 50 * s, y: y - 18 * s, width: 300 * s, height: 50 * s), xRadius: 8 * s, yRadius: 8 * s).fill()
        }
        NSColor(white: 0.55, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: page.minX + 60 * s, y: y, width: width * s, height: 16 * s), xRadius: 8 * s, yRadius: 8 * s).fill()
    }

    // Signature stroke.
    let signature = NSBezierPath()
    signature.lineWidth = 16 * s
    signature.lineCapStyle = .round
    signature.lineJoinStyle = .round
    signature.move(to: NSPoint(x: page.minX + 60 * s, y: page.minY + 80 * s))
    signature.curve(to: NSPoint(x: page.minX + 200 * s, y: page.minY + 90 * s),
                    controlPoint1: NSPoint(x: page.minX + 110 * s, y: page.minY + 200 * s),
                    controlPoint2: NSPoint(x: page.minX + 150 * s, y: page.minY - 10 * s))
    signature.curve(to: NSPoint(x: page.minX + 360 * s, y: page.minY + 110 * s),
                    controlPoint1: NSPoint(x: page.minX + 250 * s, y: page.minY + 190 * s),
                    controlPoint2: NSPoint(x: page.minX + 300 * s, y: page.minY + 20 * s))
    NSColor(srgbRed: 0.12, green: 0.2, blue: 0.6, alpha: 1).setStroke()
    signature.stroke()

    NSGraphicsContext.restoreGraphicsState()
    return rep
}

extension NSShadow {
    func apply(_ configure: (NSShadow) -> Void) {
        configure(self)
        set()
    }
}

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = scale == 1 ? "icon_\(points)x\(points).png" : "icon_\(points)x\(points)@2x.png"
        let data = drawIcon(size: CGFloat(points * scale)).representation(using: .png, properties: [:])!
        try! data.write(to: iconset.appendingPathComponent(name))
    }
}
let output = root.appendingPathComponent("Resources/AppIcon.icns")
let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", output.path]
try! task.run()
task.waitUntilExit()
print(task.terminationStatus == 0 ? "Wrote \(output.path)" : "iconutil failed")
