using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PdfSvd;

/// A page in the main list: its rendering, the search matches and the selected text (see PageItem).
public sealed class PageView : Control
{
    static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(110, 255, 220, 0));
    static readonly IBrush CurrentMatchBrush = new SolidColorBrush(Color.FromArgb(150, 255, 140, 0));
    static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(80, 0, 110, 230));

    PageItem? item;

    public PageView() => Cursor = new Cursor(StandardCursorType.Ibeam);

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (item != null) item.PropertyChanged -= ItemChanged;
        item = DataContext as PageItem;
        if (item != null) item.PropertyChanged += ItemChanged;
        InvalidateVisual();
        base.OnDataContextChanged(e);
    }

    void ItemChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);
        if (item == null) return;
        if (item.Bitmap is { } bitmap) context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
        Fill(context, item.Matches, MatchBrush);
        Fill(context, item.CurrentMatch, CurrentMatchBrush);
        Fill(context, item.Selection, SelectionBrush);
    }

    void Fill(DrawingContext context, IReadOnlyList<Rect> rects, IBrush brush)
    {
        var scale = item!.Scale;
        foreach (var r in rects) context.FillRectangle(brush, new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale));
    }
}
