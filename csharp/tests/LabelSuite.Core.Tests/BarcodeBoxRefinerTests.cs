// 바코드 박스 정밀화 — ZXing 검출점(선/모서리)을 실제 심볼 영역으로 확장 검증.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class BarcodeBoxRefinerTests
{
    private static SKBitmap Canvas(int w = 800, int h = 500)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        return bmp;
    }

    [Fact]
    public void OneDimensionalSeedLineExpandsToFullBars()
    {
        using var image = Canvas();
        using (var canvas = new SKCanvas(image))
        using (var ink = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill })
        {
            // GS1-128 모사: x 100~500, y 150~230 세로 막대 (4px 바 + 4px 공백)
            for (var x = 100; x < 500; x += 8)
                canvas.DrawRect(new SKRect(x, 150, x + 4, 230), ink);
            // 바코드 아래 HRI 텍스트 모사 (y 250~262) — 여기까지 번지면 안 됨
            for (var x = 120; x < 460; x += 14)
                canvas.DrawRect(new SKRect(x, 250, x + 7, 262), ink);
        }
        // ZXing 1D 검출점: 시작~정지 패턴 중심을 잇는 높이 0의 선
        var refined = BarcodeBoxRefiner.Refine(image, (110, 190, 380, 1),
                                               oneDimensional: true);
        Assert.InRange(refined.Y, 140, 152);                    // 막대 상단
        Assert.InRange(refined.Y + refined.H, 228, 242);        // 막대 하단 (HRI 제외)
        Assert.InRange(refined.X, 90, 104);                     // 첫 막대
        Assert.InRange(refined.X + refined.W, 496, 512);        // 마지막 막대
    }

    [Fact]
    public void DataMatrixCornerSeedExpandsToFullSymbol()
    {
        using var image = Canvas();
        using (var canvas = new SKCanvas(image))
        using (var ink = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill })
        {
            // DataMatrix 모사: 300~420 정방형, 10px 모듈, 솔리드 L 보더 + 체커 내부
            canvas.DrawRect(new SKRect(300, 300, 310, 420), ink);   // 왼쪽 L
            canvas.DrawRect(new SKRect(300, 410, 420, 420), ink);   // 아래 L
            for (var my = 0; my < 11; my++)
                for (var mx = 1; mx < 12; mx++)
                    if ((mx + my) % 2 == 0)
                        canvas.DrawRect(new SKRect(300 + mx * 10, 300 + my * 10,
                                                   310 + mx * 10, 310 + my * 10), ink);
        }
        // 파인더 모서리 근사 시드 (심볼보다 작음)
        var refined = BarcodeBoxRefiner.Refine(image, (330, 330, 60, 60),
                                               oneDimensional: false);
        Assert.InRange(refined.X, 292, 304);
        Assert.InRange(refined.Y, 292, 304);
        Assert.InRange(refined.X + refined.W, 416, 428);
        Assert.InRange(refined.Y + refined.H, 416, 428);
    }

    [Fact]
    public void BlankImageKeepsSeed()
    {
        using var image = Canvas(200, 200);
        var seed = (50, 50, 40, 40);
        Assert.Equal(seed, BarcodeBoxRefiner.Refine(image, seed,
                                                    oneDimensional: false));
    }
}
