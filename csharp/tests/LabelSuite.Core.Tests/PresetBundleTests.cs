// 프리셋 번들 — 규격·필드 규칙·학습 데이터를 통째로 다른 PC에 이식하는 흐름과
// 설정의 원자적 저장·스키마 버전 이전을 검증한다.
using System.Text.Json.Nodes;
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class PresetBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public PresetBundleTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public void ExportThenImportCarriesRulesAndLearningButKeepsMachineSettings()
    {
        var source = new AppConfig(Dir("pc-a"));
        // 규칙 편집: 규격 카운트·필드 영역·유효기간
        source.StandardsRaw["standards"]!["MDR"]!["counts"]!["LOT"] = 5;
        source.SaveStandards();
        source.Section("fields")["zones"] = new JsonArray(new JsonObject
        {
            ["field"] = "LOT", ["standard"] = "", ["region"] = new JsonArray(10, 20, 30, 5),
        });
        source.Settings["shelf_life_months"] = 24;
        source.Settings["save_directory"] = @"C:\pc-a-only";
        source.Section("ocr")["engine"] = "aws";
        source.SaveSettings();
        // 학습 데이터 파일
        var merges = new WordMergeRules(Path.Combine(source.Directory, "word_merges.json"));
        merges.Add(["MEGACATH", "KIT"]);
        var corrections = new OcrCorrections(Path.Combine(source.Directory, "ocr_corrections.json"));
        corrections.Add("25O9O776", "25090776", "LOT");

        var bundle = Path.Combine(_root, "line-a" + PresetBundle.Extension);
        var manifest = PresetBundle.Export(bundle, source, "A 라인 스텐트", "2026-09 기준");
        Assert.Equal("A 라인 스텐트", manifest.Name);
        Assert.Contains("standards.json", manifest.Contents);
        Assert.Contains("word_merges.json", manifest.Contents);
        Assert.Contains("ocr_corrections.json", manifest.Contents);
        Assert.Equal(manifest.Name, PresetBundle.Inspect(bundle).Name);

        // 다른 PC: 자기만의 저장 폴더·엔진 선택을 가진 상태
        var target = new AppConfig(Dir("pc-b"));
        target.Settings["save_directory"] = @"D:\pc-b";
        target.Section("ocr")["engine"] = "pattern";
        target.SaveSettings();

        var applied = PresetBundle.Import(bundle, target);
        Assert.NotNull(applied.BackupDirectory);
        Assert.True(File.Exists(Path.Combine(applied.BackupDirectory!, AppConfig.SettingsFile)));

        Assert.Equal(5, target.StandardsRaw["standards"]!["MDR"]!["counts"]!["LOT"]!.GetValue<int>());
        Assert.Equal(24, target.GetInt("shelf_life_months", -1));
        Assert.Single(target.Section("fields")["zones"]!.AsArray());
        Assert.Single(new WordMergeRules(Path.Combine(target.Directory, "word_merges.json")).Rules);
        Assert.Single(new OcrCorrections(Path.Combine(target.Directory, "ocr_corrections.json")).Entries);
        // PC 고유값은 유지
        Assert.Equal(@"D:\pc-b", target.GetString("save_directory"));
        Assert.Equal("pattern", target.Section("ocr")["engine"]!.GetValue<string>());
        // 디스크에도 영속 (재시작 시 유지)
        Assert.Equal(24, new AppConfig(target.Directory).GetInt("shelf_life_months", -1));
    }

    [Fact]
    public void LearningBundleIsRejectedAsPreset()
    {
        var glyphs = new GlyphLibrary(Path.Combine(_root, "g.json"));
        var corrections = new OcrCorrections(Path.Combine(_root, "c.json"));
        corrections.Add("O1", "01");
        var learn = Path.Combine(_root, "x" + LearningBundle.Extension);
        LearningBundle.Export(learn, glyphs, corrections);
        var ex = Assert.Throws<InvalidDataException>(() => PresetBundle.Inspect(learn));
        Assert.Contains("프리셋", ex.Message);
    }

    [Fact]
    public void SettingsSaveIsAtomicAndStampsSchemaVersion()
    {
        var dir = Dir("cfg");
        // 구버전(v1) 설정 파일을 흉내
        File.WriteAllText(Path.Combine(dir, AppConfig.SettingsFile),
                          """{ "schema_version": 1, "shelf_life_months": 12 }""");
        var config = new AppConfig(dir);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.GetInt("schema_version", -1));
        Assert.Equal(12, config.GetInt("shelf_life_months", -1));   // 사용자 값 유지
        Assert.False(config.SettingsFromNewerVersion);
        config.SaveSettings();
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));            // 임시 파일이 남지 않음
        Assert.Equal(AppConfig.CurrentSchemaVersion,
                     new AppConfig(dir).GetInt("schema_version", -1));
    }

    [Fact]
    public void NewerSchemaIsKeptAndFlagged()
    {
        var dir = Dir("newer");
        File.WriteAllText(Path.Combine(dir, AppConfig.SettingsFile),
                          $$"""{ "schema_version": {{AppConfig.CurrentSchemaVersion + 5}} }""");
        var config = new AppConfig(dir);
        Assert.True(config.SettingsFromNewerVersion);
        Assert.Equal(AppConfig.CurrentSchemaVersion + 5, config.GetInt("schema_version", -1));
    }
}
