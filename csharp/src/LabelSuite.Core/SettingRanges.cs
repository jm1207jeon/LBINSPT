// 설정 값 허용 범위의 단일 출처 — AppConfig.Normalize(로드 시 보정)와
// SettingsWindow(입력란 검증)가 같은 표를 참조한다. 리터럴을 여기 말고 다른 곳에 두지 말 것.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

/// <summary>숫자 설정 하나의 허용 범위. Path는 settings.json 경로("ocr.max_dimension").
/// 배열 항목 안의 값은 "[]"로 표기한다("fields.same_value[].min_instances").</summary>
public sealed record RangeDef(string Path, double Min, double Max, double Default,
                              bool Integer, string Unit, string Label)
{
    public int IntMin => (int)Math.Round(Min);
    public int IntMax => (int)Math.Round(Max);
    public int IntDefault => (int)Math.Round(Default);

    /// <summary>숫자를 이 범위로 잘라 넣는다 (정수 정의면 반올림).</summary>
    public double Clamp(double value)
    {
        var v = Math.Clamp(value, Min, Max);
        return Integer ? Math.Round(v) : v;
    }

    /// <summary>문자열 입력을 정수로 해석해 범위 안으로 — 해석 불가면 Default.
    /// (SettingsWindow의 ParseInt 대체용)</summary>
    public int ParseInt(string? text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                     out var v)
            ? (int)Clamp(v) : IntDefault;

    public double ParseDouble(string? text) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var v)
            ? Clamp(v) : Default;
}

public static class SettingRanges
{
    public static readonly IReadOnlyList<RangeDef> All =
    [
        new("pdf_render_zoom", 1, 8, 4.0, false, "배", "PDF 렌더 배율"),
        new("shelf_life_months", 1, 120, 36, true, "개월", "유효기간"),
        new("ocr_cache_max_entries", 50, 5000, 500, true, "건", "OCR 캐시 최대 항목"),
        new("page_image_cache_pages", 1, 20, 6, true, "페이지", "페이지 이미지 캐시"),
        new("save_scale", 0.1, 1.0, 0.5, false, "배", "결과 이미지 축소 배율"),
        new("jpeg_quality", 30, 100, 90, true, "", "결과 이미지 JPEG 품질"),
        new("ocr.max_dimension", 500, 4000, 2000, true, "px", "OCR 입력 최대 변"),
        new("ocr.jpeg_quality", 30, 100, 85, true, "", "OCR 입력 JPEG 품질"),
        new("ocr.min_confidence", 0, 100, 0, true, "%", "OCR 최소 신뢰도"),
        new("ocr.low_word_confidence", 0, 100, 70, true, "%", "저신뢰 단어 기준"),
        new("ocr.low_avg_confidence", 0, 100, 80, true, "%", "저신뢰 평균 기준"),
        new("type_learning.min_samples", 2, 100, 5, true, "건", "유형 학습 최소 표본"),
        new("overlay.thickness", 1, 12, 2, true, "px", "오버레이 선 굵기"),
        new("overlay.fill_alpha", 0, 255, 90, true, "", "오버레이 채움 불투명도"),
        new("overlay.gallery_width", 160, 1600, 460, true, "px", "필드 모아보기 폭"),
        // 배열 항목 안의 값 — Normalize가 별도 경로로 처리, Find()로 UI가 참조
        new("fields.same_value[].min_instances", 1, 20, 2, true, "개", "동일값 최소 개수"),
        new("overlay.colors[].rgba", 0, 255, 128, true, "", "오버레이 색상 성분"),
    ];

    /// <summary>ocr.engine 허용값 (소문자).</summary>
    public static readonly string[] EngineNames = ["aws", "pattern", "onnx"];
    public const string DefaultEngine = "aws";

    /// <summary>fields.disabled에 넣을 수 있는 필드 — LOT은 매칭 기준이라 제외 불가.</summary>
    public static readonly string[] DisableableFields =
        ["PRODUCTS", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN", "CHINA"];

    /// <summary>prefetch_policy 숫자 정책의 상한 (앞서 읽어 둘 페이지 수).</summary>
    public const int PrefetchPolicyMax = 50;

    /// <summary>"all" 또는 0~PrefetchPolicyMax 정수만 유효.</summary>
    public static bool IsValidPrefetchPolicy(JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.GetValue<string>() == "all";
            case JsonValueKind.Number:
                if (!TryNumber(value, out var n)) return false;
                return n == Math.Floor(n) && n >= 0 && n <= PrefetchPolicyMax;
            default:
                return false;
        }
    }

    public static RangeDef? Find(string path) =>
        All.FirstOrDefault(d => d.Path == path);

    /// <summary>JSON 숫자 노드를 double로 (JsonValue의 CLR 타입과 무관하게).</summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out value);
    }

    /// <summary>값을 범위표에 맞춘다. 보정이 필요했으면 true — fixedNode에 새 노드,
    /// note에 '이전 → 이후' 설명. 이미 유효하면 false(fixedNode는 원래 노드, note는 null).
    /// 숫자가 아닌 타입은 Default로, 범위 밖은 clamp, 정수 정의에 소수는 반올림.</summary>
    public static bool TryNormalize(RangeDef def, JsonNode? node,
                                    out JsonNode fixedNode, out string? note)
    {
        note = null;
        if (!TryNumber(node, out var value))
        {
            fixedNode = Make(def, def.Default);
            note = $"{Describe(node)} → {Format(def, def.Default)} (숫자가 아님, 기본값)";
            return true;
        }
        var fixedValue = def.Clamp(value);
        if (fixedValue == value)
        {
            fixedNode = node!;
            return false;
        }
        fixedNode = Make(def, fixedValue);
        note = value < def.Min || value > def.Max
            ? $"{Format(def, value)} → {Format(def, fixedValue)} (허용 {Format(def, def.Min)}~{Format(def, def.Max)})"
            : $"{Format(def, value)} → {Format(def, fixedValue)} (정수만 허용)";
        return true;
    }

    private static JsonNode Make(RangeDef def, double value) =>
        def.Integer ? JsonValue.Create((int)Math.Round(value)) : JsonValue.Create(value);

    private static string Format(RangeDef def, double value) =>
        def.Integer ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                    : value.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Describe(JsonNode? node) =>
        node is null ? "(없음)" : node.ToJsonString();
}
