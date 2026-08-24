using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class OcrCorrectionsTests
{
    [Fact]
    public void AppliesExactWordCorrection()
    {
        var corrections = new OcrCorrections();
        corrections.Add("25O9O776", "25090776");
        var words = corrections.Apply([new OcrWord("25O9O776", (0, 0, 1, 1), 90),
                                       new OcrWord("other", (0, 0, 1, 1), 90)]);
        Assert.Equal("25090776", words[0].Text);
        Assert.Equal("other", words[1].Text);
    }

    [Fact]
    public void BuiltinConfusablesMatch()
    {
        var corrections = new OcrCorrections();
        Assert.True(corrections.ConfusableEquals("25O9O776", "25090776"));   // O↔0
        Assert.True(corrections.ConfusableEquals("NCN2O-O8O", "NCN20-080"));
        Assert.True(corrections.ConfusableContains("LOT:25O90776x", "25090776"));
        Assert.False(corrections.ConfusableEquals("ABC", "XYZ"));
    }

    [Fact]
    public void LearnsNewConfusablePairFromCorrection()
    {
        var corrections = new OcrCorrections();
        Assert.False(corrections.ConfusableEquals("25090776", "25R90776"));
        corrections.Add("25R90776", "25090776");   // R↔0 학습
        Assert.True(corrections.ConfusableEquals("25R90776", "25090776"));
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var first = new OcrCorrections(path);
            first.Add("8806l73", "8806173", field: "GTIN");
            var second = new OcrCorrections(path);
            Assert.Single(second.Entries);
            Assert.Equal("GTIN", second.Entries[0].Field);
            Assert.Equal("8806173", second.Apply(
                [new OcrWord("8806l73", (0, 0, 1, 1), 90)])[0].Text);
        }
        finally { File.Delete(path); }
    }
}

public class InspectionOptionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly StandardsBundle _standards;

    public InspectionOptionsTests()
    {
        _standards = StandardsBundle.Load(new AppConfig(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    private static OcrWord Word(string text) => new(text, (0, 0, 10, 10), 95);

    [Fact]
    public void DisabledFieldsAreSkipped()
    {
        var engine = new InspectionEngine(_standards, new InspectionOptions
        { DisabledFields = ["PN", "REF", "LOT"] });   // LOT은 무시돼야 함
        var outcome = engine.Inspect(Record, "MDR", [Word("x-25090776")]);
        Assert.False(outcome.Fields.ContainsKey("PN"));
        Assert.False(outcome.Fields.ContainsKey("REF"));
        Assert.True(outcome.Fields.ContainsKey("LOT"));   // 제외 불가
    }

    [Fact]
    public void CustomFieldFixedStringCounts()
    {
        var engine = new InspectionEngine(_standards, new InspectionOptions
        { CustomFields = [new CustomFieldDef("CE마크", "CE0123", false, 2)] });
        var outcome = engine.Inspect(Record, "MDR",
            [Word("CE0123-a"), Word("xx CE0123"), Word("nope")]);
        Assert.Equal(2, outcome.Fields["CE마크"].Found);
        Assert.Equal(2, outcome.Fields["CE마크"].Expected);
        Assert.True(outcome.Fields["CE마크"].Passed);
    }

    [Fact]
    public void CustomFieldRegexCounts()
    {
        var engine = new InspectionEngine(_standards, new InspectionOptions
        { CustomFields = [new CustomFieldDef("UDI", @"^\(01\)\d{14}.*", true, null)] });
        var outcome = engine.Inspect(Record, "MDR",
            [Word("(01)08806173612345(10)25090776"), Word("text")]);
        Assert.Equal(1, outcome.Fields["UDI"].Found);
    }

    [Fact]
    public void ConfusableMatchingCountsMisreadWords()
    {
        var corrections = new OcrCorrections();
        var engine = new InspectionEngine(_standards, new InspectionOptions
        { AllowConfusables = true, Corrections = corrections });
        // OCR이 0을 O로 오인한 LOT — 혼동 허용 시 카운트돼야 한다
        var matches = engine.CountField("LOT", "25090776", [Word("x-25O9O776")]);
        Assert.Single(matches);
        // GTIN도 혼동 문자 보정 후 (01) 패턴 매칭
        var gtin = engine.CountField("GTIN", "08806173612345",
                                     [Word("(01)O8806173612345")]);
        Assert.Single(gtin);
    }

    [Fact]
    public void CorrectionDictionaryFixesWordBeforeCounting()
    {
        var corrections = new OcrCorrections();
        corrections.Add("Z5090776", "25090776");
        var engine = new InspectionEngine(_standards, new InspectionOptions
        { Corrections = corrections });
        var outcome = engine.Inspect(Record, "MDR", [Word("Z5090776x")]);
        // 완전 일치 단어가 아니므로 치환 안 됨 → 0
        Assert.Equal(0, outcome.Fields["LOT"].Found);
        var outcome2 = engine.Inspect(Record, "MDR", [Word("Z5090776")]);
        Assert.Equal(1, outcome2.Fields["LOT"].Found);
    }
}

public class BarcodeGraderTests
{
    private static SKBitmap CleanSymbol()
    {
        // 선명한 흑백 체커보드 (모듈 경계 뚜렷)
        var bmp = new SKBitmap(80, 80);
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++)
                bmp.SetPixel(x, y, ((x / 8 + y / 8) % 2 == 0)
                    ? SKColors.Black : SKColors.White);
        return bmp;
    }

    [Fact]
    public void CleanSymbolGetsHighGrade()
    {
        using var bmp = CleanSymbol();
        var grade = BarcodeGrader.Grade(bmp, (8, 8, 64, 64));
        Assert.True(grade.Score >= 3.0, $"score={grade.Score}");
        Assert.True(grade.SymbolContrast > 0.8);
    }

    [Fact]
    public void LowContrastSymbolGetsLowGrade()
    {
        var bmp = new SKBitmap(80, 80);
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++)
                bmp.SetPixel(x, y, ((x / 8 + y / 8) % 2 == 0)
                    ? new SKColor(110, 110, 110) : new SKColor(150, 150, 150));
        var grade = BarcodeGrader.Grade(bmp, (8, 8, 64, 64));
        Assert.True(grade.Score <= 1.0, $"score={grade.Score}");
        Assert.True(grade.Letter is 'D' or 'F');
        bmp.Dispose();
    }

    [Fact]
    public void FlatRegionIsF()
    {
        var bmp = new SKBitmap(40, 40);
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 40; x++) bmp.SetPixel(x, y, SKColors.Gray);
        Assert.Equal('F', BarcodeGrader.Grade(bmp, (0, 0, 40, 40)).Letter);
        bmp.Dispose();
    }
}

