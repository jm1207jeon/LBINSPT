// 동일값 패턴 검사(위치 추적)와 라벨 유형 학습·이상 감지 테스트.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class SameValueCheckerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public SameValueCheckerTests() { Directory.CreateDirectory(_directory); }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LayoutPath => Path.Combine(_directory, "layouts.json");

    private static readonly SameValueRule LotRule = new("LOT 반복", @"^\d{8}$", 3);
    private const string Format = "HANARO-01|MDR";
    private static readonly (int W, int H) Page = (1000, 1000);

    private static OcrWord At(string text, int x, int y) =>
        new(text, (x, y, 100, 20), 95);

    [Fact]
    public void AllEqualInstancesPass()
    {
        var checker = new SameValueChecker();
        var results = checker.Check(Format, [LotRule],
            [At("25090776", 100, 100), At("25090776", 700, 100),
             At("25090776", 400, 800), At("HANARO", 100, 300)], Page);
        var result = Assert.Single(results);
        Assert.True(result.Passed);
        Assert.Equal(3, result.Instances.Count);
        Assert.Equal("25090776", result.Consensus);
    }

    [Fact]
    public void ValueMismatchBetweenObjectsIsDetected()
    {
        var checker = new SameValueChecker();
        var results = checker.Check(Format, [LotRule],
            [At("25090776", 100, 100), At("25090776", 700, 100),
             At("25090777", 400, 800)], Page);
        var result = Assert.Single(results);
        Assert.False(result.Passed);
        Assert.Contains(result.Issues, i => i.Kind == "값 불일치"
            && i.Detail.Contains("25090777"));
    }

    [Fact]
    public void TooFewInstancesFail()
    {
        var checker = new SameValueChecker();
        var results = checker.Check(Format, [LotRule],
            [At("25090776", 100, 100), At("25090776", 700, 100)], Page);
        Assert.Contains(Assert.Single(results).Issues, i => i.Kind == "개수 부족");
    }

    [Fact]
    public void ConfusableDifferencesAreNotMismatch()
    {
        // OCR 오독(O↔0)은 불일치가 아님 — 실제 인쇄 불일치만 잡는다
        var checker = new SameValueChecker(corrections: new OcrCorrections());
        var results = checker.Check(Format, [LotRule with { Pattern = @"^[\dO]{8}$" }],
            [At("25090776", 100, 100), At("25O90776", 700, 100),
             At("25090776", 400, 800)], Page);
        Assert.True(Assert.Single(results).Passed);
    }

    [Fact]
    public void DriftIsCompensatedAndMissingObjectDetected()
    {
        var checker = new SameValueChecker(LayoutPath);
        // 1) 합격 라벨 → 배치(3개 위치) 자동 학습
        Assert.True(checker.Check(Format, [LotRule],
            [At("25090776", 100, 100), At("25090776", 700, 100),
             At("25090776", 400, 800)], Page)[0].Passed);

        // 2) 전체가 3% 이동한 라벨 — 드리프트 보정으로 여전히 합격
        var shifted = checker.Check(Format, [LotRule],
            [At("25090776", 130, 130), At("25090776", 730, 130),
             At("25090776", 430, 830)], Page)[0];
        Assert.True(shifted.Passed);
        Assert.NotNull(shifted.Drift);
        Assert.Equal(0.03, shifted.Drift!.Value.X, 2);

        // 3) 새 검사기(재시작 흉내)에서 한 객체가 사라진 라벨 → 객체 누락 검출
        var restarted = new SameValueChecker(LayoutPath);
        var missing = restarted.Check(Format, [LotRule with { MinInstances = 2 }],
            [At("25090776", 130, 130), At("25090776", 730, 130)], Page)[0];
        Assert.False(missing.Passed);
        Assert.Contains(missing.Issues, i => i.Kind == "객체 누락");
    }

    [Fact]
    public void CrossCheckRowsReflectPassAndIssues()
    {
        var pass = new SameValueResult
        {
            Rule = "LOT 반복", MinInstances = 2, Consensus = "25090776",
            Instances = [new SameValueInstance("25090776", (0, 0, 1, 1), (0.1, 0.1)),
                         new SameValueInstance("25090776", (0, 0, 1, 1), (0.9, 0.1))],
        };
        var rows = SameValueChecker.ToCrossChecks([pass]);
        Assert.True(Assert.Single(rows).Matched);
        Assert.Equal("동일값", rows[0].Source);

        var fail = new SameValueResult
        {
            Rule = "LOT 반복", MinInstances = 2, Consensus = "25090776",
            Instances = [],
            Issues = [new SameValueIssue("개수 부족", "0개 검출 (최소 2개)")],
        };
        Assert.False(Assert.Single(SameValueChecker.ToCrossChecks([fail])).Matched);
    }
}

