using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PdfSvd;

/// Reads the images that can be placed or turned into a signature. Skia handles PNG, JPEG, BMP, GIF and WebP;
/// HEIC/HEIF (iPhone photos) goes through Windows Imaging Component, which uses the system's HEIF extension.
static class ImageDecoder
{
    public static readonly string[] Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.heic", "*.heif"]; // l10n-ignore

    public static bool IsHeif(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif"; // l10n-ignore

    /// The image, turned upright according to its EXIF orientation. Null if it cannot be read.
    public static SKBitmap? Decode(string path)
    {
        using (var codec = SKCodec.Create(path))
        {
            if (codec != null && SKBitmap.Decode(codec) is { } bitmap) return Upright(bitmap, codec.EncodedOrigin);
        }
        return OperatingSystem.IsWindows() ? Wic.Decode(path) : null;
    }

    static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return bitmap;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height, bitmap.ColorType, bitmap.AlphaType);
        using (var canvas = new SKCanvas(result))
        {
            canvas.SetMatrix(origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, bitmap.Width, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, bitmap.Width, 0, -1, bitmap.Height, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, bitmap.Height, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, bitmap.Height, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, bitmap.Height, -1, 0, bitmap.Width, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, bitmap.Width, 0, 0, 1),
                _ => SKMatrix.Identity,
            });
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        bitmap.Dispose();
        return result;
    }

    /// Windows Imaging Component, called through its COM vtables (no generated interop, works in a trimmed single file).
    static unsafe class Wic
    {
        static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static readonly Guid FactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
        static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
        const uint GenericRead = 0x80000000, ClsctxInproc = 1, CoinitMultithreaded = 0;

        [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr result);

        static void** VTable(IntPtr obj) => *(void***)obj;
        static void Release(IntPtr obj)
        {
            if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)VTable(obj)[2])(obj);
        }

        public static SKBitmap? Decode(string path)
        {
            CoInitializeEx(IntPtr.Zero, CoinitMultithreaded); // fails harmlessly if the thread is already initialised
            IntPtr factory = 0, decoder = 0, frame = 0, converter = 0;
            try
            {
                if (CoCreateInstance(FactoryClass, 0, ClsctxInproc, FactoryInterface, out factory) < 0) return null;
                fixed (char* name = path)
                {
                    // IWICImagingFactory::CreateDecoderFromFilename
                    var create = (delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, uint, int, IntPtr*, int>)VTable(factory)[3];
                    if (create(factory, name, null, GenericRead, 0, &decoder) < 0) return null;
                }
                // IWICBitmapDecoder::GetFrame
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(decoder)[13])(decoder, 0, &frame) < 0) return null;
                // IWICImagingFactory::CreateFormatConverter, then IWICFormatConverter::Initialize to 32-bit BGRA
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTable(factory)[10])(factory, &converter) < 0) return null;
                var format = Bgra32;
                var initialize = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)VTable(converter)[8];
                if (initialize(converter, frame, &format, 0, 0, 0, 0) < 0) return null;
                // IWICBitmapSource::GetSize and CopyPixels
                uint width, height;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)VTable(converter)[3])(converter, &width, &height) < 0 || width == 0 || height == 0) return null;
                var bitmap = new SKBitmap(new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                var copy = (delegate* unmanaged[Stdcall]<IntPtr, void*, uint, uint, byte*, int>)VTable(converter)[7];
                if (copy(converter, null, (uint)bitmap.RowBytes, (uint)bitmap.ByteCount, (byte*)bitmap.GetPixels()) < 0)
                {
                    bitmap.Dispose();
                    return null;
                }
                return bitmap;
            }
            catch (Exception) { return null; }
            finally
            {
                Release(converter);
                Release(frame);
                Release(decoder);
                Release(factory);
            }
        }
    }
}
