// 판정 사유 한 줄 — 화면 배지 아래 사유줄과 저장 이미지 요약 박스가 같은 문구를 쓴다.
namespace LabelSuite.Core;

public static class InspectionSummary
{
    /// <summary>확인 필요면 원인을 우선순위(불합격 필드 → 바코드 불일치 → 기타)로 최대 maxItems개,
    /// 넘치면 ' 외 n건'. 합격이면 '필드 n/n 일치 · 바코드 m건 일치'.</summary>
    public static string Describe(InspectionOutcome outcome, int maxItems = 3)
    {
        if (outcome.Passed)
        {
            var gating = outcome.Fields.Values.Count(f => f.Gating);
            var barcodes = outcome.BarcodeChecks.Count(c => c.Field != "미등록 AI");
            return barcodes > 0
                ? $"필드 {gating}/{gating} 일치 · 바코드 {barcodes}건 일치"
                : $"필드 {gating}/{gating} 일치";
        }
        var items = new List<string>();
        foreach (var field in outcome.Fields.Values.Where(f => !f.Passed))
            items.Add(field.ExtractionFailed
                ? $"{field.Field} 추출 실패(바코드·OCR 없음)"
                : $"{field.Field} {field.Found}/{field.Expected}");
        foreach (var check in outcome.BarcodeChecks.Where(c => !c.Matched))
            items.Add(check.Field switch
            {
                "GS1 해석" => "GS1 해석 불가",
                "LOT 매칭" => "LOT 미매칭",
                _ => $"{check.Field} 바코드 불일치",
            });
        if (items.Count == 0) return "확인 필요";
        var shown = items.Take(maxItems).ToList();
        var rest = items.Count - shown.Count;
        return string.Join(" · ", shown) + (rest > 0 ? $" 외 {rest}건" : "");
    }
}
