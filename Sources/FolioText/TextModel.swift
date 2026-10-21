import CoreGraphics
import CoreText
import CPDFium
import Foundation

/// Font of a text object as described by the PDF.
public struct FontInfo: Hashable {
    /// e.g. "ABCDEF+Helvetica-Bold" (subset prefix included).
    public var baseName: String
    public var familyName: String
    public var weight: Int
    public var italicAngle: Int
    public var flags: Int
    public var isEmbedded: Bool
    /// Serif design read from the embedded font program (PDF flags are often missing).
    public var hasSerifDesign = false

    /// Base name without the "ABCDEF+" subset tag.
    public var postScriptName: String {
        let parts = baseName.split(separator: "+", maxSplits: 1)
        if parts.count == 2, parts[0].count == 6, parts[0].allSatisfy(\.isUppercase) { return String(parts[1]) }
        return baseName
    }

    private var lowerName: String { postScriptName.lowercased() }

    public var isBold: Bool {
        weight >= 600 || flags & (1 << 18) != 0 || ["bold", "black", "heavy", "semibold", "demi"].contains { lowerName.contains($0) }
    }

    public var isItalic: Bool {
        italicAngle != 0 || flags & (1 << 6) != 0 || lowerName.contains("italic") || lowerName.contains("oblique")
    }

    public var isMonospaced: Bool { flags & 1 != 0 }
    public var isSerif: Bool { flags & 2 != 0 || hasSerifDesign }
}

/// RGBA, 0...1.
public struct TextColor: Hashable {
    public var red, green, blue, alpha: CGFloat

    public static let black = TextColor(red: 0, green: 0, blue: 0, alpha: 1)
}

/// One PDF text object (a "run": same font, size and color).
public struct TextRun {
    /// Index of the object on the page, then inside nested form XObjects.
    public var path: [Int]
    public var text: String
    /// Page space.
    public var bounds: CGRect
    /// Text space to page space, including the font size scale applied by the text matrix.
    public var matrix: CGAffineTransform
    /// PDFium's text extraction puts a space (real or generated) right after this run.
    public var followedBySpace = false
    /// Font size as written in the PDF (Tf).
    public var nominalSize: CGFloat
    public var font: FontInfo
    public var color: TextColor
    public var renderMode: Int

    /// Size as it appears on the page.
    public var fontSize: CGFloat { nominalSize * hypot(matrix.c, matrix.d) }
    /// Baseline start, page space.
    public var origin: CGPoint { CGPoint(x: matrix.tx, y: matrix.ty) }
    /// Rotation of the baseline, in degrees.
    public var angle: CGFloat { atan2(matrix.b, matrix.a) * 180 / .pi }
    /// Only top-level, unrotated, visible text is editable for now.
    public var isEditable: Bool { path.count == 1 && abs(angle) < 0.5 && renderMode != 3 }
}

public struct TextLine {
    public var runs: [TextRun]
    public var bounds: CGRect
    public var baseline: CGFloat
    public var text: String
    public var fontSize: CGFloat { runs.map(\.fontSize).max() ?? 0 }
}

/// A paragraph: consecutive lines with similar size and regular spacing.
public struct TextBlock {
    public var lines: [TextLine]
    public var bounds: CGRect

    public var runs: [TextRun] { lines.flatMap(\.runs) }
    public var text: String { lines.map(\.text).joined(separator: "\n") }
    public var isEditable: Bool { runs.allSatisfy(\.isEditable) }

    /// Font, size and color covering the most characters.
    public var dominantRun: TextRun? {
        Dictionary(grouping: runs) { "\($0.font.baseName)|\(Int($0.fontSize * 10))|\($0.color)" }
            .max { a, b in a.value.reduce(0) { $0 + $1.text.count } < b.value.reduce(0) { $0 + $1.text.count } }?
            .value.first
    }

    /// Median distance between baselines, or 1.2 × size for a single line.
    public var leading: CGFloat {
        let gaps = zip(lines, lines.dropFirst()).map { $0.baseline - $1.baseline }.sorted()
        return gaps.isEmpty ? (dominantRun?.fontSize ?? 12) * 1.2 : gaps[gaps.count / 2]
    }
}

// MARK: - Reading text objects

extension PDFiumPage {
    /// Top-level text objects in page order (including whitespace-only ones): page index and matrix (Tm × CTM).
    func topLevelTextObjects() -> [(index: Int, matrix: CGAffineTransform)] {
        (0..<Int(FPDFPage_CountObjects(handle))).compactMap { index in
            guard let object = FPDFPage_GetObject(handle, Int32(index)), FPDFPageObj_GetType(object) == FPDF_PAGEOBJ_TEXT else { return nil }
            var m = FS_MATRIX()
            FPDFPageObj_GetMatrix(object, &m)
            return (index, CGAffineTransform(m))
        }
    }

