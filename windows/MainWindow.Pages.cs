using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EdPdf.Pdf;
using SkiaSharp;
using static EdPdf.L10n;
using Path = System.IO.Path;

namespace EdPdf;

/// Pages (rotate, delete, reorder, insert, extract, flatten), exports and smaller copies.
public partial class MainWindow
{
    /// Signatures and images in a copy to send are limited to this size (as on the Mac).
    const int SendImageMaxPixels = 600;

    /// Items of the Pages menu and of the thumbnails' context menu (enabled with a document).
    readonly List<MenuItem> pageItems = [];

    // Reordering by dragging thumbnails: thumbnail pressed, where, whether the drag has started, insertion index.
    int thumbnailPressed = -1;
    Point thumbnailPressPoint;
    bool draggingThumbnails;
    int dropIndex = -1;

    void SetUpPages()
    {
        PagesMenu.Header = Tr("Pages");
        foreach (var item in PageMenuItems(menu: true)) PagesMenu.Items.Add(item);
        Thumbnails.ContextMenu = new ContextMenu();
        foreach (var item in PageMenuItems(menu: false)) Thumbnails.ContextMenu.Items.Add(item);
        Item(SendExportItem, Tr("Exporter pour envoi…"), new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift), async () => await ExportForSending());
        Item(ImageExportItem, Tr("Exporter en images…"), null, async () => await ExportImages());
        Item(ReduceSizeItem, Tr("Réduire la taille…"), null, async () => await ReduceSize());

        RotateLeftButton.Content = RotateIcon(clockwise: false);
        RotateRightButton.Content = RotateIcon(clockwise: true);
        ToolTip.SetTip(RotateLeftButton, Tr("Pivoter à gauche") + " (Ctrl+L)");
        ToolTip.SetTip(RotateRightButton, Tr("Pivoter à droite") + " (Ctrl+R)");
        RotateLeftButton.Click += (_, _) => RotatePages(-1);
        RotateRightButton.Click += (_, _) => RotatePages(+1);

