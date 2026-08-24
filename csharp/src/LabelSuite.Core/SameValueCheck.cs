// 동일값 패턴 검사 — 한 라벨 안에서 같은 값이 여러 위치(객체)에 인쇄되는
// 형식을 위한 검사. 패턴(정규식)으로 객체 인스턴스를 찾아 서로 값이 같은지
// 비교하고, 합격한 라벨의 객체 배치(기준 레이아웃)를 자동 학습해 두었다가
// 다음 라벨에서 전역 위치 이동(스캔/출력 오프셋)을 추정·보정한 뒤 객체별로
// 추적한다. 기준 위치에 값이 없으면 '객체 누락'으로 검출한다.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LabelSuite.Core;

/// <summary>동일값 규칙 — Pattern(정규식)에 맞는 모든 객체는 값이 같아야 한다.</summary>
public sealed record SameValueRule(string Name, string Pattern, int MinInstances);

public sealed record SameValueInstance(string Value, (int X, int Y, int W, int H) Bbox,
                                       (double X, double Y) Center);

public sealed record SameValueIssue(string Kind, string Detail);

public sealed class SameValueResult
{
    public required string Rule { get; init; }
    public required List<SameValueInstance> Instances { get; init; }
    public required int MinInstances { get; init; }
    public string? Consensus { get; init; }
    public List<SameValueIssue> Issues { get; init; } = [];
    /// <summary>기준 레이아웃 대비 감지된 전역 이동량 (페이지 비율).</summary>
    public (double X, double Y)? Drift { get; init; }
    public bool Passed => Issues.Count == 0;
}

