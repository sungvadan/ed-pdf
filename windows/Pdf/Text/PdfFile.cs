using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace EdPdf.Pdf.Text;

public enum PdfKind { Null, Bool, Number, Name, String, Array, Dict, Ref, Stream }

/// A PDF object, as parsed from a file or a content stream (port of PDFFile.swift).
public sealed class PdfObject
{
    public PdfKind Kind { get; private init; }
    public bool Bool { get; private init; }
    public double Number { get; private init; }
    public string Name { get; private init; } = "";
    /// String bytes, or stream data (raw, as in the file).
    public byte[] Bytes { get; private init; } = [];
    public List<PdfObject> Items { get; private init; } = [];
    public Dictionary<string, PdfObject> Entries { get; private init; } = [];
    public int RefNumber { get; private init; }
    public int RefGeneration { get; private init; }

    public static readonly PdfObject Null = new() { Kind = PdfKind.Null };
    public static PdfObject FromBool(bool v) => new() { Kind = PdfKind.Bool, Bool = v };
    public static PdfObject FromNumber(double v) => new() { Kind = PdfKind.Number, Number = v };
    public static PdfObject FromName(string v) => new() { Kind = PdfKind.Name, Name = v };
    public static PdfObject FromString(byte[] v) => new() { Kind = PdfKind.String, Bytes = v };
    public static PdfObject FromArray(List<PdfObject> v) => new() { Kind = PdfKind.Array, Items = v };
    public static PdfObject FromDict(Dictionary<string, PdfObject> v) => new() { Kind = PdfKind.Dict, Entries = v };
    public static PdfObject FromRef(int number, int generation) => new() { Kind = PdfKind.Ref, RefNumber = number, RefGeneration = generation };
    public static PdfObject FromStream(Dictionary<string, PdfObject> dict, byte[] data) => new() { Kind = PdfKind.Stream, Entries = dict, Bytes = data };

    public int? Int => Kind == PdfKind.Number ? (int)Number : null;
    public double? Double => Kind == PdfKind.Number ? Number : null;
    public string? NameValue => Kind == PdfKind.Name ? Name : null;
    public List<PdfObject>? Array => Kind == PdfKind.Array ? Items : null;
    public byte[]? StringValue => Kind == PdfKind.String ? Bytes : null;
    public Dictionary<string, PdfObject>? Dict => Kind is PdfKind.Dict or PdfKind.Stream ? Entries : null;
    public int? Ref => Kind == PdfKind.Ref ? RefNumber : null;

    public PdfObject? this[string key] => Dict is { } d && d.TryGetValue(key, out var v) ? v : null;
}

public sealed class PdfFileException(string message, bool unsupported = false) : Exception(message)
{
    public bool Unsupported { get; } = unsupported;
}

/// Tokenizer shared by file parsing and content streams.
public sealed class PdfLexer(byte[] bytes, int position = 0)
{
    public enum TokenKind { Number, Name, String, ArrayStart, ArrayEnd, DictStart, DictEnd, Keyword, Eof }

    public readonly record struct Token(TokenKind Kind, double Number = 0, bool IsInteger = false, string Text = "", byte[]? Data = null);

    public byte[] Bytes { get; } = bytes;
    public int Position { get; set; } = position;

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public void SkipWhitespace()
    {
        while (Position < Bytes.Length)
        {
            var b = Bytes[Position];
            if (IsWhitespace(b)) Position++;
            else if (b == '%') { while (Position < Bytes.Length && Bytes[Position] != 10 && Bytes[Position] != 13) Position++; }
            else return;
        }
    }

