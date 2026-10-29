using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using static PdfSvd.L10n;

namespace PdfSvd;

/// Small modal dialogs (Avalonia has no message box).
static class Dialogs
{
    static Window Make(string title, Control content, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.AddRange(buttons);
        var panel = new StackPanel { Margin = new(20), Spacing = 14 };
        panel.Children.Add(content);
        panel.Children.Add(row);
        return new Window
        {
            Title = title, Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    public static Task ShowMessage(Window owner, string message, string detail)
    {
        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock { Text = message, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var window = Make("pdf-svd", text, ok);
        ok.Click += (_, _) => window.Close();
        return window.ShowDialog(owner);
    }

    /// The password typed, or null if cancelled. `wrong`: the previous attempt was refused.
    public static Task<string?> AskPassword(Window owner, bool wrong)
    {
        var field = new TextBox { PasswordChar = '•', Watermark = Tr("Mot de passe") };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = Tr("Ce document est protégé par un mot de passe."), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(field);
        if (wrong) content.Children.Add(new TextBlock { Text = Tr("Mot de passe incorrect."), Foreground = Avalonia.Media.Brushes.Red });
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var unlock = new Button { Content = Tr("Déverrouiller"), IsDefault = true, Classes = { "accent" } };
        var window = Make("pdf-svd", content, cancel, unlock);
        cancel.Click += (_, _) => window.Close(null);
        unlock.Click += (_, _) => window.Close(field.Text ?? "");
        window.Opened += (_, _) => field.Focus(NavigationMethod.Tab);
        return window.ShowDialog<string?>(owner);
    }

    public enum SaveChoice { Save, Discard, Cancel }

    /// Before closing or replacing a modified document.
    public static async Task<SaveChoice> AskSave(Window owner, string fileName)
    {
        var text = new TextBlock
        {
            Text = Tr("Enregistrer les modifications apportées à « %@ » ?", fileName),
            FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var discard = new Button { Content = Tr("Ne pas enregistrer") };
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var save = new Button { Content = Tr("Enregistrer"), IsDefault = true, Classes = { "accent" } };
        var window = Make("pdf-svd", text, discard, cancel, save);
        discard.Click += (_, _) => window.Close(SaveChoice.Discard);
        cancel.Click += (_, _) => window.Close(SaveChoice.Cancel);
        save.Click += (_, _) => window.Close(SaveChoice.Save);
        return await window.ShowDialog<SaveChoice?>(owner) ?? SaveChoice.Cancel;
    }

    /// Text of a note: the text typed, or null if cancelled.
    public static Task<string?> AskText(Window owner, string title, string text)
    {
        var field = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 140 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(field);
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var ok = new Button { Content = "OK", Classes = { "accent" } };
        var window = Make("pdf-svd", content, cancel, ok);
        cancel.Click += (_, _) => window.Close(null);
        ok.Click += (_, _) => window.Close(field.Text ?? "");
        window.Opened += (_, _) => { field.Focus(NavigationMethod.Tab); field.CaretIndex = field.Text?.Length ?? 0; };
        return window.ShowDialog<string?>(owner);
    }
}
