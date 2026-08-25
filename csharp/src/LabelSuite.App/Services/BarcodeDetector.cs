// 바코드/데이터매트릭스 검출 — ZXing.Net (순수 관리형, 네이티브 의존성 없음).
using LabelSuite.Core;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;

namespace LabelSuite.App.Services;

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

    public static List<BarcodeHit> Detect(SKBitmap image)
    {
        var hits = new List<BarcodeHit>();
        Result[]? results;
        try { results = Reader.DecodeMultiple(image); }
        catch (Exception) { return hits; }
        if (results is null) return hits;
        foreach (var result in results)
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
        return hits;
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
