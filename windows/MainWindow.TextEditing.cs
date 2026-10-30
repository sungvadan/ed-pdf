using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EdPdf.Pdf;
using EdPdf.Pdf.Text;
using SkiaSharp;
using static EdPdf.L10n;
using Path = System.IO.Path;
using TextBlock = EdPdf.Pdf.Text.TextBlock;
using TextLayout = EdPdf.Pdf.Text.TextLayout;

namespace EdPdf;

/// Editing the existing text of the page, "as in Word": the Edit Text tool frames the paragraph under the pointer,
/// a click opens an editor laid over it, validating rewrites the paragraph (Pdf/Text/TextEditor.cs) as one undoable
/// change. Characters keep the style of the run they come from; typed text takes the style before it.
public partial class MainWindow
{
    /// Incremented by every change of the document: paragraphs analysed for an older version are analysed again.
    int documentVersion;
    (int Page, int Version, List<TextBlock> Blocks)? textLayout;

    // Paragraph under the pointer (Edit Text tool), page space.
    int hoverPage = -1;
    Rect? hoverBounds;

    // Open editor: page, paragraph, its geometry and text when opened.
    int paragraphPage = -1;
    TextBlock? paragraphBlock;
    ParagraphGeometry? paragraphGeometry;
    string paragraphText = "";

