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
    public void DetectsMultipleDataMatricesRegardlessOfSizeAndPosition()
    {
        // 같은 값 2개(크기 다름) + 다른 값 1개 — 위치·크기 무관 전부 검출돼야 한다
        using var page = new SKBitmap(1200, 900);
        var placements = new (string Content, int X, int Y, int Size)[]
        {
            ("(01)08806173612345(10)25090776", 100, 100, 120),
            ("(01)08806173612345(10)25090776", 900, 640, 160),   // 동일 값, 다른 위치
            ("(01)08806173612399(10)25100113", 620, 120, 90),    // 다른 값, 소형
        };
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            foreach (var (content, x, y, size) in placements)
            {
                using var symbol = Encode(BarcodeFormat.DATA_MATRIX, content,
                                          size, size);
                canvas.DrawBitmap(symbol, x, y);
            }
        }

        var hits = BarcodeDetector.Detect(page)
            .Where(h => h.Symbology.Contains("DataMatrix")).ToList();
        Assert.Equal(3, hits.Count);
        // 각 배치 위치마다 그 중심을 포함하는 박스가 정확히 하나씩
        foreach (var (content, x, y, size) in placements)
        {
            var hit = Assert.Single(hits, h =>
                x + size / 2 >= h.Bbox.X && x + size / 2 <= h.Bbox.X + h.Bbox.W
                && y + size / 2 >= h.Bbox.Y && y + size / 2 <= h.Bbox.Y + h.Bbox.H);
            Assert.Equal(content, hit.Text);   // 위치별로 올바른 값이 대응
            Assert.InRange(hit.Bbox.W, (int)(size * 0.55), (int)(size * 1.4));
        }
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

    [Fact]
    public void DetectsSmallDataMatrixNearBottomEdgeAmongText()
    {
        // 요청: 하단·소형 심볼 누락 — 텍스트 줄이 가득한 페이지의 우하단 구석 44px 심볼
        using var page = new SKBitmap(1200, 1600);
        const int size = 44;
        const int x = 1120, y = 1540;
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var ink = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            for (var ty = 100; ty < 1500; ty += 40)
                for (var tx = 80; tx < 1000; tx += 18)
                    canvas.DrawRect(new SKRect(tx, ty, tx + 10, ty + 24), ink);
            using var symbol = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612345(10)25090776", size, size);
            canvas.DrawBitmap(symbol, x, y);
        }
        var hits = BarcodeDetector.Detect(page).Where(h => h.Symbology.Contains("DataMatrix")).ToList();
        var hit = Assert.Single(hits);
        Assert.Contains("08806173612345", hit.Text);
        AssertBoxCovers(hit.Bbox, x, y, size, size);
    }

    [Fact]
    public void DetectsDataMatrixTouchingLineAndText()
    {
        // 요청: 테두리 선·텍스트가 심볼 콰이엇 존에 맞닿은 경우 — 두 심볼 모두 검출되고 박스가 선으로 번지지 않아야 한다
        using var page = new SKBitmap(1000, 900);
        const int size = 100;
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var ink = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            using var symbol = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612345(10)25090776", size, size);
            canvas.DrawBitmap(symbol, 300, 300);
            canvas.DrawRect(new SKRect(0, 300 + size - 3, 1000, 300 + size + 1), ink);   // 심볼 하단 여백을 파고든 가로선
            canvas.DrawRect(new SKRect(300 + size - 2, 100, 300 + size + 2, 500), ink);   // 우측 여백에 붙은 세로선
            using var symbol2 = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612399(10)25100113", size, size);
            canvas.DrawBitmap(symbol2, 700, 600);
            for (var tx = 560; tx <= 700 - 8; tx += 14)                                  // 좌측에 붙은 텍스트 줄 모사
                canvas.DrawRect(new SKRect(tx, 620, tx + 8, 680), ink);
            canvas.DrawRect(new SKRect(698, 600, 704, 700), ink);                          // 좌측 여백을 파고든 세로선
        }
        var hits = BarcodeDetector.Detect(page).Where(h => h.Symbology.Contains("DataMatrix")).ToList();
        Assert.Equal(2, hits.Count);
        var first = Assert.Single(hits, h => h.Text.Contains("08806173612345"));
        AssertBoxCovers(first.Bbox, 300, 300, size, size);
        var second = Assert.Single(hits, h => h.Text.Contains("08806173612399"));
        AssertBoxCovers(second.Bbox, 700, 600, size, size);
    }

    /// <summary>로케이터 경로만으로(전체 페이지 DecodeMultiple 없이) 검출돼야 하는 경우 — 실제 라벨 렌더처럼
    /// 큰 이미지에서는 DecodeMultiple이 자주 놓치므로 이 경로가 재현율을 좌우한다.</summary>
    [Fact]
    public void LocatorPathFindsSmallCornerSymbolAndSymbolsTouchingFrameLines()
    {
        using var page = new SKBitmap(1400, 1800);
        const int small = 40;
        const int big = 100;
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var ink = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            // 라벨 테두리 사각형(프레임) — DataMatrix 하나가 프레임 안쪽 모서리에 콰이엇 존 없이 붙어 있다
            using var frame = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 5 };
            canvas.DrawRect(new SKRect(200, 200, 1200, 1100), frame);
            using var symbolA = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612345(10)25090776", big, big);
            canvas.DrawBitmap(symbolA, 1200 - big - 1, 1100 - big - 1);   // 우하단 모서리, 프레임 선과 맞닿음
            // 텍스트 줄들 (프레임 안)
            for (var ty = 260; ty < 1000; ty += 40)
                for (var tx = 260; tx < 1000; tx += 18)
                    canvas.DrawRect(new SKRect(tx, ty, tx + 10, ty + 24), ink);
            // 페이지 하단 구석의 소형 심볼 — 텍스트 줄 바로 옆
            using var symbolB = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612399(10)25100113", small, small);
            canvas.DrawBitmap(symbolB, 1340, 1740);
            for (var tx = 900; tx < 1336; tx += 18)
                canvas.DrawRect(new SKRect(tx, 1748, tx + 10, 1772), ink);
        }
        var candidates = DataMatrixLocator.FindCandidates(page);
        // 프레임과 붙은 심볼: 프레임 전체가 아니라 심볼 크기의 후보가 있어야 한다
        var dump = "candidates: " + string.Join(" ", candidates.Select(c => $"({c.X},{c.Y},{c.W}x{c.H})"));
        Assert.True(candidates.Any(c => Contains(c, 1200 - big / 2, 1100 - big / 2) && c.W <= big * 1.6 && c.H <= big * 1.6),
                    "프레임에 붙은 심볼 후보 없음 — " + dump);
        Assert.True(candidates.Any(c => Contains(c, 1340 + small / 2, 1740 + small / 2) && c.W <= small * 2.2),
                    "하단 구석 소형 심볼 후보 없음 — " + dump);

        var hits = BarcodeDetector.DetectDataMatrixByLocator(page);
        var hitDump = "hits: " + string.Join(" ", hits.Select(h => $"{h.Text}@({h.Bbox.X},{h.Bbox.Y},{h.Bbox.W}x{h.Bbox.H})"));
        Assert.True(hits.Any(h => h.Text.Contains("08806173612345")), "프레임에 붙은 심볼 미검출 — " + hitDump);
        Assert.True(hits.Any(h => h.Text.Contains("08806173612399")), "하단 구석 소형 심볼 미검출 — " + hitDump);
        var a = Assert.Single(hits, h => h.Text.Contains("08806173612345"));
        AssertBoxCovers(a.Bbox, 1200 - big - 1, 1100 - big - 1, big, big);
        var b = Assert.Single(hits, h => h.Text.Contains("08806173612399"));
        AssertBoxCovers(b.Bbox, 1340, 1740, small, small);
    }

    private static bool Contains((int X, int Y, int W, int H) rect, int px, int py) =>
        px >= rect.X && px <= rect.X + rect.W && py >= rect.Y && py <= rect.Y + rect.H;

    [Fact]
    public void DecodeAtReturnsTheSymbolUnderTheCursor()
    {
        using var page = new SKBitmap(1400, 1000);
        using (var canvas = new SKCanvas(page))
        {
            canvas.Clear(SKColors.White);
            using var dataMatrix = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612345(10)25090776", 120, 120);
            canvas.DrawBitmap(dataMatrix, 200, 200);
            using var code128 = Encode(BarcodeFormat.CODE_128, "0108806173612399", 400, 90);
            canvas.DrawBitmap(code128, 700, 700);
        }
        var dm = BarcodeDetector.DecodeAt(page, 260, 260);
        Assert.NotNull(dm);
        Assert.Contains("DataMatrix", dm!.Symbology);
        Assert.Contains("08806173612345", dm.Text);
        AssertBoxCovers(dm.Bbox, 200, 200, 120, 120);

        var c128 = BarcodeDetector.DecodeAt(page, 900, 745);
        Assert.NotNull(c128);
        Assert.Contains("128", c128!.Symbology);
        Assert.Contains("08806173612399", c128.Text);

        // 심볼에서 조금 벗어난 클릭도 가장 가까운 심볼로
        Assert.Contains("08806173612345", BarcodeDetector.DecodeAt(page, 340, 330)!.Text);
        // 빈 곳(모든 심볼에서 멀리)은 null
        Assert.Null(BarcodeDetector.DecodeAt(page, 1300, 100));
    }
}
