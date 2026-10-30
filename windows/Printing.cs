using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using EdPdf.Pdf;

namespace EdPdf;

/// Printing through the Windows print dialog and GDI: PDFium draws each page into the printer's device context.
[SupportedOSPlatform("windows")]
static class Printing
{
    /// Shows the print dialog and prints in the background. False if cancelled; throws if printing failed.
    public static async Task<bool> Print(IntPtr owner, PdfDocument document)
    {
        var maxPage = (ushort)Math.Min(document.PageCount, ushort.MaxValue);
        var dialog = new PRINTDLGW
        {
            lStructSize = Marshal.SizeOf<PRINTDLGW>(),
            hwndOwner = owner,
            Flags = PD_RETURNDC | PD_NOSELECTION | PD_USEDEVMODECOPIESANDCOLLATE,
            nFromPage = 1, nToPage = maxPage, nMinPage = 1, nMaxPage = maxPage, nCopies = 1,
        };
        var accepted = PrintDlgW(ref dialog) != 0;
        if (dialog.hDevMode != IntPtr.Zero) GlobalFree(dialog.hDevMode);
        if (dialog.hDevNames != IntPtr.Zero) GlobalFree(dialog.hDevNames);
        if (!accepted || dialog.hDC == IntPtr.Zero) return false;

        int first = 0, last = document.PageCount - 1;
        if ((dialog.Flags & PD_PAGENUMS) != 0)
        {
            first = Math.Clamp(dialog.nFromPage - 1, 0, last);
            last = Math.Clamp(dialog.nToPage - 1, first, last);
        }
        var dc = dialog.hDC;
        await Task.Run(() =>
        {
            try
            {
                var info = new DOCINFOW { cbSize = Marshal.SizeOf<DOCINFOW>(), lpszDocName = Path.GetFileName(document.Path) };
                if (StartDocW(dc, ref info) <= 0) throw new IOException("StartDoc");
                for (var index = first; index <= last; index++)
                {
                    if (StartPage(dc) <= 0) { AbortDoc(dc); throw new IOException("StartPage"); }
                    PrintPage(document, index, dc);
                    if (EndPage(dc) <= 0) { AbortDoc(dc); throw new IOException("EndPage"); }
                }
                EndDoc(dc);
            }
            finally { DeleteDC(dc); }
        });
        return true;
    }

    /// Fits the page in the printable area, centred, turned a quarter if its orientation differs from the paper's.
    static void PrintPage(PdfDocument document, int index, IntPtr dc)
    {
        int areaWidth = GetDeviceCaps(dc, HORZRES), areaHeight = GetDeviceCaps(dc, VERTRES);
        double dpiX = GetDeviceCaps(dc, LOGPIXELSX), dpiY = GetDeviceCaps(dc, LOGPIXELSY);
        var (width, height) = document.PageSize(index);
        var rotate = width > height != areaWidth > areaHeight;
        if (rotate) (width, height) = (height, width);
        double pageWidth = width / 72 * dpiX, pageHeight = height / 72 * dpiY;
        var fit = Math.Min(areaWidth / pageWidth, areaHeight / pageHeight);
        int drawWidth = (int)(pageWidth * fit), drawHeight = (int)(pageHeight * fit);
        document.RenderToDevice(index, dc, (areaWidth - drawWidth) / 2, (areaHeight - drawHeight) / 2, drawWidth, drawHeight, rotate);
    }

    // MARK: Win32

    const int PD_RETURNDC = 0x100, PD_NOSELECTION = 0x4, PD_PAGENUMS = 0x2, PD_USEDEVMODECOPIESANDCOLLATE = 0x40000;
    const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90;

    [StructLayout(LayoutKind.Sequential)]
    struct PRINTDLGW
    {
        public int lStructSize;
        public IntPtr hwndOwner, hDevMode, hDevNames, hDC;
        public int Flags;
        public ushort nFromPage, nToPage, nMinPage, nMaxPage, nCopies;
        public IntPtr hInstance, lCustData, lpfnPrintHook, lpfnSetupHook, lpPrintTemplateName, lpSetupTemplateName, hPrintTemplate, hSetupTemplate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DOCINFOW
    {
        public int cbSize;
        public string lpszDocName;
        public string? lpszOutput, lpszDatatype;
        public int fwType;
    }

    [DllImport("comdlg32.dll")] static extern int PrintDlgW(ref PRINTDLGW dialog);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int StartDocW(IntPtr dc, ref DOCINFOW info);
    [DllImport("gdi32.dll")] static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
}
