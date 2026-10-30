using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using Avalonia;

namespace EdPdf.Pdf;

/// PDFium C API (fpdfview.h). PDFium is not thread-safe: every call goes through `Pdfium.Lock`.
internal static partial class Native
{
    const string Lib = "pdfium";

    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern CULong FPDF_GetLastError();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int index, out SizeF size);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, CULong color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    [DllImport(Lib)] public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    [DllImport(Lib)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    /// Windows only: renders into a device context (printing).
    [DllImport(Lib)] public static extern void FPDF_RenderPage(IntPtr dc, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    // fpdf_text.h
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern unsafe int FPDFText_GetText(IntPtr textPage, int start, int count, char* result);
    [DllImport(Lib)] public static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
    [DllImport(Lib)] public static extern int FPDFText_CountRects(IntPtr textPage, int start, int count);
    [DllImport(Lib)] public static extern int FPDFText_GetRect(IntPtr textPage, int index, out double left, out double top, out double right, out double bottom);
    [DllImport(Lib)] public static extern IntPtr FPDFText_FindStart(IntPtr textPage, [MarshalAs(UnmanagedType.LPWStr)] string query, CULong flags, int startIndex);
    [DllImport(Lib)] public static extern int FPDFText_FindNext(IntPtr search);
    [DllImport(Lib)] public static extern int FPDFText_GetSchResultIndex(IntPtr search);
    [DllImport(Lib)] public static extern int FPDFText_GetSchCount(IntPtr search);
    [DllImport(Lib)] public static extern void FPDFText_FindClose(IntPtr search);

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeF { public float Width, Height; }

    public const int BitmapBGRA = 4;
    public const int RenderAnnotations = 0x01;
    public const int RenderPrinting = 0x800;
    public const int ErrorPassword = 4;
}

public static class Pdfium
{
    public static readonly Lock Lock = new();
    static bool initialized;

    internal static void EnsureInitialized()
    {
        lock (Lock)
        {
            if (initialized) return;
            Native.FPDF_InitLibrary();
            initialized = true;
        }
    }
}

public sealed class PdfPasswordException : Exception;

/// A search match: characters [Start, Start + Count) of a page, and their rectangles in points from the page's top left.
public sealed record TextMatch(int Page, int Start, int Count, Rect[] Rects);

/// An open PDF. The file bytes stay in unmanaged memory: PDFium reads them lazily.
public sealed partial class PdfDocument : IDisposable
{
    IntPtr handle;
    bool closed;
    IntPtr buffer;
    /// Password the document was opened with: needed to load it again (snapshots keep its encryption).
    readonly string? password;
    public string Path { get; private set; }
    public int PageCount { get; private set; }

    PdfDocument(string path, string? password, IntPtr handle, IntPtr buffer)
    {
        Path = path;
        this.password = password;
        this.handle = handle;
        this.buffer = buffer;
        lock (Pdfium.Lock) PageCount = Native.FPDF_GetPageCount(handle);
    }

    /// Throws PdfPasswordException when a password is required (or wrong), IOException when the file is not a readable PDF.
    public static PdfDocument Open(string path, string? password = null) =>
        Load(File.ReadAllBytes(path), password, out var handle, out var buffer) is var error && error == 0
            ? new PdfDocument(path, password, handle, buffer)
            : throw (error == Native.ErrorPassword ? new PdfPasswordException() : new IOException($"PDFium error {error}"));

    /// Opens a PDF held in memory (an inserted file, a snapshot). Null if it cannot be read.
    public static PdfDocument? FromBytes(byte[] bytes, string? password = null) =>
        Load(bytes, password, out var handle, out var buffer) == 0 ? new PdfDocument("", password, handle, buffer) : null;

    /// Loads `bytes` into PDFium; the PDFium error, or 0 and the document with its buffer.
    static uint Load(byte[] bytes, string? password, out IntPtr handle, out IntPtr buffer)
    {
        Pdfium.EnsureInitialized();
        buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        lock (Pdfium.Lock)
        {
            handle = Native.FPDF_LoadMemDocument64(buffer, (nuint)bytes.Length, password);
            if (handle != IntPtr.Zero) return 0;
            var error = (uint)Native.FPDF_GetLastError().Value;
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            return error == 0 ? uint.MaxValue : error;
        }
    }

    /// Replaces the contents of the document by a snapshot (undoing or redoing a change of its pages).
    public void Restore(byte[] snapshot)
    {
        if (Load(snapshot, password, out var newHandle, out var newBuffer) != 0) throw new IOException("snapshot");
        lock (Pdfium.Lock)
        {
            CloseTextPage();
            pageToView.Clear();
            Native.FPDF_CloseDocument(handle);
            Marshal.FreeHGlobal(buffer);
            (handle, buffer) = (newHandle, newBuffer);
            PageCount = Native.FPDF_GetPageCount(handle);
        }
    }

    /// After a change of the page list: forgets what was cached about pages.
    void PagesChanged()
    {
        CloseTextPage();
        pageToView.Clear();
        PageCount = Native.FPDF_GetPageCount(handle);
    }

    /// Page size in points (1/72 inch).
    public (double Width, double Height) PageSize(int index)
    {
        lock (Pdfium.Lock)
        {
            if (closed) return (0, 0);
            Native.FPDF_GetPageSizeByIndexF(handle, index, out var size);
            return (size.Width, size.Height);
        }
    }

    /// Renders a page on white into a BGRA buffer (premultiplied, as Avalonia expects).
    public void Render(int index, IntPtr pixels, int width, int height, int stride)
    {
        lock (Pdfium.Lock)
        {
            if (closed) return;
            var page = Native.FPDF_LoadPage(handle, index);
            if (page == IntPtr.Zero) return;
            var bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.BitmapBGRA, pixels, stride);
            Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, new CULong(0xFFFFFFFF));
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Native.RenderAnnotations);
            Native.FPDFBitmap_Destroy(bitmap);
            Native.FPDF_ClosePage(page);
        }
    }

    /// Windows only: prints a page fitted in (x, y, width, height) of a printer device context, rotated a quarter turn if `rotate`.
    public void RenderToDevice(int index, IntPtr dc, int x, int y, int width, int height, bool rotate)
    {
        lock (Pdfium.Lock)
        {
            if (closed) return;
            var page = Native.FPDF_LoadPage(handle, index);
            if (page == IntPtr.Zero) return;
            Native.FPDF_RenderPage(dc, page, x, y, width, height, rotate ? 1 : 0, Native.RenderAnnotations | Native.RenderPrinting);
            Native.FPDF_ClosePage(page);
        }
    }

    // MARK: Text

    /// Device units per point when converting coordinates (PDFium's device coordinates are integers).
    const double Precision = 20;
    /// The last text page used, kept open: selecting with the mouse queries the same page many times.
    int textIndex = -1;
    IntPtr textPage, textPageOwner;

    /// Runs `body(page, textPage)` under the lock; `fallback` if the document is closed or the page unreadable.
    T WithText<T>(int index, T fallback, Func<IntPtr, IntPtr, T> body)
    {
        lock (Pdfium.Lock)
        {
            if (closed || index < 0 || index >= PageCount) return fallback;
            if (index != textIndex)
            {
                CloseTextPage();
                textPageOwner = Native.FPDF_LoadPage(handle, index);
                if (textPageOwner == IntPtr.Zero) return fallback;
                textPage = Native.FPDFText_LoadPage(textPageOwner);
                textIndex = index;
            }
            return textPage == IntPtr.Zero ? fallback : body(textPageOwner, textPage);
        }
    }

    void CloseTextPage()
    {
        if (textPage != IntPtr.Zero) Native.FPDFText_ClosePage(textPage);
        if (textPageOwner != IntPtr.Zero) Native.FPDF_ClosePage(textPageOwner);
        textPage = textPageOwner = IntPtr.Zero;
        textIndex = -1;
    }

    (int Width, int Height) DeviceSize(int index)
    {
        Native.FPDF_GetPageSizeByIndexF(handle, index, out var size);
        return ((int)Math.Round(size.Width * Precision), (int)Math.Round(size.Height * Precision));
    }

    public int CharCount(int index) => WithText(index, 0, (_, text) => Native.FPDFText_CountChars(text));

    /// Index of the character at `point` (points from the page's top left), or -1.
    public int CharIndexAt(int index, Point point, double tolerance) => WithText(index, -1, (page, text) =>
    {
        var (width, height) = DeviceSize(index);
        Native.FPDF_DeviceToPage(page, 0, 0, width, height, 0, (int)Math.Round(point.X * Precision), (int)Math.Round(point.Y * Precision), out var x, out var y);
        return Native.FPDFText_GetCharIndexAtPos(text, x, y, tolerance, tolerance);
    });

    public string Text(int index, int start, int count) => WithText(index, "", (_, text) =>
    {
        var buffer = new char[count + 1];
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                var written = Native.FPDFText_GetText(text, start, count, pointer);
                return new string(buffer, 0, Math.Max(0, written - 1));
            }
        }
    });

    /// The word around character `charIndex`: letters and digits, or the character alone.
    public (int Start, int Count) WordAt(int index, int charIndex) => WithText(index, (charIndex, 0), (_, text) =>
    {
        var total = Native.FPDFText_CountChars(text);
        bool IsWordChar(int i) => i >= 0 && i < total && Native.FPDFText_GetUnicode(text, i) is var c && c <= 0xFFFF && char.IsLetterOrDigit((char)c);
        if (!IsWordChar(charIndex)) return (charIndex, 1);
        int start = charIndex, end = charIndex;
        while (IsWordChar(start - 1)) start--;
        while (IsWordChar(end + 1)) end++;
        return (start, end - start + 1);
    });

    /// Rectangles covering characters [start, start + count), in points from the page's top left.
    public Rect[] TextRects(int index, int start, int count) => WithText(index, Array.Empty<Rect>(), (page, text) => Rects(index, page, text, start, count));

    Rect[] Rects(int index, IntPtr page, IntPtr text, int start, int count)
    {
        var (width, height) = DeviceSize(index);
        var rects = new Rect[Native.FPDFText_CountRects(text, start, count)];
        for (var i = 0; i < rects.Length; i++)
        {
            Native.FPDFText_GetRect(text, i, out var left, out var top, out var right, out var bottom);
            Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, left, top, out var x1, out var y1);
            Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, right, bottom, out var x2, out var y2);
            rects[i] = new Rect(new Point(x1 / Precision, y1 / Precision), new Point(x2 / Precision, y2 / Precision)).Normalize();
        }
        return MergeLines(rects);
    }

    /// PDFium often gives one rectangle per character: joins consecutive ones on the same line (an even highlight).
    static Rect[] MergeLines(Rect[] rects)
    {
        var lines = new List<Rect>();
        foreach (var rect in rects)
        {
            if (lines.Count > 0 && lines[^1] is var last
                && Math.Abs(last.Center.Y - rect.Center.Y) < Math.Max(last.Height, rect.Height) / 2
                && rect.X >= last.X - 1 && rect.X <= last.Right + Math.Max(last.Height, rect.Height))
                lines[^1] = last.Union(rect);
            else
                lines.Add(rect);
        }
        return lines.ToArray();
    }

    /// Case-insensitive search of the whole document, page by page (the lock is released between pages).
    public List<TextMatch> Find(string query, CancellationToken cancel)
    {
        var matches = new List<TextMatch>();
        for (var index = 0; index < PageCount; index++)
        {
            cancel.ThrowIfCancellationRequested();
            WithText(index, 0, (page, text) =>
            {
                var search = Native.FPDFText_FindStart(text, query, new CULong(0), 0);
                if (search == IntPtr.Zero) return 0;
                while (Native.FPDFText_FindNext(search) != 0)
                {
                    int start = Native.FPDFText_GetSchResultIndex(search), count = Native.FPDFText_GetSchCount(search);
                    matches.Add(new TextMatch(index, start, count, Rects(index, page, text, start, count)));
                }
                Native.FPDFText_FindClose(search);
                return 0;
            });
        }
        return matches;
    }

    public void Dispose()
    {
        lock (Pdfium.Lock)
        {
            if (closed) return;
            closed = true;
            CloseTextPage();
            Native.FPDF_CloseDocument(handle);
            Marshal.FreeHGlobal(buffer);
        }
    }
}
