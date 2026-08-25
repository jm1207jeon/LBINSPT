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

/// <summary>사용자 정의 OCR 대상 필드 — 고정 문자열 또는 정규식.
/// Standard가 지정되면 라벨에서 이 값이 검출될 때 해당 규격을 자동 선택한다.</summary>
public sealed record CustomFieldDef(string Name, string Pattern, bool IsRegex,
                                    int? Expected, string? Standard = null);

/// <summary>필드 검출 허용 영역 (페이지 비율 사각형). Standard가 비어 있으면
/// 모든 규격에 적용. 영역이 등록된 필드는 영역 안의 검출만 인정한다.</summary>
public sealed record FieldZone(string Field, string Standard,
                               (double X, double Y, double W, double H) Region);

/// <summary>검사 동작 옵션 (설정에서 주입).</summary>
public sealed class InspectionOptions
{
    /// <summary>검사에서 제외할 내장 필드 (LOT은 제외 불가 — 매칭 기준).</summary>
    public HashSet<string> DisabledFields { get; init; } = [];
    public List<CustomFieldDef> CustomFields { get; init; } = [];
    /// <summary>OCR 혼동 문자(O↔0 등) 차이를 무시하고 매칭할지.</summary>
    public bool AllowConfusables { get; init; }
    public OcrCorrections? Corrections { get; init; }
    /// <summary>필드별 문자 제약 (나올 수 없는 문자 지정 → 자동 복원·에러 검출).</summary>
    public FieldCharsets Charsets { get; init; } = new();
    /// <summary>필드별 검출 허용 영역 — 등록된 필드는 영역 밖 검출을 무시한다.</summary>
    public List<FieldZone> Zones { get; init; } = [];
    /// <summary>단어 병합 규칙 (여러 OCR 단어 → 한 문장) — 교정 전에 적용.</summary>
    public WordMergeRules? Merges { get; init; }
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

