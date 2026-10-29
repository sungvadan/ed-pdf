using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;
using static PdfSvd.L10n;

namespace PdfSvd;

/// Area where a signature is drawn with the mouse, a pen or a finger.
sealed class SignatureCanvas : Control
{
    readonly List<List<Point>> strokes = [];
    List<Point>? current;

    public event Action? Changed;
    public IEnumerable<Point[]> Strokes => strokes.Select(s => s.ToArray());
    public bool IsEmpty => strokes.Count == 0;

    public SignatureCanvas()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
    }

    public void Clear()
    {
        strokes.Clear();
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        current = [point, point];
        strokes.Add(current);
        e.Pointer.Capture(this);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (current == null) return;
        current.Add(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        current = null;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray), bounds, 8, 8);
        context.DrawLine(new Pen(Brushes.LightGray), new Point(30, bounds.Height - 50), new Point(bounds.Width - 30, bounds.Height - 50));
        var pen = new Pen(Brushes.Black, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var stroke in strokes)
            for (var i = 1; i < stroke.Count; i++) context.DrawLine(pen, stroke[i - 1], stroke[i]);
    }
}

static class SignatureDialogs
{
    static Window Make(string title, Control content, Control left, params Button[] buttons)
    {
        var row = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.AddRange(buttons);
        row.Children.Add(right);
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        panel.Children.Add(content);
        panel.Children.Add(row);
        return new Window
        {
            Title = "pdf-svd", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    /// Draws a new signature. Null if cancelled.
    public static Task<SavedSignature?> DrawSignature(Window owner)
    {
        var canvas = new SignatureCanvas { Width = 520, Height = 200 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = Tr("Signez dans le cadre avec la souris ou le trackpad."), Opacity = 0.7 });
        content.Children.Add(canvas);
        var clear = new Button { Content = Tr("Effacer"), IsEnabled = false };
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var done = new Button { Content = Tr("Enregistrer et placer"), IsDefault = true, IsEnabled = false, Classes = { "accent" } };
        var window = Make(Tr("Nouvelle signature"), content, clear, cancel, done);
        canvas.Changed += () => clear.IsEnabled = done.IsEnabled = !canvas.IsEmpty;
        clear.Click += (_, _) => canvas.Clear();
        cancel.Click += (_, _) => window.Close(null);
        done.Click += (_, _) => window.Close(SavedSignature.FromStrokes(canvas.Strokes));
        return window.ShowDialog<SavedSignature?>(owner);
    }

    /// Creates a custom stamp: its text (in capitals) and colour. Null if cancelled.
    public static Task<CustomStamp?> CreateStamp(Window owner, IReadOnlyList<Color> palette)
    {
        var color = palette.FirstOrDefault(c => c == Color.Parse("#E5322D"), palette[0]);
        var field = new TextBox { Watermark = Tr("Ex. : VU ET VÉRIFIÉ"), Width = 340 };
        var preview = new TextBlock { FontFamily = new FontFamily("Arial"), FontWeight = FontWeight.Bold, FontSize = 20 };
        var frame = new Border { BorderThickness = new Thickness(2.5), Padding = new Thickness(10, 4), HorizontalAlignment = HorizontalAlignment.Center, Child = preview };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var done = new Button { Content = Tr("Enregistrer et placer"), IsDefault = true, IsEnabled = false, Classes = { "accent" } };
        void Update()
        {
            var text = (field.Text ?? "").Trim();
            preview.Text = text.Length == 0 ? Tr("APERÇU") : text.ToUpper();
            frame.Opacity = text.Length == 0 ? 0.3 : 1;
            preview.Foreground = frame.BorderBrush = new SolidColorBrush(color);
            done.IsEnabled = text.Length > 0;
            foreach (var swatch in swatches.Children.OfType<Button>())
                swatch.BorderBrush = (Color)swatch.Tag! == color ? Brushes.DodgerBlue : Brushes.Transparent;
        }
        foreach (var c in palette)
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(2), CornerRadius = new CornerRadius(13), Tag = c, Focusable = false,
                Background = Brushes.Transparent, BorderThickness = new Thickness(2),
                Content = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(c) },
            };
            swatch.Click += (_, _) => { color = c; Update(); };
            swatches.Children.Add(swatch);
        }
        field.TextChanged += (_, _) => Update();
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(field);
        content.Children.Add(swatches);
        content.Children.Add(frame);
        var cancel = new Button { Content = Tr("Annuler"), IsCancel = true };
        var window = Make(Tr("Nouveau tampon"), content, new Control(), cancel, done);
        cancel.Click += (_, _) => window.Close(null);
        done.Click += (_, _) => window.Close(new CustomStamp { Text = (field.Text ?? "").Trim().ToUpper(), Color = color.ToString() });
        window.Opened += (_, _) => field.Focus();
        Update();
        return window.ShowDialog<CustomStamp?>(owner);
    }

    /// Signature written on paper, from a scan or a photo: the paper becomes transparent, the ink keeps its colour,
    /// the result is cropped around the ink. Null if no ink was found.
    public static byte[]? ExtractSignature(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        if (decoded == null) return null;
        var scale = Math.Min(1, 1600.0 / Math.Max(decoded.Width, decoded.Height));
        var info = new SKImageInfo((int)(decoded.Width * scale), (int)(decoded.Height * scale), SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = decoded.Resize(info, SKFilterQuality.High);
        if (image == null) return null;
        // Paper is the brightest part of the photo: ink is what is clearly darker than it.
        var pixels = image.Pixels;
        var luminances = pixels.Select(p => 0.299 * p.Red + 0.587 * p.Green + 0.114 * p.Blue).ToArray();
        var paper = luminances.OrderBy(l => l).ElementAt(luminances.Length * 9 / 10);
        double opaque = paper * 0.45, clear = paper * 0.75;
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (var i = 0; i < pixels.Length; i++)
        {
            var alpha = Math.Clamp((clear - luminances[i]) / (clear - opaque), 0, 1);
            var p = pixels[i];
            pixels[i] = new SKColor(p.Red, p.Green, p.Blue, (byte)(alpha * 255));
            if (alpha < 0.3) continue;
            int x = i % image.Width, y = i / image.Width;
            (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
        }
        if (right < 0) return null;
        image.Pixels = pixels;
        var margin = 4;
        var crop = SKRectI.Create(Math.Max(0, left - margin), Math.Max(0, top - margin),
            Math.Min(image.Width, right + margin + 1) - Math.Max(0, left - margin), Math.Min(image.Height, bottom + margin + 1) - Math.Max(0, top - margin));
        using var cropped = new SKBitmap();
        if (!image.ExtractSubset(cropped, crop)) return null;
        return cropped.Encode(SKEncodedImageFormat.Png, 100)?.ToArray();
    }

    /// An image file as PNG, reduced to `maxPixels` on its longest side. Null if it cannot be read.
    public static byte[]? LoadImage(string path, int maxPixels = 1200)
    {
        using var decoded = SKBitmap.Decode(path);
        if (decoded == null) return null;
        var scale = Math.Min(1, (double)maxPixels / Math.Max(decoded.Width, decoded.Height));
        if (scale >= 1) return decoded.Encode(SKEncodedImageFormat.Png, 100)?.ToArray();
        using var resized = decoded.Resize(new SKImageInfo((int)(decoded.Width * scale), (int)(decoded.Height * scale)), SKFilterQuality.High);
        return resized?.Encode(SKEncodedImageFormat.Png, 100)?.ToArray();
    }
}
