import CoreGraphics
import Foundation
import PDFKit

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

    /// Edits, then reads the page back with PDFKit (what search and copy use): if the new text does not
    /// extract exactly (some embedded subsets map characters to look-alike glyphs, e.g. "-" to a soft
    /// hyphen), retries with system fonts only.
    public static func replace(
        blockAt blockIndex: Int, onPage pageIndex: Int, in data: Data, password: String? = nil,
        with makeSegments: (TextBlock) -> [StyledSegment], avoidEmbeddedFonts: Bool = false, verify: Bool = true
    ) throws -> Result {
        var expected = ""
        let result = try write(blockAt: blockIndex, onPage: pageIndex, in: data, password: password, with: { block in
            let segments = makeSegments(block)
            expected = segments.map(\.text).joined()
            return segments
        }, avoidEmbeddedFonts: avoidEmbeddedFonts)
        let found = pageText(result.data, pageIndex: pageIndex, password: password)
        if !verify || found.contains(normalized(expected)) { return result }
        if !avoidEmbeddedFonts {
            return try replace(blockAt: blockIndex, onPage: pageIndex, in: data, password: password, with: makeSegments, avoidEmbeddedFonts: true)
        }
        throw TextEditError.verificationFailed(expected: expected, found: found)
    }

    /// Text comparison that ignores line breaks and kinds of spaces.
    public static func normalized(_ text: String) -> String {
        text.replacingOccurrences(of: "\u{00A0}", with: " ")
            .components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// The page text as PDFKit extracts it, normalized.
    static func pageText(_ data: Data, pageIndex: Int, password: String?) -> String {
        guard let document = PDFDocument(data: data) else { return "" }
        if document.isLocked, let password { document.unlock(withPassword: password) }
        return normalized(document.page(at: pageIndex)?.string ?? "")
    }

    private static func write(
        blockAt blockIndex: Int, onPage pageIndex: Int, in data: Data, password: String?,
        with makeSegments: (TextBlock) -> [StyledSegment], avoidEmbeddedFonts: Bool
    ) throws -> Result {
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
        return Result(data: output, fontReport: writer.fontReport)
    }
}
