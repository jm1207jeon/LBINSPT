// GTIN 기대 1개(1개 이상 일치 = 합격) · 라벨 유형 학습의 잡음 토큰 배제.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class GtinAtLeastOneTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly InspectionEngine _engine;
    public GtinAtLeastOneTests() => _engine = new InspectionEngine(StandardsBundle.Load(new AppConfig(_directory)));
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly LabelRecord Record = new(
        "25090776", "MEGACATH KIT", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806367067654", "MDR");

    [Fact]
    public void GtinExpectedIsOneRegardlessOfStandardCount()
    {
        // MDR 규격의 GTIN 카운트는 8이지만 기대는 1 — 하나만 잡혀도 합격
        var outcome = _engine.Inspect(Record, "MDR", [new OcrWord("(01)08806367067654", (0, 0, 200, 20), 95)]);
        var gtin = outcome.Fields["GTIN"];
        Assert.Equal(1, gtin.Expected);
        Assert.True(gtin.AtLeastOne);
        Assert.Equal("≥1", gtin.ExpectedDisplay);
        Assert.True(gtin.Passed);
    }

    [Fact]
    public void ManyGtinMatchesStillPass()
    {
        var words = Enumerable.Range(0, 8)
            .Select(i => new OcrWord("(01)08806367067654", (0, i * 40, 200, 20), 95)).ToList();
        var outcome = _engine.Inspect(Record, "MDR", words);
        Assert.Equal(8, outcome.Fields["GTIN"].Found);
        Assert.True(outcome.Fields["GTIN"].Passed);
        Assert.Contains("GTIN: 8/≥1 OK", Describe(outcome));
    }

    private static string Describe(InspectionOutcome outcome)
    {
        // 저장 이미지 요약 박스와 CSV가 쓰는 표기 — ExpectedDisplay
        var f = outcome.Fields["GTIN"];
        return $"GTIN: {f.Found}/{f.ExpectedDisplay} {(f.Passed ? "OK" : "NG")}";
    }

    [Fact]
    public void ZeroGtinFailsAndSummarySaysAtLeastOne()
    {
        var hits = new List<BarcodeHit> { new("GS1-128", "]C10108806367067699" + "1025090776", (0, 0, 300, 60), true) };
        var outcome = _engine.Inspect(Record, "MDR", [new OcrWord("(01)08806367067654", (0, 0, 200, 20), 95)], barcodes: hits);
        Assert.False(outcome.Fields["GTIN"].Passed);   // 바코드 GTIN 불일치 → 0건 (OCR로 덮지 않음)
        Assert.Contains("GTIN 0/≥1", InspectionSummary.Describe(outcome, maxItems: 10));
        var csv = InspectionCsv.Row(0, outcome, null, "x.pdf", DateTime.Now, "v");
        Assert.Contains("0/≥1", csv);
    }
}

public class ProfilerNoiseTests
{
    private static readonly LabelRecord Record = new(
        "25090776", "MEGACATH KIT", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806367067654", "MDR");

    [Theory]
    [InlineData("|", false)]
    [InlineData("—", false)]
    [InlineData(")(", false)]
    [InlineData("..", false)]
    [InlineData("A1", false)]          // 3자 미만
    [InlineData("12A", false)]         // 글자 1개
    [InlineData("STERILE", true)]
    [InlineData("Rx", false)]
    [InlineData("LOT", true)]
    [InlineData("MDR-2017/745", true)]
    public void WordLikeFilter(string token, bool expected) =>
        Assert.Equal(expected, LabelTypeProfiler.IsWordLike(token));

    [Fact]
    public void NoiseAndLowConfidenceTokensAreNotLearned()
    {
        var words = new List<OcrWord>
        {
            new("STERILE", (0, 0, 60, 20), 96),
            new("|", (0, 30, 5, 20), 90),
            new("GHOST", (0, 60, 60, 20), 40),      // 저신뢰
            new("KEEP DRY", (0, 90, 80, 20), 95),
            new("25090776", (0, 120, 80, 20), 95),  // LOT(가변)
        };
        var tokens = LabelTypeProfiler.StaticTokens(words, Record);
        Assert.Equal(["KEEP DRY", "STERILE"], tokens.OrderBy(t => t).ToArray());
    }

    [Fact]
    public void FewNewTokensDoNotAlarm()
    {
        var profiler = new LabelTypeProfiler { MinSamples = 2 };
        var baseWords = new List<OcrWord> { new("STERILE", (0, 0, 60, 20), 96), new("KEEP DRY", (0, 30, 80, 20), 95) };
        profiler.Learn("k", baseWords, Record);
        profiler.Learn("k", baseWords, Record);
        // 처음 보는 문구 2개(OCR 변동 수준) — 이상 아님
        var report = profiler.Check("k", [.. baseWords, new OcrWord("STERlLE", (0, 60, 60, 20), 90), new OcrWord("NEWTXT", (0, 90, 60, 20), 90)], Record);
        Assert.False(report.IsAnomaly);
        // 고정 문구 누락은 여전히 이상
        Assert.True(profiler.Check("k", [new OcrWord("STERILE", (0, 0, 60, 20), 96)], Record).IsAnomaly);
    }
}

public class LearningSettingsTests
{
    [Fact]
    public void LearningModulesDefaultOffAndLegacyKeyIsHonoured()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(directory);
            var config = new AppConfig(directory);
            foreach (var module in new[] { "glyph_patterns", "label_type", "same_value_layout", "master_db" })
                Assert.False(config.LearningEnabled(module), module);
            Assert.False(config.Section("type_learning").ContainsKey("enabled"));   // 구 키는 기본값에서 제거

            // 구버전 설정: type_learning.enabled=true → label_type 켜짐으로 이어받음 (다른 모듈은 여전히 꺼짐)
            File.WriteAllText(Path.Combine(directory, "settings.json"),
                """{"schema_version": 2, "type_learning": {"enabled": true, "min_samples": 5}}""");
            var legacy = new AppConfig(directory);
            Assert.True(legacy.LearningEnabled("label_type"));
            Assert.False(legacy.LearningEnabled("glyph_patterns"));
            // 새 키가 있으면 새 키가 우선
            legacy.Section("learning")["label_type"] = false;
            Assert.False(legacy.LearningEnabled("label_type"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SameValueCheckerDoesNotLearnUnlessEnabled()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "layouts.json");
        var checker = new SameValueChecker(path, new OcrCorrections(null));
        var rule = new SameValueRule("LOT", @"^\d{8}$", 2);
        var words = new List<OcrWord> { new("25090776", (10, 10, 80, 20), 95), new("25090776", (10, 200, 80, 20), 95) };
        checker.Check("k", [rule], words, (400, 400));
        Assert.False(File.Exists(path));                 // 기본: 배치 저장 안 함
        checker.AutoLearn = true;
        checker.Check("k", [rule], words, (400, 400));
        Assert.True(File.Exists(path));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

