using ClosedXML.Excel;
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class ComputeExpDateTests
{
    [Fact]
    public void NormalDate() =>
        Assert.Equal(new DateOnly(2027, 5, 9),
                     ListGenerator.ComputeExpDate(new DateOnly(2024, 5, 10)));

    [Fact]
    public void Feb29NoCrash() =>
        // 레거시(파이썬)는 replace(year+3) ValueError → 행 소멸. 2024-02-29 → 2027-02-27.
        Assert.Equal(new DateOnly(2027, 2, 27),
                     ListGenerator.ComputeExpDate(new DateOnly(2024, 2, 29)));

    [Fact]
    public void CustomShelfLife() =>
        Assert.Equal(new DateOnly(2026, 5, 9),
                     ListGenerator.ComputeExpDate(new DateOnly(2024, 5, 10), 24));
}

public class CleanRefTests
{
    [Fact]
    public void NormalRefKept() =>
        Assert.Equal(("NCN20-080-230", (string?)null), ListGenerator.CleanRef("NCN20-080-230"));

    [Fact]
    public void RefWithColonKept()
    {
        var (refValue, warning) = ListGenerator.CleanRef("ABC:12");
        Assert.Equal("ABC:12", refValue);
        Assert.Null(warning);
    }

    [Fact]
    public void LargeNumericStringRefKept() =>
        Assert.Equal("50001", ListGenerator.CleanRef("50001").Ref);

    [Fact]
    public void DateTimeBlankedWithWarning()
    {
        var (refValue, warning) = ListGenerator.CleanRef(new DateTime(2024, 5, 10));
        Assert.Equal("", refValue);
        Assert.NotNull(warning);
    }

    [Fact]
    public void FullDateStringBlanked()
    {
        var (refValue, warning) = ListGenerator.CleanRef("2024-05-10 00:00:00");
        Assert.Equal("", refValue);
        Assert.NotNull(warning);
    }

    [Fact]
    public void NumericExcelSerialBlanked()
    {
        var (refValue, warning) = ListGenerator.CleanRef(45000.0);
        Assert.Equal("", refValue);
        Assert.Contains("시리얼", warning);
    }
}

public class GenerateListTests : IDisposable
{
    private static readonly Dictionary<string, string> CountryMap =
        new() { ["일본"] = "BSC", ["중국"] = "중국", ["*"] = "MDR" };

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ColumnMaps _maps;