    public func textRuns() -> [TextRun] {
        var runs: [TextRun] = []
        for index in 0..<Int(FPDFPage_CountObjects(handle)) {
            if let object = FPDFPage_GetObject(handle, Int32(index)) {
                collect(object, path: [index], parent: .identity, into: &runs)
            }
        }
        let spaced = objectsFollowedBySpace()
        for i in runs.indices where runs[i].path.count == 1 {
            if let object = FPDFPage_GetObject(handle, Int32(runs[i].path[0])), spaced.contains(object) {
                runs[i].followedBySpace = true
            }
        }
        return runs
    }

    /// Text objects followed, on the same line, by a word gap: the next glyph starts more than 15 % of
    /// the font size after the advance of the object's last glyph ("loose" boxes span the advance).
    /// This tells real word gaps from kerning between glyph runs or style changes ("2026" + ",").
    private func objectsFollowedBySpace() -> Set<OpaquePointer> {
        guard let textPage else { return [] }
        var result = Set<OpaquePointer>()
        var last: (object: FPDF_PAGEOBJECT, right: Float, y: Float, size: Double)?
        for index in 0..<Int(FPDFText_CountChars(textPage)) {
            let unicode = FPDFText_GetUnicode(textPage, Int32(index))
            guard unicode > 32, unicode != 0xA0, let object = FPDFText_GetTextObject(textPage, Int32(index)) else { continue }
            var box = FS_RECTF()
            guard FPDFText_GetLooseCharBox(textPage, Int32(index), &box) != 0 else { continue }
            let size = FPDFText_GetFontSize(textPage, Int32(index))
            if let previous = last, previous.object != object, abs(box.bottom - previous.y) < Float(previous.size) * 0.5,
               Double(box.left - previous.right) > 0.15 * effectiveSize(previous.object, fallback: previous.size) {
                result.insert(previous.object)
            }
            last = (object, box.right, box.bottom, size)
        }
        return result
    }

    private func effectiveSize(_ object: FPDF_PAGEOBJECT, fallback: Double) -> Double {
        var nominal: Float = 0
        FPDFTextObj_GetFontSize(object, &nominal)
        var m = FS_MATRIX()
        FPDFPageObj_GetMatrix(object, &m)
        let size = Double(nominal) * Double(hypot(m.c, m.d))
        return size > 0 ? size : fallback
    }

    private func collect(_ object: FPDF_PAGEOBJECT, path: [Int], parent: CGAffineTransform, into runs: inout [TextRun]) {
        switch FPDFPageObj_GetType(object) {
        case FPDF_PAGEOBJ_TEXT:
            if let run = makeRun(object, path: path, parent: parent) { runs.append(run) }
        case FPDF_PAGEOBJ_FORM:
            var m = FS_MATRIX()
            FPDFPageObj_GetMatrix(object, &m)
            let transform = CGAffineTransform(m).concatenating(parent)
            for index in 0..<Int(FPDFFormObj_CountObjects(object)) {
                if let child = FPDFFormObj_GetObject(object, UInt(index)) {
                    collect(child, path: path + [index], parent: transform, into: &runs)
                }
            }
        default:
            break
        }
    }

    private func makeRun(_ object: FPDF_PAGEOBJECT, path: [Int], parent: CGAffineTransform) -> TextRun? {
        let text = utf16String { FPDFTextObj_GetText(object, textPage, $0, $1) }
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }

        var left: Float = 0, bottom: Float = 0, right: Float = 0, top: Float = 0
        FPDFPageObj_GetBounds(object, &left, &bottom, &right, &top)
        let bounds = CGRect(x: CGFloat(left), y: CGFloat(bottom), width: CGFloat(right - left), height: CGFloat(top - bottom))
            .applying(parent)

        var m = FS_MATRIX()
        FPDFPageObj_GetMatrix(object, &m)
        var size: Float = 0
        FPDFTextObj_GetFontSize(object, &size)

        var r: UInt32 = 0, g: UInt32 = 0, b: UInt32 = 0, a: UInt32 = 255
        FPDFPageObj_GetFillColor(object, &r, &g, &b, &a)

