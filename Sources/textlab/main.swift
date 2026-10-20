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
    let segments = blocks[blockNumber].styledSegments
    let oldText = segments.map(\.text).joined()
    guard oldText.contains(arguments[5]) else { fail("\"\(arguments[5])\" not found in: \(oldText)") }
    let newText = oldText.replacingOccurrences(of: arguments[5], with: arguments[6])

    let result: TextEditor.Result
    do {
        result = try TextEditor.replace(blockAt: blockNumber, onPage: pageNumber - 1, in: data, with: { block in
            block.styledSegments.applying(newText: newText)
        }, avoidEmbeddedFonts: arguments.contains("--system"), verify: !arguments.contains("--noverify"))
    } catch {
        fail("edit failed: \(error)")
    }
    result.fontReport.forEach { print("font:", $0) }
    let output = result.data
    FileManager.default.createFile(atPath: arguments[7], contents: output)

    // Check: the old paragraph is gone (TextEditor already verified the new one).
    let check = PDFiumDocument(data: output)!.page(at: pageNumber - 1)!
    let readBack = TextLayout.blocks(from: check.textRuns()).map { TextEditor.normalized($0.text) }
    let oldGone = !readBack.contains(TextEditor.normalized(oldText))
    print("readback: new text OK", oldGone ? "| old text removed" : "| OLD TEXT STILL THERE")
    exit(oldGone ? 0 : 2)
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
        if ProcessInfo.processInfo.environment["TEXTLAB_GEOMETRY"] != nil {
            print("    alignment:", ParagraphGeometry(block: block).alignment, block.lines.map { "\(Int($0.bounds.minX))-\(String(format: "%.1f", Double($0.bounds.maxX)))" })
        }
        if ProcessInfo.processInfo.environment["TEXTLAB_RUNS"] != nil {
            for run in block.runs {
                print(String(format: "      run %.2f…%.2f origin %.2f size %.2f", Double(run.bounds.minX), Double(run.bounds.maxX), Double(run.origin.x), Double(run.fontSize)), run.font.postScriptName, run.text.debugDescription, run.followedBySpace ? "+space" : "")
            }
        }
        for line in block.lines {
            print("    | \(line.text)")
        }
    }
}
