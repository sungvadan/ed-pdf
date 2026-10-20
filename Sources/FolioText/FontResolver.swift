import CoreGraphics
import CoreText
import CPDFium
import Foundation

/// A font usable for new text in a document.
final class ResolvedFont {
    let handle: FPDF_FONT
    let description: String
    /// The font has a space glyph. Otherwise (e.g. subsets made by Chrome) words are written as separate
    /// objects and the gap stands for the space.
    let writesSpaces: Bool
    /// Whether the font has a glyph for a character.
    let covers: (Character) -> Bool
    private let document: FPDF_DOCUMENT
    /// A character with ink, used as a right-hand marker when measuring.
    private let marker: String
    private var cache: [Substring: CGFloat] = [:]

    init(handle: FPDF_FONT, document: FPDF_DOCUMENT, marker: Character, description: String, writesSpaces: Bool = true,
         covers: @escaping (Character) -> Bool) {
        self.handle = handle
        self.document = document
        self.marker = String(marker)
        self.description = description
        self.writesSpaces = writesSpaces
        self.covers = covers
    }

    /// Exact advance width, as PDF readers lay the text out: PDFium bounds stop at the last glyph's ink,
    /// so the width of `text` is right(text + marker) − right(marker).
    func width(of text: Substring, size: CGFloat) -> CGFloat {
        guard !text.isEmpty else { return 0 }
        if let cached = cache[text] { return cached * size }
        let unit = (inkRight(String(text) + marker) - inkRight(marker)) / 1000
        cache[text] = unit
        return unit * size
    }

    private func inkRight(_ text: String) -> CGFloat {
        guard let object = FPDFPageObj_CreateTextObj(document, handle, 1000) else { return 0 }
        defer { FPDFPageObj_Destroy(object) }
        _ = withWideString(text) { FPDFText_SetText(object, $0) }
        var left: Float = 0, bottom: Float = 0, right: Float = 0, top: Float = 0
        FPDFPageObj_GetBounds(object, &left, &bottom, &right, &top)
        return CGFloat(right)
    }
}

/// Picks fonts for edited text: the PDF's own font when it can encode the text (pixel-identical result),
/// else the same font installed on the Mac, else the closest system font by style.
final class FontResolver {
    private let document: PDFiumDocument
    private let page: PDFiumPage
    private var systemFonts: [String: ResolvedFont] = [:]
    /// Per font resource: one PDF can hold several subsets of the same font under the same name
    /// (CoreText puts "€" or "→" in a second subset), so coverage is keyed by font handle, not name.
    private lazy var coverage: [OpaquePointer: Set<Character>] = learnCoverage()

    init(page: PDFiumPage) {
        self.document = page.document
        self.page = page
    }

    /// Characters drawn on the page with a font resource, i.e. the glyphs an embedded subset surely has.
    func knownCharacters(of font: FPDF_FONT) -> Set<Character> {
        coverage[font] ?? []
    }

    /// The original embedded font, if it can encode every character of `text`.
    func embedded(_ handle: FPDF_FONT, info: FontInfo, for text: String) -> ResolvedFont? {
        let coverage = knownCharacters(of: handle)
        let needed = Set(text.filter { !$0.isNewline })
        guard !coverage.isEmpty, needed.isSubset(of: coverage.union([" "])),
              let marker = ["o", "n", "e", "a", "x", "0"].first(where: coverage.contains) ?? coverage.first(where: { $0.isLetter || $0.isNumber }) else { return nil }
        return ResolvedFont(handle: handle, document: document.handle, marker: marker,
                            description: "\(info.postScriptName) (embedded)", writesSpaces: coverage.contains(" "),
                            covers: { $0 == " " || coverage.contains($0) })
    }

    /// A system font matching `info` as closely as possible, embedded into the document once.
    func system(matching info: FontInfo, bold: Bool? = nil, italic: Bool? = nil) -> ResolvedFont? {
        system(Self.closestSystemFont(to: info, bold: bold ?? info.isBold, italic: italic ?? info.isItalic))
    }

    func system(named postScriptName: String) -> ResolvedFont? {
        let font = CTFontCreateWithName(postScriptName as CFString, 12, nil)
        guard (CTFontCopyPostScriptName(font) as String) == postScriptName else { return nil }
        return system(font)
    }

    /// An installed font with a glyph for `character`, for characters the paragraph's font lacks
    /// (as macOS does when you type "✓" in Times). Only TrueType-outline fonts can be embedded.
    func fallback(for character: Character, near base: CTFont) -> (name: String, font: ResolvedFont)? {
        let string = String(character) as CFString
        let suggested = CTFontCreateForString(base, string, CFRange(location: 0, length: CFStringGetLength(string)))
        let candidates = [CTFontCopyPostScriptName(suggested) as String, "ArialUnicodeMS", "Arial", "TimesNewRomanPSMT", "Menlo-Regular", "AppleSymbols"]
        for name in candidates {
            if let font = system(named: name), font.covers(character) { return (name, font) }
        }
        return nil
    }

