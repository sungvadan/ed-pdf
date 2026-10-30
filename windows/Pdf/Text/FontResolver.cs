using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace EdPdf.Pdf.Text;

/// A font usable for new text in a document.
sealed class ResolvedFont(IntPtr handle, IntPtr document, int marker, string description, bool writesSpaces, Func<int, bool> covers)
{
    public IntPtr Handle { get; } = handle;
    public string Description { get; } = description;
    /// The font has a space glyph. Otherwise (e.g. subsets made by Chrome) words are written as separate objects
    /// and the gap stands for the space.
    public bool WritesSpaces { get; } = writesSpaces;
    /// Whether the font has a glyph for a character (code point).
    public Func<int, bool> Covers { get; } = covers;
    /// A character with ink, used as a right-hand marker when measuring.
    readonly string markerText = char.ConvertFromUtf32(marker);
    readonly Dictionary<string, double> cache = [];

    /// Exact advance width, as PDF readers lay the text out: PDFium bounds stop at the last glyph's ink,
    /// so the width of `text` is right(text + marker) − right(marker).
    public double Width(string text, double size)
    {
        if (text.Length == 0) return 0;
        if (!cache.TryGetValue(text, out var unit)) cache[text] = unit = (InkRight(text + markerText) - InkRight(markerText)) / 1000;
        return unit * size;
    }

    double InkRight(string text)
    {
        var obj = TextNative.FPDFPageObj_CreateTextObj(document, Handle, 1000);
        if (obj == IntPtr.Zero) return 0;
        try
        {
            Native.FPDFText_SetText(obj, text);
            Native.FPDFPageObj_GetBounds(obj, out _, out _, out var right, out _);
            return right;
        }
        finally { Native.FPDFPageObj_Destroy(obj); }
    }
}

/// Picks fonts for edited text: the PDF's own font when it can encode the text (pixel-identical result), else the
/// same font installed on the computer, else the closest system font by style (port of FontResolver.swift; system
/// fonts come from Skia instead of CoreText).
sealed class FontResolver(TextPage page)
{
    readonly Dictionary<string, ResolvedFont> systemFonts = [];
    Dictionary<IntPtr, HashSet<int>>? coverage;

    /// Characters drawn on the page with a font resource, i.e. the glyphs an embedded subset surely has.
    HashSet<int> KnownCharacters(IntPtr font) => (coverage ??= LearnCoverage()).GetValueOrDefault(font) ?? [];

    /// The original embedded font, if it can encode every character of `text`.
    public ResolvedFont? Embedded(IntPtr handle, FontInfo info, string text)
    {
        var known = KnownCharacters(handle);
        var needed = Runes(text).Where(c => c is not ('\n' or '\r')).ToHashSet();
        if (known.Count == 0 || !needed.All(c => c == ' ' || known.Contains(c))) return null;
        var marker = "oneax0".Select(c => (int)c).FirstOrDefault(known.Contains, -1);
        if (marker < 0) marker = known.FirstOrDefault(c => Rune.IsLetterOrDigit(new Rune(c)), -1);
        if (marker < 0) return null;
        return new ResolvedFont(handle, page.Document, marker, $"{info.PostScriptName} (embedded)", known.Contains(' '),
            c => c == ' ' || known.Contains(c));
    }

    /// A system font matching `info` as closely as possible, embedded into the document once.
    public ResolvedFont? System(FontInfo info, bool? bold = null, bool? italic = null)
    {
        bool isBold = bold ?? info.IsBold, isItalic = italic ?? info.IsItalic;
        if (System(ClosestSystemFont(info, isBold, isItalic)) is { } font) return font;
        // The closest font cannot be embedded (license or format): use a generic one of the same class.
        var generic = info with
        {
            BaseName = info.IsSerif ? "TimesNewRomanPSMT" : info.IsMonospaced ? "CourierNewPSMT" : "ArialMT", // l10n-ignore
            FamilyName = "",
        };
        return System(ClosestSystemFont(generic, isBold, isItalic));
    }

