// DataMatrix 후보 영역 탐지 — ZXing의 전체 페이지 다중 검색(DecodeMultiple)은
// 큰 이미지 속 작은 DataMatrix를 자주 놓친다(알려진 약점). 어두운 모듈이
// 밀집한 정방형 영역을 격자 밀도 + 연결 요소로 직접 찾아, 그 영역만 잘라
// 전용 리더로 집중 디코딩하도록 후보를 제공한다. (오탐 후보는 디코딩 실패로
// 자연 배제되므로 재현율 우선으로 느슨하게 잡는다)
using SkiaSharp;

namespace LabelSuite.Core;

public static class DataMatrixLocator
{
    private const int Cell = 8;            // 밀도 격자 셀 크기(px)
    private const double DarkCellRatio = 0.28;
    private const int MinSidePx = 20;
    private const int MaxSidePx = 700;

    /// <summary>DataMatrix로 보이는 후보 사각형 목록 (큰 것부터, 최대 12개).</summary>
    public static List<(int X, int Y, int W, int H)> FindCandidates(SKBitmap image)
    {
        var results = new List<(int, int, int, int)>();
        if (image.Width < MinSidePx || image.Height < MinSidePx) return results;
        var luma = ImagePreprocess.LumaBuffer(image);
        var width = image.Width;
        var height = image.Height;
        var threshold = DarkThreshold(luma);

        // 1) 셀 단위 어두운 픽셀 밀도 격자
        var gridW = (width + Cell - 1) / Cell;
        var gridH = (height + Cell - 1) / Cell;
        var dark = new bool[gridW * gridH];
        for (var gy = 0; gy < gridH; gy++)
            for (var gx = 0; gx < gridW; gx++)
            {
                int count = 0, total = 0;
                var yEnd = Math.Min(height, (gy + 1) * Cell);
                var xEnd = Math.Min(width, (gx + 1) * Cell);
                for (var y = gy * Cell; y < yEnd; y += 2)
                    for (var x = gx * Cell; x < xEnd; x += 2)
                    {
                        total++;
                        if (luma[y * width + x] < threshold) count++;
                    }
                dark[gy * gridW + gx] = total > 0
                    && (double)count / total >= DarkCellRatio;
            }

        // 2) 연결 요소 → 정방형·밀집 필터
        var visited = new bool[gridW * gridH];
        var queue = new Queue<int>();
        for (var start = 0; start < dark.Length; start++)
        {
            if (!dark[start] || visited[start]) continue;
            visited[start] = true;
            queue.Enqueue(start);
            int minX = gridW, minY = gridH, maxX = 0, maxY = 0, cells = 0;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                cells++;
                var cx = index % gridW;
                var cy = index / gridW;
                if (cx < minX) minX = cx;
                if (cy < minY) minY = cy;
                if (cx > maxX) maxX = cx;
                if (cy > maxY) maxY = cy;
                Span<int> neighbors =
                    [index - 1, index + 1, index - gridW, index + gridW];
                foreach (var n in neighbors)
                {
                    if (n < 0 || n >= dark.Length || visited[n] || !dark[n]) continue;
                    // 좌우 이웃은 같은 행일 때만
                    if ((n == index - 1 || n == index + 1) && n / gridW != cy) continue;
                    visited[n] = true;
                    queue.Enqueue(n);
                }
            }
            var pxW = (maxX - minX + 1) * Cell;
            var pxH = (maxY - minY + 1) * Cell;
            if (pxW < MinSidePx || pxH < MinSidePx
                || pxW > MaxSidePx || pxH > MaxSidePx) continue;
            var aspect = (double)pxW / pxH;
            if (aspect is < 0.55 or > 1.8) continue;   // 정방형만 (텍스트 줄 배제)
            var fill = (double)cells / ((maxX - minX + 1) * (maxY - minY + 1));
            if (fill < 0.45) continue;                  // 밀집 영역만
            results.Add((minX * Cell, minY * Cell, pxW, pxH));
        }
        return results
            .OrderByDescending(r => (long)r.Item3 * r.Item4)
            .Take(12)
            .ToList();
    }

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
