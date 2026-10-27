import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Rewrites a whole PDF to make it smaller, without touching text or vector content:
/// images downsampled to 150 dpi (relative to the largest page) and recompressed as JPEG,
/// unreachable objects and editor private data dropped, identical streams merged,
/// uncompressed streams deflated, other objects packed into object streams.
public enum PDFCompressor {
    static let targetDPI: Double = 150
    static let jpegQuality: Double = 0.6
    /// Dropped from every dictionary: editor private data (Illustrator…) and page thumbnails.
    static let droppedKeys: Set<String> = ["PieceInfo", "Thumb"]
    static let objectsPerStream = 200

    /// An encrypted document is decrypted with `password` first; the result is not encrypted.
    public static func compress(_ data: Data, password: String? = nil) throws -> Data {
        var file = try PDFFile(data: data)
        if file.trailer["Encrypt"] != nil {
            guard let decrypted = PDFiumDocument(data: data, password: password)?.decryptedData() else {
                throw PDFFileError.unsupported("document chiffré")
            }
            file = try PDFFile(data: decrypted)
        }
        guard let root = file.trailer["Root"]?.ref?.0 else { throw PDFFileError.malformed("Root") }

        // Reachable objects, cleaned.
        var objects: [Int: PDFObject] = [:]
        var queue = [root]
        if let info = file.trailer["Info"]?.ref?.0 { queue.append(info) }
        var largestPage: Double = 0
        var softMasks = Set<Int>()
        while let number = queue.popLast() {
            guard objects[number] == nil, var object = file.object(number) else { continue }
            if number == root, case .dict(var dict) = object {
                dict.removeValue(forKey: "Metadata")
                object = .dict(dict)
            }
            object = cleaned(object)
            objects[number] = object
            if let dict = object.dict {
                if dict["Type"]?.name == "Page" || dict["Type"]?.name == "Pages",
                   let box = file.resolve(dict["MediaBox"] ?? .null).array?.compactMap({ file.resolve($0).double }), box.count == 4 {
                    largestPage = max(largestPage, abs(box[2] - box[0]), abs(box[3] - box[1]))
                }
                if let mask = dict["SMask"]?.ref?.0 { softMasks.insert(mask) }
            }
            collectReferences(object, into: &queue)
        }

        // Streams: images recompressed, the rest deflated when not compressed yet.
        let maxSide = Int((largestPage > 0 ? largestPage : 842) / 72 * targetDPI)
        for (number, object) in objects {
            guard case .stream(var dict, let raw) = object else { continue }
            if dict["Subtype"]?.name == "Image", !softMasks.contains(number),
               let image = recompressedImage(dict, raw, file: file, maxSide: maxSide) {
                objects[number] = image
            } else if dict["Filter"] == nil {
                let deflated = PDFFile.deflate(raw)
                guard deflated.count < raw.count else { continue }
                dict["Filter"] = .name("FlateDecode")
                objects[number] = .stream(dict, deflated)
            }
        }

        // Identical streams (fonts or images embedded several times) share one object.
        var duplicates: [Int: Int] = [:]
        var seen: [Data: Int] = [:]
        for number in objects.keys.sorted() {
            guard case .stream(let dict, let raw) = objects[number]! else { continue }
            var key = Data(serialize(.dict(dict)).utf8)
            key.append(raw)
            if let first = seen[key] { duplicates[number] = first } else { seen[key] = number }
        }

        // Renumbering from 1, streams first.
        let kept = objects.keys.filter { duplicates[$0] == nil }.sorted()
        let streams = kept.filter { if case .stream = objects[$0]! { true } else { false } }
        let others = kept.filter { if case .stream = objects[$0]! { false } else { true } }
        var newNumbers: [Int: Int] = [:]
        for (i, number) in (streams + others).enumerated() { newNumbers[number] = i + 1 }
        for (duplicate, first) in duplicates { newNumbers[duplicate] = newNumbers[first] }
        func remap(_ object: PDFObject) -> PDFObject {
            switch object {
            case .ref(let n, _): return newNumbers[n].map { .ref($0, 0) } ?? .null
            case .array(let items): return .array(items.map(remap))
            case .dict(let dict): return .dict(dict.mapValues(remap))
            case .stream(let dict, let data): return .stream(dict.mapValues(remap), data)
            default: return object
            }
        }

        var output = Data("%PDF-1.7\n".utf8)
        output.append(contentsOf: [0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A])
        let chunks = stride(from: 0, to: others.count, by: objectsPerStream).map {
            Array(others[$0..<min($0 + objectsPerStream, others.count)])
        }
        let xrefNumber = kept.count + chunks.count + 1
        // Cross-reference rows (type, field 2, field 3), indexed by object number.
        var rows = [(Int, Int, Int)](repeating: (0, 0, 0), count: xrefNumber + 1)
        rows[0] = (0, 0, 65535)

        func writeStream(_ number: Int, _ dict: [String: PDFObject], _ data: Data) {
            rows[number] = (1, output.count, 0)
            var dict = dict
            dict["Length"] = .number(Double(data.count))
            output.append(Data("\(number) 0 obj\n\(serialize(.dict(dict)))\nstream\n".utf8))
            output.append(data)
            output.append(Data("\nendstream\nendobj\n".utf8))
        }

        for number in streams {
            guard case .stream(let dict, let data) = remap(objects[number]!) else { continue }
            writeStream(newNumbers[number]!, dict, data)
        }
        for (k, chunk) in chunks.enumerated() {
            let streamNumber = kept.count + k + 1
            var header = "", body = ""
            for (index, number) in chunk.enumerated() {
                let newNumber = newNumbers[number]!
                header += "\(newNumber) \(body.utf8.count) "
                body += serialize(remap(objects[number]!)) + "\n"
                rows[newNumber] = (2, streamNumber, index)
            }
            writeStream(streamNumber, [
                "Type": .name("ObjStm"), "N": .number(Double(chunk.count)),
                "First": .number(Double(header.utf8.count)), "Filter": .name("FlateDecode"),
            ], PDFFile.deflate(Data((header + body).utf8)))
        }

        let xrefOffset = output.count
        rows[xrefNumber] = (1, xrefOffset, 0)
        var table = Data()
        for (type, a, b) in rows {
            table.append(UInt8(type))
            withUnsafeBytes(of: UInt32(a).bigEndian) { table.append(contentsOf: $0) }
            withUnsafeBytes(of: UInt16(b).bigEndian) { table.append(contentsOf: $0) }
        }
        var trailer: [String: PDFObject] = [
            "Type": .name("XRef"), "Size": .number(Double(xrefNumber + 1)),
            "W": .array([.number(1), .number(4), .number(2)]), "Filter": .name("FlateDecode"),
            "Root": remap(.ref(root, 0)),
        ]
        if let info = file.trailer["Info"] { trailer["Info"] = remap(info) }
        if let id = file.trailer["ID"] { trailer["ID"] = id }
        writeStream(xrefNumber, trailer, PDFFile.deflate(table))
        output.append(Data("startxref\n\(xrefOffset)\n%%EOF\n".utf8))
        return output
    }

