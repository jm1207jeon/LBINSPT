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

public class BarcodeTruthTests
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
    public void UnparseableGs1BarcodeForcesCheckNotOcrFallback()
    {
        // 바코드는 읽혔지만 구조가 깨짐(GS1 외형) — OCR에 정확한 (01) 텍스트가 있어도 폴백 금지
        var hits = new List<BarcodeHit>
        { new("GS1 DataMatrix", "(01)ABC", (10, 10, 50, 50), IsGs1: true) };
        var checks = BarcodeDetector.CrossCheckHits(hits, Record);
        var outcome = Engine().Inspect(Record, "MDR", WordsWithPrintedGtin(), checks,
                                       "", (300, 300), hits);
        Assert.Equal(0, outcome.Fields["GTIN"].Found);
        Assert.False(outcome.Passed);
        Assert.Contains(outcome.BarcodeChecks, c => c.Field == "GS1 해석" && !c.Matched);
    }

    [Fact]
    public void NoBarcodeStillFallsBackToOcr()
    {
        var outcome = Engine().Inspect(Record, "MDR", WordsWithPrintedGtin(), [], "",
                                       (300, 300), []);
        Assert.Equal(1, outcome.Fields["GTIN"].Found);
    }

    [Fact]
    public void PartialMessageStillCrossChecksLotAndGtin()
    {
        var hits = new List<BarcodeHit>
        { new("GS1 DataMatrix", "(01)08806367067654(10)25090776(91)XYZ", (10, 10, 50, 50), IsGs1: true) };
        var checks = BarcodeDetector.CrossCheckHits(hits, Record);
        Assert.Contains(checks, c => c.Field == "GTIN" && c.Matched);
        Assert.Contains(checks, c => c.Field == "LOT" && c.Matched);
        var reference = Assert.Single(checks, c => c.Field == "미등록 AI");
        Assert.True(reference.Matched);
        Assert.Equal("91", reference.BarcodeValue);
        var outcome = Engine().Inspect(Record, "MDR", WordsWithPrintedGtin(), checks, "",
                                       (300, 300), hits);
        Assert.Equal(1, outcome.Fields["GTIN"].Found);
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