public class LabelTypeProfilerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LabelTypeProfilerTests() { Directory.CreateDirectory(_directory); }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ProfilePath => Path.Combine(_directory, "profiles.json");

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");
    private const string Format = "HANARO-01|MDR";

    private static List<OcrWord> Words(params string[] texts) =>
        texts.Select((t, i) => new OcrWord(t, (0, i * 30, 100, 20), 95)).ToList();

    private static readonly string[] NormalTokens =
        ["HANAROSTENT", "STERILE", "MFG", "EXP", "KEEP", "DRY", "SINGLE", "USE"];

    [Fact]
    public void VariableValuesAreExcludedFromProfile()
    {
        var tokens = LabelTypeProfiler.StaticTokens(
            Words("HANAROSTENT", "25090776", "2024-05-10", "(01)08806173612345",
                  "123", "STERILE"), Record);
        Assert.Contains("HANAROSTENT", tokens);
        Assert.Contains("STERILE", tokens);
        Assert.DoesNotContain("25090776", tokens);           // LOT 값
        Assert.DoesNotContain("2024-05-10", tokens);         // 날짜
        Assert.DoesNotContain("(01)08806173612345", tokens); // GTIN
        Assert.DoesNotContain("123", tokens);                // 숫자류
    }

    [Fact]
    public void LearningPhaseDoesNotAlarm()
    {
        var profiler = new LabelTypeProfiler(ProfilePath);
        var report = profiler.Check(Format, Words("WHATEVER", "TOKENS"), Record);
        Assert.False(report.IsAnomaly);
        Assert.Contains("학습 중", report.Summary);
    }

    [Fact]
    public void MatchingLabelIsNormalAfterLearning()
    {
        var profiler = new LabelTypeProfiler(ProfilePath);
        for (var i = 0; i < 5; i++) profiler.Learn(Format, Words(NormalTokens), Record);
        var report = profiler.Check(Format, Words(NormalTokens), Record);
        Assert.False(report.IsAnomaly);
        Assert.Contains("일치", report.Summary);
    }

    [Fact]
    public void MissingStaticTokenRaisesAnomaly()
    {
        var profiler = new LabelTypeProfiler(ProfilePath);
        for (var i = 0; i < 5; i++) profiler.Learn(Format, Words(NormalTokens), Record);
        var withoutSterile = NormalTokens.Where(t => t != "STERILE").ToArray();
        var report = profiler.Check(Format, Words(withoutSterile), Record);
        Assert.True(report.IsAnomaly);
        Assert.Contains("STERILE", report.MissingTokens);
    }

    [Fact]
    public void ManyUnseenTokensRaiseAnomaly()
    {
        var profiler = new LabelTypeProfiler(ProfilePath);
        for (var i = 0; i < 5; i++) profiler.Learn(Format, Words(NormalTokens), Record);
        var mutated = NormalTokens.Concat(["NEUE", "AUFSCHRIFT", "WARNUNG"]).ToArray();
        var report = profiler.Check(Format, Words(mutated), Record);
        Assert.True(report.IsAnomaly);
        Assert.Equal(3, report.NewTokens.Count);
        // 한두 개의 낯선 토큰(노이즈)은 이상이 아님
        var minor = profiler.Check(Format,
            Words(NormalTokens.Concat(["NOISE"]).ToArray()), Record);
        Assert.False(minor.IsAnomaly);
    }

    [Fact]
    public void ProfilePersistsAcrossInstances()
    {
        var profiler = new LabelTypeProfiler(ProfilePath);
        for (var i = 0; i < 5; i++) profiler.Learn(Format, Words(NormalTokens), Record);
        var reloaded = new LabelTypeProfiler(ProfilePath);
        Assert.Equal(5, reloaded.SampleCount(Format));
        Assert.True(reloaded.Check(Format,
            Words(NormalTokens.Where(t => t != "MFG").ToArray()), Record).IsAnomaly);
    }
}