    public Token Next()
    {
        SkipWhitespace();
        if (Position >= Bytes.Length) return new Token(TokenKind.Eof);
        var b = Bytes[Position];
        switch (b)
        {
            case (byte)'[': Position++; return new Token(TokenKind.ArrayStart);
            case (byte)']': Position++; return new Token(TokenKind.ArrayEnd);
            case (byte)'{' or (byte)'}': Position++; return new Token(TokenKind.Keyword, Text: ((char)b).ToString());
            case (byte)'<':
                if (Position + 1 < Bytes.Length && Bytes[Position + 1] == '<') { Position += 2; return new Token(TokenKind.DictStart); }
                return new Token(TokenKind.String, Data: HexString());
            case (byte)'>':
                if (Position + 1 < Bytes.Length && Bytes[Position + 1] == '>') { Position += 2; return new Token(TokenKind.DictEnd); }
                Position++;
                return new Token(TokenKind.Keyword, Text: ">");
            case (byte)'(': return new Token(TokenKind.String, Data: LiteralString());
            case (byte)'/': return new Token(TokenKind.Name, Text: NameToken());
            default:
                var start = Position;
                while (Position < Bytes.Length && !IsWhitespace(Bytes[Position]) && !IsDelimiter(Bytes[Position])) Position++;
                if (Position == start) Position++;
                var text = Encoding.UTF8.GetString(Bytes, start, Position - start);
                if (text.Length > 0 && (char.IsDigit(text[0]) || text[0] is '-' or '+' or '.')
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return new Token(TokenKind.Number, value, !text.Contains('.'));
                return new Token(TokenKind.Keyword, Text: text);
        }
    }

    string NameToken()
    {
        Position++;
        var result = new List<byte>();
        while (Position < Bytes.Length && !IsWhitespace(Bytes[Position]) && !IsDelimiter(Bytes[Position]))
        {
            if (Bytes[Position] == '#' && Position + 2 < Bytes.Length
                && byte.TryParse(Encoding.ASCII.GetString(Bytes, Position + 1, 2), NumberStyles.HexNumber, null, out var value))
            {
                result.Add(value);
                Position += 3;
            }
            else result.Add(Bytes[Position++]);
        }
        return Encoding.UTF8.GetString(result.ToArray());
    }

    byte[] HexString()
    {
        Position++;
        var digits = new List<byte>();
        while (Position < Bytes.Length && Bytes[Position] != '>')
        {
            if (!IsWhitespace(Bytes[Position])) digits.Add(Bytes[Position]);
            Position++;
        }
        Position++;
        if (digits.Count % 2 == 1) digits.Add((byte)'0');
        var data = new byte[digits.Count / 2];
        for (var i = 0; i < data.Length; i++)
            data[i] = byte.TryParse(Encoding.ASCII.GetString([digits[2 * i], digits[2 * i + 1]]), NumberStyles.HexNumber, null, out var v) ? v : (byte)0;
        return data;
    }

    byte[] LiteralString()
    {
        Position++;
        var depth = 1;
        var data = new List<byte>();
        while (Position < Bytes.Length)
        {
            var b = Bytes[Position++];
            switch (b)
            {
                case (byte)'(': depth++; data.Add(b); break;
                case (byte)')':
                    if (--depth == 0) return data.ToArray();
                    data.Add(b);
                    break;
                case (byte)'\\':
                    if (Position >= Bytes.Length) return data.ToArray();
                    var e = Bytes[Position++];
                    switch (e)
                    {
                        case (byte)'n': data.Add(10); break;
                        case (byte)'r': data.Add(13); break;
                        case (byte)'t': data.Add(9); break;
                        case (byte)'b': data.Add(8); break;
                        case (byte)'f': data.Add(12); break;
                        case 13: if (Position < Bytes.Length && Bytes[Position] == 10) Position++; break;
                        case 10: break;
                        case >= (byte)'0' and <= (byte)'7':
                            var value = e - 48;
                            for (var k = 0; k < 2 && Position < Bytes.Length && Bytes[Position] is >= 48 and <= 55; k++)
                                value = value * 8 + Bytes[Position++] - 48;
                            data.Add((byte)(value & 0xFF));
                            break;
                        default: data.Add(e); break;
                    }
                    break;
                default: data.Add(b); break;
            }
        }
        return data.ToArray();
    }

    /// Parses one object, resolving "n g R" into a reference.
    public PdfObject? ParseObject() => ParseObject(Next());

    public PdfObject? ParseObject(Token token)
    {
        switch (token.Kind)
        {
            case TokenKind.Number:
                if (token.IsInteger)
                {
                    var saved = Position;
                    if (Next() is { Kind: TokenKind.Number, IsInteger: true } generation && Next() is { Kind: TokenKind.Keyword, Text: "R" })
                        return PdfObject.FromRef((int)token.Number, (int)generation.Number);
                    Position = saved;
                }
                return PdfObject.FromNumber(token.Number);
            case TokenKind.Name: return PdfObject.FromName(token.Text);
            case TokenKind.String: return PdfObject.FromString(token.Data ?? []);
            case TokenKind.ArrayStart:
                var items = new List<PdfObject>();
                while (true)
                {
                    var t = Next();
                    if (t.Kind is TokenKind.ArrayEnd or TokenKind.Eof) break;
                    if (ParseObject(t) is { } item) items.Add(item);
                }
                return PdfObject.FromArray(items);
            case TokenKind.DictStart:
                var dict = new Dictionary<string, PdfObject>();
                while (true)
                {
                    var t = Next();
                    if (t.Kind != TokenKind.Name) break;
                    dict[t.Text] = ParseObject() ?? PdfObject.Null;
                }
                return PdfObject.FromDict(dict);
            case TokenKind.Keyword when token.Text == "true": return PdfObject.FromBool(true);
            case TokenKind.Keyword when token.Text == "false": return PdfObject.FromBool(false);
            case TokenKind.Keyword when token.Text == "null": return PdfObject.Null;
            default: return null;
        }
    }
}

/// Minimal PDF reader (classic xref tables and xref streams, object streams) with incremental updates.
public sealed class PdfFile
{
    readonly record struct XrefEntry(int Offset, int Stream = -1, int Index = 0);