    void SetUpParagraphEditor()
    {
        ParagraphEditor.KeyDown += (_, e) =>
        {
            // Escape validates like a click outside: Ctrl+Z undoes the whole edit, retyping lost text is worse.
            if (e.Key != Key.Escape && !(e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))) return;
            EndParagraphEditing(commit: true);
            e.Handled = true;
        };
        ParagraphEditor.TextChanged += (_, _) => PlaceParagraphEditor();
    }

    List<TextBlock> TextBlocksOf(int page)
    {
        if (textLayout is var (cachedPage, version, blocks) && cachedPage == page && version == documentVersion) return blocks;
        var analysed = document?.TextBlocks(page) ?? [];
        textLayout = (page, documentVersion, analysed);
        return analysed;
    }

    /// Only pages shown upright can be edited (the editor is laid out horizontally).
    bool IsUpright(int page) => document?.PageToView(page) is { } m && Math.Abs(m.M12) < 1e-6 && m.M11 > 0;

    TextBlock? BlockAt(int page, Point view)
    {
        var point = ToPage(page, view);
        return TextBlocksOf(page).FirstOrDefault(b => b.Bounds.Inflate(3).Contains(point));
    }

    void UpdateTextHover(PointerEventArgs e)
    {
        Rect? bounds = null;
        var page = -1;
        if (PageViewAt(e) is { DataContext: PageItem item } view && document != null && IsUpright(item.Index))
        {
            var block = BlockAt(item.Index, e.GetPosition(view) / item.Scale);
            var editing = paragraphPage == item.Index && block != null && block.Bounds == paragraphBlock?.Bounds;
            if (block is { IsEditable: true } && !editing) (page, bounds) = (item.Index, block.Bounds);
        }
        if (page == hoverPage && bounds == hoverBounds) return;
        var previous = hoverPage;
        (hoverPage, hoverBounds) = (page, bounds);
        UpdateOverlay(previous);
        UpdateOverlay(page);
    }

    void HideTextHover()
    {
        var previous = hoverPage;
        (hoverPage, hoverBounds) = (-1, null);
        UpdateOverlay(previous);
    }

    void BeginParagraphEditing(int page, Point click)
    {
        if (document == null || !IsUpright(page) || BlockAt(page, click) is not { } block) return;
        if (!block.IsEditable)
        {
            _ = Dialogs.ShowMessage(this, Tr("Le texte n'a pas pu être modifié"), Tr("Ce paragraphe ne peut pas être modifié (texte pivoté ou imbriqué)."));
            return;
        }
        HideTextHover();
        var geometry = ParagraphGeometry.Of(block);
        (paragraphPage, paragraphBlock, paragraphGeometry) = (page, block, geometry);
        paragraphText = string.Concat(StyledText.Segments(block).Select(s => s.Text)).TrimEnd(' ');

        // Shown with the installed font closest to the paragraph's main style.
        var run = block.DominantRun!;
        var typeface = FontResolver.ClosestSystemFont(run.Font, run.Font.IsBold, run.Font.IsItalic);
        ParagraphEditor.FontFamily = new FontFamily(typeface.FamilyName);
        ParagraphEditor.FontWeight = (FontWeight)typeface.FontWeight;
        ParagraphEditor.FontStyle = typeface.FontSlant == SKFontStyleSlant.Upright ? FontStyle.Normal : FontStyle.Italic;
        var c = run.Color;
        ParagraphEditor.Foreground = new SolidColorBrush(Color.FromArgb(255, (byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255)));
        // The page color behind the paragraph (white text on a dark banner stays readable); Fluent replaces the
        // background when focused or hovered, hence the resources.
        var background = document.BackgroundColor(page, block.Bounds);
        var brush = new SolidColorBrush(Color.FromRgb(background.Red, background.Green, background.Blue));
        ParagraphEditor.Background = brush;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" }) // l10n-ignore
            ParagraphEditor.Resources[key] = brush;
        var dark = 0.299 * background.Red + 0.587 * background.Green + 0.114 * background.Blue < 128;
        ParagraphEditor.CaretBrush = dark ? Brushes.White : Brushes.Black;
        ParagraphEditor.TextAlignment = geometry.Alignment switch
        {
            ParagraphAlignment.Center => TextAlignment.Center,
            ParagraphAlignment.Right => TextAlignment.Right,
            ParagraphAlignment.Justified => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        // A single line (title, label…) grows to the right instead of wrapping.
        ParagraphEditor.TextWrapping = block.Lines.Count == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        ParagraphEditor.Text = paragraphText;
        ParagraphEditor.IsVisible = true;
        PlaceParagraphEditor();
        ParagraphEditor.Focus();
        // Caret where the user clicked, once the editor is laid out.
        var target = new Point(click.X * scale, click.Y * scale);
        Dispatcher.UIThread.Post(() =>
        {
            if (paragraphPage != page || ParagraphEditor.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter) return;
            var view = PageViewOf(page);
            if (view?.TranslatePoint(target, presenter) is not { } inside) return;
            ParagraphEditor.CaretIndex = presenter.TextLayout.HitTestPoint(inside).TextPosition;
        }, DispatcherPriority.Background);
        UpdateEditingState();
    }

    PageView? PageViewOf(int page) =>
        (Pages.ContainerFromIndex(page) as Visual)?.GetVisualDescendants().OfType<PageView>().FirstOrDefault();

    /// Puts the editor over its paragraph, its first baseline on the paragraph's first baseline.
    void PlaceParagraphEditor()
    {
        if (paragraphPage < 0 || paragraphGeometry is not { } geometry || paragraphBlock is not { } block || document == null) return;
        var origin = geometry.Origin.Transform(document.PageToView(paragraphPage));
        if (PageViewOf(paragraphPage)?.TranslatePoint(new Point(origin.X * scale, origin.Y * scale), EditorLayer) is not { } baseline) return;
        var size = (block.DominantRun?.FontSize ?? 12) * scale;
        ParagraphEditor.FontSize = size;
        ParagraphEditor.LineHeight = geometry.Leading * scale;
        if (block.Lines.Count == 1)
        {
            ParagraphEditor.Width = double.NaN;
            ParagraphEditor.MinWidth = block.Bounds.Width * scale + 2;
        }
        else
        {
            // The paragraph width comes from ink bounds, slightly narrower than glyph advances.
            ParagraphEditor.Width = (geometry.Width + geometry.WrapTolerance) * scale + 2;
            ParagraphEditor.MinWidth = 0;
        }
        var typeface = new Typeface(ParagraphEditor.FontFamily, ParagraphEditor.FontStyle, ParagraphEditor.FontWeight);
        using var layout = new Avalonia.Media.TextFormatting.TextLayout("X", typeface, size, null, lineHeight: ParagraphEditor.LineHeight); // l10n-ignore
        var firstBaseline = layout.TextLines[0].Baseline;
        Canvas.SetLeft(ParagraphEditor, baseline.X - 1);
        Canvas.SetTop(ParagraphEditor, baseline.Y - firstBaseline - 1);
    }

    /// Closes the paragraph editor; when committing, rewrites the paragraph. Whether an editor was open.
    bool EndParagraphEditing(bool commit)
    {
        if (paragraphPage < 0 || paragraphBlock is not { } block) return false;
        var page = paragraphPage;
        var text = ParagraphEditor.Text ?? "";
        paragraphPage = -1;
        paragraphBlock = null;
        ParagraphEditor.IsVisible = false;
        Scroller.Focus();
        if (commit && text != paragraphText) ApplyTextEdit(page, block, text);
        UpdateEditingState();
        return true;
    }

    /// Rewrites a paragraph on a snapshot of the document and loads the result, as one undoable change.
    void ApplyTextEdit(int page, TextBlock block, string newText)
    {
        if (document is not { } doc) return;
        var cursor = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            var before = doc.Snapshot(changedPages);
            // The same paragraph in the snapshot (analysed the same way as the open document).
            int index;
            lock (Pdfium.Lock)
            {
                using var snapshot = TextDocument.Open(before, doc.Password) ?? throw new TextEditException(TextEditFailure.WriteFailed, "snapshot");
                using var textPage = snapshot.Page(page) ?? throw new TextEditException(TextEditFailure.WriteFailed, "page");
                index = TextLayout.Blocks(textPage.TextRuns()).FindIndex(b => b.Text == block.Text
                    && Math.Abs(b.Bounds.X - block.Bounds.X) < 0.5 && Math.Abs(b.Bounds.Y - block.Bounds.Y) < 0.5);
            }
            if (index < 0) throw new TextEditException(TextEditFailure.NotEditable);
            var after = TextEditor.Replace(index, page, before, doc.Password, b => StyledText.Applying(StyledText.Segments(b), newText)).Data;
            doc.Restore(after);
            DocumentReloaded();
            undo.Push(() => { doc.Restore(before); DocumentReloaded(); }, () => { doc.Restore(after); DocumentReloaded(); });
        }
        catch (Exception error)
        {
            var log = LogTextEditFailure(error, block);
            _ = Dialogs.ShowMessage(this, Tr("Le texte n'a pas pu être modifié"),
                Explain(error) + " " + Tr("Le document n'a pas été changé.") + (log != null ? "\n\n" + Tr("Détails : %@", log) : ""));
        }
        finally { Cursor = cursor; }
    }

    /// After the document was loaded again with the same pages (text edit, or its undo): everything is redrawn.
    void DocumentReloaded()
    {
        changedPages.Clear();
        modified = true;
        documentVersion++;
        selected = null;
        selectedPage = -1;
        SetSelection(-1, 0, 0);
        foreach (var page in pages) page.RenderedScale = 0;
        ViewportChanged();
        foreach (var thumbnail in thumbnails.Where(t => t.Bitmap != null)) RenderThumbnail(thumbnail, force: true);
        for (var i = 0; i < pages.Count; i++) UpdateOverlay(i);
        RecountMarks();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _ = Search(delay: false);
        UpdateEditingState();
    }

    static string Explain(Exception error) => error switch
    {
        TextEditException { Failure: TextEditFailure.VerificationFailed } e when TextEditor.MissingCharacters(e.Expected, e.Found) is { Length: > 0 } missing =>
            Tr("Aucune police disponible ne contient ces caractères : « %@ ».", string.Join(" ", missing.EnumerateRunes())),
        TextEditException { Failure: TextEditFailure.VerificationFailed } => Tr("ed-pdf n'a pas pu écrire ce texte fidèlement avec les polices disponibles."),
        PdfFileException { Message: "encrypted" } => Tr("La modification du texte n'est pas encore possible sur un document protégé par mot de passe."), // l10n-ignore
        TextEditException { Failure: TextEditFailure.NotEditable } => Tr("Ce paragraphe ne peut pas être modifié (texte pivoté ou imbriqué)."),
        TextSurgeryException or PdfFileException => Tr("Ce PDF utilise une structure qu'ed-pdf ne sait pas encore modifier."),
        _ => Tr("Erreur inattendue (%@).", error.Message),
    };

    /// Appends the failure to %LOCALAPPDATA%\ed-pdf\text-edit.log, to send with a bug report.
    static string? LogTextEditFailure(Exception error, TextBlock block)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ed-pdf");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "text-edit.log");
            var fonts = block.Runs.Select(r => $"{r.Font.BaseName} {r.FontSize:0.0}pt embedded={r.Font.IsEmbedded}").Distinct().Order(); // l10n-ignore
            var entry = $"=== {DateTime.Now:O}\nerror: {error}\nfonts: {string.Join("; ", fonts)}\n"; // l10n-ignore
            if (error is TextEditException { Failure: TextEditFailure.VerificationFailed } failed)
                entry += "missing: " + string.Join(" ", TextEditor.MissingCharacters(failed.Expected, failed.Found).EnumerateRunes().Select(r => $"U+{r.Value:X4}")) + "\n"; // l10n-ignore
            File.AppendAllText(path, entry + "\n");
            return path;
        }
        catch (Exception) { return null; }
    }
}
