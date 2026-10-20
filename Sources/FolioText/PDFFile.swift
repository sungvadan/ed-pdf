import Compression
import Foundation

/// A PDF object, as parsed from a file or a content stream.
indirect enum PDFObject {
    case null
    case bool(Bool)
    case number(Double)
    case name(String)
    case string(Data)
    case array([PDFObject])
    case dict([String: PDFObject])
    case ref(Int, Int)
    case stream([String: PDFObject], Data)

    var int: Int? { if case .number(let v) = self { return Int(v) } else { return nil } }
    var double: Double? { if case .number(let v) = self { return v } else { return nil } }
    var name: String? { if case .name(let v) = self { return v } else { return nil } }
    var array: [PDFObject]? { if case .array(let v) = self { return v } else { return nil } }
    var string: Data? { if case .string(let v) = self { return v } else { return nil } }
    var dict: [String: PDFObject]? {
        switch self {
        case .dict(let v), .stream(let v, _): return v
        default: return nil
        }
    }
    var ref: (Int, Int)? { if case .ref(let n, let g) = self { return (n, g) } else { return nil } }
}

public enum PDFFileError: Error {
    case malformed(String)
    case unsupported(String)
}

// MARK: - Lexer

/// Tokenizer shared by file parsing and content streams.
struct PDFLexer {
    enum Token: Equatable {
        case number(Double, isInteger: Bool)
        case name(String)
        case string(Data)
        case arrayStart, arrayEnd, dictStart, dictEnd
        case keyword(String)
        case eof
    }

    let bytes: [UInt8]
    var position: Int

    init(bytes: [UInt8], position: Int = 0) {
        self.bytes = bytes
        self.position = position
    }

    static func isWhitespace(_ b: UInt8) -> Bool { b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32 }
    static func isDelimiter(_ b: UInt8) -> Bool { "()<>[]{}/%".utf8.contains(b) }

    mutating func skipWhitespace() {
        while position < bytes.count {
            let b = bytes[position]
            if Self.isWhitespace(b) {
                position += 1
            } else if b == UInt8(ascii: "%") {
                while position < bytes.count, bytes[position] != 10, bytes[position] != 13 { position += 1 }
            } else {
                return
            }
        }
    }

    mutating func next() -> Token {
        skipWhitespace()
        guard position < bytes.count else { return .eof }
        let b = bytes[position]
        switch b {
        case UInt8(ascii: "["): position += 1; return .arrayStart
        case UInt8(ascii: "]"): position += 1; return .arrayEnd
        case UInt8(ascii: "{"), UInt8(ascii: "}"): position += 1; return .keyword(String(UnicodeScalar(b)))
        case UInt8(ascii: "<"):
            if position + 1 < bytes.count, bytes[position + 1] == UInt8(ascii: "<") { position += 2; return .dictStart }
            return .string(hexString())
        case UInt8(ascii: ">"):
            if position + 1 < bytes.count, bytes[position + 1] == UInt8(ascii: ">") { position += 2; return .dictEnd }
            position += 1
            return .keyword(">")
        case UInt8(ascii: "("): return .string(literalString())
        case UInt8(ascii: "/"): return .name(nameToken())
        default:
            let start = position
            while position < bytes.count, !Self.isWhitespace(bytes[position]), !Self.isDelimiter(bytes[position]) { position += 1 }
            if position == start { position += 1 }
            let text = String(decoding: bytes[start..<position], as: UTF8.self)
            if let value = Double(text), text.first.map({ $0.isNumber || $0 == "-" || $0 == "+" || $0 == "." }) == true {
                return .number(value, isInteger: !text.contains("."))
            }
            return .keyword(text)
        }
    }

    private mutating func nameToken() -> String {
        position += 1
        var result = [UInt8]()
        while position < bytes.count, !Self.isWhitespace(bytes[position]), !Self.isDelimiter(bytes[position]) {
            if bytes[position] == UInt8(ascii: "#"), position + 2 < bytes.count,
               let value = UInt8(String(decoding: bytes[(position + 1)...(position + 2)], as: UTF8.self), radix: 16) {
                result.append(value)
                position += 3
            } else {
                result.append(bytes[position])
                position += 1
            }
        }
        return String(decoding: result, as: UTF8.self)
    }

