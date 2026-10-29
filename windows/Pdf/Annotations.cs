using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;

namespace PdfSvd.Pdf;

/// fpdf_annot.h and fpdf_save.h.
internal static partial class Native
{
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetRect(IntPtr annot, out RectF rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, in RectF rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetBorder(IntPtr annot, out float horizontalRadius, out float verticalRadius, out float width);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float width);
    [DllImport(Lib)] public static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint index, out QuadF quad);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetAttachmentPoints(IntPtr annot, nuint index, in QuadF quad);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, in QuadF quad);
    [DllImport(Lib)] public static extern CULong FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport(Lib)] public static extern unsafe CULong FPDFAnnot_GetInkListPath(IntPtr annot, CULong index, PointF* buffer, CULong length);
    [DllImport(Lib)] public static extern unsafe int FPDFAnnot_AddInkStroke(IntPtr annot, PointF* points, nuint count);
    [DllImport(Lib)] public static extern int FPDFAnnot_RemoveInkList(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern unsafe CULong FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, char* buffer, CULong length);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetAP(IntPtr annot, int mode, [MarshalAs(UnmanagedType.LPWStr)] string? value);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetFlags(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr pageObject);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetObjectCount(IntPtr annot);
    [DllImport(Lib)] public static extern IntPtr FPDFAnnot_GetObject(IntPtr annot, int index);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetBitmap(IntPtr pages, int count, IntPtr imageObject, IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetMatrix(IntPtr imageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern IntPtr FPDFImageObj_GetBitmap(IntPtr imageObject);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite writer, CULong flags);

    [StructLayout(LayoutKind.Sequential)] public struct RectF { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct PointF { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct QuadF { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }
    [StructLayout(LayoutKind.Sequential)] public struct FileWrite { public int Version; public IntPtr WriteBlock; }

    public const int ColorStroke = 0;
    public const int AppearanceNormal = 0;
    public const int FlagHidden = 2, FlagPrint = 4;
    public const int SaveNoIncremental = 2;
    public const int PageObjectImage = 3;
}

/// Annotation types pdf-svd can create and edit (PDF subtype numbers in PDFium).
public enum AnnotationKind { Note = 1, TextBox = 3, Rectangle = 5, Ellipse = 6, Highlight = 9, Underline = 10, Squiggly = 11, StrikeOut = 12, Stamp = 13, Ink = 15 }

/// An editable annotation, in page space: PDF points with the origin at the bottom left (so `Bounds.Y` is the bottom edge).
/// Identified by its /NM name, given to it on first read if it had none.
public sealed record Annotation
{
    public required string Name { get; init; }
    public required AnnotationKind Kind { get; init; }
    public Rect Bounds { get; init; }
    public Color Color { get; init; } = Colors.Black;
    public double LineWidth { get; init; } = 1;
    /// Ink strokes (lines and arrows are drawn as ink too: PDFium cannot create Line annotations).
    public Point[][] Ink { get; init; } = [];
    /// Highlight, underline, strike-out: one quadrilateral per line, top left, top right, bottom left, bottom right.
    public Point[][] Quads { get; init; } = [];
    public string Contents { get; init; } = "";
    public double FontSize { get; init; } = 12;
    /// Text box drawn as a stamp: bold, centred, framed.
    public bool StampStyle { get; init; }
    /// Image of a Stamp annotation (PNG), when known: given when placing an image, read with PdfDocument.StampImage.
    public byte[]? ImagePng { get; init; }

    public bool IsMarkup => Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.Squiggly or AnnotationKind.StrikeOut;
    public bool IsResizable => Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.TextBox or AnnotationKind.Stamp;
    public bool HasLineWidth => Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Ink;

    public Annotation Offset(Vector delta) => this with
    {
        Bounds = Bounds.Translate(delta),
        Ink = Ink.Select(stroke => stroke.Select(p => p + delta).ToArray()).ToArray(),
        Quads = Quads.Select(quad => quad.Select(p => p + delta).ToArray()).ToArray(),
    };

    /// Bounds of ink strokes, widened by the line width.
    public static Rect InkBounds(IEnumerable<Point[]> strokes, double lineWidth)
    {
        var points = strokes.SelectMany(s => s).ToList();
        if (points.Count == 0) return default;
        return new Rect(new Point(points.Min(p => p.X), points.Min(p => p.Y)), new Point(points.Max(p => p.X), points.Max(p => p.Y)))
            .Inflate(lineWidth / 2 + 1);
    }
}

public sealed partial class PdfDocument
{
    /// Runs `body(page)` under the lock with a freshly loaded page; `fallback` if the document is closed.
    T WithPage<T>(int index, T fallback, Func<IntPtr, T> body)
    {
        lock (Pdfium.Lock)
        {
            if (closed || index < 0 || index >= PageCount) return fallback;
            var page = Native.FPDF_LoadPage(handle, index);
            if (page == IntPtr.Zero) return fallback;
            try { return body(page); }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    // MARK: Coordinates

    readonly Dictionary<int, Matrix> pageToView = [];

    /// Page space → view space (points from the page's top left, as displayed: rotation and crop box applied).
    public Matrix PageToView(int index)
    {
        lock (Pdfium.Lock)
        {
            if (pageToView.TryGetValue(index, out var cached)) return cached;
            var matrix = WithPage(index, Matrix.Identity, page =>
            {
                var (width, height) = DeviceSize(index);
                Point Device(double x, double y)
                {
                    Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, x, y, out var dx, out var dy);
                    return new Point(dx / Precision, dy / Precision);
                }
                Point origin = Device(0, 0), unitX = Device(1000, 0), unitY = Device(0, 1000);
                return new Matrix((unitX.X - origin.X) / 1000, (unitX.Y - origin.Y) / 1000,
                                  (unitY.X - origin.X) / 1000, (unitY.Y - origin.Y) / 1000, origin.X, origin.Y);
            });
            return pageToView[index] = matrix;
        }
    }

    public Matrix ViewToPage(int index) => PageToView(index).Invert();

    // MARK: Annotations

    /// The annotations of a page that pdf-svd can edit, bottom to top.
    public List<Annotation> Annotations(int index) => WithPage(index, new List<Annotation>(), page =>
    {
        var list = new List<Annotation>();
        var count = Native.FPDFPage_GetAnnotCount(page);
        for (var i = 0; i < count; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try { if (Read(annot) is { } annotation) list.Add(annotation); }
            finally { Native.FPDFPage_CloseAnnot(annot); }
        }
        return list;
    });

    public void AddAnnotation(int index, Annotation annotation) => WithPage(index, false, page =>
    {
        var annot = Native.FPDFPage_CreateAnnot(page, (int)annotation.Kind);
        if (annot == IntPtr.Zero) return false;
        Native.FPDFAnnot_SetStringValue(annot, "T", Environment.UserName);
        Native.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTime.Now));
        Write(annot, annotation, isNew: true);
        if (annotation is { Kind: AnnotationKind.Stamp, ImagePng: { } png }) AppendImage(annot, annotation.Bounds, png);
        Native.FPDFPage_CloseAnnot(annot);
        return true;
    });

    /// Draws a PNG in a new stamp annotation (PDFium makes its appearance from the image object).
    void AppendImage(IntPtr annot, Rect bounds, byte[] png)
    {
        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        if (decoded == null) return;
        // PDFium bitmaps are BGRA with straight (not premultiplied) alpha.
        using var bgra = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(decoded.Width, decoded.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        decoded.CopyTo(bgra, SkiaSharp.SKColorType.Bgra8888);
        var bitmap = Native.FPDFBitmap_CreateEx(bgra.Width, bgra.Height, Native.BitmapBGRA, bgra.GetPixels(), bgra.RowBytes);
        var image = Native.FPDFPageObj_NewImageObj(handle);
        Native.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap);
        Native.FPDFImageObj_SetMatrix(image, bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y);
        Native.FPDFAnnot_AppendObject(annot, image);
        Native.FPDFBitmap_Destroy(bitmap);
        // Also kept as PNG, like the Mac version (which draws it from there): lets pdf-svd place it again after an undo.
        Native.FPDFAnnot_SetStringValue(annot, ImageKey, Convert.ToBase64String(png));
    }

    /// The image of a stamp: its /FolioImage PNG, or else its first image (without transparency). Null if it has none.
    public byte[]? StampImage(int index, string name) => WithAnnotation(index, name, (_, annot, _) =>
    {
        if (GetString(annot, ImageKey) is { Length: > 0 } encoded)
            try { return Convert.FromBase64String(encoded); } catch (FormatException) { }
        for (var i = 0; i < Native.FPDFAnnot_GetObjectCount(annot); i++)
        {
            var obj = Native.FPDFAnnot_GetObject(annot, i);
            if (Native.FPDFPageObj_GetType(obj) != Native.PageObjectImage) continue;
            var bitmap = Native.FPDFImageObj_GetBitmap(obj);
            if (bitmap == IntPtr.Zero) continue;
            try { return EncodePng(bitmap); }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
        return null;
    }, (byte[]?)null);

    static byte[]? EncodePng(IntPtr bitmap)
    {
        using var image = ToSkia(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)?.ToArray();
    }

    /// Copy of a PDFium bitmap (gray, BGR, BGRx or BGRA) as a BGRA Skia bitmap.
    internal static unsafe SkiaSharp.SKBitmap ToSkia(IntPtr bitmap)
    {
        int width = Native.FPDFBitmap_GetWidth(bitmap), height = Native.FPDFBitmap_GetHeight(bitmap), stride = Native.FPDFBitmap_GetStride(bitmap);
        var format = Native.FPDFBitmap_GetFormat(bitmap);
        var source = (byte*)Native.FPDFBitmap_GetBuffer(bitmap);
        var result = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888,
            format == 4 ? SkiaSharp.SKAlphaType.Unpremul : SkiaSharp.SKAlphaType.Opaque));
        var target = (byte*)result.GetPixels();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                byte* from = source + y * stride, to = target + (y * width + x) * 4;
                switch (format)
                {
                    case 1: to[0] = to[1] = to[2] = from[x]; to[3] = 255; break; // gray
                    case 2: to[0] = from[x * 3]; to[1] = from[x * 3 + 1]; to[2] = from[x * 3 + 2]; to[3] = 255; break; // BGR
                    default: to[0] = from[x * 4]; to[1] = from[x * 4 + 1]; to[2] = from[x * 4 + 2]; to[3] = format == 4 ? from[x * 4 + 3] : (byte)255; break;
                }
            }
        return result;
    }

    /// `keepAppearance`: only the position changed; the appearance follows the rectangle (keeps that of annotations
    /// made by other apps, which PDFium may not know how to draw again).
    public void UpdateAnnotation(int index, Annotation annotation, bool keepAppearance = false) => WithAnnotation(index, annotation.Name, (_, annot, _) =>
    {
        Native.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTime.Now));
        Write(annot, annotation, isNew: false, keepAppearance);
    });

    public void RemoveAnnotation(int index, string name) => WithAnnotation(index, name, (page, _, position) =>
        Native.FPDFPage_RemoveAnnot(page, position));

    /// Hides an annotation while it is edited in place (not saved: editing ends with an update, which shows it again).
    public void SetAnnotationHidden(int index, string name, bool hidden) => WithAnnotation(index, name, (_, annot, _) =>
    {
        var flags = Native.FPDFAnnot_GetFlags(annot);
        Native.FPDFAnnot_SetFlags(annot, hidden ? flags | Native.FlagHidden : flags & ~Native.FlagHidden);
    });

    void WithAnnotation(int index, string name, Action<IntPtr, IntPtr, int> body) =>
        WithAnnotation(index, name, (page, annot, position) => { body(page, annot, position); return true; }, false);

    T WithAnnotation<T>(int index, string name, Func<IntPtr, IntPtr, int, T> body, T fallback) => WithPage(index, fallback, page =>
    {
        var count = Native.FPDFPage_GetAnnotCount(page);
        for (var i = 0; i < count; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try { if (GetString(annot, "NM") == name) return body(page, annot, i); }
            finally { Native.FPDFPage_CloseAnnot(annot); }
        }
        return fallback;
    });

    static Annotation? Read(IntPtr annot)
    {
        var kind = (AnnotationKind)Native.FPDFAnnot_GetSubtype(annot);
        if (!Enum.IsDefined(kind)) return null;
        var name = GetString(annot, "NM");
        if (name.Length == 0)
        {
            name = "pdf-svd-" + Guid.NewGuid().ToString("N");
            Native.FPDFAnnot_SetStringValue(annot, "NM", name);
        }
        Native.FPDFAnnot_GetRect(annot, out var r);
        // PDFium only gives the colour of annotations without an appearance: pdf-svd also keeps it in /FolioColor.
        var color = Colors.Black;
        if (Native.FPDFAnnot_GetColor(annot, Native.ColorStroke, out var red, out var green, out var blue, out _) != 0)
            color = Color.FromRgb((byte)red, (byte)green, (byte)blue);
        else if (Color.TryParse(GetString(annot, ColorKey), out var saved))
            color = saved;
        else if (kind == AnnotationKind.Highlight)
            color = Colors.Yellow;
        var lineWidth = Native.FPDFAnnot_GetBorder(annot, out _, out _, out var width) != 0 ? width : 1;

        var ink = new List<Point[]>();
        var strokes = (int)Native.FPDFAnnot_GetInkListCount(annot).Value;
        for (var i = 0; i < strokes; i++)
        {
            var length = (int)GetInkPath(annot, i, null).Value;
            var points = new Native.PointF[length];
            GetInkPath(annot, i, points);
            ink.Add(points.Select(p => new Point(p.X, p.Y)).ToArray());
        }
        var quads = new List<Point[]>();
        var quadCount = (int)Native.FPDFAnnot_CountAttachmentPoints(annot);
        for (var i = 0; i < quadCount; i++)
            if (Native.FPDFAnnot_GetAttachmentPoints(annot, (nuint)i, out var q) != 0)
                quads.Add([new(q.X1, q.Y1), new(q.X2, q.Y2), new(q.X3, q.Y3), new(q.X4, q.Y4)]);

        var annotation = new Annotation
        {
            Name = name, Kind = kind, Color = color, LineWidth = lineWidth, Ink = ink.ToArray(), Quads = quads.ToArray(),
            Bounds = new Rect(new Point(r.Left, r.Bottom), new Point(r.Right, r.Top)).Normalize(),
            Contents = GetString(annot, "Contents"),
        };
        if (kind == AnnotationKind.TextBox && ParseAppearance(GetString(annot, "DA")) is var (size, textColor))
            annotation = annotation with { FontSize = size, Color = textColor, StampStyle = GetString(annot, StampKey) == "1" };
        return annotation;
    }

    const string ColorKey = "FolioColor", StampKey = "FolioStamp", ImageKey = "FolioImage";

    static void Write(IntPtr annot, Annotation a, bool isNew, bool keepAppearance = false)
    {
        var b = a.Bounds;
        // Colours cannot be set while there is an appearance: remove it first (PDFium generates it again when drawing).
        // Stamps keep theirs (drawn from their image): it follows the rectangle.
        if (!keepAppearance && a.Kind is not (AnnotationKind.TextBox or AnnotationKind.Stamp)) Native.FPDFAnnot_SetAP(annot, Native.AppearanceNormal, null);
        Native.FPDFAnnot_SetRect(annot, new Native.RectF { Left = (float)b.X, Bottom = (float)b.Y, Right = (float)b.Right, Top = (float)b.Bottom });
        Native.FPDFAnnot_SetStringValue(annot, "NM", a.Name);
        Native.FPDFAnnot_SetStringValue(annot, "Contents", a.Contents);
        Native.FPDFAnnot_SetFlags(annot, Native.FlagPrint);
        if (a.Kind == AnnotationKind.Stamp) return;
        if (a.Kind == AnnotationKind.TextBox)
        {
            if (a.StampStyle) Native.FPDFAnnot_SetStringValue(annot, StampKey, "1");
            Native.FPDFAnnot_SetStringValue(annot, "DA", $"/Helv {F(a.FontSize)} Tf {F(a.Color.R / 255.0)} {F(a.Color.G / 255.0)} {F(a.Color.B / 255.0)} rg");
            Native.FPDFAnnot_SetBorder(annot, 0, 0, 0);
            if (!keepAppearance) Native.FPDFAnnot_SetAP(annot, Native.AppearanceNormal, TextAppearance.Build(b, a.Contents, a.FontSize, a.Color, a.StampStyle));
            return;
        }
        Native.FPDFAnnot_SetColor(annot, Native.ColorStroke, a.Color.R, a.Color.G, a.Color.B, 255);
        Native.FPDFAnnot_SetStringValue(annot, ColorKey, $"#{a.Color.R:x2}{a.Color.G:x2}{a.Color.B:x2}");
        if (a.HasLineWidth) Native.FPDFAnnot_SetBorder(annot, 0, 0, (float)a.LineWidth);
        if (a.Kind == AnnotationKind.Ink)
        {
            Native.FPDFAnnot_RemoveInkList(annot);
            foreach (var stroke in a.Ink) AddInkStroke(annot, stroke);
        }
        if (a.IsMarkup)
        {
            var existing = isNew ? 0 : (int)Native.FPDFAnnot_CountAttachmentPoints(annot);
            for (var i = 0; i < a.Quads.Length; i++)
            {
                var p = a.Quads[i];
                var quad = new Native.QuadF
                {
                    X1 = (float)p[0].X, Y1 = (float)p[0].Y, X2 = (float)p[1].X, Y2 = (float)p[1].Y,
                    X3 = (float)p[2].X, Y3 = (float)p[2].Y, X4 = (float)p[3].X, Y4 = (float)p[3].Y,
                };
                if (i < existing) Native.FPDFAnnot_SetAttachmentPoints(annot, (nuint)i, quad);
                else Native.FPDFAnnot_AppendAttachmentPoints(annot, quad);
            }
        }
    }

    static unsafe CULong GetInkPath(IntPtr annot, int index, Native.PointF[]? points)
    {
        fixed (Native.PointF* buffer = points)
            return Native.FPDFAnnot_GetInkListPath(annot, new CULong((uint)index), buffer, new CULong((uint)(points?.Length ?? 0)));
    }

    static unsafe void AddInkStroke(IntPtr annot, Point[] stroke)
    {
        var points = stroke.Select(p => new Native.PointF { X = (float)p.X, Y = (float)p.Y }).ToArray();
        fixed (Native.PointF* buffer = points) Native.FPDFAnnot_AddInkStroke(annot, buffer, (nuint)points.Length);
    }

    static unsafe string GetString(IntPtr annot, string key)
    {
        var bytes = (int)Native.FPDFAnnot_GetStringValue(annot, key, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new char[bytes / 2];
        fixed (char* pointer = buffer) Native.FPDFAnnot_GetStringValue(annot, key, pointer, new CULong((uint)bytes));
        return new string(buffer, 0, buffer.Length - 1);
    }

    /// Font size and colour of a default appearance string such as "/Helv 12 Tf 1 0 0 rg".
    static (double Size, Color Color)? ParseAppearance(string da)
    {
        static double N(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        var size = Regex.Match(da, @"([\d.]+)\s+Tf");
        if (!size.Success) return null;
        var color = Colors.Black;
        if (Regex.Match(da, @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg") is { Success: true } rgb)
            color = Color.FromRgb((byte)(N(rgb.Groups[1].Value) * 255), (byte)(N(rgb.Groups[2].Value) * 255), (byte)(N(rgb.Groups[3].Value) * 255));
        else if (Regex.Match(da, @"([\d.]+)\s+g\b") is { Success: true } gray)
            color = Color.FromRgb((byte)(N(gray.Groups[1].Value) * 255), (byte)(N(gray.Groups[1].Value) * 255), (byte)(N(gray.Groups[1].Value) * 255));
        return (N(size.Groups[1].Value), color);
    }

    static string PdfDate(DateTime date) => "D:" + date.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    internal static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // MARK: Saving

    /// Writes the document (with its changes) to `path`, through a temporary file so a failure leaves the original intact.
    public void Save(string path, IEnumerable<int> changedPages)
    {
        var temporary = path + ".pdf-svd-tmp";
        try
        {
            using (var stream = File.Create(temporary))
                lock (Pdfium.Lock)
                {
                    if (closed) throw new ObjectDisposedException(nameof(PdfDocument));
                    GenerateAppearances(changedPages);
                    Write(handle, stream);
                }
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        File.Move(temporary, path, overwrite: true);
        Path = path;
    }

    /// The whole document as a PDF file in memory (used to undo changes of the page list).
    public byte[] Snapshot(IEnumerable<int> changedPages)
    {
        lock (Pdfium.Lock)
        {
            if (closed) throw new ObjectDisposedException(nameof(PdfDocument));
            GenerateAppearances(changedPages);
            using var stream = new MemoryStream();
            Write(handle, stream);
            return stream.ToArray();
        }
    }

    /// Annotation appearances are generated when a page is drawn: makes sure each of these pages has been
    /// (before saving or copying pages). Under the lock.
    void GenerateAppearances(IEnumerable<int> pages)
    {
        foreach (var index in pages) WithPage(index, 0, page =>
        {
            var bitmap = Native.FPDFBitmap_CreateEx(1, 1, Native.BitmapBGRA, IntPtr.Zero, 0);
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, 1, 1, 0, Native.RenderAnnotations);
            Native.FPDFBitmap_Destroy(bitmap);
            return 0;
        });
    }

    /// Writes a PDFium document (not incrementally) to a stream. Under the lock.
    internal static void Write(IntPtr document, Stream stream)
    {
        saveStream = stream;
        try
        {
            bool saved;
            unsafe
            {
                var writer = new Native.FileWrite { Version = 1, WriteBlock = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, CULong, int>)&WriteBlock };
                saved = Native.FPDF_SaveAsCopy(document, ref writer, new CULong(Native.SaveNoIncremental)) != 0;
            }
            if (!saved) throw new IOException("FPDF_SaveAsCopy");
        }
        finally { saveStream = null; }
    }

    /// Destination of the save in progress (saving holds the PDFium lock, so there is only one).
    static Stream? saveStream;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static unsafe int WriteBlock(IntPtr writer, IntPtr data, CULong size)
    {
        try
        {
            saveStream!.Write(new ReadOnlySpan<byte>((void*)data, (int)size.Value));
            return 1;
        }
        catch { return 0; }
    }
}
