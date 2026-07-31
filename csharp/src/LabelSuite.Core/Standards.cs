// 검사 규격 로딩 — standards.json이 단일 원천 (파이썬 core/standards.py 포팅).
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

public sealed record StandardSpec(
    string Name,
    IReadOnlyDictionary<string, int> Counts,
    string DateFormat,
    bool UsesChinaField);

public sealed class StandardsBundle
{
    public required IReadOnlyDictionary<string, StandardSpec> Standards { get; init; }
    public required IReadOnlyDictionary<string, string> ChinaRefMapping { get; init; }
    public required IReadOnlyDictionary<string, (byte R, byte G, byte B, byte A)> FieldColors { get; init; }

    public StandardSpec Spec(string name) =>
        Standards.TryGetValue(name, out var spec)
            ? spec : throw new KeyNotFoundException($"정의되지 않은 검사 규격: {name}");

    /// <summary>REF 접두 3자로 중국 등록번호 코드를 찾는다.</summary>
    public string? ChinaCodeForRef(string? refValue)
    {
        var prefix = (refValue ?? "").Length >= 3
            ? refValue![..3].ToUpperInvariant()
            : (refValue ?? "").ToUpperInvariant();
        return ChinaRefMapping.TryGetValue(prefix, out var code) ? code : null;
    }

    /// <summary>Python strftime 포맷(%Y-%m-%d)을 .NET 포맷으로 변환해 로드.</summary>
    private static string ConvertDateFormat(string pythonFormat) => pythonFormat
        .Replace("%Y", "yyyy").Replace("%m", "MM").Replace("%d", "dd");

    public static StandardsBundle Load(AppConfig config)
    {
        var raw = config.StandardsRaw;
        var standards = new Dictionary<string, StandardSpec>();
        if (raw["standards"] is JsonObject specs)
        {
            foreach (var (name, node) in specs)
            {
                var obj = node!.AsObject();
                var counts = new Dictionary<string, int>();
                if (obj["counts"] is JsonObject countsNode)
                    foreach (var (field, value) in countsNode)
                        counts[field] = value?.GetValue<int>() ?? 0;
                standards[name] = new StandardSpec(
                    name, counts,
                    ConvertDateFormat(obj["date_format"]?.GetValue<string>() ?? "%Y-%m-%d"),
                    obj["uses_china_field"]?.GetValue<bool>() ?? false);
            }
        }
        var china = new Dictionary<string, string>();
        if (raw["china_ref_mapping"] is JsonObject chinaNode)
            foreach (var (prefix, code) in chinaNode)
                china[prefix] = code?.GetValue<string>() ?? "";
        var colors = new Dictionary<string, (byte, byte, byte, byte)>();
        if (raw["field_colors"] is JsonObject colorsNode)
            foreach (var (field, rgba) in colorsNode)
            {
                var array = rgba!.AsArray();
                colors[field] = ((byte)array[0]!.GetValue<int>(), (byte)array[1]!.GetValue<int>(),
                                 (byte)array[2]!.GetValue<int>(), (byte)array[3]!.GetValue<int>());
            }
        return new StandardsBundle
        { Standards = standards, ChinaRefMapping = china, FieldColors = colors };
    }
}
