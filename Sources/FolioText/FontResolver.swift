import CoreGraphics
import CoreText
import CPDFium
import Foundation

/// A font usable for new text in a document, with the advance widths needed for line layout.
struct ResolvedFont {
    let handle: FPDF_FONT
    /// Advance of a character for a 1 pt font size, or nil if unknown.
    let advance: (Character) -> CGFloat?
    /// Fallback advance (1 pt) when a character was never seen.
    let averageAdvance: CGFloat
    let description: String

    func width(of text: Substring, size: CGFloat) -> CGFloat {
        text.reduce(0) { $0 + (advance($1) ?? averageAdvance) } * size
    }
}

/// Picks fonts for edited text: the PDF's own font when it can encode the text (pixel-identical result),
/// else the same font installed on the Mac, else the closest system font by style.
final class FontResolver {
    private let document: PDFiumDocument
    private let page: PDFiumPage
    private var systemFonts: [String: ResolvedFont] = [:]
    private lazy var learned: (advances: [String: [Character: CGFloat]], coverage: [String: Set<Character>]) = learnAdvances()

    init(page: PDFiumPage) {
        self.document = page.document
        self.page = page
    }

    /// Characters seen in the page text for each font, i.e. the glyphs an embedded subset surely has.
    func knownCharacters(of font: FontInfo) -> Set<Character> {
        learned.coverage[font.baseName] ?? []
    }

    /// The original embedded font, if it can encode every character of `text`.
    func embedded(_ handle: FPDF_FONT, info: FontInfo, for text: String) -> ResolvedFont? {
        let known = learned.advances[info.baseName] ?? [:]
        let needed = Set(text.filter { !$0.isNewline })
        guard !known.isEmpty, needed.isSubset(of: knownCharacters(of: info).union([" "])) else { return nil }
        let average = known.values.sorted()[known.count / 2]
        let space = known[" "] ?? average * 0.5
        return ResolvedFont(
            handle: handle,
            advance: { $0 == " " ? space : known[$0] },
            averageAdvance: average,
            description: "\(info.postScriptName) (embedded)"
        )
    }

    /// A system font matching `info` as closely as possible, embedded into the document once.
    func system(matching info: FontInfo, bold: Bool? = nil, italic: Bool? = nil) -> ResolvedFont? {
        let ctFont = Self.closestSystemFont(to: info, bold: bold ?? info.isBold, italic: italic ?? info.isItalic)
        let name = CTFontCopyPostScriptName(ctFont) as String
        if let cached = systemFonts[name] { return cached }
        guard let data = Self.sfntData(of: ctFont) else { return nil }
        let handle = data.withUnsafeBytes { bytes in
            FPDFText_LoadFont(document.handle, bytes.bindMemory(to: UInt8.self).baseAddress, UInt32(data.count), FPDF_FONT_TRUETYPE, 1)
        }
        guard let handle else { return nil }
        let unit = CTFontCreateCopyWithAttributes(ctFont, 1, nil, nil)
        let resolved = ResolvedFont(
            handle: handle,
            advance: { Self.advance(of: $0, in: unit) },
            averageAdvance: Self.advance(of: "n", in: unit) ?? 0.5,
            description: "\(name) (system)"
        )
        systemFonts[name] = resolved
        return resolved
    }

    // MARK: Matching

    static func closestSystemFont(to info: FontInfo, bold: Bool, italic: Bool) -> CTFont {
        var traits: CTFontSymbolicTraits = []
        if bold { traits.insert(.traitBold) }
        if italic { traits.insert(.traitItalic) }

        // 1. Exact PostScript name, when the requested style matches the PDF's.
        if bold == info.isBold, italic == info.isItalic {
            let exact = CTFontCreateWithName(info.postScriptName as CFString, 12, nil)
            if (CTFontCopyPostScriptName(exact) as String).caseInsensitiveCompare(info.postScriptName) == .orderedSame {
                return exact
            }
        }
        // 2. Same family with the requested traits.
        for family in candidateFamilies(info) {
            let base = CTFontCreateWithName(family as CFString, 12, nil)
            guard (CTFontCopyFamilyName(base) as String).caseInsensitiveCompare(family) == .orderedSame else { continue }
            if let styled = CTFontCreateCopyWithSymbolicTraits(base, 12, nil, traits, [.traitBold, .traitItalic]) {
                return styled
            }
            if traits.isEmpty { return base }
        }
        // 3. Generic fallback by classification.
        let lower = info.postScriptName.lowercased()
        let generic: String
        if info.isMonospaced || ["courier", "mono", "consol", "menlo"].contains(where: lower.contains) {
            generic = "Courier New"
        } else if info.isSerif || ["times", "georgia", "garamond", "serif", "roman", "cambria", "book", "minion"].contains(where: lower.contains) {
            generic = "Times New Roman"
        } else {
            generic = "Arial"
        }
        let base = CTFontCreateWithName(generic as CFString, 12, nil)
        return CTFontCreateCopyWithSymbolicTraits(base, 12, nil, traits, [.traitBold, .traitItalic]) ?? base
    }

    /// Family names to try: the PDF's family, and the PostScript name without its style suffix.
    private static func candidateFamilies(_ info: FontInfo) -> [String] {
        var names = [info.familyName]
        let stem = info.postScriptName.split(separator: "-").first.map(String.init) ?? info.postScriptName
        names.append(stem)
        // "TimesNewRomanPSMT" → "Times New Roman"
        let cleaned = stem.replacingOccurrences(of: "PSMT", with: "").replacingOccurrences(of: "MT", with: "")
        let spaced = cleaned.replacingOccurrences(of: "([a-z])([A-Z])", with: "$1 $2", options: .regularExpression)
        names.append(spaced)
        return names.filter { !$0.isEmpty }
    }

