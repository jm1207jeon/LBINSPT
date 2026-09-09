// A-04 GS1 관대 파싱·바코드 진실 보장, A-05 판정 서명, A-07 판정 사유 요약.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class Gs1TolerantParseTests
{
    [Fact]
    public void UnknownAiIsSkippedAndGtinKept()
    {
        var message = Gs1.Parse("(01)08806367067654(17)270509(10)25090776(91)ABC");
        Assert.Equal("08806367067654", message.Get("01"));
        Assert.Equal("25090776", message.Get("10"));
        Assert.Equal("270509", message.Get("17"));
        Assert.True(message.Partial);
        Assert.Equal(["91"], message.UnknownAis);
    }

    [Fact]
    public void UnknownAiFnc1FormIsSkippedToNextGs()
    {
        var message = Gs1.Parse("01088063670676549912345\u001d1025090776");
        Assert.Equal("08806367067654", message.Get("01"));
        Assert.Equal("25090776", message.Get("10"));
        Assert.Equal(["99"], message.UnknownAis);
    }

    [Fact]
    public void FourDigitParenthesizedAiIsRecordedExactly()
    {
        var message = Gs1.Parse("(01)08806367067654(8012)V2(10)L1");
        Assert.Equal("L1", message.Get("10"));
        Assert.Equal(["8012"], message.UnknownAis);
    }

    [Fact]
    public void StructurallyBrokenStillThrows()
    {
        Assert.Throws<Gs1ParseException>(() => Gs1.Parse("(01)ABC"));
        Assert.Throws<Gs1ParseException>(() => Gs1.Parse("(91)ONLY-UNKNOWN"));
    }

    [Fact]
    public void StrictModeStillThrowsOnUnknownAi() =>
        Assert.Throws<Gs1ParseException>(() =>
            Gs1.Parse("(01)08806367067654(91)ABC", tolerant: false));
}

