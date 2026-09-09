// 단어 병합 학습과 바코드 리딩 기반 GTIN 검출 테스트.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class WordMergeRulesTests
{
    private static OcrWord At(string text, int x, int y) =>
        new(text, (x, y, text.Length * 20, 30), 95);

    [Fact]
    public void MergesAdjacentWordsOnSameLine()
    {
        var rules = new WordMergeRules();
        Assert.True(rules.Add(["HANAROSTENT", "X"]));
        var result = rules.Apply(
            [At("LOT", 0, 0), At("HANAROSTENT", 0, 50), At("X", 240, 50),
             At("REF", 0, 100)]);
        Assert.Equal(3, result.Count);
        var merged = Assert.Single(result, w => w.Text == "HANAROSTENT X");
        Assert.Equal(0, merged.Bbox.X);
        Assert.Equal(260, merged.Bbox.W);   // 240 + 1*20
    }

    [Fact]
    public void DoesNotMergeAcrossLines()
    {
        var rules = new WordMergeRules();
        rules.Add(["HANAROSTENT", "X"]);
        // 두 단어가 다른 줄에 있으면 병합하지 않는다
        var result = rules.Apply([At("HANAROSTENT", 0, 0), At("X", 0, 200)]);
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, w => w.Text.Contains(' '));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndDuplicateRulesRejected()
    {
        var rules = new WordMergeRules();
        Assert.True(rules.Add(["Single", "Use", "Only"]));
        Assert.False(rules.Add(["SINGLE", "USE", "ONLY"]));   // 중복
        var result = rules.Apply(
            [At("SINGLE", 0, 0), At("USE", 140, 0), At("ONLY", 220, 0)]);
        Assert.Equal("SINGLE USE ONLY", Assert.Single(result).Text);
    }

    [Fact]
    public void ChainedMergeRulesApplyAcrossPasses()
    {
        var rules = new WordMergeRules();
        rules.Add(["SINGLE", "USE"]);
        rules.Add(["SINGLE USE", "ONLY"]);   // 병합 결과를 다시 참조
        var result = rules.Apply(
            [At("SINGLE", 0, 0), At("USE", 140, 0), At("ONLY", 220, 0)]);
        Assert.Equal("SINGLE USE ONLY", Assert.Single(result).Text);
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            new WordMergeRules(path).Add(["KEEP", "DRY"]);
            var reloaded = new WordMergeRules(path);
            Assert.Equal(1, reloaded.Count);
            Assert.Single(reloaded.Apply([At("KEEP", 0, 0), At("DRY", 100, 0)]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EngineAppliesMergesBeforeMatching()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var merges = new WordMergeRules();
            merges.Add(["MEGACATH", "KIT"]);
            var engine = new InspectionEngine(
                StandardsBundle.Load(new AppConfig(directory)),
                new InspectionOptions
                {
                    Merges = merges,
                    CustomFields = [new CustomFieldDef("제품명", "MEGACATH KIT",
                                                       false, 1)],
                });
            var record = new LabelRecord("25090776", "MEGACATH KIT", "HANARO-01",
                "NCN20-080-230", "2024-05-10", "2027-05-09", "08806173612345", "MDR");
            // 병합 전에는 "MEGACATH KIT"이 두 단어라 매칭 불가 →
            // 병합 규칙 적용 후 한 문장으로 합쳐져 매칭된다
            var outcome = engine.Inspect(record, "MDR",
                [At("MEGACATH", 0, 0), At("KIT", 180, 0)]);
            Assert.Equal(1, outcome.Fields["제품명"].Found);
            // 기본 검출 필드는 6종 — PRODUCTS는 기대 횟수 없으면 검사·표시 제외
            Assert.False(outcome.Fields.ContainsKey("PRODUCTS"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

public class BarcodeGtinTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly InspectionEngine _engine;

    public BarcodeGtinTests()
    {
        _engine = new InspectionEngine(StandardsBundle.Load(new AppConfig(_directory)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    [Fact]
    public void DataMatrixDoesNotAffectGtinCount()
    {
        // DataMatrix는 박스만 표시 — 값이 다르든 같든 GTIN 카운트에 영향 없음 (인쇄 텍스트 기준)
        var hits = new List<BarcodeHit>
        {
            new("GS1 DataMatrix", "(01)08806173612399(10)25090776", (100, 500, 80, 80), IsGs1: true),
        };
        var outcome = _engine.Inspect(Record, "MDR",
            [new OcrWord("(01)08806173612345", (0, 0, 100, 10), 95)], barcodes: hits);
        Assert.Equal(1, outcome.Fields["GTIN"].Found);
        Assert.Equal((0, 0), (outcome.Fields["GTIN"].Matches[0].Word.Bbox.X,
                              outcome.Fields["GTIN"].Matches[0].Word.Bbox.Y));
    }

    [Fact]
    public void Gs1_128BarcodeIsTheFirstGtinSource()
    {
        // 요청 순서: ① 바코드 판독 → ② OCR. GS1-128에서 GTIN이 나오면 그것이 원천(박스 = 바코드 위치), OCR 텍스트는 세지 않는다
        var hits = new List<BarcodeHit>
        {
            new("GS1-128", "]C10108806173612345" + "1025090776", (100, 500, 300, 60), IsGs1: true),
        };
        var outcome = _engine.Inspect(Record, "MDR",
            [new OcrWord("(01)08806173612345", (0, 0, 100, 10), 95)], barcodes: hits);
        var gtin = outcome.Fields["GTIN"];
        Assert.Equal(1, gtin.Found);
        Assert.Equal("바코드", gtin.Source);
        Assert.Equal((100, 500), (gtin.Matches[0].Word.Bbox.X, gtin.Matches[0].Word.Bbox.Y));
        Assert.False(gtin.ExtractionFailed);
    }

    [Fact]
    public void BarcodeGtinMismatchIsZeroNotFallback()
    {
        var hits = new List<BarcodeHit>
        {
            new("GS1-128", "]C10108806173612399" + "1025090776", (100, 500, 300, 60), IsGs1: true),
        };
        var outcome = _engine.Inspect(Record, "MDR",
            [new OcrWord("(01)08806173612345", (0, 0, 100, 10), 95)], barcodes: hits);
        Assert.Equal(0, outcome.Fields["GTIN"].Found);   // 바코드가 판독됐고 값이 다르다 — OCR로 덮지 않는다
        Assert.Equal("바코드", outcome.Fields["GTIN"].Source);
        Assert.False(outcome.Fields["GTIN"].ExtractionFailed);
    }

    [Fact]
    public void FallsBackToOcrWhenNoBarcodeGtin()
    {
        // DataMatrix만 있거나(제외 대상) 바코드가 없으면 인쇄 텍스트 OCR
        var hits = new List<BarcodeHit>
        {
            new("GS1 DataMatrix", "(01)08806173612345(10)25090776", (100, 500, 80, 80), IsGs1: true),
        };
        var outcome = _engine.Inspect(Record, "MDR",
            [new OcrWord("(01)08806173612345", (0, 0, 100, 10), 95)], barcodes: hits);
        Assert.Equal(1, outcome.Fields["GTIN"].Found);
        Assert.Equal("OCR", outcome.Fields["GTIN"].Source);
    }

    [Fact]
    public void BothSourcesMissingIsExtractionError()
    {
        var hits = new List<BarcodeHit>
        {
            new("GS1 DataMatrix", "(01)08806173612345(10)25090776", (100, 500, 80, 80), IsGs1: true),
        };
        var outcome = _engine.Inspect(Record, "MDR",
            [new OcrWord("LOT", (0, 0, 30, 10), 95)], barcodes: hits);
        var gtin = outcome.Fields["GTIN"];
        Assert.Equal(0, gtin.Found);
        Assert.Equal("없음", gtin.Source);
        Assert.True(gtin.ExtractionFailed);
        Assert.False(outcome.Passed);
        Assert.Contains("GTIN 추출 실패(바코드·OCR 없음)", InspectionSummary.Describe(outcome, maxItems: 10));
    }
}
