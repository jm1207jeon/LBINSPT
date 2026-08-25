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
        SKBitmap image, (int X, int Y, int W, int H) seed, bool oneDimensional)
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
            // 1) 세로: 시드 스캔라인에서 막대 밴드로 (빈 줄 즉시 경계)
            var capY = Math.Max(160, seedWidth);
            Expand(ref y0, ref y1, 0, height, capY, window: 3, minRatio: 0.30,
                   y => RowDark(y, x0, x1));
            // 2) 가로: 시작/정지 패턴 중심 → 심볼 끝까지 (공백 4모듈 허용)
            var spaceWindow = Math.Max(6, seedWidth / 15);
            var capX = seedWidth * 2 + 80;
            Expand(ref x0, ref x1, 0, width, capX, spaceWindow, minRatio: 0.35,
                   x => ColDark(x, y0, y1));
            // 3) 넓어진 가로 범위로 세로 한 번 더 (기울어진 인쇄 여유)
            Expand(ref y0, ref y1, 0, height, capY, window: 3, minRatio: 0.30,
                   y => RowDark(y, x0, x1));
        }
        else
        {
            // DataMatrix/QR: L 보더 덕에 심볼 내부 행/열엔 항상 어두운 픽셀 존재
            var size = Math.Max(x1 - x0, y1 - y0);
            var cap = size * 2 + 60;
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
