using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class NormalizeGtin14Tests
{
    [Theory]
    [InlineData("8806173612345", "08806173612345")]
    [InlineData("18806173612345", "18806173612345")]
    [InlineData("8806173612345.0", "08806173612345")]
    [InlineData("(01)08806173612345", "08806173612345")]
    [InlineData("", "")]
    [InlineData("nan", "")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, Schema.NormalizeGtin14(input));

    [Fact]
    public void IntInput() =>
        Assert.Equal("08806173612345", Schema.NormalizeGtin14(8806173612345L));

    [Fact]
    public void TooLongRejected() =>
        Assert.Throws<SchemaException>(() => Schema.NormalizeGtin14("123456789012345"));
}

public class DateFormatTests
{
    [Fact]
    public void BscUsesDots() => Assert.Equal("yyyy.MM.dd", Schema.DateFormatFor("BSC"));

    [Fact]
    public void DefaultUsesHyphen()
    {
        Assert.Equal("yyyy-MM-dd", Schema.DateFormatFor("MDR"));
        Assert.Equal("yyyy-MM-dd", Schema.DateFormatFor(null));
    }
}

public class RoundTripTests
{
    private static List<LabelRecord> SampleRecords() =>
    [
        new("24A1234", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
            "2024-05-10", "2027-05-09", "08806173612345", "MDR"),
        new("24B0001", "STENT Y", "HANARO-02", "BPJ01-01",
            "2024.05.11", "2027.05.10", "08806173699999", "BSC"),
    ];

    [Fact]
    public void EightColumnRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            Schema.SaveInspectionList(SampleRecords(), path);
            var (records, warnings) = Schema.LoadInspectionList(path);
            Assert.Equal(["24A1234", "24B0001"], records.Select(r => r.Lot).ToArray());
            Assert.Equal("MDR", records[0].Standard);
            Assert.Equal("BSC", records[1].Standard);
            Assert.Equal("08806173612345", records[0].Gtin);
            Assert.Empty(warnings);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SevenColumnLegacyFileNormalizesGtin()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var sheet = workbook.AddWorksheet(Schema.SheetName);
                for (var i = 0; i < Schema.CanonicalColumns.Length; i++)
                    sheet.Cell(1, i + 1).Value = Schema.CanonicalColumns[i];
                var values = new[] { "24A1234", "HANAROSTENT X", "HANARO-01",
                                     "NCN20-080-230", "2024-05-10", "2027-05-09",
                                     "8806173612345" };
                for (var i = 0; i < values.Length; i++)
                    sheet.Cell(2, i + 1).Value = values[i];
                workbook.SaveAs(path);
            }
            var (records, _) = Schema.LoadInspectionList(path);
            Assert.Single(records);
            Assert.Null(records[0].Standard);
            Assert.Equal("08806173612345", records[0].Gtin);   // 13 → 14자리
        }
        finally { File.Delete(path); }
    }
}

public class ConfigAndStandardsTests
{
    [Fact]
    public void DefaultsCopiedAndLoaded()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var config = new AppConfig(directory);
            var bundle = StandardsBundle.Load(config);
            Assert.Equal(6, bundle.Standards.Count);
            Assert.Equal(11, bundle.Spec("MDR").Counts["LOT"]);
            Assert.Equal("yyyy.MM.dd", bundle.Spec("BSC").DateFormat);
            Assert.True(bundle.Spec("중국").UsesChinaField);
            Assert.Equal("LBDB-04", bundle.ChinaCodeForRef("BPJ01-01"));
            Assert.Null(bundle.ChinaCodeForRef("XXX"));
            Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)100), bundle.FieldColors["LOT"]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SettingsMigrationAddsNewKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"),
                              """{"schema_version": 1}""");
            var config = new AppConfig(directory);
            Assert.True(config.Settings.ContainsKey("country_standard_map"));
            Assert.Equal("all", config.GetString("prefetch_policy"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
