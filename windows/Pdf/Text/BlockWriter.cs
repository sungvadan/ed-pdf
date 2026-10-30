using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;

namespace PdfSvd.Pdf.Text;

/// Style of a span of edited text. Two styles are equal when they look the same, whatever run they came from.
public sealed class RunStyle : IEquatable<RunStyle>
{
    public required FontInfo Font { get; init; }
    /// Size on the page.
    public required double FontSize { get; init; }
    public required TextColor Color { get; init; }
    /// An original run drawn with this font, whose embedded font can be reused.
    public int[]? SourcePath { get; init; }
    /// Requested emphasis; null keeps the font's own.
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    /// Installed font used for characters the main font lacks (set by the writer).
    public string? FallbackFont { get; init; }

    public static RunStyle Of(TextRun run) => new() { Font = run.Font, FontSize = run.FontSize, Color = run.Color, SourcePath = run.Path };

    public RunStyle With(string? fallbackFont) => new()
    {
        Font = Font, FontSize = FontSize, Color = Color, SourcePath = SourcePath, Bold = Bold, Italic = Italic, FallbackFont = fallbackFont,
    };

    public bool ChangesEmphasis => (Bold is { } b && b != Font.IsBold) || (Italic is { } i && i != Font.IsItalic);

    public bool Equals(RunStyle? other) => other != null && Font == other.Font && Math.Abs(FontSize - other.FontSize) < 0.01
        && Color == other.Color && Bold == other.Bold && Italic == other.Italic && FallbackFont == other.FallbackFont;

    public override bool Equals(object? obj) => Equals(obj as RunStyle);
    public override int GetHashCode() => HashCode.Combine(Font, (int)Math.Round(FontSize * 100), Color, Bold, Italic, FallbackFont);
}

public sealed record StyledSegment(string Text, RunStyle Style);

public enum ParagraphAlignment { Left, Center, Right, Justified }

/// Where and how a paragraph is laid out.
public sealed class ParagraphGeometry
{
    /// Start of the first baseline, page space.
    public Point Origin { get; init; }
    public double Width { get; init; }
    public double Leading { get; init; }
    public ParagraphAlignment Alignment { get; init; }
    /// Extra width allowed when breaking lines: `Width` comes from ink bounds, a bit narrower than the advances of
    /// the same text, so the original lines must still fit.
    public double WrapTolerance { get; init; }

    public static ParagraphGeometry Of(TextBlock block)
    {
        var first = block.Lines[0];
        return new ParagraphGeometry
        {
            Origin = new Point(block.Lines.Min(l => l.Bounds.MinX()), first.Baseline),
            // A single line (title, label…) grows to the right instead of wrapping.
            Width = block.Lines.Count == 1 ? double.MaxValue : block.Bounds.Width,
            Leading = block.Leading,
            Alignment = AlignmentOf(block),
            WrapTolerance = (block.DominantRun?.FontSize ?? 10) * 0.25,
        };
    }

    static ParagraphAlignment AlignmentOf(TextBlock block)
    {
        var lines = block.Lines;
        if (lines.Count < 2) return ParagraphAlignment.Left;
        const double tolerance = 2;
        var body = lines.Take(lines.Count - 1).ToList();
        static bool Aligned(IEnumerable<double> values) => values.Max() - values.Min() <= tolerance;
        var lefts = Aligned(lines.Select(l => l.Bounds.MinX()));
        if (lefts && body.Count >= 1 && Aligned(body.Select(l => l.Bounds.MaxX())) && lines.Count >= 3) return ParagraphAlignment.Justified;
        if (lefts) return ParagraphAlignment.Left;
        if (Aligned(lines.Select(l => l.Bounds.MaxX()))) return ParagraphAlignment.Right;
        if (Aligned(lines.Select(l => l.Bounds.MidX()))) return ParagraphAlignment.Center;
        return ParagraphAlignment.Left;
    }
}

public enum TextEditFailure { NotEditable, FontUnavailable, WriteFailed, VerificationFailed }

public sealed class TextEditException(TextEditFailure failure, string detail = "", string expected = "", string found = "")
    : Exception($"{failure} {detail}")
{
    public TextEditFailure Failure { get; } = failure;
    /// The written text does not read back as expected (VerificationFailed).
    public string Expected { get; } = expected;
    public string Found { get; } = found;
}

/// Replaces a paragraph's text objects with newly laid-out text (port of BlockWriter.swift).
sealed class BlockWriter(TextPage page)
{
    readonly FontResolver fonts = new(page);
    /// Use system fonts even when the embedded font could encode the text.
    public bool AvoidEmbeddedFonts { get; init; }
    /// One line per used font, for diagnostics.
    public List<string> FontReport { get; } = [];
    /// Number of text objects created by the last replacement.
    public int CreatedObjectCount { get; private set; }

