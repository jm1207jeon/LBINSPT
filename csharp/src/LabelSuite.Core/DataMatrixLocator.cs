// DataMatrix 후보 영역 탐지 — ZXing의 전체 페이지 다중 검색(DecodeMultiple)은
// 큰 이미지 속 작은 DataMatrix를 자주 놓친다(알려진 약점). 어두운 모듈이
// 밀집한 정방형 영역을 격자 밀도 + 연결 요소로 직접 찾아, 그 영역만 잘라
// 전용 리더로 집중 디코딩하도록 후보를 제공한다. (오탐 후보는 디코딩 실패로
// 자연 배제되므로 재현율 우선으로 느슨하게 잡는다)
//
// 재현율 보강(요청: 하단·소형·다른 객체와 맞닿은 심볼 누락):
//  · 다중 격자 — 8px(일반) + 4px(소형 20~60px) + 8px 고밀도(0.42, 텍스트보다 촘촘한 모듈만 남겨 분리)
//  · 맞닿은 연결 요소 분리 — 텍스트 줄·테두리 선과 이어져 길쭉해지거나 성긴 요소에서
//    정방형·고밀도 부분 창을 찾아 후보로 추가
//  · 후보 수 24개, 정방성×밀도 순 (크기 순이 아니라 소형 심볼이 밀리지 않게)
using SkiaSharp;

namespace LabelSuite.Core;

public static class DataMatrixLocator
{
    private const int MinSidePx = 20;
    private const int MaxSidePx = 700;
    private const int MaxCandidates = 48;   // 원형 후보 우선, 남는 자리에 분리 후보

    private readonly record struct Scale(int Cell, double DarkRatio, int MaxSide);

    private static readonly Scale[] Scales =
    [
        new(8, 0.28, MaxSidePx),
        new(8, 0.42, MaxSidePx),
        new(4, 0.30, 240),
    ];

    /// <summary>DataMatrix로 보이는 후보 사각형 목록 (정방성·밀도 순, 최대 24개, 겹침 제거).</summary>
    public static List<(int X, int Y, int W, int H)> FindCandidates(SKBitmap image)
    {
        var results = new List<(int X, int Y, int W, int H, double Score, bool Split)>();
        if (image.Width < MinSidePx || image.Height < MinSidePx) return [];
        var luma = ImagePreprocess.LumaBuffer(image);
        var threshold = DarkThreshold(luma);
        foreach (var scale in Scales)
            results.AddRange(FindAtScale(luma, image.Width, image.Height, threshold, scale));

        // 우선순위: L 파인더(인접한 두 변이 실선) 보유 > 원형 후보 > 분리 후보, 그 안에서 점수순.
        // 텍스트 덩어리 조각(정방형·밀집이지만 실선 변이 없음)이 진짜 심볼을 후보 상한 밖으로 밀어내지 않게 한다.
        var accepted = new List<(int X, int Y, int W, int H)>();
        var ranked = results
            .Select(r => (r.X, r.Y, r.W, r.H, r.Score, r.Split,
                          L: HasLFinder(luma, image.Width, image.Height, threshold, (r.X, r.Y, r.W, r.H))))
            .OrderByDescending(r => r.L).ThenBy(r => r.Split).ThenByDescending(r => r.Score);
        foreach (var r in ranked)
        {
            var rect = (r.X, r.Y, r.W, r.H);
            if (accepted.Any(a => Overlap(a, rect) >= 0.5)) continue;
            accepted.Add(rect);
            if (accepted.Count >= MaxCandidates) break;
        }
        return accepted;
    }

