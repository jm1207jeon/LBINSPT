// CSV 내보내기 무결성 — UDInspect RobustnessTests의 CSV 케이스 이식 + LaVIS 검사 결과 CSV.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class CsvUtilTests
{
    [Fact]
    public void QuotedFieldsAndEscapesAreSplit()
    {
        var f = CsvUtil.ParseLine("1,\"a,b\",\"say \"\"hi\"\"\",x");
        Assert.Equal(new[] { "1", "a,b", "say \"hi\"", "x" }, f);
    }

    [Fact]
    public void EmptyLineAndEmptyFieldsAreEmptyStrings()
    {
        Assert.Equal(new[] { "" }, CsvUtil.ParseLine(""));
        Assert.Equal(new[] { "", "", "" }, CsvUtil.ParseLine(",,"));
    }

    [Theory]
    [InlineData("\"=\"\"08806367\"\"\"", "08806367")]   // 이 프로그램 내보내기 형식 (="값")
    [InlineData("=\"25081215\"", "25081215")]           // 따옴표 한 겹만 남은 경우
    [InlineData("25081215", "25081215")]                // 엑셀 재저장본(일반 값)
    [InlineData("\"  1, 2, 3 \"", "1, 2, 3")]           // 따옴표 필드 안의 콤마 유지 + 앞뒤 공백 제거
    public void TextProtectWrappingIsRemoved(string raw, string expected)
    {
        // ParseLine이 바깥 따옴표를 벗긴 뒤 Unwrap이 ="…" 래핑을 벗긴다
        var field = CsvUtil.ParseLine(raw)[0];
        Assert.Equal(expected, CsvUtil.Unwrap(field));
    }

    [Fact]
    public void ExportFormatRoundTrips()
    {
        string[] samples =
        {
            "08806367067654", "01-0854", "M00523870", "a,b", "q\"uote",
            "한글 값", "줄\n바꿈",
        };
        foreach (var s in samples)
        {
            var line = CsvUtil.TextField(s) + "," + CsvUtil.Field(s);
            var f = CsvUtil.ParseLine(line);
            Assert.Equal(s, CsvUtil.Unwrap(f[0]));
            Assert.Equal(s, f[1]);
        }
    }

    [Fact]
    public void EmptyTextFieldStaysEmpty()
    {
        Assert.Equal("", CsvUtil.TextField(""));
        Assert.Equal("", CsvUtil.Field(""));
    }
}

public class InspectionCsvTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly InspectionEngine _engine;

    public InspectionCsvTests()
    {
        _engine = new InspectionEngine(StandardsBundle.Load(new AppConfig(_directory)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly LabelRecord Record = new(
        "00123", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    private InspectionOutcome Inspect() =>
        _engine.Inspect(Record, "MDR", [new OcrWord("LOT 00123", (0, 0, 80, 20), 95)]);

    [Fact]
    public void InspectionCsv_LotIsTextProtectedAndColumnCountConstant()
    {
        var outcome = Inspect();
        var at = new DateTime(2026, 9, 8, 10, 30, 0);
        var csv = InspectionCsv.Build(
            [(0, outcome, @"C:\out\p1.jpg"), (1, null, null), (2, outcome, null)],
            @"C:\scan\labels.pdf", "1.2.3", textProtect: true, at: at);
        var lines = csv.Split("\r\n");
        Assert.Equal(4, lines.Length);
        Assert.Equal(InspectionCsv.Header, lines[0]);
        var columns = InspectionCsv.ColumnCount;
        foreach (var line in lines)
            Assert.Equal(columns, CsvUtil.ParseLine(line).Count);

        // LOT 선행 0 보호: 셀 원문이 "=""00123""" 이고 Unwrap하면 원래 값
        var raw = lines[1].Split(',');
        Assert.Equal("\"=\"\"00123\"\"\"", raw[1]);
        var row1 = CsvUtil.ParseLine(lines[1]);
        Assert.Equal("1", row1[0]);
        Assert.Equal("00123", CsvUtil.Unwrap(row1[1]));
        Assert.Equal("08806173612345", CsvUtil.Unwrap(row1[4]));
        Assert.Equal(outcome.Passed ? "MATCH" : "CHECK", row1[8]);
        Assert.Equal(@"C:\out\p1.jpg", CsvUtil.Unwrap(row1[16]));
        Assert.Equal(@"C:\scan\labels.pdf", row1[17]);
        Assert.Equal("2026-09-08 10:30:00", row1[18]);
        Assert.Equal("1.2.3", row1[19]);
        // FIELD_LOT은 검출/기대, 기대 없는 필드는 '-'
        Assert.Matches(@"^\d+/\d+$", row1[9]);

        // 미검사 행: 값 빈칸·AUTO=UNINSPECTED, 페이지 번호는 1-based
        var row2 = CsvUtil.ParseLine(lines[2]);
        Assert.Equal("2", row2[0]);
        Assert.Equal("", row2[1]);
        Assert.Equal("UNINSPECTED", row2[8]);
        Assert.Equal("1.2.3", row2[19]);
    }

    [Fact]
    public void TextProtectCanBeTurnedOff()
    {
        var line = InspectionCsv.Row(0, Inspect(), null, "x.pdf", DateTime.Now, "v",
                                     textProtect: false);
        var cells = CsvUtil.ParseLine(line);
        Assert.Equal("00123", cells[1]);   // 래핑 없이 원래 값
        Assert.Equal(InspectionCsv.ColumnCount, cells.Count);
    }
}
