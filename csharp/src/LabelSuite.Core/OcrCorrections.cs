// OCR 교정 학습 — 사용자가 등록한 오인식→정정 사전과, 그로부터 학습한
// 문자 혼동(confusable) 규칙으로 OCR 결과를 후처리·유사 매칭한다.
using System.Text;
using System.Text.Json;

namespace LabelSuite.Core;

public sealed record CorrectionEntry(string Wrong, string Right, string? Field = null,
                                     string? LearnedAt = null);

public sealed class OcrCorrections
{
    // 내장 혼동 문자 → 대표 문자 (OCR에서 흔히 뒤바뀌는 글자들)
    private static readonly (char From, char To)[] BuiltinConfusables =
    [
        ('O', '0'), ('o', '0'), ('Q', '0'), ('D', '0'),
        ('I', '1'), ('l', '1'), ('|', '1'),
        ('Z', '2'), ('S', '5'), ('B', '8'), ('G', '6'), ('T', '7'),
    ];

    private readonly Dictionary<char, char> _canonical = [];
    private readonly List<CorrectionEntry> _entries = [];
    private readonly object _lock = new();

    public string? Path { get; }

    public OcrCorrections(string? path = null)
    {
        Path = path;
        foreach (var (from, to) in BuiltinConfusables) _canonical[from] = to;
        if (path is not null && File.Exists(path)) Load(path);
    }

    public IReadOnlyList<CorrectionEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    // ---------------- 사전 관리 ----------------

    public void Add(string wrong, string right, string? field = null)
    {
        wrong = wrong.Trim();
        right = right.Trim();
        if (wrong.Length == 0 || right.Length == 0 || wrong == right) return;
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Wrong == wrong);
            _entries.Add(new CorrectionEntry(wrong, right, field,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            LearnConfusablesFrom(wrong, right);
        }
        Save();
    }

    public void Remove(string wrong)
    {
        lock (_lock) _entries.RemoveAll(e => e.Wrong == wrong);
        Save();
    }

    public void ReplaceAll(IEnumerable<CorrectionEntry> entries)
    {
        lock (_lock)
        {
            _entries.Clear();
            foreach (var e in entries)
            {
                if (e.Wrong.Trim().Length == 0 || e.Right.Trim().Length == 0) continue;
                _entries.Add(e);
                LearnConfusablesFrom(e.Wrong, e.Right);
            }
        }
        Save();
    }

    /// <summary>가져온 학습 데이터의 교정 항목을 병합한다 (같은 오인식 값은 교체).
    /// 반환: 반영된 항목 수.</summary>
    public int ImportFrom(IEnumerable<CorrectionEntry> entries)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var e in entries)
            {
                if (e.Wrong.Trim().Length == 0 || e.Right.Trim().Length == 0
                    || e.Wrong == e.Right) continue;
                _entries.RemoveAll(x => x.Wrong == e.Wrong);
                _entries.Add(e);
                LearnConfusablesFrom(e.Wrong, e.Right);
                added++;
            }
        }
        if (added > 0) Save();
        return added;
    }

    /// <summary>같은 길이의 교정에서 서로 다른 문자쌍을 혼동 규칙으로 학습한다.</summary>
    private void LearnConfusablesFrom(string wrong, string right)
    {
        if (wrong.Length != right.Length) return;
        for (var i = 0; i < wrong.Length; i++)
        {
            var w = wrong[i];
            var r = right[i];
            if (w == r) continue;
            // 양방향 모두 같은 대표 문자로 수렴시킨다
            var canonical = Canonical(r);
            _canonical[w] = canonical;
            _canonical[r] = canonical;
        }
    }

    // ---------------- 적용 ----------------

    /// <summary>OCR 결과에 교정 사전을 적용한다 (완전 일치 단어 치환).</summary>
    public List<OcrWord> Apply(IReadOnlyList<OcrWord> words)
    {
        List<CorrectionEntry> entries;
        lock (_lock) entries = _entries.ToList();
        if (entries.Count == 0) return words.ToList();
        var map = entries.ToDictionary(e => e.Wrong, e => e.Right);
        return words.Select(w => map.TryGetValue(w.Text.Trim(), out var right)
            ? w with { Text = right } : w).ToList();
    }

    private char Canonical(char c)
    {
        c = char.ToUpperInvariant(c);
        return _canonical.TryGetValue(c, out var mapped) ? mapped
             : _canonical.TryGetValue(char.ToLowerInvariant(c), out var mapped2) ? mapped2
             : c;
    }

    /// <summary>혼동 문자를 대표 문자로 치환한 정규형 (유사 매칭용).</summary>
    public string Canonicalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(Canonical(c));
        return builder.ToString();
    }

    /// <summary>혼동 문자 차이를 무시하고 두 문자열이 같은지.</summary>
    public bool ConfusableEquals(string a, string b) =>
        Canonicalize(a) == Canonicalize(b);

    /// <summary>혼동 문자 차이를 무시하고 text가 term을 포함하는지.</summary>
    public bool ConfusableContains(string text, string term) =>
        Canonicalize(text).Contains(Canonicalize(term), StringComparison.Ordinal);

    // ---------------- 영속 ----------------

    private sealed record Dto(List<CorrectionEntry> entries);

    private void Load(string path)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (dto is null) return;
            foreach (var e in dto.entries)
            {
                _entries.Add(e);
                LearnConfusablesFrom(e.Wrong, e.Right);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    private void Save()
    {
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(
                System.IO.Path.GetFullPath(Path))!);
            List<CorrectionEntry> entries;
            lock (_lock) entries = _entries.ToList();
            File.WriteAllText(Path, JsonSerializer.Serialize(new Dto(entries),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder
                        .UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (IOException) { }
    }
}