    public GenerateListTests()
    {
        Directory.CreateDirectory(_directory);
        _maps = ColumnMaps.FromConfig(new AppConfig(
            Path.Combine(_directory, "cfg")).ColumnMapsRaw);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string BuildSchedule(params (string Lot, string Pn, string Country, DateTime Mfg)[] rows)
    {
        var path = Path.Combine(_directory, Path.GetRandomFileName() + ".xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(_maps.Schedule.Sheet);
        sheet.Cell(1, 1).Value = "헤더";
        var rowNumber = 2;
        foreach (var (lot, pn, country, mfg) in rows)
        {
            sheet.Cell(rowNumber, _maps.Schedule.Col("lot") + 1).Value = lot;
            sheet.Cell(rowNumber, _maps.Schedule.Col("pn") + 1).Value = pn;
            sheet.Cell(rowNumber, _maps.Schedule.Col("products") + 1).Value = "HANAROSTENT X";
            sheet.Cell(rowNumber, _maps.Schedule.Col("ref") + 1).Value = "NCN20-080-230";
            sheet.Cell(rowNumber, _maps.Schedule.Col("country") + 1).Value = country;
            sheet.Cell(rowNumber, _maps.Schedule.Col("mfg") + 1).Value = mfg;
            rowNumber++;
        }
        workbook.SaveAs(path);
        return path;
    }

    private string BuildProduct(params (string Pn, object Gtin)[] rows)
    {
        var path = Path.Combine(_directory, Path.GetRandomFileName() + ".xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(_maps.Product.Sheet);
        sheet.Cell(1, 1).Value = "PN";
        var rowNumber = 2;
        foreach (var (pn, gtin) in rows)
        {
            sheet.Cell(rowNumber, _maps.Product.Col("pn") + 1).Value = pn;
            sheet.Cell(rowNumber, _maps.Product.Col("gtin") + 1).Value =
                XLCellValue.FromObject(gtin);
            rowNumber++;
        }
        workbook.SaveAs(path);
        return path;
    }

    private string BuildBsc(params (string Ref, string Pn, string Gtin)[] rows)
    {
        var path = Path.Combine(_directory, Path.GetRandomFileName() + ".xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(_maps.Bsc.Sheet);
        sheet.Cell(1, 1).Value = "헤더";
        var rowNumber = 2;
        foreach (var (refValue, pn, gtin) in rows)
        {
            sheet.Cell(rowNumber, _maps.Bsc.Col("ref") + 1).Value = refValue;
            sheet.Cell(rowNumber, _maps.Bsc.Col("pn") + 1).Value = pn;
            sheet.Cell(rowNumber, _maps.Bsc.Col("gtin") + 1).Value = gtin;
            rowNumber++;
        }
        workbook.SaveAs(path);
        return path;
    }

    [Fact]
    public void NonJapanRow()
    {
        var schedule = InputFrame.Load(
            BuildSchedule(("24A1234", "HANARO-01", "독일", new DateTime(2024, 5, 10))),
            _maps.Schedule);
        var product = InputFrame.Load(
            BuildProduct(("HANARO-01", 8806173612345.0)), _maps.Product);
        var result = ListGenerator.Generate(schedule, product, null,
            new HashSet<DateOnly> { new(2024, 5, 10) }, _maps, CountryMap);
        var record = Assert.Single(result.Records);
        Assert.Equal("24A1234", record.Lot);
        Assert.Equal("NCN20-080-230", record.Ref);
        Assert.Equal("2024-05-10", record.MfgDate);
        Assert.Equal("2027-05-09", record.ExpDate);
        Assert.Equal("08806173612345", record.Gtin);   // GTIN-14 (레거시는 13자리)
        Assert.Equal("MDR", record.Standard);
    }

    [Fact]
    public void JapanRowUsesBscAndDotDates()
    {
        var schedule = InputFrame.Load(
            BuildSchedule(("24A1234", "HANARO-01", "일본", new DateTime(2024, 5, 10))),
            _maps.Schedule);
        var product = InputFrame.Load(
            BuildProduct(("HANARO-01", 8806173612345.0)), _maps.Product);
        var bsc = InputFrame.Load(
            BuildBsc(("M730-BSC-REF", "HANARO-01", "4987654321098")), _maps.Bsc);
        var result = ListGenerator.Generate(schedule, product, bsc,
            new HashSet<DateOnly> { new(2024, 5, 10) }, _maps, CountryMap);
        var record = Assert.Single(result.Records);
        Assert.Equal("M730-BSC-REF", record.Ref);
        Assert.Equal("2024.05.10", record.MfgDate);
        Assert.Equal("2027.05.09", record.ExpDate);
        Assert.Equal("04987654321098", record.Gtin);
        Assert.Equal("BSC", record.Standard);
    }

    [Fact]
    public void Feb29RowSurvives()
    {
        var schedule = InputFrame.Load(
            BuildSchedule(("24A1234", "HANARO-01", "독일", new DateTime(2024, 2, 29))),
            _maps.Schedule);
        var product = InputFrame.Load(
            BuildProduct(("HANARO-01", 8806173612345.0)), _maps.Product);
        var result = ListGenerator.Generate(schedule, product, null,
            new HashSet<DateOnly> { new(2024, 2, 29) }, _maps, CountryMap);
        var record = Assert.Single(result.Records);
        Assert.Equal("2027-02-27", record.ExpDate);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void JapanBscMissFallsBackToProduct()
    {
        var schedule = InputFrame.Load(
            BuildSchedule(("24A1234", "HANARO-01", "일본", new DateTime(2024, 5, 10))),
            _maps.Schedule);
        var product = InputFrame.Load(
            BuildProduct(("HANARO-01", 8806173612345.0)), _maps.Product);
        var bsc = InputFrame.Load(
            BuildBsc(("REF-X", "OTHER-PN", "999")), _maps.Bsc);
        var result = ListGenerator.Generate(schedule, product, bsc,
            new HashSet<DateOnly> { new(2024, 5, 10) }, _maps, CountryMap);
        Assert.Equal("08806173612345", result.Records[0].Gtin);
        Assert.Contains(result.Issues, i => i.Message.Contains("품목리스트 값으로 대체"));
    }

    [Fact]
    public void MissingGtinWarnsNotSilent()
    {
        var schedule = InputFrame.Load(
            BuildSchedule(("24A1234", "UNKNOWN-PN", "독일", new DateTime(2024, 5, 10))),
            _maps.Schedule);
        var product = InputFrame.Load(
            BuildProduct(("HANARO-01", 8806173612345.0)), _maps.Product);
        var result = ListGenerator.Generate(schedule, product, null,
            new HashSet<DateOnly> { new(2024, 5, 10) }, _maps, CountryMap);
        Assert.Equal("", result.Records[0].Gtin);
        Assert.Contains(result.Issues, i => i.Message.Contains("GTIN을 찾지 못했습니다"));
    }

    [Fact]
    public void DateFilterAndOrdering()
    {
        var schedule = InputFrame.Load(BuildSchedule(
            ("L-C", "HANARO-01", "독일", new DateTime(2024, 5, 12)),
            ("L-A", "HANARO-01", "독일", new DateTime(2024, 5, 10)),
            ("L-SKIP", "HANARO-01", "독일", new DateTime(2024, 5, 11))), _maps.Schedule);
        var product = InputFrame.Load(
            BuildProduct(("HANARO-01", 8806173612345.0)), _maps.Product);
        var result = ListGenerator.Generate(schedule, product, null,
            new HashSet<DateOnly> { new(2024, 5, 10), new(2024, 5, 12) },
            _maps, CountryMap);
        Assert.Equal(["L-A", "L-C"], result.Records.Select(r => r.Lot).ToArray());
    }

    [Fact]
    public void ExtractAvailableDatesDedupesAndSorts()
    {
        var schedule = InputFrame.Load(BuildSchedule(
            ("A", "P", "독일", new DateTime(2024, 5, 12)),
            ("B", "P", "독일", new DateTime(2024, 5, 10)),
            ("C", "P", "독일", new DateTime(2024, 5, 10))), _maps.Schedule);
        Assert.Equal([new DateOnly(2024, 5, 10), new DateOnly(2024, 5, 12)],
                     ListGenerator.ExtractAvailableDates(schedule, _maps));
    }
}
