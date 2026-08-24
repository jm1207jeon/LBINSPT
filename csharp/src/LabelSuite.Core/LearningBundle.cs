// 학습 데이터 이식 — 글자 패턴(glyphs)과 OCR 교정 사전을 하나의 번들(zip)로
// 내보내고, 다른 PC에서 가져와 병합해 동일한 인식 성능을 재현한다.
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

public sealed record LearningBundleInfo(int GlyphChars, int GlyphTemplates, int Corrections);

public static class LearningBundle
{
    public const string Extension = ".lslearn";
    private const string GlyphsEntry = "glyphs.json";
    private const string CorrectionsEntry = "ocr_corrections.json";
    private const string ManifestEntry = "manifest.json";

    /// <summary>학습 데이터를 zip 번들로 내보낸다.</summary>
    public static LearningBundleInfo Export(string bundlePath, GlyphLibrary glyphs,
                                            OcrCorrections corrections)
    {
        var temp = Directory.CreateTempSubdirectory("lslearn-export");
        try
        {
            var glyphsFile = Path.Combine(temp.FullName, GlyphsEntry);
            glyphs.SaveTo(glyphsFile);

            var entries = corrections.Entries;
            var correctionsFile = Path.Combine(temp.FullName, CorrectionsEntry);
            File.WriteAllText(correctionsFile, new JsonObject
            {
                ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
                {
                    ["Wrong"] = e.Wrong,
                    ["Right"] = e.Right,
                    ["Field"] = e.Field,
                    ["LearnedAt"] = e.LearnedAt,
                }).ToArray()),
            }.ToJsonString());

            var info = new LearningBundleInfo(glyphs.CharCount, glyphs.TemplateCount,
                                              entries.Count);
            var manifestFile = Path.Combine(temp.FullName, ManifestEntry);
            File.WriteAllText(manifestFile, new JsonObject
            {
                ["app"] = AppConfig.AppName,
                ["kind"] = "learning-bundle",
                ["version"] = 1,
                ["exported_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["glyph_chars"] = info.GlyphChars,
                ["glyph_templates"] = info.GlyphTemplates,
                ["corrections"] = info.Corrections,
            }.ToJsonString());

            File.Delete(bundlePath);
            ZipFile.CreateFromDirectory(temp.FullName, bundlePath);
            return info;
        }
        finally
        {
            try { temp.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>번들의 학습 데이터를 현재 라이브러리/사전에 병합한다.
    /// 반환: 새로 반영된 수 (글자 템플릿 / 교정 항목).</summary>
    public static LearningBundleInfo Import(string bundlePath, GlyphLibrary glyphs,
                                            OcrCorrections corrections)
    {
        var temp = Directory.CreateTempSubdirectory("lslearn-import");
        try
        {
            ZipFile.ExtractToDirectory(bundlePath, temp.FullName);
            var glyphsFile = Path.Combine(temp.FullName, GlyphsEntry);
            var correctionsFile = Path.Combine(temp.FullName, CorrectionsEntry);
            if (!File.Exists(glyphsFile) && !File.Exists(correctionsFile))
                throw new InvalidDataException(
                    "LaVIS 학습 데이터 번들이 아닙니다 (glyphs/corrections 없음).");

            var addedTemplates = 0;
            var importedGlyphs = new GlyphLibrary(
                File.Exists(glyphsFile) ? glyphsFile : null);
            if (importedGlyphs.TemplateCount > 0)
                addedTemplates = glyphs.MergeFrom(importedGlyphs);

            var addedCorrections = 0;
            if (File.Exists(correctionsFile))
            {
                var imported = new OcrCorrections(correctionsFile);
                addedCorrections = corrections.ImportFrom(imported.Entries);
            }
            return new LearningBundleInfo(importedGlyphs.CharCount, addedTemplates,
                                          addedCorrections);
        }
        finally
        {
            try { temp.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