public class SymbologyTests
{
    [Theory]
    [InlineData("DataMatrix", true, "GS1 DataMatrix")]
    [InlineData("DATA_MATRIX", false, "DataMatrix")]
    [InlineData("CODE_128", true, "GS1-128")]
    [InlineData("CODE_128", false, "Code128")]
    [InlineData("QR_CODE", false, "QR")]
    [InlineData("EAN_13", false, "EAN-13")]
    public void NormalizesFormats(string raw, bool gs1, string expected) =>
        Assert.Equal(expected, BarcodeSymbology.Normalize(raw, gs1));
}

public class MasterDbTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MasterDbTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    [Fact]
    public void UpsertFromRecordDoesNotOverwriteRegisteredValues()
    {
        using var db = new HistoryDb(Path.Combine(_directory, "h.sqlite3"));
        db.SaveMasterRow(new MasterRow("HANARO-01", "", "MASTER-REF", "", "", "사전 등록"));
        db.UpsertMasterFromRecord(Record);
        var master = db.GetMaster("HANARO-01")!;
        Assert.Equal("MASTER-REF", master.Ref);              // 사전 등록값 유지
        Assert.Equal("HANAROSTENT X", master.Products);      // 빈 값은 채워짐
        Assert.Equal("08806173612345", master.Gtin);
    }

    [Fact]
    public void MasterCheckDetectsMismatchAndSymbology()
    {
        var master = new MasterRow("HANARO-01", "P", "OTHER-REF", "08806173612345",
                                   "GS1 DataMatrix", "");
        var detected = new List<BarcodeHit>
        { new("GS1 DataMatrix", "01...", (0, 0, 1, 1), true) };
        var checks = MasterCheck.Check(master, Record, detected);
        var byField = checks.ToLookup(c => c.Field);
        Assert.False(byField["REF"].First().Matched);        // 목록 REF ≠ 마스터 REF
        Assert.True(byField["GTIN"].First().Matched);
        Assert.True(byField["심볼로지"].First().Matched);

        var checksMissing = MasterCheck.Check(master, Record, []);
        Assert.False(checksMissing.First(c => c.Field == "심볼로지").Matched);
    }

    [Fact]
    public void AllMasterAndDelete()
    {
        using var db = new HistoryDb(Path.Combine(_directory, "h2.sqlite3"));
        db.UpsertMasterFromRecord(Record);
        Assert.Single(db.AllMaster());
        db.DeleteMaster("HANARO-01");
        Assert.Empty(db.AllMaster());
    }
}

public class OverlayStyleTests
{
    [Fact]
    public void NumberingDrawsDifferentOutput()
    {
        var bmp = new SKBitmap(200, 120);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.White);
        var matches = new[]
        {
            new TextMatch("LOT", new OcrWord("A", (20, 40, 40, 20), 90), "A"),
            new TextMatch("REF", new OcrWord("B", (100, 40, 40, 20), 90), "B"),
        };
        var colors = new Dictionary<string, (byte, byte, byte, byte)>
        { ["LOT"] = (255, 0, 0, 100), ["REF"] = (0, 255, 0, 100) };

        using var plain = Annotate.RenderOverlays(bmp, matches, colors,
            new OverlayStyle(ShowNumbers: false));
        using var numbered = Annotate.RenderOverlays(bmp, matches, colors,
            new OverlayStyle(ShowNumbers: true));
        Assert.False(plain.Bytes.SequenceEqual(numbered.Bytes));
        using var thick = Annotate.RenderOverlays(bmp, matches, colors,
            new OverlayStyle(Thickness: 6));
        Assert.False(plain.Bytes.SequenceEqual(thick.Bytes));
        bmp.Dispose();
    }
}

public class ImagePreprocessTests
{
    [Fact]
    public void StretchExpandsLowContrast()
    {
        var bmp = new SKBitmap(20, 20);
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                bmp.SetPixel(x, y, x < 10
                    ? new SKColor(100, 100, 100) : new SKColor(160, 160, 160));
        using var stretched = ImagePreprocess.StretchContrast(bmp);
        Assert.True(stretched.GetPixel(0, 0).Red < 40);
        Assert.True(stretched.GetPixel(19, 0).Red > 215);
        bmp.Dispose();
    }
}
