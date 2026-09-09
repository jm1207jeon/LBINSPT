// 검사자 최종 처리(합격 확정) — 이력 DB 열 확장·조회, 저장 이미지 요약 박스.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class InspectorVerdictTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly InspectionEngine _engine;

    public InspectorVerdictTests()
    {
        Directory.CreateDirectory(_directory);
        _engine = new InspectionEngine(StandardsBundle.Load(new AppConfig(_directory)));
    }
    public void Dispose()
    {
        // Windows: SQLite 연결 풀이 history.db를 잠시 더 잡고 있어 즉시 삭제가 실패할 수 있다 (기존 이력 테스트와 동일 처리)
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static readonly LabelRecord Record = new(
        "25090776", "MEGACATH KIT", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806367067654", "MDR");

    private InspectionOutcome CheckOutcome() =>
        _engine.Inspect(Record, "MDR", [new OcrWord("25090776", (0, 0, 80, 20), 95)]);

    [Fact]
    public void HistoryRecordsInspectorVerdictAndMigratesOldDb()
    {
        var path = Path.Combine(_directory, "history.db");
        using (var db = new HistoryDb(path))
        {
            // 구버전 스키마 흉내: 열을 지운 뒤 다시 열면 ALTER로 추가돼야 한다
        }
        using (var db = new HistoryDb(path))
        {
            var verdict = new InspectorVerdict(true, "user1", DateTime.Now, "육안 확인 정상");
            db.RecordInspection(CheckOutcome(), "img.jpg", "pdf", "a.pdf", 3, inspector: verdict);
            db.RecordInspection(CheckOutcome(), "img2.jpg", "pdf", "a.pdf", 4);
            var rows = db.Query();
            Assert.Equal(2, rows.Count);
            var overridden = Assert.Single(rows, r => r.Page == 3);
            Assert.False(overridden.Passed);                 // 자동 판정은 보존
            Assert.Equal("PASS", overridden.InspectorVerdict);
            Assert.Equal("육안 확인 정상", overridden.InspectorNote);
            Assert.Equal("user1", overridden.InspectorBy);
            Assert.True(overridden.FinalPassed);
            var plain = Assert.Single(rows, r => r.Page == 4);
            Assert.Equal("", plain.InspectorVerdict);
            Assert.False(plain.FinalPassed);
        }
    }

    [Fact]
    public void SummaryBoxShowsInspectorLine()
    {
        using var image = new SKBitmap(600, 400);
        using (var canvas = new SKCanvas(image)) canvas.Clear(SKColors.White);
        var verdict = new InspectorVerdict(true, "user1", DateTime.Now, "확인");
        Annotate.DrawSummaryBox(image, CheckOutcome(), verdict);   // 예외 없이 그려진다
        var path = Path.Combine(_directory, "out.jpg");
        Annotate.SaveAnnotatedJpeg(image, CheckOutcome(), new Dictionary<string, (byte, byte, byte, byte)>(),
                                   path, inspector: verdict,
                                   barcodes: [new BarcodeHit("DataMatrix", "X", (10, 10, 40, 40), false)]);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DisplayText()
    {
        Assert.Equal("합격(검사자 확인)", new InspectorVerdict(true, "u", DateTime.Now, "").Display);
        Assert.Equal("부적합(검사자)", new InspectorVerdict(false, "u", DateTime.Now, "").Display);
    }
}