    public byte[] Bytes { get; }
    public Dictionary<string, PdfObject> Trailer { get; private set; } = [];
    readonly Dictionary<int, XrefEntry> entries = [];
    readonly Dictionary<int, PdfObject> cache = [];
    public int LastXrefOffset { get; }
    public bool UsesXrefStream { get; private set; }

    public PdfFile(byte[] data)
    {
        Bytes = data;
        var start = FindLast("startxref", Bytes) ?? throw new PdfFileException("startxref");
        var lexer = new PdfLexer(Bytes, start + 9);
        if (lexer.Next() is not { Kind: PdfLexer.TokenKind.Number } offset) throw new PdfFileException("startxref offset");
        LastXrefOffset = (int)offset.Number;
        int? next = LastXrefOffset;
        var visited = new HashSet<int>();
        while (next is { } at && visited.Add(at))
        {
            var section = ReadXref(at);
            if (Trailer.Count == 0) Trailer = section;
            next = section.TryGetValue("Prev", out var prev) ? prev.Int : null;
            if (section.TryGetValue("XRefStm", out var hybrid) && hybrid.Int is { } h) try { ReadXref(h); } catch (PdfFileException) { }
        }
    }

    static int? FindLast(string needle, byte[] bytes)
    {
        var pattern = Encoding.ASCII.GetBytes(needle);
        for (var i = bytes.Length - pattern.Length; i >= 0; i--)
            if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return null;
    }

