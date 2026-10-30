using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EdPdf.Pdf;
using static EdPdf.L10n;
using Path = System.IO.Path;

namespace EdPdf;

public enum Tool { Select, EditText, Highlight, Underline, StrikeOut, Note, Text, Ink, Rectangle, Ellipse, Line, Arrow, Redact }

/// Annotations (tools, selection, moving, in-place text editing), undo and saving.
public partial class MainWindow
{
    static readonly Color[] Palette =
    [
        Color.Parse("#FFD60A"), Color.Parse("#34C759"), Color.Parse("#1E5AE6"),
        Color.Parse("#E5322D"), Color.Parse("#8E44AD"), Colors.Black,
    ];
    static readonly double[] LineWidths = [1, 2, 4, 8];
    static readonly double[] FontSizes = [9, 10, 12, 14, 18, 20, 24, 36];
    const double DefaultTextBoxWidth = 200, NoteSize = 24;

    Tool tool = Tool.Select;
    readonly Dictionary<Tool, Color> toolColors = new()
    {
        [Tool.Highlight] = Palette[0], [Tool.Note] = Palette[0], [Tool.Underline] = Palette[2], [Tool.Text] = Colors.Black,
        [Tool.StrikeOut] = Palette[3], [Tool.Ink] = Palette[3], [Tool.Rectangle] = Palette[3], [Tool.Ellipse] = Palette[3],
        [Tool.Line] = Palette[3], [Tool.Arrow] = Palette[3], [Tool.Select] = Palette[3], [Tool.Redact] = RedactionColor,
        [Tool.EditText] = Colors.Black,
    };
    double lineWidth = 2, fontSize = 14;
    readonly Dictionary<Tool, ToggleButton> toolButtons = [];
    readonly List<(Color Color, Button Button)> colorButtons = [];
    bool updatingBar;

    readonly UndoStack undo = new();
    bool modified;
    /// Pages changed since the last save (their annotation appearances are generated before saving).
    readonly HashSet<int> changedPages = [];

    int selectedPage = -1;
    Annotation? selected;

    enum Drag { None, Text, Draw, Shape, Move, Resize }
    Drag drag;
    PageView? dragView;
    int dragPage;
    /// Drag start and current position, in view points (from the page's top left).
    Point dragStart, dragPoint;
    List<Point> inkPoints = [];
    /// Corner being dragged: 0 top left, 1 top right, 2 bottom left, 3 bottom right.
    int resizeCorner;

    // In-place text editing: page, annotation edited (null: new box), top left and width in view points.
    int editingPage = -1;
    Annotation? editingAnnotation;
    Point editingTopLeft;
    double editingWidth, editingSize;
    bool editingStamp;
    Color editingColor;

    // MARK: Annotation bar

