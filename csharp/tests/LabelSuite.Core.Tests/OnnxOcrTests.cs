// ONNX(RapidOCR) 엔진 실추론 테스트 — 모델 로드부터 인식까지 전 구간 검증.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class OnnxOcrTests
{
    [Fact]
    public async Task RecognizesRenderedLabelText()
    {
        using var typeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold)
            ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 42);
        var bmp = new SKBitmap(560, 200);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("LOT 25090776", 40, 80, font, paint);
            canvas.DrawText("REF NCN20-080", 40, 150, font, paint);
        }

        using var engine = new OnnxOcrEngine();
        var words = await engine.DetectWordsAsync(bmp);
        var all = string.Join(" ", words.Select(w => w.Text));
        Assert.Contains("25090776", all);
        Assert.Contains("NCN20", all.Replace(" ", ""));
        Assert.All(words, w => Assert.True(w.Bbox.W > 0 && w.Bbox.H > 0));
        bmp.Dispose();
    }

    [Fact]
    public void SplitLineFallbackDistributesBoxes()
    {
        var words = OnnxOcrEngine.SplitLineToWords("LOT 25090776", (100, 10, 240, 30), 90);
        Assert.Equal(2, words.Count);
        Assert.Equal("LOT", words[0].Text);
        Assert.Equal("25090776", words[1].Text);
        Assert.True(words[1].Bbox.X > words[0].Bbox.X);
        Assert.True(words[1].Bbox.W > words[0].Bbox.W);
    }
}
