using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PdfSvd.Pdf;
using static PdfSvd.L10n;
using Path = System.IO.Path;

namespace PdfSvd;

/// Signatures, stamps and images placed on the page.
public partial class MainWindow
{
    const double SignatureWidth = 180, ImageMaxSide = 200, StampFontSize = 20;

    /// Preset stamps: text and colour (the date stamp is built when placed).
    static (string Text, Color Color)[] PresetStamps =>
    [
        (Tr("APPROUVÉ"), Color.Parse("#34C759")),
        (Tr("REJETÉ"), Color.Parse("#E5322D")),
        (Tr("CONFIDENTIEL"), Color.Parse("#E5322D")),
        (Tr("BROUILLON"), Color.Parse("#FF9500")),
        (Tr("PAYÉ"), Color.Parse("#34C759")),
    ];

    void SetUpInsertMenus()
    {
        SignButton.Content = Tr("Signer");
        ToolTip.SetTip(SignButton, Tr("Signature"));
        StampButton.Content = Tr("Tampon");
        ToolTip.SetTip(StampButton, Tr("Tampon"));
        ImageButton.Content = Tr("Image");
        ToolTip.SetTip(ImageButton, Tr("Placer une image"));
        // Menus rebuilt before each opening: items added during Opening are not shown.
        SignButton.Click += (_, _) => ShowMenu(SignButton, BuildSignMenu);
        StampButton.Click += (_, _) => ShowMenu(StampButton, BuildStampMenu);
        ImageButton.Click += async (_, _) => await ChooseImageToPlace();
    }

