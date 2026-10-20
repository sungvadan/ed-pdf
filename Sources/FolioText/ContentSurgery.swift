import CoreGraphics
import Foundation

/// One operator of a content stream with its operands and byte range.
struct ContentOperation {
    var name: String
    var operands: [PDFObject]
    var range: Range<Int>
}

enum ContentParser {
    static func operations(in bytes: [UInt8]) -> [ContentOperation] {
        var lexer = PDFLexer(bytes: bytes)
        var operations: [ContentOperation] = []
        var operands: [PDFObject] = []
        var start: Int?
        while true {
            lexer.skipWhitespace()
            let tokenStart = lexer.position
            let token = lexer.next()
            if token == .eof { break }
            guard case .keyword(let name) = token else {
                if start == nil { start = tokenStart }
                if let operand = lexer.parseObject(startingWith: token) { operands.append(operand) }
                continue
            }
            if name == "BI" {
                skipInlineImage(&lexer)
            }
            operations.append(ContentOperation(name: name, operands: operands, range: (start ?? tokenStart)..<lexer.position))
            operands = []
            start = nil
        }
        return operations
    }

    /// Skips "BI <dict> ID <binary> EI"; the binary data can contain anything.
    private static func skipInlineImage(_ lexer: inout PDFLexer) {
        while true {
            let token = lexer.next()
            if token == .eof { return }
            if case .keyword("ID") = token { break }
        }
        let bytes = lexer.bytes
        var i = lexer.position + 1
        while i + 1 < bytes.count {
            if bytes[i] == UInt8(ascii: "E"), bytes[i + 1] == UInt8(ascii: "I"), PDFLexer.isWhitespace(bytes[i - 1]),
               i + 2 == bytes.count || PDFLexer.isWhitespace(bytes[i + 2]) || PDFLexer.isDelimiter(bytes[i + 2]) {
                lexer.position = i + 2
                return
            }
            i += 1
        }
        lexer.position = bytes.count
    }
}

/// A text-showing operator (Tj, TJ, ', ") and the state needed to replace it.
struct ShowOperation {
    var stream: Int
    var operation: Int
    var name: String
    var operands: [PDFObject]
    var range: Range<Int>
    var fontSize: Double
    var horizontalScale: Double
    var textBlock: Int
    /// A Td/TD/Tm/T*/'/" happened since the previous show operator of the same BT block.
    var repositioned: Bool
    /// PDFium creates a text object only for operators that show at least one character.
    var producesObject: Bool
}

public enum TextSurgeryError: Error {
    case objectCountMismatch(stream: Int, pdfium: Int)
    case unsupportedLayout(String)
}

/// Removes the text objects of an edited paragraph from the original content streams, operator by
/// operator, leaving everything else byte-identical.
enum ContentSurgery {
    static func showOperations(in streams: [[UInt8]]) -> (operations: [[ContentOperation]], shows: [ShowOperation]) {
        var all: [[ContentOperation]] = []
        var shows: [ShowOperation] = []
        var fontSize = 0.0, scale = 1.0
        var stack: [(Double, Double)] = []
        var textBlock = 0
        var repositioned = true

        for (streamIndex, bytes) in streams.enumerated() {
            let operations = ContentParser.operations(in: bytes)
            all.append(operations)
            for (index, op) in operations.enumerated() {
                switch op.name {
                case "q": stack.append((fontSize, scale))
                case "Q": if let saved = stack.popLast() { (fontSize, scale) = saved }
                case "BT":
                    textBlock += 1
                    repositioned = true
                case "Tf": fontSize = op.operands.last?.double ?? fontSize
                case "Tz": scale = (op.operands.first?.double ?? 100) / 100
                case "Td", "TD", "Tm", "T*": repositioned = true
                case "Tj", "TJ", "'", "\"":
                    let strings: [Data] = op.name == "TJ"
                        ? (op.operands.first?.array ?? []).compactMap(\.string)
                        : op.operands.compactMap(\.string)
                    shows.append(ShowOperation(
                        stream: streamIndex, operation: index, name: op.name, operands: op.operands, range: op.range,
                        fontSize: fontSize, horizontalScale: scale, textBlock: textBlock,
                        repositioned: repositioned || op.name == "'" || op.name == "\"",
                        producesObject: strings.contains { !$0.isEmpty }
                    ))
                    repositioned = false
                default: break
                }
            }
        }
        return (all, shows)
    }

