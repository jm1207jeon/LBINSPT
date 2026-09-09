// 라벨 유형 학습 — 검사할 때마다 전체 OCR 토큰을 유형(PN+규격)별로 축적해
// "이 유형의 라벨에 항상 인쇄되는 고정 문구" 프로필을 만든다. 표본이 충분히
// 쌓인 뒤 기존과 다른 유형(고정 문구 누락, 처음 보는 문구 다수)이 나타나면
// 이상으로 보고한다 → UI에서 알람 표시.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LabelSuite.Core;

public sealed record TypeCheckReport(bool IsAnomaly, int SampleCount,
                                     List<string> MissingTokens, List<string> NewTokens,
                                     string Summary);

public sealed class LabelTypeProfiler(string? path = null)
{
    /// <summary>이 표본 수 미만이면 학습 중 — 이상 판정하지 않음.</summary>
    public int MinSamples { get; set; } = 5;
    /// <summary>이 비율 이상 등장한 토큰 = 유형의 고정 문구.</summary>
    public double StaticThreshold { get; set; } = 0.8;
    /// <summary>처음 보는 토큰이 이 수 이상이면 이상으로 판정.</summary>
    public int NewTokenAlarm { get; set; } = 3;
    /// <summary>이 신뢰도 미만의 OCR 단어는 유형 토큰으로 쓰지 않는다 (잡음이 '처음 보는 문구'로 튀지 않게).</summary>
    public static int MinTokenConfidence { get; set; } = 75;

    private static readonly Regex NumericLike =
        new(@"^[\d\.\-/():%]+$", RegexOptions.Compiled);

    /// <summary>유형 토큰으로 쓸 만한 '단어'인가 — 3자 이상, 글자·숫자가 60% 이상, 글자(문자) 2개 이상.
    /// "|", "—", ")(", ".·." 같은 OCR 잡음과 기호 조각을 배제한다.</summary>
    public static bool IsWordLike(string text)
    {
        if (text.Length < 3) return false;
        var alnum = text.Count(char.IsLetterOrDigit);
        if (alnum < text.Length * 0.6) return false;
        return text.Count(char.IsLetter) >= 2;
    }

    private readonly string? _path = path;
    private JsonObject _profiles = Load(path);

    // ---------------- 토큰화 ----------------

    /// <summary>가변 값(LOT·날짜·GTIN·숫자류)을 제외한 고정 문구 토큰 집합.</summary>
    public static HashSet<string> StaticTokens(IEnumerable<OcrWord> words,
                                               LabelRecord record)
    {
        var variable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddVariable(string? value)
        {
            if (value is { Length: > 0 }) variable.Add(value.Trim());
        }
        AddVariable(record.Lot);
        AddVariable(record.MfgDate);
        AddVariable(record.ExpDate);
        if (record.Gtin.Length > 0) AddVariable(Schema.NormalizeGtin14(record.Gtin));

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            var text = word.Text.Trim().ToUpperInvariant();
            if (word.Confidence < MinTokenConfidence) continue;   // 저신뢰 = 잡음 가능성
            if (!IsWordLike(text)) continue;                      // 기호 조각·짧은 토큰 배제
            if (NumericLike.IsMatch(text)) continue;              // 숫자/날짜류 = 가변
            if (variable.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase)))
                continue;                                          // 레코드 가변 값 포함
            tokens.Add(text);
        }
        return tokens;
    }

    // ---------------- 검사 / 학습 ----------------

    public int SampleCount(string formatKey) =>
        _profiles[formatKey] is JsonObject p ? p["samples"]?.GetValue<int>() ?? 0 : 0;

    /// <summary>현재 라벨이 축적된 유형 프로필과 일치하는지 검사.</summary>
    public TypeCheckReport Check(string formatKey, IEnumerable<OcrWord> words,
                                 LabelRecord record)
    {
        var current = StaticTokens(words, record);
        var samples = SampleCount(formatKey);
        if (samples < MinSamples)
            return new TypeCheckReport(false, samples, [], [],
                $"유형 학습 중 ({samples}/{MinSamples})");

        var counts = TokenCounts(formatKey);
        var staticTokens = counts
            .Where(pair => (double)pair.Value / samples >= StaticThreshold)
            .Select(pair => pair.Key).ToList();

        var missing = staticTokens.Where(t => !current.Contains(t))
            .OrderBy(t => t).ToList();
        var fresh = current.Where(t => !counts.ContainsKey(t))
            .OrderBy(t => t).ToList();

        // 처음 보는 문구는 '여러 개'일 때만 이상 — 한두 개는 OCR 변동(붙여 읽기·오인식)일 가능성이 높다
        var anomaly = missing.Count > 0 || fresh.Count >= NewTokenAlarm;
        var summary = anomaly
            ? "기존 유형과 다름 — " +
              string.Join(" / ", new[]
              {
                  missing.Count > 0 ? $"고정 문구 {missing.Count}개 누락" : null,
                  fresh.Count > 0 ? $"처음 보는 문구 {fresh.Count}개" : null,
              }.Where(s => s is not null))
            : $"기존 유형과 일치 (표본 {samples}건)";
        return new TypeCheckReport(anomaly, samples, missing, fresh, summary);
    }

    /// <summary>현재 라벨을 유형 표본으로 축적한다.</summary>
    public void Learn(string formatKey, IEnumerable<OcrWord> words, LabelRecord record)
    {
        var current = StaticTokens(words, record);
        if (_profiles[formatKey] is not JsonObject profile)
        {
            profile = new JsonObject { ["samples"] = 0, ["tokens"] = new JsonObject() };
            _profiles[formatKey] = profile;
        }
        var tokens = profile["tokens"]!.AsObject();
        foreach (var token in current)
            tokens[token] = (tokens[token]?.GetValue<int>() ?? 0) + 1;
        var samples = (profile["samples"]?.GetValue<int>() ?? 0) + 1;

        // 오래된 표본의 영향 감쇠 — 유형이 서서히 바뀌어도 적응
        if (samples > 200)
        {
            samples /= 2;
            foreach (var key in tokens.Select(pair => pair.Key).ToList())
            {
                var halved = (tokens[key]?.GetValue<int>() ?? 0) / 2;
                if (halved <= 0) tokens.Remove(key);
                else tokens[key] = halved;
            }
        }
        profile["samples"] = samples;
        Save();
    }

    private Dictionary<string, int> TokenCounts(string formatKey)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_profiles[formatKey] is JsonObject profile
            && profile["tokens"] is JsonObject tokens)
            foreach (var (token, count) in tokens)
                counts[token] = count?.GetValue<int>() ?? 0;
        return counts;
    }

    // ---------------- 영속 ----------------

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, _profiles.ToJsonString());
        }
        catch (IOException) { }
    }

    private static JsonObject Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or JsonException) { return new JsonObject(); }
    }
}