    // MARK: Objects

    static func cleaned(_ object: PDFObject) -> PDFObject {
        switch object {
        case .dict(let dict): return .dict(cleaned(dict))
        case .stream(let dict, let data):
            var dict = cleaned(dict)
            dict.removeValue(forKey: "Length")
            return .stream(dict, data)
        case .array(let items): return .array(items.map(cleaned))
        default: return object
        }
    }

    static func cleaned(_ dict: [String: PDFObject]) -> [String: PDFObject] {
        dict.filter { !droppedKeys.contains($0.key) }.mapValues(cleaned)
    }

    static func collectReferences(_ object: PDFObject, into numbers: inout [Int]) {
        switch object {
        case .ref(let n, _): numbers.append(n)
        case .array(let items): items.forEach { collectReferences($0, into: &numbers) }
        case .dict(let dict), .stream(let dict, _): dict.values.forEach { collectReferences($0, into: &numbers) }
        default: break
        }
    }

    /// Like `PDFFile.serialize`, with exact numbers and escaped names.
    static func serialize(_ object: PDFObject) -> String {
        switch object {
        case .null: return "null"
        case .bool(let v): return v ? "true" : "false"
        case .number(let v):
            if v == v.rounded(), abs(v) < 1e15 { return String(Int(v)) }
            var text = String(format: "%.6f", v)
            while text.hasSuffix("0") { text.removeLast() }
            if text.hasSuffix(".") { text.removeLast() }
            return text == "-0" ? "0" : text
        case .name(let v):
            return "/" + v.utf8.map { b in
                b < 0x21 || b > 0x7E || "#()<>[]{}/%".utf8.contains(b) ? String(format: "#%02X", b) : String(UnicodeScalar(b))
            }.joined()
        case .string(let v): return "<" + v.map { String(format: "%02X", $0) }.joined() + ">"
        case .array(let items): return "[" + items.map(serialize).joined(separator: " ") + "]"
        case .dict(let dict), .stream(let dict, _):
            return "<<" + dict.sorted { $0.key < $1.key }.map { "\(serialize(.name($0.key))) \(serialize($0.value))" }.joined(separator: " ") + ">>"
        case .ref(let n, let g): return "\(n) \(g) R"
        }
    }

