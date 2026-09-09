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
    /// <summary>값을 어디서 읽었는지 — "OCR"(인쇄 텍스트) / "바코드"(GS1 바코드 판독) / "없음".
    /// GTIN은 바코드 → OCR 순으로 시도한다.</summary>
    public string Source { get; init; } = "OCR";
    /// <summary>검사 대상(Gating)인데 바코드·OCR 어느 경로로도 값을 추출하지 못함 — 오류로 표시.</summary>
    public bool ExtractionFailed { get; init; }
}

public sealed record CrossCheckResult(
    string Source, string Field, string BarcodeValue, string ExpectedValue, bool Matched);

public sealed class InspectionOutcome
{
    public required LabelRecord Record { get; init; }
    public required StandardSpec Standard { get; init; }
    public Dictionary<string, FieldResult> Fields { get; } = [];
    public List<CrossCheckResult> BarcodeChecks { get; init; } = [];
    /// <summary>검사 시 사용한 추가 검색어 (판정 서명에 포함).</summary>
    public string SearchTerm { get; init; } = "";
    public bool Passed => Fields.Values.All(f => f.Passed) && BarcodeChecks.All(c => c.Matched);
    public IEnumerable<TextMatch> AllMatches => Fields.Values.SelectMany(f => f.Matches);

