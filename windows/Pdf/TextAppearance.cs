using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace PdfSvd.Pdf;

/// Appearance of a text box (FreeText annotation). PDFium does not generate one, and its API cannot add a font resource
/// to an appearance stream: the text is drawn as glyph outlines. /Contents and /DA keep the text editable in other apps.
public static class TextAppearance
{
    public const string FontFamily = "Arial";
    /// Inner margin, in points; line height as a multiple of the font size (same values in the in-place editor).
    public const double Padding = 2, LineHeight = 1.2;
    static readonly SKTypeface Typeface = SKTypeface.FromFamilyName(FontFamily) ?? SKTypeface.Default;

    /// The text wrapped to a box `width` points wide.
    public static List<string> Lines(string text, double size, double width)
    {
        using var font = Paint(Typeface, size);
        var available = Math.Max(1, width - 2 * Padding);
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in Regex.Split(paragraph, "(?<= )")) // each word keeps its following space
            {
                if (word.Length == 0) continue;
                if (line.Length > 0 && font.MeasureText(line + word.TrimEnd()) > available)
                {
                    lines.Add(line.TrimEnd());
                    line = "";
                }
                var rest = word;
                while (line.Length == 0 && rest.Length > 1 && font.MeasureText(rest.TrimEnd()) > available)
                {
                    // A word wider than the box: cut it.
                    var cut = rest.Length - 1;
                    while (cut > 1 && font.MeasureText(rest[..cut]) > available) cut--;
                    lines.Add(rest[..cut]);
                    rest = rest[cut..];
                }
                line += rest;
            }
            lines.Add(line.TrimEnd());
        }
        return lines;
    }

    /// Height of the box that holds the text.
    public static double Height(string text, double size, double width) => Lines(text, size, width).Count * size * LineHeight + 2 * Padding;

    /// Content stream drawing the text in `bounds` (page space, `Y` = bottom edge).
    public static string Build(Rect bounds, string text, double size, Color color)
    {
        var stream = new StringBuilder();
        stream.Append($"{F(color.R / 255.0)} {F(color.G / 255.0)} {F(color.B / 255.0)} rg\n");
        using var font = Paint(Typeface, size);
        var metrics = font.FontMetrics;
        double ascent = -metrics.Ascent, leading = size * LineHeight - (ascent + metrics.Descent);
        var baseline = bounds.Y + bounds.Height - Padding - leading / 2 - ascent;
        foreach (var line in Lines(text, size, bounds.Width))
        {
            var x = bounds.X + Padding;
            foreach (var rune in line.EnumerateRunes())
            {
                var glyph = rune.ToString();
                // Characters missing from Arial (Japanese, Chinese…): a system font that has them.
                var typeface = Typeface.ContainsGlyph(rune.Value) ? Typeface : SKFontManager.Default.MatchCharacter(rune.Value) ?? Typeface;
                using var glyphFont = typeface == Typeface ? null : Paint(typeface, size);
                var used = glyphFont ?? font;
                using var path = used.GetTextPath(glyph, 0, 0);
                AppendPath(stream, path, x, baseline);
                x += used.MeasureText(glyph);
            }
            baseline -= size * LineHeight;
        }
        return stream.ToString();
    }

    static SKPaint Paint(SKTypeface typeface, double size) => new() { Typeface = typeface, TextSize = (float)size, IsAntialias = true };

    /// Skia path (y down, origin on the baseline) → PDF path operators, filled.
    static void AppendPath(StringBuilder stream, SKPath path, double x, double baseline)
    {
        if (path.IsEmpty) return;
        string P(SKPoint p) => $"{F(x + p.X)} {F(baseline - p.Y)}";
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: stream.Append($"{P(points[0])} m\n"); break;
                case SKPathVerb.Line: stream.Append($"{P(points[1])} l\n"); break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic: // conics (rare in fonts) drawn as quadratics
                    SKPoint start = points[0], control = points[1], end = points[2];
                    var c1 = new SKPoint(start.X + 2f / 3 * (control.X - start.X), start.Y + 2f / 3 * (control.Y - start.Y));
                    var c2 = new SKPoint(end.X + 2f / 3 * (control.X - end.X), end.Y + 2f / 3 * (control.Y - end.Y));
                    stream.Append($"{P(c1)} {P(c2)} {P(end)} c\n");
                    break;
                case SKPathVerb.Cubic: stream.Append($"{P(points[1])} {P(points[2])} {P(points[3])} c\n"); break;
                case SKPathVerb.Close: stream.Append("h\n"); break;
            }
        }
        stream.Append("f\n");
    }

    static string F(double value) => PdfDocument.F(value);
}
