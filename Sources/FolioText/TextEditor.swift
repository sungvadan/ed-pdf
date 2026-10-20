import CoreGraphics
import Foundation

/// Rewrites a paragraph of a PDF and returns the new file.
///
/// PDFium lays out and writes the new text into a content stream of its own; the original text objects
/// are then removed from the original streams operator by operator (`ContentSurgery`), so kerning and
/// spacing of every other text on the page stay exactly as they were.
public enum TextEditor {
    public struct Result {
        public var data: Data
        public var fontReport: [String]
    }

    /// Edits, then reads back the text objects just written: if a character does not come back
    /// (glyph missing, or mapped to a look-alike such as "-" → soft hyphen by some embedded subsets),
    /// retries with system fonts only, and refuses the edit if that fails too.
    public static func replace(
        blockAt blockIndex: Int, onPage pageIndex: Int, in data: Data, password: String? = nil,
        with makeSegments: (TextBlock) -> [StyledSegment], avoidEmbeddedFonts: Bool = false, verify: Bool = true
    ) throws -> Result {
        var expected = ""
        let (result, created) = try write(blockAt: blockIndex, onPage: pageIndex, in: data, password: password, with: { block in
            let segments = makeSegments(block).map { StyledSegment(text: withoutLigatures($0.text), style: $0.style) }
            expected = segments.map(\.text).joined()
            return segments
        }, avoidEmbeddedFonts: avoidEmbeddedFonts)
        let found = writtenText(result.data, pageIndex: pageIndex, password: password, created: created)
        if !verify || compact(found) == compact(expected) { return result }
        if !avoidEmbeddedFonts {
            return try replace(blockAt: blockIndex, onPage: pageIndex, in: data, password: password, with: makeSegments,
                               avoidEmbeddedFonts: true, verify: verify)
        }
        throw TextEditError.verificationFailed(expected: expected, found: found)
    }

    /// Text comparison that ignores line breaks and kinds of spaces.
    public static func normalized(_ text: String) -> String {
        text.replacingOccurrences(of: "\u{00A0}", with: " ")
            .components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// Characters only: compatibility forms folded (ligatures, no-break spaces), whitespace dropped.
    /// Soft hyphens are kept, so a "-" read back as U+00AD still counts as a difference.
    public static func compact(_ text: String) -> String {
        String(text.precomposedStringWithCompatibilityMapping.unicodeScalars.filter { !CharacterSet.whitespacesAndNewlines.contains($0) })
    }

    /// Characters of `expected` that did not come back in `found`.
    public static func missingCharacters(expected: String, found: String) -> String {
        var remaining = Array(compact(found))
        var missing: [Character] = []
        for character in compact(expected) {
            if let index = remaining.firstIndex(of: character) { remaining.remove(at: index) } else if !missing.contains(character) { missing.append(character) }
        }
        return String(missing)
    }

    /// "ﬁ" → "fi" etc.: ligature code points from the original text are written as plain letters.
    static func withoutLigatures(_ text: String) -> String {
        String(text.unicodeScalars.flatMap { scalar -> [Unicode.Scalar] in
            (0xFB00...0xFB06).contains(scalar.value)
                ? Array(String(scalar).precomposedStringWithCompatibilityMapping.unicodeScalars) : [scalar]
        }.map(Character.init))
    }

    /// Text of the objects written by the edit: the last `created` objects of the page, in writing order.
    static func writtenText(_ data: Data, pageIndex: Int, password: String?, created: Int) -> String {
        guard let page = PDFiumDocument(data: data, password: password)?.page(at: pageIndex) else { return "" }
        let firstNew = page.objectCount - created
        return page.textRuns().filter { $0.path[0] >= firstNew }.map(\.text).joined(separator: " ")
    }

    private static func write(
        blockAt blockIndex: Int, onPage pageIndex: Int, in data: Data, password: String?,
        with makeSegments: (TextBlock) -> [StyledSegment], avoidEmbeddedFonts: Bool
    ) throws -> (Result, created: Int) {
        guard let document = PDFiumDocument(data: data, password: password), let page = document.page(at: pageIndex) else {
            throw TextEditError.writeFailed
        }
        let blocks = TextLayout.blocks(from: page.textRuns())
        guard blocks.indices.contains(blockIndex) else { throw TextEditError.notEditable }
        let block = blocks[blockIndex]

        // Ordinals (among text objects) of the runs to remove, taken before anything is added.
        let textObjects = page.topLevelTextObjects()
        let ordinalOfIndex = Dictionary(uniqueKeysWithValues: textObjects.enumerated().map { ($1.index, $0) })
        let removed = Set(block.runs.compactMap { ordinalOfIndex[$0.path[0]] })

        let writer = BlockWriter(page: page)
        writer.keepsOriginalObjects = true
        writer.avoidEmbeddedFonts = avoidEmbeddedFonts
        try writer.replace(block, with: makeSegments(block))
        guard let intermediate = document.data() else { throw TextEditError.writeFailed }

        // New objects come after the original ones; their positions are not needed (never removed).
        let file = try PDFFile(data: intermediate)
        guard let pageObject = file.pageObjectNumber(at: pageIndex) else { throw PDFFileError.malformed("page") }
        let streamNumbers = file.contentStreams(ofPage: pageObject)
        let streams = try streamNumbers.map { number -> [UInt8] in
            guard let object = file.object(number), let decoded = file.decodedStream(object) else {
                throw PDFFileError.unsupported("content stream \(number)")
            }
            return [UInt8](decoded)
        }
        let matrices = textObjects.map(\.matrix) + [CGAffineTransform](repeating: .identity, count: writer.createdObjectCount)
        let edited = try ContentSurgery.removeText(removed, matrices: matrices, streams: streams)
        let output = try file.incrementalUpdate(replacingStreams: Dictionary(uniqueKeysWithValues: edited.map {
            (streamNumbers[$0.key], Data($0.value))
        }))
        return (Result(data: output, fontReport: writer.fontReport), writer.createdObjectCount)
    }
}
