// 이미지 보정 — 저대비/흐린 인쇄 대응 (2D-Verifier SoftwareDecoder의 스트레칭 이식)
// + 라벨 정렬(하늘색 이형지 기준 크롭 · 본 라벨 잉크의 수평선 기준 기울기 보정).
// 대형 스캔 이미지(수천만 픽셀)에서도 빠르도록 픽셀 버퍼에 포인터로 직접 접근한다.
using SkiaSharp;

namespace LabelSuite.Core;

public sealed record LabelAlignResult(SKBitmap Image, double AngleDegrees, bool Cropped);

public static class ImagePreprocess
{
    // ---------------- 픽셀 고속 접근 ----------------

    /// <summary>휘도(0~255) 버퍼. BGRA/RGBA 8888이면 포인터로 고속 계산,
    /// 그 외 색 형식은 GetPixel 폴백.</summary>
    internal static byte[] LumaBuffer(SKBitmap bmp)
    {
        var width = bmp.Width;
        var height = bmp.Height;
        var luma = new byte[width * height];
        if (bmp.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888)
        {
            var redOffset = bmp.ColorType == SKColorType.Bgra8888 ? 2 : 0;
            var blueOffset = bmp.ColorType == SKColorType.Bgra8888 ? 0 : 2;
            unsafe
            {
                var basePtr = (byte*)bmp.GetPixels().ToPointer();
                var rowBytes = bmp.RowBytes;
                var index = 0;
                for (var y = 0; y < height; y++)
                {
                    var row = basePtr + (long)y * rowBytes;
                    for (var x = 0; x < width; x++)
                    {
                        var p = row + x * 4;
                        luma[index++] = (byte)((p[redOffset] * 299 + p[1] * 587
                                                + p[blueOffset] * 114) / 1000);
                    }
                }
            }
        }
        else
        {
            var index = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    luma[index++] = (byte)((c.Red * 299 + c.Green * 587
                                            + c.Blue * 114) / 1000);
                }
        }
        return luma;
    }

    private static (byte R, byte G, byte B) ReadPixel(SKBitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        return (c.Red, c.Green, c.Blue);
    }

    // ---------------- 대비 스트레칭 ----------------

    /// <summary>휘도 2%/98% 퍼센타일 대비 스트레칭. 대비가 이미 충분하면 원본 사본 반환.</summary>
    public static SKBitmap StretchContrast(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var luma = LumaBuffer(source);
        var histogram = new int[256];
        foreach (var v in luma) histogram[v]++;
        long total = (long)width * height, acc = 0;
        int lo = 0, hi = 255;
        for (var v = 0; v < 256; v++) { acc += histogram[v]; if (acc >= total * 0.02) { lo = v; break; } }
        acc = 0;
        for (var v = 255; v >= 0; v--) { acc += histogram[v]; if (acc >= total * 0.02) { hi = v; break; } }
        if (hi <= lo + 5) return source.Copy();

        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        unsafe
        {
            var basePtr = (byte*)result.GetPixels().ToPointer();
            var rowBytes = result.RowBytes;
            var index = 0;
            var scale = 255.0 / (hi - lo);
            for (var y = 0; y < height; y++)
            {
                var row = basePtr + (long)y * rowBytes;
                for (var x = 0; x < width; x++)
                {
                    var stretched = (byte)Math.Clamp((int)((luma[index++] - lo) * scale), 0, 255);
                    var p = row + x * 4;
                    p[0] = stretched; p[1] = stretched; p[2] = stretched; p[3] = 255;
                }
            }
        }
        return result;
    }

    // ---------------- 라벨 정렬 (이형지 크롭 + 잉크 기준 기울기 보정) ----------------

    /// <summary>(1) 본 라벨 잉크(텍스트·수평선)를 기준으로 기울기를 보정하고
    /// (2) 하늘색 이형지 바깥의 흰 배경을 잘라낸다 → 라벨 위치·크기·각도 일정화.
    /// 이형지가 감지되지 않으면 원본을 그대로 반환한다 (Cropped=false).</summary>
    public static LabelAlignResult DeskewAndCropLiner(SKBitmap source)
    {
        const int Step = 2;               // 이형지 스캔 서브샘플링 간격
        const double MinAreaRatio = 0.02; // 이형지가 이보다 작으면 미감지로 간주
        const double MaxAngle = 10.0;     // 이보다 크면 오검출로 보고 회전 생략

        var stats = ScanLiner(source, Step);
        long sampled = (long)((source.Width + Step - 1) / Step)
                     * ((source.Height + Step - 1) / Step);
        if (stats.Count < sampled * MinAreaRatio)
            return new LabelAlignResult(source, 0, false);

        // 기울기: 이형지가 아니라 '본 라벨의 잉크'(텍스트 행·수평선) 기준으로 추정
        var angle = EstimateSkewFromInk(source,
            (stats.MinX, stats.MinY, stats.MaxX, stats.MaxY));
        SKBitmap working = source;
        var rotated = false;
        if (Math.Abs(angle) >= 0.3 && Math.Abs(angle) <= MaxAngle)
        {
            working = Rotate(source, angle);
            rotated = true;
            stats = ScanLiner(working, Step);
            if (stats.Count == 0)
            {
                working.Dispose();
                return new LabelAlignResult(source, 0, false);
            }
        }
        else angle = 0;

        // 이형지 바깥 여백 제거 (약간의 마진 포함)
        var margin = Math.Max(4, Math.Min(working.Width, working.Height) / 200);
        var x0 = Math.Max(0, stats.MinX - margin);
        var y0 = Math.Max(0, stats.MinY - margin);
        var x1 = Math.Min(working.Width, stats.MaxX + margin);
        var y1 = Math.Min(working.Height, stats.MaxY + margin);
        if (x1 - x0 < 20 || y1 - y0 < 20)
        {
            if (rotated) working.Dispose();
            return new LabelAlignResult(source, 0, false);
        }
        var cropped = new SKBitmap(x1 - x0, y1 - y0, source.ColorType, source.AlphaType);
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(working, new SKRect(x0, y0, x1, y1),
                              new SKRect(0, 0, x1 - x0, y1 - y0));
        if (rotated) working.Dispose();
        return new LabelAlignResult(cropped, angle, true);
    }

    private sealed class LinerStats
    {
        public long Count;
        public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX, MaxY;
    }

    /// <summary>하늘색(파랑 우세) 픽셀 통계 — 이형지 감지용.</summary>
    private static LinerStats ScanLiner(SKBitmap image, int step)
    {
        var stats = new LinerStats();
        void Accumulate(int x, int y, byte r, byte g, byte b)
        {
            // 하늘색: 파랑이 충분히 밝고 빨강보다 뚜렷이 큼, 초록도 빨강 이상
            if (b < 110 || b <= r + 18 || g + 6 < r) return;
            stats.Count++;
            if (x < stats.MinX) stats.MinX = x;
            if (y < stats.MinY) stats.MinY = y;
            if (x > stats.MaxX) stats.MaxX = x;
            if (y > stats.MaxY) stats.MaxY = y;
        }
        if (image.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888)
        {
            var redOffset = image.ColorType == SKColorType.Bgra8888 ? 2 : 0;
            var blueOffset = image.ColorType == SKColorType.Bgra8888 ? 0 : 2;
            unsafe
            {
                var basePtr = (byte*)image.GetPixels().ToPointer();
                var rowBytes = image.RowBytes;
                for (var y = 0; y < image.Height; y += step)
                {
                    var row = basePtr + (long)y * rowBytes;
                    for (var x = 0; x < image.Width; x += step)
                    {
                        var p = row + x * 4;
                        Accumulate(x, y, p[redOffset], p[1], p[blueOffset]);
                    }
                }
            }
        }
        else
        {
            for (var y = 0; y < image.Height; y += step)
                for (var x = 0; x < image.Width; x += step)
                {
                    var (r, g, b) = ReadPixel(image, x, y);
                    Accumulate(x, y, r, g, b);
                }
        }
        return stats;
    }

    /// <summary>본 라벨 잉크(어두운 픽셀)의 가로 투영 선명도로 기울기 추정 —
    /// 텍스트 행/수평선이 수평이 되는 보정각을 찾는다. 반환: 적용할 회전각(도).</summary>
    internal static double EstimateSkewFromInk(SKBitmap image,
        (int X0, int Y0, int X1, int Y1) roi)
    {
        const byte InkThreshold = 115;   // 하늘색(휘도 ~185)·흰 배경 제외, 검정 잉크만
        var luma = LumaBuffer(image);
        var width = image.Width;

        var x0 = Math.Max(0, roi.X0);
        var y0 = Math.Max(0, roi.Y0);
        var x1 = Math.Min(width, roi.X1);
        var y1 = Math.Min(image.Height, roi.Y1);
        var area = (long)Math.Max(1, x1 - x0) * Math.Max(1, y1 - y0);
        var step = Math.Max(2, (int)Math.Sqrt(area / 60000.0));

        var xs = new List<int>();
        var ys = new List<int>();
        for (var y = y0; y < y1; y += step)
            for (var x = x0; x < x1; x += step)
                if (luma[y * width + x] < InkThreshold)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
        if (xs.Count < 300) return 0;

        double Score(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            var sin = Math.Sin(radians);
            var cos = Math.Cos(radians);
            const int Bin = 3;
            var bins = new Dictionary<int, int>();
            for (var i = 0; i < xs.Count; i++)
            {
                var projected = (int)Math.Round((xs[i] * sin + ys[i] * cos) / Bin);
                bins[projected] = bins.TryGetValue(projected, out var n) ? n + 1 : 1;
            }
            double score = 0;
            foreach (var count in bins.Values) score += (double)count * count;
            return score;
        }

        var best = 0.0;
        var bestScore = Score(0);
        for (var a = -6.0; a <= 6.0; a += 0.5)
        {
            if (Math.Abs(a) < 0.01) continue;
            var s = Score(a);
            if (s > bestScore) { bestScore = s; best = a; }
        }
        for (var a = best - 0.4; a <= best + 0.4; a += 0.1)
        {
            var s = Score(a);
            if (s > bestScore) { bestScore = s; best = a; }
        }
        // 0도 대비 유의미하게 선명해질 때만 보정 (노이즈 방지)
        return bestScore > Score(0) * 1.03 ? best : 0;
    }

    private static SKBitmap Rotate(SKBitmap source, double degrees)
    {
        var result = new SKBitmap(source.Width, source.Height,
                                  source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.Translate(source.Width / 2f, source.Height / 2f);
        canvas.RotateDegrees((float)degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