    /// <summary>DataMatrix 고유 특징 — 인접한 두 변을 따라 끊김 없는 실선(L 파인더 패턴)이 있는지.
    /// 후보 사각형 변 근처(±12px)에서 어두운 비율 ≥ 0.85인 행/열을 찾아, 인접한 두 변에서 모두 발견되면 true.
    /// 텍스트·격자 조각은 한 변은 실선일 수 있어도 인접 두 변이 동시에 실선인 경우가 드물다.</summary>
    internal static bool HasLFinder(byte[] luma, int width, int height, byte threshold,
                                    (int X, int Y, int W, int H) rect)
    {
        const double Solid = 0.85;
        var band = Math.Max(4, Math.Min(12, Math.Min(rect.W, rect.H) / 4));
        var x0 = Math.Max(0, rect.X);
        var x1 = Math.Min(width, rect.X + rect.W);
        var y0 = Math.Max(0, rect.Y);
        var y1 = Math.Min(height, rect.Y + rect.H);
        if (x1 - x0 < 8 || y1 - y0 < 8) return false;

        double RowDark(int y)
        {
            var dark = 0;
            var row = y * width;
            for (var x = x0; x < x1; x++) if (luma[row + x] < threshold) dark++;
            return (double)dark / (x1 - x0);
        }
        double ColDark(int x)
        {
            var dark = 0;
            for (var y = y0; y < y1; y++) if (luma[y * width + x] < threshold) dark++;
            return (double)dark / (y1 - y0);
        }
        double top = 0, bottom = 0, left = 0, right = 0;
        for (var d = -band; d <= band; d++)
        {
            var yt = y0 + d; if (yt >= 0 && yt < height) top = Math.Max(top, RowDark(yt));
            var yb = y1 - 1 + d; if (yb >= 0 && yb < height) bottom = Math.Max(bottom, RowDark(yb));
            var xl = x0 + d; if (xl >= 0 && xl < width) left = Math.Max(left, ColDark(xl));
            var xr = x1 - 1 + d; if (xr >= 0 && xr < width) right = Math.Max(right, ColDark(xr));
        }
        return (left >= Solid || right >= Solid) && (top >= Solid || bottom >= Solid);
    }

