import CoreGraphics
import CPDFium
import Foundation

/// Style of a span of edited text. Two styles are equal when they look the same, whatever run they came from.
public struct RunStyle: Hashable {
    public static func == (lhs: RunStyle, rhs: RunStyle) -> Bool {
        lhs.font == rhs.font && abs(lhs.fontSize - rhs.fontSize) < 0.01 && lhs.color == rhs.color
            && lhs.bold == rhs.bold && lhs.italic == rhs.italic
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(font)
        hasher.combine(Int((fontSize * 100).rounded()))
        hasher.combine(color)
        hasher.combine(bold)
        hasher.combine(italic)
    }

    public var font: FontInfo
    /// Size on the page.
    public var fontSize: CGFloat
    public var color: TextColor
    /// An original run drawn with this font, whose embedded font can be reused.
    public var sourcePath: [Int]?
    /// Requested emphasis; nil keeps the font's own.
    public var bold: Bool?
    public var italic: Bool?

    public init(font: FontInfo, fontSize: CGFloat, color: TextColor, sourcePath: [Int]? = nil, bold: Bool? = nil, italic: Bool? = nil) {
        self.font = font
        self.fontSize = fontSize
        self.color = color
        self.sourcePath = sourcePath
        self.bold = bold
        self.italic = italic
    }

    public init(run: TextRun) {
        self.init(font: run.font, fontSize: run.fontSize, color: run.color, sourcePath: run.path)
    }

    var changesEmphasis: Bool {
        (bold.map { $0 != font.isBold } ?? false) || (italic.map { $0 != font.isItalic } ?? false)
    }
}

public struct StyledSegment {
    public var text: String
    public var style: RunStyle

    public init(text: String, style: RunStyle) {
        self.text = text
        self.style = style
    }
}

public enum ParagraphAlignment: String {
    case left, center, right, justified
}

/// Where and how a paragraph is laid out.
public struct ParagraphGeometry {
    /// Start of the first baseline, page space.
    public var origin: CGPoint
    public var width: CGFloat
    public var leading: CGFloat
    public var alignment: ParagraphAlignment

    public init(origin: CGPoint, width: CGFloat, leading: CGFloat, alignment: ParagraphAlignment) {
        self.origin = origin
        self.width = width
        self.leading = leading
        self.alignment = alignment
    }

    public init(block: TextBlock) {
        let first = block.lines[0]
        let left = block.lines.map(\.bounds.minX).min() ?? first.bounds.minX
        origin = CGPoint(x: left, y: first.baseline)
        // A single line (title, label…) grows to the right instead of wrapping.
        width = block.lines.count == 1 ? .greatestFiniteMagnitude : block.bounds.width
        leading = block.leading
        alignment = Self.alignment(of: block)
    }

    static func alignment(of block: TextBlock) -> ParagraphAlignment {
        let lines = block.lines
        guard lines.count >= 2 else { return .left }
        let tolerance: CGFloat = 2
        let body = lines.dropLast()
        func aligned(_ values: [CGFloat]) -> Bool { (values.max() ?? 0) - (values.min() ?? 0) <= tolerance }
        let lefts = aligned(lines.map(\.bounds.minX))
        if lefts && body.count >= 1 && aligned(body.map(\.bounds.maxX)) && lines.count >= 3 { return .justified }
        if lefts { return .left }
        if aligned(lines.map(\.bounds.maxX)) { return .right }
        if aligned(lines.map(\.bounds.midX)) { return .center }
        return .left
    }
}

public enum TextEditError: Error {
    case notEditable
    case fontUnavailable(String)
    case writeFailed
    /// The written text does not read back as expected, even with system fonts.
    case verificationFailed(expected: String, found: String)
}

