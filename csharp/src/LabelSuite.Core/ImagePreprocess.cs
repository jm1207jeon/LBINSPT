// 이미지 보정 — 저대비/흐린 인쇄 대응 (2D-Verifier SoftwareDecoder의 스트레칭 이식)
// + 라벨 정렬(하늘색 이형지 기준 크롭·기울기 보정).
using SkiaSharp;

namespace LabelSuite.Core;

public sealed record LabelAlignResult(SKBitmap Image, double AngleDegrees, bool Cropped);

public static class ImagePreprocess
{
    /// <summary>하늘색 이형지(라벨 용지) 영역을 찾아 (1) 기울기를 보정하고
    /// (2) 이형지 바깥 흰 배경을 잘라낸다 → 라벨 위치·크기·각도가 일정해진다.
    /// 이형지가 감지되지 않으면 원본을 그대로 반환한다 (Cropped=false).</summary>
    public static LabelAlignResult DeskewAndCropLiner(SKBitmap source)
    {
        const int Step = 2;              // 서브샘플링 간격 (속도)
        const double MinAreaRatio = 0.02; // 이형지가 이보다 작으면 미감지로 간주
        const double MaxAngle = 10.0;     // 이보다 크면 오검출로 보고 회전 생략

        var stats = ScanLiner(source, Step);
        long sampled = (long)((source.Width + Step - 1) / Step)
                     * ((source.Height + Step - 1) / Step);
        if (stats.Count < sampled * MinAreaRatio)
            return new LabelAlignResult(source, 0, false);

        // 2차 모멘트 주축으로 기울기 추정 (수평/수직 축 기준 -45~45도)
        var angle = PrincipalAngle(stats);
        SKBitmap working = source;
        var rotated = false;
        if (Math.Abs(angle) >= 0.3 && Math.Abs(angle) <= MaxAngle)
        {
            working = Rotate(source, -angle);
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
        public double SumX, SumY, SumXX, SumYY, SumXY;
    }

    /// <summary>하늘색(파랑 우세) 픽셀 통계 — 이형지 감지용.</summary>
    private static LinerStats ScanLiner(SKBitmap image, int step)
    {
        var stats = new LinerStats();
        for (var y = 0; y < image.Height; y += step)
            for (var x = 0; x < image.Width; x += step)
            {
                var c = image.GetPixel(x, y);
                // 하늘색: 파랑이 충분히 밝고 빨강보다 뚜렷이 큼, 초록도 빨강 이상
                if (c.Blue < 110 || c.Blue <= c.Red + 18 || c.Green + 6 < c.Red)
                    continue;
                stats.Count++;
                if (x < stats.MinX) stats.MinX = x;
                if (y < stats.MinY) stats.MinY = y;
                if (x > stats.MaxX) stats.MaxX = x;
                if (y > stats.MaxY) stats.MaxY = y;
                stats.SumX += x; stats.SumY += y;
                stats.SumXX += (double)x * x;
                stats.SumYY += (double)y * y;
                stats.SumXY += (double)x * y;
            }
        return stats;
    }

    private static double PrincipalAngle(LinerStats s)
    {
        if (s.Count < 16) return 0;
        var meanX = s.SumX / s.Count;
        var meanY = s.SumY / s.Count;
        var mu20 = s.SumXX / s.Count - meanX * meanX;
        var mu02 = s.SumYY / s.Count - meanY * meanY;
        var mu11 = s.SumXY / s.Count - meanX * meanY;
        var theta = 0.5 * Math.Atan2(2 * mu11, mu20 - mu02) * 180 / Math.PI;
        // 주축을 가장 가까운 수평/수직 축 기준 편차로 정규화
        while (theta > 45) theta -= 90;
        while (theta < -45) theta += 90;
        return theta;
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