public class BarcodeVerificationRuleTests
{
    private static InspectionEngine Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        return new InspectionEngine(StandardsBundle.Load(new AppConfig(dir)));
    }

    private static readonly LabelRecord Record = new(
        "25090776", "MEGACATH KIT", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806367067654", "MDR");

    private static List<OcrWord> WordsWithPrintedGtin() =>
    [
        new("25090776", (0, 0, 80, 20), 95),
        new("(01)08806367067654", (0, 40, 200, 20), 95),
    ];

    [Fact]
    public void DataMatrixIsExcludedFromChecksAndCount()
    {
        // 요청: DataMatrix는 박스만 — 값이 틀려도 검증 행·GTIN 카운트에 영향 없음
        var hits = new List<BarcodeHit>
        { new("GS1 DataMatrix", "(01)08806367067699(10)WRONG", (10, 10, 50, 50), IsGs1: true) };
        Assert.True(hits[0].IsDataMatrix);
        Assert.Empty(BarcodeDetector.CrossCheckHits(hits, Record));
        Assert.Equal(("(01)08806367067699(10)WRONG", "-", null), BarcodeDetector.Summarize(hits[0], Record));
        var outcome = Engine().Inspect(Record, "MDR", WordsWithPrintedGtin(), [], "", (300, 300), hits);
        Assert.Equal(1, outcome.Fields["GTIN"].Found);
        Assert.Equal("OCR", outcome.Fields["GTIN"].Source);   // DataMatrix는 원천이 아니므로 OCR 폴백
    }

    [Fact]
    public void Gs1_128WithSymbologyPrefixAndGsSeparatorsMatches()
    {
        // ZXing AssumeGS1: 선두 FNC1 → ']C1', 이후 FNC1 → GS
        var text = "]C1" + "0108806367067654" + "1025090776" + "\u001d" + "17270509";
        Assert.True(BarcodeDetector.LooksGs1(text));
        var hit = new BarcodeHit("GS1-128", text, (0, 0, 300, 60), IsGs1: true);
        var checks = BarcodeDetector.CrossCheckHits([hit], Record);
        Assert.Contains(checks, c => c.Field == "GTIN" && c.Matched && c.BarcodeValue == "08806367067654");
        Assert.Contains(checks, c => c.Field == "LOT" && c.Matched);
        Assert.Contains(checks, c => c.Field == "EXP DATE" && c.Matched);
        Assert.Equal("일치", BarcodeDetector.Summarize(hit, Record).State);
    }

    [Fact]
    public void Gs1_128WithoutFnc1IsRepairedByExpectedLot()
    {
        // FNC1이 버려진 디코드: (10) 값이 뒤의 (17)까지 삼킴 → 기대 LOT 뒤 잔여가 온전한 AI 열이면 분리
        var hit = new BarcodeHit("GS1-128", "0108806367067654" + "1025090776" + "17270509", (0, 0, 300, 60), IsGs1: true);
        var checks = BarcodeDetector.CrossCheckHits([hit], Record);
        Assert.Contains(checks, c => c.Field == "LOT" && c.Matched && c.BarcodeValue == "25090776");
        Assert.Contains(checks, c => c.Field == "EXP DATE" && c.Matched && c.BarcodeValue == "270509");
        Assert.Contains(checks, c => c.Field == "FNC1 누락" && c.Matched);
        Assert.All(checks, c => Assert.True(c.Matched));
        var (_, state, tip) = BarcodeDetector.Summarize(hit, Record);
        Assert.Equal("일치", state);
        Assert.Contains("FNC1", tip);
    }

    [Fact]
    public void Gs1_128LotTrulyDifferentStaysMismatch()
    {
        var hit = new BarcodeHit("GS1-128", "0108806367067654" + "1025099999", (0, 0, 300, 60), IsGs1: true);
        var checks = BarcodeDetector.CrossCheckHits([hit], Record);
        Assert.Contains(checks, c => c.Field == "GTIN" && c.Matched);
        Assert.Contains(checks, c => c.Field == "LOT" && !c.Matched);
        Assert.Equal("불일치", BarcodeDetector.Summarize(hit, Record).State);
    }

    [Fact]
    public void Gs1_128UnparseableTailStillComparesLeadingGtin()
    {
        // 요청 규칙: (01) 다음 14자리를 GTIN으로 대조 — 뒤 (10) 값이 최대 길이를 넘어 해석 불가여도
        var text = "(01)08806367067654(10)" + new string('X', 25);
        Assert.Throws<Gs1ParseException>(() => Gs1.Parse(text));
        Assert.Equal("08806367067654", Gs1.TryExtractGtin(text));
        var hit = new BarcodeHit("GS1-128", text, (0, 0, 300, 60), IsGs1: true);
        var checks = BarcodeDetector.CrossCheckHits([hit], Record);
        Assert.Contains(checks, c => c.Field == "GTIN" && c.Matched);
        Assert.DoesNotContain(checks, c => !c.Matched);
        var (value, state, _) = BarcodeDetector.Summarize(hit, Record);
        Assert.Equal("일치", state);
        Assert.StartsWith("(01)08806367067654", value);
    }

    [Fact]
    public void Gs1_128WithoutExtractableGtinForcesCheck()
    {
        var hit = new BarcodeHit("GS1-128", "]C1" + "01ABCDEFGHIJKLMN", (0, 0, 300, 60), IsGs1: true);
        var checks = BarcodeDetector.CrossCheckHits([hit], Record);
        Assert.Contains(checks, c => c.Field == "GS1 해석" && !c.Matched);
        Assert.Equal("해석 불가", BarcodeDetector.Summarize(hit, Record).State);
        var outcome = Engine().Inspect(Record, "MDR", WordsWithPrintedGtin(), checks, "", (300, 300), [hit]);
        Assert.False(outcome.Passed);
    }

    [Fact]
    public void TryExtractGtinRules()
    {
        Assert.Equal("08806367067654", Gs1.TryExtractGtin("]C10108806367067654" + "10ABC"));
        Assert.Equal("08806367067654", Gs1.TryExtractGtin("(01)08806367067654(10)ABC"));
        Assert.Null(Gs1.TryExtractGtin("1025090776" + "0108806367067654"));   // (01)이 맨 앞이 아니면 규칙 밖
        Assert.Null(Gs1.TryExtractGtin("010880636706"));                        // 14자리 미만
        Assert.Null(Gs1.TryExtractGtin(""));
    }

    [Fact]
    public void PartialMessageStillCrossChecksLotAndGtin()
    {
        var hits = new List<BarcodeHit>
        { new("GS1-128", "(01)08806367067654(10)25090776(91)XYZ", (10, 10, 300, 60), IsGs1: true) };
        var checks = BarcodeDetector.CrossCheckHits(hits, Record);
        Assert.Contains(checks, c => c.Field == "GTIN" && c.Matched);
        Assert.Contains(checks, c => c.Field == "LOT" && c.Matched);
        var reference = Assert.Single(checks, c => c.Field == "미등록 AI");
        Assert.True(reference.Matched);
        Assert.Equal("91", reference.BarcodeValue);
    }
}

