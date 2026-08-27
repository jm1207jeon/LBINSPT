// 바코드 검출 종단 테스트 — ZXing 인코더로 실제 DataMatrix/Code128을 생성해
// 큰 페이지에 배치하고, 검출값·심볼로지·바운딩 박스 정합까지 검증한다.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace LabelSuite.Core.Tests;

public class BarcodeDetectorEndToEndTests
{
    private static SKBitmap Encode(BarcodeFormat format, string content,
                                   int width, int height)
    {
        var writer = new ZXing.SkiaSharp.BarcodeWriter
        {
            Format = format,
            Options = new EncodingOptions
            { Width = width, Height = height, Margin = 1, PureBarcode = true },
        };
        return writer.Write(content);
    }

    private static void AssertBoxCovers((int X, int Y, int W, int H) bbox,
                                        int x, int y, int w, int h)
    {
        // 그린 심볼의 중심이 박스 안에 있고, 크기가 실제와 비슷해야 한다
        var cx = x + w / 2;
        var cy = y + h / 2;
        Assert.InRange(cx, bbox.X, bbox.X + bbox.W);
        Assert.InRange(cy, bbox.Y, bbox.Y + bbox.H);
        Assert.InRange(bbox.W, (int)(w * 0.55), (int)(w * 1.4));
        Assert.InRange(bbox.H, (int)(h * 0.45), (int)(h * 1.4));
    }

    [Fact]
    public void DetectsDataMatrixAndCode128WithAccurateBoxes()
    {
        using var page = new SKBitmap(1000, 800);
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var dataMatrix = Encode(BarcodeFormat.DATA_MATRIX,
                "(01)08806173612345(10)25090776", 120, 120);
            canvas.DrawBitmap(dataMatrix, 620, 480);
            using var code128 = Encode(BarcodeFormat.CODE_128,
                "0108806173612345", 400, 90);
            canvas.DrawBitmap(code128, 100, 620);
        }

        var hits = BarcodeDetector.Detect(page);

        var dmHit = Assert.Single(hits, h => h.Symbology.Contains("DataMatrix"));
        Assert.Contains("08806173612345", dmHit.Text);
        AssertBoxCovers(dmHit.Bbox, 620, 480, 120, 120);

        var code128Hit = Assert.Single(hits, h => h.Symbology.Contains("128"));
        AssertBoxCovers(code128Hit.Bbox, 100, 620, 400, 90);
    }

    [Fact]
    public void LocatorFindsDataMatrixCandidateOnComposedPage()
    {
        using var page = new SKBitmap(1000, 800);
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var dataMatrix = Encode(BarcodeFormat.DATA_MATRIX,
                "(01)08806173612345(10)25090776", 120, 120);
            canvas.DrawBitmap(dataMatrix, 620, 480);
            // 텍스트 줄 모사 (가로로 긴 어두운 조각들 — 후보에서 배제돼야 함)
            using var ink = new SKPaint
            { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            for (var x = 80; x < 500; x += 18)
                canvas.DrawRect(new SKRect(x, 100, x + 10, 128), ink);
        }
        var candidates = DataMatrixLocator.FindCandidates(page);
        Assert.Contains(candidates, c =>
            680 >= c.X && 680 <= c.X + c.W && 540 >= c.Y && 540 <= c.Y + c.H);
    }
}
