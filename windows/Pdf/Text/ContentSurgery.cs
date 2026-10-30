using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;

namespace PdfSvd.Pdf.Text;

/// One operator of a content stream with its operands and byte range.
public sealed record ContentOperation(string Name, List<PdfObject> Operands, int Start, int End);

public static class ContentParser
{
    public static List<ContentOperation> Operations(byte[] bytes)
    {
        var lexer = new PdfLexer(bytes);
        var operations = new List<ContentOperation>();
        var operands = new List<PdfObject>();
        int? start = null;
        while (true)
        {
            lexer.SkipWhitespace();
            var tokenStart = lexer.Position;
            var token = lexer.Next();
            if (token.Kind == PdfLexer.TokenKind.Eof) break;
            if (token.Kind != PdfLexer.TokenKind.Keyword)
            {
                start ??= tokenStart;
                if (lexer.ParseObject(token) is { } operand) operands.Add(operand);
                continue;
            }
            if (token.Text == "BI") SkipInlineImage(lexer);
            operations.Add(new ContentOperation(token.Text, operands, start ?? tokenStart, lexer.Position));
            operands = [];
            start = null;
        }
        return operations;
    }

    /// Skips "BI <dict> ID <binary> EI"; the binary data can contain anything.
    static void SkipInlineImage(PdfLexer lexer)
    {
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == PdfLexer.TokenKind.Eof) return;
            if (token is { Kind: PdfLexer.TokenKind.Keyword, Text: "ID" }) break;
        }
        var bytes = lexer.Bytes;
        for (var i = lexer.Position + 1; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 'E' && bytes[i + 1] == 'I' && PdfLexer.IsWhitespace(bytes[i - 1])
                && (i + 2 == bytes.Length || PdfLexer.IsWhitespace(bytes[i + 2]) || PdfLexer.IsDelimiter(bytes[i + 2])))
            {
                lexer.Position = i + 2;
                return;
            }
        }
        lexer.Position = bytes.Length;
    }
}

/// A text-showing operator (Tj, TJ, ', ") and the state needed to replace it.
sealed record ShowOperation(int Stream, int Operation, string Name, List<PdfObject> Operands, int Start, int End,
    double FontSize, double HorizontalScale, int TextBlock, bool Repositioned, bool ProducesObject);

public sealed class TextSurgeryException(string message) : Exception(message);

/// Removes the text objects of an edited paragraph from the original content streams, operator by operator,
/// leaving everything else byte-identical (port of ContentSurgery.swift).
static class ContentSurgery
{
    static List<ShowOperation> ShowOperations(List<byte[]> streams)
    {
        var shows = new List<ShowOperation>();
        double fontSize = 0, scale = 1;
        var stack = new Stack<(double, double)>();
        var textBlock = 0;
        var repositioned = true;
        for (var streamIndex = 0; streamIndex < streams.Count; streamIndex++)
        {
            var operations = ContentParser.Operations(streams[streamIndex]);
            for (var index = 0; index < operations.Count; index++)
            {
                var op = operations[index];
                switch (op.Name)
                {
                    case "q": stack.Push((fontSize, scale)); break;
                    case "Q": if (stack.TryPop(out var saved)) (fontSize, scale) = saved; break;
                    case "BT": textBlock++; repositioned = true; break;
                    case "Tf": fontSize = op.Operands.LastOrDefault()?.Double ?? fontSize; break;
                    case "Tz": scale = (op.Operands.FirstOrDefault()?.Double ?? 100) / 100; break;
                    case "Td" or "TD" or "Tm" or "T*": repositioned = true; break;
                    case "Tj" or "TJ" or "'" or "\"":
                        var strings = op.Name == "TJ"
                            ? (op.Operands.FirstOrDefault()?.Array ?? []).Select(o => o.StringValue).OfType<byte[]>()
                            : op.Operands.Select(o => o.StringValue).OfType<byte[]>();
                        shows.Add(new ShowOperation(streamIndex, index, op.Name, op.Operands, op.Start, op.End, fontSize, scale, textBlock,
                            repositioned || op.Name is "'" or "\"", strings.Any(s => s.Length > 0)));
                        repositioned = false;
                        break;
                }
            }
        }
        return shows;
    }

    /// `remove`: indices into PDFium's text objects (in page order) to delete. `matrices`: PDFium's matrix
    /// (Tm × CTM) of every text object, in page order. Returns the new content of each modified stream.
    public static Dictionary<int, byte[]> RemoveText(HashSet<int> remove, List<Matrix> matrices, List<byte[]> streams)
    {
        var shows = ShowOperations(streams);
        var producing = Enumerable.Range(0, shows.Count).Where(i => shows[i].ProducesObject).ToList();
        if (producing.Count != matrices.Count)
            throw new TextSurgeryException($"object count mismatch: stream {producing.Count}, pdfium {matrices.Count}");
        var ordinalOfShow = new Dictionary<int, int>();
        for (var ordinal = 0; ordinal < producing.Count; ordinal++) ordinalOfShow[producing[ordinal]] = ordinal;
        bool IsRemoved(int showIndex) => ordinalOfShow.TryGetValue(showIndex, out var o) && remove.Contains(o);

        var replacements = new Dictionary<int, List<(int Start, int End, string Text)>>();
        for (var index = 0; index < shows.Count; index++)
        {
            if (!IsRemoved(index)) continue;
            var show = shows[index];
            var replacement = "";
            if (show.Name == "'") replacement = "T*";
            if (show.Name == "\"" && show.Operands.Count >= 2)
                replacement = $"{Format(show.Operands[0].Double ?? 0)} Tw {Format(show.Operands[1].Double ?? 0)} Tc T*";

            // If the next show operator of the same BT block is kept and relies on the current position,
            // the first removed operator of the chain moves the pen by the same amount without drawing.
            var chainStart = index == 0 || show.Repositioned || !IsRemoved(index - 1) || shows[index - 1].TextBlock != show.TextBlock;
            if (chainStart)
            {
                var next = index + 1;
                while (next < shows.Count && shows[next].TextBlock == show.TextBlock && !shows[next].Repositioned && IsRemoved(next)) next++;
                if (next < shows.Count && shows[next].TextBlock == show.TextBlock && !shows[next].Repositioned)
                {
                    if (!ordinalOfShow.TryGetValue(index, out var from) || !ordinalOfShow.TryGetValue(next, out var to))
                        throw new TextSurgeryException("offset without a character after the edited text");
                    var m = matrices[from];
                    double dx = matrices[to].M31 - m.M31, dy = matrices[to].M32 - m.M32;
                    var tx = (dx * m.M11 + dy * m.M12) / Math.Max(m.M11 * m.M11 + m.M12 * m.M12, 1e-9);
                    var denominator = show.FontSize * show.HorizontalScale;
                    if (Math.Abs(denominator) <= 1e-9) throw new TextSurgeryException("zero font size");
                    var n = -tx * 1000 / denominator;
                    replacement += (replacement.Length == 0 ? "" : " ") + $"[{Format(n)}] TJ";
                }
            }
            if (!replacements.TryGetValue(show.Stream, out var list)) replacements[show.Stream] = list = [];
            list.Add((show.Start, show.End, replacement));
        }

        var result = new Dictionary<int, byte[]>();
        foreach (var (stream, edits) in replacements)
        {
            var bytes = streams[stream].ToList();
            foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start))
            {
                bytes.RemoveRange(start, end - start);
                bytes.InsertRange(start, Encoding.ASCII.GetBytes(text));
            }
            result[stream] = bytes.ToArray();
        }
        return result;
    }

    static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