    private bool TextCounts(string fieldName, string term, OcrWord word)
    {
        // 필드 문자 제약이 있으면 위반 문자를 먼저 복원 (예: 날짜의 O→0)
        var text = Options.Charsets.Repair(fieldName, word.Text.Trim());
        return !text.Contains("(01)")
            && ContainsTerm(text, term)
            && !FieldNameWords.Contains(text.ToUpperInvariant())
            && !text.ToUpperInvariant().EndsWith(':')
            && text.Length > 2
            && !ExcludedWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>바코드 리딩 기반 GTIN 매칭 — 검출된 GS1 바코드의 AI(01) 값과
    /// 대조한다. GS1 바코드가 하나라도 파싱되면 이것이 기준(OCR 폴백 안 함),
    /// 없으면 null을 반환해 OCR 경로로 폴백한다.</summary>
    private static List<TextMatch>? BarcodeGtinMatches(
        string gtin14, IReadOnlyList<BarcodeHit>? barcodes)
    {
        if (barcodes is not { Count: > 0 }) return null;
        var matches = new List<TextMatch>();
        var sawGs1 = false;
        foreach (var hit in barcodes)
        {
            Gs1Message message;
            try { message = Gs1.Parse(hit.Text); }
            catch (Gs1ParseException) { continue; }
            if (message.Get("01") is not { } gtin) continue;
            sawGs1 = true;
            if (gtin == gtin14)
                matches.Add(new TextMatch("GTIN",
                    new OcrWord($"(01){gtin}", hit.Bbox, 100), gtin14));
        }
        return sawGs1 ? matches : null;
    }

    /// <summary>GTIN 매칭 — UDI 문자열에서 AI(01) 구간만 찾아, 바운딩 박스도
    /// (01)+14자리 구간으로 잘라 반환한다 (뒤따르는 (10) 등 다른 AI는 제외).</summary>
    private TextMatch? GtinMatch(string gtin14, OcrWord word)
    {
        var text = Options.Charsets.Repair("GTIN", word.Text.Trim());
        if (Options.AllowConfusables && Options.Corrections is { } corrections)
            text = corrections.Canonicalize(text);
        var match = GtinAi.Match(text);
        if (!match.Success || match.Groups[1].Value != gtin14) return null;
        var (x, y, w, h) = word.Bbox;
        var length = Math.Max(1, text.Length);
        var subX = x + (int)((double)w * match.Index / length);
        var subW = Math.Max(1, (int)((double)w * match.Length / length));
        return new TextMatch("GTIN", word with { Bbox = (subX, y, subW, h) }, gtin14);
    }

    public List<TextMatch> CountField(string fieldName, string term, IReadOnlyList<OcrWord> words)
    {
        if (term.Length == 0) return [];
        if (fieldName == "GTIN")
            return words.Select(w => GtinMatch(term, w))
                        .Where(m => m is not null).Select(m => m!).ToList();
        return words.Where(w => TextCounts(fieldName, term, w))
                    .Select(w => new TextMatch(fieldName, w, term)).ToList();
    }

    /// <summary>검색창 매칭 — 사전 지정 필드가 아니어도 찾을 수 있도록
    /// 길이/제외어 필터 없이 관대하게 매칭한다. 공백으로 나눈 토큰별 검색.</summary>
    public List<TextMatch> CountSearch(string term, IReadOnlyList<OcrWord> words)
    {
        var tokens = term.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return [];
        return words.Where(w => tokens.Any(token =>
                w.Text.Contains(token, StringComparison.OrdinalIgnoreCase)
                || (Options.AllowConfusables && Options.Corrections is { } c
                    && c.ConfusableContains(w.Text, token))))
            .Select(w => new TextMatch("SEARCH", w, term)).ToList();
    }

    /// <summary>커스텀 필드 카운트 — 정규식이면 단어 전체 매칭, 아니면 부분 문자열 규칙.</summary>
    public List<TextMatch> CountCustomField(CustomFieldDef def, IReadOnlyList<OcrWord> words)
    {
        if (def.Pattern.Length == 0) return [];
        if (!def.IsRegex)
            return words.Where(w => TextCounts(def.Name, def.Pattern, w))
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
                                     string extraSearch = "",
                                     (int W, int H)? pageSize = null,
                                     IReadOnlyList<BarcodeHit>? barcodes = null)
    {
        // 단어 병합(문장 학습) → 교정 사전(오인식 치환) 순으로 적용
        IReadOnlyList<OcrWord> mergedWords = Options.Merges?.Apply(words) ?? words.ToList();
        IReadOnlyList<OcrWord> effective = Options.Corrections is { } corrections
            ? corrections.Apply(mergedWords) : mergedWords;

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
            // GTIN은 바코드 리딩이 있으면 그것을 기준으로 (인쇄=바코드 가정, OCR보다 정확)
            var matches = fieldName == "GTIN"
                && BarcodeGtinMatches(term, barcodes) is { } fromBarcodes
                ? fromBarcodes
                : ApplyZones(fieldName, standardName, pageSize,
                             CountField(fieldName, term, effective));
            outcome.Fields[fieldName] = new FieldResult
            {
                Field = fieldName, Term = term, Expected = expected,
                Matches = matches,
            };
        }
        foreach (var custom in Options.CustomFields)
        {
            if (custom.Name.Trim().Length == 0) continue;
            outcome.Fields[custom.Name] = new FieldResult
            {
                Field = custom.Name, Term = custom.Pattern,
                Expected = custom.Expected,
                Matches = ApplyZones(custom.Name, standardName, pageSize,
                                     CountCustomField(custom, effective)),
            };
        }
        var search = extraSearch.Trim();
        if (search.Length > 0)
            outcome.Fields["SEARCH"] = new FieldResult
            {
                Field = "SEARCH", Term = search, Expected = null,
                Matches = CountSearch(search, effective),
            };
        return outcome;
    }

    /// <summary>필드에 검출 허용 영역이 등록돼 있으면 영역 안의 검출만 남긴다
    /// (미세한 위치 이동 허용을 위해 페이지의 4% 마진 적용).</summary>
    private List<TextMatch> ApplyZones(string fieldName, string standardName,
                                       (int W, int H)? pageSize,
                                       List<TextMatch> matches)
    {
        if (pageSize is not { } size || matches.Count == 0) return matches;
        const double Margin = 0.04;
        var zones = Options.Zones.Where(z =>
            z.Field.Equals(fieldName, StringComparison.OrdinalIgnoreCase)
            && (z.Standard.Length == 0
                || z.Standard.Equals(standardName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (zones.Count == 0) return matches;
        return matches.Where(m =>
        {
            var cx = (m.Word.Bbox.X + m.Word.Bbox.W / 2.0) / Math.Max(1, size.W);
            var cy = (m.Word.Bbox.Y + m.Word.Bbox.H / 2.0) / Math.Max(1, size.H);
            return zones.Any(z =>
                cx >= z.Region.X - Margin && cx <= z.Region.X + z.Region.W + Margin
                && cy >= z.Region.Y - Margin && cy <= z.Region.Y + z.Region.H + Margin);
        }).ToList();
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
        words.Select(w => w with { Text = Options.Charsets.Repair("LOT", w.Text.Trim()) })
             .Where(w => w.Text.Length >= 4 && w.Confidence >= 30
                 && LotPatterns.Any(p => p.IsMatch(w.Text)))
             .ToList();

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
