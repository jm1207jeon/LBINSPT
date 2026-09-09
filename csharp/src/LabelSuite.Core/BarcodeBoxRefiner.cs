// 바코드 바운딩 박스 정밀화 — ZXing 검출점은 심볼 전체가 아니라
// 파인더/시작·정지 패턴의 점 몇 개라서 그대로 쓰면 박스가 어긋난다.
// (1D는 높이 0의 '선', DataMatrix는 모서리 근사) 검출점 사각형을 시드로 삼아
// 이미지의 어두운 픽셀 분포를 따라 실제 심볼 영역까지 확장한다.
//
// 확장 규칙(라벨 심볼 구조 이용):
//  · 1D 세로: 막대는 세로로 연속 → 빈 줄을 만나면 즉시 멈춤 (HRI 텍스트로 번짐 방지)
//  · 1D 가로: 막대/공백이 교차 → 최대 4모듈 공백까지 건너뛰며 확장
//  · 2D(DataMatrix): L 보더 덕에 심볼 안 모든 행/열에 어두운 픽셀 존재 →
//    빈 줄(콰이엇 존)을 만나면 멈춤
using SkiaSharp;

namespace LabelSuite.Core;

public static class BarcodeBoxRefiner
{
    /// <summary>ZXing 시드 박스를 실제 심볼 영역으로 확장한다.
    /// oneDimensional=true면 세로 막대(GS1-128 등), 아니면 정방 모듈(DataMatrix·QR).</summary>
    public static (int X, int Y, int W, int H) Refine(
        SKBitmap image, (int X, int Y, int W, int H) seed, bool oneDimensional,
        int? maxGrowth = null)
    {
        if (image.Width < 8 || image.Height < 8) return seed;
        var luma = ImagePreprocess.LumaBuffer(image);
        var width = image.Width;
        var height = image.Height;
        var threshold = DarkThreshold(luma);

        var x0 = Math.Clamp(seed.X, 0, width - 1);
        var y0 = Math.Clamp(seed.Y, 0, height - 1);
        var x1 = Math.Clamp(seed.X + Math.Max(1, seed.W), x0 + 1, width);
        var y1 = Math.Clamp(seed.Y + Math.Max(1, seed.H), y0 + 1, height);
        var seedWidth = x1 - x0;

        double RowDark(int y, int fromX, int toX)
        {
            int dark = 0;
            var row = y * width;
            for (var x = fromX; x < toX; x++)
                if (luma[row + x] < threshold) dark++;
            return (double)dark / Math.Max(1, toX - fromX);
        }
        double ColDark(int x, int fromY, int toY)
        {
            int dark = 0;
            for (var y = fromY; y < toY; y++)
                if (luma[y * width + x] < threshold) dark++;
            return (double)dark / Math.Max(1, toY - fromY);
        }

        // window: 이만큼 연속으로 밀도가 낮으면 경계로 판단 (공백 건너뛰기 허용치)
        void Expand(ref int lo, ref int hi, int min, int max, int cap,
                    int window, double minRatio, Func<int, double> density)
        {
            var run = 0;
            while (hi < max && hi - lo < cap && run < window)
            {
                if (density(hi) >= minRatio) { hi++; run = 0; }
                else { hi++; run++; }
            }
            hi -= run;
            run = 0;
            while (lo > min && hi - lo < cap && run < window)
            {
                if (density(lo - 1) >= minRatio) { lo--; run = 0; }
                else { lo--; run++; }
            }
            lo += run;
        }

        if (oneDimensional)
        {
            // 세로 막대 '런(run)' 분석 — 시드 스캔라인을 가로지르는 세로 막대들의
            // 실제 상/하단(중앙값)과 첫/끝 막대 위치를 직접 측정한다.
            // 밀도 확장 방식은 스캔라인 주변 일부만 잡는 경우가 있어 교체.
            var yCenter = Math.Clamp(y0 + (y1 - y0) / 2, 0, height - 1);
            var margin = Math.Max(30, seedWidth / 5);
            var winX0 = Math.Max(0, x0 - margin);
            var winX1 = Math.Min(width, x1 + margin);
            var winY0 = Math.Max(0, yCenter - 300);
            var winY1 = Math.Min(height, yCenter + 300);

            var bars = new List<(int X, int Top, int Bottom)>();
            for (var x = winX0; x < winX1; x++)
            {
                // 스캔라인(±2px)에서 어두운 열만 막대 후보
                var seedY = -1;
                for (var dy = -2; dy <= 2 && seedY < 0; dy++)
                {
                    var y = yCenter + dy;
                    if (y >= 0 && y < height && luma[y * width + x] < threshold)
                        seedY = y;
                }
                if (seedY < 0) continue;
                var top = seedY;
                while (top > winY0 && luma[(top - 1) * width + x] < threshold) top--;
                var bottom = seedY;
                while (bottom < winY1 - 1 && luma[(bottom + 1) * width + x] < threshold)
                    bottom++;
                if (bottom - top + 1 >= 15) bars.Add((x, top, bottom));
            }
            if (bars.Count >= 8)
            {
                static int Median(IEnumerable<int> values)
                {
                    var sorted = values.OrderBy(v => v).ToList();
                    return sorted[sorted.Count / 2];
                }
                var top = Median(bars.Select(b => b.Top));
                var bottom = Median(bars.Select(b => b.Bottom));
                var bandHeight = Math.Max(1, bottom - top);
                // 밴드를 60% 이상 덮는 막대만 심볼로 인정 (주변 텍스트·잡티 배제)
                var symbolBars = bars.Where(b =>
                    Math.Min(b.Bottom, bottom) - Math.Max(b.Top, top)
                    >= bandHeight * 0.6).ToList();
                if (symbolBars.Count >= 8)
                {
                    var left = symbolBars.Min(b => b.X);
                    var right = symbolBars.Max(b => b.X);
                    return (left, top, right - left + 1, bottom - top + 1);
                }
            }
            return seed;   // 막대를 찾지 못하면 시드 유지
        }
        else
        {
            // DataMatrix/QR: L 보더 덕에 심볼 내부 행/열엔 항상 어두운 픽셀 존재
            var size = Math.Max(x1 - x0, y1 - y0);
            // maxGrowth: 시드가 이미 심볼에 가까울 때(로케이터 후보) 맞닿은 객체로 번지지 않게 성장 한도를 준다
            var cap = maxGrowth is { } growth ? size + Math.Max(4, growth) : size * 2 + 60;
            for (var pass = 0; pass < 2; pass++)
            {
                var fromX = x0;
                var toX = x1;
                Expand(ref y0, ref y1, 0, height, cap, window: 4, minRatio: 0.12,
                       y => RowDark(y, fromX, toX));
                var fromY = y0;
                var toY = y1;
                Expand(ref x0, ref x1, 0, width, cap, window: 4, minRatio: 0.12,
                       x => ColDark(x, fromY, toY));
            }
        }
        if (x1 - x0 < 4 || y1 - y0 < 4) return seed;
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>2%/98% 퍼센타일 중간값 문턱 (라벨: 흰 바탕 + 검정 잉크).</summary>
    private static byte DarkThreshold(byte[] luma)
    {
        var histogram = new int[256];
        foreach (var v in luma) histogram[v]++;
        long total = luma.Length, acc = 0;
        int lo = 0, hi = 255;
        for (var v = 0; v < 256; v++) { acc += histogram[v]; if (acc >= total * 0.02) { lo = v; break; } }
        acc = 0;
        for (var v = 255; v >= 0; v--) { acc += histogram[v]; if (acc >= total * 0.02) { hi = v; break; } }
        return (byte)((lo + hi) / 2);
    }
}
