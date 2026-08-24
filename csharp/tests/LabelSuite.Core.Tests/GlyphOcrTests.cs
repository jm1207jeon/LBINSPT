// 패턴 학습 엔진 왕복 테스트 — 합성 렌더링으로 학습→인식 검증.
using LabelSuite.Core;
using SkiaSharp;
using Xunit;

namespace LabelSuite.Core.Tests;

public class GlyphOcrTests
{
    private static readonly SKTypeface Mono =
        SKTypeface.FromFamilyName("DejaVu Sans Mono", SKFontStyle.Bold)
        ?? SKTypeface.Default;

    /// <summary>단어들을 흰 바탕에 그리고 각 단어의 실제 bbox를 함께 반환.</summary>
    private static (SKBitmap Image, List<OcrWord> Words) Render(params string[] lines)
    {
        const float fontSize = 32;
        using var font = new SKFont(Mono, fontSize);
        var charWidth = font.MeasureText("0");
        var width = (int)(lines.Max(l => l.Length) * charWidth + 80);
        var height = lines.Length * 70 + 40;
        var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var words = new List<OcrWord>();
        for (var i = 0; i < lines.Length; i++)
        {
            var y = 40 + i * 70;
            var x = 40f;
            foreach (var word in lines[i].Split(' '))
            {
                canvas.DrawText(word, x, y + fontSize, font, paint);
                var wordWidth = font.MeasureText(word);
                words.Add(new OcrWord(word,
                    ((int)x - 2, y, (int)wordWidth + 4, (int)(fontSize * 1.3)), 99));
                x += wordWidth + charWidth * 2;   // 단어 간 넓은 간격
            }
        }
        return (bmp, words);
    }

    [Fact]
    public async Task LearnThenRecognizeRoundTrip()
    {
        var library = new GlyphLibrary();
        var engine = new GlyphOcrEngine(library);

        // 학습: 문자셋을 다양하게 포함한 단어들 (Textract 결과를 흉내)
        var (trainImage, trainWords) = Render(
            "0123456789 NCN20-080", "HANARO-01 2024-05-10", "ABCDEFGHIJ KLMNSTUWXY");
        var added = engine.LearnFrom(trainImage, trainWords);
        Assert.True(added > 20, $"학습된 템플릿이 너무 적음: {added}");
        Assert.True(library.CharCount >= 8);

        // 인식: 학습에 쓰지 않은 조합의 문자열
        var (testImage, _) = Render("25080123 NCN20-230");
        var detected = await engine.DetectWordsAsync(testImage);
        var texts = detected.Select(w => w.Text).ToList();
        Assert.Contains("25080123", texts);
        Assert.Contains("NCN20-230", texts);
        trainImage.Dispose();
        testImage.Dispose();
    }

    [Fact]
    public async Task EmptyLibraryThrowsGuidance()
    {
        var engine = new GlyphOcrEngine(new GlyphLibrary());
        var (image, _) = Render("25090776");
        var ex = await Assert.ThrowsAsync<OcrException>(
            () => engine.DetectWordsAsync(image));
        Assert.Contains("자동 학습", ex.Message);
        image.Dispose();
    }

    [Fact]
    public void LibraryPersistsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var library = new GlyphLibrary(path);
            var engine = new GlyphOcrEngine(library);
            var (image, words) = Render("0123456789");
            engine.LearnFrom(image, words);
            image.Dispose();
            Assert.True(library.TemplateCount > 0);

            var reloaded = new GlyphLibrary(path);
            Assert.Equal(library.CharCount, reloaded.CharCount);
            Assert.Equal(library.TemplateCount, reloaded.TemplateCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PunctuationAndSpecialsRoundTrip()
    {
        // 하이픈·마침표·어퍼스트로피는 크기 정규화 후 모양이 비슷해 오인식되기 쉽다.
        // 세로 위치/상대 크기 특징으로 구분되는지 검증.
        var library = new GlyphLibrary();
        var engine = new GlyphOcrEngine(library);
        var (trainImage, trainWords) = Render(
            "0123456789 2024.05.10", "AB-CD'EF 25-08'10.3");
        var added = engine.LearnFrom(trainImage, trainWords);
        Assert.True(added > 10, $"학습된 템플릿이 너무 적음: {added}");

        var (testImage, _) = Render("2024-08.25'01");
        var detected = await engine.DetectWordsAsync(testImage);
        Assert.Contains("2024-08.25'01", detected.Select(w => w.Text));
        trainImage.Dispose();
        testImage.Dispose();
    }

    [Fact]
    public void LegacyTemplateFormatStillLoads()
    {
        // 구버전 파일([aspect][vector] 블롭)도 로드되고, 저장 시 신형 포맷으로 갱신
        var pixels = new float[GlyphLibrary.GlyphWidth * GlyphLibrary.GlyphHeight];
        for (var i = 0; i < pixels.Length; i += 3) pixels[i] = 1f;
        var vector = GlyphLibrary.NormalizeVector(pixels);
        var legacy = new float[vector.Length + 1];
        legacy[0] = 0.7f;
        vector.CopyTo(legacy, 1);
        var bytes = new byte[legacy.Length * 4];
        Buffer.BlockCopy(legacy, 0, bytes, 0, bytes.Length);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path,
                $"{{\"A\":[\"{Convert.ToBase64String(bytes)}\"]}}");
            var library = new GlyphLibrary(path);
            Assert.Equal(1, library.CharCount);
            Assert.Equal(1, library.TemplateCount);

            library.Save();   // 신형 포맷([aspect][voffset][relheight][vector])으로 재저장
            var reloaded = new GlyphLibrary(path);
            Assert.Equal(1, reloaded.TemplateCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MismatchedSegmentationIsNotLearned()
    {
        // 단어 텍스트 길이와 분할 글자 수가 다르면 잘못 학습하지 않아야 한다
        var library = new GlyphLibrary();
        var engine = new GlyphOcrEngine(library);
        var (image, words) = Render("2509");
        var wrong = words.Select(w => w with { Text = "250" }).ToList();   // 길이 불일치
        Assert.Equal(0, engine.LearnFrom(image, wrong));
        image.Dispose();
    }
}