    /// - Parameters:
    ///   - remove: indices into PDFium's text objects (in page order) to delete.
    ///   - matrices: PDFium's matrix (Tm × CTM) of every text object, in page order.
    /// - Returns: new content for each modified stream.
    static func removeText(_ remove: Set<Int>, matrices: [CGAffineTransform], streams: [[UInt8]]) throws -> [Int: [UInt8]] {
        let (_, shows) = showOperations(in: streams)
        let producing = shows.indices.filter { shows[$0].producesObject }
        guard producing.count == matrices.count else {
            throw TextSurgeryError.objectCountMismatch(stream: producing.count, pdfium: matrices.count)
        }
        var ordinalOfShow: [Int: Int] = [:]
        for (ordinal, showIndex) in producing.enumerated() { ordinalOfShow[showIndex] = ordinal }
        func isRemoved(_ showIndex: Int) -> Bool { ordinalOfShow[showIndex].map(remove.contains) ?? false }

        var replacements: [Int: [(Range<Int>, String)]] = [:]
        for (index, show) in shows.enumerated() where isRemoved(index) {
            var replacement = ""
            if show.name == "'" { replacement = "T*" }
            if show.name == "\"", show.operands.count >= 2 {
                replacement = "\(format(show.operands[0].double ?? 0)) Tw \(format(show.operands[1].double ?? 0)) Tc T*"
            }

            // If the next show operator of the same BT block is kept and relies on the current position,
            // the first removed operator of the chain moves the pen by the same amount without drawing.
            let chainStart = index == 0 || show.repositioned || !isRemoved(index - 1) || shows[index - 1].textBlock != show.textBlock
            if chainStart {
                var next = index + 1
                while next < shows.count, shows[next].textBlock == show.textBlock, !shows[next].repositioned, isRemoved(next) {
                    next += 1
                }
                if next < shows.count, shows[next].textBlock == show.textBlock, !shows[next].repositioned {
                    guard let from = ordinalOfShow[index], let to = ordinalOfShow[next] else {
                        throw TextSurgeryError.unsupportedLayout("décalage sans caractère après le texte modifié")
                    }
                    let m = matrices[from]
                    let delta = CGPoint(x: matrices[to].tx - m.tx, y: matrices[to].ty - m.ty)
                    let tx = (delta.x * m.a + delta.y * m.b) / max(m.a * m.a + m.b * m.b, 1e-9)
                    let denominator = show.fontSize * show.horizontalScale
                    guard abs(denominator) > 1e-9 else { throw TextSurgeryError.unsupportedLayout("taille de police nulle") }
                    let n = -Double(tx) * 1000 / denominator
                    replacement += (replacement.isEmpty ? "" : " ") + "[\(format(n))] TJ"
                }
            }
            replacements[show.stream, default: []].append((show.range, replacement))
        }

        var result: [Int: [UInt8]] = [:]
        for (stream, edits) in replacements {
            var bytes = streams[stream]
            for (range, text) in edits.sorted(by: { $0.0.lowerBound > $1.0.lowerBound }) {
                bytes.replaceSubrange(range, with: Array(text.utf8))
            }
            result[stream] = bytes
        }
        return result
    }

    private static func format(_ value: Double) -> String {
        String(format: "%.4f", value).replacingOccurrences(of: "\\.?0+$", with: "", options: .regularExpression)
    }
}
