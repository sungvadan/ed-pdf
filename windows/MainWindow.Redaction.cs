using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using EdPdf.Pdf;
using static EdPdf.L10n;

namespace EdPdf;

/// Redaction: marks placed with the Redact tool, then applied (see Pdf/Redaction.cs).
public partial class MainWindow
{
    static readonly Color RedactionColor = Color.Parse("#FF3B30");

    /// Redaction marks per page (counted in the background when a document is opened).
    Dictionary<int, int> markCounts = [];

    int RedactionMarkTotal => markCounts.Values.Sum();

    void SetUpRedaction()
    {
        ApplyRedactionButton.Click += async (_, _) => await ApplyRedactions();
    }

    static Annotation RedactionMark(Rect bounds) => new()
    {
        Name = NewName(), Kind = AnnotationKind.Rectangle, RedactionMark = true, Color = RedactionColor, LineWidth = 1, Bounds = bounds,
    };

    /// One mark per line of the selected text (on each page it spans), undone together.
    void AddRedactionMarks()
    {
        if (document is not { } doc || !HasTextSelection) return;
        var ranges = textSelection;
        SetSelection(-1, 0, 0);
        undo.Group(() =>
        {
            foreach (var (page, start, count) in ranges)
                AddAnnotations(page, doc.TextRects(page, start, count).Select(r => RedactionMark(ToPage(page, r.Inflate(1)))).ToList());
        });
    }

    /// Adds annotations to a page as one undoable change.
    void AddAnnotations(int page, List<Annotation> annotations)
    {
        if (annotations.Count == 0) return;
        var doc = document!;
        foreach (var annotation in annotations) doc.AddAnnotation(page, annotation);
        PageChanged(page);
        undo.Push(() => { foreach (var a in annotations) doc.RemoveAnnotation(page, a.Name); PageChanged(page); },
                  () => { foreach (var a in annotations) doc.AddAnnotation(page, a); PageChanged(page); });
    }

    void CountMarks(int page)
    {
        if (document == null) return;
        var count = document.RedactionMarkCount(page);
        if (count > 0) markCounts[page] = count;
        else markCounts.Remove(page);
    }

    /// Counts the marks of every page off the UI thread (a long document opens without waiting for it).
    void RecountMarks()
    {
        markCounts = [];
        if (document is not { } doc) return;
        var counts = markCounts;
        _ = Task.Run(() => Enumerable.Range(0, doc.PageCount).Select(i => (i, doc.RedactionMarkCount(i))).Where(c => c.Item2 > 0).ToList())
            .ContinueWith(task =>
            {
                // Dropped if the document or its pages changed meanwhile (a newer count is under way).
                if (task.IsFaulted || doc != document || counts != markCounts) return;
                foreach (var (page, count) in task.Result) markCounts.TryAdd(page, count);
                UpdateEditingState();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    async Task ApplyRedactions()
    {
        if (document == null) return;
        EndTextEditing(commit: true);
        var affected = markCounts.Where(c => c.Value > 0).Select(c => c.Key).Order().ToArray();
        if (affected.Length == 0) return;
        var confirmed = await Dialogs.Confirm(this, Tr("Appliquer le caviardage ?"),
            Tr("Le contenu sous les %d zones marquées sera supprimé du document. Les %d pages concernées sont reconstruites : leur texte restant reste sélectionnable, mais leurs annotations et liens sont intégrés à la page. Vous pouvez annuler (Ctrl+Z) tant que le document est ouvert ; une fois enregistré, le contenu caviardé est perdu.",
                RedactionMarkTotal, affected.Length),
            Tr("Appliquer"));
        if (!confirmed) return;
        var cursor = Cursor;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
        try
        {
            ChangePages(doc =>
            {
                foreach (var page in affected) doc.Redact(page);
                return (currentPage, []);
            });
        }
        finally { Cursor = cursor; }
    }
}