    private mutating func hexString() -> Data {
        position += 1
        var digits = [UInt8]()
        while position < bytes.count, bytes[position] != UInt8(ascii: ">") {
            if !Self.isWhitespace(bytes[position]) { digits.append(bytes[position]) }
            position += 1
        }
        position += 1
        if digits.count % 2 == 1 { digits.append(UInt8(ascii: "0")) }
        var data = Data()
        for i in stride(from: 0, to: digits.count, by: 2) {
            data.append(UInt8(String(decoding: digits[i...i + 1], as: UTF8.self), radix: 16) ?? 0)
        }
        return data
    }

    private mutating func literalString() -> Data {
        position += 1
        var depth = 1
        var data = Data()
        while position < bytes.count {
            let b = bytes[position]
            position += 1
            switch b {
            case UInt8(ascii: "("): depth += 1; data.append(b)
            case UInt8(ascii: ")"):
                depth -= 1
                if depth == 0 { return data }
                data.append(b)
            case UInt8(ascii: "\\"):
                guard position < bytes.count else { return data }
                let e = bytes[position]
                position += 1
                switch e {
                case UInt8(ascii: "n"): data.append(10)
                case UInt8(ascii: "r"): data.append(13)
                case UInt8(ascii: "t"): data.append(9)
                case UInt8(ascii: "b"): data.append(8)
                case UInt8(ascii: "f"): data.append(12)
                case 13: if position < bytes.count, bytes[position] == 10 { position += 1 }
                case 10: break
                case UInt8(ascii: "0")...UInt8(ascii: "7"):
                    var value = Int(e - 48)
                    for _ in 0..<2 where position < bytes.count && (48...55).contains(bytes[position]) {
                        value = value * 8 + Int(bytes[position] - 48)
                        position += 1
                    }
                    data.append(UInt8(value & 0xFF))
                default: data.append(e)
                }
            default: data.append(b)
            }
        }
        return data
    }

    /// Parses one object, resolving "n g R" into a reference.
    mutating func parseObject() -> PDFObject? {
        let token = next()
        return parseObject(startingWith: token)
    }

    mutating func parseObject(startingWith token: Token) -> PDFObject? {
        switch token {
        case .number(let value, let isInteger):
            if isInteger {
                let saved = position
                if case .number(let gen, true) = next(), case .keyword("R") = next() {
                    return .ref(Int(value), Int(gen))
                }
                position = saved
            }
            return .number(value)
        case .name(let name): return .name(name)
        case .string(let data): return .string(data)
        case .arrayStart:
            var items: [PDFObject] = []
            while true {
                let t = next()
                if t == .arrayEnd || t == .eof { break }
                if let item = parseObject(startingWith: t) { items.append(item) }
            }
            return .array(items)
        case .dictStart:
            var dict: [String: PDFObject] = [:]
            while true {
                let t = next()
                guard case .name(let key) = t else { break }
                dict[key] = parseObject() ?? .null
            }
            return .dict(dict)
        case .keyword("true"): return .bool(true)
        case .keyword("false"): return .bool(false)
        case .keyword("null"): return .null
        default: return nil
        }
    }
}

// MARK: - File

/// Minimal PDF reader (classic xref tables and xref streams, object streams) with incremental updates.
final class PDFFile {
    private enum XrefEntry {
        case offset(Int)
        case compressed(stream: Int, index: Int)
    }

    let bytes: [UInt8]
    private(set) var trailer: [String: PDFObject] = [:]
    private var entries: [Int: XrefEntry] = [:]
    private var cache: [Int: PDFObject] = [:]
    let lastXrefOffset: Int
    private(set) var usesXrefStream = false

    init(data: Data) throws {
        bytes = [UInt8](data)
        guard let start = Self.findLast("startxref", in: bytes) else { throw PDFFileError.malformed("startxref") }
        var lexer = PDFLexer(bytes: bytes, position: start + 9)
        guard case .number(let offset, _) = lexer.next() else { throw PDFFileError.malformed("startxref offset") }
        lastXrefOffset = Int(offset)

        var next: Int? = lastXrefOffset
        var visited = Set<Int>()
        while let offset = next, !visited.contains(offset) {
            visited.insert(offset)
            let sectionTrailer = try readXref(at: offset)
            if trailer.isEmpty { trailer = sectionTrailer }
            next = sectionTrailer["Prev"]?.int
            if let hybrid = sectionTrailer["XRefStm"]?.int { _ = try? readXref(at: hybrid) }
        }
    }