        return TextRun(
            path: path,
            text: text,
            bounds: bounds,
            matrix: CGAffineTransform(m).concatenating(parent),
            nominalSize: CGFloat(size),
            font: fontInfo(FPDFTextObj_GetFont(object)),
            color: TextColor(red: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255),
            renderMode: Int(FPDFTextObj_GetTextRenderMode(object).rawValue)
        )
    }

    private func fontInfo(_ font: FPDF_FONT?) -> FontInfo {
        guard let font else {
            return FontInfo(baseName: "", familyName: "", weight: 400, italicAngle: 0, flags: 0, isEmbedded: false)
        }
        if let cached = fontInfoCache[font] { return cached }
        var angle: Int32 = 0
        FPDFFont_GetItalicAngle(font, &angle)
        var info = FontInfo(
            baseName: utf8String { FPDFFont_GetBaseFontName(font, $0, $1) },
            familyName: utf8String { FPDFFont_GetFamilyName(font, $0, $1) },
            weight: Int(FPDFFont_GetWeight(font)),
            italicAngle: Int(angle),
            flags: Int(FPDFFont_GetFlags(font)),
            isEmbedded: FPDFFont_GetIsEmbedded(font) != 0
        )
        info.hasSerifDesign = Self.serifDesign(of: font)
        fontInfoCache[font] = info
        return info
    }

    /// CoreText's stylistic class of the embedded font program, when it can be loaded.
    private static func serifDesign(of font: FPDF_FONT) -> Bool {
        var length = 0
        guard FPDFFont_GetIsEmbedded(font) == 1, FPDFFont_GetFontData(font, nil, 0, &length) != 0, length > 0 else { return false }
        var bytes = [UInt8](repeating: 0, count: length)
        guard FPDFFont_GetFontData(font, &bytes, length, &length) != 0,
              let provider = CGDataProvider(data: Data(bytes) as CFData), let cgFont = CGFont(provider) else { return false }
        let fontClass = CTFontGetSymbolicTraits(CTFontCreateWithGraphicsFont(cgFont, 12, nil, nil)).rawValue & CTFontSymbolicTraits.traitClassMask.rawValue
        let serifClasses: [CTFontStylisticClass] = [.oldStyleSerifsClass, .transitionalSerifsClass, .modernSerifsClass, .clarendonSerifsClass, .slabSerifsClass, .freeformSerifsClass]
        return serifClasses.contains { $0.rawValue == fontClass }
    }
}

// MARK: - Layout analysis

public enum TextLayout {
    /// Groups the runs of a page into paragraphs, top to bottom.
    public static func blocks(from runs: [TextRun]) -> [TextBlock] {
        groupBlocks(lines(from: runs))
    }

    /// Runs sharing a baseline, split where a wide gap suggests another column.
    public static func lines(from runs: [TextRun]) -> [TextLine] {
        let sorted = runs.sorted { $0.origin.y > $1.origin.y }
        var rows: [[TextRun]] = []
        for run in sorted {
            if let last = rows.last?.last, abs(last.origin.y - run.origin.y) < max(last.fontSize, run.fontSize) * 0.35 {
                rows[rows.count - 1].append(run)
            } else {
                rows.append([run])
            }
        }

        var lines: [TextLine] = []
        for row in rows {
            var current: [TextRun] = []
            for run in row.sorted(by: { $0.bounds.minX < $1.bounds.minX }) {
                if let last = current.last, run.bounds.minX - last.bounds.maxX > max(last.fontSize, run.fontSize) * 1.5 {
                    lines.append(makeLine(current))
                    current = []
                }
                current.append(run)
            }
            if !current.isEmpty { lines.append(makeLine(current)) }
        }
        return lines
    }

    private static func makeLine(_ runs: [TextRun]) -> TextLine {
        var text = ""
        for (index, run) in runs.enumerated() {
            if index > 0, runs[index - 1].followedBySpace, !text.hasSuffix(" "), !run.text.hasPrefix(" ") {
                text += " "
            }
            text += text.hasSuffix(" ") && run.text.hasPrefix(" ") ? String(run.text.dropFirst()) : run.text
        }
        let bounds = runs.dropFirst().reduce(runs[0].bounds) { $0.union($1.bounds) }
        let baseline = runs.map(\.origin.y).sorted()[runs.count / 2]
        return TextLine(runs: runs, bounds: bounds, baseline: baseline, text: text)
    }

    private static func groupBlocks(_ lines: [TextLine]) -> [TextBlock] {
        var blocks: [TextBlock] = []
        for line in lines.sorted(by: { $0.baseline > $1.baseline }) {
            let size = line.fontSize
            let index = blocks.lastIndex { block in
                guard let last = block.lines.last else { return false }
                let drop = last.baseline - line.baseline
                let similarSize = abs(last.fontSize - size) <= max(last.fontSize, size) * 0.2
                let regular = block.lines.count < 2 || abs(drop - block.leading) <= block.leading * 0.25
                let overlapping = line.bounds.minX < block.bounds.maxX && line.bounds.maxX > block.bounds.minX
                return drop > size * 0.8 && drop < size * 2 && similarSize && regular && overlapping
            }
            if let index {
                blocks[index].lines.append(line)
                blocks[index].bounds = blocks[index].bounds.union(line.bounds)
            } else {
                blocks.append(TextBlock(lines: [line], bounds: line.bounds))
            }
        }
        return blocks
    }
}