    private func system(_ ctFont: CTFont) -> ResolvedFont? {
        let name = CTFontCopyPostScriptName(ctFont) as String
        if let cached = systemFonts[name] { return cached }
        guard let data = Self.sfntData(of: ctFont) else { return nil }
        let handle = data.withUnsafeBytes { bytes in
            FPDFText_LoadFont(document.handle, bytes.bindMemory(to: UInt8.self).baseAddress, UInt32(data.count), FPDF_FONT_TRUETYPE, 1)
        }
        guard let handle else { return nil }
        let resolved = ResolvedFont(handle: handle, document: document.handle, marker: "o", description: "\(name) (system)",
                                    covers: { Self.hasGlyph($0, in: ctFont) })
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

    static func hasGlyph(_ character: Character, in font: CTFont) -> Bool {
        let utf16 = Array(String(character).utf16)
        var glyphs = [CGGlyph](repeating: 0, count: utf16.count)
        return CTFontGetGlyphsForCharacters(font, utf16, &glyphs, utf16.count) && glyphs.allSatisfy { $0 != 0 }
    }

    // MARK: Coverage

    /// Per font, every character actually drawn on the page with it: the glyphs an embedded subset surely contains.
    private func learnCoverage() -> [OpaquePointer: Set<Character>] {
        guard let textPage = page.textPage else { return [:] }
        var coverage: [OpaquePointer: Set<Character>] = [:]
        for index in 0..<Int(FPDFText_CountChars(textPage)) {
            // Spaces generated by the text extraction are not glyphs of the font.
            guard FPDFText_IsGenerated(textPage, Int32(index)) != 1,
                  let scalar = Unicode.Scalar(FPDFText_GetUnicode(textPage, Int32(index))),
                  let object = FPDFText_GetTextObject(textPage, Int32(index)) else { continue }
            let character = Character(scalar)
            guard !character.isNewline else { continue }
            guard let font = FPDFTextObj_GetFont(object) else { continue }
            coverage[font, default: []].insert(character)
        }
        return coverage
    }

    // MARK: Font data

    /// A standalone sfnt rebuilt from the font's tables, so fonts inside .ttc collections can be embedded.
    /// A cmap (format 12) mapping each glyph from a single character, the lowest code point.
    static func uniqueCmap(of font: CTFont) -> Data? {
        let characters = CTFontCopyCharacterSet(font)
        var codeOfGlyph: [CGGlyph: UInt32] = [:]
        var pairs: [(UInt32, CGGlyph)] = []
        for code in UInt32(0)...0x1FFFF {
            // Control characters often share the space glyph; they must never win it.
            guard code >= 0x20, !(0x7F...0x9F).contains(code),
                  CFCharacterSetIsLongCharacterMember(characters, code), let scalar = Unicode.Scalar(code) else { continue }
            let utf16 = Array(String(scalar).utf16)
            var glyphs = [CGGlyph](repeating: 0, count: utf16.count)
            guard CTFontGetGlyphsForCharacters(font, utf16, &glyphs, utf16.count), glyphs[0] != 0 else { continue }
            if codeOfGlyph[glyphs[0]] == nil { codeOfGlyph[glyphs[0]] = code }
            pairs.append((code, glyphs[0]))
        }
        let kept = pairs.filter { codeOfGlyph[$0.1] == $0.0 }
        guard !kept.isEmpty else { return nil }

        var groups: [(start: UInt32, end: UInt32, glyph: UInt32)] = []
        for (code, glyph) in kept {
            if let last = groups.last, last.end + 1 == code, last.glyph + (last.end - last.start) + 1 == UInt32(glyph) {
                groups[groups.count - 1].end = code
            } else {
                groups.append((code, code, UInt32(glyph)))
            }
        }
        var data = Data()
        func put16(_ v: UInt16) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
        func put32(_ v: UInt32) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
        put16(0); put16(1)                 // version, one subtable
        put16(3); put16(10); put32(12)     // Windows, Unicode full repertoire, offset
        put16(12); put16(0)                // format 12
        put32(UInt32(16 + 12 * groups.count)); put32(0); put32(UInt32(groups.count))
        for group in groups { put32(group.start); put32(group.end); put32(group.glyph) }
        return data
    }

    static func sfntData(of font: CTFont) -> Data? {
        guard let tagArray = CTFontCopyAvailableTables(font, []) else { return nil }
        var tables: [(tag: UInt32, data: Data)] = []
        for index in 0..<CFArrayGetCount(tagArray) {
            let tag = UInt32(UInt(bitPattern: CFArrayGetValueAtIndex(tagArray, index)))
            if let data = CTFontCopyTable(font, tag, []) as Data? { tables.append((tag, data)) }
        }
        guard tables.contains(where: { $0.tag == 0x676C_7966 }) else { return nil } // TrueType outlines ('glyf') only
        // PDFium derives the ToUnicode map from the cmap; with one glyph shared by several characters
        // (space/no-break space, hyphen/soft hyphen) it may pick the wrong one. Keep one character per glyph.
        if let index = tables.firstIndex(where: { $0.tag == 0x636D_6170 }), let cmap = uniqueCmap(of: font) {
            tables[index].data = cmap
        }
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

/// Fonts for showing editable text on screen.
public enum TextDisplay {
    /// The installed font closest to a style (the same one used when the PDF's font cannot be reused).
    public static func font(for style: RunStyle, size: CGFloat) -> CTFont {
        let font = FontResolver.closestSystemFont(to: style.font, bold: style.bold ?? style.font.isBold, italic: style.italic ?? style.font.isItalic)
        return CTFontCreateCopyWithAttributes(font, size, nil, nil)
    }
}

// MARK: - Diagnostics

extension PDFiumPage {
    /// Width of `text` with the embedded font of the first run named `fontName`.
    public func debugMeasure(_ text: String, fontName: String) -> String {
        guard let run = textRuns().first(where: { $0.font.baseName == fontName || $0.font.postScriptName == fontName }),
              let object = FPDFPage_GetObject(handle, Int32(run.path[0])), let font = FPDFTextObj_GetFont(object),
              let resolved = FontResolver(page: self).embedded(font, info: run.font, for: text) else { return "font not found or text not covered" }
        return String(format: "width %.2f at %.1f pt", Double(resolved.width(of: Substring(text), size: run.fontSize)), Double(run.fontSize))
    }
}
