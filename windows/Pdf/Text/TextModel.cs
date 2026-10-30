using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace EdPdf.Pdf.Text;

/// fpdf_edit.h and fpdf_text.h functions used to read and write text objects.
static class TextNative
{
    const string Lib = "pdfium";

    [StructLayout(LayoutKind.Sequential)] public struct FsMatrix { public float A, B, C, D, E, F; }

    [DllImport(Lib)] public static extern int FPDFPageObj_GetMatrix(IntPtr obj, out FsMatrix matrix);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetMatrix(IntPtr obj, in FsMatrix matrix);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetFontSize(IntPtr obj, out float size);
    [DllImport(Lib)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr obj);
    [DllImport(Lib)] public static extern unsafe nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buffer, nuint length);
    [DllImport(Lib)] public static extern unsafe nuint FPDFFont_GetFamilyName(IntPtr font, byte* buffer, nuint length);
    [DllImport(Lib)] public static extern int FPDFFont_GetWeight(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFFont_GetItalicAngle(IntPtr font, out int angle);
    [DllImport(Lib)] public static extern int FPDFFont_GetFlags(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFFont_GetIsEmbedded(IntPtr font);
    [DllImport(Lib)] public static extern unsafe int FPDFFont_GetFontData(IntPtr font, byte* buffer, nuint length, out nuint written);
    [DllImport(Lib)] public static extern unsafe CULong FPDFTextObj_GetText(IntPtr obj, IntPtr textPage, char* buffer, CULong length);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetFillColor(IntPtr obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetTextRenderMode(IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFFormObj_CountObjects(IntPtr obj);
    [DllImport(Lib)] public static extern IntPtr FPDFFormObj_GetObject(IntPtr obj, CULong index);
    [DllImport(Lib)] public static extern IntPtr FPDFText_GetTextObject(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern double FPDFText_GetFontSize(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_IsGenerated(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float size);
    [DllImport(Lib)] public static extern unsafe IntPtr FPDFText_LoadFont(IntPtr document, byte* data, uint size, int fontType, int cid);
    [DllImport(Lib)] public static extern int FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr obj, nuint index);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr obj);

    public const int PageObjectText = 1, PageObjectForm = 5;
    public const int FontTrueType = 2;

    public static Matrix ToMatrix(FsMatrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);
    public static FsMatrix ToFs(Matrix m) => new() { A = (float)m.M11, B = (float)m.M12, C = (float)m.M21, D = (float)m.M22, E = (float)m.M31, F = (float)m.M32 };

    public static Matrix ObjectMatrix(IntPtr obj) => FPDFPageObj_GetMatrix(obj, out var m) != 0 ? ToMatrix(m) : Matrix.Identity;

    public static unsafe string Utf8(Func<IntPtr, nuint, nuint> fill)
    {
        var length = (int)fill(IntPtr.Zero, 0);
        if (length <= 1) return "";
        var buffer = new byte[length];
        fixed (byte* pointer = buffer) fill((IntPtr)pointer, (nuint)length);
        return Encoding.UTF8.GetString(buffer, 0, length - 1);
    }

    public static unsafe byte[]? FontData(IntPtr font)
    {
        if (FPDFFont_GetFontData(font, null, 0, out var length) == 0 || length == 0) return null;
        var bytes = new byte[(int)length];
        fixed (byte* pointer = bytes)
            if (FPDFFont_GetFontData(font, pointer, length, out _) == 0) return null;
        return bytes;
    }
}

/// Page-space rectangles (y up): readable names for the edges.
static class RectEdges
{
    public static double MinX(this Rect r) => r.X;
    public static double MaxX(this Rect r) => r.Right;
    public static double MinY(this Rect r) => r.Y;
    public static double MaxY(this Rect r) => r.Bottom;
    public static double MidX(this Rect r) => r.X + r.Width / 2;
}

/// Font of a text object as described by the PDF.
public sealed record FontInfo(string BaseName, string FamilyName, int Weight, int ItalicAngle, int Flags, bool IsEmbedded, bool HasSerifDesign = false)
{
    /// Base name without the "ABCDEF+" subset tag.
    public string PostScriptName
    {
        get
        {
            var parts = BaseName.Split('+', 2);
            return parts.Length == 2 && parts[0].Length == 6 && parts[0].All(char.IsUpper) ? parts[1] : BaseName;
        }
    }

    string LowerName => PostScriptName.ToLowerInvariant();

    public bool IsBold => Weight >= 600 || (Flags & (1 << 18)) != 0 || new[] { "bold", "black", "heavy", "semibold", "demi" }.Any(LowerName.Contains); // l10n-ignore
    public bool IsItalic => ItalicAngle != 0 || (Flags & (1 << 6)) != 0 || LowerName.Contains("italic") || LowerName.Contains("oblique"); // l10n-ignore
    public bool IsMonospaced => (Flags & 1) != 0;
    public bool IsSerif => (Flags & 2) != 0 || HasSerifDesign;

    public static readonly FontInfo Empty = new("", "", 400, 0, 0, false);
}

/// RGBA, 0…1.
public readonly record struct TextColor(double Red, double Green, double Blue, double Alpha)
{
    public static readonly TextColor Black = new(0, 0, 0, 1);
}

/// One PDF text object (a "run": same font, size and color).
public sealed record TextRun
{
    /// Index of the object on the page, then inside nested form XObjects.
    public required int[] Path { get; init; }
    public required string Text { get; init; }
    /// Page space.
    public required Rect Bounds { get; init; }
    /// Text space to page space, including the font size scale applied by the text matrix.
    public required Matrix Matrix { get; init; }
    /// PDFium's text extraction puts a space (real or generated) right after this run.
    public bool FollowedBySpace { get; init; }
    /// Font size as written in the PDF (Tf).
    public required double NominalSize { get; init; }
    public required FontInfo Font { get; init; }
    public required TextColor Color { get; init; }
    public int RenderMode { get; init; }

    /// Size as it appears on the page.
    public double FontSize => NominalSize * Math.Sqrt(Matrix.M21 * Matrix.M21 + Matrix.M22 * Matrix.M22);
    /// Baseline start, page space.
    public Point Origin => new(Matrix.M31, Matrix.M32);
    /// Rotation of the baseline, in degrees.
    public double Angle => Math.Atan2(Matrix.M12, Matrix.M11) * 180 / Math.PI;
    /// Only top-level, unrotated, visible text is editable for now.
    public bool IsEditable => Path.Length == 1 && Math.Abs(Angle) < 0.5 && RenderMode != 3;
}

public sealed class TextLine
{
    public required List<TextRun> Runs { get; init; }
    public Rect Bounds { get; init; }
    public double Baseline { get; init; }
    public string Text { get; init; } = "";
    public double FontSize => Runs.Count == 0 ? 0 : Runs.Max(r => r.FontSize);
}

/// A paragraph: consecutive lines with similar size and regular spacing.
public sealed class TextBlock
{
    public List<TextLine> Lines { get; } = [];
    public Rect Bounds { get; set; }

    public IEnumerable<TextRun> Runs => Lines.SelectMany(l => l.Runs);
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
    public bool IsEditable => Runs.All(r => r.IsEditable);

    /// Font, size and color covering the most characters.
    public TextRun? DominantRun => Runs.GroupBy(r => (r.Font.BaseName, (int)(r.FontSize * 10), r.Color))
        .MaxBy(g => g.Sum(r => r.Text.Length))?.First();

    /// Median distance between baselines, or 1.2 × size for a single line.
    public double Leading
    {
        get
        {
            var gaps = Lines.Zip(Lines.Skip(1), (a, b) => a.Baseline - b.Baseline).Order().ToList();
            return gaps.Count == 0 ? (DominantRun?.FontSize ?? 12) * 1.2 : gaps[gaps.Count / 2];
        }
    }
}

/// A PDFium page used to read and write text. Does not own the page unless created by TextDocument.
public sealed class TextPage : IDisposable
{
    public IntPtr Document { get; }
    public IntPtr Handle { get; }
    readonly bool ownsPage;
    IntPtr textPage;
    readonly Dictionary<IntPtr, FontInfo> fontInfoCache = [];

    public TextPage(IntPtr document, IntPtr page, bool ownsPage)
    {
        Document = document;
        Handle = page;
        this.ownsPage = ownsPage;
    }

    public IntPtr TextPageHandle => textPage != IntPtr.Zero ? textPage : textPage = Native.FPDFText_LoadPage(Handle);

    public int ObjectCount => Native.FPDFPage_CountObjects(Handle);

    /// Rewrites the page content stream after objects were changed, added or removed.
    public bool GenerateContent() => Native.FPDFPage_GenerateContent(Handle) != 0;

    public void Dispose()
    {
        if (textPage != IntPtr.Zero) Native.FPDFText_ClosePage(textPage);
        textPage = IntPtr.Zero;
        if (ownsPage) Native.FPDF_ClosePage(Handle);
    }

    /// Top-level text objects in page order (including whitespace-only ones): page index and matrix (Tm × CTM).
    public List<(int Index, Matrix Matrix)> TopLevelTextObjects()
    {
        var result = new List<(int, Matrix)>();
        for (var i = 0; i < ObjectCount; i++)
        {
            var obj = Native.FPDFPage_GetObject(Handle, i);
            if (Native.FPDFPageObj_GetType(obj) == TextNative.PageObjectText) result.Add((i, TextNative.ObjectMatrix(obj)));
        }
        return result;
    }

    public List<TextRun> TextRuns()
    {
        var runs = new List<TextRun>();
        for (var i = 0; i < ObjectCount; i++)
            if (Native.FPDFPage_GetObject(Handle, i) is var obj && obj != IntPtr.Zero) Collect(obj, [i], Matrix.Identity, runs);
        var spaced = ObjectsFollowedBySpace();
        for (var i = 0; i < runs.Count; i++)
            if (runs[i].Path.Length == 1 && spaced.Contains(Native.FPDFPage_GetObject(Handle, runs[i].Path[0])))
                runs[i] = runs[i] with { FollowedBySpace = true };
        return runs;
    }

    /// Text objects followed, on the same line, by a word gap: the next glyph starts more than 15 % of the font
    /// size after the advance of the object's last glyph ("loose" boxes span the advance). This tells real word gaps
    /// from kerning between glyph runs or style changes ("2026" + ",").
    HashSet<IntPtr> ObjectsFollowedBySpace()
    {
        var result = new HashSet<IntPtr>();
        var text = TextPageHandle;
        if (text == IntPtr.Zero) return result;
        (IntPtr Object, float Right, float Y, double Size)? last = null;
        for (var i = 0; i < Native.FPDFText_CountChars(text); i++)
        {
            var unicode = Native.FPDFText_GetUnicode(text, i);
            if (unicode <= 32 || unicode == 0xA0) continue;
            var obj = TextNative.FPDFText_GetTextObject(text, i);
            if (obj == IntPtr.Zero || Native.FPDFText_GetLooseCharBox(text, i, out var box) == 0) continue;
            var size = TextNative.FPDFText_GetFontSize(text, i);
            if (last is { } previous && previous.Object != obj && Math.Abs(box.Bottom - previous.Y) < previous.Size * 0.5
                && box.Left - previous.Right > 0.15 * EffectiveSize(previous.Object, previous.Size))
                result.Add(previous.Object);
            last = (obj, box.Right, box.Bottom, size);
        }
        return result;
    }

    static double EffectiveSize(IntPtr obj, double fallback)
    {
        TextNative.FPDFTextObj_GetFontSize(obj, out var nominal);
        var m = TextNative.ObjectMatrix(obj);
        var size = nominal * Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
        return size > 0 ? size : fallback;
    }

    void Collect(IntPtr obj, int[] path, Matrix parent, List<TextRun> runs)
    {
        switch (Native.FPDFPageObj_GetType(obj))
        {
            case TextNative.PageObjectText:
                if (MakeRun(obj, path, parent) is { } run) runs.Add(run);
                break;
            case TextNative.PageObjectForm:
                var transform = TextNative.ObjectMatrix(obj) * parent;
                for (var i = 0; i < TextNative.FPDFFormObj_CountObjects(obj); i++)
                    if (TextNative.FPDFFormObj_GetObject(obj, new CULong((uint)i)) is var child && child != IntPtr.Zero)
                        Collect(child, [.. path, i], transform, runs);
                break;
        }
    }

    unsafe string ObjectText(IntPtr obj)
    {
        var bytes = (int)TextNative.FPDFTextObj_GetText(obj, TextPageHandle, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new char[bytes / 2];
        fixed (char* pointer = buffer) TextNative.FPDFTextObj_GetText(obj, TextPageHandle, pointer, new CULong((uint)bytes));
        var length = buffer.Length;
        while (length > 0 && buffer[length - 1] == '\0') length--;
        return new string(buffer, 0, length);
    }

    TextRun? MakeRun(IntPtr obj, int[] path, Matrix parent)
    {
        var text = ObjectText(obj);
        if (string.IsNullOrWhiteSpace(text)) return null;
        Native.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top);
        var bounds = new Rect(left, bottom, right - left, top - bottom).TransformToAABB(parent);
        TextNative.FPDFTextObj_GetFontSize(obj, out var size);
        TextNative.FPDFPageObj_GetFillColor(obj, out var r, out var g, out var b, out var a);
        return new TextRun
        {
            Path = path, Text = text, Bounds = bounds, Matrix = TextNative.ObjectMatrix(obj) * parent, NominalSize = size,
            Font = FontInfoOf(TextNative.FPDFTextObj_GetFont(obj)),
            Color = new TextColor(r / 255.0, g / 255.0, b / 255.0, a / 255.0),
            RenderMode = TextNative.FPDFTextObj_GetTextRenderMode(obj),
        };
    }

    FontInfo FontInfoOf(IntPtr font)
    {
        if (font == IntPtr.Zero) return FontInfo.Empty;
        if (fontInfoCache.TryGetValue(font, out var cached)) return cached;
        TextNative.FPDFFont_GetItalicAngle(font, out var angle);
        unsafe
        {
            var info = new FontInfo(
                TextNative.Utf8((buffer, length) => TextNative.FPDFFont_GetBaseFontName(font, (byte*)buffer, length)),
                TextNative.Utf8((buffer, length) => TextNative.FPDFFont_GetFamilyName(font, (byte*)buffer, length)),
                TextNative.FPDFFont_GetWeight(font), angle, TextNative.FPDFFont_GetFlags(font),
                TextNative.FPDFFont_GetIsEmbedded(font) != 0, HasSerifDesign(font));
            return fontInfoCache[font] = info;
        }
    }

    /// Serif design read from the embedded font program (OS/2 family class), PDF flags being often missing.
    static bool HasSerifDesign(IntPtr font)
    {
        if (TextNative.FPDFFont_GetIsEmbedded(font) == 0 || TextNative.FontData(font) is not { } data) return false;
        return Sfnt.Table(data, "OS/2") is { Length: >= 32 } os2 && os2[30] is 1 or 2 or 3 or 4 or 5 or 7;
    }
}

/// Reading tables of TrueType / OpenType font data.
static class Sfnt
{
    public static byte[]? Table(byte[] data, string tag)
    {
        if (data.Length < 12) return null;
        int count = data[4] << 8 | data[5];
        var wanted = Encoding.ASCII.GetBytes(tag);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + 16 * i;
            if (record + 16 > data.Length) return null;
            if (!data.AsSpan(record, 4).SequenceEqual(wanted)) continue;
            int offset = data[record + 8] << 24 | data[record + 9] << 16 | data[record + 10] << 8 | data[record + 11];
            int length = data[record + 12] << 24 | data[record + 13] << 16 | data[record + 14] << 8 | data[record + 15];
            return offset >= 0 && length >= 0 && offset + length <= data.Length ? data.AsSpan(offset, length).ToArray() : null;
        }
        return null;
    }
}

public static class TextLayout
{
    /// Groups the runs of a page into paragraphs, top to bottom.
    public static List<TextBlock> Blocks(List<TextRun> runs) => GroupBlocks(Lines(runs));

    /// Runs sharing a baseline, split where a wide gap suggests another column, or where the color changes across
    /// more than a word space (a black "B2" in a white box next to a white title on a dark banner).
    public static List<TextLine> Lines(List<TextRun> runs)
    {
        var rows = new List<List<TextRun>>();
        foreach (var run in runs.OrderByDescending(r => r.Origin.Y))
        {
            if (rows.Count > 0 && rows[^1][^1] is var last && Math.Abs(last.Origin.Y - run.Origin.Y) < Math.Max(last.FontSize, run.FontSize) * 0.35)
                rows[^1].Add(run);
            else
                rows.Add([run]);
        }
        var lines = new List<TextLine>();
        foreach (var row in rows)
        {
            var current = new List<TextRun>();
            foreach (var run in row.OrderBy(r => r.Bounds.MinX()))
            {
                if (current.Count > 0)
                {
                    var last = current[^1];
                    var gap = (run.Bounds.MinX() - last.Bounds.MaxX()) / Math.Max(last.FontSize, run.FontSize);
                    if (gap > 1.5 || (gap > 0.6 && last.Color != run.Color))
                    {
                        lines.Add(MakeLine(current));
                        current = [];
                    }
                }
                current.Add(run);
            }
            if (current.Count > 0) lines.Add(MakeLine(current));
        }
        return lines;
    }

    static TextLine MakeLine(List<TextRun> runs)
    {
        var text = new StringBuilder();
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var ends = text.Length > 0 && text[^1] == ' ';
            if (i > 0 && runs[i - 1].FollowedBySpace && !ends && !run.Text.StartsWith(' ')) text.Append(' ');
            ends = text.Length > 0 && text[^1] == ' ';
            text.Append(ends && run.Text.StartsWith(' ') ? run.Text[1..] : run.Text);
        }
        var bounds = runs.Skip(1).Aggregate(runs[0].Bounds, (all, r) => all.Union(r.Bounds));
        var baselines = runs.Select(r => r.Origin.Y).Order().ToList();
        return new TextLine { Runs = runs, Bounds = bounds, Baseline = baselines[runs.Count / 2], Text = text.ToString() };
    }

    static List<TextBlock> GroupBlocks(List<TextLine> lines)
    {
        var blocks = new List<TextBlock>();
        foreach (var line in lines.OrderByDescending(l => l.Baseline))
        {
            var size = line.FontSize;
            var index = blocks.FindLastIndex(block =>
            {
                var last = block.Lines[^1];
                var drop = last.Baseline - line.Baseline;
                var similarSize = Math.Abs(last.FontSize - size) <= Math.Max(last.FontSize, size) * 0.2;
                var regular = block.Lines.Count < 2 || Math.Abs(drop - block.Leading) <= block.Leading * 0.25;
                var overlapping = line.Bounds.MinX() < block.Bounds.MaxX() && line.Bounds.MaxX() > block.Bounds.MinX();
                return drop > size * 0.8 && drop < size * 2 && similarSize && regular && overlapping;
            });
            if (index >= 0)
            {
                blocks[index].Lines.Add(line);
                blocks[index].Bounds = blocks[index].Bounds.Union(line.Bounds);
            }
            else
            {
                var block = new TextBlock { Bounds = line.Bounds };
                block.Lines.Add(line);
                blocks.Add(block);
            }
        }
        return blocks;
    }
}

/// A PDFium document opened from bytes for text editing (owns its buffer). Used under Pdfium.Lock.
public sealed class TextDocument : IDisposable
{
    public IntPtr Handle { get; }
    readonly IntPtr buffer;

    TextDocument(IntPtr handle, IntPtr buffer)
    {
        Handle = handle;
        this.buffer = buffer;
    }

    public static TextDocument? Open(byte[] data, string? password)
    {
        Pdfium.EnsureInitialized();
        var buffer = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        Marshal.Copy(data, 0, buffer, data.Length);
        var handle = Native.FPDF_LoadMemDocument64(buffer, (nuint)data.Length, password);
        if (handle != IntPtr.Zero) return new TextDocument(handle, buffer);
        Marshal.FreeHGlobal(buffer);
        return null;
    }

    public TextPage? Page(int index)
    {
        var page = Native.FPDF_LoadPage(Handle, index);
        return page == IntPtr.Zero ? null : new TextPage(Handle, page, ownsPage: true);
    }

    /// Serializes the document, including edits whose page content was regenerated.
    public byte[] Data()
    {
        using var stream = new MemoryStream();
        PdfDocument.Write(Handle, stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        Native.FPDF_CloseDocument(Handle);
        Marshal.FreeHGlobal(buffer);
    }
}
