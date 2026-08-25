// 필드 검출 영역 제한과 라벨 정렬(이형지 크롭·기울기 보정) 테스트.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class FieldZoneTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly StandardsBundle _standards;

    public FieldZoneTests()
    {
        _standards = StandardsBundle.Load(new AppConfig(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly LabelRecord Record = new(
        "25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
        "2024-05-10", "2027-05-09", "08806173612345", "MDR");

    private static OcrWord WordAt(string text, double cx, double cy) =>
        new(text, ((int)(cx * 1000) - 40, (int)(cy * 1000) - 10, 80, 20), 95);

    [Fact]
    public void ZoneRestrictsMatchesToRegisteredArea()
    {
        var engine = new InspectionEngine(_standards, new InspectionOptions
        {
            Zones = [new FieldZone("LOT", "", (0.0, 0.0, 0.5, 0.3))],
        });
        // 영역 안(좌상단) 1건 + 영역 밖(우하단) 1건 → 영역 안만 인정
        var outcome = engine.Inspect(Record, "MDR",
            [WordAt("x-25090776", 0.2, 0.1), WordAt("x-25090776", 0.9, 0.9)],
            pageSize: (1000, 1000));
        Assert.Equal(1, outcome.Fields["LOT"].Found);

        // 영역 미등록 필드는 영향 없음 / 페이지 크기 미전달 시에도 필터 안 함
        var noSize = engine.Inspect(Record, "MDR",
            [WordAt("x-25090776", 0.2, 0.1), WordAt("x-25090776", 0.9, 0.9)]);
        Assert.Equal(2, noSize.Fields["LOT"].Found);
    }

    [Fact]
    public void ZoneWithStandardAppliesOnlyToThatStandard()
    {
        var engine = new InspectionEngine(_standards, new InspectionOptions
        {
            Zones = [new FieldZone("LOT", "A00", (0.0, 0.0, 0.3, 0.3))],
        });
        var words = new[] { WordAt("x-25090776", 0.9, 0.9) };
        // MDR 검사에는 A00 전용 영역이 적용되지 않음
        Assert.Equal(1, engine.Inspect(Record, "MDR", words,
            pageSize: (1000, 1000)).Fields["LOT"].Found);
        Assert.Equal(0, engine.Inspect(Record, "A00", words,
            pageSize: (1000, 1000)).Fields["LOT"].Found);
    }

    [Fact]
    public void ZoneAllowsSmallDrift()
    {
        var engine = new InspectionEngine(_standards, new InspectionOptions
        {
            Zones = [new FieldZone("LOT", "", (0.1, 0.1, 0.2, 0.1))],
        });
        // 영역보다 3% 벗어난 위치 — 마진(4%) 안이므로 인정
        var outcome = engine.Inspect(Record, "MDR",
            [WordAt("x-25090776", 0.33, 0.23)], pageSize: (1000, 1000));
        Assert.Equal(1, outcome.Fields["LOT"].Found);
    }
}

public class LabelAlignTests
{
    /// <summary>흰 배경 위에 하늘색 이형지 사각형(안에 흰 라벨)을 회전시켜 그린다.</summary>
    private static SKBitmap Scan(double angleDegrees, out SKRect liner)
    {
        var bmp = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        liner = new SKRect(150, 100, 650, 500);
        canvas.Save();
        canvas.Translate(400, 300);
        canvas.RotateDegrees((float)angleDegrees);
        canvas.Translate(-400, -300);
        using var blue = new SKPaint
        { Color = new SKColor(135, 206, 235), Style = SKPaintStyle.Fill };  // 하늘색
        canvas.DrawRect(liner, blue);
        using var white = new SKPaint
        { Color = SKColors.White, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(200, 150, 600, 450), white);   // 라벨(흰색)
        canvas.Restore();
        return bmp;
    }

    [Fact]
    public void CropsWhiteMarginOutsideLiner()
    {
        using var image = Scan(0, out var liner);
        var result = ImagePreprocess.DeskewAndCropLiner(image);
        Assert.True(result.Cropped);
        // 크롭 결과가 이형지 크기(500x400)에 근접 (마진 포함 오차 허용)
        Assert.InRange(result.Image.Width, 480, 540);
        Assert.InRange(result.Image.Height, 380, 440);
        if (!ReferenceEquals(result.Image, image)) result.Image.Dispose();
    }

    [Fact]
    public void DeskewsTiltedScan()
    {
        using var image = Scan(3.0, out _);
        var result = ImagePreprocess.DeskewAndCropLiner(image);
        Assert.True(result.Cropped);
        // 감지한 기울기가 실제(3도)에 근접
        Assert.InRange(Math.Abs(result.AngleDegrees), 1.5, 4.5);
        // 보정 후 크롭 크기가 정자세 이형지 크기에 근접
        Assert.InRange(result.Image.Width, 470, 560);
        Assert.InRange(result.Image.Height, 370, 460);
        if (!ReferenceEquals(result.Image, image)) result.Image.Dispose();
    }

    [Fact]
    public void NoLinerMeansNoCrop()
    {
        using var image = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(image)) canvas.Clear(SKColors.White);
        var result = ImagePreprocess.DeskewAndCropLiner(image);
        Assert.False(result.Cropped);
        Assert.Same(image, result.Image);
    }
}
