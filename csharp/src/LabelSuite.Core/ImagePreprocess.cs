// 이미지 보정 — 저대비/흐린 인쇄 대응 (2D-Verifier SoftwareDecoder의 스트레칭 이식).
using SkiaSharp;

namespace LabelSuite.Core;

public static class ImagePreprocess
{
    /// <summary>휘도 2%/98% 퍼센타일 대비 스트레칭. 대비가 이미 충분하면 원본 사본 반환.</summary>
    public static SKBitmap StretchContrast(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var histogram = new int[256];
        var luma = new byte[width * height];
        var index = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = source.GetPixel(x, y);
                var v = (byte)((color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000);
                luma[index++] = v;
                histogram[v]++;
            }
        long total = (long)width * height, acc = 0;
        int lo = 0, hi = 255;
        for (var v = 0; v < 256; v++) { acc += histogram[v]; if (acc >= total * 0.02) { lo = v; break; } }
        acc = 0;
        for (var v = 255; v >= 0; v--) { acc += histogram[v]; if (acc >= total * 0.02) { hi = v; break; } }
        if (hi <= lo + 5) return source.Copy();

        var result = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        index = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var stretched = (byte)Math.Clamp((luma[index++] - lo) * 255 / (hi - lo), 0, 255);
                result.SetPixel(x, y, new SKColor(stretched, stretched, stretched));
            }
        return result;
    }
}
