// 검사 엔진 — 위젯과 분리된 순수 모델 (파이썬 core/inspection.py 포팅).
// 합불은 InspectionOutcome.Passed가 유일한 원천이다.
using System.Globalization;
using System.Text.RegularExpressions;

namespace LabelSuite.Core;

public sealed record OcrWord(string Text, (int X, int Y, int W, int H) Bbox, int Confidence);

public sealed record TextMatch(string Field, OcrWord Word, string MatchedTerm);

public sealed class FieldResult
{
    public required string Field { get; init; }
    public required string Term { get; init; }
    public int? Expected { get; init; }
    public List<TextMatch> Matches { get; init; } = [];
    public int Found => Matches.Count;
    public bool Gating => Expected is > 0 && Term.Length > 0;
    public bool Passed => !Gating || Found == Expected;
}

public sealed record CrossCheckResult(
    string Source, string Field, string BarcodeValue, string ExpectedValue, bool Matched);

public sealed class InspectionOutcome
{
    public required LabelRecord Record { get; init; }
    public required StandardSpec Standard { get; init; }
    public Dictionary<string, FieldResult> Fields { get; } = [];
    public List<CrossCheckResult> BarcodeChecks { get; init; } = [];
    public bool Passed => Fields.Values.All(f => f.Passed) && BarcodeChecks.All(c => c.Matched);
    public IEnumerable<TextMatch> AllMatches => Fields.Values.SelectMany(f => f.Matches);
}

public sealed record LotMatchResult(
    string Lot, string Candidate, string MatchType, int Confidence, double Score = 0);

/// <summary>사용자 정의 OCR 대상 필드 — 고정 문자열 또는 정규식.</summary>
public sealed record CustomFieldDef(string Name, string Pattern, bool IsRegex,
                                    int? Expected);

/// <summary>검사 동작 옵션 (설정에서 주입).</summary>
public sealed class InspectionOptions
{
    /// <summary>검사에서 제외할 내장 필드 (LOT은 제외 불가 — 매칭 기준).</summary>
    public HashSet<string> DisabledFields { get; init; } = [];
    public List<CustomFieldDef> CustomFields { get; init; } = [];
    /// <summary>OCR 혼동 문자(O↔0 등) 차이를 무시하고 매칭할지.</summary>
    public bool AllowConfusables { get; init; }
    public OcrCorrections? Corrections { get; init; }
}

