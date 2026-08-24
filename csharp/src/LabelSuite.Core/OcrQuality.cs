// 페이지 OCR 신뢰도 평가 — 유의미하게 낮으면 알람을 띄우기 위한 판정.
namespace LabelSuite.Core;

public sealed record OcrQualityReport(
    double Average, int WordCount, int LowCount, double LowRatio,
    bool IsPoor, string Summary)
{
    public static readonly OcrQualityReport Empty =
        new(0, 0, 0, 0, false, "OCR 단어 없음");
}

public static class OcrQuality
{
    /// <summary>페이지 OCR 신뢰도를 평가한다.
    /// 평균이 minAverage 미만이거나, lowWordThreshold 미만 단어 비율이
    /// maxLowRatio를 넘으면 '신뢰도 낮음'으로 판정.</summary>
    public static OcrQualityReport Assess(IReadOnlyList<OcrWord> words,
                                          int minAverage = 80,
                                          int lowWordThreshold = 70,
                                          double maxLowRatio = 0.3)
    {
        if (words.Count == 0) return OcrQualityReport.Empty;
        var average = words.Average(w => (double)w.Confidence);
        var lowCount = words.Count(w => w.Confidence < lowWordThreshold);
        var lowRatio = (double)lowCount / words.Count;
        var isPoor = average < minAverage || lowRatio > maxLowRatio;
        var summary = isPoor
            ? $"OCR 신뢰도 낮음 — 평균 {average:F0}%, " +
              $"저신뢰({lowWordThreshold}% 미만) 단어 {lowCount}/{words.Count}개. " +
              "스캔 품질(해상도·대비)을 확인하세요."
            : $"OCR 신뢰도 정상 (평균 {average:F0}%)";
        return new OcrQualityReport(average, words.Count, lowCount, lowRatio,
                                    isPoor, summary);
    }

    /// <summary>하이라이트 대상 저신뢰 단어 목록.</summary>
    public static List<OcrWord> LowConfidenceWords(IReadOnlyList<OcrWord> words,
                                                   int lowWordThreshold = 70) =>
        words.Where(w => w.Confidence < lowWordThreshold).ToList();
}
