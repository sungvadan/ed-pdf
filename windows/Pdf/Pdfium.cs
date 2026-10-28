using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PdfSvd.Pdf;

/// PDFium C API (fpdfview.h). PDFium is not thread-safe: every call goes through `Pdfium.Lock`.
internal static class Native
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

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeF { public float Width, Height; }

    public const int BitmapBGRA = 4;
    public const int RenderAnnotations = 0x01;
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

/// An open PDF. The file bytes stay in unmanaged memory: PDFium reads them lazily.
public sealed class PdfDocument : IDisposable
{
    readonly IntPtr handle;
    bool closed;
    readonly IntPtr buffer;
    public string Path { get; }
    public int PageCount { get; }

    PdfDocument(string path, IntPtr handle, IntPtr buffer)
    {
        Path = path;
        this.handle = handle;
        this.buffer = buffer;
        lock (Pdfium.Lock) PageCount = Native.FPDF_GetPageCount(handle);
    }

    /// Throws PdfPasswordException when a password is required (or wrong), IOException when the file is not a readable PDF.
    public static PdfDocument Open(string path, string? password = null)
    {
        Pdfium.EnsureInitialized();
        var bytes = File.ReadAllBytes(path);
        var buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        lock (Pdfium.Lock)
        {
            var handle = Native.FPDF_LoadMemDocument64(buffer, (nuint)bytes.Length, password);
            if (handle != IntPtr.Zero) return new PdfDocument(path, handle, buffer);
            var error = Native.FPDF_GetLastError().Value;
            Marshal.FreeHGlobal(buffer);
            if (error == Native.ErrorPassword) throw new PdfPasswordException();
            throw new IOException($"PDFium error {error}");
        }
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

    public void Dispose()
    {
        lock (Pdfium.Lock)
        {
            if (closed) return;
            closed = true;
            Native.FPDF_CloseDocument(handle);
            Marshal.FreeHGlobal(buffer);
        }
    }
}