    private static func findLast(_ needle: String, in bytes: [UInt8]) -> Int? {
        let pattern = Array(needle.utf8)
        var i = bytes.count - pattern.count
        while i >= 0 {
            if bytes[i] == pattern[0], Array(bytes[i..<i + pattern.count]) == pattern { return i }
            i -= 1
        }
        return nil
    }

    /// Reads one xref section (table or stream); newer sections take precedence.
    private func readXref(at offset: Int) throws -> [String: PDFObject] {
        var lexer = PDFLexer(bytes: bytes, position: offset)
        if case .keyword("xref") = lexer.next() {
            while true {
                let saved = lexer.position
                guard case .number(let first, _) = lexer.next(), case .number(let count, _) = lexer.next() else {
                    lexer.position = saved
                    break
                }
                for n in 0..<Int(count) {
                    guard case .number(let off, _) = lexer.next(), case .number = lexer.next(), case .keyword(let kind) = lexer.next() else {
                        throw PDFFileError.malformed("xref entry")
                    }
                    let number = Int(first) + n
                    if kind == "n", entries[number] == nil { entries[number] = .offset(Int(off)) }
                }
            }
            guard case .keyword("trailer") = lexer.next(), case .dict(let dict)? = lexer.parseObject() else {
                throw PDFFileError.malformed("trailer")
            }
            return dict
        }

        // Cross-reference stream.
        guard case .stream(let dict, _)? = try objectAt(offset: offset), let data = try? decodedStream(objectAt(offset: offset)!),
              let widths = dict["W"]?.array?.compactMap(\.int), widths.count == 3 else {
            throw PDFFileError.malformed("xref stream")
        }
        usesXrefStream = true
        let index = dict["Index"]?.array?.compactMap(\.int) ?? [0, dict["Size"]?.int ?? 0]
        let rowSize = widths.reduce(0, +)
        var row = 0
        let raw = [UInt8](data)
        func field(_ start: Int, _ width: Int) -> Int {
            (0..<width).reduce(0) { $0 << 8 | Int(raw[start + $1]) }
        }
        for pair in stride(from: 0, to: index.count - 1, by: 2) {
            for n in 0..<index[pair + 1] {
                let base = row * rowSize
                row += 1
                guard base + rowSize <= raw.count else { break }
                let type = widths[0] == 0 ? 1 : field(base, widths[0])
                let a = field(base + widths[0], widths[1])
                let b = field(base + widths[0] + widths[1], widths[2])
                let number = index[pair] + n
                guard entries[number] == nil else { continue }
                if type == 1 { entries[number] = .offset(a) }
                if type == 2 { entries[number] = .compressed(stream: a, index: b) }
            }
        }
        return dict
    }

    private func objectAt(offset: Int) throws -> PDFObject? {
        var lexer = PDFLexer(bytes: bytes, position: offset)
        guard case .number = lexer.next(), case .number = lexer.next(), case .keyword("obj") = lexer.next() else {
            throw PDFFileError.malformed("obj at \(offset)")
        }
        guard let object = lexer.parseObject() else { return nil }
        guard case .dict(let dict) = object else { return object }
        let saved = lexer.position
        guard case .keyword("stream") = lexer.next() else {
            lexer.position = saved
            return object
        }
        var start = lexer.position
        if start < bytes.count, bytes[start] == 13 { start += 1 }
        if start < bytes.count, bytes[start] == 10 { start += 1 }
        var length = resolve(dict["Length"] ?? .null).int ?? -1
        if length < 0 || start + length > bytes.count {
            // Bad /Length: search for endstream.
            let marker = Array("endstream".utf8)
            var end = start
            while end + marker.count <= bytes.count, Array(bytes[end..<end + marker.count]) != marker { end += 1 }
            length = end - start
        }
        return .stream(dict, Data(bytes[start..<start + length]))
    }