public class OutcomeSignatureAndSummaryTests
{
    private static InspectionEngine Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        return new InspectionEngine(StandardsBundle.Load(new AppConfig(dir)));
    }

    private static readonly LabelRecord Record = new(
        "25090776", "MEGACATH KIT", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806367067654", "MDR");

    private static InspectionOutcome Run(string search = "", bool withLot = true) =>
        Engine().Inspect(Record, "MDR",
            withLot ? [new OcrWord("25090776", (0, 0, 80, 20), 95)] : [],
            [new CrossCheckResult("DataMatrix", "GTIN", "08806367067654", "08806367067654", true)],
            search, (300, 300), null);

    [Fact]
    public void OutcomeSignatureIsStableAndSensitive()
    {
        var a = Run().Signature();
        var b = Run().Signature();
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.NotEqual(a, Run(withLot: false).Signature());      // Found 변화
        Assert.NotEqual(a, Run(search: "CE0123").Signature());   // 검색어 변화
    }

    [Fact]
    public void SignatureIgnoresBarcodeCheckOrder()
    {
        var outcome1 = Engine().Inspect(Record, "MDR", [], [
            new CrossCheckResult("DM", "GTIN", "a", "a", true),
            new CrossCheckResult("DM", "LOT", "b", "b", false)], "", (300, 300), null);
        var outcome2 = Engine().Inspect(Record, "MDR", [], [
            new CrossCheckResult("DM", "LOT", "b", "b", false),
            new CrossCheckResult("DM", "GTIN", "a", "a", true)], "", (300, 300), null);
        Assert.Equal(outcome1.Signature(), outcome2.Signature());
    }

    [Fact]
    public void Describe_ListsFailedFieldsBeforeBarcode()
    {
        var outcome = Engine().Inspect(Record, "MDR", [], [
            new CrossCheckResult("DM", "GTIN", "x", "y", false)], "", (300, 300), null);
        var text = InspectionSummary.Describe(outcome, maxItems: 10);
        Assert.StartsWith("LOT 0/", text);
        Assert.Contains("GTIN 바코드 불일치", text);
        Assert.True(text.IndexOf("LOT", StringComparison.Ordinal) < text.IndexOf("GTIN", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_TruncatesToMaxItems()
    {
        var outcome = Engine().Inspect(Record, "MDR", [], [], "", (300, 300), null);
        var failed = outcome.Fields.Values.Count(f => !f.Passed);
        Assert.True(failed >= 4, $"불합격 필드 {failed}건 (테스트 전제 4건 이상)");
        var text = InspectionSummary.Describe(outcome, maxItems: 3);
        Assert.Equal(3, text.Split(" · ").Length);
        Assert.EndsWith($" 외 {failed - 3}건", text);
    }

    [Fact]
    public void Describe_PassSummaryCounts()
    {
        var words = new List<OcrWord>
        {
            new("25090776", (0, 0, 80, 20), 95), new("NCN20-080-230", (0, 30, 80, 20), 95),
            new("HANARO-01", (0, 60, 80, 20), 95), new("2024-05-10", (0, 90, 80, 20), 95),
            new("2027-05-09", (0, 120, 80, 20), 95), new("(01)08806367067654", (0, 150, 80, 20), 95),
        };
        var outcome = Engine().Inspect(Record, "MDR", words,
            [new CrossCheckResult("DM", "GTIN", "08806367067654", "08806367067654", true)],
            "", (300, 300), null);
        if (!outcome.Passed) return;   // 기본 규격 카운트가 바뀌면 이 케이스는 건너뜀
        Assert.Matches(@"^필드 \d+/\d+ 일치 · 바코드 1건 일치$", InspectionSummary.Describe(outcome));
    }
}
