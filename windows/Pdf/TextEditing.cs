using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using PdfSvd.Pdf.Text;
using SkiaSharp;

namespace PdfSvd.Pdf;

/// Reading paragraphs of the open document for editing (the edit itself is made on a snapshot, see Text/TextEditor.cs).
public sealed partial class PdfDocument
{
    /// Paragraphs of a page, analysed as TextEditor does.
    public List<TextBlock> TextBlocks(int index) => WithPage(index, new List<TextBlock>(), page =>
    {
        using var textPage = new TextPage(handle, page, ownsPage: false);
        return TextLayout.Blocks(textPage.TextRuns());
    });

    /// Most frequent color of a page area, in page space (text covers far fewer pixels than its background).
    public SKColor BackgroundColor(int index, Rect area)
    {
        var view = area.Inflate(2).TransformToAABB(PageToView(index));
        using var bitmap = RenderBitmap(index, 1);
        var counts = new Dictionary<int, int>();
        for (var y = (int)Math.Max(0, view.Y); y < Math.Min(bitmap.Height, view.Bottom); y++)
            for (var x = (int)Math.Max(0, view.X); x < Math.Min(bitmap.Width, view.Right); x++)
            {
                var p = bitmap.GetPixel(x, y);
                var key = p.Red >> 3 << 10 | p.Green >> 3 << 5 | p.Blue >> 3;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        if (counts.Count == 0) return SKColors.White;
        var best = counts.MaxBy(c => c.Value).Key;
        byte Channel(int shift) => (byte)Math.Min(255, ((best >> shift) & 31) * 8 + 4);
        return new SKColor(Channel(10), Channel(5), Channel(0));
    }
}
