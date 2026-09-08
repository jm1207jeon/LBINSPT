// 설정 값 검증·보정(AppConfig.Normalize / SettingRanges / PathRules) — '마음껏 만져도 깨지지 않는 설정'.
using System.Text.Json;
using System.Text.Json.Nodes;
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class NormalizeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public NormalizeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string SettingsPath => Path.Combine(_directory, AppConfig.SettingsFile);

    private AppConfig LoadWith(string json)
    {
        File.WriteAllText(SettingsPath, json);
        return new AppConfig(_directory);
    }

    private JsonObject OnDisk() =>
        JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

    [Fact]
    public void OutOfRangeValuesAreClampedAndReported()
    {
        var config = LoadWith("""{ "pdf_render_zoom": 20, "ocr": { "max_dimension": 9999 } }""");
        Assert.Equal(8.0, config.GetDouble("pdf_render_zoom", -1));
        Assert.Equal(4000, config.SectionInt("ocr", "max_dimension", -1));
        Assert.Equal(2, config.Corrections.Count);
        Assert.Contains(config.Corrections, c => c.StartsWith("pdf_render_zoom:"));
        Assert.Contains(config.Corrections, c => c.StartsWith("ocr.max_dimension:"));
        // 파일에도 저장됨
        var disk = OnDisk();
        Assert.Equal(8.0, disk["pdf_render_zoom"]!.GetValue<double>());
        Assert.Equal(4000, disk["ocr"]!["max_dimension"]!.GetValue<int>());
    }

    [Fact]
    public void InvalidZoneEntryIsDroppedNotZeroed()
    {
        var config = LoadWith("""
            { "fields": { "zones": [
                { "field": "LOT", "standard": "", "region": ["a", 1, 2, 3] },
                { "field": "PN",  "standard": "", "region": [10, 20, 30, 5] },
                { "field": "REF", "standard": "", "region": [10, 20, 30] },
                { "field": "GTIN", "standard": "", "region": [10, 20, 130, 5] },
                "not-an-object"
            ] } }
            """);
        var zones = config.Section("fields")["zones"]!.AsArray();
        var only = Assert.Single(zones);
        Assert.Equal("PN", only!["field"]!.GetValue<string>());
        Assert.Equal(4, config.Corrections.Count(c => c.StartsWith("fields.zones[")));
        Assert.Single(OnDisk()["fields"]!["zones"]!.AsArray());
    }

    [Theory]
    [InlineData("AWS", "aws")]
    [InlineData("tesseract", "aws")]
    [InlineData(" Onnx ", "onnx")]
    public void EngineNameIsCaseNormalized(string raw, string expected)
    {
        var config = LoadWith($$"""{ "ocr": { "engine": "{{raw}}" } }""");
        Assert.Equal(expected, config.Section("ocr")["engine"]!.GetValue<string>());
        Assert.Contains(config.Corrections, c => c.StartsWith("ocr.engine:"));
    }

    [Fact]
    public void ValidEngineNameIsNotReported()
    {
        var config = LoadWith("""{ "ocr": { "engine": "pattern" } }""");
        Assert.Equal("pattern", config.Section("ocr")["engine"]!.GetValue<string>());
        Assert.DoesNotContain(config.Corrections, c => c.StartsWith("ocr.engine:"));
    }

    [Theory]
    [InlineData("\"sometimes\"")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("2.5")]
    [InlineData("true")]
    public void InvalidPrefetchPolicyFallsBackToAll(string raw)
    {
        var config = LoadWith($$"""{ "prefetch_policy": {{raw}} }""");
        Assert.Equal("all", config.Settings["prefetch_policy"]!.GetValue<string>());
        Assert.Contains(config.Corrections, c => c.StartsWith("prefetch_policy:"));
    }

    [Theory]
    [InlineData("\"all\"")]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("50")]
    public void ValidPrefetchPolicyIsKept(string raw)
    {
        var config = LoadWith($$"""{ "prefetch_policy": {{raw}} }""");
        Assert.DoesNotContain(config.Corrections, c => c.StartsWith("prefetch_policy:"));
        Assert.Equal(raw, config.Settings["prefetch_policy"]!.ToJsonString());
    }

    [Fact]
    public void InvalidSaveDirectoryIsClearedWithNote()
    {
        var config = LoadWith("""{ "save_directory": "relative\\x" }""");
        Assert.Equal("", config.GetString("save_directory", "unset"));
        var note = Assert.Single(config.Corrections, c => c.StartsWith("save_directory:"));
        Assert.Contains("relative\\x", note);
        // 유효한 경로·빈 값은 손대지 않음
        Assert.Empty(LoadWith("""{ "save_directory": "C:\\LaVIS_결과" }""").Corrections);
        Assert.Empty(LoadWith("""{ "save_directory": "" }""").Corrections);
    }

    [Fact]
    public void NormalizeIsIdempotent()
    {
        LoadWith("""
            { "pdf_render_zoom": 20, "jpeg_quality": 3, "ocr": { "engine": "AWS" },
              "fields": { "zones": [ { "field": "LOT", "standard": "", "region": [1, 2, 3] } ],
                          "custom": [ { "name": "" }, { "name": "X" }, { "name": "X" } ],
                          "disabled": [ "LOT", "PN", "NOPE" ] } }
            """);
        var firstBytes = File.ReadAllBytes(SettingsPath);
        var second = new AppConfig(_directory);
        Assert.Empty(second.Corrections);
        Assert.Equal(firstBytes, File.ReadAllBytes(SettingsPath));
        // 내용 확인
        var custom = second.Section("fields")["custom"]!.AsArray();
        Assert.Single(custom);
        Assert.Equal(["PN"], second.Section("fields")["disabled"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void DefaultSettingsProduceNoCorrections()
    {
        var config = new AppConfig(_directory);   // 번들 기본값 그대로
        Assert.Empty(config.Corrections);
        Assert.Empty(new AppConfig(_directory).Corrections);
    }

    [Fact]
    public void ArrayItemRulesAreApplied()
    {
        var config = LoadWith("""
            { "fields": {
                "charsets": [ { "field": "", "allowed": "1" }, { "field": "LOT", "allowed": "" } ],
                "same_value": [ { "name": "a", "pattern": "x", "min_instances": 99 },
                                { "name": "b", "pattern": "y", "min_instances": "bad" },
                                { "name": "c", "pattern": "z" } ] },
              "label_forms": { "rules": [ { "name": "r", "region": [0, 0, 100, 100] },
                                          { "name": "s", "region": null } ] } }
            """);
        var fields = config.Section("fields");
        Assert.Single(fields["charsets"]!.AsArray());
        var same = fields["same_value"]!.AsArray();
        Assert.Equal(20, same[0]!["min_instances"]!.GetValue<int>());
        Assert.Equal(2, same[1]!["min_instances"]!.GetValue<int>());
        Assert.False(same[2]!.AsObject().ContainsKey("min_instances"));   // 없는 키는 보충 안 함
        Assert.Single(config.Section("label_forms")["rules"]!.AsArray());
    }

    [Fact]
    public void SettingRangesCoverAllNumericDefaultKeys()
    {
        var defaults = JsonNode.Parse(AppConfig.EmbeddedDefault(AppConfig.SettingsFile))!.AsObject();
        string[] exceptions = ["schema_version"];
        var missing = new List<string>();
        void Walk(JsonObject obj, string prefix)
        {
            foreach (var (key, value) in obj)
            {
                var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (value is JsonObject child) Walk(child, path);
                else if (value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
                         && !exceptions.Contains(path) && SettingRanges.Find(path) is null)
                    missing.Add(path);
            }
        }
        Walk(defaults, "");
        Assert.True(missing.Count == 0,
            "SettingRanges.All에 없는 숫자 설정 키: " + string.Join(", ", missing));
        // 범위표의 기본값은 번들 기본값과 일치해야 한다 (단일 출처)
        foreach (var def in SettingRanges.All.Where(d => !d.Path.Contains("[]")))
        {
            JsonNode? node = defaults;
            foreach (var part in def.Path.Split('.')) node = node?[part];
            Assert.True(SettingRanges.TryNumber(node, out var value), def.Path);
            Assert.True(Math.Abs(value - def.Default) < 1e-9,
                        $"{def.Path}: 범위표 기본값 {def.Default} ≠ settings.json {value}");
        }
    }

    [Fact]
    public void NewerSchemaSkipsNormalize()
    {
        var config = LoadWith($$"""
            { "schema_version": {{AppConfig.CurrentSchemaVersion + 5}}, "pdf_render_zoom": 20 }
            """);
        Assert.True(config.SettingsFromNewerVersion);
        Assert.Empty(config.Corrections);
        Assert.Equal(20.0, config.GetDouble("pdf_render_zoom", -1));
    }

    [Fact]
    public void TryNormalizeReportsTypeAndRange()
    {
        var def = SettingRanges.Find("ocr.max_dimension")!;
        Assert.False(SettingRanges.TryNormalize(def, JsonValue.Create(1500), out var same, out var none));
        Assert.Null(none);
        Assert.Equal(1500, same.GetValue<int>());
        Assert.True(SettingRanges.TryNormalize(def, JsonValue.Create("big"), out var typed, out var typeNote));
        Assert.Equal(2000, typed.GetValue<int>());
        Assert.Contains("기본값", typeNote);
        Assert.True(SettingRanges.TryNormalize(def, JsonValue.Create(100), out var low, out _));
        Assert.Equal(500, low.GetValue<int>());
        Assert.Equal(3000, def.ParseInt("3000"));
        Assert.Equal(4000, def.ParseInt("99999"));
        Assert.Equal(2000, def.ParseInt("abc"));
    }
}

public class PathRulesTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("relative\\x", false)]
    [InlineData("C:\\a|b", false)]
    [InlineData("C:\\a?b", false)]
    [InlineData("..\\x", false)]
    [InlineData("C:\\LaVIS_결과", true)]
    [InlineData("\\\\srv\\share\\LaVIS", true)]
    [InlineData("D:\\x ", true)]
    [InlineData("\\\\srv", false)]
    [InlineData("C:\\a\\..\\b", false)]
    [InlineData("C:/forward/slash", true)]
    public void IsValidDirectory(string path, bool expected) =>
        Assert.Equal(expected, PathRules.IsValidDirectory(path));

    [Fact]
    public void UnixRootIsValidOnlyOffWindows()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), PathRules.IsValidDirectory("/tmp/lavis"));
        Assert.False(PathRules.IsValidDirectory("/tmp/../etc"));
    }

    [Fact]
    public void UniquePathAppendsSuffix()
    {
        var dir = Directory.CreateTempSubdirectory("lavis-unique");
        try
        {
            var path = Path.Combine(dir.FullName, "결과.csv");
            Assert.Equal(path, PathRules.UniquePath(path));
            File.WriteAllText(path, "");
            var second = PathRules.UniquePath(path);
            Assert.Equal(Path.Combine(dir.FullName, "결과_(2).csv"), second);
            File.WriteAllText(second, "");
            Assert.Equal(Path.Combine(dir.FullName, "결과_(3).csv"), PathRules.UniquePath(path));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void FileStemTruncatedAndSafe()
    {
        var long41 = new string('A', 41);
        Assert.Equal(40, PathRules.SanitizeFileStem(long41).Length);
        Assert.Equal("LOT-25_09", PathRules.SanitizeFileStem("LOT 한글-25_09 ★"));
        Assert.Equal("file", PathRules.SanitizeFileStem("한글만"));
        Assert.Equal("page", PathRules.SanitizeFileStem(null, fallback: "page"));
        Assert.Equal("ab", PathRules.SanitizeFileStem("abcdef", maxLen: 2));
    }
}
