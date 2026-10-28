using System;
using Avalonia;

namespace PdfSvd;

static class Program
{
    /// pdf-svd.exe [file.pdf]
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