    /// Reads one xref section (table or stream); newer sections take precedence.
    Dictionary<string, PdfObject> ReadXref(int offset)
    {
        var lexer = new PdfLexer(Bytes, offset);
        if (lexer.Next() is { Kind: PdfLexer.TokenKind.Keyword, Text: "xref" })
        {
            while (true)
            {
                var saved = lexer.Position;
                if (lexer.Next() is not { Kind: PdfLexer.TokenKind.Number } first || lexer.Next() is not { Kind: PdfLexer.TokenKind.Number } count)
                {
                    lexer.Position = saved;
                    break;
                }
                for (var n = 0; n < (int)count.Number; n++)
                {
                    if (lexer.Next() is not { Kind: PdfLexer.TokenKind.Number } off || lexer.Next().Kind != PdfLexer.TokenKind.Number
                        || lexer.Next() is not { Kind: PdfLexer.TokenKind.Keyword } kind)
                        throw new PdfFileException("xref entry");
                    var number = (int)first.Number + n;
                    if (kind.Text == "n" && !entries.ContainsKey(number)) entries[number] = new XrefEntry((int)off.Number);
                }
            }
            if (lexer.Next() is not { Kind: PdfLexer.TokenKind.Keyword, Text: "trailer" } || lexer.ParseObject()?.Dict is not { } trailer)
                throw new PdfFileException("trailer");
            return trailer;
        }

        // Cross-reference stream.
        var stream = ObjectAt(offset);
        if (stream is not { Kind: PdfKind.Stream } || DecodedStream(stream) is not { } raw
            || stream["W"]?.Array?.Select(w => w.Int ?? -1).ToArray() is not { Length: 3 } widths || widths.Any(w => w < 0))
            throw new PdfFileException("xref stream");
        UsesXrefStream = true;
        var index = stream["Index"]?.Array?.Select(i => i.Int ?? 0).ToArray() ?? [0, stream["Size"]?.Int ?? 0];
        var rowSize = widths.Sum();
        var row = 0;
        int Field(int start, int width)
        {
            var value = 0;
            for (var k = 0; k < width; k++) value = value << 8 | raw[start + k];
            return value;
        }
        for (var pair = 0; pair + 1 < index.Length; pair += 2)
            for (var n = 0; n < index[pair + 1]; n++)
            {
                var b = row++ * rowSize;
                if (b + rowSize > raw.Length) break;
                var type = widths[0] == 0 ? 1 : Field(b, widths[0]);
                var x = Field(b + widths[0], widths[1]);
                var y = Field(b + widths[0] + widths[1], widths[2]);
                var number = index[pair] + n;
                if (entries.ContainsKey(number)) continue;
                if (type == 1) entries[number] = new XrefEntry(x);
                if (type == 2) entries[number] = new XrefEntry(0, x, y);
            }
        return stream.Entries;
    }

    PdfObject? ObjectAt(int offset)
    {
        var lexer = new PdfLexer(Bytes, offset);
        if (lexer.Next().Kind != PdfLexer.TokenKind.Number || lexer.Next().Kind != PdfLexer.TokenKind.Number
            || lexer.Next() is not { Kind: PdfLexer.TokenKind.Keyword, Text: "obj" })
            throw new PdfFileException($"obj at {offset}");
        if (lexer.ParseObject() is not { } obj) return null;
        if (obj.Kind != PdfKind.Dict) return obj;
        var saved = lexer.Position;
        if (lexer.Next() is not { Kind: PdfLexer.TokenKind.Keyword, Text: "stream" })
        {
            lexer.Position = saved;
            return obj;
        }
        var start = lexer.Position;
        if (start < Bytes.Length && Bytes[start] == 13) start++;
        if (start < Bytes.Length && Bytes[start] == 10) start++;
        var length = Resolve(obj["Length"] ?? PdfObject.Null).Int ?? -1;
        if (length < 0 || start + length > Bytes.Length)
        {
            // Bad /Length: search for endstream.
            var marker = Encoding.ASCII.GetBytes("endstream");
            var end = start;
            while (end + marker.Length <= Bytes.Length && !Bytes.AsSpan(end, marker.Length).SequenceEqual(marker)) end++;
            length = end - start;
        }
        return PdfObject.FromStream(obj.Entries, Bytes.AsSpan(start, length).ToArray());
    }

