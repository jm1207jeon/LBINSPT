// 하이라이트 렌더링·결과 저장의 단일 구현 (SkiaSharp, 크로스플랫폼).
using SkiaSharp;

namespace LabelSuite.Core;

public static class Annotate
{
    private static readonly (byte R, byte G, byte B, byte A) FallbackColor = (128, 128, 128, 100);

    /// <summary>매칭 단어 위에 반투명 색 박스를 그린 사본을 반환한다.</summary>
    public static SKBitmap RenderOverlays(
        SKBitmap image, IEnumerable<TextMatch> matches,
        IReadOnlyDictionary<string, (byte R, byte G, byte B, byte A)> colors)
    {
        var annotated = image.Copy();
        using var canvas = new SKCanvas(annotated);
        foreach (var match in matches)
        {
            var (x, y, w, h) = match.Word.Bbox;
            var color = colors.TryGetValue(match.Field, out var c) ? c : FallbackColor;
            using var fill = new SKPaint
            { Color = new SKColor(color.R, color.G, color.B, 90), Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, 255),
                Style = SKPaintStyle.Stroke, StrokeWidth = 2,
            };
            var rect = new SKRect(x, y, x + w, y + h);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
        }
        return annotated;
    }

    /// <summary>우상단 필드별 found/expected 요약 박스 (저장본용).</summary>
    public static void DrawSummaryBox(SKBitmap image, InspectionOutcome outcome)
    {
        using var canvas = new SKCanvas(image);
        var lines = new List<(string Text, bool Ok)>
        { ($"[{outcome.Standard.Name}] {(outcome.Passed ? "PASSED" : "CHECK")}", outcome.Passed) };
        foreach (var field in outcome.Fields.Values.Where(f => f.Expected is not null))
            lines.Add(($"{field.Field}: {field.Found}/{field.Expected} " +
                       (field.Passed ? "OK" : "NG"), field.Passed));

        const int lineHeight = 28;
        const int boxWidth = 260;
        var boxHeight = lineHeight * lines.Count + 16;
        var x0 = Math.Max(0, image.Width - boxWidth - 10);
        const int y0 = 10;
        using var background = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var border = new SKPaint
        { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(x0, y0, boxWidth, boxHeight, background);
        canvas.DrawRect(x0, y0, boxWidth, boxHeight, border);
        using var font = new SKFont(SKTypeface.Default, 18);
        for (var i = 0; i < lines.Count; i++)
        {
            using var paint = new SKPaint
            {
                Color = lines[i].Ok ? new SKColor(0, 130, 0) : new SKColor(200, 0, 0),
                IsAntialias = true,
            };
            canvas.DrawText(lines[i].Text, x0 + 8, y0 + lineHeight * (i + 1), font, paint);
        }
    }

    /// <summary>오버레이+요약 박스를 넣은 결과 JPEG 저장.</summary>
    public static void SaveAnnotatedJpeg(
        SKBitmap image, InspectionOutcome outcome,
        IReadOnlyDictionary<string, (byte R, byte G, byte B, byte A)> colors,
        string path, double scale = 0.5, int quality = 90)
    {
        using var annotated = RenderOverlays(image, outcome.AllMatches, colors);
        DrawSummaryBox(annotated, outcome);
        SKBitmap final = annotated;
        if (scale is > 0 and < 1)
            final = annotated.Resize(
                new SKImageInfo((int)(annotated.Width * scale), (int)(annotated.Height * scale)),
                SKFilterQuality.Medium) ?? annotated;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var skImage = SKImage.FromBitmap(final);
        using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        if (!ReferenceEquals(final, annotated)) final.Dispose();
    }

    /// <summary>###_LOT_REF_YYYYMMDD_Passed|_Check.jpg</summary>
    public static string MakeResultFilename(int counter, string? lot, string? refValue,
                                            bool passed, DateOnly? when = null)
    {
        var date = when ?? DateOnly.FromDateTime(DateTime.Today);
        static string Safe(string? value, string fallback)
        {
            var cleaned = new string((value ?? "")
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            return cleaned.Length > 0 ? cleaned : fallback;
        }
        var suffix = passed ? "Passed" : "Check";
        return $"{counter:D3}_{Safe(lot, "NOLOT")}_{Safe(refValue, "NOREF")}_" +
               $"{date:yyyyMMdd}_{suffix}.jpg";
    }
}
