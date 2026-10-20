import AppKit
import Foundation
import FolioText
import PDFKit

/// textlab analyze <file.pdf> [page]
///     Prints detected paragraphs with font, size and color.
/// textlab replace <in.pdf> <page> <block> <find> <replacement> <out.pdf> [--system]
///     Rewrites one paragraph with a text substitution (styles of unchanged text kept).
/// textlab render <file.pdf> <page> <out.png> [scale]
///     Renders a page with PDFKit, to check results visually.
let arguments = CommandLine.arguments
func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

switch arguments.count >= 2 ? arguments[1] : "" {
case "replace": replace()
case "render": render()
case "analyze": break
default: fail("usage: textlab analyze|replace|render … (see source)")
}

func replace() -> Never {
    guard arguments.count >= 8, let pageNumber = Int(arguments[3]), let blockNumber = Int(arguments[4]),
          let data = FileManager.default.contents(atPath: arguments[2]),
          let document = PDFiumDocument(data: data), let page = document.page(at: pageNumber - 1) else {
        fail("usage: textlab replace <in.pdf> <page> <block> <find> <replacement> <out.pdf> [--system]")
    }
    let blocks = TextLayout.blocks(from: page.textRuns())
    guard blocks.indices.contains(blockNumber) else { fail("no block #\(blockNumber)") }
    let block = blocks[blockNumber]
    let segments = block.styledSegments
    let oldText = segments.map(\.text).joined()
    guard oldText.contains(arguments[5]) else { fail("\"\(arguments[5])\" not found in: \(oldText)") }
    let newText = oldText.replacingOccurrences(of: arguments[5], with: arguments[6])

    let writer = BlockWriter(page: page)
    writer.avoidEmbeddedFonts = arguments.contains("--system")
    do {
        try writer.replace(block, with: segments.applying(newText: newText))
    } catch {
        fail("edit failed: \(error)")
    }
    writer.fontReport.forEach { print("font:", $0) }
    guard let output = document.data() else { fail("save failed") }
    FileManager.default.createFile(atPath: arguments[7], contents: output)

    // Check: the edited text must read back identically.
    let check = PDFiumDocument(data: output)!.page(at: pageNumber - 1)!
    let found = TextLayout.blocks(from: check.textRuns()).contains { $0.text.replacingOccurrences(of: "\n", with: " ") == newText.trimmingCharacters(in: .whitespaces) }
    print(found ? "readback: OK" : "readback: MISMATCH")
    exit(found ? 0 : 2)
}

func render() -> Never {
    guard arguments.count >= 5, let pageNumber = Int(arguments[3]),
          let document = PDFDocument(url: URL(fileURLWithPath: arguments[2])), let page = document.page(at: pageNumber - 1) else {
        fail("usage: textlab render <file.pdf> <page> <out.png> [scale]")
    }
    let scale = arguments.count > 5 ? CGFloat(Double(arguments[5]) ?? 2) : 2
    let size = page.bounds(for: .cropBox).size
    let image = page.thumbnail(of: CGSize(width: size.width * scale, height: size.height * scale), for: .cropBox)
    let rep = NSBitmapImageRep(cgImage: image.cgImage(forProposedRect: nil, context: nil, hints: nil)!)
    FileManager.default.createFile(atPath: arguments[4], contents: rep.representation(using: .png, properties: [:]))
    exit(0)
}
guard let data = FileManager.default.contents(atPath: arguments[2]),
      let document = PDFiumDocument(data: data) else { fail("cannot open \(arguments[2])") }

let pages = arguments.count > 3 ? [Int(arguments[3])! - 1] : Array(0..<document.pageCount)
for index in pages {
    guard let page = document.page(at: index) else { continue }
    let runs = page.textRuns()
    let blocks = TextLayout.blocks(from: runs)
    print("== page \(index + 1): \(runs.count) runs, \(blocks.count) blocks")
    for (n, block) in blocks.enumerated() {
        let run = block.dominantRun
        let b = block.bounds
        var style = "?"
        if let r = run {
            let size = String(format: "%.1f", Double(r.fontSize))
            let color = "rgb(\(Int(r.color.red * 255)),\(Int(r.color.green * 255)),\(Int(r.color.blue * 255)))"
            var traits = ""
            if r.font.isBold { traits += " bold" }
            if r.font.isItalic { traits += " italic" }
            if r.font.isEmbedded { traits += " embedded" }
            style = "\(r.font.postScriptName) \(size)pt\(traits) \(color)"
        }
        let geometry = "[\(Int(b.minX)),\(Int(b.minY)) \(Int(b.width))×\(Int(b.height))]"
        let leading = String(format: "%.1f", Double(block.leading))
        let readOnly = block.isEditable ? "" : " (read-only)"
        print("#\(n) \(geometry) \(block.lines.count) lines, leading \(leading), \(style)\(readOnly)")
        for line in block.lines {
            print("    | \(line.text)")
        }
    }
}