    /// <summary>판정 내용의 서명(SHA1 앞 16자) — 같은 페이지를 다시 방문·재검사해도 판정이 같으면
    /// 자동 저장이 중복 파일·이력을 만들지 않게 하는 비교 키. 필드/바코드 순서와 무관하다.</summary>
    public string Signature(bool includeSearch = true)
    {
        var parts = new List<string>
        {
            Standard.Name, Record.Lot, Record.Ref, Passed ? "P" : "C",
        };
        parts.AddRange(Fields.Values.OrderBy(f => f.Field, StringComparer.Ordinal)
            .Select(f => $"{f.Field}:{f.Found}/{(f.Expected is { } e ? e.ToString() : "-")}"));
        parts.AddRange(BarcodeChecks
            .OrderBy(c => c.Field, StringComparer.Ordinal).ThenBy(c => c.BarcodeValue, StringComparer.Ordinal)
            .Select(c => $"{c.Field}={c.BarcodeValue}:{(c.Matched ? 1 : 0)}"));
        if (includeSearch) parts.Add(SearchTerm.Trim());
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

/// <summary>검사자 최종 처리 — 자동 판정이 '확인 필요'인 페이지를 검사자가 육안 확인 후 합격으로 확정한 기록.
/// 자동 판정(InspectionOutcome.Passed)은 그대로 보존되고, 이 기록이 있으면 최종 판정만 바뀐다.</summary>
public sealed record InspectorVerdict(bool Passed, string By, DateTime At, string Note)
{
    public string Display => Passed ? "합격(검사자 확인)" : "부적합(검사자)";
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

    /// <summary>바코드 판독 기반 GTIN — 검증 대상 바코드(GS1-128 등, DataMatrix 제외)에서 GTIN을 뽑을 수 있으면
    /// 그것이 원천이다: 기대 GTIN과 같은 바코드마다 매치 1건(박스 = 바코드 위치). 어느 바코드에서도 GTIN을
    /// 뽑지 못하면 null → 호출 측이 OCR 텍스트로 폴백한다.</summary>
    private static List<TextMatch>? BarcodeGtinMatches(string gtin14, IReadOnlyList<BarcodeHit>? barcodes)
    {
        if (barcodes is not { Count: > 0 } || gtin14.Length == 0) return null;
        var matches = new List<TextMatch>();
        var extracted = false;
        foreach (var hit in barcodes)
        {
            if (!BarcodeDetector.IsVerifiable(hit)) continue;
            if (Gs1.TryExtractGtin(hit.Text) is not { } gtin) continue;
            extracted = true;
            if (gtin == gtin14)
                matches.Add(new TextMatch("GTIN", new OcrWord($"(01){gtin}", hit.Bbox, 100), gtin14));
        }
        return extracted ? matches : null;
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
                                     IReadOnlyList<BarcodeHit>? barcodes = null)   // GTIN 1순위 원천(바코드 판독)
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
            SearchTerm = extraSearch ?? "",
        };
        foreach (var (fieldName, term) in terms)
        {
            if (fieldName != "LOT" && Options.DisabledFields.Contains(fieldName))
                continue;   // 사용자가 제외한 필드 (LOT은 매칭 기준이라 항상 유지)
            int? expected = standard.Counts.TryGetValue(fieldName, out var count)
                ? count : null;
            // 기본 검출 필드는 LOT/PN/REF/MFG/EXP/GTIN(+중국 규격의 CHINA) —
            // PRODUCTS는 규격이 명시적으로 기대 횟수를 요구할 때만 검사한다
            if (fieldName == "PRODUCTS" && expected is null or <= 0) continue;
            // GTIN 추출 순서(요청): ① 바코드 판독(GS1-128 등 — DataMatrix 제외)의 AI(01) → ② 실패하면 인쇄 텍스트
            // OCR '(01)+14자리' → ③ 둘 다 없으면 추출 실패(오류). 다른 필드는 OCR.
            List<TextMatch> matches;
            var source = "OCR";
            if (fieldName == "GTIN" && BarcodeGtinMatches(term, barcodes) is { } fromBarcodes)
            {
                matches = fromBarcodes;
                source = "바코드";
            }
            else
            {
                matches = ApplyZones(fieldName, standardName, pageSize,
                                     CountField(fieldName, term, effective));
                if (fieldName == "GTIN" && matches.Count == 0) source = "없음";
            }
            outcome.Fields[fieldName] = new FieldResult
            {
                Field = fieldName, Term = term, Expected = expected,
                Matches = matches, Source = source,
                ExtractionFailed = fieldName == "GTIN" && source == "없음" && expected is > 0 && term.Length > 0,
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
        var search = (extraSearch ?? "").Trim();
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

    /// <summary>(10) 뒤에 붙은 잔여 문자열이 등록 AI로만 이루어진 온전한 GS1 요소열이면 그 메시지, 아니면 null.</summary>
    private static Gs1Message? TryParseTail(string tail)
    {
        if (tail.Length < 3 || !tail.All(char.IsDigit)) return null;   // 잔여가 숫자 AI 열이 아니면 그냥 LOT 불일치
        try { return Gs1.Parse(tail, tolerant: false); }
        catch (Gs1ParseException) { return null; }
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
            var matched = expected.Length > 0 && lot == expected;
            // FNC1(구분자) 없이 인쇄·디코드된 GS1-128은 가변장 (10) 값이 뒤따르는 AI(17 유효기한 등)까지
            // 삼킨다. 기대 LOT 뒤가 온전한 GS1 요소열이면 LOT 일치로 보고 나머지 AI를 분리해 대조한다.
            if (!matched && expected.Length > 0 && lot.Length > expected.Length
                && lot.StartsWith(expected, StringComparison.Ordinal)
                && TryParseTail(lot[expected.Length..]) is { } tail)
            {
                checks.Add(new CrossCheckResult(source, "LOT", expected, expected, true));
                checks.Add(new CrossCheckResult(source, "FNC1 누락", lot[expected.Length..],
                    "(참고) (10) 뒤 구분자 없이 이어진 AI를 분리해 대조", true));
                var repaired = new Gs1Message();
                foreach (var element in message.Elements.Where(e => e.Ai is not "01" and not "10"))
                    repaired.Elements.Add(element);
                repaired.Elements.AddRange(tail.Elements);
                checks.AddRange(Check(repaired, record, source));
                return checks;
            }
            checks.Add(new CrossCheckResult(source, "LOT", lot, expected, matched));
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