    public PdfObject? Object(int number)
    {
        if (cache.TryGetValue(number, out var cached)) return cached;
        PdfObject? result = null;
        if (entries.TryGetValue(number, out var entry))
        {
            try { result = entry.Stream >= 0 ? ObjectFromStream(entry.Stream, entry.Index) : ObjectAt(entry.Offset); }
            catch (PdfFileException) { result = null; }
        }
        if (result != null) cache[number] = result;
        return result;
    }

    PdfObject? ObjectFromStream(int streamNumber, int index)
    {
        if (Object(streamNumber) is not { } stream || DecodedStream(stream) is not { } raw
            || stream["N"]?.Int is not { } count || stream["First"]?.Int is not { } first) return null;
        var lexer = new PdfLexer(raw);
        var offsets = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (lexer.Next().Kind != PdfLexer.TokenKind.Number || lexer.Next() is not { Kind: PdfLexer.TokenKind.Number } off) return null;
            offsets.Add((int)off.Number);
        }
        if (index >= offsets.Count) return null;
        return new PdfLexer(raw, first + offsets[index]).ParseObject();
    }

    public PdfObject Resolve(PdfObject obj) => obj.Ref is { } number ? Object(number) ?? PdfObject.Null : obj;

    public byte[]? DecodedStream(PdfObject obj)
    {
        if (obj.Kind != PdfKind.Stream) return null;
        var filters = Resolve(obj["Filter"] ?? PdfObject.Null);
        var names = filters.NameValue is { } single ? [single] : filters.Array?.Select(f => f.NameValue).OfType<string>().ToList() ?? [];
        var data = obj.Bytes;
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] is not ("FlateDecode" or "Fl")) return null;
            if (Inflate(data) is not { } inflated) return null;
            data = inflated;
            var parameters = Resolve(obj["DecodeParms"] ?? PdfObject.Null);
            var p = parameters.Array is { } list ? (i < list.Count ? Resolve(list[i]) : PdfObject.Null) : parameters;
            if (p["Predictor"]?.Int is >= 10)
                data = UnpredictPng(data, p["Columns"]?.Int ?? 1, p["Colors"]?.Int ?? 1, p["BitsPerComponent"]?.Int ?? 8);
        }
        return data;
    }

    // MARK: Pages

    public int? PageObjectNumber(int index)
    {
        if (Resolve(Trailer.GetValueOrDefault("Root") ?? PdfObject.Null)["Pages"]?.Ref is not { } pages) return null;
        var remaining = index;
        int? Walk(int number, int depth)
        {
            if (depth >= 64 || Object(number)?.Dict is not { } node) return null;
            if (node.GetValueOrDefault("Type")?.NameValue == "Page" || !node.ContainsKey("Kids"))
            {
                if (remaining == 0) return number;
                remaining--;
                return null;
            }
            foreach (var kid in Resolve(node["Kids"]).Array ?? [])
            {
                if (kid.Ref is not { } kidNumber) continue;
                var kidObject = Object(kidNumber);
                if (kidObject?["Count"]?.Int is { } count && kidObject["Type"]?.NameValue == "Pages" && count <= remaining)
                {
                    remaining -= count;
                    continue;
                }
                if (Walk(kidNumber, depth + 1) is { } found) return found;
            }
            return null;
        }
        return Walk(pages, 0);
    }

    /// Object numbers of the page's content streams, in order.
    public List<int> ContentStreams(int pageNumber)
    {
        if (Object(pageNumber)?["Contents"] is not { } contents) return [];
        if (contents.Ref is { } number)
            return Object(number)?.Array is { } array ? array.Select(r => r.Ref).OfType<int>().ToList() : [number];
        return contents.Array?.Select(r => r.Ref).OfType<int>().ToList() ?? [];
    }

    // MARK: Incremental update

    /// Appends new versions of streams (raw decoded content, re-compressed) as an incremental update.
    public byte[] IncrementalUpdate(Dictionary<int, byte[]> streams)
    {
        if (Trailer.ContainsKey("Encrypt")) throw new PdfFileException("encrypted", unsupported: true);
        if (UsesXrefStream) throw new PdfFileException("xref stream", unsupported: true);
        using var output = new MemoryStream();
        output.Write(Bytes);
        if (Bytes.Length == 0 || Bytes[^1] != 10) output.WriteByte(10);
        void Text(string s) => output.Write(Encoding.ASCII.GetBytes(s));
        var offsets = new List<(int Number, long Offset)>();
        foreach (var (number, content) in streams.OrderBy(s => s.Key))
        {
            var compressed = Deflate(content);
            offsets.Add((number, output.Position));
            Text($"{number} 0 obj\n<</Length {compressed.Length}/Filter/FlateDecode>>stream\n");
            output.Write(compressed);
            Text("\nendstream\nendobj\n");
        }
        var xrefOffset = output.Position;
        var xref = new StringBuilder("xref\n0 1\n0000000000 65535 f \n");
        foreach (var (number, offset) in offsets) xref.Append($"{number} 1\n{offset:D10} 00000 n \n");
        var trailer = new Dictionary<string, PdfObject>(Trailer) { ["Prev"] = PdfObject.FromNumber(LastXrefOffset) };
        trailer.Remove("XRefStm");
        xref.Append("trailer\n").Append(Serialize(PdfObject.FromDict(trailer))).Append($"\nstartxref\n{xrefOffset}\n%%EOF\n");
        Text(xref.ToString());
        return output.ToArray();
    }

    public static string Serialize(PdfObject obj) => obj.Kind switch
    {
        PdfKind.Null => "null",
        PdfKind.Bool => obj.Bool ? "true" : "false",
        PdfKind.Number => obj.Number == Math.Round(obj.Number) ? ((long)obj.Number).ToString(CultureInfo.InvariantCulture) : obj.Number.ToString("0.0000", CultureInfo.InvariantCulture),
        PdfKind.Name => "/" + obj.Name,
        PdfKind.String => "<" + Convert.ToHexString(obj.Bytes) + ">",
        PdfKind.Array => "[" + string.Join(" ", obj.Items.Select(Serialize)) + "]",
        PdfKind.Dict or PdfKind.Stream => "<<" + string.Join(" ", obj.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"/{e.Key} {Serialize(e.Value)}")) + ">>",
        _ => $"{obj.RefNumber} {obj.RefGeneration} R",
    };

    // MARK: Compression

    public static byte[]? Inflate(byte[] data)
    {
        if (data.Length <= 2) return [];
        try
        {
            using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            // Some writers omit the checksum or end the stream early: raw DEFLATE after the 2-byte header.
            try
            {
                using var input = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress);
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException) { return null; }
        }
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    static byte[] UnpredictPng(byte[] raw, int columns, int colors, int bitsPerComponent)
    {
        var bpp = Math.Max(1, colors * bitsPerComponent / 8);
        var rowLength = (columns * colors * bitsPerComponent + 7) / 8;
        var output = new List<byte>(raw.Length);
        var previous = new byte[rowLength];
        var i = 0;
        while (i + rowLength < raw.Length + 1 && i < raw.Length)
        {
            var type = raw[i];
            var row = new byte[rowLength];
            Array.Copy(raw, i + 1, row, 0, Math.Max(0, Math.Min(rowLength, raw.Length - i - 1)));
            for (var x = 0; x < rowLength; x++)
            {
                int left = x >= bpp ? row[x - bpp] : 0, up = previous[x], upLeft = x >= bpp ? previous[x - bpp] : 0;
                switch (type)
                {
                    case 1: row[x] = (byte)(row[x] + left); break;
                    case 2: row[x] = (byte)(row[x] + up); break;
                    case 3: row[x] = (byte)(row[x] + (left + up) / 2); break;
                    case 4:
                        var p = left + up - upLeft;
                        int pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - upLeft);
                        row[x] = (byte)(row[x] + (pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft));
                        break;
                }
            }
            output.AddRange(row);
            previous = row;
            i += 1 + rowLength;
        }
        return output.ToArray();
    }
}
