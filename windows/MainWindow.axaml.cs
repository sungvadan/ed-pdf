using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using PdfSvd.Pdf;
using static PdfSvd.L10n;

namespace PdfSvd;

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
        Scroller.ScrollChanged += (_, _) => ViewportChanged();
        Scroller.SizeChanged += (_, _) => ViewportChanged();
        Thumbnails.SelectionChanged += (_, _) =>
        {
            if (!updatingSelection && Thumbnails.SelectedIndex >= 0) GoTo(Thumbnails.SelectedIndex);
        };
        Thumbnails.ContainerPrepared += (_, e) => { if (e.Index < thumbnails.Count) RenderThumbnail(thumbnails[e.Index]); };
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = PdfPath(e) != null ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) => { if (PdfPath(e) is { } dropped) await Open(dropped); });

        UpdatePageInfo();
        if (path != null) Opened += async (_, _) => await Open(path);
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

        document?.Dispose();
        document = opened;
        Title = $"{Path.GetFileName(path)} — pdf-svd";
        var sizes = Enumerable.Range(0, opened.PageCount).Select(opened.PageSize).ToList();
        pages = sizes.Select((s, i) => new PageItem(i, s.Width, s.Height)).ToList();
        thumbnails = sizes.Select((s, i) => new PageItem(i, s.Width, s.Height)
        {
            Scale = Math.Min(ThumbnailWidth / Math.Max(s.Width, 1), ThumbnailHeight / Math.Max(s.Height, 1)),
        }).ToList();
        Pages.ItemsSource = pages;
        Thumbnails.ItemsSource = thumbnails;
        EmptyText.IsVisible = false;
        currentPage = 0;
        SetScale(Math.Min(FitWidthScale(), 2 * ActualSize));
        Scroller.Offset = default;
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
        var page = currentPage;
        var within = pages.Count == 0 ? 0 : (Scroller.Offset.Y - PageTop(page)) / pages[page].Height;
        scale = newScale;
        foreach (var item in pages) item.Scale = scale;
        Scroller.UpdateLayout();
        if (pages.Count > 0) Scroller.Offset = new Vector(Scroller.Offset.X, PageTop(page) + within * pages[page].Height);
        ViewportChanged();
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
        ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = FitButton.IsEnabled = hasDocument;
        PageCountText.Text = hasDocument ? $"/ {pages.Count}" : "";
        ZoomText.Text = hasDocument ? $"{Math.Round(scale / ActualSize * 100)} %" : "";
        updatingSelection = true;
        Thumbnails.SelectedIndex = hasDocument ? currentPage : -1;
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

    void Render(PageItem page)
    {
        if (page.RenderedScale == page.Scale) return;
        page.RenderedScale = page.Scale;
        _ = RenderInto(page, page.Scale * RenderScaling, () => page.RenderedScale == page.Scale);
    }

    void RenderThumbnail(PageItem thumbnail)
    {
        if (thumbnail.Bitmap != null) return;
        _ = RenderInto(thumbnail, thumbnail.Scale * RenderScaling, () => true);
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

    // MARK: Keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.O when command: _ = ChooseFile(); break;
            case Key.OemPlus or Key.Add when command: Zoom(+1); break;
            case Key.OemMinus or Key.Subtract when command: Zoom(-1); break;
            case Key.D0 or Key.NumPad0 when command: SetScale(ActualSize); break;
            case Key.Home when !PageBox.IsFocused: GoTo(0); break;
            case Key.End when !PageBox.IsFocused: GoTo(pages.Count - 1); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        document?.Dispose();
        base.OnClosed(e);
    }
}
