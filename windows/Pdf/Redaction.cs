using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace PdfSvd.Pdf;

internal static partial class Native
{
    [DllImport(Lib)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out RectF rect);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewTextObj(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font, float size);
    [DllImport(Lib)] public static extern int FPDFText_SetText(IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern int FPDFTextObj_SetTextRenderMode(IntPtr textObject, int mode);
    [DllImport(Lib)] public static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    [DllImport(Lib)] public static extern int FPDF_GetPageBoundingBox(IntPtr page, out RectF rect);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr pageObject);

    public const int TextRenderInvisible = 3;
    public const int BitmapBGRx = 3;
}

/// Redaction happens in two steps: the user places marks (Square annotations tagged /FolioRedact, as on the Mac),
/// then applies them. Applying rebuilds each marked page as a 300 ppi image with black boxes, plus invisible text
/// for every word outside the marks: the hidden content is really gone, the rest stays searchable and selectable.
public sealed partial class PdfDocument
{
    const double RedactionDpi = 300;
    const string RedactKey = "FolioRedact";

    /// Appearance of a mark: red outline and hatching (an appearance stream cannot use transparency without resources).
    internal static string RedactionAppearance(Rect b)
    {
        var stream = new StringBuilder("1 0.23 0.19 RG\n");
        stream.Append($"q {F(b.X)} {F(b.Y)} {F(b.Width)} {F(b.Height)} re W n 0.5 w\n");
        for (var offset = -b.Height; offset < b.Width; offset += 5)
            stream.Append($"{F(b.X + offset)} {F(b.Y)} m {F(b.X + offset + b.Height)} {F(b.Bottom)} l\n");
        stream.Append("S Q\n");
        stream.Append($"1 w {F(b.X + 0.5)} {F(b.Y + 0.5)} {F(Math.Max(0, b.Width - 1))} {F(Math.Max(0, b.Height - 1))} re S\n");
        return stream.ToString();
    }

    public int RedactionMarkCount(int index) => WithPage(index, 0, page =>
    {
        var count = 0;
        for (var i = 0; i < Native.FPDFPage_GetAnnotCount(page); i++)
        {
            var annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            if (GetString(annot, RedactKey).Length > 0) count++;
            Native.FPDFPage_CloseAnnot(annot);
        }
        return count;
    });

    /// Replaces a page by its redacted version. False if the page has no marks.
    public bool Redact(int index)
    {
        lock (Pdfium.Lock)
        {
            GenerateAppearances([index]);
            var built = WithPage(index, false, page => BuildRedactedPage(page, index));
            if (!built) return false;
            Native.FPDFPage_Delete(handle, index);
            PagesChanged();
            return true;
        }
    }

    /// Inserts the redacted version of `page` just after it. Under the lock.
    bool BuildRedactedPage(IntPtr page, int index)
    {
        // Marks, removed from the page (they must not be drawn into the image).
        var areas = new List<Rect>();
        for (var i = Native.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
        {
            var annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            var isMark = GetString(annot, RedactKey).Length > 0;
            if (isMark && Native.FPDFAnnot_GetRect(annot, out var r) != 0)
                areas.Add(new Rect(new Point(r.Left, r.Bottom), new Point(r.Right, r.Top)).Normalize());
            Native.FPDFPage_CloseAnnot(annot);
            if (isMark) Native.FPDFPage_RemoveAnnot(page, i);
        }
        if (areas.Count == 0) return false;
        var words = Words(page).Where(w => !areas.Any(a => Covers(a, w.Bounds))).ToList();

        // The page is drawn upright; its rotation is given back to the new page.
        var rotation = Native.FPDFPage_GetRotation(page);
        Native.FPDFPage_SetRotation(page, 0);
        // Visible area: crop box within the media box (inherited from the page tree if need be).
        Native.FPDF_GetPageBoundingBox(page, out var visible);
        var box = new Rect(new Point(visible.Left, visible.Bottom), new Point(visible.Right, visible.Top)).Normalize();
        var scale = RedactionDpi / 72;
        int width = Math.Max(1, (int)Math.Round(box.Width * scale)), height = Math.Max(1, (int)Math.Round(box.Height * scale));

        var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.BitmapBGRx, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) { Native.FPDFPage_SetRotation(page, rotation); return false; }
        try
        {
            Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, new CULong(0xFFFFFFFF));
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Native.RenderAnnotations);
            foreach (var area in areas)
            {
                int x1 = (int)Math.Floor((area.X - box.X) * scale), x2 = (int)Math.Ceiling((area.Right - box.X) * scale);
                int y1 = (int)Math.Floor((box.Bottom - area.Bottom) * scale), y2 = (int)Math.Ceiling((box.Bottom - area.Y) * scale);
                x1 = Math.Clamp(x1, 0, width); x2 = Math.Clamp(x2, 0, width);
                y1 = Math.Clamp(y1, 0, height); y2 = Math.Clamp(y2, 0, height);
                if (x2 > x1 && y2 > y1) Native.FPDFBitmap_FillRect(bitmap, x1, y1, x2 - x1, y2 - y1, new CULong(0xFF000000));
            }
            Native.FPDFPage_SetRotation(page, rotation);