/// Replaces a paragraph's text objects with newly laid-out text.
public final class BlockWriter {
    private let page: PDFiumPage
    private let fonts: FontResolver
    /// Use system fonts even when the embedded font could encode the text.
    public var avoidEmbeddedFonts = false
    /// One line per used font, for diagnostics.
    public private(set) var fontReport: [String] = []
    /// Leave the original objects in place and append the new ones at the end of the page, so PDFium
    /// writes them to a new content stream and keeps the original streams byte-identical.
    var keepsOriginalObjects = false
    /// Number of text objects created by the last replacement.
    private(set) var createdObjectCount = 0

    public init(page: PDFiumPage) {
        self.page = page
        self.fonts = FontResolver(page: page)
    }

    public func replace(_ block: TextBlock, with segments: [StyledSegment], geometry: ParagraphGeometry? = nil) throws {
        guard block.isEditable else { throw TextEditError.notEditable }
        let geometry = geometry ?? ParagraphGeometry(block: block)

        // Handles of the original objects, gathered before anything moves.
        let objects = block.runs.compactMap { run in FPDFPage_GetObject(page.handle, Int32(run.path[0])) }
        let insertIndex = block.runs.map { $0.path[0] }.min() ?? Int(FPDFPage_CountObjects(page.handle))

        // Fonts per style.
        var resolved: [RunStyle: ResolvedFont] = [:]
        fontReport = []
        for style in Set(segments.map(\.style)) {
            let text = segments.filter { $0.style == style }.map(\.text).joined()
            var font: ResolvedFont?
            if !avoidEmbeddedFonts, !style.changesEmphasis, let path = style.sourcePath,
               let object = FPDFPage_GetObject(page.handle, Int32(path[0])), let handle = FPDFTextObj_GetFont(object) {
                font = fonts.embedded(handle, info: style.font, for: text)
            }
            font = font ?? fonts.system(matching: style.font, bold: style.bold, italic: style.italic)
            guard let font else { throw TextEditError.fontUnavailable(style.font.postScriptName) }
            resolved[style] = font
            fontReport.append("\(style.font.postScriptName) → \(font.description)")
        }

        // Layout, then create the new objects.
        let lines = layout(segments, fonts: resolved, width: geometry.width)
        var created: [FPDF_PAGEOBJECT] = []
        for (number, line) in lines.enumerated() {
            let baseline = geometry.origin.y - CGFloat(number) * geometry.leading
            created += emit(line, baseline: baseline, geometry: geometry, fonts: resolved)
        }

        createdObjectCount = created.count
        if keepsOriginalObjects {
            created.forEach { FPDFPage_InsertObject(page.handle, $0) }
        } else {
            for (offset, object) in created.enumerated() {
                guard FPDFPage_InsertObjectAtIndex(page.handle, object, insertIndex + offset) != 0 else { throw TextEditError.writeFailed }
            }
            for object in objects where FPDFPage_RemoveObject(page.handle, object) != 0 {
                FPDFPageObj_Destroy(object)
            }
        }
        guard page.generateContent() else { throw TextEditError.writeFailed }
    }

    // MARK: Layout

    private struct Piece {
        var text: Substring
        var style: RunStyle
        var width: CGFloat
    }

    private struct Line {
        var pieces: [Piece]
        /// Last line of a paragraph (not stretched when justified).
        var endsParagraph: Bool
        var width: CGFloat { pieces.reduce(0) { $0 + $1.width } }
    }

