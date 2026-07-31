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
        AutoRotate = true,
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
            hits.Add(new BarcodeHit(
                result.BarcodeFormat.ToString(),
                result.Text,
                (x, y, Math.Max(1, xs.Max() - x), Math.Max(1, ys.Max() - y)),
                LooksGs1(result.Text)));
        }
        return hits;
    }

    private static bool LooksGs1(string text) =>
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