    sealed class Piece(string text, RunStyle style, double width)
    {
        public string Text { get; set; } = text;
        public RunStyle Style { get; } = style;
        public double Width { get; set; } = width;
    }

    sealed record Line(List<Piece> Pieces, bool EndsParagraph);

    /// Appends the new objects at the end of the page (the original ones stay: they are removed from the content
    /// streams afterwards), so PDFium writes them to a new content stream and keeps the original streams intact.
    public void Replace(TextBlock block, List<StyledSegment> segments, ParagraphGeometry? geometry = null)
    {
        if (!block.IsEditable) throw new TextEditException(TextEditFailure.NotEditable);
        geometry ??= ParagraphGeometry.Of(block);

        var resolved = new Dictionary<RunStyle, ResolvedFont>();
        foreach (var style in segments.Select(s => s.Style).Distinct())
        {
            var text = string.Concat(segments.Where(s => s.Style.Equals(style)).Select(s => s.Text));
            ResolvedFont? font = null;
            if (!AvoidEmbeddedFonts && !style.ChangesEmphasis && style.SourcePath is { } path
                && Native.FPDFPage_GetObject(page.Handle, path[0]) is var obj && obj != IntPtr.Zero
                && TextNative.FPDFTextObj_GetFont(obj) is var handle && handle != IntPtr.Zero)
                font = fonts.Embedded(handle, style.Font, text);
            font ??= fonts.System(style.Font, style.Bold, style.Italic);
            resolved[style] = font ?? throw new TextEditException(TextEditFailure.FontUnavailable, style.Font.PostScriptName);
            FontReport.Add($"{style.Font.PostScriptName} → {font.Description}");
        }

        segments = WithFallbackFonts(segments, resolved);
        var lines = Layout(segments, resolved, geometry.Width == double.MaxValue ? double.MaxValue : geometry.Width + geometry.WrapTolerance);
        var created = new List<IntPtr>();
        for (var number = 0; number < lines.Count; number++)
            created.AddRange(Emit(lines[number], geometry.Origin.Y - number * geometry.Leading, geometry, resolved));
        CreatedObjectCount = created.Count;
        foreach (var obj in created) Native.FPDFPage_InsertObject(page.Handle, obj);
        if (!page.GenerateContent()) throw new TextEditException(TextEditFailure.WriteFailed, "generate content");
    }

    /// Splits off characters the chosen font lacks and assigns them an installed font that has them.
    List<StyledSegment> WithFallbackFonts(List<StyledSegment> segments, Dictionary<RunStyle, ResolvedFont> resolved)
    {
        var result = new List<(StringBuilder Text, RunStyle Style)>();
        foreach (var segment in segments)
        {
            var font = resolved[segment.Style];
            foreach (var rune in segment.Text.EnumerateRunes())
            {
                var style = segment.Style;
                if (!Rune.IsWhiteSpace(rune) && !font.Covers(rune.Value))
                {
                    var near = FontResolver.ClosestSystemFont(style.Font, style.Bold ?? style.Font.IsBold, style.Italic ?? style.Font.IsItalic);
                    if (fonts.Fallback(rune.Value, near) is var (name, fallback))
                    {
                        style = style.With(name);
                        resolved[style] = fallback;
                        FontReport.Add($"{rune} → {name} (fallback)");
                    }
                }
                if (result.Count > 0 && result[^1].Style.Equals(style)) result[^1].Text.Append(rune.ToString());
                else result.Add((new StringBuilder(rune.ToString()), style));
            }
        }
        return result.Select(r => new StyledSegment(r.Text.ToString(), r.Style)).ToList();
    }

    // MARK: Layout

