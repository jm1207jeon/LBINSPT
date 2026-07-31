using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class HistoryDbTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly InspectionEngine _engine;

    public HistoryDbTests()
    {
        _engine = new InspectionEngine(StandardsBundle.Load(
            new AppConfig(Path.Combine(_directory, "cfg"))));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private InspectionOutcome SampleOutcome() =>
        _engine.Inspect(
            new LabelRecord("25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
                            "2024-05-10", "2027-05-09", "08806173612345", "MDR"),
            "MDR",
            [new OcrWord("x-25090776-1", (0, 0, 1, 1), 95)],
            [new CrossCheckResult("DataMatrix", "GTIN",
                                  "08806173612345", "08806173612345", true)]);

    [Fact]
    public void RecordAndQuery()
    {
        using var db = new HistoryDb(Path.Combine(_directory, "history.sqlite3"));
        db.RecordInspection(SampleOutcome(), "/tmp/img.jpg", "pdf", "/tmp/a.pdf", 0);
        var rows = db.Query();
        var row = Assert.Single(rows);
        Assert.Equal("25090776", row.Lot);
        Assert.Equal("MDR", row.Standard);
        Assert.False(row.Passed);   // 카운트 미달이므로 확인 필요
        Assert.Contains(db.FieldsFor(row.Id), f => f is { Field: "LOT", Found: 1 });
    }

    [Fact]
    public void QueryFilters()
    {
        using var db = new HistoryDb(Path.Combine(_directory, "history2.sqlite3"));
        db.RecordInspection(SampleOutcome(), "", "pdf");
        Assert.NotEmpty(db.Query(lot: "2509"));
        Assert.Empty(db.Query(lot: "NOPE"));
        Assert.Empty(db.Query(passed: true));
        Assert.Single(db.Query(passed: false));
    }

    [Fact]
    public void CounterPersistsAcrossConnections()
    {
        var path = Path.Combine(_directory, "history3.sqlite3");
        using (var first = new HistoryDb(path))
        {
            Assert.Equal(1, first.NextFileCounter());
            Assert.Equal(2, first.NextFileCounter());
        }
        using var second = new HistoryDb(path);
        Assert.Equal(3, second.NextFileCounter());
    }

    [Fact]
    public void CascadeDelete()
    {
        using var db = new HistoryDb(Path.Combine(_directory, "history4.sqlite3"));
        var id = db.RecordInspection(SampleOutcome(), "", "pdf");
        db.Delete(id);
        Assert.Empty(db.Query());
        Assert.Empty(db.FieldsFor(id));
    }

    [Fact]
    public void ExportLotReport()
    {
        using var db = new HistoryDb(Path.Combine(_directory, "history5.sqlite3"));
        db.RecordInspection(SampleOutcome(), "/tmp/img.jpg", "pdf", page: 0);
        var outPath = Path.Combine(_directory, "report.xlsx");
        Assert.Equal(1, Report.ExportLotReport(db, "25090776", outPath));
        Assert.True(File.Exists(outPath));
        using var workbook = new ClosedXML.Excel.XLWorkbook(outPath);
        Assert.Equal("25090776", workbook.Worksheet("요약").Cell(1, 2).GetString());
    }
}

public class OcrCacheTests
{
    private static readonly PageAnalysis Analysis = new()
    {
        Words = [new OcrWord("hello", (1, 2, 3, 4), 99)],
        Barcodes = [new BarcodeHit("DataMatrix", "0108806173612345", (5, 6, 7, 8), true)],
    };

    [Fact]
    public void KeyStableAndSensitive()
    {
        var a = OcrCache.PageKey("/x/a.pdf", 123.0, 0, 4.0, "skew-v1");
        Assert.Equal(a, OcrCache.PageKey("/x/a.pdf", 123.0, 0, 4.0, "skew-v1"));
        Assert.NotEqual(a, OcrCache.PageKey("/x/a.pdf", 124.0, 0, 4.0, "skew-v1"));
        Assert.NotEqual(a, OcrCache.PageKey("/x/a.pdf", 123.0, 1, 4.0, "skew-v1"));
        Assert.NotEqual(a, OcrCache.PageKey("/x/a.pdf", 123.0, 0, 2.0, "skew-v1"));
    }

    [Fact]
    public void MemoryRoundTrip()
    {
        var cache = new OcrCache(null);
        cache.Put("k1", Analysis);
        Assert.Same(Analysis, cache.Get("k1"));
        Assert.Null(cache.Get("missing"));
    }

    [Fact]
    public void DiskPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            new OcrCache(directory).Put("k1", Analysis);
            var fresh = new OcrCache(directory);
            var got = fresh.Get("k1");
            Assert.NotNull(got);
            Assert.Equal("hello", got!.Words[0].Text);
            Assert.Equal((1, 2, 3, 4), got.Words[0].Bbox);
            Assert.Equal("DataMatrix", got.Barcodes[0].Symbology);
            Assert.True(got.Barcodes[0].IsGs1);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LruCap()
    {
        var cache = new OcrCache(null, maxEntries: 2);
        cache.Put("a", Analysis);
        cache.Put("b", Analysis);
        cache.Put("c", Analysis);
        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("c"));
    }

    [Fact]
    public void AnnotateFilenameRules()
    {
        Assert.EndsWith("_Passed.jpg",
            Annotate.MakeResultFilename(1, "25090776", "REF-1", true));
        var name = Annotate.MakeResultFilename(7, "L/1", "R:2", false);
        Assert.StartsWith("007_L1_R2_", name);
        Assert.EndsWith("_Check.jpg", name);
    }
}