    /// Greedy line breaking on spaces; explicit newlines end paragraphs.
    private func layout(_ segments: [StyledSegment], fonts: [RunStyle: ResolvedFont], width: CGFloat) -> [Line] {
        // Words keep their trailing spaces so breaking never loses or adds characters.
        var paragraphs: [[[Piece]]] = [[]]
        for segment in segments {
            let font = fonts[segment.style]!
            var start = segment.text.startIndex
            var index = start
            func flushWord(through end: String.Index) {
                guard start < end else { return }
                let text = segment.text[start..<end]
                let piece = Piece(text: text, style: segment.style, width: font.width(of: text, size: segment.style.fontSize))
                // Continue the current word if the previous piece did not end with a space (style change mid-word).
                if let lastWord = paragraphs[paragraphs.count - 1].last, let lastPiece = lastWord.last, lastPiece.text.last != " " {
                    paragraphs[paragraphs.count - 1][paragraphs[paragraphs.count - 1].count - 1].append(piece)
                } else {
                    paragraphs[paragraphs.count - 1].append([piece])
                }
                start = end
            }
            while index < segment.text.endIndex {
                let character = segment.text[index]
                let next = segment.text.index(after: index)
                if character.isNewline {
                    flushWord(through: index)
                    paragraphs.append([])
                    start = next
                } else if character == " " {
                    flushWord(through: next)
                }
                index = next
            }
            flushWord(through: segment.text.endIndex)
        }

        var lines: [Line] = []
        for words in paragraphs {
            var current: [Piece] = []
            var currentWidth: CGFloat = 0
            for word in words {
                let wordWidth = word.reduce(0) { $0 + $1.width }
                let visibleWidth = wordWidth - trailingSpaceWidth(word, fonts: fonts)
                if !current.isEmpty, currentWidth + visibleWidth > width + 0.5 {
                    lines.append(Line(pieces: current, endsParagraph: false))
                    current = []
                    currentWidth = 0
                }
                current += word
                currentWidth += wordWidth
            }
            lines.append(Line(pieces: current, endsParagraph: true))
        }
        return lines
    }

    private func trailingSpaceWidth(_ word: [Piece], fonts: [RunStyle: ResolvedFont]) -> CGFloat {
        guard let last = word.last, last.text.last == " " else { return 0 }
        return fonts[last.style]!.width(of: " ", size: last.style.fontSize)
    }

    // MARK: Output

    private func emit(_ line: Line, baseline: CGFloat, geometry: ParagraphGeometry, fonts: [RunStyle: ResolvedFont]) -> [FPDF_PAGEOBJECT] {
        // Trailing spaces do not count for alignment.
        var pieces = line.pieces
        while let last = pieces.last, last.text.allSatisfy({ $0 == " " }) { pieces.removeLast() }
        if var last = pieces.last, last.text.last == " " {
            while last.text.last == " " { last.text = last.text.dropLast() }
            last.width = fonts[last.style]!.width(of: last.text, size: last.style.fontSize)
            pieces[pieces.count - 1] = last
        }
        // One object per style run (words were separate pieces only for line breaking).
        var merged: [Piece] = []
        for piece in pieces {
            if let last = merged.last, last.style == piece.style {
                merged[merged.count - 1].text = Substring(String(last.text) + String(piece.text))
                merged[merged.count - 1].width += piece.width
            } else {
                merged.append(piece)
            }
        }
        pieces = merged
        let lineWidth = pieces.reduce(0) { $0 + $1.width }

        var x = geometry.origin.x
        var extraPerSpace: CGFloat = 0
        switch geometry.alignment {
        case .left: break
        case .center: x += (geometry.width - lineWidth) / 2
        case .right: x += geometry.width - lineWidth
        case .justified:
            let spaces = pieces.reduce(0) { $0 + $1.text.filter { $0 == " " }.count }
            if !line.endsParagraph, spaces > 0, lineWidth < geometry.width {
                extraPerSpace = (geometry.width - lineWidth) / CGFloat(spaces)
            }
        }

        var objects: [FPDF_PAGEOBJECT] = []
        for piece in pieces {
            let font = fonts[piece.style]!
            let size = piece.style.fontSize
            // Justified lines stretch their spaces, so they are written word by word, as are fonts
            // without a space glyph. Otherwise a style run is one object, spaces included.
            guard extraPerSpace > 0 || !font.writesSpaces else {
                if let object = makeText(String(piece.text), style: piece.style, font: font, at: CGPoint(x: x, y: baseline)) {
                    objects.append(object)
                }
                x += piece.width
                continue
            }
            for token in Self.wordsWithTrailingSpaces(piece.text) {
                let spaces = token.reversed().prefix { $0 == " " }.count
                let word = token.dropLast(spaces)
                let written = font.writesSpaces ? token : word
                if !word.isEmpty, let object = makeText(String(written), style: piece.style, font: font, at: CGPoint(x: x, y: baseline)) {
                    objects.append(object)
                }
                x += font.width(of: token, size: size) + CGFloat(spaces) * extraPerSpace
            }
        }
        return objects
    }

