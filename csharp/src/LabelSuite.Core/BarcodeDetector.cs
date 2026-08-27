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
            hits.Add(new BarcodeHit(
                BarcodeSymbology.Normalize(result.BarcodeFormat.ToString(), isGs1),
                result.Text, bbox, isGs1));
        }
        SupplementDataMatrix(image, hits);
        return hits;
    }

    /// <summary>ZXing 전체 페이지 다중 검색이 놓친 DataMatrix 보강 탐지 —
    /// 어두운 정방형 후보 영역을 직접 찾아 그 부분만 잘라 집중 디코딩한다.
    /// (큰 이미지 속 작은 DataMatrix를 DecodeMultiple이 놓치는 알려진 약점 보완)</summary>
    private static void SupplementDataMatrix(SKBitmap image, List<BarcodeHit> hits)
    {
        List<(int X, int Y, int W, int H)> candidates;
        try { candidates = DataMatrixLocator.FindCandidates(image); }
        catch (Exception) { return; }
        foreach (var candidate in candidates)
        {
            if (hits.Any(h => Overlaps(h.Bbox, candidate))) continue;
            var text = TryDecodeCrop(image, candidate);
            if (string.IsNullOrEmpty(text)) continue;
            if (hits.Any(h => h.Text == text)) continue;   // 동일 심볼 중복 방지
            var bbox = BarcodeBoxRefiner.Refine(image, candidate,
                                                oneDimensional: false);
            var isGs1 = LooksGs1(text);
            hits.Add(new BarcodeHit(BarcodeSymbology.Normalize("DATA_MATRIX", isGs1),
                                    text, bbox, isGs1));
        }
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

    /// <summary>후보 영역을 여유 포함해 잘라 0/90/180/270° 회전 시도로 디코딩.</summary>
    private static string? TryDecodeCrop(SKBitmap image,
                                         (int X, int Y, int W, int H) rect)
    {
        var margin = Math.Max(12, Math.Max(rect.W, rect.H) / 8);
        var x0 = Math.Max(0, rect.X - margin);
        var y0 = Math.Max(0, rect.Y - margin);
        var x1 = Math.Min(image.Width, rect.X + rect.W + margin);
        var y1 = Math.Min(image.Height, rect.Y + rect.H + margin);
        if (x1 - x0 < 16 || y1 - y0 < 16) return null;

        // 작은 심볼은 2배 확대해 디코딩 성공률을 높인다
        var scale = Math.Max(x1 - x0, y1 - y0) < 90 ? 2 : 1;
        using var crop = new SKBitmap((x1 - x0) * scale, (y1 - y0) * scale,
                                      image.ColorType, image.AlphaType);
        using (var canvas = new SKCanvas(crop))
            canvas.DrawBitmap(image, new SKRect(x0, y0, x1, y1),
                              new SKRect(0, 0, crop.Width, crop.Height));

        for (var rotation = 0; rotation < 4; rotation++)
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
            catch (Exception) { /* 다음 회전 시도 */ }
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
        text.Contains('\x1d') || text.StartsWith("(01)") ||
        (text.Length >= 16 && text.StartsWith("01") && text[2..16].All(char.IsDigit));

    /// <summary>검출된 GS1 바코드를 파싱해 레코드와 교차 검증한다.</summary>
    public static List<CrossCheckResult> CrossCheckHits(
        IEnumerable<BarcodeHit> hits, LabelRecord record)
    {
        var checks = new List<CrossCheckResult>();
        foreach (var hit in hits)
        {
            if (!hit.IsGs1 && !LooksGs1(hit.Text)) continue;
            Gs1Message message;
            try { message = Gs1.Parse(hit.Text); }
            catch (Gs1ParseException) { continue; }
            checks.AddRange(BarcodeCrossCheck.Check(message, record, hit.Symbology));
        }
        return checks;
    }
}
