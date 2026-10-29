using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PdfSvd.Pdf;

/// fpdf_edit.h, fpdf_ppo.h, fpdf_flatten.h: pages and their objects.
internal static partial class Native
{
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Lib)] public static extern void FPDFPage_Delete(IntPtr document, int index);
    [DllImport(Lib)] public static extern int FPDF_MovePages(IntPtr document, int[] indices, CULong count, int destination);
    [DllImport(Lib)] public static extern int FPDF_ImportPagesByIndex(IntPtr destination, IntPtr source, int[]? indices, CULong count, int index);
    [DllImport(Lib)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern IntPtr FPDFPage_New(IntPtr document, int index, double width, double height);
    [DllImport(Lib)] public static extern int FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);
    [DllImport(Lib)] public static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Lib)] public static extern int FPDFPage_Flatten(IntPtr page, int flags);
    [DllImport(Lib)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);
    [DllImport(Lib)] public static extern int FPDFImageObj_GetImagePixelSize(IntPtr imageObject, out uint width, out uint height);
    [DllImport(Lib)] public static extern CULong FPDFImageObj_GetImageDataRaw(IntPtr imageObject, IntPtr buffer, CULong length);
    [DllImport(Lib)] public static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr imageObject);
    [DllImport(Lib)] public static extern unsafe int FPDFImageObj_LoadJpegFileInline(IntPtr* pages, int count, IntPtr imageObject, ref FileAccess file);
    [DllImport(Lib)] public static extern int FPDFAnnot_UpdateObject(IntPtr annot, IntPtr pageObject);

    [StructLayout(LayoutKind.Sequential)] public struct FileAccess { public CULong Length; public IntPtr GetBlock; public IntPtr Param; }

    public const int FlattenNormalDisplay = 0;
}

public sealed partial class PdfDocument
{
    /// Turns pages by quarter turns (positive: clockwise).
    public void Rotate(IEnumerable<int> indices, int quarterTurns)
    {
        lock (Pdfium.Lock)
        {
            foreach (var index in indices) WithPage(index, 0, page =>
            {
                Native.FPDFPage_SetRotation(page, ((Native.FPDFPage_GetRotation(page) + quarterTurns) % 4 + 4) % 4);
                return 0;
            });
            PagesChanged();
        }
    }

    public void DeletePages(IEnumerable<int> indices)
    {
        lock (Pdfium.Lock)
        {
            foreach (var index in indices.Distinct().OrderDescending()) Native.FPDFPage_Delete(handle, index);
            PagesChanged();
        }
    }

    /// Moves pages (in this order) so that the first one ends up at `destination` in the resulting list.
    public void MovePages(int[] indices, int destination)
    {
        lock (Pdfium.Lock)
        {
            Native.FPDF_MovePages(handle, indices, new CULong((uint)indices.Length), destination);
            PagesChanged();
        }
    }

    /// Inserts copies of pages of this document at `at` (a page cannot be imported from its own document).
    public void DuplicatePages(int[] indices, int at)
    {
        lock (Pdfium.Lock)
        {
            using var stream = new MemoryStream();
            Write(handle, stream);
            using var copy = FromBytes(stream.ToArray(), password) ?? throw new IOException("copy");
            Native.FPDF_ImportPagesByIndex(handle, copy.handle, indices, new CULong((uint)indices.Length), at);
            PagesChanged();
        }
    }

    /// Inserts all the pages of another document at `at`. Returns how many.
    public int InsertPages(PdfDocument source, int at)
    {
        lock (Pdfium.Lock)
        {
            if (Native.FPDF_ImportPagesByIndex(handle, source.handle, null, new CULong(0), at) == 0) return 0;
            PagesChanged();
            return source.PageCount;
        }
    }

    public void InsertBlankPage(int at, double width, double height)
    {
        lock (Pdfium.Lock)
        {
            var page = Native.FPDFPage_New(handle, at, width, height);
            if (page != IntPtr.Zero) Native.FPDF_ClosePage(page);
            PagesChanged();
        }
    }

    /// Inserts a page showing an image (at 96 dpi, reduced to the size of an A4 page if larger).
    public void InsertImagePage(SKBitmap image, int at)
    {
        double scale = 72.0 / 96, longest = Math.Max(image.Width, image.Height) * scale;
        if (longest > 842) scale *= 842 / longest;
        double width = image.Width * scale, height = image.Height * scale;
        lock (Pdfium.Lock)
        {
            var page = Native.FPDFPage_New(handle, at, width, height);
            if (page == IntPtr.Zero) return;
            var imageObject = Native.FPDFPageObj_NewImageObj(handle);
            if (HasTransparency(image)) SetBitmap(imageObject, image);
            else SetJpeg(page, imageObject, image, 90);
            Native.FPDFImageObj_SetMatrix(imageObject, width, 0, 0, height, 0, 0);
            Native.FPDFPage_InsertObject(page, imageObject);
            Native.FPDFPage_GenerateContent(page);
            Native.FPDF_ClosePage(page);
            PagesChanged();
        }
    }

