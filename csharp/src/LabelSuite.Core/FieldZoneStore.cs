// 필드 검출 영역(settings fields.zones) 편집 규칙 — 같은 필드·규격의 재등록 시 교체/추가,
// 개수 조회, 영역 좌표로 삭제. 항목 형식: {"field","standard","region":[x,y,w,h] (%)}.
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

public static class FieldZoneStore
{
    private const double RegionTolerance = 0.001;

    private static bool SameKey(JsonNode? node, string field, string standard) =>
        node is JsonObject obj
        && string.Equals(obj["field"]?.GetValue<string>()?.Trim() ?? "", field.Trim(),
                         StringComparison.OrdinalIgnoreCase)
        && string.Equals(obj["standard"]?.GetValue<string>()?.Trim() ?? "", standard.Trim(),
                         StringComparison.Ordinal);

    /// <summary>영역 항목을 추가한다. replace=true면 같은 field(대소문자 무시)+standard 항목을
    /// 먼저 모두 제거한다. 반환값은 제거된 항목 수.</summary>
    public static int Upsert(JsonArray zones, JsonObject zone, bool replace)
    {
        var field = zone["field"]?.GetValue<string>() ?? "";
        var standard = zone["standard"]?.GetValue<string>() ?? "";
        var removed = 0;
        if (replace)
        {
            for (var i = zones.Count - 1; i >= 0; i--)
                if (SameKey(zones[i], field, standard))
                {
                    zones.RemoveAt(i);
                    removed++;
                }
        }
        zones.Add(zone.Parent is null ? zone : zone.DeepClone());
        return removed;
    }

    /// <summary>해당 field(대소문자 무시)+standard로 등록된 영역 수.</summary>
    public static int CountFor(JsonArray zones, string field, string standard) =>
        zones.Count(z => SameKey(z, field, standard));

    /// <summary>field+standard+region(%)이 일치하는 첫 항목을 제거한다. 제거했으면 true.</summary>
    public static bool Remove(JsonArray zones, string field, string standard, double[] region)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            if (!SameKey(zones[i], field, standard)) continue;
            if (!TryRegion(zones[i]!.AsObject(), out var r) || r.Length != region.Length) continue;
            var same = true;
            for (var k = 0; k < r.Length && same; k++)
                same = Math.Abs(r[k] - region[k]) <= RegionTolerance;
            if (!same) continue;
            zones.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>region이 0~100 범위의 숫자 4개인지 검사하며 읽는다 (AppConfig.Normalize와 동일 규칙).</summary>
    public static bool TryRegion(JsonObject zone, out double[] region)
    {
        region = [];
        if (zone["region"] is not JsonArray array || array.Count != 4) return false;
        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!SettingRanges.TryNumber(array[i], out var v) || v < 0 || v > 100) return false;
            values[i] = v;
        }
        region = values;
        return true;
    }
}
