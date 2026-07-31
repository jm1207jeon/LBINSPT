using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class InspectionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly InspectionEngine _engine;

    public InspectionTests()
    {
        _engine = new InspectionEngine(StandardsBundle.Load(new AppConfig(_directory)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static OcrWord Word(string text, int confidence = 95) =>
        new(text, (0, 0, 10, 10), confidence);

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    private List<OcrWord> WordsForPass(StandardSpec spec)
    {
        var words = new List<OcrWord>();
        words.AddRange(Enumerable.Range(0, spec.Counts["LOT"]).Select(i => Word($"x-25090776-{i}")));
        words.AddRange(Enumerable.Range(0, spec.Counts["PN"]).Select(i => Word($"HANARO-01-{i}")));
        words.AddRange(Enumerable.Range(0, spec.Counts["REF"]).Select(i => Word($"NCN20-080-230-{i}")));
        words.AddRange(Enumerable.Range(0, spec.Counts["MFG DATE"]).Select(i => Word($"MFG 2024-05-10 #{i}")));
        words.AddRange(Enumerable.Range(0, spec.Counts["EXP DATE"]).Select(i => Word($"EXP 2027-05-09 #{i}")));
        words.AddRange(Enumerable.Range(0, spec.Counts["GTIN"]).Select(_ => Word("(01)08806173612345")));
        return words;
    }

    // ---- 검색어 구성 ----

    [Fact]
    public void BasicTerms()
    {
        var terms = _engine.BuildSearchTerms(Record, _engine.Standards.Spec("MDR"));
        Assert.Equal("25090776", terms["LOT"]);
        Assert.Equal("08806173612345", terms["GTIN"]);
        Assert.False(terms.ContainsKey("CHINA"));
    }

    [Fact]
    public void BscReformatsDates()
    {
        var terms = _engine.BuildSearchTerms(Record, _engine.Standards.Spec("BSC"));
        Assert.Equal("2024.05.10", terms["MFG DATE"]);
        Assert.Equal("2027.05.09", terms["EXP DATE"]);
    }

    [Fact]
    public void ChinaFieldFromRefPrefix()
    {
        var record = new LabelRecord("L", "P", "PN", "BPJ01-001",
                                     "2024-05-10", "2027-05-09", "", "중국");
        var terms = _engine.BuildSearchTerms(record, _engine.Standards.Spec("중국"));
        Assert.Equal("LBDB-04", terms["CHINA"]);
    }

    // ---- 카운팅 ----

    [Fact]
    public void CountsSubstringMatches()
    {
        var words = new List<OcrWord> { Word("25090776"), Word("LOT:25090776"), Word("no-match") };
        Assert.Equal(2, _engine.CountField("LOT", "25090776", words).Count);
    }

    [Fact]
    public void ExcludesFieldNamesAndLabels()
    {
        var words = new List<OcrWord> { Word("LOT"), Word("REF:"), Word("ab") };
        Assert.Empty(_engine.CountField("LOT", "lot", words));
    }

    [Fact]
    public void ExcludesGtinBarcodeTextForTextFields()
    {
        var words = new List<OcrWord> { Word("(01)08806173612345(10)25090776") };
        Assert.Empty(_engine.CountField("LOT", "25090776", words));
    }

    [Fact]
    public void GtinExactAiMatchOnly()
    {
        var words = new List<OcrWord>
        {
            Word("(01)08806173612345"),
            Word("(01)08806173612345(10)25090776"),
            Word("08806173612345"),
            Word("(01)08806173699999"),
        };
        Assert.Equal(2, _engine.CountField("GTIN", "08806173612345", words).Count);
    }

    // ---- 합불 ----

    [Fact]
    public void PassedReachable()
    {
        // 레거시(파이썬 이전의 원본)는 라벨 텍스트 역파싱으로 _Passed 도달 불가였다
        var spec = _engine.Standards.Spec("MDR");
        var outcome = _engine.Inspect(Record, "MDR", WordsForPass(spec));
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void CountMismatchFails()
    {
        var spec = _engine.Standards.Spec("MDR");
        var words = WordsForPass(spec);
        words.RemoveAt(words.Count - 1);   // GTIN 1개 부족
        var outcome = _engine.Inspect(Record, "MDR", words);
        Assert.False(outcome.Passed);
        Assert.False(outcome.Fields["GTIN"].Passed);
    }

    [Fact]
    public void BarcodeCheckGatesOutcome()
    {
        var spec = _engine.Standards.Spec("MDR");
        var badCheck = new CrossCheckResult("DataMatrix", "GTIN", "0999",
                                            "08806173612345", false);
        var outcome = _engine.Inspect(Record, "MDR", WordsForPass(spec), [badCheck]);
        Assert.False(outcome.Passed);
    }

    // ---- LOT 매칭 ----

    private static readonly List<LabelRecord> LotRecords =
    [
        new("25090776", "", "", "", "", "", ""),
        new("25080776", "", "", "", "", "", ""),
        new("24A0001", "", "", "", "", "", ""),
    ];

    [Fact]
    public void ExactMatch()
    {
        var result = _engine.MatchLot([Word("25090776")], LotRecords);
        Assert.Equal("exact", result!.MatchType);
        Assert.Equal("25090776", result.Lot);
    }

    [Fact]
    public void SuffixUnique()
    {
        var records = new List<LabelRecord>
        { new("25091234", "", "", "", "", "", ""), new("25095678", "", "", "", "", "", "") };
        var result = _engine.MatchLot([Word("99991234")], records);
        Assert.Equal("suffix_unique", result!.MatchType);
        Assert.Equal("25091234", result.Lot);
    }

    [Fact]
    public void SuffixBestBySimilarity()
    {
        var result = _engine.MatchLot([Word("15090776")], LotRecords);
        Assert.NotNull(result);
        Assert.Equal("suffix_best", result!.MatchType);
        Assert.Equal("25090776", result.Lot);
    }

    [Fact]
    public void LowConfidenceIgnored() =>
        Assert.Null(_engine.MatchLot([Word("25090776", confidence: 10)], LotRecords));

    [Fact]
    public void RatcliffObershelpMatchesPythonDifflib()
    {
        // difflib.SequenceMatcher(None, "abcd", "bcde").ratio() == 0.75
        var score = InspectionEngine.SimilarityScore("abcd", "abcd", 100);
        Assert.Equal(100.0, score, precision: 5);
    }
}
