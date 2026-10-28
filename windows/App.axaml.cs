using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PdfSvd;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // PDFSVD_LANG=en forces a language (to check translations on the Mac).
        var language = Environment.GetEnvironmentVariable("PDFSVD_LANG");
        L10n.Load(string.IsNullOrEmpty(language) ? CultureInfo.CurrentUICulture : new CultureInfo(language));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault());
        base.OnFrameworkInitializationCompleted();
    }
}