    private static List<(int X, int Y, int W, int H, double Score, bool Split)> FindAtScale(
        byte[] luma, int width, int height, byte threshold, Scale scale)
    {
        var cell = scale.Cell;
        var results = new List<(int, int, int, int, double, bool)>();
        var gridW = (width + cell - 1) / cell;
        var gridH = (height + cell - 1) / cell;
        var dark = new bool[gridW * gridH];
        var step = cell >= 8 ? 2 : 1;
        for (var gy = 0; gy < gridH; gy++)
            for (var gx = 0; gx < gridW; gx++)
            {
                int count = 0, total = 0;
                var yEnd = Math.Min(height, (gy + 1) * cell);
                var xEnd = Math.Min(width, (gx + 1) * cell);
                for (var y = gy * cell; y < yEnd; y += step)
                    for (var x = gx * cell; x < xEnd; x += step)
                    {
                        total++;
                        if (luma[y * width + x] < threshold) count++;
                    }
                dark[gy * gridW + gx] = total > 0 && (double)count / total >= scale.DarkRatio;
            }

        var visited = new bool[gridW * gridH];
        var queue = new Queue<int>();
        var component = new List<int>();
        var minSideCells = Math.Max(2, (MinSidePx + cell - 1) / cell);   // 8px→3칸(24px), 4px→5칸(20px)
        var maxSideCells = scale.MaxSide / cell;
        for (var start = 0; start < dark.Length; start++)
        {
            if (!dark[start] || visited[start]) continue;
            visited[start] = true;
            queue.Enqueue(start);
            component.Clear();
            int minX = gridW, minY = gridH, maxX = 0, maxY = 0;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                component.Add(index);
                var cx = index % gridW;
                var cy = index / gridW;
                if (cx < minX) minX = cx;
                if (cy < minY) minY = cy;
                if (cx > maxX) maxX = cx;
                if (cy > maxY) maxY = cy;
                Span<int> neighbors = [index - 1, index + 1, index - gridW, index + gridW];
                foreach (var n in neighbors)
                {
                    if (n < 0 || n >= dark.Length || visited[n] || !dark[n]) continue;
                    if ((n == index - 1 || n == index + 1) && n / gridW != cy) continue;
                    visited[n] = true;
                    queue.Enqueue(n);
                }
            }
            var cellsW = maxX - minX + 1;
            var cellsH = maxY - minY + 1;
            if (cellsW < minSideCells && cellsH < minSideCells) continue;
            var aspect = (double)cellsW / cellsH;
            var fill = (double)component.Count / (cellsW * cellsH);
            var squareEnough = aspect is >= 0.55 and <= 1.8;
            if (squareEnough && fill >= 0.45
                && cellsW >= minSideCells && cellsH >= minSideCells
                && cellsW <= maxSideCells && cellsH <= maxSideCells)
            {
                results.Add((minX * cell, minY * cell, cellsW * cell, cellsH * cell,
                             Score(aspect, fill), false));
                continue;
            }
            // 정방형이 아니거나 성긴 요소: 다른 객체(텍스트 줄·선·라벨 프레임)와 붙은 심볼일 수 있다 —
            // 요소 안에서 정방형 고밀도 부분 창을 찾는다. 짧은 변이 32px(8px 격자 4칸) 미만인 요소(보통
            // 텍스트 한 줄)는 건너뛴다 — 그보다 작은 심볼은 모듈이 2px 미만이라 디코드되지 않는다.
            var splitMinCells = Math.Max(minSideCells, (32 + cell - 1) / cell);
            if (Math.Min(cellsW, cellsH) < splitMinCells) continue;
            foreach (var window in SplitSquares(component, dark, gridW, gridH, minX, minY, cellsW, cellsH,
                                                splitMinCells, Math.Min(maxSideCells, Math.Min(cellsW, cellsH))))
                results.Add((window.X * cell, window.Y * cell, window.S * cell, window.S * cell,
                             window.Score, true));
        }
        return results;
    }

    /// <summary>연결 요소 안에서 밀도 ≥ 0.8인 정방형 창(변 s)을 찾는다. 창 크기는 짧은 변부터 몇 단계,
    /// 위치는 2D 슬라이딩(누적합 O(1)). 점수 = 밀도 × (1 − 창 둘레 1칸 띠의 어두운 비율) — DataMatrix는
    /// 붙은 쪽을 빼면 둘레가 밝고(콰이엇 존), 텍스트 덩어리 속 창은 둘레가 계속 어둡다.
    /// 겹치는 창은 점수 높은 것만, 요소당 최대 4개.</summary>
    private static List<(int X, int Y, int S, double Score)> SplitSquares(
        List<int> component, bool[] dark, int gridW, int gridH, int minX, int minY, int cellsW, int cellsH,
        int minSideCells, int maxSideCells)
    {
        var found = new List<(int X, int Y, int S, double Score)>();
        if (maxSideCells < minSideCells) return found;
        // 요소 마스크 누적합
        var sum = new int[(cellsW + 1) * (cellsH + 1)];
        var mask = new bool[cellsW * cellsH];
        foreach (var index in component)
            mask[(index / gridW - minY) * cellsW + (index % gridW - minX)] = true;
        for (var y = 1; y <= cellsH; y++)
            for (var x = 1; x <= cellsW; x++)
                sum[y * (cellsW + 1) + x] = (mask[(y - 1) * cellsW + (x - 1)] ? 1 : 0)
                    + sum[(y - 1) * (cellsW + 1) + x] + sum[y * (cellsW + 1) + (x - 1)]
                    - sum[(y - 1) * (cellsW + 1) + (x - 1)];
        int Area(int x, int y, int s) =>
            sum[(y + s) * (cellsW + 1) + (x + s)] - sum[y * (cellsW + 1) + (x + s)]
            - sum[(y + s) * (cellsW + 1) + x] + sum[y * (cellsW + 1) + x];

        double RingDark(int gx, int gy, int s)
        {
            int ring = 0, darkCount = 0;
            for (var x = gx - 1; x <= gx + s; x++)
                for (var y = gy - 1; y <= gy + s; y++)
                {
                    var onRing = x == gx - 1 || x == gx + s || y == gy - 1 || y == gy + s;
                    if (!onRing || x < 0 || y < 0 || x >= gridW || y >= gridH) continue;
                    ring++;
                    if (dark[y * gridW + x]) darkCount++;
                }
            return ring == 0 ? 1 : (double)darkCount / ring;
        }

        var sizes = new List<int>();
        for (var s = maxSideCells; s >= minSideCells && sizes.Count < 4; s = (int)(s * 0.7))
            sizes.Add(s);
        var candidates = new List<(int X, int Y, int S, double Score)>();
        foreach (var s in sizes.Distinct())
            for (var y = 0; y + s <= cellsH; y++)
                for (var x = 0; x + s <= cellsW; x++)
                {
                    var fill = (double)Area(x, y, s) / (s * s);
                    if (fill < 0.8) continue;
                    var score = fill * (1 - RingDark(minX + x, minY + y, s));
                    candidates.Add((minX + x, minY + y, s, score));
                }
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.S))
        {
            if (found.Any(f => Overlap((f.X, f.Y, f.S, f.S), (c.X, c.Y, c.S, c.S)) >= 0.3)) continue;
            found.Add(c);
            if (found.Count >= 4) break;
        }
        return found;
    }

    private static double Score(double aspect, double fill) =>
        fill * (aspect <= 1 ? aspect : 1 / aspect);   // 정방형·밀집일수록 1에 가깝다

    /// <summary>교집합 / 작은 쪽 넓이.</summary>
    private static double Overlap((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b)
    {
        var ix = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
        var iy = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        if (ix <= 0 || iy <= 0) return 0;
        var smaller = Math.Min((long)a.W * a.H, (long)b.W * b.H);
        return smaller <= 0 ? 0 : (double)((long)ix * iy) / smaller;
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