    // MARK: Images

    /// The image downsampled to `maxSide` pixels and saved as JPEG, or nil when unsupported or not smaller.
    /// Masks, exotic color spaces (CMYK, Indexed, Lab…) and JPEG 2000 are left untouched.
    static func recompressedImage(_ dict: [String: PDFObject], _ raw: Data, file: PDFFile, maxSide: Int) -> PDFObject? {
        if case .bool(true) = file.resolve(dict["ImageMask"] ?? .null) { return nil }
        guard dict["Decode"] == nil, file.resolve(dict["Mask"] ?? .null).array == nil,
              let width = file.resolve(dict["Width"] ?? .null).int, let height = file.resolve(dict["Height"] ?? .null).int,
              width > 0, height > 0,
              let components = componentCount(file.resolve(dict["ColorSpace"] ?? .null), file: file) else { return nil }
        let filter = file.resolve(dict["Filter"] ?? .null)
        let filters = filter.name.map { [$0] } ?? filter.array?.compactMap { file.resolve($0).name } ?? []

        let image: CGImage
        let space: CGColorSpace
        if filters == ["DCTDecode"] || filters == ["DCT"] {
            guard let source = CGImageSourceCreateWithData(raw as CFData, nil),
                  let decoded = CGImageSourceCreateImageAtIndex(source, 0, nil),
                  let decodedSpace = decoded.colorSpace,
                  decodedSpace.model == (components == 1 ? .monochrome : .rgb) else { return nil }
            // Same color space as the source: pixel values are kept, no color conversion.
            (image, space) = (decoded, decodedSpace)
        } else if filters == ["FlateDecode"] || filters == ["Fl"],
                  file.resolve(dict["BitsPerComponent"] ?? .null).int == 8,
                  (file.resolve(dict["DecodeParms"] ?? .null).dict?["Predictor"]?.int ?? 1) != 2,
                  let pixels = file.decodedStream(.stream(dict, raw)), pixels.count >= width * height * components {
            space = components == 1 ? CGColorSpaceCreateDeviceGray() : CGColorSpaceCreateDeviceRGB()
            guard let provider = CGDataProvider(data: pixels as CFData),
                  let decoded = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 8 * components,
                                        bytesPerRow: width * components, space: space, bitmapInfo: [], provider: provider,
                                        decode: nil, shouldInterpolate: false, intent: .defaultIntent) else { return nil }
            image = decoded
        } else {
            return nil
        }

        let scale = min(1, Double(maxSide) / Double(max(width, height)))
        let w = max(1, Int((Double(width) * scale).rounded())), h = max(1, Int((Double(height) * scale).rounded()))
        let alpha = components == 1 ? CGImageAlphaInfo.none : .noneSkipLast
        guard let context = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: space, bitmapInfo: alpha.rawValue) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        let jpeg = NSMutableData()
        guard let resized = context.makeImage(),
              let destination = CGImageDestinationCreateWithData(jpeg, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, resized, [kCGImageDestinationLossyCompressionQuality: jpegQuality] as CFDictionary)
        guard CGImageDestinationFinalize(destination), jpeg.length < raw.count * 9 / 10 else { return nil }

        var result = dict
        result["Filter"] = .name("DCTDecode")
        result.removeValue(forKey: "DecodeParms")
        result["Width"] = .number(Double(w))
        result["Height"] = .number(Double(h))
        result["BitsPerComponent"] = .number(8)
        return .stream(result, jpeg as Data)
    }

    static func componentCount(_ space: PDFObject, file: PDFFile) -> Int? {
        if let name = space.name { return ["DeviceRGB": 3, "RGB": 3, "DeviceGray": 1, "G": 1][name] }
        guard let array = space.array, let family = array.first?.name else { return nil }
        switch family {
        case "CalRGB": return 3
        case "CalGray": return 1
        case "ICCBased":
            guard array.count > 1, let n = file.resolve(array[1]).dict?["N"].flatMap({ file.resolve($0).int }) else { return nil }
            return n == 1 || n == 3 ? n : nil
        default: return nil
        }
    }
}
