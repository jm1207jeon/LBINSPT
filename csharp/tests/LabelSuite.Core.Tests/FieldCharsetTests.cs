// 필드별 문자 제약 — 허용/금지 문자 지정으로 혼동 복원·에러 검출 검증.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class FieldCharsetTests
{
    [Fact]
    public void RepairsConfusablesIntoAllowedSet()
    {
        var charsets = new FieldCharsets(
            [new FieldCharsetRule("MFG DATE", "0123456789-./", "")]);
        // 날짜에 문자가 나올 수 없음 → O→0 자동 복원
        Assert.Equal("2024-05-10", charsets.Repair("MFG DATE", "2O24-O5-1O"));

        var lot = new FieldCharsets([new FieldCharsetRule("LOT", "0123456789", "")]);
        Assert.Equal("25080123", lot.Repair("LOT", "2508Ol23"));   // O→0, l→1
        Assert.Equal("25567890", lot.Repair("LOT", "2SS678gO"));   // S→5, g→9
    }

    [Fact]
    public void NoRuleMeansNoChange()
    {
        var charsets = new FieldCharsets([new FieldCharsetRule("LOT", "0123456789", "")]);
        Assert.Equal("2O24-O5-1O", charsets.Repair("MFG DATE", "2O24-O5-1O"));
        var empty = new FieldCharsets();
        Assert.Equal("2508Ol23", empty.Repair("LOT", "2508Ol23"));
        // 두 칸 모두 빈 규칙 = 제약 없음
        var blank = new FieldCharsets([new FieldCharsetRule("LOT", "", "")]);
        Assert.Equal("2508Ol23", blank.Repair("LOT", "2508Ol23"));
    }

    [Fact]
    public void AmbiguousRepairIsKept()
    {
        // 'O'만 금지: 복원 후보(0·o·Q·D)가 여러 개 → 함부로 바꾸지 않음
        var charsets = new FieldCharsets([new FieldCharsetRule("PN", "", "O")]);
        Assert.Equal("HANARO-01", charsets.Repair("PN", "HANARO-01"));
    }

    [Fact]
    public void UnrepairableCharsAreReportedAsViolations()
    {
        var charsets = new FieldCharsets(
            [new FieldCharsetRule("GTIN", "0123456789()", "")]);
        // 'X'는 복원 후보가 없음 → 위반으로 검출
        var violations = charsets.Violations("GTIN", "(01)X8806173612345");
        Assert.Single(violations);
        Assert.Equal(('X', 4), (violations[0].Ch, violations[0].Index));
        // 복원 가능한 O는 위반이 아님
        Assert.Empty(charsets.Violations("GTIN", "(O1)08806173612345"));
    }

    [Fact]
    public void FieldLookupIsCaseInsensitive()
    {
        var charsets = new FieldCharsets([new FieldCharsetRule("lot", "0123456789", "")]);
        Assert.Equal("25080123", charsets.Repair("LOT", "25O80123"));
    }

    [Fact]
    public void WhitespaceIsAlwaysAllowed()
    {
        var charsets = new FieldCharsets([new FieldCharsetRule("REF", "0123456789-", "")]);
        Assert.Empty(charsets.Violations("REF", "123 456"));
    }
}

public class CharsetInspectionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly StandardsBundle _standards;

    public CharsetInspectionTests()
    {
        _standards = StandardsBundle.Load(new AppConfig(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    private static OcrWord Word(string text) => new(text, (0, 0, 10, 10), 95);

    private InspectionEngine Engine() => new(_standards, new InspectionOptions
    {
        Charsets = new FieldCharsets(
        [
            new FieldCharsetRule("LOT", "0123456789", ""),
            new FieldCharsetRule("MFG DATE", "0123456789-./", ""),
            new FieldCharsetRule("GTIN", "0123456789()", ""),
        ]),
    });

    [Fact]
    public void DateWithMisreadLetterCountsAfterRepair()
    {
        var outcome = Engine().Inspect(Record, "MDR", [Word("2O24-O5-1O")]);
        Assert.Equal(1, outcome.Fields["MFG DATE"].Found);
    }

    [Fact]
    public void GtinWithMisreadLetterCountsAfterRepair()
    {
        var matches = Engine().CountField("GTIN", "08806173612345",
            [Word("(01)O88O6173612345")]);
        Assert.Single(matches);
    }

    [Fact]
    public void LotCandidateIsRepairedForAutoMatch()
    {
        // OCR이 "25O90776"으로 읽어도 LOT 자동 매칭이 정확 일치로 성공해야 한다
        var match = Engine().MatchLot([Word("25O90776")], [Record]);
        Assert.NotNull(match);
        Assert.Equal("25090776", match!.Lot);
        Assert.Equal("exact", match.MatchType);
    }

    [Fact]
    public void WithoutCharsetsMisreadLotIsNotExact()
    {
        var plain = new InspectionEngine(_standards);
        var match = plain.MatchLot([Word("25O90776")], [Record]);
        Assert.True(match is null || match.MatchType != "exact");
    }
}
