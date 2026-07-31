// SKBitmap ↔ WPF BitmapSource 변환.
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace LabelSuite.App.Services;

public static class SkiaWpf
{
    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        SKBitmap source = bitmap;
        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            source = new SKBitmap(bitmap.Width, bitmap.Height,
                                  SKColorType.Bgra8888, SKAlphaType.Premul);
            bitmap.CopyTo(source, SKColorType.Bgra8888);
        }
        var writeable = new WriteableBitmap(source.Width, source.Height, 96, 96,
                                            PixelFormats.Bgra32, null);
        writeable.Lock();
        try
        {
            unsafe
            {
                Buffer.MemoryCopy((void*)source.GetPixels(),
                                  (void*)writeable.BackBuffer,
                                  (long)writeable.BackBufferStride * source.Height,
                                  (long)source.RowBytes * source.Height);
            }
            writeable.AddDirtyRect(new System.Windows.Int32Rect(
                0, 0, source.Width, source.Height));
        }
        finally { writeable.Unlock(); }
        if (!ReferenceEquals(source, bitmap)) source.Dispose();
        writeable.Freeze();
        return writeable;
    }
}
