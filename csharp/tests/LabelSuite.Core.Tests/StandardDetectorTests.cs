// 라벨 문서번호 → 규격 자동 판별 (기본 standards.json: MDR=PML-001(Rev.1), MDD=PML-001(Rev.0),
// BSC=BSL-01(Rev.5), A00, A02, 중국).
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class StandardDetectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly StandardsBundle _bundle;

    public StandardDetectorTests() => _bundle = StandardsBundle.Load(new AppConfig(_directory));
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static OcrWord W(string text, int x = 700, int y = 400, int w = 90, int h = 20, int conf = 95) =>
        new(text, (x, y, w, h), conf);

    private StandardDetection? Detect(params OcrWord[] words) =>
        StandardDetector.Detect(_bundle.Standards.Values, words);

    [Fact]
    public void SignaturesAreDerivedFromDisplayNames()
    {
        var sigs = StandardDetector.Signatures(_bundle.Standards.Values).ToDictionary(s => s.Standard);
        Assert.Equal(("PML-001", "1"), (sigs["MDR"].DocNumber, sigs["MDR"].Rev));
        Assert.Equal(("PML-001", "0"), (sigs["MDD"].DocNumber, sigs["MDD"].Rev));
        Assert.Equal(("BSL-01", "5"), (sigs["BSC"].DocNumber, sigs["BSC"].Rev));
        Assert.Equal((null, "A00"), (sigs["A00"].DocNumber, sigs["A00"].Rev));
        Assert.False(sigs.ContainsKey("중국"));   // 라틴/숫자 없는 표시명은 감지 대상 아님
    }

    [Fact]
    public void RevA00SelectsA00()
    {
        var d = Detect(W("LOT"), W("Rev.A00"), W("25090776"));
        Assert.NotNull(d);
        Assert.Equal("A00", d!.Standard);
        Assert.Equal("Rev.A00", d.MatchedText);
        Assert.Equal((700, 400, 90, 20), d.Bbox);
    }

    [Fact]
    public void BslDocNumberSelectsBsc()
    {
        Assert.Equal("BSC", Detect(W("BSL-01"))!.Standard);
        var full = Detect(W("BSL-01(Rev.5)"));
        Assert.Equal("BSC", full!.Standard);
        Assert.Equal("문서번호+Rev", full.Basis);
    }

    [Fact]
    public void SharedDocNumberNeedsRevToDisambiguate()
    {
        var ambiguous = Detect(W("PML-001"));
        Assert.NotNull(ambiguous);
        Assert.True(ambiguous!.Ambiguous);
        Assert.Null(ambiguous.Standard);
        Assert.Equal(["MDR", "MDD"], ambiguous.Candidates.OrderByDescending(c => c).ToArray());

        Assert.Equal("MDR", Detect(W("PML-001(Rev.1)"))!.Standard);
        Assert.Equal("MDD", Detect(W("PML-001(Rev.0)"))!.Standard);
        // 두 단어로 분리된 경우 — 같은 줄에서 이웃하면 결합해 인식
        Assert.Equal("MDR", Detect(W("PML-001", x: 700), W("(Rev.1)", x: 795))!.Standard);
        // Rev.10은 Rev.1이 아니다
        Assert.True(Detect(W("PML-001(Rev.10)"))!.Ambiguous);
    }

    [Fact]
    public void OcrConfusionsAreTolerated()
    {
        Assert.Equal("A00", Detect(W("Rev.AOO"))!.Standard);
        Assert.Equal("A00", Detect(W("REV A0O"))!.Standard);
        Assert.Equal("BSC", Detect(W("BSL-O1"))!.Standard);
        Assert.Equal("BSC", Detect(W("BSL 01"))!.Standard);
    }

    [Fact]
    public void SplitRevWordsAreJoined()
    {
        Assert.Equal("A00", Detect(W("Rev.", x: 700, w: 30), W("A00", x: 735))!.Standard);
    }

    [Fact]
    public void NoDocumentNumberReturnsNull()
    {
        Assert.Null(Detect(W("LOT"), W("25090776"), W("HANAROSTENT")));
        Assert.Null(StandardDetector.Detect(_bundle.Standards.Values, []));
    }

    [Fact]
    public void ExplicitDocPatternsWin()
    {
        var specs = new List<StandardSpec>
        {
            new("MDR", new Dictionary<string, int>(), "yyyy-MM-dd", false, "PML-001(Rev.1)"),
            new("X1", new Dictionary<string, int>(), "yyyy-MM-dd", false, "특수라벨", ["FORM-\\d{3}"]),
        };
        var d = StandardDetector.Detect(specs, [W("FORM-123")]);
        Assert.Equal("X1", d!.Standard);
        Assert.Equal("패턴", d.Basis);
    }

    [Fact]
    public void RevOnTheLineBelowDocNumberIsPaired()
    {
        // PML-001 라벨: 문서번호 아랫줄에 Rev.0 / Rev.1 — 두 줄을 읽어 매칭
        var mdr = Detect(W("LOT"), W("PML-001", x: 700, y: 400), W("Rev.1", x: 700, y: 424), W("25090776"));
        Assert.Equal("MDR", mdr!.Standard);
        Assert.Equal("문서번호+Rev", mdr.Basis);
        Assert.Equal("PML-001 Rev.1", mdr.MatchedText);
        Assert.Equal((700, 400, 90, 44), mdr.Bbox);   // 두 줄을 감싸는 박스

        var mdd = Detect(W("PML-001", x: 700, y: 400), W("Rev.0", x: 704, y: 426));
        Assert.Equal("MDD", mdd!.Standard);
        // OCR 혼동: Rev.O → Rev.0
        Assert.Equal("MDD", Detect(W("PML-001", x: 700, y: 400), W("Rev.O", x: 700, y: 424))!.Standard);
    }

    [Fact]
    public void AdjacentRevBeatsDistantRevOfOtherStandard()
    {
        // 페이지 다른 곳에 'Rev.0'(다른 문서의 개정)이 있어도 문서번호 바로 아래의 Rev.1이 우선
        var d = Detect(W("Rev.0", x: 100, y: 900), W("PML-001", x: 700, y: 400), W("Rev.1", x: 700, y: 424));
        Assert.Equal("MDR", d!.Standard);
        Assert.Equal("문서번호+Rev", d.Basis);
        // 인접한 Rev가 전혀 없으면 원거리 Rev로라도 판별하되 낮은 점수
        var far = Detect(W("PML-001", x: 700, y: 400), W("Rev.1", x: 100, y: 900));
        Assert.Equal("MDR", far!.Standard);
        Assert.Equal("문서번호+Rev(원거리)", far.Basis);
    }

    [Fact]
    public void RevAboveDocNumberAlsoPairs()
    {
        Assert.Equal("MDD", Detect(W("Rev.0", x: 700, y: 376), W("PML-001", x: 700, y: 400))!.Standard);
    }
}