    /// A new PDF file holding copies of these pages, with their annotations.
    public byte[] ExtractPages(int[] indices, IEnumerable<int> changedPages)
    {
        lock (Pdfium.Lock)
        {
            GenerateAppearances(changedPages);
            var output = Native.FPDF_CreateNewDocument();
            try
            {
                if (Native.FPDF_ImportPagesByIndex(output, handle, indices, new CULong((uint)indices.Length), 0) == 0) throw new IOException("import");
                using var stream = new MemoryStream();
                Write(output, stream);
                return stream.ToArray();
            }
            finally { Native.FPDF_CloseDocument(output); }
        }
    }

    /// Draws the annotations and form fields of these pages into their content: they can no longer be edited.
    public void Flatten(IEnumerable<int> indices)
    {
        lock (Pdfium.Lock)
        {
            var list = indices.ToList();
            GenerateAppearances(list);
            foreach (var index in list) WithPage(index, 0, page => Native.FPDFPage_Flatten(page, Native.FlattenNormalDisplay));
            PagesChanged();
        }
    }

    /// A page drawn with its annotations, on white, at `pixelsPerPoint`.
    public SKBitmap RenderBitmap(int index, double pixelsPerPoint)
    {
        var (width, height) = PageSize(index);
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Round(width * pixelsPerPoint)), Math.Max(1, (int)Math.Round(height * pixelsPerPoint)),
            SKColorType.Bgra8888, SKAlphaType.Premul));
        Render(index, bitmap.GetPixels(), bitmap.Width, bitmap.Height, bitmap.RowBytes);
        return bitmap;
    }

    // MARK: Smaller copies

    /// Images shown at more than this resolution are reduced to it.
    const double TargetDpi = 150;

    /// Recompresses the images of the document as JPEG at 150 dpi of their size on the page (text and drawings
    /// are untouched). Images with transparency are left as they are. Works on this document: use a copy.
    public void ReduceImages()
    {
        lock (Pdfium.Lock)
        {
            for (var index = 0; index < PageCount; index++) WithPage(index, 0, page =>
            {
                var reduced = false;
                for (var i = 0; i < Native.FPDFPage_CountObjects(page); i++)
                {
                    var obj = Native.FPDFPage_GetObject(page, i);
                    if (Native.FPDFPageObj_GetType(obj) == Native.PageObjectImage) reduced |= ReduceImage(page, obj);
                }
                // The new image data is only written with the page content generated again.
                if (reduced) Native.FPDFPage_GenerateContent(page);
                return 0;
            });
        }
    }

    /// Whether the image was replaced.
    bool ReduceImage(IntPtr page, IntPtr image)
    {
        if (Native.FPDFImageObj_GetImagePixelSize(image, out var pixelWidth, out var pixelHeight) == 0 || pixelWidth * pixelHeight < 100 * 100) return false;
        Native.FPDFPageObj_GetBounds(image, out var left, out var bottom, out var right, out var top);
        // Pixels wanted for the size shown (the image may be turned: compare the longest sides).
        var shown = Math.Max(right - left, top - bottom) / 72 * TargetDpi;
        var scale = Math.Min(1, shown / Math.Max(pixelWidth, pixelHeight));
        var raw = (long)Native.FPDFImageObj_GetImageDataRaw(image, IntPtr.Zero, new CULong(0)).Value;
        if (IsTransparent(page, image)) return false;
        var bitmap = Native.FPDFImageObj_GetBitmap(image);
        if (bitmap == IntPtr.Zero) return false;
        using var decoded = ToSkia(bitmap);
        Native.FPDFBitmap_Destroy(bitmap);
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(decoded.Width * scale)), Math.Max(1, (int)Math.Round(decoded.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var resized = scale < 1 ? decoded.Resize(info, SKFilterQuality.High) : null;
        var jpeg = (resized ?? decoded).Encode(SKEncodedImageFormat.Jpeg, 70)?.ToArray();
        // Only when clearly smaller (an image already in JPEG at the right size stays as it is).
        if (jpeg == null || jpeg.Length > raw * 0.8) return false;
        LoadJpeg(page, image, jpeg);
        return true;
    }

    /// Whether an image has a mask (or is turned, or cannot be drawn): such images are left as they are.
    bool IsTransparent(IntPtr page, IntPtr image)
    {
        // Drawn at its size on the page, with its mask applied (null when turned or flipped).
        var bitmap = Native.FPDFImageObj_GetRenderedBitmap(handle, page, image);
        if (bitmap == IntPtr.Zero) return true;
        using var rendered = ToSkia(bitmap);
        Native.FPDFBitmap_Destroy(bitmap);
        return HasTransparency(rendered);
    }

    static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque) return false;
        foreach (var pixel in bitmap.Pixels) if (pixel.Alpha < 250) return true;
        return false;
    }

    void SetBitmap(IntPtr imageObject, SKBitmap image)
    {
        using var bgra = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        image.CopyTo(bgra, SKColorType.Bgra8888);
        var bitmap = Native.FPDFBitmap_CreateEx(bgra.Width, bgra.Height, Native.BitmapBGRA, bgra.GetPixels(), bgra.RowBytes);
        Native.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, imageObject, bitmap);
        Native.FPDFBitmap_Destroy(bitmap);
    }

    void SetJpeg(IntPtr page, IntPtr imageObject, SKBitmap image, int quality)
    {
        if (image.Encode(SKEncodedImageFormat.Jpeg, quality)?.ToArray() is { } jpeg) LoadJpeg(page, imageObject, jpeg);
        else SetBitmap(imageObject, image);
    }

    /// Replaces the data of an image object by a JPEG file (stored as is, DCT-encoded).
    static unsafe void LoadJpeg(IntPtr page, IntPtr imageObject, byte[] jpeg)
    {
        var pinned = GCHandle.Alloc(jpeg, GCHandleType.Pinned);
        try
        {
            var access = new Native.FileAccess
            {
                Length = new CULong((uint)jpeg.Length),
                GetBlock = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, CULong, byte*, CULong, int>)&ReadBlock,
                Param = GCHandle.ToIntPtr(pinned),
            };
            var pages = stackalloc IntPtr[1];
            pages[0] = page;
            Native.FPDFImageObj_LoadJpegFileInline(pages, 1, imageObject, ref access);
        }
        finally { pinned.Free(); }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static unsafe int ReadBlock(IntPtr param, CULong position, byte* buffer, CULong size)
    {
        var data = (byte[])GCHandle.FromIntPtr(param).Target!;
        var (start, count) = ((long)position.Value, (long)size.Value);
        if (start + count > data.Length) return 0;
        new ReadOnlySpan<byte>(data, (int)start, (int)count).CopyTo(new Span<byte>(buffer, (int)count));
        return 1;
    }

    /// Prepares a copy to send to someone else: without the author's name, the images of signatures and stamps
    /// limited to `imageMaxPixels` and without the copy pdf-svd keeps to edit them again, annotations drawn into
    /// the pages if `flatten`. Works on this document: use a copy.
    public void PrepareForSending(bool flatten, bool removeAuthor, int? imageMaxPixels, bool stripEditingData)
    {
        lock (Pdfium.Lock)
        {
            for (var index = 0; index < PageCount; index++) WithPage(index, 0, page =>
            {
                for (var i = 0; i < Native.FPDFPage_GetAnnotCount(page); i++)
                {
                    var annot = Native.FPDFPage_GetAnnot(page, i);
                    if (annot == IntPtr.Zero) continue;
                    if (removeAuthor && GetString(annot, "T").Length > 0) Native.FPDFAnnot_SetStringValue(annot, "T", "");
                    if (imageMaxPixels is { } max && Native.FPDFAnnot_GetSubtype(annot) == (int)AnnotationKind.Stamp) ReduceStampImage(annot, max);
                    if (stripEditingData && GetString(annot, ImageKey).Length > 0) Native.FPDFAnnot_SetStringValue(annot, ImageKey, "");
                    Native.FPDFPage_CloseAnnot(annot);
                }
                return 0;
            });
            if (flatten) Flatten(Enumerable.Range(0, PageCount));
        }
    }

    /// Signatures and images placed by pdf-svd: drawn again from their PNG, reduced (other stamps are left as they are).
    void ReduceStampImage(IntPtr annot, int maxPixels)
    {
        if (GetString(annot, ImageKey) is not { Length: > 0 } encoded) return;
        SKBitmap? image;
        try { image = SKBitmap.Decode(Convert.FromBase64String(encoded)); } catch (FormatException) { return; }
        using var _ = image;
        if (image == null || Math.Max(image.Width, image.Height) <= maxPixels) return;
        var scale = (double)maxPixels / Math.Max(image.Width, image.Height);
        using var resized = image.Resize(new SKImageInfo(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))), SKFilterQuality.High);
        if (resized == null) return;
        for (var i = 0; i < Native.FPDFAnnot_GetObjectCount(annot); i++)
        {
            var obj = Native.FPDFAnnot_GetObject(annot, i);
            if (Native.FPDFPageObj_GetType(obj) != Native.PageObjectImage) continue;
            SetBitmap(obj, resized);
            Native.FPDFAnnot_UpdateObject(annot, obj);
            break;
        }
        if (resized.Encode(SKEncodedImageFormat.Png, 100)?.ToArray() is { } png) Native.FPDFAnnot_SetStringValue(annot, ImageKey, Convert.ToBase64String(png));
    }

    public string? Password => password;

    /// An independent copy, to make an export from (with the appearances of `changedPages` generated first).
    public PdfDocument Copy(IEnumerable<int> changedPages) => FromBytes(Snapshot(changedPages), password) ?? throw new IOException("copy");

    /// The document as a PDF file in memory, as it is now (for copies made from a snapshot).
    public byte[] ToBytes()
    {
        lock (Pdfium.Lock)
        {
            using var stream = new MemoryStream();
            Write(handle, stream);
            return stream.ToArray();
        }
    }
}
