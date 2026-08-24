// 학습 데이터 번들 — 내보내기/가져오기로 다른 PC에서 동일 성능 재현 검증.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class LearningBundleTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LearningBundleTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static GlyphTemplate MakeTemplate(int seed)
    {
        var pixels = new float[GlyphLibrary.GlyphWidth * GlyphLibrary.GlyphHeight];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (i + seed) % 7 == 0 ? 1f : 0f;
        return new GlyphTemplate(GlyphLibrary.NormalizeVector(pixels),
                                 0.6f + seed * 0.01f, 0.5f, 0.9f);
    }

    [Fact]
    public void ExportThenImportReproducesLearningData()
    {
        var glyphs = new GlyphLibrary(PathOf("glyphs.json"));
        glyphs.AddTemplate('0', MakeTemplate(1));
        glyphs.AddTemplate('5', MakeTemplate(2));
        glyphs.AddTemplate('A', MakeTemplate(3));
        var corrections = new OcrCorrections(PathOf("corr.json"));
        corrections.Add("25O9O776", "25090776", "LOT");
        corrections.Add("8806l73", "8806173", "GTIN");

        var bundle = PathOf("bundle" + LearningBundle.Extension);
        var exported = LearningBundle.Export(bundle, glyphs, corrections);
        Assert.Equal(3, exported.GlyphChars);
        Assert.Equal(3, exported.GlyphTemplates);
        Assert.Equal(2, exported.Corrections);
        Assert.True(File.Exists(bundle));

        // 다른 PC를 흉내: 빈 라이브러리/사전으로 가져오기
        var newGlyphs = new GlyphLibrary(PathOf("glyphs2.json"));
        var newCorrections = new OcrCorrections(PathOf("corr2.json"));
        var imported = LearningBundle.Import(bundle, newGlyphs, newCorrections);
        Assert.Equal(3, imported.GlyphTemplates);
        Assert.Equal(2, imported.Corrections);
        Assert.Equal(glyphs.CharCount, newGlyphs.CharCount);
        Assert.Equal(glyphs.TemplateCount, newGlyphs.TemplateCount);
        Assert.Equal(2, newCorrections.Entries.Count);
        Assert.True(newCorrections.ConfusableEquals("25O9O776", "25090776"));

        // 병합 파일이 디스크에 영속됐는지 (재시작해도 유지)
        Assert.Equal(3, new GlyphLibrary(PathOf("glyphs2.json")).TemplateCount);
        Assert.Equal(2, new OcrCorrections(PathOf("corr2.json")).Entries.Count);
    }

    [Fact]
    public void ImportIsIdempotentMerge()
    {
        var glyphs = new GlyphLibrary(PathOf("g.json"));
        glyphs.AddTemplate('7', MakeTemplate(4));
        var corrections = new OcrCorrections(PathOf("c.json"));
        corrections.Add("7O7", "707");
        var bundle = PathOf("b" + LearningBundle.Extension);
        LearningBundle.Export(bundle, glyphs, corrections);

        // 같은 번들을 자기 자신에 다시 가져와도 중복 축적되지 않음
        var again = LearningBundle.Import(bundle, glyphs, corrections);
        Assert.Equal(0, again.GlyphTemplates);   // 유사 중복은 무시됨
        Assert.Equal(1, glyphs.TemplateCount);
        Assert.Single(corrections.Entries);      // 같은 오인식 값은 교체
    }

    [Fact]
    public void InvalidBundleThrowsFriendlyError()
    {
        var bogus = PathOf("bogus.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(
            Directory.CreateDirectory(PathOf("emptydir")).FullName, bogus);
        var ex = Assert.Throws<InvalidDataException>(() =>
            LearningBundle.Import(bogus, new GlyphLibrary(), new OcrCorrections()));
        Assert.Contains("학습 데이터", ex.Message);
    }
}
