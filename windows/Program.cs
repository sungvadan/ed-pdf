using System;
using Avalonia;

namespace EdPdf;

static class Program
{
    /// ed-pdf.exe [file.pdf]
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
