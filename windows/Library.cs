using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace PdfSvd;

/// A saved signature: strokes drawn in pdf-svd (points in a y-down space, bounding box at the origin),
/// or a transparent PNG taken from a scanned or photographed signature.
public sealed class SavedSignature
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double[][][] Strokes { get; set; } = [];
    public string? Png { get; set; }

    public byte[]? ImagePng => Png == null ? null : Convert.FromBase64String(Png);
    public Point[][] Points => Strokes.Select(s => s.Select(p => new Point(p[0], p[1])).ToArray()).ToArray();

    /// From strokes drawn in a view: moved so that their bounding box starts at the origin.
    public static SavedSignature FromStrokes(IEnumerable<Point[]> strokes)
    {
        var list = strokes.Where(s => s.Length > 0).ToList();
        var all = list.SelectMany(s => s).ToList();
        double left = all.Min(p => p.X), top = all.Min(p => p.Y);
        return new SavedSignature { Strokes = list.Select(s => s.Select(p => new[] { p.X - left, p.Y - top }).ToArray()).ToArray() };
    }

    /// Size of the drawing, or of the image in pixels.
    public Size Size
    {
        get
        {
            if (ImagePng is { } png && SkiaSharp.SKBitmap.DecodeBounds(png) is { Width: > 0 } info) return new Size(info.Width, info.Height);
            var all = Strokes.SelectMany(s => s).ToList();
            return all.Count == 0 ? default : new Size(all.Max(p => p[0]), all.Max(p => p[1]));
        }
    }
}

/// A stamp defined by the user.
public sealed class CustomStamp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#E5322D";
}

/// Signatures and custom stamps, kept in %APPDATA%\pdf-svd (each user's own).
public static class Library
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdf-svd");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<SavedSignature> Signatures { get; } = Load<SavedSignature>("signatures.json");
    public static List<CustomStamp> Stamps { get; } = Load<CustomStamp>("stamps.json");

    public static void SaveSignatures() => Save("signatures.json", Signatures);
    public static void SaveStamps() => Save("stamps.json", Stamps);

    static List<T> Load<T>(string name)
    {
        try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(Folder, name))) ?? []; }
        catch (Exception) { return []; }
    }

    static void Save<T>(string name, List<T> items)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, name), JsonSerializer.Serialize(items, Options));
        }
        catch (Exception) { }
    }
}