    func object(_ number: Int) -> PDFObject? {
        if let cached = cache[number] { return cached }
        var result: PDFObject?
        switch entries[number] {
        case .offset(let offset): result = try? objectAt(offset: offset)
        case .compressed(let streamNumber, let index): result = objectFromStream(streamNumber, index: index)
        case nil: result = nil
        }
        if let result { cache[number] = result }
        return result
    }

    private func objectFromStream(_ streamNumber: Int, index: Int) -> PDFObject? {
        guard let stream = object(streamNumber), let dict = stream.dict, let data = decodedStream(stream),
              let count = dict["N"]?.int, let first = dict["First"]?.int else { return nil }
        let raw = [UInt8](data)
        var lexer = PDFLexer(bytes: raw)
        var offsets: [Int] = []
        for _ in 0..<count {
            guard case .number = lexer.next(), case .number(let off, _) = lexer.next() else { return nil }
            offsets.append(Int(off))
        }
        guard index < offsets.count else { return nil }
        var objectLexer = PDFLexer(bytes: raw, position: first + offsets[index])
        return objectLexer.parseObject()
    }

    func resolve(_ object: PDFObject) -> PDFObject {
        if case .ref(let number, _) = object { return self.object(number) ?? .null }
        return object
    }

    func decodedStream(_ object: PDFObject) -> Data? {
        guard case .stream(let dict, let raw) = object else { return nil }
        let filters = resolve(dict["Filter"] ?? .null)
        let names = filters.name.map { [$0] } ?? filters.array?.compactMap(\.name) ?? []
        var data = raw
        for (i, filter) in names.enumerated() {
            guard filter == "FlateDecode" || filter == "Fl" else { return nil }
            guard let inflated = Self.inflate(data) else { return nil }
            data = inflated
            let params = resolve(dict["DecodeParms"] ?? .null)
            let p = params.array.map { $0.indices.contains(i) ? resolve($0[i]) : .null } ?? params
            if let predictor = p.dict?["Predictor"]?.int, predictor >= 10 {
                data = Self.unpredictPNG(data, columns: p.dict?["Columns"]?.int ?? 1, colors: p.dict?["Colors"]?.int ?? 1,
                                         bitsPerComponent: p.dict?["BitsPerComponent"]?.int ?? 8)
            }
        }
        return data
    }

    // MARK: Pages

    func pageObjectNumber(at index: Int) -> Int? {
        guard let root = resolve(trailer["Root"] ?? .null).dict, let pages = root["Pages"]?.ref?.0 else { return nil }
        var remaining = index
        func walk(_ number: Int, depth: Int) -> Int? {
            guard depth < 64, let node = object(number)?.dict else { return nil }
            if node["Type"]?.name == "Page" || node["Kids"] == nil {
                if remaining == 0 { return number }
                remaining -= 1
                return nil
            }
            for kid in resolve(node["Kids"] ?? .null).array ?? [] {
                guard let kidNumber = kid.ref?.0 else { continue }
                if let count = object(kidNumber)?.dict?["Count"]?.int, object(kidNumber)?.dict?["Type"]?.name == "Pages", count <= remaining {
                    remaining -= count
                    continue
                }
                if let found = walk(kidNumber, depth: depth + 1) { return found }
            }
            return nil
        }
        return walk(pages, depth: 0)
    }

    /// Object numbers of the page's content streams, in order.
    func contentStreams(ofPage number: Int) -> [Int] {
        guard let page = object(number)?.dict, let contents = page["Contents"] else { return [] }
        if let ref = contents.ref {
            if let array = object(ref.0)?.array { return array.compactMap { $0.ref?.0 } }
            return [ref.0]
        }
        return contents.array?.compactMap { $0.ref?.0 } ?? []
    }

    // MARK: Incremental update