    /// An installed font with a glyph for `character`, for characters the paragraph's font lacks.
    public (string Name, ResolvedFont Font)? Fallback(int character, SKTypeface near)
    {
        var candidates = new List<SKTypeface?> { SKFontManager.Default.MatchCharacter(near.FamilyName, near.FontStyle, null, character) };
        foreach (var family in new[] { "Arial", "Segoe UI Symbol", "Segoe UI", "Times New Roman", "Arial Unicode MS", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Menlo" }) // l10n-ignore
            candidates.Add(SKTypeface.FromFamilyName(family));
        foreach (var typeface in candidates.OfType<SKTypeface>())
            if (System(typeface) is { } font && font.Covers(character)) return (Key(typeface), font);
        return null;
    }

    static string Key(SKTypeface typeface) => $"{typeface.FamilyName} {typeface.FontWeight} {typeface.FontSlant}";

    ResolvedFont? System(SKTypeface typeface)
    {
        var name = Key(typeface);
        if (systemFonts.TryGetValue(name, out var cached)) return cached;
        if (!AllowsEmbedding(typeface) || SfntData(typeface) is not { } data) return null;
        IntPtr handle;
        unsafe
        {
            fixed (byte* bytes = data) handle = TextNative.FPDFText_LoadFont(page.Document, bytes, (uint)data.Length, TextNative.FontTrueType, 1);
        }
        if (handle == IntPtr.Zero) return null;
        return systemFonts[name] = new ResolvedFont(handle, page.Document, 'o', $"{name} (system)", true, typeface.ContainsGlyph);
    }

    /// OS/2 fsType: "restricted license embedding" fonts must not be embedded in documents.
    static bool AllowsEmbedding(SKTypeface typeface)
    {
        var os2 = TableData(typeface, "OS/2");
        if (os2 is not { Length: >= 10 }) return true;
        var fsType = os2[8] << 8 | os2[9];
        return (fsType & 0x000F) != 0x0002;
    }

    static uint Tag(string tag) => (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);

    static byte[]? TableData(SKTypeface typeface, string tag) =>
        typeface.TryGetTableData(Tag(tag), out var data) ? data : null;

    // MARK: Matching

    public static SKTypeface ClosestSystemFont(FontInfo info, bool bold, bool italic)
    {
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        // Same family with the requested traits (Skia matches families, not PostScript names).
        foreach (var family in CandidateFamilies(info))
            if (SKFontManager.Default.MatchFamily(family, style) is { } match && string.Equals(match.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                return match;
        // Generic fallback by classification.
        var lower = info.PostScriptName.ToLowerInvariant();
        string generic;
        if (info.IsMonospaced || new[] { "courier", "mono", "consol", "menlo" }.Any(lower.Contains)) // l10n-ignore
            generic = "Courier New"; // l10n-ignore
        else if (info.IsSerif || (!lower.Contains("sans") && new[] { "times", "georgia", "garamond", "serif", "roman", "cambria", "book", "minion", // l10n-ignore
                     "spectral", "merriweather", "playfair", "lora", "crimson", "baskerville", "caslon", "didot", "bodoni", "palatino", // l10n-ignore
                     "charter", "constantia", "libre", "pt-serif", "noto serif", "source serif", "tinos", "liberation serif" }.Any(lower.Contains))) // l10n-ignore
            generic = "Times New Roman"; // l10n-ignore
        else
            generic = "Arial"; // l10n-ignore
        return SKFontManager.Default.MatchFamily(generic, style) ?? SKTypeface.FromFamilyName(generic, style) ?? SKTypeface.Default;
    }

    /// Family names to try: the PDF's family, and the PostScript name without its style suffix.
    static IEnumerable<string> CandidateFamilies(FontInfo info)
    {
        var stem = info.PostScriptName.Split('-')[0];
        // "TimesNewRomanPSMT" → "Times New Roman"
        var cleaned = stem.Replace("PSMT", "").Replace("MT", "");
        var spaced = Regex.Replace(cleaned, "([a-z])([A-Z])", "$1 $2");
        return new[] { info.FamilyName, stem, spaced }.Where(n => n.Length > 0).Distinct();
    }

    // MARK: Coverage

    /// Per font, every character actually drawn on the page with it: the glyphs an embedded subset surely contains.
    Dictionary<IntPtr, HashSet<int>> LearnCoverage()
    {
        var result = new Dictionary<IntPtr, HashSet<int>>();
        var text = page.TextPageHandle;
        if (text == IntPtr.Zero) return result;
        for (var i = 0; i < Native.FPDFText_CountChars(text); i++)
        {
            // Spaces generated by the text extraction are not glyphs of the font.
            if (TextNative.FPDFText_IsGenerated(text, i) == 1) continue;
            var code = (int)Native.FPDFText_GetUnicode(text, i);
            if (code is <= 0 or '\n' or '\r' || code is >= 0xD800 and <= 0xDFFF || code > 0x10FFFF) continue;
            var obj = TextNative.FPDFText_GetTextObject(text, i);
            if (obj == IntPtr.Zero) continue;
            var font = TextNative.FPDFTextObj_GetFont(obj);
            if (font == IntPtr.Zero) continue;
            if (!result.TryGetValue(font, out var set)) result[font] = set = [];
            set.Add(code);
        }
        return result;
    }

    public static IEnumerable<int> Runes(string text) => text.EnumerateRunes().Select(r => r.Value);

    // MARK: Font data

    /// A cmap (format 12) mapping each glyph from a single character, the lowest code point.
    static byte[]? UniqueCmap(SKTypeface typeface)
    {
        using var font = new SKFont(typeface);
        var codeOfGlyph = new Dictionary<ushort, int>();
        var pairs = new List<(int Code, ushort Glyph)>();
        for (var code = 0x20; code <= 0x1FFFF; code++)
        {
            // Control characters often share the space glyph; they must never win it.
            if (code is >= 0x7F and <= 0x9F or >= 0xD800 and <= 0xDFFF) continue;
            var glyph = font.GetGlyph(code);
            if (glyph == 0) continue;
            codeOfGlyph.TryAdd(glyph, code);
            pairs.Add((code, glyph));
        }
        var kept = pairs.Where(p => codeOfGlyph[p.Glyph] == p.Code).ToList();
        if (kept.Count == 0) return null;
        var groups = new List<(int Start, int End, int Glyph)>();
        foreach (var (code, glyph) in kept)
        {
            if (groups.Count > 0 && groups[^1] is var last && last.End + 1 == code && last.Glyph + (last.End - last.Start) + 1 == glyph)
                groups[^1] = (last.Start, code, last.Glyph);
            else
                groups.Add((code, code, glyph));
        }
        using var data = new MemoryStream();
        void Put16(int v) { data.WriteByte((byte)(v >> 8)); data.WriteByte((byte)v); }
        void Put32(long v) { Put16((int)(v >> 16) & 0xFFFF); Put16((int)v & 0xFFFF); }
        Put16(0); Put16(1);              // version, one subtable
        Put16(3); Put16(10); Put32(12);  // Windows, Unicode full repertoire, offset
        Put16(12); Put16(0);             // format 12
        Put32(16 + 12 * groups.Count); Put32(0); Put32(groups.Count);
        foreach (var (start, end, glyph) in groups) { Put32(start); Put32(end); Put32(glyph); }
        return data.ToArray();
    }

    /// A standalone sfnt rebuilt from the font's tables, so fonts inside .ttc collections can be embedded.
    /// TrueType outlines ('glyf') only.
    static byte[]? SfntData(SKTypeface typeface)
    {
        var tags = typeface.GetTableTags();
        if (tags == null || !tags.Contains(Tag("glyf"))) return null;
        var tables = new List<(uint Tag, byte[] Data)>();
        foreach (var tag in tags)
            if (typeface.TryGetTableData(tag, out var data)) tables.Add((tag, data));
        // PDFium derives the ToUnicode map from the cmap; with one glyph shared by several characters
        // (space/no-break space, hyphen/soft hyphen) it may pick the wrong one. Keep one character per glyph.
        var cmapIndex = tables.FindIndex(t => t.Tag == Tag("cmap"));
        if (cmapIndex >= 0 && UniqueCmap(typeface) is { } cmap) tables[cmapIndex] = (tables[cmapIndex].Tag, cmap);
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        using var output = new MemoryStream();
        void Put16(int v) { output.WriteByte((byte)(v >> 8)); output.WriteByte((byte)v); }
        void Put32(uint v) { Put16((int)(v >> 16) & 0xFFFF); Put16((int)(v & 0xFFFF)); }
        var count = tables.Count;
        int power = 1, log = 0;
        while (power * 2 <= count) { power *= 2; log++; }
        Put32(0x00010000);
        Put16(count);
        Put16(power * 16);
        Put16(log);
        Put16(count * 16 - power * 16);
        var offset = 12 + 16 * count;
        var bodies = new MemoryStream();
        foreach (var (tag, data) in tables)
        {
            var padded = new byte[(data.Length + 3) / 4 * 4];
            data.CopyTo(padded, 0);
            uint checksum = 0;
            for (var i = 0; i < padded.Length; i += 4)
                checksum += (uint)(padded[i] << 24 | padded[i + 1] << 16 | padded[i + 2] << 8 | padded[i + 3]);
            Put32(tag);
            Put32(checksum);
            Put32((uint)offset);
            Put32((uint)data.Length);
            bodies.Write(padded);
            offset += padded.Length;
        }
        output.Write(bodies.ToArray());
        return output.ToArray();
    }
}
