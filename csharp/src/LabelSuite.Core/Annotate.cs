// 하이라이트 렌더링·결과 저장의 단일 구현 (SkiaSharp, 크로스플랫폼).
using SkiaSharp;

namespace LabelSuite.Core;

/// <summary>바운딩 박스 표시 스타일 (설정에서 주입).</summary>
public sealed record OverlayStyle(
    float Thickness = 2f,
    byte FillAlpha = 90,
    bool ShowNumbers = false,
    float NumberFontSize = 22f)
{
    public static readonly OverlayStyle Default = new();
}

public static class Annotate
{
    private static readonly (byte R, byte G, byte B, byte A) FallbackColor = (128, 128, 128, 100);

    /// <summary>매칭 단어 위에 반투명 색 박스를 그린 사본을 반환한다.</summary>
    public static SKBitmap RenderOverlays(
        SKBitmap image, IEnumerable<TextMatch> matches,
        IReadOnlyDictionary<string, (byte R, byte G, byte B, byte A)> colors,
        OverlayStyle? style = null)
    {
        style ??= OverlayStyle.Default;
        var annotated = image.Copy();
        using var canvas = new SKCanvas(annotated);
        using var numberFont = new SKFont(SKTypeface.Default, style.NumberFontSize);
        var number = 0;
        foreach (var match in matches)
        {
            number++;
            var (x, y, w, h) = match.Word.Bbox;
            var color = colors.TryGetValue(match.Field, out var c) ? c : FallbackColor;
            using var fill = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, style.FillAlpha),
                Style = SKPaintStyle.Fill,
            };
            using var stroke = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, 255),
                Style = SKPaintStyle.Stroke, StrokeWidth = style.Thickness,
            };
            // 텍스트에 붙지 않도록 박스를 살짝 바깥으로 (높이 비례 여백)
            var pad = Math.Max(3f, h * 0.12f);
            var rect = new SKRect(x - pad, y - pad, x + w + pad, y + h + pad);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
            if (style.ShowNumbers)
            {
                var label = number.ToString();
                var width = numberFont.MeasureText(label) + 8;
                var height = style.NumberFontSize + 6;
                var badge = new SKRect(rect.Left, Math.Max(0, rect.Top - height),
                                       rect.Left + width, Math.Max(height, rect.Top));
                using var badgeFill = new SKPaint
                { Color = new SKColor(color.R, color.G, color.B, 230), Style = SKPaintStyle.Fill };
                using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
                canvas.DrawRect(badge, badgeFill);
                canvas.DrawText(label, badge.Left + 4, badge.Bottom - 5, numberFont, text);
            }
        }
        return annotated;
    }

    /// <summary>임의 영역에 색 박스 + 라벨 배지를 그린다 (양식 감지 영역 등).</summary>
    public static void DrawTaggedBox(SKBitmap image, (int X, int Y, int W, int H) bbox,
                                     string label, SKColor color,
                                     OverlayStyle? style = null)
    {
        style ??= OverlayStyle.Default;
        using var canvas = new SKCanvas(image);
        using var stroke = new SKPaint
        {
            Color = color, Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, style.Thickness),
        };
        using var font = new SKFont(SKTypeface.Default, 18);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var badgeFill = new SKPaint
        { Color = color.WithAlpha(220), Style = SKPaintStyle.Fill };
        var (x, y, w, h) = bbox;
        const float Pad = 4f;
        var rect = new SKRect(x - Pad, y - Pad, x + w + Pad, y + h + Pad);
        canvas.DrawRect(rect, stroke);
        var labelWidth = font.MeasureText(label) + 10;
        var badge = new SKRect(rect.Left, Math.Max(0, rect.Top - 24),
                               rect.Left + labelWidth, Math.Max(24, rect.Top));
        canvas.DrawRect(badge, badgeFill);
        canvas.DrawText(label, badge.Left + 5, badge.Bottom - 6, font, textPaint);
    }

    /// <summary>검출된 바코드(DataMatrix·GS1-128 등)에 청록 박스 + 종류 라벨을
    /// 그린다. 1D 바코드는 검출점이 선 형태라 세로로 부풀려 표시한다.</summary>
    public static void DrawBarcodeBoxes(SKBitmap image,
                                        IEnumerable<BarcodeHit> hits,
                                        OverlayStyle? style = null)
    {
        style ??= OverlayStyle.Default;
        using var canvas = new SKCanvas(image);
        var teal = BarcodeBoxColor;
        using var stroke = new SKPaint
        {
            Color = teal, Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, style.Thickness),
        };
        using var font = new SKFont(SKTypeface.Default, 18);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var badgeFill = new SKPaint
        { Color = teal.WithAlpha(220), Style = SKPaintStyle.Fill };
        foreach (var hit in hits)
        {
            var (x, y, w, h) = hit.Bbox;
            // 박스는 검출 시 심볼 영역으로 정밀화되어 있음 — 표시 여백만 소폭
            const float Pad = 4f;
            var rect = new SKRect(x - Pad, y - Pad, x + w + Pad, y + h + Pad);
            canvas.DrawRect(rect, stroke);
            var label = hit.Symbology;
            var labelWidth = font.MeasureText(label) + 10;
            var badge = new SKRect(rect.Left, Math.Max(0, rect.Top - 24),
                                   rect.Left + labelWidth, Math.Max(24, rect.Top));
            canvas.DrawRect(badge, badgeFill);
            canvas.DrawText(label, badge.Left + 5, badge.Bottom - 6, font, textPaint);
        }
    }

    /// <summary>저신뢰 OCR 단어를 경고색(주황) 파선 박스로 하이라이트한다.
    /// 원본을 제자리에서 수정한다 (RenderOverlays 결과 위에 겹쳐 그리는 용도).</summary>
    public static void HighlightLowConfidence(SKBitmap image,
                                              IEnumerable<OcrWord> lowWords,
                                              OverlayStyle? style = null)
    {
        style ??= OverlayStyle.Default;
        using var canvas = new SKCanvas(image);
        var orange = LowConfidenceColor;
        using var fill = new SKPaint
        { Color = orange.WithAlpha(45), Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            Color = orange, Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, style.Thickness),
            PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var font = new SKFont(SKTypeface.Default, 16);
        using var text = new SKPaint { Color = orange, IsAntialias = true };
        foreach (var word in lowWords)
        {
            var (x, y, w, h) = word.Bbox;
            var rect = new SKRect(x - 2, y - 2, x + w + 2, y + h + 2);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
            canvas.DrawText($"{word.Confidence}%", rect.Left,
                            Math.Max(14, rect.Top - 4), font, text);
        }
    }

    /// <summary>우상단 필드별 found/expected 요약 박스 (저장본용).</summary>
    /// <summary>오버레이 색 — WPF Theme.xaml의 OverlayBarcodeBrush/OverlayFormBrush/OverlayLowConfBrush와
    /// 동기화된다 (ThemeTokenTests가 hex 일치를 검증). 저장 이미지와 화면 범례가 같은 색을 쓰기 위함.</summary>
    public static readonly SKColor BarcodeBoxColor = new(0, 128, 128);
    public static readonly SKColor FormBoxColor = new(128, 0, 160);
    public static readonly SKColor LowConfidenceColor = new(255, 140, 0);

    public static void DrawSummaryBox(SKBitmap image, InspectionOutcome outcome)
    {
        using var canvas = new SKCanvas(image);
        var lines = new List<(string Text, bool Ok)>
        { ($"[{outcome.Standard.Name}] {(outcome.Passed ? "PASSED" : "CHECK")}", outcome.Passed) };
        // 2번째 줄: 화면 배지의 사유줄과 같은 문구 (저장 이미지만 봐도 왜 확인 필요인지 알 수 있게)
        var reason = InspectionSummary.Describe(outcome);
        if (reason.Length > 0)
            lines.Add((reason.Length > 34 ? reason[..33] + "…" : reason, outcome.Passed));
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
        string path, double scale = 0.5, int quality = 90, OverlayStyle? style = null)
    {
        using var annotated = RenderOverlays(image, outcome.AllMatches, colors, style);
        DrawSummaryBox(annotated, outcome);
        SKBitmap final = annotated;
        if (scale is > 0 and < 1)
            final = annotated.Resize(
                new SKImageInfo((int)(annotated.Width * scale), (int)(annotated.Height * scale)),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? annotated;
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
        // 파일명 줄기 정제: 영숫자·'-'·'_'만, 40자 절단 (PathRules.SanitizeFileStem) — 긴 LOT/REF로
        // 경로 길이 한계를 넘거나 OS 금지 문자가 들어가지 않게. 남는 게 없으면 NOLOT/NOREF.
        static string Safe(string? value, string fallback) =>
            PathRules.SanitizeFileStem(value, 40, fallback);
        var suffix = passed ? "Passed" : "Check";
        return $"{counter:D3}_{Safe(lot, "NOLOT")}_{Safe(refValue, "NOREF")}_" +
               $"{date:yyyyMMdd}_{suffix}.jpg";
    }
}