    /// Greedy line breaking on spaces; explicit newlines end paragraphs.
    List<Line> Layout(List<StyledSegment> segments, Dictionary<RunStyle, ResolvedFont> resolved, double width)
    {
        // Words keep their trailing spaces so breaking never loses or adds characters.
        var paragraphs = new List<List<List<Piece>>> { new() };
        foreach (var segment in segments)
        {
            var font = resolved[segment.Style];
            var text = segment.Text;
            var start = 0;
            void FlushWord(int end)
            {
                if (start >= end) return;
                var part = text[start..end];
                var piece = new Piece(part, segment.Style, font.Width(part, segment.Style.FontSize));
                var words = paragraphs[^1];
                // Continue the current word if the previous piece did not end with a space (style change mid-word).
                if (words.Count > 0 && words[^1].Count > 0 && !words[^1][^1].Text.EndsWith(' ')) words[^1].Add(piece);
                else words.Add([piece]);
                start = end;
            }
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] is '\n' or '\r')
                {
                    FlushWord(index);
                    paragraphs.Add([]);
                    start = index + 1;
                }
                else if (text[index] == ' ') FlushWord(index + 1);
            }
            FlushWord(text.Length);
        }

        var lines = new List<Line>();
        foreach (var words in paragraphs)
        {
            var current = new List<Piece>();
            double currentWidth = 0;
            foreach (var word in words)
            {
                var wordWidth = word.Sum(p => p.Width);
                var visibleWidth = wordWidth - TrailingSpaceWidth(word, resolved);
                if (current.Count > 0 && currentWidth + visibleWidth > width)
                {
                    lines.Add(new Line(current, false));
                    current = [];
                    currentWidth = 0;
                }
                current.AddRange(word);
                currentWidth += wordWidth;
            }
            lines.Add(new Line(current, true));
        }
        return lines;
    }

    static double TrailingSpaceWidth(List<Piece> word, Dictionary<RunStyle, ResolvedFont> resolved) =>
        word.Count > 0 && word[^1].Text.EndsWith(' ') ? resolved[word[^1].Style].Width(" ", word[^1].Style.FontSize) : 0;

    // MARK: Output

    List<IntPtr> Emit(Line line, double baseline, ParagraphGeometry geometry, Dictionary<RunStyle, ResolvedFont> resolved)
    {
        // Trailing spaces do not count for alignment.
        var pieces = line.Pieces.Select(p => new Piece(p.Text, p.Style, p.Width)).ToList();
        while (pieces.Count > 0 && pieces[^1].Text.All(c => c == ' ')) pieces.RemoveAt(pieces.Count - 1);
        if (pieces.Count > 0 && pieces[^1].Text.EndsWith(' '))
        {
            var last = pieces[^1];
            last.Text = last.Text.TrimEnd(' ');
            last.Width = resolved[last.Style].Width(last.Text, last.Style.FontSize);
        }
        // One object per style run (words were separate pieces only for line breaking).
        var merged = new List<Piece>();
        foreach (var piece in pieces)
        {
            if (merged.Count > 0 && merged[^1].Style.Equals(piece.Style))
            {
                merged[^1].Text += piece.Text;
                merged[^1].Width += piece.Width;
            }
            else merged.Add(new Piece(piece.Text, piece.Style, piece.Width));
        }
        pieces = merged;
        var lineWidth = pieces.Sum(p => p.Width);

        var x = geometry.Origin.X;
        double extraPerSpace = 0;
        switch (geometry.Alignment)
        {
            case ParagraphAlignment.Center: x += (geometry.Width - lineWidth) / 2; break;
            case ParagraphAlignment.Right: x += geometry.Width - lineWidth; break;
            case ParagraphAlignment.Justified:
                var spaces = pieces.Sum(p => p.Text.Count(c => c == ' '));
                if (!line.EndsParagraph && spaces > 0 && lineWidth < geometry.Width) extraPerSpace = (geometry.Width - lineWidth) / spaces;
                break;
        }

        var objects = new List<IntPtr>();
        foreach (var piece in pieces)
        {
            var font = resolved[piece.Style];
            var size = piece.Style.FontSize;
            // Justified lines stretch their spaces, so they are written word by word, as are fonts without a space
            // glyph. Otherwise a style run is one object, spaces included.
            if (extraPerSpace <= 0 && font.WritesSpaces)
            {
                if (MakeText(piece.Text, piece.Style, font, new Point(x, baseline)) is { } obj) objects.Add(obj);
                x += piece.Width;
                continue;
            }
            foreach (var token in WordsWithTrailingSpaces(piece.Text))
            {
                var word = token.TrimEnd(' ');
                var spaces = token.Length - word.Length;
                var written = font.WritesSpaces ? token : word;
                if (word.Length > 0 && MakeText(written, piece.Style, font, new Point(x, baseline)) is { } obj) objects.Add(obj);
                x += font.Width(token, size) + spaces * extraPerSpace;
            }
        }
        return objects;
    }

    /// "a  b c" → ["a  ", "b ", "c"]; leading spaces form their own token.
    public static List<string> WordsWithTrailingSpaces(string text)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == ' ' && index + 1 < text.Length && text[index + 1] != ' ')
            {
                tokens.Add(text[start..(index + 1)]);
                start = index + 1;
            }
        }
        if (start < text.Length) tokens.Add(text[start..]);
        return tokens;
    }

    IntPtr? MakeText(string text, RunStyle style, ResolvedFont font, Point origin)
    {
        // Fonts embedded by pdf-svd map each glyph from one character only (see FontResolver.UniqueCmap).
        text = text.Replace(' ', ' ').Replace("­", "");
        if (text.Length == 0) return null;
        var obj = TextNative.FPDFPageObj_CreateTextObj(page.Document, font.Handle, (float)style.FontSize);
        if (obj == IntPtr.Zero) return null;
        if (Native.FPDFText_SetText(obj, text) == 0)
        {
            Native.FPDFPageObj_Destroy(obj);
            return null;
        }
        TextNative.FPDFPageObj_SetMatrix(obj, TextNative.ToFs(Matrix.CreateTranslation(origin.X, origin.Y)));
        var c = style.Color;
        TextNative.FPDFPageObj_SetFillColor(obj, (uint)Math.Round(c.Red * 255), (uint)Math.Round(c.Green * 255), (uint)Math.Round(c.Blue * 255), (uint)Math.Round(c.Alpha * 255));
        return obj;
    }
}

