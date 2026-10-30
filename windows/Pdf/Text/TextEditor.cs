using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;

namespace EdPdf.Pdf.Text;

/// Rewrites a paragraph of a PDF and returns the new file (port of TextEditor.swift).
///
/// PDFium lays out and writes the new text into a content stream of its own; the original text objects are then
/// removed from the original streams operator by operator (ContentSurgery), so kerning and spacing of every other
/// text on the page stay exactly as they were.
public static class TextEditor
{
    public sealed record Result(byte[] Data, List<string> FontReport);

    /// Edits, then reads back the text objects just written: if a character does not come back (glyph missing, or
    /// mapped to a look-alike), retries with system fonts only, and refuses the edit if that fails too.
    /// Holds the PDFium lock.
    public static Result Replace(int blockIndex, int pageIndex, byte[] data, string? password, Func<TextBlock, List<StyledSegment>> makeSegments,
        bool avoidEmbeddedFonts = false, bool verify = true)
    {
        lock (Pdfium.Lock)
        {
            var expected = "";
            var (result, created) = Write(blockIndex, pageIndex, data, password, block =>
            {
                var segments = makeSegments(block).Select(s => s with { Text = WithoutLigatures(s.Text) }).ToList();
                expected = string.Concat(segments.Select(s => s.Text));
                return segments;
            }, avoidEmbeddedFonts);
            var found = WrittenText(result.Data, pageIndex, password, created);
            if (!verify || Compact(found) == Compact(expected)) return result;
            if (!avoidEmbeddedFonts) return Replace(blockIndex, pageIndex, data, password, makeSegments, avoidEmbeddedFonts: true, verify);
            throw new TextEditException(TextEditFailure.VerificationFailed, expected: expected, found: found);
        }
    }

    /// Characters only: compatibility forms folded (ligatures, no-break spaces), whitespace dropped. Soft hyphens are
    /// kept, so a "-" read back as U+00AD still counts as a difference.
    public static string Compact(string text) =>
        string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));

    /// Characters of `expected` that did not come back in `found`.
    public static string MissingCharacters(string expected, string found)
    {
        var remaining = Compact(found).ToList();
        var missing = new StringBuilder();
        foreach (var character in Compact(expected))
        {
            if (remaining.IndexOf(character) is var index and >= 0) remaining.RemoveAt(index);
            else if (!missing.ToString().Contains(character)) missing.Append(character);
        }
        return missing.ToString();
    }

    /// "ﬁ" → "fi" etc.: ligature code points from the original text are written as plain letters.
    static string WithoutLigatures(string text) =>
        string.Concat(text.Select(c => c is >= 'ﬀ' and <= 'ﬆ' ? c.ToString().Normalize(NormalizationForm.FormKC) : c.ToString()));

    /// Text of the objects written by the edit: the last `created` objects of the page, in writing order.
    static string WrittenText(byte[] data, int pageIndex, string? password, int created)
    {
        using var document = TextDocument.Open(data, password);
        using var page = document?.Page(pageIndex);
        if (page == null) return "";
        var firstNew = page.ObjectCount - created;
        return string.Join(" ", page.TextRuns().Where(r => r.Path[0] >= firstNew).Select(r => r.Text));
    }

    static (Result, int Created) Write(int blockIndex, int pageIndex, byte[] data, string? password, Func<TextBlock, List<StyledSegment>> makeSegments,
        bool avoidEmbeddedFonts)
    {
        using var document = TextDocument.Open(data, password) ?? throw new TextEditException(TextEditFailure.WriteFailed, "open");
        using var page = document.Page(pageIndex) ?? throw new TextEditException(TextEditFailure.WriteFailed, "page");
        var blocks = TextLayout.Blocks(page.TextRuns());
        if (blockIndex < 0 || blockIndex >= blocks.Count) throw new TextEditException(TextEditFailure.NotEditable);
        var block = blocks[blockIndex];

        // Ordinals (among text objects) of the runs to remove, taken before anything is added.
        var textObjects = page.TopLevelTextObjects();
        var ordinalOfIndex = textObjects.Select((o, ordinal) => (o.Index, ordinal)).ToDictionary(p => p.Index, p => p.ordinal);
        var removed = block.Runs.Select(r => ordinalOfIndex.TryGetValue(r.Path[0], out var o) ? o : -1).Where(o => o >= 0).ToHashSet();

        var writer = new BlockWriter(page) { AvoidEmbeddedFonts = avoidEmbeddedFonts };
        writer.Replace(block, makeSegments(block));
        var intermediate = document.Data();

        // New objects come after the original ones; their positions are not needed (never removed).
        var file = new PdfFile(intermediate);
        var pageObject = file.PageObjectNumber(pageIndex) ?? throw new PdfFileException("page");
        var streamNumbers = file.ContentStreams(pageObject);
        var streams = streamNumbers.Select(number =>
            file.Object(number) is { } obj && file.DecodedStream(obj) is { } decoded ? decoded
                : throw new PdfFileException($"content stream {number}", unsupported: true)).ToList();
        var matrices = textObjects.Select(o => o.Matrix).Concat(Enumerable.Repeat(Matrix.Identity, writer.CreatedObjectCount)).ToList();
        var edited = ContentSurgery.RemoveText(removed, matrices, streams);
        var output = file.IncrementalUpdate(edited.ToDictionary(e => streamNumbers[e.Key], e => e.Value));
        return (new Result(output, writer.FontReport), writer.CreatedObjectCount);
    }
}