    void SetUpAnnotationBar()
    {
        foreach (var t in Enum.GetValues<Tool>())
        {
            var button = new ToggleButton
            {
                Content = ToolIcon(t), Width = 34, Height = 30, Padding = new Thickness(0), Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(button, ToolName(t));
            button.Click += (_, _) => SelectTool(t);
            toolButtons[t] = button;
            ToolButtons.Children.Add(button);
        }
        foreach (var color in Palette)
        {
            // The colour is drawn inside the button: Fluent replaces the button background on hover.
            var swatch = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(2), CornerRadius = new CornerRadius(13), Focusable = false,
                Background = Brushes.Transparent, BorderThickness = new Thickness(2),
                Content = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(color) },
            };
            ToolTip.SetTip(swatch, Tr("Couleur de l'outil"));
            swatch.Click += (_, _) => SetColor(color);
            colorButtons.Add((color, swatch));
            ColorButtons.Children.Add(swatch);
        }
        LineWidthBox.ItemsSource = LineWidths.Select(w => Tr("%d pt", (int)w)).ToList();
        ToolTip.SetTip(LineWidthBox, Tr("Épaisseur du trait"));
        LineWidthBox.SelectionChanged += (_, _) =>
        {
            if (!updatingBar && LineWidthBox.SelectedIndex >= 0) SetLineWidth(LineWidths[LineWidthBox.SelectedIndex]);
        };
        FontSizeBox.ItemsSource = FontSizes.Select(s => Tr("%d pt", (int)s)).ToList();
        ToolTip.SetTip(FontSizeBox, Tr("Taille du texte"));
        FontSizeBox.SelectionChanged += (_, _) =>
        {
            if (!updatingBar && FontSizeBox.SelectedIndex >= 0) SetFontSize(FontSizes[FontSizeBox.SelectedIndex]);
        };
        DeleteButton.Content = Tr("Supprimer");
        DeleteButton.Click += (_, _) => DeleteSelectedAnnotation();
        Editor.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape && !(e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))) return;
            EndTextEditing(commit: true);
            e.Handled = true;
        };
        SetUpInsertMenus();
        undo.Changed += UpdateEditingState;
        UpdateEditingState();
    }

    static string ToolName(Tool t) => t switch
    {
        Tool.Select => Tr("Sélection"),
        Tool.EditText => Tr("Modifier le texte"),
        Tool.Highlight => Tr("Surligner"),
        Tool.Underline => Tr("Souligner"),
        Tool.StrikeOut => Tr("Barrer"),
        Tool.Note => Tr("Note"),
        Tool.Text => Tr("Zone de texte"),
        Tool.Ink => Tr("Dessin"),
        Tool.Rectangle => Tr("Rectangle"),
        Tool.Ellipse => Tr("Ellipse"),
        Tool.Line => Tr("Ligne"),
        Tool.Arrow => Tr("Flèche"),
        _ => Tr("Caviarder"),
    };

    static Control ToolIcon(Tool t)
    {
        T Stroked<T>(T shape) where T : Shape
        {
            shape.StrokeThickness = 1.5;
            shape.Bind(Shape.StrokeProperty, shape.GetResourceObservable("SystemControlForegroundBaseHighBrush"));
            return shape;
        }
        Avalonia.Controls.Shapes.Path Drawing(string data) => Stroked(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Width = 18, Height = 18 });
        TextBlock Letter(string text, TextDecorationCollection? decorations = null) =>
            new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, TextDecorations = decorations };
        return t switch
        {
            Tool.Select => Drawing("M5,2 L5,15 L8.5,11.5 L11,17 L13,16 L10.5,10.5 L15,10.5 Z"),
            Tool.EditText => Drawing("M2,4 H11 M6.5,4 V15 M14.5,3 V16 M12.5,3 H16.5 M12.5,16 H16.5"),
            Tool.Highlight => new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FFE14D")), Padding = new Thickness(2, 0),
                Child = new TextBlock { Text = "ab", FontSize = 13, Foreground = Brushes.Black }, // l10n-ignore
            },
            Tool.Underline => Letter("U", TextDecorations.Underline), // l10n-ignore
            Tool.StrikeOut => Letter("S", TextDecorations.Strikethrough), // l10n-ignore
            Tool.Note => Drawing("M3,3 H15 V12 H9 L6,15 V12 H3 Z"),
            Tool.Text => new Border
            {
                BorderThickness = new Thickness(1), Padding = new Thickness(3, 0),
                Child = Letter("T"), // l10n-ignore
            }.Also(b => b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("SystemControlForegroundBaseHighBrush"))),
            Tool.Ink => Drawing("M2,13 C5,3 7,3 8,9 C9,15 12,15 16,4"),
            Tool.Rectangle => Stroked(new Rectangle { Width = 16, Height = 12 }),
            Tool.Ellipse => Stroked(new Ellipse { Width = 16, Height = 12 }),
            Tool.Line => Drawing("M3,15 L15,3"),
            Tool.Arrow => Drawing("M3,15 L15,3 M8,3 H15 V10"),
            _ => Stroked(new Rectangle { Width = 16, Height = 12 }).Also(r =>
                r.Bind(Shape.FillProperty, r.GetResourceObservable("SystemControlForegroundBaseHighBrush"))),
        };
    }

    void SelectTool(Tool t)
    {
        EndTextEditing(commit: true);
        if (t != Tool.EditText) HideTextHover();
        // A markup tool with text already selected: mark it up at once.
        if (t is Tool.Highlight or Tool.Underline or Tool.StrikeOut or Tool.Redact && HasTextSelection)
        {
            if (t == Tool.Redact) AddRedactionMarks();
            else AddMarkup(t);
            UpdateEditingState();
            return;
        }
        tool = t;
        if (t != Tool.Select) SelectAnnotation(-1, null);
        Pages.Cursor = new Cursor(t is Tool.Select or Tool.EditText or Tool.Highlight or Tool.Underline or Tool.StrikeOut or Tool.Redact
            ? StandardCursorType.Ibeam : StandardCursorType.Cross);
        UpdateEditingState();
    }

    /// Kind whose properties the bar shows: the selected annotation's, or what the current tool creates.
    (bool Color, bool Width, bool FontSize) EditedProperties()
    {
        if (editingPage >= 0) return (true, false, true);
        if (selected != null) return (selected.Kind != AnnotationKind.Stamp && !selected.RedactionMark, selected.HasLineWidth, selected.Kind == AnnotationKind.TextBox);
        return (tool is not (Tool.Select or Tool.Redact or Tool.EditText), tool is Tool.Ink or Tool.Rectangle or Tool.Ellipse or Tool.Line or Tool.Arrow, tool == Tool.Text);
    }

    Color CurrentColor => editingPage >= 0 ? editingColor : selected?.Color ?? toolColors[tool];
    double CurrentLineWidth => selected?.LineWidth ?? lineWidth;
    double CurrentFontSize => editingPage >= 0 ? editingSize : selected?.FontSize ?? fontSize;

    /// Updates the bar, the menus and the title after any change of tool, selection or document.
    void UpdateEditingState()
    {
        updatingBar = true;
        foreach (var (t, button) in toolButtons) button.IsChecked = t == tool;
        var (color, width, size) = EditedProperties();
        ColorButtons.IsVisible = color;
        foreach (var (c, button) in colorButtons)
            button.BorderBrush = c == CurrentColor ? Brushes.DodgerBlue : Brushes.Transparent;
        LineWidthBox.IsVisible = width;
        LineWidthBox.SelectedIndex = Nearest(LineWidths, CurrentLineWidth);
        FontSizeBox.IsVisible = size;
        FontSizeBox.SelectedIndex = Nearest(FontSizes, CurrentFontSize);
        DeleteButton.IsVisible = selected != null;
        var marks = RedactionMarkTotal;
        ApplyRedactionButton.IsVisible = marks > 0;
        ApplyRedactionButton.Content = Tr("Appliquer le caviardage (%d)", marks);
        updatingBar = false;

        AnnotationBar.IsEnabled = document != null;
        UndoItem.IsEnabled = undo.CanUndo;
        RedoItem.IsEnabled = undo.CanRedo;
        DeleteItem.IsEnabled = selected != null;
        Title = document == null ? "ed-pdf" : $"{(modified ? "*" : "")}{Path.GetFileName(document.Path)} — ed-pdf";
    }

    static int Nearest(double[] values, double value) =>
        Array.IndexOf(values, values.MinBy(v => Math.Abs(v - value)));

    void SetColor(Color color)
    {
        if (editingPage >= 0)
        {
            editingColor = toolColors[Tool.Text] = color;
            Editor.Foreground = new SolidColorBrush(color);
        }
        else if (selected != null)
            UpdateAnnotation(selectedPage, selected, selected with { Color = color });
        else
            toolColors[tool] = color;
        UpdateEditingState();
    }

    void SetLineWidth(double width)
    {
        if (selected is { HasLineWidth: true })
            UpdateAnnotation(selectedPage, selected, selected with { LineWidth = width });
        else
            lineWidth = width;
        UpdateEditingState();
    }

    void SetFontSize(double size)
    {
        if (editingPage >= 0)
        {
            editingSize = fontSize = size;
            PlaceEditor();
        }
        else if (selected is { Kind: AnnotationKind.TextBox } box)
            UpdateAnnotation(selectedPage, box, box with { FontSize = size, Bounds = TextBoxBounds(box.Bounds, box.Contents, size, box.StampStyle) });
        else
            fontSize = size;
        UpdateEditingState();
    }

    /// Text box bounds fitting its text: same top and width. A stamp gets the width of its text, same centre.
    static Rect TextBoxBounds(Rect bounds, string text, double size, bool stamp = false)
    {
        var width = stamp ? TextAppearance.NaturalWidth(text, size, stamp: true) + 20 : bounds.Width;
        var height = TextAppearance.Height(text, size, width, stamp);
        var left = stamp ? bounds.Center.X - width / 2 : bounds.X;
        return new Rect(left, bounds.Y + bounds.Height - height, width, height);
    }

    // MARK: Changes (all undoable)

    static string NewName() => "ed-pdf-" + Guid.NewGuid().ToString("N");

    void AddAnnotation(int page, Annotation annotation)
    {
        var doc = document!;
        doc.AddAnnotation(page, annotation);
        PageChanged(page);
        undo.Push(() => { doc.RemoveAnnotation(page, annotation.Name); PageChanged(page); },
                  () => { doc.AddAnnotation(page, annotation); PageChanged(page); });
    }

    void UpdateAnnotation(int page, Annotation before, Annotation after, bool keepAppearance = false)
    {
        var doc = document!;
        doc.UpdateAnnotation(page, after, keepAppearance);
        PageChanged(page);
        undo.Push(() => { doc.UpdateAnnotation(page, before, keepAppearance); PageChanged(page); },
                  () => { doc.UpdateAnnotation(page, after, keepAppearance); PageChanged(page); });
    }

    void RemoveAnnotation(int page, Annotation annotation)
    {
        var doc = document!;
        // An image is needed to put a stamp back if the deletion is undone.
        if (annotation is { Kind: AnnotationKind.Stamp, ImagePng: null }) annotation = annotation with { ImagePng = doc.StampImage(page, annotation.Name) };
        doc.RemoveAnnotation(page, annotation.Name);
        PageChanged(page);
        undo.Push(() => { doc.AddAnnotation(page, annotation); PageChanged(page); },
                  () => { doc.RemoveAnnotation(page, annotation.Name); PageChanged(page); });
    }

    void Undo()
    {
        EndTextEditing(commit: true);
        undo.Undo();
    }

    void Redo()
    {
        EndTextEditing(commit: true);
        undo.Redo();
    }

    /// After a change of page `page`: marks the document modified, redraws the page and its thumbnail.
    void PageChanged(int page)
    {
        if (document == null) return;
        modified = true;
        changedPages.Add(page);
        documentVersion++;
        CountMarks(page);
        Render(pages[page], force: true);
        RenderThumbnail(thumbnails[page], force: true);
        if (selected != null && selectedPage == page)
        {
            selected = document.Annotations(page).FirstOrDefault(a => a.Name == selected.Name);
            if (selected == null) selectedPage = -1;
        }
        UpdateOverlay(page);
        UpdateEditingState();
    }

    void ResetEditing()
    {
        undo.Clear();
        modified = false;
        changedPages.Clear();
        selected = null;
        selectedPage = -1;
        drag = Drag.None;
        UpdateEditingState();
    }

    void DeleteSelectedAnnotation()
    {
        if (selected == null) return;
        var (page, annotation) = (selectedPage, selected);
        SelectAnnotation(-1, null);
        RemoveAnnotation(page, annotation);
    }

    void Escape()
    {
        if (tool != Tool.Select) SelectTool(Tool.Select);
        else if (selected != null) SelectAnnotation(-1, null);
        else SetSelection(-1, 0, 0);
    }

    // MARK: Creating

    Point ToPage(int page, Point view) => view.Transform(document!.ViewToPage(page));
    Rect ToPage(int page, Rect view) => view.TransformToAABB(document!.ViewToPage(page));
    Rect ToView(int page, Rect bounds) => bounds.TransformToAABB(document!.PageToView(page));

    /// Marks up the selected text, one annotation per page, undone together.
    void AddMarkup(Tool markup)
    {
        if (document is not { } doc || !HasTextSelection) return;
        var ranges = textSelection;
        SetSelection(-1, 0, 0);
        undo.Group(() =>
        {
            foreach (var (page, start, count) in ranges)
            {
                var rects = doc.TextRects(page, start, count);
                if (rects.Length == 0) continue;
                var quads = rects.Select(r => new[] { ToPage(page, r.TopLeft), ToPage(page, r.TopRight), ToPage(page, r.BottomLeft), ToPage(page, r.BottomRight) }).ToArray();
                var bounds = rects.Skip(1).Aggregate(rects[0], (all, r) => all.Union(r));
                AddAnnotation(page, new Annotation
                {
                    Name = NewName(), Quads = quads, Bounds = ToPage(page, bounds), Color = toolColors[markup],
                    Kind = markup switch { Tool.Highlight => AnnotationKind.Highlight, Tool.Underline => AnnotationKind.Underline, _ => AnnotationKind.StrikeOut },
                });
            }
        });
    }

    void AddInk(int page, List<Point[]> strokes)
    {
        var ink = strokes.Select(stroke => stroke.Select(p => ToPage(page, p)).ToArray()).ToArray();
        AddAnnotation(page, new Annotation
        {
            Name = NewName(), Kind = AnnotationKind.Ink, Color = toolColors[tool], LineWidth = lineWidth,
            Ink = ink, Bounds = Annotation.InkBounds(ink, lineWidth),
        });
    }

    void AddShape(int page)
    {
        var rect = new Rect(dragStart, dragPoint).Normalize();
        if (rect.Width < 3 && rect.Height < 3) return;
        if (tool == Tool.Redact)
        {
            AddAnnotations(page, [RedactionMark(ToPage(page, rect))]);
            return;
        }
        if (tool is Tool.Line or Tool.Arrow)
        {
            AddInk(page, ShapeStrokes(tool, dragStart, dragPoint, lineWidth));
            return;
        }
        AddAnnotation(page, new Annotation
        {
            Name = NewName(), Kind = tool == Tool.Rectangle ? AnnotationKind.Rectangle : AnnotationKind.Ellipse,
            Color = toolColors[tool], LineWidth = lineWidth, Bounds = ToPage(page, rect.Inflate(lineWidth / 2)),
        });
    }

    /// A line, with a head if `arrow`, as ink strokes.
    static List<Point[]> ShapeStrokes(Tool shape, Point from, Point to, double width)
    {
        var strokes = new List<Point[]> { new[] { from, to } };
        var length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        if (shape != Tool.Arrow || length < 1) return strokes;
        var head = Math.Max(10, 4 * width);
        double dx = (to.X - from.X) / length, dy = (to.Y - from.Y) / length, angle = 25 * Math.PI / 180;
        Point Wing(double a) => new(to.X - head * (dx * Math.Cos(a) - dy * Math.Sin(a)), to.Y - head * (dx * Math.Sin(a) + dy * Math.Cos(a)));
        strokes.Add([Wing(angle), to, Wing(-angle)]);
        return strokes;
    }

    async Task AddNote(int page, Point point)
    {
        var text = await Dialogs.AskText(this, Tr("Note"), "");
        if (string.IsNullOrWhiteSpace(text) || document == null) return;
        var rect = new Rect(point.X - NoteSize / 2, point.Y - NoteSize / 2, NoteSize, NoteSize);
        AddAnnotation(page, new Annotation
        {
            Name = NewName(), Kind = AnnotationKind.Note, Color = toolColors[Tool.Note], Contents = text, Bounds = ToPage(page, rect),
        });
    }

    /// Double click on an annotation: edit the text of a note or of a text box.
    async Task EditAnnotation(int page, Annotation annotation)
    {
        if (annotation.Kind == AnnotationKind.Note)
        {
            var text = await Dialogs.AskText(this, Tr("Note"), annotation.Contents);
            if (text != null && text != annotation.Contents) UpdateAnnotation(page, annotation, annotation with { Contents = text });
        }
        else if (annotation.Kind == AnnotationKind.TextBox)
        {
            var rect = ToView(page, annotation.Bounds);
            BeginTextEditing(page, annotation, rect.TopLeft, rect.Width);
        }
    }

    // MARK: Selection and overlay

    void SelectAnnotation(int page, Annotation? annotation)
    {
        var previous = selectedPage;
        selectedPage = annotation == null ? -1 : page;
        selected = annotation;
        if (previous >= 0) UpdateOverlay(previous);
        if (page >= 0) UpdateOverlay(page);
        UpdateEditingState();
    }

    /// Topmost editable annotation under `point` (view points).
    Annotation? AnnotationAt(int page, Point point) =>
        document!.Annotations(page).LastOrDefault(a => ToView(page, a.Bounds).Inflate(3).Contains(point));

    Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight];

    /// Resize handle of the selected annotation under `point`, or -1.
    int HandleAt(Point point)
    {
        if (selected is not { IsResizable: true }) return -1;
        var corners = Corners(ToView(selectedPage, selected.Bounds));
        var reach = 6 / scale;
        return Array.FindIndex(corners, c => Math.Abs(c.X - point.X) <= reach && Math.Abs(c.Y - point.Y) <= reach);
    }

    Rect ResizedRect()
    {
        var corners = Corners(ToView(selectedPage, selected!.Bounds));
        var opposite = corners[3 - resizeCorner];
        var rect = new Rect(opposite, dragPoint).Normalize();
        if (selected.Kind != AnnotationKind.Stamp) return rect;
        // Images keep their proportions: the width decides, from the fixed corner.
        var original = ToView(selectedPage, selected.Bounds);
        var height = rect.Width * original.Height / Math.Max(original.Width, 0.01);
        return new Rect(rect.X, dragPoint.Y < opposite.Y ? opposite.Y - height : opposite.Y, rect.Width, height);
    }

    void UpdateOverlay(int page)
    {
        if (page < 0 || page >= pages.Count) return;
        var hasSelection = selected != null && selectedPage == page;
        var dragging = drag is not (Drag.None or Drag.Text) && dragPage == page;
        var hovering = hoverPage == page;
        pages[page].Overlay = hasSelection || dragging || hovering ? context => DrawOverlay(context, page) : null;
    }

    void DrawOverlay(DrawingContext context, int page)
    {
        if (document == null) return;
        var thin = 1 / scale;
        var outline = new Pen(Brushes.DodgerBlue, thin, new DashStyle([4, 3], 0));
        if (hoverPage == page && hoverBounds is { } hover) context.DrawRectangle(null, new Pen(Brushes.DodgerBlue, thin), ToView(page, hover).Inflate(2));
        if (selected != null && selectedPage == page)
        {
            var rect = drag switch
            {
                Drag.Move => ToView(page, selected.Bounds).Translate(dragPoint - dragStart),
                Drag.Resize => ResizedRect(),
                _ => ToView(page, selected.Bounds),
            };
            context.DrawRectangle(null, outline, rect.Inflate(2 * thin));
            if (selected.IsResizable && drag == Drag.None)
            {
                var size = 7 * thin;
                foreach (var corner in Corners(rect))
                    context.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, thin), new Rect(corner.X - size / 2, corner.Y - size / 2, size, size));
            }
        }
        if (dragPage != page) return;
        var pen = new Pen(new SolidColorBrush(toolColors[tool]), lineWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (drag)
        {
            case Drag.Draw:
                for (var i = 1; i < inkPoints.Count; i++) context.DrawLine(pen, inkPoints[i - 1], inkPoints[i]);
                break;
            case Drag.Shape when tool == Tool.Redact:
                context.DrawRectangle(new SolidColorBrush(RedactionColor, 0.25), new Pen(new SolidColorBrush(RedactionColor), thin), new Rect(dragStart, dragPoint).Normalize());
                break;
            case Drag.Shape when tool == Tool.Rectangle:
                context.DrawRectangle(null, pen, new Rect(dragStart, dragPoint).Normalize());
                break;
            case Drag.Shape when tool == Tool.Ellipse:
                var r = new Rect(dragStart, dragPoint).Normalize();
                context.DrawEllipse(null, pen, r.Center, r.Width / 2, r.Height / 2);
                break;
            case Drag.Shape:
                foreach (var stroke in ShapeStrokes(tool, dragStart, dragPoint, lineWidth))
                    for (var i = 1; i < stroke.Length; i++) context.DrawLine(pen, stroke[i - 1], stroke[i]);
                break;
        }
    }

    // MARK: Mouse

    void PointerDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Scroller).Properties.IsLeftButtonPressed) return;
        // A click outside the open paragraph editor validates it (and does nothing else).
        if (EndParagraphEditing(commit: true)) return;
        EndTextEditing(commit: true);
        if (PageViewAt(e) is not { DataContext: PageItem item } view || document == null)
        {
            SetSelection(-1, 0, 0);
            SelectAnnotation(-1, null);
            return;
        }
        Scroller.Focus();
        var point = e.GetPosition(view) / item.Scale;
        var page = item.Index;
        (dragView, dragPage, dragStart, dragPoint) = (view, page, point, point);
        drag = Drag.None;
        switch (tool)
        {
            case Tool.Select:
                if (selectedPage == page && HandleAt(point) is var corner and >= 0)
                {
                    (drag, resizeCorner) = (Drag.Resize, corner);
                    break;
                }
                if (AnnotationAt(page, point) is { } hit)
                {
                    SetSelection(-1, 0, 0);
                    SelectAnnotation(page, hit);
                    if (e.ClickCount >= 2) { _ = EditAnnotation(page, hit); return; }
                    drag = Drag.Move;
                    break;
                }
                SelectAnnotation(-1, null);
                SetSelection(-1, 0, 0);
                drag = Drag.Text;
                StartTextSelection(e, view, item);
                return;
            case Tool.Highlight or Tool.Underline or Tool.StrikeOut:
                SetSelection(-1, 0, 0);
                drag = Drag.Text;
                StartTextSelection(e, view, item);
                return;
            // Over text: words to redact; elsewhere: an area.
            case Tool.Redact when CharIndexAt(view, e, near: false) >= 0:
                SetSelection(-1, 0, 0);
                drag = Drag.Text;
                StartTextSelection(e, view, item);
                return;
            case Tool.EditText:
                BeginParagraphEditing(page, point);
                return;
            case Tool.Note:
                _ = AddNote(page, point);
                return;
            case Tool.Text:
                BeginTextEditing(page, null, point, Math.Max(40, Math.Min(DefaultTextBoxWidth, item.WidthPoints - point.X - 4)));
                return;
            case Tool.Ink:
                drag = Drag.Draw;
                inkPoints = [point];
                break;
            default:
                drag = Drag.Shape;
                break;
        }
        e.Pointer.Capture(view);
    }

    void PointerDrag(object? sender, PointerEventArgs e)
    {
        if (drag == Drag.None && tool == Tool.EditText) { UpdateTextHover(e); return; }
        if (drag == Drag.Text) { ExtendTextSelection(e); return; }
        if (drag == Drag.None || dragView?.DataContext is not PageItem item) return;
        var point = e.GetPosition(dragView) / item.Scale;
        if (drag == Drag.Draw && Math.Abs(point.X - inkPoints[^1].X) + Math.Abs(point.Y - inkPoints[^1].Y) >= 0.75) inkPoints.Add(point);
        dragPoint = point;
        UpdateOverlay(dragPage);
    }

    void PointerUp(object? sender, PointerReleasedEventArgs e)
    {
        var finished = drag;
        drag = Drag.None;
        selectingView = null;
        e.Pointer.Capture(null);
        if (document == null) return;
        switch (finished)
        {
            case Drag.Text when tool is Tool.Highlight or Tool.Underline or Tool.StrikeOut:
                AddMarkup(tool);
                break;
            case Drag.Text when tool == Tool.Redact:
                AddRedactionMarks();
                break;
            case Drag.Draw when inkPoints.Count >= 2:
                AddInk(dragPage, [inkPoints.ToArray()]);
                break;
            case Drag.Shape:
                AddShape(dragPage);
                break;
            case Drag.Move when selected != null && (Math.Abs(dragPoint.X - dragStart.X) > 0.5 || Math.Abs(dragPoint.Y - dragStart.Y) > 0.5):
                var delta = ToPage(dragPage, dragPoint) - ToPage(dragPage, dragStart);
                UpdateAnnotation(dragPage, selected, selected.Offset(delta), keepAppearance: true);
                break;
            case Drag.Resize when selected != null:
                var bounds = ToPage(dragPage, ResizedRect());
                if (selected is { Kind: AnnotationKind.TextBox, StampStyle: false }) bounds = TextBoxBounds(bounds, selected.Contents, selected.FontSize);
                if (bounds.Width >= 2 && bounds.Height >= 2) UpdateAnnotation(dragPage, selected, selected with { Bounds = bounds });
                break;
        }
        UpdateOverlay(dragPage);
        dragView = null;
    }

    // MARK: In-place text editing

    void BeginTextEditing(int page, Annotation? existing, Point topLeft, double width)
    {
        if (document == null) return;
        SelectAnnotation(-1, null);
        (editingPage, editingAnnotation, editingTopLeft, editingWidth) = (page, existing, topLeft, width);
        editingSize = existing?.FontSize ?? fontSize;
        editingColor = existing?.Color ?? toolColors[Tool.Text];
        Editor.Text = existing?.Contents ?? "";
        Editor.FontFamily = new FontFamily(TextAppearance.FontFamily);
        editingStamp = existing?.StampStyle ?? false;
        Editor.FontWeight = editingStamp ? FontWeight.Bold : FontWeight.Normal;
        Editor.TextAlignment = editingStamp ? TextAlignment.Center : TextAlignment.Left;
        Editor.Foreground = new SolidColorBrush(editingColor);
        if (existing != null)
        {
            document.SetAnnotationHidden(page, existing.Name, true);
            Render(pages[page], force: true);
        }
        Editor.IsVisible = true;
        PlaceEditor();
        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
        UpdateEditingState();
    }

    /// Puts the editor over its page (after scrolling or zooming).
    void PlaceEditor()
    {
        if (editingPage < 0) return;
        var view = (Pages.ContainerFromIndex(editingPage) as Visual)?.GetVisualDescendants().OfType<PageView>().FirstOrDefault();
        if (view?.TranslatePoint(new Point(editingTopLeft.X * scale, editingTopLeft.Y * scale), EditorLayer) is not { } origin) return;
        Editor.FontSize = editingSize * scale;
        Editor.LineHeight = editingSize * TextAppearance.LineHeight * scale;
        Editor.Padding = new Thickness(Math.Max(0, TextAppearance.PaddingFor(editingStamp) * scale - 1));
        Editor.Width = editingWidth * scale;
        Canvas.SetLeft(Editor, origin.X);
        Canvas.SetTop(Editor, origin.Y);
    }

    /// Ends in-place editing; with `commit`, creates, updates or (if emptied) removes the text box.
    void EndTextEditing(bool commit)
    {
        EndParagraphEditing(commit);
        if (editingPage < 0) return;
        var (page, existing) = (editingPage, editingAnnotation);
        editingPage = -1;
        Editor.IsVisible = false;
        Scroller.Focus();
        if (document == null) return;
        var text = (Editor.Text ?? "").TrimEnd();
        if (existing != null)
        {
            document.SetAnnotationHidden(page, existing.Name, false);
            Render(pages[page], force: true);
        }
        if (commit)
        {
            var bounds = ToPage(page, new Rect(editingTopLeft, new Size(editingWidth, 1)));
            bounds = TextBoxBounds(existing?.Bounds ?? bounds, text, editingSize, editingStamp);
            var box = new Annotation
            {
                Name = existing?.Name ?? NewName(), Kind = AnnotationKind.TextBox, Bounds = bounds,
                Color = editingColor, FontSize = editingSize, Contents = text, StampStyle = editingStamp,
            };
            if (text.Length == 0) { if (existing != null) RemoveAnnotation(page, existing); }
            else if (existing == null) AddAnnotation(page, box);
            else if (text != existing.Contents || editingSize != existing.FontSize || editingColor != existing.Color) UpdateAnnotation(page, existing, box);
        }
        UpdateEditingState();
    }

    // MARK: Saving

    async Task<bool> Save()
    {
        if (document == null) return false;
        EndTextEditing(commit: true);
        return await SaveTo(document.Path);
    }

    async Task<bool> SaveAs()
    {
        if (document == null) return false;
        EndTextEditing(commit: true);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr("Enregistrer sous…"),
            SuggestedFileName = Path.GetFileName(document.Path),
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType(Tr("Documents PDF")) { Patterns = ["*.pdf"] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath() is { } path && await SaveTo(path);
    }

    async Task<bool> SaveTo(string path)
    {
        var doc = document!;
        var changed = changedPages.ToList();
        try
        {
            await Task.Run(() => doc.Save(path, changed));
        }
        catch (Exception)
        {
            await Dialogs.ShowMessage(this, Tr("Impossible d'enregistrer le document."), Path.GetFileName(path));
            return false;
        }
        modified = false;
        changedPages.Clear();
        UpdateEditingState();
        return true;
    }

    /// Before closing or replacing the document: offers to save it. False if the user cancels.
    async Task<bool> ConfirmDiscardChanges()
    {
        EndTextEditing(commit: true);
        if (!modified || document == null) return true;
        return await Dialogs.AskSave(this, Path.GetFileName(document.Path)) switch
        {
            Dialogs.SaveChoice.Save => await Save(),
            Dialogs.SaveChoice.Discard => true,
            _ => false,
        };
    }
}

static class ObjectExtensions
{
    /// Configures an object inside an expression.
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