    /// "a  b c" → ["a  ", "b ", "c"]; leading spaces form their own token.
    static func wordsWithTrailingSpaces(_ text: Substring) -> [Substring] {
        var tokens: [Substring] = []
        var start = text.startIndex
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(after: index)
            if text[index] == " ", next < text.endIndex, text[next] != " " {
                tokens.append(text[start..<next])
                start = next
            }
            index = next
        }
        if start < text.endIndex { tokens.append(text[start...]) }
        return tokens
    }

    private func makeText(_ text: String, style: RunStyle, font: ResolvedFont, at origin: CGPoint) -> FPDF_PAGEOBJECT? {
        // Fonts embedded by Folio map each glyph from one character only (see FontResolver.uniqueCmap).
        let text = text.replacingOccurrences(of: "\u{00A0}", with: " ").replacingOccurrences(of: "\u{00AD}", with: "")
        guard !text.isEmpty, let object = FPDFPageObj_CreateTextObj(page.document.handle, font.handle, Float(style.fontSize)) else { return nil }
        guard withWideString(text, { FPDFText_SetText(object, $0) }) != 0 else {
            FPDFPageObj_Destroy(object)
            return nil
        }
        var matrix = CGAffineTransform(translationX: origin.x, y: origin.y).fsMatrix
        FPDFPageObj_SetMatrix(object, &matrix)
        let c = style.color
        FPDFPageObj_SetFillColor(object, UInt32(c.red * 255), UInt32(c.green * 255), UInt32(c.blue * 255), UInt32(c.alpha * 255))
        return object
    }
}

// MARK: - Convenience

extension TextBlock {
    /// The block's text split into styled segments, one per run, with spaces/newlines between lines.
    public var styledSegments: [StyledSegment] {
        var segments: [StyledSegment] = []
        for (lineIndex, line) in lines.enumerated() {
            for (runIndex, run) in line.runs.enumerated() {
                var text = run.text
                if runIndex > 0, line.runs[runIndex - 1].followedBySpace,
                   !(segments.last?.text.hasSuffix(" ") ?? true), !text.hasPrefix(" ") {
                    text = " " + text
                }
                segments.append(StyledSegment(text: text, style: RunStyle(run: run)))
            }
            // Lines of a paragraph are joined with a space; the writer re-wraps them.
            if lineIndex < lines.count - 1, let last = segments.last, !last.text.hasSuffix(" "), !last.text.hasSuffix("-") {
                segments[segments.count - 1].text += " "
            }
        }
        return segments
    }
}

extension Array where Element == StyledSegment {
    /// Applies a plain-text edit, keeping styles of unchanged characters; inserted text takes the
    /// style of the character before it (common prefix/suffix diff).
    public func applying(newText: String) -> [StyledSegment] {
        var characters: [(Character, RunStyle)] = flatMap { segment in segment.text.map { ($0, segment.style) } }
        let old = characters.map(\.0)
        let new = [Character](newText)
        var prefix = 0
        while prefix < old.count, prefix < new.count, old[prefix] == new[prefix] { prefix += 1 }
        var suffix = 0
        while suffix < old.count - prefix, suffix < new.count - prefix, old[old.count - 1 - suffix] == new[new.count - 1 - suffix] { suffix += 1 }
        let insertStyle = prefix > 0 ? characters[prefix - 1].1 : (characters.first?.1 ?? first!.style)
        characters.replaceSubrange(prefix..<(old.count - suffix), with: new[prefix..<(new.count - suffix)].map { ($0, insertStyle) })

        var result: [StyledSegment] = []
        for (character, style) in characters {
            if let last = result.last, last.style == style {
                result[result.count - 1].text.append(character)
            } else {
                result.append(StyledSegment(text: String(character), style: style))
            }
        }
        return result
    }
}
