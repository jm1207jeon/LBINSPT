// 라벨 양식 자동 감지(텍스트/이미지 패턴)와 OCR 신뢰도 알람 판정 테스트.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class LabelFormDetectorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LabelFormDetectorTests() { Directory.CreateDirectory(_directory); }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string TemplatePath => Path.Combine(_directory, "form_templates.json");

    // 우상단 20% 영역에서 양식명을 찾는 규칙
    private static LabelFormRule TextRule(string name, string pattern) =>
        new(name, name, (0.6, 0.0, 0.4, 0.2), pattern, UseImage: false);

    private static OcrWord WordAt(string text, double cx, double cy,
                                  int pageW = 1000, int pageH = 800) =>
        new(text, ((int)(cx * pageW) - 40, (int)(cy * pageH) - 10, 80, 20), 95);

    private static SKBitmap Blank(int w = 1000, int h = 800)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        return bmp;
    }

    [Fact]
    public void TextPatternDetectsOnlyInsideRegion()
    {
        var detector = new LabelFormDetector();
        var rules = new[] { TextRule("A00", @"Rev\.?A00") };
        using var image = Blank();

        var inside = detector.Detect(rules, [WordAt("Rev.A00", 0.75, 0.08)], image);
        Assert.NotNull(inside);
        Assert.Equal(("A00", "A00", "텍스트"),
                     (inside!.Name, inside.Standard, inside.Method));
        // 판별에 쓰인 OCR 단어의 바운딩 박스 반환 (검사 화면 박스 표시용)
        Assert.NotNull(inside.TextBbox);
        Assert.Equal(WordAt("Rev.A00", 0.75, 0.08).Bbox, inside.TextBbox!.Value);

        // 같은 텍스트라도 영역 밖(좌하단)이면 감지하지 않음
        Assert.Null(detector.Detect(rules, [WordAt("Rev.A00", 0.2, 0.9)], image));
        // 영역 안이라도 패턴이 다르면 감지하지 않음
        Assert.Null(detector.Detect(rules, [WordAt("Rev.A02", 0.75, 0.08)], image));
    }

    [Fact]
    public void BestMatchingRuleWins()
    {
        var detector = new LabelFormDetector();
        var rules = new[]
        {
            TextRule("A00", @"Rev\.?A00"),
            TextRule("A02", @"Rev\.?A02"),
        };
        using var image = Blank();
        var detected = detector.Detect(rules, [WordAt("Rev.A02", 0.7, 0.1)], image);
        Assert.Equal("A02", detected!.Name);
    }

    private static SKBitmap Mark(bool circle)
    {
        var bmp = Blank(400, 300);
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
        // 우상단 영역(60%~100%, 0~20%) 안에 서로 다른 마크
        if (circle) canvas.DrawCircle(320, 30, 22, paint);
        else for (var i = 0; i < 4; i++) canvas.DrawRect(250 + i * 30, 12, 14, 36, paint);
        return bmp;
    }

    [Fact]
    public void ImageTemplateLearnsAndDistinguishesForms()
    {
        var detector = new LabelFormDetector(TemplatePath);
        var region = (0.6, 0.0, 0.4, 0.2);
        var ruleA = new LabelFormRule("원형마크", "MDR", region, "", UseImage: true);
        var ruleB = new LabelFormRule("줄무늬마크", "MDD", region, "", UseImage: true);
        using var imageA = Mark(circle: true);
        using var imageB = Mark(circle: false);
        Assert.True(detector.LearnTemplate(ruleA, imageA));
        Assert.True(detector.LearnTemplate(ruleB, imageB));

        var rules = new[] { ruleA, ruleB };
        var detectedA = detector.Detect(rules, [], imageA);
        Assert.Equal(("원형마크", "MDR", "이미지"),
                     (detectedA!.Name, detectedA.Standard, detectedA.Method));
        Assert.Equal("줄무늬마크", detector.Detect(rules, [], imageB)!.Name);

        // 빈 라벨(마크 없음)은 어느 양식도 아님
        using var blank = Blank(400, 300);
        Assert.Null(detector.Detect(rules, [], blank));
    }

    [Fact]
    public void TemplatesPersistAcrossInstances()
    {
        var region = (0.6, 0.0, 0.4, 0.2);
        var rule = new LabelFormRule("원형마크", "MDR", region, "", UseImage: true);
        using var image = Mark(circle: true);
        new LabelFormDetector(TemplatePath).LearnTemplate(rule, image);

        var reloaded = new LabelFormDetector(TemplatePath);
        Assert.True(reloaded.HasTemplate("원형마크"));
        Assert.Equal("원형마크", reloaded.Detect([rule], [], image)!.Name);
    }

    [Fact]
    public void TextPlusImageRaisesConfidence()
    {
        var detector = new LabelFormDetector(TemplatePath);
        var rule = new LabelFormRule("A00", "A00", (0.6, 0.0, 0.4, 0.2),
                                     @"Rev\.?A00", UseImage: true);
        using var image = Mark(circle: true);
        detector.LearnTemplate(rule, image);
        var detected = detector.Detect([rule], [WordAt("Rev.A00", 0.75, 0.08,
                                                       400, 300)], image);
        Assert.Equal("텍스트+이미지", detected!.Method);
        Assert.True(detected.Score >= 0.95);
    }
}

public class OcrQualityTests
{
    private static List<OcrWord> Words(params int[] confidences) =>
        confidences.Select((c, i) => new OcrWord($"w{i}", (0, i * 20, 50, 15), c))
                   .ToList();

    [Fact]
    public void HighConfidenceIsNormal()
    {
        var report = OcrQuality.Assess(Words(95, 97, 92, 99, 96));
        Assert.False(report.IsPoor);
        Assert.Contains("정상", report.Summary);
    }

    [Fact]
    public void LowAverageTriggersAlarm()
    {
        var report = OcrQuality.Assess(Words(65, 60, 72, 68, 70));
        Assert.True(report.IsPoor);
        Assert.Contains("신뢰도 낮음", report.Summary);
    }

    [Fact]
    public void HighLowWordRatioTriggersAlarmEvenWithOkAverage()
    {
        // 평균은 임계 이상이지만 저신뢰 단어가 40% → 알람
        var report = OcrQuality.Assess(Words(65, 65, 65, 65, 99, 99, 99, 99, 99, 99));
        Assert.True(report.Average >= 80);
        Assert.True(report.IsPoor);
        Assert.Equal(4, report.LowCount);
    }

    [Fact]
    public void EmptyPageIsNotAlarmed()
    {
        Assert.False(OcrQuality.Assess([]).IsPoor);
    }

    [Fact]
    public void LowConfidenceWordsAreFiltered()
    {
        var low = OcrQuality.LowConfidenceWords(Words(50, 90, 65, 95), 70);
        Assert.Equal(2, low.Count);
        Assert.All(low, w => Assert.True(w.Confidence < 70));
    }
}
