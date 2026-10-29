using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PdfSvd;

/// One page of the open document, as shown in the page list or the thumbnails.
public sealed class PageItem(int index, double widthPoints, double heightPoints) : INotifyPropertyChanged
{
    public int Index { get; } = index;
    public int Number => Index + 1;
    public double WidthPoints { get; } = widthPoints;
    public double HeightPoints { get; } = heightPoints;

    double scale = 1;
    /// Display size in device-independent pixels per point.
    public double Scale
    {
        get => scale;
        set { scale = value; Changed(); Changed(nameof(Width)); Changed(nameof(Height)); }
    }
    public double Width => WidthPoints * scale;
    public double Height => HeightPoints * scale;

    Bitmap? bitmap;
    /// Last rendering, possibly at an older scale (stretched until the new one is ready); null when released.
    public Bitmap? Bitmap { get => bitmap; set { bitmap = value; Changed(); } }
    /// Scale of `Bitmap`, or of the rendering in progress.
    public double RenderedScale { get; set; }

    // Highlights, in points from the page's top left.
    IReadOnlyList<Rect> matches = [], currentMatch = [], selection = [];
    /// Search matches on this page.
    public IReadOnlyList<Rect> Matches { get => matches; set { matches = value; Changed(); } }
    /// The selected search match, if it is on this page.
    public IReadOnlyList<Rect> CurrentMatch { get => currentMatch; set { currentMatch = value; Changed(); } }
    /// Selected text.
    public IReadOnlyList<Rect> Selection { get => selection; set { selection = value; Changed(); } }

    Action<Avalonia.Media.DrawingContext>? overlay;
    /// Extra drawing over the page, in points (selected annotation, shape being drawn).
    public Action<Avalonia.Media.DrawingContext>? Overlay { get => overlay; set { overlay = value; Changed(); } }

    /// Incremented by each rendering request: a rendering that finishes after a newer one was requested is dropped.
    public int RenderVersion { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