        Thumbnails.AddHandler(PointerPressedEvent, ThumbnailPressed, RoutingStrategies.Tunnel);
        Thumbnails.AddHandler(PointerMovedEvent, ThumbnailDragged, RoutingStrategies.Tunnel);
        Thumbnails.AddHandler(PointerReleasedEvent, ThumbnailReleased, RoutingStrategies.Tunnel);
        Thumbnails.AddHandler(PointerCaptureLostEvent, (_, _) => EndThumbnailDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// A circular arrow: three quarters of a circle, the head at the top.
    static Control RotateIcon(bool clockwise)
    {
        var data = clockwise ? "M9,3 A6,6 0 1 0 15,9 M6.5,0.5 L9,3 L6.5,5.5" : "M9,3 A6,6 0 1 1 3,9 M11.5,0.5 L9,3 L11.5,5.5";
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data), Width = 18, Height = 18, StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, icon.GetResourceObservable("SystemControlForegroundBaseHighBrush"));
        return icon;
    }

    List<Control> PageMenuItems(bool menu)
    {
        MenuItem Page(string title, KeyGesture? gesture, Action action)
        {
            var item = new MenuItem();
            Item(item, title, menu ? gesture : null, action);
            pageItems.Add(item);
            return item;
        }
        List<Control> items =
        [
            Page(Tr("Pivoter à gauche"), new KeyGesture(Key.L, KeyModifiers.Control), () => RotatePages(-1)),
            Page(Tr("Pivoter à droite"), new KeyGesture(Key.R, KeyModifiers.Control), () => RotatePages(+1)),
            new Separator(),
            Page(Tr("Dupliquer"), new KeyGesture(Key.D, KeyModifiers.Control), DuplicatePages),
            Page(Tr("Insérer une page vierge"), null, InsertBlankPage),
            Page(Tr("Insérer depuis un fichier…"), null, async () => await ChooseFilesToInsert()),
            Page(Tr("Extraire…"), null, async () => await ExtractPages()),
        ];
        if (menu)
        {
            items.Add(new Separator());
            items.Add(Page(Tr("Aplatir les annotations des pages sélectionnées"), null, () => FlattenPages(TargetPages())));
            items.Add(Page(Tr("Aplatir tout le document"), null, () => FlattenPages(Enumerable.Range(0, pages.Count).ToArray())));
        }
        else
        {
            items.Add(Page(Tr("Exporter en images…"), null, async () => await ExportImages()));
            items.Add(Page(Tr("Aplatir les annotations"), null, () => FlattenPages(TargetPages())));
        }
        items.Add(new Separator());
        items.Add(Page(Tr("Supprimer"), null, DeletePages));
        return items;
    }

    /// Selected pages in document order, or the current page when none is selected.
    int[] TargetPages()
    {
        var selection = Thumbnails.Selection.SelectedIndexes.Where(i => i >= 0 && i < pages.Count).Order().ToArray();
        return selection.Length > 0 ? selection : pages.Count > 0 ? [currentPage] : [];
    }

    /// Just after the last targeted page.
    int InsertionIndex => TargetPages() is { Length: > 0 } targets ? targets[^1] + 1 : pages.Count;

    // MARK: Changes (undoable as a whole: the document before and after is kept)

    /// Changes the page list. `change` returns the page to show and the pages to select afterwards, or null if
    /// it changed nothing.
    bool ChangePages(Func<PdfDocument, (int Show, int[] Select)?> change)
    {
        if (document is not { } doc) return false;
        EndTextEditing(commit: true);
        var (shownBefore, selectedBefore) = (currentPage, TargetPages());
        byte[] before;
        (int Show, int[] Select)? result;
        try
        {
            before = doc.Snapshot(changedPages);
            result = change(doc);
        }
        catch (Exception)
        {
            _ = Dialogs.ShowMessage(this, Tr("Impossible de modifier les pages."), Path.GetFileName(doc.Path));
            return false;
        }
        if (result is not var (show, select)) return false;
        var after = doc.ToBytes();
        changedPages.Clear();
        modified = true;
        PagesRebuilt(show, select);
        undo.Push(() => { doc.Restore(before); AfterRestore(shownBefore, selectedBefore); },
                  () => { doc.Restore(after); AfterRestore(show, select); });
        return true;
    }

    void AfterRestore(int show, int[] select)
    {
        changedPages.Clear();
        modified = true;
        PagesRebuilt(show, select);
    }

    /// After the page list changed: new page items, then shows `show` and selects `select`.
    void PagesRebuilt(int show, int[] select)
    {
        documentVersion++;
        selected = null;
        selectedPage = -1;
        textSelection = [];
        BuildPageItems();
        Scroller.UpdateLayout();
        GoTo(show);
        updatingSelection = true;
        Thumbnails.Selection.Clear();
        foreach (var index in select.Where(i => i < thumbnails.Count)) Thumbnails.Selection.Select(index);
        if (select.Length == 0 && pages.Count > 0) Thumbnails.SelectedIndex = currentPage;
        updatingSelection = false;
        ViewportChanged();
        matches = [];
        matchIndex = -1;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _ = Search(delay: false);
        else UpdateMatchInfo();
        UpdateEditingState();
    }

    static int[] Range(int start, int count) => Enumerable.Range(start, count).ToArray();

    void RotatePages(int quarterTurns)
    {
        var targets = TargetPages();
        if (targets.Length > 0) ChangePages(doc => { doc.Rotate(targets, quarterTurns); return (currentPage, targets); });
    }

    void DeletePages()
    {
        var targets = TargetPages();
        // A document keeps at least one page.
        if (targets.Length == 0 || targets.Length >= pages.Count) return;
        ChangePages(doc =>
        {
            doc.DeletePages(targets);
            return (Math.Min(targets[0], doc.PageCount - 1), []);
        });
    }

    void DuplicatePages()
    {
        var targets = TargetPages();
        if (targets.Length == 0) return;
        var at = targets[^1] + 1;
        ChangePages(doc => { doc.DuplicatePages(targets, at); return (at, Range(at, targets.Length)); });
    }

    void InsertBlankPage()
    {
        if (document == null) return;
        var (width, height) = document.PageSize(currentPage);
        var at = InsertionIndex;
        ChangePages(doc => { doc.InsertBlankPage(at, width, height); return (at, [at]); });
    }

    /// Moves the targeted pages (or the dragged one) so that they are inserted before page `insertion`.
    void MovePages(int[] moved, int insertion)
    {
        var destination = insertion - moved.Count(i => i < insertion);
        var unchanged = moved.Select((page, k) => page == destination + k).All(same => same);
        if (moved.Length == 0 || unchanged) return;
        ChangePages(doc => { doc.MovePages(moved, destination); return (destination, Range(destination, moved.Length)); });
    }

    void FlattenPages(int[] targets)
    {
        if (targets.Length > 0) ChangePages(doc => { doc.Flatten(targets); return (currentPage, targets); });
    }

    // MARK: Inserting files

    static bool IsInsertable(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || ImageDecoder.Patterns.Any(p => path.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase));

    async Task ChooseFilesToInsert()
    {
        if (document == null) return;
        var at = InsertionIndex;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr("Choisissez des PDF ou des images à insérer après la page courante"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Tr("PDF et images")) { Patterns = ["*.pdf", .. ImageDecoder.Patterns] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await InsertFiles(paths, at);
    }

    /// Inserts the pages of PDF files and pages made of images at `at`.
    async Task InsertFiles(IReadOnlyList<string> paths, int at)
    {
        // Files are read off the UI thread, then inserted.
        var sources = await Task.Run(() => paths.Select(path => (path, source: ReadInsertable(path))).ToList());
        try
        {
            var count = 0;
            ChangePages(doc =>
            {
                foreach (var (_, source) in sources)
                {
                    if (source is PdfDocument pdf) count += doc.InsertPages(pdf, at + count);
                    else if (source is SKBitmap image) { doc.InsertImagePage(image, at + count); count++; }
                }
                return count == 0 ? null : (at, Range(at, count));
            });
            var unreadable = sources.Where(s => s.source == null).Select(s => Path.GetFileName(s.path)).ToList();
            if (unreadable.Count > 0) await Dialogs.ShowMessage(this, Tr("Impossible d'insérer ces fichiers."), string.Join("\n", unreadable));
        }
        finally
        {
            foreach (var (_, source) in sources) (source as IDisposable)?.Dispose();
        }
    }

    /// A PDF (not protected by a password) or an image; null if the file cannot be read.
    static object? ReadInsertable(string path)
    {
        try
        {
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return PdfDocument.Open(path);
            return ImageDecoder.Decode(path);
        }
        catch (Exception) { return null; }
    }

    // MARK: Dragging thumbnails

    int ThumbnailIndexAt(PointerEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item ? Thumbnails.IndexFromContainer(item) : -1;

    void ThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Thumbnails).Properties.IsLeftButtonPressed) return;
        thumbnailPressed = ThumbnailIndexAt(e);
        thumbnailPressPoint = e.GetPosition(Thumbnails);
    }

    void ThumbnailDragged(object? sender, PointerEventArgs e)
    {
        if (thumbnailPressed < 0) return;
        var point = e.GetPosition(Thumbnails);
        if (!draggingThumbnails)
        {
            if (!e.GetCurrentPoint(Thumbnails).Properties.IsLeftButtonPressed) { thumbnailPressed = -1; return; }
            if (Math.Abs(point.Y - thumbnailPressPoint.Y) < 8) return;
            draggingThumbnails = true;
            e.Pointer.Capture(Thumbnails);
        }
        // Scrolls when the pointer is near the top or bottom edge.
        if (Thumbnails.Scroll is ScrollViewer scroller)
        {
            if (point.Y < 30) scroller.Offset = scroller.Offset.WithY(Math.Max(0, scroller.Offset.Y - 12));
            else if (point.Y > Thumbnails.Bounds.Height - 30) scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + 12);
        }
        ShowDropIndicator(point);
        e.Handled = true;
    }

    void ThumbnailReleased(object? sender, PointerReleasedEventArgs e)
    {
        var (pressed, dragging, insertion) = (thumbnailPressed, draggingThumbnails, dropIndex);
        EndThumbnailDrag();
        if (!dragging || pressed < 0 || insertion < 0) return;
        e.Pointer.Capture(null);
        e.Handled = true;
        var targets = TargetPages();
        MovePages(targets.Contains(pressed) ? targets : [pressed], insertion);
    }

    void EndThumbnailDrag()
    {
        thumbnailPressed = -1;
        draggingThumbnails = false;
        HideDropIndicator();
    }

    /// Insertion index for a point of the thumbnail list (before the thumbnail whose middle is below it).
    int InsertionIndexAt(Point point, out double lineY)
    {
        lineY = 6;
        var last = -1;
        for (var i = 0; i < thumbnails.Count; i++)
        {
            if (Thumbnails.ContainerFromIndex(i) is not Control container || !container.IsVisible) continue;
            if (container.TranslatePoint(default, Thumbnails) is not { } top) continue;
            if (point.Y < top.Y + container.Bounds.Height / 2)
            {
                lineY = top.Y;
                return i;
            }
            lineY = top.Y + container.Bounds.Height;
            last = i;
        }
        return last + 1;
    }

    void ShowDropIndicator(Point point)
    {
        dropIndex = InsertionIndexAt(point, out var y);
        Canvas.SetTop(DropIndicator, Math.Clamp(y - 1.5, 0, Math.Max(0, Thumbnails.Bounds.Height - 3)));
        DropIndicator.IsVisible = true;
    }

    void HideDropIndicator()
    {
        DropIndicator.IsVisible = false;
        dropIndex = -1;
    }

    /// Files dragged over the thumbnails: inserted where they are dropped (anywhere else, a PDF is opened).
    bool IsOverThumbnails(DragEventArgs e) =>
        document != null && new Rect(Thumbnails.Bounds.Size).Contains(e.GetPosition(Thumbnails));

    static List<string> DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(IsInsertable).ToList() ?? [];

    // MARK: Extracting and exporting

    string BaseName => document == null ? "" : Path.GetFileNameWithoutExtension(document.Path);

    async Task ExtractPages()
    {
        if (document is not { } doc || TargetPages() is not { Length: > 0 } targets) return;
        EndTextEditing(commit: true);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = targets.Length > 1 ? Tr("Enregistrer les %d pages sélectionnées", targets.Length) : Tr("Enregistrer la page"),
            SuggestedFileName = Tr("Pages extraites") + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType(Tr("Documents PDF")) { Patterns = ["*.pdf"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var changed = changedPages.ToList();
        await WriteCopy(path, () => doc.ExtractPages(targets, changed));
    }

    /// Writes a file made off the UI thread; reports a failure.
    async Task<bool> WriteCopy(string path, Func<byte[]> make)
    {
        var cursor = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, make()));
            return true;
        }
        catch (Exception)
        {
            await Dialogs.ShowMessage(this, Tr("Impossible d'enregistrer le document."), Path.GetFileName(path));
            return false;
        }
        finally { Cursor = cursor; }
    }

    async Task ExportImages()
    {
        if (document is not { } doc) return;
        EndTextEditing(commit: true);
        var targets = TargetPages();
        if (await ExportDialogs.AskImageExport(this, pages.Count, targets.Length) is not var (jpeg, dpi, onlySelection)) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Tr("Choisissez le dossier où enregistrer les images") });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        var indexes = onlySelection ? targets : Range(0, pages.Count);
        var digits = pages.Count.ToString().Length;
        var name = BaseName;
        var cursor = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            await Task.Run(() =>
            {
                foreach (var index in indexes)
                {
                    using var bitmap = doc.RenderBitmap(index, dpi / 72.0);
                    using var data = bitmap.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
                    var file = Tr("%@ - page %@", name, (index + 1).ToString().PadLeft(digits, '0')) + (jpeg ? ".jpg" : ".png");
                    File.WriteAllBytes(Path.Combine(folder, file), data.ToArray());
                }
            });
        }
        catch (Exception)
        {
            await Dialogs.ShowMessage(this, Tr("Impossible d'enregistrer les images."), folder);
        }
        finally { Cursor = cursor; }
    }

    async Task ExportForSending()
    {
        if (document is not { } doc) return;
        EndTextEditing(commit: true);
        if (await ExportDialogs.AskSendOptions(this, SendImageMaxPixels) is not { } options) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr("Enregistrer la copie à envoyer"),
            SuggestedFileName = Tr("%@ (envoi)", BaseName) + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType(Tr("Documents PDF")) { Patterns = ["*.pdf"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var changed = changedPages.ToList();
        var written = await WriteCopy(path, () =>
        {
            using var copy = doc.Copy(changed);
            copy.PrepareForSending(options.Flatten, options.RemoveAuthor, options.DownscaleImages ? SendImageMaxPixels : null, options.StripEditingData);
            if (options.Compress) copy.ReduceImages();
            return copy.ToBytes();
        });
        if (written && OperatingSystem.IsWindows())
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (Exception) { } // l10n-ignore
    }

    async Task ReduceSize()
    {
        if (document is not { } doc) return;
        EndTextEditing(commit: true);
        var changed = changedPages.ToList();
        var pageCount = pages.Count;
        var cursor = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        byte[]? reduced;
        long before;
        try
        {
            (reduced, before) = await Task.Run(() =>
            {
                using var copy = doc.Copy(changed);
                var original = File.Exists(doc.Path) ? new FileInfo(doc.Path).Length : copy.ToBytes().LongLength;
                copy.ReduceImages();
                var bytes = copy.ToBytes();
                using var check = PdfDocument.FromBytes(bytes, doc.Password);
                return (check?.PageCount == pageCount ? bytes : null, original);
            });
        }
        catch (Exception) { (reduced, before) = (null, 0); }
        finally { Cursor = cursor; }
        if (reduced == null)
        {
            await Dialogs.ShowMessage(this, Tr("Impossible de réduire ce document."), Tr("Sa structure n'est pas prise en charge."));
            return;
        }
        if (reduced.Length >= before * 0.95)
        {
            await Dialogs.ShowMessage(this, Tr("Ce document est déjà compact."), Tr("Aucune réduction significative n'est possible."));
            return;
        }
        var percent = (int)((1 - (double)reduced.Length / before) * 100);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr("Taille : %@ → %@ (−%d %%)", FileSize(before), FileSize(reduced.Length), percent),
            SuggestedFileName = Tr("%@ (réduit)", BaseName) + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType(Tr("Documents PDF")) { Patterns = ["*.pdf"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is { } path) await WriteCopy(path, () => reduced);
    }

    static string FileSize(long bytes) => bytes >= 1_000_000
        ? Tr("%@ Mo", (bytes / 1_000_000.0).ToString("0.#"))
        : Tr("%@ Ko", Math.Max(1, bytes / 1000).ToString());
}

/// Options of a copy to send.
sealed record SendOptions(bool Flatten, bool DownscaleImages, bool RemoveAuthor, bool StripEditingData, bool Compress);

static class ExportDialogs
{
    static Window Make(string title, string? subtitle, Control content, Button cancel, Button ok)
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        if (subtitle != null) panel.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(content);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);
        return new Window
        {
            Title = "ed-pdf", Content = panel, Width = 480, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    /// Format (JPEG or PNG), resolution, only the selected pages. Null if cancelled.
    public static async Task<(bool Jpeg, int Dpi, bool OnlySelection)?> AskImageExport(Window owner, int pageCount, int selectionCount)
    {
        int[] resolutions = [72, 150, 300];
        var format = new ComboBox { ItemsSource = new[] { "PNG", "JPEG" }, SelectedIndex = 0, MinWidth = 220 }; // l10n-ignore
        var dpi = new ComboBox { ItemsSource = new[] { Tr("72 ppp (écran)"), Tr("150 ppp (standard)"), Tr("300 ppp (impression)") }, SelectedIndex = 1, MinWidth = 220 };
        var range = new ComboBox { ItemsSource = new[] { Tr("Toutes (%d)", pageCount), Tr("Sélection (%d)", selectionCount) }, SelectedIndex = 0, MinWidth = 220 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 12 };
        void Row(int row, string label, Control control)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }
        Row(0, Tr("Format"), format);
        Row(1, Tr("Résolution"), dpi);
        Row(2, Tr("Pages"), range);
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var ok = new Button { Content = Tr("Exporter…"), IsDefault = true, Classes = { "accent" } };
        var window = Make(Tr("Exporter en images"), null, grid, cancel, ok);
        var accepted = false;
        cancel.Click += (_, _) => window.Close();
        ok.Click += (_, _) => { accepted = true; window.Close(); };
        await window.ShowDialog(owner);
        if (!accepted) return null;
        return (format.SelectedIndex == 1, resolutions[Math.Max(0, dpi.SelectedIndex)], range.SelectedIndex == 1);
    }

    public static async Task<SendOptions?> AskSendOptions(Window owner, int imageMaxPixels)
    {
        var content = new StackPanel { Spacing = 12 };
        CheckBox Option(string title, string detail, bool isChecked)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            var box = new CheckBox { Content = text, IsChecked = isChecked, VerticalContentAlignment = VerticalAlignment.Top };
            content.Children.Add(box);
            return box;
        }
        var flatten = Option(Tr("Aplatir les annotations et les formulaires"),
            Tr("Signatures, tampons, notes et champs sont intégrés à la page : ils ne peuvent plus être sélectionnés, déplacés ni modifiés."), true);
        var downscale = Option(Tr("Réduire la résolution des signatures et images"),
            Tr("Limite les images à %d pixels : suffisant à l'écran et à l'impression, moins exploitable si quelqu'un l'extrait.", imageMaxPixels), true);
        var author = Option(Tr("Retirer mon nom d'auteur"), Tr("ed-pdf inscrit le nom de votre compte Windows dans chaque annotation."), true);
        var strip = Option(Tr("Retirer les données d'édition d'ed-pdf"),
            Tr("Supprime la copie de l'image conservée pour la rééditer dans ed-pdf. L'image reste visible."), true);
        var compress = Option(Tr("Réduire la taille du fichier"),
            Tr("Recompresse les images en JPEG adapté à l'écran. Utile pour l'envoi par e-mail ; un peu moins net à l'impression."), false);
        // Flattened, nothing is left to edit.
        void Update() => strip.IsVisible = flatten.IsChecked != true;
        flatten.IsCheckedChanged += (_, _) => Update();
        Update();
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var ok = new Button { Content = Tr("Exporter…"), IsDefault = true, Classes = { "accent" } };
        var window = Make(Tr("Exporter pour envoi"), Tr("Crée une copie prête à envoyer. Le document ouvert n'est pas modifié."), content, cancel, ok);
        SendOptions? result = null;
        cancel.Click += (_, _) => window.Close();
        ok.Click += (_, _) =>
        {
            var flat = flatten.IsChecked == true;
            result = new SendOptions(flat, downscale.IsChecked == true, author.IsChecked == true, flat || strip.IsChecked == true, compress.IsChecked == true);
            window.Close();
        };
        await window.ShowDialog(owner);
        return result;
    }
}