public static class StyledText
{
    /// The block's text split into styled segments, one per run, with spaces/newlines between lines.
    public static List<StyledSegment> Segments(TextBlock block)
    {
        var segments = new List<(string Text, RunStyle Style)>();
        for (var lineIndex = 0; lineIndex < block.Lines.Count; lineIndex++)
        {
            var line = block.Lines[lineIndex];
            for (var runIndex = 0; runIndex < line.Runs.Count; runIndex++)
            {
                var run = line.Runs[runIndex];
                var text = run.Text;
                if (runIndex > 0 && line.Runs[runIndex - 1].FollowedBySpace && !(segments.Count == 0 || segments[^1].Text.EndsWith(' ')) && !text.StartsWith(' '))
                    text = " " + text;
                // Runs ending and starting with a space would double it.
                if (text.StartsWith(' ') && segments.Count > 0 && segments[^1].Text.EndsWith(' ')) text = text[1..];
                segments.Add((text, RunStyle.Of(run)));
            }
            // Wrapped lines are joined with a space so the writer can re-wrap them; a line the author broke on
            // purpose keeps its line break.
            if (lineIndex < block.Lines.Count - 1 && segments.Count > 0)
            {
                var last = segments[^1];
                var trimmed = last.Text.TrimEnd(' ');
                if (EndsWithForcedBreak(block, lineIndex)) trimmed += "\n";
                else if (!trimmed.EndsWith('-')) trimmed += " ";
                segments[^1] = (trimmed, last.Style);
            }
        }
        return segments.Select(s => new StyledSegment(s.Text, s.Style)).ToList();
    }

    /// A line ends with a forced break when the next line's first word would have fit after it: automatic
    /// wrapping would have kept it there (titles, addresses, centered headings).
    static bool EndsWithForcedBreak(TextBlock block, int lineIndex)
    {
        TextLine line = block.Lines[lineIndex], next = block.Lines[lineIndex + 1];
        var nextText = next.Text.Trim(' ');
        var firstWord = nextText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord == null) return false;
        var firstWordWidth = next.Bounds.Width * firstWord.Length / nextText.Length;
        var space = line.FontSize * 0.3;
        return line.Bounds.Width + space + firstWordWidth < block.Bounds.Width * 0.98;
    }

    /// Applies a plain-text edit, keeping styles of unchanged characters; inserted text takes the style of the
    /// character before it (common prefix/suffix diff).
    public static List<StyledSegment> Applying(List<StyledSegment> segments, string newText)
    {
        var characters = segments.SelectMany(s => s.Text.Select(c => (Char: c, s.Style))).ToList();
        var old = characters.Select(c => c.Char).ToList();
        var prefix = 0;
        while (prefix < old.Count && prefix < newText.Length && old[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < old.Count - prefix && suffix < newText.Length - prefix && old[old.Count - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
        var insertStyle = prefix > 0 ? characters[prefix - 1].Style : characters.Count > 0 ? characters[0].Style : segments[0].Style;
        characters.RemoveRange(prefix, old.Count - suffix - prefix);
        characters.InsertRange(prefix, newText[prefix..(newText.Length - suffix)].Select(c => (c, insertStyle)));

        var result = new List<(StringBuilder Text, RunStyle Style)>();
        foreach (var (character, style) in characters)
        {
            if (result.Count > 0 && result[^1].Style.Equals(style)) result[^1].Text.Append(character);
            else result.Add((new StringBuilder().Append(character), style));
        }
        return result.Select(r => new StyledSegment(r.Text.ToString(), r.Style)).ToList();
    }
}