public sealed class SameValueChecker(string? layoutPath = null,
                                     OcrCorrections? corrections = null)
{
    /// <summary>기준 위치 추적 허용 반경 (페이지 대각선 비율).</summary>
    public double MatchTolerance { get; set; } = 0.08;
    /// <summary>드리프트 추정에 쓰는 후보 최대 거리 (페이지 비율).</summary>
    public double DriftSearchRadius { get; set; } = 0.2;

    private readonly string? _layoutPath = layoutPath;
    private JsonObject _layouts = LoadLayouts(layoutPath);

    // ---------------- 검사 ----------------

    public List<SameValueResult> Check(string formatKey,
                                       IReadOnlyList<SameValueRule> rules,
                                       IReadOnlyList<OcrWord> words,
                                       (int W, int H) pageSize)
    {
        var results = new List<SameValueResult>();
        foreach (var rule in rules)
        {
            if (rule.Name.Trim().Length == 0 || rule.Pattern.Trim().Length == 0) continue;
            Regex regex;
            try { regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { continue; }

            var instances = FindInstances(regex, words, pageSize);
            var issues = new List<SameValueIssue>();

            // 1) 개수: 최소 인스턴스 수
            if (instances.Count < Math.Max(1, rule.MinInstances))
                issues.Add(new SameValueIssue("개수 부족",
                    $"{instances.Count}개 검출 (최소 {rule.MinInstances}개)"));

            // 2) 값 상호 비교: 다수결 값과 다른 객체 검출
            var consensus = ConsensusValue(instances);
            if (consensus is not null)
                foreach (var inst in instances)
                    if (!ValuesEqual(inst.Value, consensus))
                        issues.Add(new SameValueIssue("값 불일치",
                            $"위치({Percent(inst.Center.X)},{Percent(inst.Center.Y)})의 " +
                            $"'{inst.Value}' ≠ '{consensus}'"));

            // 3) 기준 레이아웃 추적: 전역 이동 보정 후 위치별 존재 확인
            (double X, double Y)? drift = null;
            var reference = LoadReference(formatKey, rule.Name);
            if (reference.Count > 0 && instances.Count > 0)
            {
                drift = EstimateDrift(reference, instances);
                foreach (var missing in UnmatchedReferences(reference, instances, drift.Value))
                    issues.Add(new SameValueIssue("객체 누락",
                        $"기준 위치({Percent(missing.X)},{Percent(missing.Y)})에서 " +
                        "값을 찾지 못함"));
            }

            var result = new SameValueResult
            {
                Rule = rule.Name,
                Instances = instances,
                MinInstances = rule.MinInstances,
                Consensus = consensus,
                Issues = issues,
                Drift = drift,
            };
            results.Add(result);

            // 4) 합격 시 현재 배치를 기준 레이아웃으로 갱신 (자동 학습)
            if (result.Passed && instances.Count >= Math.Max(1, rule.MinInstances))
                SaveReference(formatKey, rule.Name,
                              instances.Select(i => i.Center).ToList());
        }
        return results;
    }

    /// <summary>결과를 검사 그리드/합불에 반영하기 위한 교차검증 행으로 변환.</summary>
    public static List<CrossCheckResult> ToCrossChecks(IEnumerable<SameValueResult> results)
    {
        var checks = new List<CrossCheckResult>();
        foreach (var result in results)
        {
            if (result.Passed)
                checks.Add(new CrossCheckResult("동일값", result.Rule,
                    $"{result.Instances.Count}개 모두 일치", result.Consensus ?? "", true));
            else
                foreach (var issue in result.Issues)
                    checks.Add(new CrossCheckResult("동일값",
                        $"{result.Rule}·{issue.Kind}", issue.Detail,
                        result.Consensus ?? "", false));
        }
        return checks;
    }

    // ---------------- 내부 ----------------

    private static string Percent(double v) => $"{v * 100:F0}%";

    private List<SameValueInstance> FindInstances(Regex regex,
                                                  IReadOnlyList<OcrWord> words,
                                                  (int W, int H) pageSize)
    {
        var instances = new List<SameValueInstance>();
        var pageW = Math.Max(1, pageSize.W);
        var pageH = Math.Max(1, pageSize.H);
        foreach (var word in words)
        {
            var match = regex.Match(word.Text.Trim());
            if (!match.Success || match.Value.Length == 0) continue;
            instances.Add(new SameValueInstance(match.Value, word.Bbox,
                ((word.Bbox.X + word.Bbox.W / 2.0) / pageW,
                 (word.Bbox.Y + word.Bbox.H / 2.0) / pageH)));
        }
        return instances;
    }

    private string? ConsensusValue(List<SameValueInstance> instances)
    {
        if (instances.Count == 0) return null;
        return instances
            .GroupBy(i => corrections?.Canonicalize(i.Value) ?? i.Value)
            .OrderByDescending(g => g.Count())
            .First().First().Value;
    }

    private bool ValuesEqual(string a, string b) =>
        a == b || (corrections?.ConfusableEquals(a, b) ?? false);

    /// <summary>기준 위치 각각의 최근접 인스턴스 이동량 중앙값 = 전역 오프셋.</summary>
    private (double X, double Y) EstimateDrift(
        List<(double X, double Y)> reference, List<SameValueInstance> instances)
    {
        var dxs = new List<double>();
        var dys = new List<double>();
        foreach (var refCenter in reference)
        {
            var nearest = instances
                .OrderBy(i => Distance(i.Center, refCenter)).First();
            if (Distance(nearest.Center, refCenter) <= DriftSearchRadius)
            {
                dxs.Add(nearest.Center.X - refCenter.X);
                dys.Add(nearest.Center.Y - refCenter.Y);
            }
        }
        if (dxs.Count == 0) return (0, 0);
        return (Median(dxs), Median(dys));
    }

    private List<(double X, double Y)> UnmatchedReferences(
        List<(double X, double Y)> reference, List<SameValueInstance> instances,
        (double X, double Y) drift)
    {
        var unmatched = new List<(double, double)>();
        var used = new bool[instances.Count];
        foreach (var refCenter in reference)
        {
            var shifted = (X: refCenter.X + drift.X, Y: refCenter.Y + drift.Y);
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < instances.Count; i++)
            {
                if (used[i]) continue;
                var distance = Distance(instances[i].Center, shifted);
                if (distance < bestDistance) { bestDistance = distance; bestIndex = i; }
            }
            if (bestIndex >= 0 && bestDistance <= MatchTolerance) used[bestIndex] = true;
            else unmatched.Add(refCenter);
        }
        return unmatched;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid]
             : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    // ---------------- 기준 레이아웃 영속 ----------------

    private static string Key(string formatKey, string ruleName) =>
        $"{formatKey}|{ruleName}";

    private List<(double X, double Y)> LoadReference(string formatKey, string ruleName)
    {
        var centers = new List<(double, double)>();
        if (_layouts[Key(formatKey, ruleName)] is JsonArray array)
            foreach (var node in array)
                if (node is JsonArray pair && pair.Count == 2)
                    centers.Add((pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
        return centers;
    }

    private void SaveReference(string formatKey, string ruleName,
                               List<(double X, double Y)> centers)
    {
        _layouts[Key(formatKey, ruleName)] = new JsonArray(centers
            .Select(c => (JsonNode)new JsonArray(
                JsonValue.Create(Math.Round(c.X, 4)),
                JsonValue.Create(Math.Round(c.Y, 4)))).ToArray());
        if (_layoutPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.GetFullPath(_layoutPath))!);
            File.WriteAllText(_layoutPath, _layouts.ToJsonString());
        }
        catch (IOException) { }
    }

    private static JsonObject LoadLayouts(string? path)
    {
        if (path is null || !File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or JsonException) { return new JsonObject(); }
    }
}
