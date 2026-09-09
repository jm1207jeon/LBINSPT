// 바코드/데이터매트릭스 검출 — ZXing.Net (순수 관리형, 네이티브 의존성 없음).
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;

namespace LabelSuite.Core;

public static class BarcodeDetector
{
    private static readonly BarcodeReader Reader = new()
    {
        // 주의: AutoRotate를 켜면 회전 시도에서 성공한 검출점이 '회전된 이미지'
        // 좌표계로 반환되어 박스가 엉뚱한 곳에 그려진다 (DataMatrix 박스 누락의
        // 원인). 2D 심볼은 디텍터가 자체적으로 회전을 처리하고, 1D는 라벨 정렬
        // (기울기 보정) 기능이 수평을 보장하므로 끈다.
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            // GS1-128: 선두 FNC1을 ']C1' 접두로, 이후 FNC1을 GS(0x1D) 구분자로 돌려받는다.
            // 이 옵션이 없으면 FNC1이 통째로 버려져 (10) LOT 값이 뒤따르는 (17) 유효기한까지
            // 삼켜 'LOT 불일치'로 보인다 (Gs1.Parse가 ]C1을 벗기고 GS로 요소를 나눈다).
            AssumeGS1 = true,
            PossibleFormats =
            [
                BarcodeFormat.DATA_MATRIX, BarcodeFormat.QR_CODE,
                BarcodeFormat.CODE_128, BarcodeFormat.CODE_39,
                BarcodeFormat.EAN_13, BarcodeFormat.ITF,
            ],
        },
    };

    private static bool Is1D(BarcodeFormat format) =>
        format is BarcodeFormat.CODE_128 or BarcodeFormat.CODE_39
               or BarcodeFormat.EAN_13 or BarcodeFormat.ITF;

    // DataMatrix 후보 크롭 집중 디코딩용 단일 리더
    private static readonly BarcodeReader DataMatrixReader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            PossibleFormats = [BarcodeFormat.DATA_MATRIX],
        },
    };

    public static List<BarcodeHit> Detect(SKBitmap image)
    {
        var hits = new List<BarcodeHit>();
        var dmFromMultiple = new List<BarcodeHit>();
        Result[]? results;
        try { results = Reader.DecodeMultiple(image); }
        catch (Exception) { results = null; }
        foreach (var result in results ?? [])
        {
            if (string.IsNullOrEmpty(result.Text)) continue;
            var xs = result.ResultPoints?.Select(p => (int)p.X).ToList() ?? [0];
            var ys = result.ResultPoints?.Select(p => (int)p.Y).ToList() ?? [0];
            var x = xs.Min();
            var y = ys.Min();
            // 검출점(파인더/시작·정지 패턴)은 심볼 일부일 뿐 — 실제 심볼 영역으로 확장
            var seed = (x, y, Math.Max(1, xs.Max() - x), Math.Max(1, ys.Max() - y));
            var bbox = BarcodeBoxRefiner.Refine(image, seed, Is1D(result.BarcodeFormat));
            var isGs1 = LooksGs1(result.Text);
            var hit = new BarcodeHit(
                BarcodeSymbology.Normalize(result.BarcodeFormat.ToString(), isGs1),
                result.Text, bbox, isGs1);
            // DataMatrix는 로케이터 전수 탐지가 기준 — DecodeMultiple 결과는 보충용
            if (result.BarcodeFormat == BarcodeFormat.DATA_MATRIX)
                dmFromMultiple.Add(hit);
            else hits.Add(hit);
        }

        // DataMatrix 전수 탐지: 라벨의 모든 위치·크기의 심볼을 후보로 찾아
        // 각각 집중 디코딩. 같은 값이 여러 위치에 인쇄된 경우도 전부 유지한다
        // (중복 제거는 '위치 겹침'으로만 판단).
        var dataMatrixHits = DetectDataMatrixByLocator(image);
        foreach (var fallback in dmFromMultiple)
            if (!dataMatrixHits.Any(h => Overlaps(h.Bbox, fallback.Bbox)))
                dataMatrixHits.Add(fallback);
        hits.AddRange(dataMatrixHits);
        return hits;
    }

    /// <summary>사용자가 클릭한 지점의 바코드 1개를 판독한다 (검사 탭 '바코드 인식' 모드). 점 주변을 240→480→960px
    /// 창으로 잘라 전체 파이프라인(DecodeMultiple + DataMatrix 로케이터)을 돌리고, 박스가 점을 포함하는 심볼을
    /// 우선, 없으면 창 안에서 가장 가까운 심볼을 돌려준다. 좌표는 원본 이미지 기준으로 되돌린다. 없으면 null.</summary>
    public static BarcodeHit? DecodeAt(SKBitmap image, int x, int y)
    {
        if (image.Width < 8 || image.Height < 8) return null;
        foreach (var window in new[] { 240, 480, 960 })
        {
            var half = window / 2;
            var x0 = Math.Max(0, x - half);
            var y0 = Math.Max(0, y - half);
            var x1 = Math.Min(image.Width, x + half);
            var y1 = Math.Min(image.Height, y + half);
            if (x1 - x0 < 16 || y1 - y0 < 16) continue;
            using var crop = new SKBitmap(x1 - x0, y1 - y0, image.ColorType, image.AlphaType);
            using (var canvas = new SKCanvas(crop))
                canvas.DrawBitmap(image, new SKRect(x0, y0, x1, y1), new SKRect(0, 0, crop.Width, crop.Height));
            var hits = Detect(crop)
                .Select(h => h with { Bbox = (h.Bbox.X + x0, h.Bbox.Y + y0, h.Bbox.W, h.Bbox.H) })
                .ToList();
            if (hits.Count == 0) continue;
            const int Slack = 20;
            var containing = hits.Where(h =>
                x >= h.Bbox.X - Slack && x <= h.Bbox.X + h.Bbox.W + Slack
                && y >= h.Bbox.Y - Slack && y <= h.Bbox.Y + h.Bbox.H + Slack).ToList();
            if (containing.Count > 0)
                return containing.OrderBy(h => (long)h.Bbox.W * h.Bbox.H).First();   // 겹치면 작은(안쪽) 심볼
            var nearest = hits.OrderBy(h => Distance(h.Bbox, x, y)).First();
            if (Distance(nearest.Bbox, x, y) <= half) return nearest;
            if (window == 960) return nearest;
        }
        return null;
    }

    private static double Distance((int X, int Y, int W, int H) box, int x, int y)
    {
        var dx = Math.Max(Math.Max(box.X - x, 0), x - (box.X + box.W));
        var dy = Math.Max(Math.Max(box.Y - y, 0), y - (box.Y + box.H));
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    /// <summary>DataMatrix 전수 탐지(로케이터 경로) — 어두운 정방형 후보 영역을 모두 찾아
    /// 각 영역을 잘라 집중 디코딩한다. (ZXing DecodeMultiple이 큰 이미지 속
    /// 여러/작은/맞닿은 DataMatrix를 놓치는 약점을 보완, 위치·크기 무관 전부 검출)
    /// Detect()가 DecodeMultiple 결과와 합치며, 단독으로도 호출할 수 있다.</summary>
    public static List<BarcodeHit> DetectDataMatrixByLocator(SKBitmap image)
    {
        var found = new List<BarcodeHit>();
        List<(int X, int Y, int W, int H)> candidates;
        try { candidates = DataMatrixLocator.FindCandidates(image); }
        catch (Exception) { return found; }
        if (candidates.Count == 0) return found;
        var luma = ImagePreprocess.LumaBuffer(image);   // L 파인더 유도 크롭용 — 페이지당 1회
        var threshold = DarkThresholdOf(luma);
        foreach (var candidate in candidates)
        {
            if (found.Any(h => Overlaps(h.Bbox, candidate))) continue;
            var text = TryDecodeCrop(image, candidate, luma, threshold);
            if (string.IsNullOrEmpty(text)) continue;
            // 후보는 격자 단위로 거칠어 정밀화하되, 맞닿은 텍스트·선으로 번지지 않게 성장 폭을 제한
            var bbox = BarcodeBoxRefiner.Refine(image, candidate, oneDimensional: false,
                                                maxGrowth: Math.Max(candidate.W, candidate.H) / 3 + 12);
            var isGs1 = LooksGs1(text);
            found.Add(new BarcodeHit(BarcodeSymbology.Normalize("DATA_MATRIX", isGs1),
                                     text, bbox, isGs1));
        }
        return found;
    }

    private static bool Overlaps((int X, int Y, int W, int H) a,
                                 (int X, int Y, int W, int H) b)
    {
        var ix = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
        var iy = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        if (ix <= 0 || iy <= 0) return false;
        long intersection = (long)ix * iy;
        var smaller = Math.Min((long)a.W * a.H, (long)b.W * b.H);
        return intersection >= smaller * 0.3;
    }

    /// <summary>후보 영역을 여유 포함해 잘라 디코딩. 실패하면 (2) 후보 밖을 흰색으로 지운 크롭(콰이엇 존 합성 —
    /// 테두리·텍스트가 맞닿은 심볼), (3) 후보를 한 칸 안쪽으로 줄이고 흰 여백을 준 크롭(얇은 선이 붙은 경우)을
    /// 차례로 시도한다. 소형 심볼은 2~3배 확대.</summary>
    private static string? TryDecodeCrop(SKBitmap image, (int X, int Y, int W, int H) rect,
                                         byte[] luma, byte threshold)
    {
        var margin = Math.Max(12, Math.Max(rect.W, rect.H) / 8);
        var size = Math.Max(rect.W, rect.H);
        var scale = size < 50 ? 3 : size < 90 ? 2 : 1;
        // (1) 원본 크롭 — 4방향 회전 시도
        if (DecodeVariant(image, rect, margin, scale, whiten: false, inset: 0, rotations: 4) is { } plain)
            return plain;
        // (2) L 파인더 유도 크롭: 심볼 안쪽의 실선 L(두 변)로 정확한 정방형을 구해 그 바깥을 흰색으로 —
        //     후보 사각형이 텍스트·선과 붙어 있거나 격자 때문에 심볼을 조금 잘라낸 경우를 모두 해결
        if (LGuidedSquare(luma, image.Width, image.Height, threshold, rect) is { } square
            && DecodeVariant(image, square, Math.Max(12, Math.Max(square.W, square.H) / 6),
                             Math.Max(square.W, square.H) < 50 ? 3 : Math.Max(square.W, square.H) < 90 ? 2 : 1,
                             whiten: true, inset: 0, rotations: 1) is { } guided)
            return guided;
        // (3) 콰이엇 존 합성: 후보 사각형(한 격자 칸 확장) 바깥을 흰색으로 — 맞닿은 객체 제거
        var grown = (X: rect.X - 8, Y: rect.Y - 8, W: rect.W + 16, H: rect.H + 16);
        if (DecodeVariant(image, grown, margin, scale, whiten: true, inset: 0, rotations: 1) is { } quiet)
            return quiet;
        // (4) 안쪽으로 살짝 줄여 얇은 테두리 선을 잘라낸다
        var inset = Math.Max(2, size / 16);
        return DecodeVariant(image, rect, margin, scale, whiten: true, inset: inset, rotations: 1);
    }

    /// <summary>후보 근처에서 DataMatrix의 L 파인더(인접한 두 변의 실선)를 찾아 심볼의 정확한 정방형을 돌려준다.
    /// 실선 = 1px 틈을 허용한 연속 어두운 구간. 행 실선과 열 실선이 모서리에서 만나고 길이가 비슷(±15%)하면
    /// 그 길이를 한 변으로 하는 정사각형이 심볼이다. 프레임 선은 길이가 맞지 않아 배제된다.</summary>
    internal static (int X, int Y, int W, int H)? LGuidedSquare(SKBitmap image, (int X, int Y, int W, int H) rect)
    {
        var luma = ImagePreprocess.LumaBuffer(image);
        return LGuidedSquare(luma, image.Width, image.Height, DarkThresholdOf(luma), rect);
    }

    internal static (int X, int Y, int W, int H)? LGuidedSquare(byte[] luma, int width, int height, byte threshold,
                                                             (int X, int Y, int W, int H) rect)
    {
        var size = Math.Max(rect.W, rect.H);
        var band = Math.Max(6, size / 4);
        var minLen = (int)(Math.Min(rect.W, rect.H) * 0.6);
        var maxLen = (int)(size * 1.7);
        var x0 = Math.Max(0, rect.X - band);
        var x1 = Math.Min(width, rect.X + rect.W + band);
        var y0 = Math.Max(0, rect.Y - band);
        var y1 = Math.Min(height, rect.Y + rect.H + band);

        // (위치, 시작, 끝) — 행 실선·열 실선
        var rowRuns = new List<(int Pos, int Start, int End)>();
        var colRuns = new List<(int Pos, int Start, int End)>();
        for (var y = y0; y < y1; y++)
            foreach (var run in DarkRuns(x => luma[y * width + x] < threshold, x0, x1, minLen, maxLen))
                rowRuns.Add((y, run.Start, run.End));
        for (var x = x0; x < x1; x++)
            foreach (var run in DarkRuns(y => luma[y * width + x] < threshold, y0, y1, minLen, maxLen))
                colRuns.Add((x, run.Start, run.End));
        if (rowRuns.Count == 0 || colRuns.Count == 0) return null;

        (int X, int Y, int W, int H)? best = null;
        var bestAgreement = double.MaxValue;
        foreach (var row in rowRuns)
            foreach (var col in colRuns)
            {
                var rowLen = row.End - row.Start;
                var colLen = col.End - col.Start;
                var agreement = Math.Abs(rowLen - colLen) / (double)Math.Max(rowLen, colLen);
                if (agreement > 0.15) continue;
                // 모서리에서 만나는가: 열 위치가 행 구간의 한쪽 끝, 행 위치가 열 구간의 한쪽 끝 (±4px)
                var atRowEnd = Math.Abs(col.Pos - row.Start) <= 4 || Math.Abs(col.Pos - row.End) <= 4;
                var atColEnd = Math.Abs(row.Pos - col.Start) <= 4 || Math.Abs(row.Pos - col.End) <= 4;
                if (!atRowEnd || !atColEnd) continue;
                var side = Math.Max(rowLen, colLen);
                var sx = Math.Abs(col.Pos - row.Start) <= 4 ? col.Pos : col.Pos - side + 1;
                var sy = Math.Abs(row.Pos - col.Start) <= 4 ? row.Pos : row.Pos - side + 1;
                var candidate = (sx, sy, side, side);
                // 후보 사각형과 겹쳐야 한다 (이웃 심볼의 L을 집지 않게)
                if (!Overlaps(candidate, rect)) continue;
                if (agreement < bestAgreement) { bestAgreement = agreement; best = candidate; }
            }
        return best;
    }

    /// <summary>[from, to) 구간의 어두운 연속 구간(1px 틈 허용) 중 길이가 [minLen, maxLen]인 것.</summary>
    private static IEnumerable<(int Start, int End)> DarkRuns(Func<int, bool> isDark, int from, int to,
                                                            int minLen, int maxLen)
    {
        var start = -1;
        var gap = 0;
        for (var i = from; i <= to; i++)
        {
            var dark = i < to && isDark(i);
            if (dark)
            {
                if (start < 0) start = i;
                gap = 0;
                continue;
            }
            if (start < 0) continue;
            if (++gap <= 1 && i < to) continue;   // 1px 틈 허용
            var end = i - gap;                     // 마지막 어두운 픽셀 + 1
            if (end - start >= minLen && end - start <= maxLen) yield return (start, end);
            start = -1;
            gap = 0;
        }
    }

    private static byte DarkThresholdOf(byte[] luma)
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

    private static string? DecodeVariant(SKBitmap image, (int X, int Y, int W, int H) rect,
                                         int margin, int scale, bool whiten, int inset, int rotations)
    {
        var rx0 = Math.Max(0, rect.X + inset);
        var ry0 = Math.Max(0, rect.Y + inset);
        var rx1 = Math.Min(image.Width, rect.X + rect.W - inset);
        var ry1 = Math.Min(image.Height, rect.Y + rect.H - inset);
        if (rx1 - rx0 < 10 || ry1 - ry0 < 10) return null;
        var x0 = Math.Max(0, rx0 - margin);
        var y0 = Math.Max(0, ry0 - margin);
        var x1 = Math.Min(image.Width, rx1 + margin);
        var y1 = Math.Min(image.Height, ry1 + margin);
        if (x1 - x0 < 16 || y1 - y0 < 16) return null;

        using var crop = new SKBitmap((x1 - x0) * scale, (y1 - y0) * scale,
                                      image.ColorType, image.AlphaType);
        using (var canvas = new SKCanvas(crop))
        {
            if (whiten)
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(image, new SKRect(rx0, ry0, rx1, ry1),
                                  new SKRect((rx0 - x0) * scale, (ry0 - y0) * scale,
                                             (rx1 - x0) * scale, (ry1 - y0) * scale));
            }
            else
                canvas.DrawBitmap(image, new SKRect(x0, y0, x1, y1),
                                  new SKRect(0, 0, crop.Width, crop.Height));
        }
        for (var rotation = 0; rotation < rotations; rotation++)
        {
            SKBitmap target = crop;
            SKBitmap? rotated = null;
            if (rotation > 0)
            {
                rotated = Rotate90(crop, rotation);
                target = rotated;
            }
            try
            {
                var result = DataMatrixReader.Decode(target);
                if (!string.IsNullOrEmpty(result?.Text)) return result.Text;
            }
            catch (Exception) { /* 다음 시도 */ }
            finally { rotated?.Dispose(); }
        }
        return null;
    }

    private static SKBitmap Rotate90(SKBitmap source, int quarterTurns)
    {
        var swap = quarterTurns % 2 == 1;
        var result = new SKBitmap(swap ? source.Height : source.Width,
                                  swap ? source.Width : source.Height,
                                  source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(90 * quarterTurns);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    public static bool LooksGs1(string text) =>
        text.Contains('\x1d') || text.StartsWith("(01)")
        || text.StartsWith("]C1") || text.StartsWith("]d2") || text.StartsWith("]Q3")   // 심볼로지 식별자(GS1)
        || (text.Length >= 16 && text.StartsWith("01") && text[2..16].All(char.IsDigit));

    /// <summary>검증 대상 바코드 — DataMatrix는 제외(박스만 표시), 나머지는 GS1 외형일 때만.</summary>
    public static bool IsVerifiable(BarcodeHit hit) =>
        !hit.IsDataMatrix && (hit.IsGs1 || LooksGs1(hit.Text));

    /// <summary>검출된 GS1 바코드를 파싱해 레코드와 교차 검증한다.</summary>
    public static List<CrossCheckResult> CrossCheckHits(
        IEnumerable<BarcodeHit> hits, LabelRecord record)
    {
        var checks = new List<CrossCheckResult>();
        foreach (var hit in hits)
            checks.AddRange(CrossCheckHit(hit, record));
        return checks;
    }

    /// <summary>바코드 1개의 교차 검증 행. DataMatrix(박스만 표시)와 GS1 외형이 아닌 바코드는 빈 목록.
    /// GS1-128 GTIN 규칙: 구조 해석에 실패해도 맨 앞 AI(01) 다음 14자리를 GTIN으로 대조한다.</summary>
    public static List<CrossCheckResult> CrossCheckHit(BarcodeHit hit, LabelRecord record)
    {
        var checks = new List<CrossCheckResult>();
        if (!IsVerifiable(hit)) return checks;
        Gs1Message message;
        try { message = Gs1.Parse(hit.Text); }
        catch (Gs1ParseException)
        {
            if (Gs1.TryExtractGtin(hit.Text) is { } gtin)
            {
                var expected = record.Gtin.Length > 0 ? Schema.NormalizeGtin14(record.Gtin) : "";
                checks.Add(new CrossCheckResult(hit.Symbology, "GTIN", gtin, expected,
                                                expected.Length > 0 && gtin == expected));
                checks.Add(new CrossCheckResult(hit.Symbology, "GS1 해석",
                    "(01) 뒤 구조 해석 불가", "(참고) GTIN만 대조", Matched: true));
                return checks;
            }
            // 바코드는 읽혔으나 GTIN조차 뽑지 못함 — 조용히 넘기면 '바코드 없음'처럼 보여
            // 거짓 합격이 날 수 있으므로 불일치 행으로 남긴다.
            var raw = hit.Text.Replace(Gs1.GS.ToString(), "<GS>");
            checks.Add(new CrossCheckResult(hit.Symbology, "GS1 해석",
                raw.Length > 40 ? raw[..40] + "…" : raw, "해석 가능한 GS1 구조", Matched: false));
            return checks;
        }
        checks.AddRange(BarcodeCrossCheck.Check(message, record, hit.Symbology));
        if (message.Partial)
            checks.Add(new CrossCheckResult(hit.Symbology, "미등록 AI",
                string.Join(", ", message.UnknownAis), "(참고)", Matched: true));
        return checks;
    }

    /// <summary>바코드 검증 표의 한 행(값·상태·툴팁) — 화면과 테스트가 같은 규칙을 쓴다.
    /// 상태: "일치" / "불일치" / "해석 불가" / "-"(대조 항목 없음).</summary>
    public static (string Value, string State, string? Tip) Summarize(BarcodeHit hit, LabelRecord record)
    {
        if (!IsVerifiable(hit)) return (hit.Text, "-", null);
        var checks = CrossCheckHit(hit, record);
        string? tip = null;
        string value;
        try
        {
            var message = Gs1.Parse(hit.Text);
            value = string.Concat(message.Elements.Select(el => $"({el.Ai}){el.Value}"));
            if (message.Partial)
            {
                value += $" (미등록 AI: {string.Join(", ", message.UnknownAis)})";
                tip = "GS1 표준 표에 없는 AI가 있어 그 구간은 대조하지 않았습니다 — 등록된 AI(01/10/17 등)만 대조";
            }
        }
        catch (Gs1ParseException)
        {
            var raw = hit.Text.Replace(Gs1.GS, '|');
            if (Gs1.TryExtractGtin(hit.Text) is { } gtin)
            {
                value = $"(01){gtin} …";
                tip = "GS1 구조 해석에 실패해 맨 앞 AI(01) 다음 14자리만 GTIN으로 대조했습니다 — 나머지 판독값: "
                      + (raw.Length > 60 ? raw[..60] + "…" : raw);
            }
            else
            {
                value = raw.Length > 40 ? raw[..40] + "…" : raw;
                return (value, "해석 불가", "바코드는 읽혔으나 GS1 구조를 해석하지 못했습니다 — 판독값을 육안 확인하세요");
            }
        }
        var compared = checks.Where(c => c.Field is "GTIN" or "LOT" or "MFG DATE" or "EXP DATE").ToList();
        if (compared.Count == 0) return (value, "-", tip);
        if (compared.All(c => c.Matched))
        {
            if (checks.Any(c => c.Field == "FNC1 누락"))
                tip = (tip is null ? "" : tip + "\n") + "FNC1 구분자 없이 인쇄된 GS1-128 — (10) 값 뒤에 이어진 AI를 분리해 대조했습니다";
            return (value, "일치", tip);
        }
        value += "  ⚠ " + string.Join(", ", compared.Where(c => !c.Matched)
            .Select(c => $"{c.Field} 기대 {c.ExpectedValue}"));
        return (value, "불일치", tip);
    }
}