            var result = Native.FPDFPage_New(handle, index + 1, box.Width, box.Height);
            if (result == IntPtr.Zero) return false;
            var image = Native.FPDFPageObj_NewImageObj(handle);
            Native.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap);
            Native.FPDFImageObj_SetMatrix(image, box.Width, 0, 0, box.Height, 0, 0);
            Native.FPDFPage_InsertObject(result, image);
            foreach (var word in words) AddInvisibleWord(result, word.Text, word.Bounds.Translate(new Vector(-box.X, -box.Y)));
            Native.FPDFPage_GenerateContent(result);
            Native.FPDFPage_SetRotation(result, rotation);
            Native.FPDF_ClosePage(result);
            return true;
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }

    /// Words of a page with their bounds in page space: letters between blanks, also cut where the text jumps
    /// to another line or back.
    static List<(string Text, Rect Bounds)> Words(IntPtr page)
    {
        var words = new List<(string Text, Rect Bounds)>();
        var textPage = Native.FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero) return words;
        var text = new StringBuilder();
        Rect bounds = default;
        void Flush()
        {
            if (text.Length > 0) words.Add((text.ToString(), bounds));
            text.Clear();
        }
        for (var i = 0; i < Native.FPDFText_CountChars(textPage); i++)
        {
            var code = Native.FPDFText_GetUnicode(textPage, i);
            var character = code is > 0 and <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? char.ConvertFromUtf32((int)code) : "";
            if (string.IsNullOrWhiteSpace(character) || Native.FPDFText_GetLooseCharBox(textPage, i, out var r) == 0) { Flush(); continue; }
            var box = new Rect(new Point(r.Left, r.Bottom), new Point(r.Right, r.Top)).Normalize();
            if (box.Width <= 0 || box.Height <= 0) { Flush(); continue; }
            if (text.Length > 0 && (box.Y >= bounds.Bottom || box.Bottom <= bounds.Y || box.Center.X < bounds.X)) Flush();
            bounds = text.Length == 0 ? box : bounds.Union(box);
            text.Append(character);
        }
        Flush();
        Native.FPDFText_ClosePage(textPage);
        return words;
    }

    /// Whether a word is hidden by a marked area: words only partly covered are removed entirely, but merely
    /// touching an area does not count.
    static bool Covers(Rect area, Rect word)
    {
        var overlap = area.Intersect(word);
        return overlap.Width > Math.Min(1, word.Width * 0.2) && overlap.Height > word.Height * 0.3;
    }

    /// Invisible text stretched over a word's bounds (so that it can be found and selected).
    void AddInvisibleWord(IntPtr page, string text, Rect bounds)
    {
        var size = (float)Math.Max(bounds.Height * 0.85, 1);
        var obj = Native.FPDFPageObj_NewTextObj(handle, "Helvetica", size);
        if (obj == IntPtr.Zero) return;
        Native.FPDFText_SetText(obj, text);
        Native.FPDFTextObj_SetTextRenderMode(obj, Native.TextRenderInvisible);
        Native.FPDFPageObj_GetBounds(obj, out var left, out _, out var right, out _);
        var natural = right - left;
        if (natural <= 0) { Native.FPDFPageObj_Destroy(obj); return; }
        // A space after each word (beyond its bounds, invisible): the extracted text keeps words apart.
        Native.FPDFText_SetText(obj, text + " ");
        Native.FPDFPageObj_Transform(obj, bounds.Width / natural, 0, 0, 1, bounds.X, bounds.Y + bounds.Height * 0.2);
        Native.FPDFPage_InsertObject(page, obj);
    }
}
