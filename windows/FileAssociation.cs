using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static EdPdf.L10n;

namespace EdPdf;

/// Registers ed-pdf for .pdf files (current user, no administrator rights): it then appears in "Open with" and in
/// Settings > Default apps. Windows does not let an app make itself the default: the user confirms in Settings.
[SupportedOSPlatform("windows")]
static class FileAssociation
{
    const string ProgId = "ed-pdf.Document";
    const string AppName = "ed-pdf";

    /// Called at each launch: updates the registration when the .exe was moved or renamed.
    public static void Register()
    {
        if (Environment.ProcessPath is not { } exe || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        var command = $"\"{exe}\" \"%1\"";
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        RemovePreviousName(classes);
        using (var existing = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
            if (existing?.GetValue("") as string == command) return;

        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue("", Tr("Document PDF"));
            using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var open = progId.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
        }
        using (var openWith = classes.CreateSubKey(@".pdf\OpenWithProgids")) openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var application = classes.CreateSubKey($@"Applications\{Path.GetFileName(exe)}"))
        {
            application.SetValue("FriendlyAppName", AppName);
            using (var types = application.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
            using (var open = application.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey($@"Software\{AppName}\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", Tr("Lire, annoter et signer des PDF"));
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, $@"Software\{AppName}\Capabilities");
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// The app was called pdf-svd before: removes that registration, which may point to an exe that is gone.
    static void RemovePreviousName(RegistryKey classes)
    {
        const string oldName = "pdf-svd", oldProgId = "pdf-svd.Document";
        using (var old = classes.OpenSubKey(oldProgId)) if (old == null) return;
        classes.DeleteSubKeyTree(oldProgId, false);
        using (var openWith = classes.OpenSubKey(@".pdf\OpenWithProgids", true)) openWith?.DeleteValue(oldProgId, false);
        using (var applications = classes.OpenSubKey("Applications", true))
            if (applications != null)
                foreach (var name in applications.GetSubKeyNames())
                {
                    using var application = applications.OpenSubKey(name);
                    if (application?.GetValue("FriendlyAppName") as string == oldName) applications.DeleteSubKeyTree(name, false);
                }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{oldName}", false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) registered?.DeleteValue(oldName, false);
    }

    /// Opens Settings > Default apps on ed-pdf's page, where the user chooses it for .pdf.
    public static void OpenDefaultAppsSettings()
    {
        Register();
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true });
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