    static void ShowMenu(Control target, Action<MenuFlyout> build)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        build(menu);
        menu.ShowAt(target);
    }

    static MenuItem Item(object header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void BuildSignMenu(MenuFlyout menu)
    {
        foreach (var signature in Library.Signatures) menu.Items.Add(Item(SignaturePreview(signature), () => PlaceSignature(signature)));
        if (Library.Signatures.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item(Tr("Nouvelle signature…"), async () =>
        {
            if (await SignatureDialogs.DrawSignature(this) is not { } signature) return;
            Library.Signatures.Add(signature);
            Library.SaveSignatures();
            PlaceSignature(signature);
        }));
        menu.Items.Add(Item(Tr("Depuis une image (signature sur papier)…"), async () => await SignatureFromImage()));
        if (Library.Signatures.Count == 0) return;
        var remove = new MenuItem { Header = Tr("Supprimer une signature") };
        foreach (var signature in Library.Signatures)
            remove.Items.Add(Item(SignaturePreview(signature), () => { Library.Signatures.Remove(signature); Library.SaveSignatures(); }));
        menu.Items.Add(remove);
    }

    void BuildStampMenu(MenuFlyout menu)
    {
        var culture = CultureInfo.CurrentCulture;
        foreach (var (text, color) in PresetStamps)
            menu.Items.Add(Item(culture.TextInfo.ToTitleCase(text.ToLower(culture)), () => PlaceStamp(text, color)));
        var today = DateTime.Now.ToString("d", culture);
        menu.Items.Add(Item(Tr("Date du jour (%@)", today), () => PlaceStamp(today, Color.Parse("#1E5AE6"))));
        if (Library.Stamps.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var stamp in Library.Stamps) menu.Items.Add(Item(stamp.Text, () => PlaceStamp(stamp.Text, Color.Parse(stamp.Color))));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Tr("Nouveau tampon…"), async () =>
        {
            if (await SignatureDialogs.CreateStamp(this, Palette) is not { } stamp) return;
            Library.Stamps.Add(stamp);
            Library.SaveStamps();
            PlaceStamp(stamp.Text, Color.Parse(stamp.Color));
        }));
        if (Library.Stamps.Count == 0) return;
        var remove = new MenuItem { Header = Tr("Supprimer un tampon") };
        foreach (var stamp in Library.Stamps)
            remove.Items.Add(Item(stamp.Text, () => { Library.Stamps.Remove(stamp); Library.SaveStamps(); }));
        menu.Items.Add(remove);
    }

    /// Small picture of a signature, for the menus.
    static Control SignaturePreview(SavedSignature signature, double height = 24)
    {
        if (signature.ImagePng is { } png)
            return new Image { Source = new Bitmap(new MemoryStream(png)), Height = height, MaxWidth = 160 };
        var size = signature.Size;
        var scale = Math.Min(height / Math.Max(size.Height, 1), 150 / Math.Max(size.Width, 1));
        var canvas = new Canvas { Width = size.Width * scale + 4, Height = size.Height * scale + 4 };
        foreach (var stroke in signature.Points)
        {
            var line = new Polyline { Points = stroke.Select(p => new Point(p.X * scale + 2, p.Y * scale + 2)).ToList(), StrokeThickness = 1.2 };
            line.Bind(Shape.StrokeProperty, line.GetResourceObservable("SystemControlForegroundBaseHighBrush"));
            canvas.Children.Add(line);
        }
        return canvas;
    }

    // MARK: Placing

    /// Centre of the visible part of the current page, in view points.
    (int Page, Point Center)? VisibleCenter()
    {
        if (document == null || pages.Count == 0) return null;
        var page = currentPage;
        var view = (Pages.ContainerFromIndex(page) as Visual)?.GetVisualDescendants().OfType<PageView>().FirstOrDefault();
        var item = pages[page];
        var center = view?.DataContext is PageItem && Scroller.TranslatePoint(new Point(Scroller.Bounds.Width / 2, Scroller.Bounds.Height / 2), view) is { } p
            ? p / item.Scale : new Point(item.WidthPoints / 2, item.HeightPoints / 2);
        return (page, new Point(Math.Clamp(center.X, 0, item.WidthPoints), Math.Clamp(center.Y, 0, item.HeightPoints)));
    }

    /// Adds the annotation and selects it, so it can be moved at once.
    void Place(int page, Annotation annotation)
    {
        EndTextEditing(commit: true);
        AddAnnotation(page, annotation);
        SelectTool(Tool.Select);
        SelectAnnotation(page, document!.Annotations(page).FirstOrDefault(a => a.Name == annotation.Name));
    }

    void PlaceSignature(SavedSignature signature)
    {
        if (VisibleCenter() is not var (page, center)) return;
        var size = signature.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var scale = SignatureWidth / size.Width;
        var rect = new Rect(center.X - SignatureWidth / 2, center.Y - size.Height * scale / 2, SignatureWidth, size.Height * scale);
        if (signature.ImagePng is { } png)
        {
            Place(page, new Annotation { Name = NewName(), Kind = AnnotationKind.Stamp, Bounds = ToPage(page, rect), ImagePng = png });
            return;
        }
        const double width = 1.5;
        var ink = signature.Points.Select(stroke => stroke.Select(p => ToPage(page, new Point(rect.X + p.X * scale, rect.Y + p.Y * scale))).ToArray()).ToArray();
        Place(page, new Annotation
        {
            Name = NewName(), Kind = AnnotationKind.Ink, Color = Colors.Black, LineWidth = width, Ink = ink, Bounds = Annotation.InkBounds(ink, width),
        });
    }

    void PlaceStamp(string text, Color color)
    {
        if (VisibleCenter() is not var (page, center)) return;
        var width = TextAppearance.NaturalWidth(text, StampFontSize, stamp: true) + 20;
        var height = TextAppearance.Height(text, StampFontSize, width, stamp: true);
        Place(page, new Annotation
        {
            Name = NewName(), Kind = AnnotationKind.TextBox, StampStyle = true, Contents = text, Color = color, FontSize = StampFontSize,
            Bounds = ToPage(page, new Rect(center.X - width / 2, center.Y - height / 2, width, height)),
        });
    }

    void PlaceImage(byte[] png)
    {
        if (VisibleCenter() is not var (page, center) || SkiaSharp.SKBitmap.DecodeBounds(png) is not { Width: > 0, Height: > 0 } info) return;
        var scale = ImageMaxSide / Math.Max(info.Width, info.Height);
        double width = info.Width * scale, height = info.Height * scale;
        Place(page, new Annotation
        {
            Name = NewName(), Kind = AnnotationKind.Stamp, ImagePng = png,
            Bounds = ToPage(page, new Rect(center.X - width / 2, center.Y - height / 2, width, height)),
        });
    }

    async Task<string?> PickImage(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(Tr("Images")) { Patterns = ImageDecoder.Patterns }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// File name, plus how to read HEIC photos when Windows lacks the codec.
    static string UnreadableDetail(string path) => ImageDecoder.IsHeif(path)
        ? Path.GetFileName(path) + "\n\n" + Tr("Pour lire les photos HEIC, installez « Extensions d'image HEIF » et « Extensions vidéo HEVC » depuis le Microsoft Store.")
        : Path.GetFileName(path);

    async Task ChooseImageToPlace()
    {
        if (document == null || await PickImage(Tr("Choisissez une image à placer sur la page")) is not { } path) return;
        if (await Task.Run(() => SignatureDialogs.LoadImage(path)) is { } png) PlaceImage(png);
        else await Dialogs.ShowMessage(this, Tr("Impossible de lire cette image."), UnreadableDetail(path));
    }

    async Task SignatureFromImage()
    {
        if (await PickImage(Tr("Choisissez la photo ou le scan d'une signature")) is not { } path) return;
        if (await Task.Run(() => SignatureDialogs.ExtractSignature(path)) is not { } png)
        {
            await Dialogs.ShowMessage(this, Tr("Aucune signature trouvée dans cette image."), UnreadableDetail(path));
            return;
        }
        var signature = new SavedSignature { Png = Convert.ToBase64String(png) };
        Library.Signatures.Add(signature);
        Library.SaveSignatures();
        PlaceSignature(signature);
    }
}