    /// Appends new versions of streams (raw decoded content, re-compressed) as an incremental update.
    func incrementalUpdate(replacingStreams streams: [Int: Data]) throws -> Data {
        guard trailer["Encrypt"] == nil else { throw PDFFileError.unsupported("document chiffré") }
        guard !usesXrefStream else { throw PDFFileError.unsupported("table xref compressée") }
        var output = Data(bytes)
        if output.last != 10 { output.append(10) }
        var offsets: [(Int, Int)] = []
        for (number, content) in streams.sorted(by: { $0.key < $1.key }) {
            let compressed = Self.deflate(content)
            offsets.append((number, output.count))
            output.append(Data("\(number) 0 obj\n<</Length \(compressed.count)/Filter/FlateDecode>>stream\n".utf8))
            output.append(compressed)
            output.append(Data("\nendstream\nendobj\n".utf8))
        }
        let xrefOffset = output.count
        var xref = "xref\n0 1\n0000000000 65535 f \n"
        for (number, offset) in offsets {
            xref += "\(number) 1\n" + String(format: "%010d 00000 n \n", offset)
        }
        var newTrailer = trailer
        newTrailer["Prev"] = .number(Double(lastXrefOffset))
        newTrailer.removeValue(forKey: "XRefStm")
        xref += "trailer\n" + Self.serialize(.dict(newTrailer)) + "\nstartxref\n\(xrefOffset)\n%%EOF\n"
        output.append(Data(xref.utf8))
        return output
    }

    static func serialize(_ object: PDFObject) -> String {
        switch object {
        case .null: return "null"
        case .bool(let v): return v ? "true" : "false"
        case .number(let v): return v == v.rounded() ? String(Int(v)) : String(format: "%.4f", v)
        case .name(let v): return "/" + v
        case .string(let v): return "<" + v.map { String(format: "%02X", $0) }.joined() + ">"
        case .array(let items): return "[" + items.map(serialize).joined(separator: " ") + "]"
        case .dict(let dict), .stream(let dict, _):
            return "<<" + dict.sorted { $0.key < $1.key }.map { "/\($0.key) \(serialize($0.value))" }.joined(separator: " ") + ">>"
        case .ref(let n, let g): return "\(n) \(g) R"
        }
    }

    // MARK: Compression

    /// zlib-wrapped data → raw DEFLATE → bytes (Apple's ZLIB is raw DEFLATE).
    static func inflate(_ data: Data) -> Data? {
        guard data.count > 2 else { return Data() }
        let body = data.subdata(in: 2..<data.count)
        return try? (body as NSData).decompressed(using: .zlib) as Data
    }

    static func deflate(_ data: Data) -> Data {
        var output = Data([0x78, 0x9C])
        output.append((try? (data as NSData).compressed(using: .zlib) as Data) ?? Data())
        var a: UInt32 = 1, b: UInt32 = 0
        for byte in data {
            a = (a + UInt32(byte)) % 65521
            b = (b + a) % 65521
        }
        withUnsafeBytes(of: (b << 16 | a).bigEndian) { output.append(contentsOf: $0) }
        return output
    }

    static func unpredictPNG(_ data: Data, columns: Int, colors: Int, bitsPerComponent: Int) -> Data {
        let bpp = max(1, colors * bitsPerComponent / 8)
        let rowLength = (columns * colors * bitsPerComponent + 7) / 8
        let raw = [UInt8](data)
        var output = [UInt8]()
        var previous = [UInt8](repeating: 0, count: rowLength)
        var i = 0
        while i + rowLength < raw.count + 1, i < raw.count {
            let type = raw[i]
            var row = Array(raw[(i + 1)..<min(i + 1 + rowLength, raw.count)])
            if row.count < rowLength { row += [UInt8](repeating: 0, count: rowLength - row.count) }
            for x in 0..<rowLength {
                let left = x >= bpp ? row[x - bpp] : 0
                let up = previous[x]
                let upLeft = x >= bpp ? previous[x - bpp] : 0
                switch type {
                case 1: row[x] = row[x] &+ left
                case 2: row[x] = row[x] &+ up
                case 3: row[x] = row[x] &+ UInt8((Int(left) + Int(up)) / 2)
                case 4:
                    let p = Int(left) + Int(up) - Int(upLeft)
                    let pa = abs(p - Int(left)), pb = abs(p - Int(up)), pc = abs(p - Int(upLeft))
                    row[x] = row[x] &+ (pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft)
                default: break
                }
            }
            output += row
            previous = row
            i += 1 + rowLength
        }
        return Data(output)
    }
}
