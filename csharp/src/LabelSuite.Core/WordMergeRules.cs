// 단어 병합 규칙 — OCR이 여러 단어로 나눠 읽는 문구(예: "HANAROSTENT" "X")를
// 하나의 문장으로 합쳐 인식하도록 학습한다. OCR 로그에서 여러 단어를 선택해
// 등록하면, 이후 검사에서 같은 순서로 나란히 등장하는 해당 토큰들이
// 공백으로 연결된 한 단어(바운딩 박스는 합집합)로 치환된다.
using System.Text.Json;

namespace LabelSuite.Core;

public sealed class WordMergeRules(string? path = null)
{
    private readonly List<List<string>> _rules = Load(path);
    private readonly string? _path = path;
    private readonly object _lock = new();

    public IReadOnlyList<IReadOnlyList<string>> Rules
    {
        get { lock (_lock) return _rules.Select(r => (IReadOnlyList<string>)r.ToList()).ToList(); }
    }

    public int Count { get { lock (_lock) return _rules.Count; } }

    private static string Norm(string text) => text.Trim().ToUpperInvariant();

    public bool Add(IEnumerable<string> tokens)
    {
        var cleaned = tokens.Select(t => t.Trim())
            .Where(t => t.Length > 0).ToList();
        if (cleaned.Count < 2) return false;
        lock (_lock)
        {
            if (_rules.Any(r => r.Count == cleaned.Count
                    && r.Zip(cleaned).All(p => Norm(p.First) == Norm(p.Second))))
                return false;   // 중복 규칙
            _rules.Add(cleaned);
        }
        Save();
        return true;
    }

    public void ReplaceAll(IEnumerable<IEnumerable<string>> rules)
    {
        lock (_lock)
        {
            _rules.Clear();
            foreach (var rule in rules)
            {
                var cleaned = rule.Select(t => t.Trim())
                    .Where(t => t.Length > 0).ToList();
                if (cleaned.Count >= 2) _rules.Add(cleaned);
            }
        }
        Save();
    }

    /// <summary>같은 줄에서 규칙 순서대로 나란히 등장하는 단어들을 병합한다.</summary>
    public List<OcrWord> Apply(IReadOnlyList<OcrWord> words)
    {
        List<List<string>> rules;
        lock (_lock) rules = _rules.Select(r => r.ToList()).ToList();
        if (rules.Count == 0 || words.Count < 2) return words.ToList();

        // 읽기 순서 정렬: 줄 그룹핑(세로 중심 근접) 후 줄 안에서 x 순
        var averageHeight = Math.Max(1, words.Average(w => w.Bbox.H));
        var lineTolerance = averageHeight * 0.6;
        var byY = words.OrderBy(w => w.Bbox.Y + w.Bbox.H / 2.0).ToList();
        var lineIds = new int[byY.Count];
        var lineId = 0;
        var lineCenter = byY[0].Bbox.Y + byY[0].Bbox.H / 2.0;
        for (var i = 0; i < byY.Count; i++)
        {
            var center = byY[i].Bbox.Y + byY[i].Bbox.H / 2.0;
            if (center - lineCenter > lineTolerance) lineId++;
            lineCenter = center;
            lineIds[i] = lineId;
        }
        var ordered = byY.Select((w, i) => (Word: w, Line: lineIds[i]))
            .OrderBy(e => e.Line).ThenBy(e => e.Word.Bbox.X).ToList();

        var consumed = new bool[ordered.Count];
        var merged = new List<OcrWord>();
        foreach (var rule in rules)
            for (var start = 0; start + rule.Count <= ordered.Count; start++)
            {
                var ok = true;
                for (var k = 0; k < rule.Count && ok; k++)
                {
                    var slot = start + k;
                    ok = !consumed[slot]
                        && ordered[slot].Line == ordered[start].Line
                        && Norm(ordered[slot].Word.Text) == Norm(rule[k]);
                }
                if (!ok) continue;
                var parts = Enumerable.Range(start, rule.Count)
                    .Select(i => ordered[i].Word).ToList();
                var x0 = parts.Min(w => w.Bbox.X);
                var y0 = parts.Min(w => w.Bbox.Y);
                var x1 = parts.Max(w => w.Bbox.X + w.Bbox.W);
                var y1 = parts.Max(w => w.Bbox.Y + w.Bbox.H);
                merged.Add(new OcrWord(
                    string.Join(" ", parts.Select(w => w.Text.Trim())),
                    (x0, y0, x1 - x0, y1 - y0),
                    parts.Min(w => w.Confidence)));
                for (var k = 0; k < rule.Count; k++) consumed[start + k] = true;
            }

        var result = new List<OcrWord>();
        for (var i = 0; i < ordered.Count; i++)
            if (!consumed[i]) result.Add(ordered[i].Word);
        result.AddRange(merged);
        return result;
    }

    // ---------------- 영속 ----------------

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            List<List<string>> rules;
            lock (_lock) rules = _rules.Select(r => r.ToList()).ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(rules,
                new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder
                        .UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (IOException) { }
    }

    private static List<List<string>> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<List<string>>>(
                File.ReadAllText(path)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }
}