public sealed class InspectionEngine(StandardsBundle standards,
                                     InspectionOptions? options = null)
{
    public StandardsBundle Standards { get; } = standards;
    public InspectionOptions Options { get; } = options ?? new InspectionOptions();

    private static readonly string[] FieldNameWords =
        ["LOT", "PN", "REF", "MFG DATE", "EXP DATE", "PRODUCTS"];
    private static readonly string[] ExcludedWords =
        ["Lasso", "Stent", "Delivery", "Device", "Use"];
    private static readonly Regex GtinAi = new(@"\(01\)(\d{14})(?=\(|\s|$)", RegexOptions.Compiled);
    private static readonly string[] DateParseFormats =
        ["yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd", "yyyyMMdd"];

    private static string ReformatDate(string value, string dateFormat)
    {
        var text = (value ?? "").Trim();
        foreach (var format in DateParseFormats)
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var parsed))
                return parsed.ToString(dateFormat, CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>필드 → 검사 값. 날짜는 규격 포맷으로 재표기, CHINA는 REF 접두로 해석.</summary>
    public Dictionary<string, string> BuildSearchTerms(LabelRecord record, StandardSpec standard)
    {
        var terms = new Dictionary<string, string>
        {
            ["LOT"] = record.Lot.Trim(),
            ["PRODUCTS"] = record.Products.Trim(),
            ["PN"] = record.Pn.Trim(),
            ["REF"] = record.Ref.Trim(),
            ["MFG DATE"] = ReformatDate(record.MfgDate, standard.DateFormat),
            ["EXP DATE"] = ReformatDate(record.ExpDate, standard.DateFormat),
            ["GTIN"] = record.Gtin.Length > 0 ? Schema.NormalizeGtin14(record.Gtin) : "",
        };
        if (standard.UsesChinaField)
            terms["CHINA"] = Standards.ChinaCodeForRef(record.Ref) ?? "";
        return terms;
    }

    private bool ContainsTerm(string text, string term)
    {
        if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        return Options.AllowConfusables && Options.Corrections is { } corrections
            && corrections.ConfusableContains(text, term);
    }

    private bool TextCounts(string term, OcrWord word)
    {
        var text = word.Text.Trim();
        return !text.Contains("(01)")
            && ContainsTerm(text, term)
            && !FieldNameWords.Contains(text.ToUpperInvariant())
            && !text.ToUpperInvariant().EndsWith(':')
            && text.Length > 2
            && !ExcludedWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private bool GtinCounts(string gtin14, OcrWord word)
    {
        var text = word.Text.Trim();
        if (Options.AllowConfusables && Options.Corrections is { } corrections)
            text = corrections.Canonicalize(text);
        var match = GtinAi.Match(text);
        return match.Success && match.Groups[1].Value == gtin14;
    }

    public List<TextMatch> CountField(string fieldName, string term, IReadOnlyList<OcrWord> words)
    {
        if (term.Length == 0) return [];
        Func<string, OcrWord, bool> matcher = fieldName == "GTIN" ? GtinCounts : TextCounts;
        return words.Where(w => matcher(term, w))
                    .Select(w => new TextMatch(fieldName, w, term)).ToList();
    }

    /// <summary>커스텀 필드 카운트 — 정규식이면 단어 전체 매칭, 아니면 부분 문자열 규칙.</summary>
    public List<TextMatch> CountCustomField(CustomFieldDef def, IReadOnlyList<OcrWord> words)
    {
        if (def.Pattern.Length == 0) return [];
        if (!def.IsRegex)
            return words.Where(w => TextCounts(def.Pattern, w))
                        .Select(w => new TextMatch(def.Name, w, def.Pattern)).ToList();
        Regex regex;
        try { regex = new Regex(def.Pattern, RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return []; }
        return words.Where(w => regex.IsMatch(w.Text.Trim()))
                    .Select(w => new TextMatch(def.Name, w, def.Pattern)).ToList();
    }

    public InspectionOutcome Inspect(LabelRecord record, string standardName,
                                     IReadOnlyList<OcrWord> words,
                                     IReadOnlyList<CrossCheckResult>? barcodeChecks = null,
                                     string extraSearch = "")
    {
        // 교정 사전 적용 (오인식 단어 치환)
        IReadOnlyList<OcrWord> effective = Options.Corrections is { } corrections
            ? corrections.Apply(words) : words;

        var standard = Standards.Spec(standardName);
        var terms = BuildSearchTerms(record, standard);
        var outcome = new InspectionOutcome
        {
            Record = record, Standard = standard,
            BarcodeChecks = barcodeChecks?.ToList() ?? [],
        };
        foreach (var (fieldName, term) in terms)
        {
            if (fieldName != "LOT" && Options.DisabledFields.Contains(fieldName))
                continue;   // 사용자가 제외한 필드 (LOT은 매칭 기준이라 항상 유지)
            int? expected = standard.Counts.TryGetValue(fieldName, out var count)
                ? count : null;
            outcome.Fields[fieldName] = new FieldResult
            {
                Field = fieldName, Term = term, Expected = expected,
                Matches = CountField(fieldName, term, effective),
            };
        }
        foreach (var custom in Options.CustomFields)
        {
            if (custom.Name.Trim().Length == 0) continue;
            outcome.Fields[custom.Name] = new FieldResult
            {
                Field = custom.Name, Term = custom.Pattern,
                Expected = custom.Expected,
                Matches = CountCustomField(custom, effective),
            };
        }
        var search = extraSearch.Trim();
        if (search.Length > 0)
            outcome.Fields["SEARCH"] = new FieldResult
            {
                Field = "SEARCH", Term = search, Expected = null,
                Matches = CountField("SEARCH", search, effective),
            };
        return outcome;
    }

    // ---------- LOT 자동 매칭 ----------

    private static readonly Regex[] LotPatterns =
    [
        new(@"^\d{8}$", RegexOptions.Compiled),
        new(@"^\d{2}[A-Z]\d{3}$", RegexOptions.Compiled),
        new(@"^[A-Z]{2}\d{4}$", RegexOptions.Compiled),
        new(@"^[A-Z0-9]{5,10}$", RegexOptions.Compiled),
    ];

    public List<OcrWord> ExtractLotCandidates(IReadOnlyList<OcrWord> words) =>
        words.Where(w =>
        {
            var text = w.Text.Trim();
            return text.Length >= 4 && w.Confidence >= 30
                && LotPatterns.Any(p => p.IsMatch(text));
        }).ToList();

    public LotMatchResult? MatchLot(IReadOnlyList<OcrWord> words,
                                    IReadOnlyList<LabelRecord> records)
    {
        var available = records.Where(r => r.Lot.Length > 0).Select(r => r.Lot).ToList();
        if (available.Count == 0) return null;
        var candidates = ExtractLotCandidates(words);

        foreach (var candidate in candidates)
        {
            var text = candidate.Text.Trim();
            if (available.Contains(text))
                return new LotMatchResult(text, text, "exact", candidate.Confidence);
        }

        LotMatchResult? best = null;
        foreach (var candidate in candidates)
        {
            var text = candidate.Text.Trim();
            if (text.Length < 4 || !text.All(char.IsDigit)) continue;
            var suffix = text[^4..];
            var suffixMatches = available
                .Where(lot => lot.Length >= 4 && lot[^4..] == suffix).ToList();
            if (suffixMatches.Count == 1)
                return new LotMatchResult(suffixMatches[0], text, "suffix_unique",
                                          candidate.Confidence);
            foreach (var lot in suffixMatches)
            {
                var score = SimilarityScore(text, lot, candidate.Confidence);
                if (best is null || score > best.Score)
                    best = new LotMatchResult(lot, text, "suffix_best",
                                              candidate.Confidence, score);
            }
        }
        return best;
    }

    /// <summary>가중 유사도: 문자열 40% + 접두 30% + OCR 신뢰도 20% + 길이 10%.
    /// 문자열 유사도는 difflib.SequenceMatcher.ratio()와 동일한 Ratcliff/Obershelp.</summary>
    public static double SimilarityScore(string candidate, string target, int ocrConfidence)
    {
        var stringSimilarity = RatcliffObershelp(candidate, target);
        var minLen = Math.Min(candidate.Length, target.Length);
        var matchingPrefix = 0;
        for (var i = 0; i < minLen && candidate[i] == target[i]; i++) matchingPrefix++;
        var prefixScore = minLen > 0 ? (double)matchingPrefix / minLen : 0;
        var confidenceScore = ocrConfidence / 100.0;
        var maxLen = Math.Max(candidate.Length, target.Length);
        var lengthScore = maxLen > 0
            ? 1 - Math.Abs(candidate.Length - target.Length) / (double)maxLen : 0;
        return (stringSimilarity * 0.4 + prefixScore * 0.3
                + confidenceScore * 0.2 + lengthScore * 0.1) * 100;
    }

    private static double RatcliffObershelp(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        var matches = MatchingCharacters(a, 0, a.Length, b, 0, b.Length);
        return 2.0 * matches / (a.Length + b.Length);
    }

    private static int MatchingCharacters(string a, int aLo, int aHi,
                                          string b, int bLo, int bHi)
    {
        // 최장 공통 부분 문자열을 찾아 좌우 구간을 재귀 처리 (difflib 동작과 동일)
        int bestI = aLo, bestJ = bLo, bestSize = 0;
        var lengths = new Dictionary<int, int>();
        for (var i = aLo; i < aHi; i++)
        {
            var newLengths = new Dictionary<int, int>();
            for (var j = bLo; j < bHi; j++)
            {
                if (a[i] != b[j]) continue;
                var length = lengths.TryGetValue(j - 1, out var prev) ? prev + 1 : 1;
                newLengths[j] = length;
                if (length > bestSize)
                {
                    bestSize = length;
                    bestI = i - length + 1;
                    bestJ = j - length + 1;
                }
            }
            lengths = newLengths;
        }
        if (bestSize == 0) return 0;
        return bestSize
            + MatchingCharacters(a, aLo, bestI, b, bLo, bestJ)
            + MatchingCharacters(a, bestI + bestSize, aHi, b, bestJ + bestSize, bHi);
    }
}

public static class BarcodeCrossCheck
{
    private static readonly string[] DateParseFormats =
        ["yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd", "yyyyMMdd"];

    private static DateOnly? ParseRecordDate(string? value)
    {
        foreach (var format in DateParseFormats)
            if (DateTime.TryParseExact((value ?? "").Trim(), format,
                                       CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var parsed))
                return DateOnly.FromDateTime(parsed);
        return null;
    }

    /// <summary>바코드 GS1 값과 목록 레코드 교차 검증. 바코드에 없는 AI는 검증하지 않는다.</summary>
    public static List<CrossCheckResult> Check(Gs1Message message, LabelRecord record,
                                               string source)
    {
        var checks = new List<CrossCheckResult>();

        if (message.Get("01") is { } gtin)
        {
            var expected = record.Gtin.Length > 0 ? Schema.NormalizeGtin14(record.Gtin) : "";
            checks.Add(new CrossCheckResult(source, "GTIN", gtin, expected,
                                            expected.Length > 0 && gtin == expected));
        }
        if (message.Get("10") is { } lot)
        {
            var expected = record.Lot.Trim();
            checks.Add(new CrossCheckResult(source, "LOT", lot, expected,
                                            expected.Length > 0 && lot == expected));
        }
        foreach (var (ai, field, recordValue) in new[]
                 { ("11", "MFG DATE", record.MfgDate), ("17", "EXP DATE", record.ExpDate) })
        {
            if (message.Get(ai) is not { } raw) continue;
            var barcodeDate = Gs1.ParseDate(raw);
            var expectedDate = ParseRecordDate(recordValue);
            var matched = barcodeDate is not null && expectedDate is not null
                && barcodeDate == expectedDate;
            checks.Add(new CrossCheckResult(
                source, field, raw,
                expectedDate?.ToString("yyyy-MM-dd") ?? recordValue ?? "", matched));
        }
        return checks;
    }
}
