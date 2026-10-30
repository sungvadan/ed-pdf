using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EdPdf.Pdf;
using static EdPdf.L10n;

namespace EdPdf;

public partial class MainWindow : Window
{
    /// Device-independent pixels per point at 100 % (1 pt = 1/72 inch, 1 DIP = 1/96 inch).
    const double ActualSize = 96.0 / 72.0;
    static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6.4];
    /// Margins around a page in the list (see MainWindow.axaml): list margin 8, page margin 8 above and below.
    const double ListMargin = 8, PageMargin = 8;
    const double ThumbnailWidth = 130, ThumbnailHeight = 170;

    PdfDocument? document;
    List<PageItem> pages = [];
    List<PageItem> thumbnails = [];
    /// Zoom, in device-independent pixels per point.
    double scale = ActualSize;
    int currentPage;
    bool updatingSelection;

    // Search: all matches of the document, the selected one, the search in progress.
    List<TextMatch> matches = [];
    int matchIndex = -1;
    CancellationTokenSource? searchCancel;

    /// Selected text: characters [Start, Start + Count) of each page, in page order (it can span several pages).
    List<(int Page, int Start, int Count)> textSelection = [];
    bool HasTextSelection => textSelection.Any(r => r.Count > 0);
    /// While selecting with the mouse: the view pressed, and the page and character where the drag started
    /// (-1: not on text yet).
    PageView? selectingView;
    int anchorPage = -1, selectionAnchor = -1;

    public MainWindow() : this(null) { }

    public MainWindow(string? path)
    {
        InitializeComponent();
        OpenButton.Content = Tr("Ouvrir…");
        FitButton.Content = Tr("Ajuster à la fenêtre");
        ToolTip.SetTip(OpenButton, Tr("Ouvrir un PDF") + " (Ctrl+O)");
        ToolTip.SetTip(PreviousButton, Tr("Page précédente"));
        ToolTip.SetTip(NextButton, Tr("Page suivante"));
        ToolTip.SetTip(ZoomOutButton, Tr("Zoom arrière") + " (Ctrl+−)");
        ToolTip.SetTip(ZoomInButton, Tr("Zoom avant") + " (Ctrl++)");
        EmptyText.Text = Tr("Glissez un PDF ici ou ouvrez-en un (Ctrl+O).");
        SearchBox.Watermark = Tr("Rechercher");
        ToolTip.SetTip(SearchBox, Tr("Rechercher") + " (Ctrl+F)");
        ToolTip.SetTip(PreviousMatchButton, Tr("Résultat précédent") + " (Maj+F3)");
        ToolTip.SetTip(NextMatchButton, Tr("Résultat suivant") + " (F3)");
        SetUpMenu();
        SetUpAnnotationBar();
        SetUpPages();
        SetUpRedaction();
        SetUpParagraphEditor();

        OpenButton.Click += async (_, _) => await ChooseFile();
        PreviousButton.Click += (_, _) => GoTo(currentPage - 1);
        NextButton.Click += (_, _) => GoTo(currentPage + 1);
        ZoomInButton.Click += (_, _) => Zoom(+1);
        ZoomOutButton.Click += (_, _) => Zoom(-1);
        FitButton.Click += (_, _) => SetScale(FitWidthScale());
        PageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (int.TryParse(PageBox.Text, out var number)) GoTo(number - 1);
            UpdatePageInfo();
            Scroller.Focus();
        };
        SearchBox.TextChanged += (_, _) => _ = Search(delay: true);
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ShowMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
            else if (e.Key == Key.Escape) { SearchBox.Text = ""; Scroller.Focus(); }
            else return;
            e.Handled = true;
        };
        PreviousMatchButton.Click += (_, _) => ShowMatch(-1);
        NextMatchButton.Click += (_, _) => ShowMatch(+1);
        Scroller.AddHandler(PointerPressedEvent, PointerDown, RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.AddHandler(PointerMovedEvent, PointerDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.AddHandler(PointerReleasedEvent, PointerUp, RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.ScrollChanged += (_, _) => { ViewportChanged(); PlaceEditor(); PlaceParagraphEditor(); };
        Scroller.SizeChanged += (_, _) => ViewportChanged();
        Thumbnails.SelectionChanged += (_, _) =>
        {
            // Several pages selected: they are the targets of page actions, the view stays where it is.
            if (!updatingSelection && Thumbnails.Selection.Count == 1 && Thumbnails.SelectedIndex >= 0) GoTo(Thumbnails.SelectedIndex);
        };
        Thumbnails.ContainerPrepared += (_, e) => { if (e.Index < thumbnails.Count) RenderThumbnail(thumbnails[e.Index]); };
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            // Over the thumbnails, PDFs and images are inserted where they are dropped; elsewhere a PDF is opened.
            if (IsOverThumbnails(e) && DroppedFiles(e).Count > 0)
            {
                ShowDropIndicator(e.GetPosition(Thumbnails));
                e.DragEffects = DragDropEffects.Copy;
                return;
            }
            HideDropIndicator();
            e.DragEffects = PdfPath(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideDropIndicator());
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (IsOverThumbnails(e) && DroppedFiles(e) is { Count: > 0 } files)
            {
                var at = InsertionIndexAt(e.GetPosition(Thumbnails), out double _);
                HideDropIndicator();
                await InsertFiles(files, at);
            }
            else if (PdfPath(e) is { } dropped) await Open(dropped);
        });

        UpdatePageInfo();
        if (path != null) Opened += async (_, _) => await Open(path);
        Task.Run(() => { try { if (OperatingSystem.IsWindows()) FileAssociation.Register(); } catch { } });
    }

    void SetUpMenu()
    {
        FileMenu.Header = Tr("Fichier");
        EditMenu.Header = Tr("Édition");
        Item(OpenItem, Tr("Ouvrir…"), new KeyGesture(Key.O, KeyModifiers.Control), async () => await ChooseFile());
        Item(SaveItem, Tr("Enregistrer"), new KeyGesture(Key.S, KeyModifiers.Control), async () => await Save());
        Item(SaveAsItem, Tr("Enregistrer sous…"), new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift), async () => await SaveAs());
        Item(PrintItem, Tr("Imprimer…"), new KeyGesture(Key.P, KeyModifiers.Control), async () => await Print());
        Item(DefaultAppItem, Tr("Utiliser ed-pdf pour ouvrir les PDF…"), null, () =>
        {
            if (OperatingSystem.IsWindows()) FileAssociation.OpenDefaultAppsSettings();
        });
        Item(QuitItem, Tr("Quitter"), null, Close);
        Item(UndoItem, Tr("Annuler"), new KeyGesture(Key.Z, KeyModifiers.Control), Undo);
        Item(RedoItem, Tr("Rétablir"), new KeyGesture(Key.Y, KeyModifiers.Control), Redo);
        Item(DeleteItem, Tr("Supprimer"), new KeyGesture(Key.Delete), DeleteSelectedAnnotation);
        Item(CopyItem, Tr("Copier"), new KeyGesture(Key.C, KeyModifiers.Control), async () => await CopySelection());
        Item(SelectAllItem, Tr("Tout sélectionner"), new KeyGesture(Key.A, KeyModifiers.Control), SelectAllOnPage);
        Item(FindItem, Tr("Rechercher…"), new KeyGesture(Key.F, KeyModifiers.Control), FocusSearch);
        Item(FindNextItem, Tr("Résultat suivant"), new KeyGesture(Key.F3), () => ShowMatch(+1));
        Item(FindPreviousItem, Tr("Résultat précédent"), new KeyGesture(Key.F3, KeyModifiers.Shift), () => ShowMatch(-1));
        // Printing and file association use Windows APIs (the app also runs on the Mac for development).
        PrintItem.IsVisible = DefaultAppItem.IsVisible = DefaultAppSeparator.IsVisible = OperatingSystem.IsWindows();
    }

    /// The gesture is only displayed: shortcuts are handled in OnKeyDown, so that text fields keep theirs.
    static void Item(MenuItem item, string title, KeyGesture? gesture, Action action)
    {
        item.Header = title;
        item.InputGesture = gesture;
        item.Click += (_, _) => action();
    }

    // MARK: Opening

    async Task ChooseFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr("Ouvrir un PDF"),
            FileTypeFilter = [new FilePickerFileType(Tr("Documents PDF")) { Patterns = ["*.pdf"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await Open(path);
    }

    static string? PdfPath(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    async Task Open(string path)
    {
        if (!await ConfirmDiscardChanges()) return;
        PdfDocument? opened = null;
        string? password = null;
        while (opened == null)
        {
            try
            {
                opened = await Task.Run(() => PdfDocument.Open(path, password));
            }
            catch (PdfPasswordException)
            {
                password = await Dialogs.AskPassword(this, wrong: password != null);
                if (password == null) return;
            }
            catch (Exception)
            {
                await Dialogs.ShowMessage(this, Tr("Impossible d'ouvrir ce fichier."), Path.GetFileName(path));
                return;
            }
        }

        EndTextEditing(commit: false);
        document?.Dispose();
        document = opened;
        ResetEditing();
        BuildPageItems();
        EmptyText.IsVisible = false;
        currentPage = 0;
        textSelection = [];
        matches = [];
        matchIndex = -1;
        SetScale(Math.Min(FitWidthScale(), 2 * ActualSize));
        Scroller.Offset = default;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) await Search(delay: false);
        else UpdateMatchInfo();
    }

    /// Page items of the view and of the thumbnails, for the document's pages.
    void BuildPageItems()
    {
        var sizes = Enumerable.Range(0, document!.PageCount).Select(document.PageSize).ToList();
        pages = sizes.Select((s, i) => new PageItem(i, s.Width, s.Height) { Scale = scale }).ToList();
        thumbnails = sizes.Select((s, i) => new PageItem(i, s.Width, s.Height)
        {
            Scale = Math.Min(ThumbnailWidth / Math.Max(s.Width, 1), ThumbnailHeight / Math.Max(s.Height, 1)),
        }).ToList();
        Pages.ItemsSource = pages;
        Thumbnails.ItemsSource = thumbnails;
        RecountMarks();
    }

    // MARK: Zoom and navigation

    double FitWidthScale()
    {
        var widest = pages.Count == 0 ? 612 : pages.Max(p => p.WidthPoints);
        var available = Scroller.Bounds.Width - 2 * 20 - 20; // list margins, vertical scroll bar
        return Math.Clamp(available / widest, 0.1, 10);
    }

    void Zoom(int direction)
    {
        var percent = scale / ActualSize;
        var next = direction > 0 ? ZoomSteps.FirstOrDefault(z => z > percent + 0.001, ZoomSteps[^1])
                                 : ZoomSteps.LastOrDefault(z => z < percent - 0.001, ZoomSteps[0]);
        SetScale(next * ActualSize);
    }

    /// Changes the zoom, keeping the same point of the current page at the top of the window.
    void SetScale(double newScale)
    {
        EndParagraphEditing(commit: true);
        var page = currentPage;
        var within = pages.Count == 0 ? 0 : (Scroller.Offset.Y - PageTop(page)) / pages[page].Height;
        scale = newScale;
        foreach (var item in pages) item.Scale = scale;
        Scroller.UpdateLayout();
        if (pages.Count > 0) Scroller.Offset = new Vector(Scroller.Offset.X, PageTop(page) + within * pages[page].Height);
        ViewportChanged();
        PlaceEditor();
    }

    /// Top of a page in the scrolled content.
    double PageTop(int index)
    {
        var top = ListMargin + PageMargin;
        for (var i = 0; i < index && i < pages.Count; i++) top += pages[i].Height + 2 * PageMargin;
        return top;
    }

    void GoTo(int index)
    {
        if (pages.Count == 0) return;
        index = Math.Clamp(index, 0, pages.Count - 1);
        Scroller.Offset = new Vector(Scroller.Offset.X, PageTop(index) - PageMargin);
        currentPage = index;
        UpdatePageInfo();
    }

    void UpdatePageInfo()
    {
        var hasDocument = pages.Count > 0;
        PageBox.Text = hasDocument ? $"{currentPage + 1}" : "";
        PageBox.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = hasDocument;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = hasDocument;
        ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = FitButton.IsEnabled = hasDocument;
        PrintItem.IsEnabled = SelectAllItem.IsEnabled = SaveItem.IsEnabled = SaveAsItem.IsEnabled = hasDocument;
        SendExportItem.IsEnabled = ImageExportItem.IsEnabled = ReduceSizeItem.IsEnabled = PagesMenu.IsEnabled = hasDocument;
        foreach (var item in pageItems) item.IsEnabled = hasDocument;
        CopyItem.IsEnabled = HasTextSelection;
        PageCountText.Text = hasDocument ? $"/ {pages.Count}" : "";
        ZoomText.Text = hasDocument ? $"{Math.Round(scale / ActualSize * 100)} %" : "";
        updatingSelection = true;
        // A selection of several pages stays while scrolling (it is what the page actions apply to).
        if (Thumbnails.Selection.Count <= 1) Thumbnails.SelectedIndex = hasDocument ? currentPage : -1;
        if (hasDocument) Thumbnails.ScrollIntoView(currentPage);
        updatingSelection = false;
    }

    // MARK: Rendering

    /// Renders the visible pages and releases the bitmaps of pages far from the viewport.
    void ViewportChanged()
    {
        if (pages.Count == 0) return;
        double top = Scroller.Offset.Y, height = Scroller.Viewport.Height, y = ListMargin + PageMargin;
        var current = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (y <= top + height / 3) current = i;
            var visible = y + page.Height >= top && y <= top + height;
            var near = y + page.Height >= top - height && y <= top + 2 * height;
            if (visible || near) Render(page);
            else if (page.Bitmap != null) { page.Bitmap = null; page.RenderedScale = 0; }
            y += page.Height + 2 * PageMargin;
        }
        if (current != currentPage) { currentPage = current; }
        UpdatePageInfo();
    }

    /// `force`: the page changed (annotations), render it again even at the same zoom.
    void Render(PageItem page, bool force = false)
    {
        if (page.RenderedScale == page.Scale && !force) return;
        page.RenderedScale = page.Scale;
        var version = ++page.RenderVersion;
        _ = RenderInto(page, page.Scale * RenderScaling, () => page.RenderedScale == page.Scale && page.RenderVersion == version);
    }

    void RenderThumbnail(PageItem thumbnail, bool force = false)
    {
        if (thumbnail.Bitmap != null && !force) return;
        var version = ++thumbnail.RenderVersion;
        _ = RenderInto(thumbnail, thumbnail.Scale * RenderScaling, () => thumbnail.RenderVersion == version);
    }

    /// Renders off the UI thread; the result is dropped if the document or zoom changed meanwhile.
    async Task RenderInto(PageItem item, double pixelsPerPoint, Func<bool> stillWanted)
    {
        var source = document;
        if (source == null) return;
        int width = Math.Max(1, (int)Math.Ceiling(item.WidthPoints * pixelsPerPoint));
        int height = Math.Max(1, (int)Math.Ceiling(item.HeightPoints * pixelsPerPoint));
        var bitmap = await Task.Run(() =>
        {
            var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = result.Lock()) source.Render(item.Index, buffer.Address, width, height, buffer.RowBytes);
            return result;
        });
        if (source != document || !stillWanted()) return;
        item.Bitmap = bitmap;
    }

    // MARK: Search

    void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// Searches the document for the text of the search field (after a pause while typing).
    async Task Search(bool delay)
    {
        searchCancel?.Cancel();
        var cancel = searchCancel = new CancellationTokenSource();
        var query = SearchBox.Text?.Trim() ?? "";
        var source = document;
        List<TextMatch> found = [];
        if (query.Length > 0 && source != null)
        {
            try
            {
                if (delay) await Task.Delay(250, cancel.Token);
                found = await Task.Run(() => source.Find(query, cancel.Token), cancel.Token);
            }
            catch (OperationCanceledException) { return; }
            if (cancel.IsCancellationRequested || source != document) return;
        }
        matches = found;
        foreach (var page in pages) page.Matches = [];
        foreach (var group in found.GroupBy(m => m.Page)) pages[group.Key].Matches = group.SelectMany(m => m.Rects).ToArray();
        // First match from the current page on.
        matchIndex = found.Count == 0 ? -1 : Math.Max(0, found.FindIndex(m => m.Page >= currentPage));
        SelectMatch();
    }

    void ShowMatch(int direction)
    {
        if (matches.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _ = Search(delay: false); // Enter before the end of the pause
            return;
        }
        matchIndex = (matchIndex + direction + matches.Count) % matches.Count;
        SelectMatch();
    }

    /// Highlights the selected match and scrolls to it if it is not visible.
    void SelectMatch()
    {
        foreach (var page in pages) if (page.CurrentMatch.Count > 0) page.CurrentMatch = [];
        UpdateMatchInfo();
        if (matchIndex < 0) return;
        var match = matches[matchIndex];
        var item = pages[match.Page];
        item.CurrentMatch = match.Rects;
        if (match.Rects.Length == 0) { GoTo(match.Page); return; }
        var rect = match.Rects[0];
        double top = PageTop(match.Page) + rect.Y * scale, bottom = top + rect.Height * scale;
        var viewport = Scroller.Viewport.Height;
        if (top < Scroller.Offset.Y || bottom > Scroller.Offset.Y + viewport)
            Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, top - viewport / 3));
    }

    void UpdateMatchInfo()
    {
        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text) && document != null;
        MatchText.Text = !searching ? "" : matches.Count == 0 ? Tr("Aucun résultat") : $"{matchIndex + 1}/{matches.Count}";
        PreviousMatchButton.IsEnabled = NextMatchButton.IsEnabled = matches.Count > 0;
        FindNextItem.IsEnabled = FindPreviousItem.IsEnabled = matches.Count > 0;
    }

    // MARK: Text selection

    static PageView? PageViewAt(PointerEventArgs e) => (e.Source as Visual)?.FindAncestorOfType<PageView>(includeSelf: true);

    /// Character under the pointer, or near it (the end of a line when dragging beyond it), or -1.
    int CharIndexAt(PageView view, PointerEventArgs e, bool near)
    {
        if (document == null || view.DataContext is not PageItem item) return -1;
        var point = e.GetPosition(view) / item.Scale;
        var index = document.CharIndexAt(item.Index, point, 4);
        return index >= 0 || !near ? index : document.CharIndexAt(item.Index, point, 40);
    }

    /// Mouse down on a page with the selection tool or a markup tool (see PointerDown).
    void StartTextSelection(PointerPressedEventArgs e, PageView view, PageItem item)
    {
        if (document == null) return;
        var index = CharIndexAt(view, e, near: false);
        if (e.ClickCount >= 2 && index >= 0)
        {
            var (start, count) = document.WordAt(item.Index, index);
            SetSelection(item.Index, start, count);
            return;
        }
        selectingView = view;
        anchorPage = item.Index;
        selectionAnchor = index;
        e.Pointer.Capture(view);
    }

    /// The page under the pointer, or the nearest one (between pages, above or below the list).
    PageView? PageViewUnder(PointerEventArgs e)
    {
        PageView? nearest = null;
        var distance = double.MaxValue;
        for (var i = 0; i < pages.Count; i++)
        {
            if (PageViewOf(i) is not { } view) continue;
            var y = e.GetPosition(view).Y;
            var d = y < 0 ? -y : y > view.Bounds.Height ? y - view.Bounds.Height : 0;
            if (d < distance) (nearest, distance) = (view, d);
        }
        return nearest;
    }

    void ExtendTextSelection(PointerEventArgs e)
    {
        if (selectingView == null || document == null || PageViewUnder(e) is not { DataContext: PageItem item } view) return;
        // Scrolls when the pointer is near the top or bottom edge, to select across pages.
        var y = e.GetPosition(Scroller).Y;
        if (y < 20) Scroller.Offset = Scroller.Offset.WithY(Math.Max(0, Scroller.Offset.Y - 20));
        else if (y > Scroller.Viewport.Height - 20) Scroller.Offset = Scroller.Offset.WithY(Scroller.Offset.Y + 20);
        var page = item.Index;
        var index = CharIndexAt(view, e, near: true);
        if (index < 0)
        {
            // Off the text of another page: from its start (going down) or to its end (going up).
            if (page == anchorPage || anchorPage < 0) return;
            index = page > anchorPage ? 0 : Math.Max(0, document.CharCount(page) - 1);
        }
        if (selectionAnchor < 0) (anchorPage, selectionAnchor) = (page, index);
        var (fromPage, from, toPage, to) = (anchorPage, selectionAnchor, page, index);
        if (toPage < fromPage || (toPage == fromPage && to < from)) (fromPage, from, toPage, to) = (toPage, to, fromPage, from);
        var ranges = new List<(int, int, int)>();
        for (var p = fromPage; p <= toPage; p++)
        {
            var start = p == fromPage ? from : 0;
            var end = p == toPage ? to : document.CharCount(p) - 1;
            if (end >= start) ranges.Add((p, start, end - start + 1));
        }
        SetSelection(ranges);
    }

    void SetSelection(int page, int start, int count) => SetSelection(page < 0 ? [] : [(page, start, count)]);

    void SetSelection(List<(int Page, int Start, int Count)> ranges)
    {
        foreach (var (page, _, _) in textSelection)
            if (page < pages.Count && ranges.All(r => r.Page != page)) pages[page].Selection = [];
        textSelection = ranges;
        if (document != null)
            foreach (var (page, start, count) in ranges) pages[page].Selection = document.TextRects(page, start, count);
        CopyItem.IsEnabled = HasTextSelection;
    }

    void SelectAllOnPage()
    {
        if (document != null && pages.Count > 0) SetSelection(currentPage, 0, document.CharCount(currentPage));
    }

    async Task CopySelection()
    {
        if (document is not { } doc || !HasTextSelection || Clipboard == null) return;
        await Clipboard.SetTextAsync(string.Join("\n", textSelection.Select(r => doc.Text(r.Page, r.Start, r.Count))));
    }

    // MARK: Printing

    async Task Print()
    {
        if (!OperatingSystem.IsWindows() || document == null) return;
        try
        {
            await Printing.Print(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, document);
        }
        catch (Exception)
        {
            await Dialogs.ShowMessage(this, Tr("Impossible d'imprimer ce document."), Path.GetFileName(document.Path));
        }
    }

    // MARK: Keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var inTextField = FocusManager?.GetFocusedElement() is TextBox;
        switch (e.Key)
        {
            case Key.O when command: _ = ChooseFile(); break;
            case Key.S when command && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _ = SaveAs(); break;
            case Key.S when command: _ = Save(); break;
            case Key.Z when command && !inTextField && e.KeyModifiers.HasFlag(KeyModifiers.Shift): Redo(); break;
            case Key.Z when command && !inTextField: Undo(); break;
            case Key.Y when command && !inTextField: Redo(); break;
            case Key.Delete or Key.Back when !inTextField && selected != null: DeleteSelectedAnnotation(); break;
            case Key.Delete or Key.Back when !inTextField && Thumbnails.IsKeyboardFocusWithin: DeletePages(); break;
            case Key.L when command && !inTextField: RotatePages(-1); break;
            case Key.R when command && !inTextField: RotatePages(+1); break;
            case Key.D when command && !inTextField: DuplicatePages(); break;
            case Key.E when command && e.KeyModifiers.HasFlag(KeyModifiers.Shift): _ = ExportForSending(); break;
            case Key.P when command: _ = Print(); break;
            case Key.F when command: FocusSearch(); break;
            case Key.F3: ShowMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1); break;
            case Key.C when command && !inTextField: _ = CopySelection(); break;
            case Key.A when command && !inTextField: SelectAllOnPage(); break;
            case Key.Escape when !inTextField: Escape(); break;
            case Key.OemPlus or Key.Add when command: Zoom(+1); break;
            case Key.OemMinus or Key.Subtract when command: Zoom(-1); break;
            case Key.D0 or Key.NumPad0 when command: SetScale(ActualSize); break;
            case Key.Home when !inTextField: GoTo(0); break;
            case Key.End when !inTextField: GoTo(pages.Count - 1); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    bool closingConfirmed;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        EndTextEditing(commit: true);
        if (closingConfirmed || !modified) return;
        e.Cancel = true;
        if (!await ConfirmDiscardChanges()) return;
        closingConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        document?.Dispose();
        base.OnClosed(e);
    }
}