    private static func advance(of character: Character, in font: CTFont) -> CGFloat? {
        let utf16 = Array(String(character).utf16)
        var glyphs = [CGGlyph](repeating: 0, count: utf16.count)
        guard CTFontGetGlyphsForCharacters(font, utf16, &glyphs, utf16.count), glyphs[0] != 0 else { return nil }
        var advance = CGSize.zero
        CTFontGetAdvancesForGlyphs(font, .horizontal, glyphs, &advance, 1)
        return advance.width
    }

    // MARK: Learning advances from the page

    /// Per font, the advance (1 pt) of each character, measured between consecutive characters of the
    /// page text, and every character drawn with it (the glyphs an embedded subset surely contains).
    private func learnAdvances() -> (advances: [String: [Character: CGFloat]], coverage: [String: Set<Character>]) {
        guard let textPage = page.textPage else { return ([:], [:]) }
        var samples: [String: [Character: [CGFloat]]] = [:]
        var coverage: [String: Set<Character>] = [:]
        var objectInfo: [OpaquePointer: (name: String, size: Double)] = [:]
        func info(_ object: FPDF_PAGEOBJECT) -> (name: String, size: Double) {
            if let cached = objectInfo[object] { return cached }
            var nominal: Float = 0
            FPDFTextObj_GetFontSize(object, &nominal)
            var m = FS_MATRIX()
            FPDFPageObj_GetMatrix(object, &m)
            // The text matrix may carry the size (e.g. "1 Tf" with a 12× matrix).
            let result = (utf8String { FPDFFont_GetBaseFontName(FPDFTextObj_GetFont(object), $0, $1) },
                          Double(nominal) * Double(hypot(m.c, m.d)))
            objectInfo[object] = result
            return result
        }
        let count = Int(FPDFText_CountChars(textPage))
        var previous: (object: FPDF_PAGEOBJECT, character: Character, x: Double, y: Double, size: Double)?

        for index in 0..<count {
            guard let scalar = Unicode.Scalar(FPDFText_GetUnicode(textPage, Int32(index))),
                  let object = FPDFText_GetTextObject(textPage, Int32(index)) else { previous = nil; continue }
            var x = 0.0, y = 0.0
            FPDFText_GetCharOrigin(textPage, Int32(index), &x, &y)
            let (name, size) = info(object)
            let character = Character(scalar)
            if !character.isNewline { coverage[name, default: []].insert(character) }

            if let p = previous, p.object == object, abs(p.y - y) < 0.01, x > p.x, p.size > 0 {
                samples[name, default: [:]][p.character, default: []].append(CGFloat((x - p.x) / p.size))
            }
            // Generated characters (synthetic spaces/line breaks) have no object of their own position.
            previous = character.isNewline ? nil : (object, character, x, y, size)
        }

        let advances = samples.mapValues { perCharacter in
            perCharacter.mapValues { values in
                let sorted = values.sorted()
                // Kerning and justification only widen gaps: lean on the lower values.
                return sorted[sorted.count / 4]
            }
        }
        return (advances, coverage)
    }

    // MARK: Font data

    /// A standalone sfnt rebuilt from the font's tables, so fonts inside .ttc collections can be embedded.
    static func sfntData(of font: CTFont) -> Data? {
        guard let tagArray = CTFontCopyAvailableTables(font, []) else { return nil }
        var tables: [(tag: UInt32, data: Data)] = []
        for index in 0..<CFArrayGetCount(tagArray) {
            let tag = UInt32(UInt(bitPattern: CFArrayGetValueAtIndex(tagArray, index)))
            if let data = CTFontCopyTable(font, tag, []) as Data? { tables.append((tag, data)) }
        }
        guard tables.contains(where: { $0.tag == 0x676C_7966 }) else { return nil } // TrueType outlines ('glyf') only
        tables.sort { $0.tag < $1.tag }

        var output = Data()
        func append32(_ value: UInt32) { withUnsafeBytes(of: value.bigEndian) { output.append(contentsOf: $0) } }
        func append16(_ value: UInt16) { withUnsafeBytes(of: value.bigEndian) { output.append(contentsOf: $0) } }

        let count = UInt16(tables.count)
        var power: UInt16 = 1, log: UInt16 = 0
        while power * 2 <= count { power *= 2; log += 1 }
        append32(0x0001_0000)
        append16(count)
        append16(power * 16)
        append16(log)
        append16(count * 16 - power * 16)

        var offset = 12 + 16 * tables.count
        var bodies = Data()
        for table in tables {
            var padded = table.data
            while padded.count % 4 != 0 { padded.append(0) }
            let checksum = stride(from: 0, to: padded.count, by: 4).reduce(UInt32(0)) { sum, i in
                sum &+ (UInt32(padded[padded.startIndex + i]) << 24 | UInt32(padded[padded.startIndex + i + 1]) << 16
                    | UInt32(padded[padded.startIndex + i + 2]) << 8 | UInt32(padded[padded.startIndex + i + 3]))
            }
            append32(table.tag)
            append32(checksum)
            append32(UInt32(offset))
            append32(UInt32(table.data.count))
            bodies.append(padded)
            offset += padded.count
        }
        output.append(bodies)
        return output
    }
}
