// 프리셋 번들 — 검사 규칙(규격·필드 규칙·영역·양식 감지·컬럼 매핑)과 학습 데이터
// (글자 패턴·교정 사전·병합 규칙·동일값 레이아웃·양식 템플릿·유형 프로필)를 하나의
// zip으로 내보내고 다른 PC/제품군에 통째로 적용한다. .lslearn(학습 데이터만)의 상위 개념.
//
// 포함하지 않는 것(PC 고유값): AWS 프로필·저장 폴더·최근 파일·캐시·이력 DB.
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

public sealed record PresetManifest(string Name, string Note, string ExportedAt, int Version,
                                    int SchemaVersion, IReadOnlyList<string> Contents,
                                    string? BackupDirectory = null);

public static class PresetBundle
{
    public const string Extension = ".lavispreset";
    public const int Version = 1;
    private const string ManifestEntry = "manifest.json";
    private const string SettingsEntry = "settings.preset.json";

    /// <summary>settings.json 중 프리셋에 담는 키 — 검사 규칙에 해당하는 것만.</summary>
    public static readonly string[] SettingsKeys =
    [
        "shelf_life_months", "ocr", "fields", "preprocess",
        "type_learning", "label_forms", "overlay", "pdf_render_zoom",
    ];

    /// <summary>settings.json 안에서도 PC 고유값이라 프리셋에 담지 않는 하위 키.</summary>
    private static readonly Dictionary<string, string[]> ExcludedSubKeys = new()
    {
        ["ocr"] = ["engine"],   // 엔진 선택은 PC 환경(AWS 자격증명 유무)에 따라 다름
    };

    /// <summary>데이터 폴더에서 통째로 복사하는 파일들 (있는 것만).</summary>
    public static readonly string[] DataFiles =
    [
        AppConfig.StandardsFile, AppConfig.ColumnMapsFile,
        "glyphs.json", "ocr_corrections.json", "word_merges.json",
        "same_value_layouts.json", "form_templates.json", "label_profiles.json",
    ];

    public static PresetManifest Export(string bundlePath, AppConfig config,
                                        string name, string note = "")
    {
        var temp = Directory.CreateTempSubdirectory("lavis-preset-export");
        try
        {
            var contents = new List<string>();
            // 1) 설정 중 규칙 부분만
            var subset = new JsonObject();
            foreach (var key in SettingsKeys)
            {
                if (config.Settings[key] is not { } node) continue;
                var clone = node.DeepClone();
                if (clone is JsonObject section && ExcludedSubKeys.TryGetValue(key, out var drop))
                    foreach (var sub in drop) section.Remove(sub);
                subset[key] = clone;
            }
            File.WriteAllText(Path.Combine(temp.FullName, SettingsEntry),
                              subset.ToJsonString(Pretty));
            contents.Add(SettingsEntry);
            // 2) 데이터 파일 복사
            foreach (var file in DataFiles)
            {
                var source = Path.Combine(config.Directory, file);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(temp.FullName, file));
                contents.Add(file);
            }
            var manifest = new PresetManifest(name.Trim(), note.Trim(),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Version,
                AppConfig.CurrentSchemaVersion, contents);
            File.WriteAllText(Path.Combine(temp.FullName, ManifestEntry), ToJson(manifest));

            File.Delete(bundlePath);
            ZipFile.CreateFromDirectory(temp.FullName, bundlePath);
            return manifest;
        }
        finally { try { temp.Delete(recursive: true); } catch (IOException) { } }
    }

    /// <summary>적용하지 않고 번들 정보만 읽는다 (가져오기 전 확인 대화상자용).</summary>
    public static PresetManifest Inspect(string bundlePath)
    {
        using var zip = ZipFile.OpenRead(bundlePath);
        var entry = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("LaVIS 프리셋 번들이 아닙니다 (manifest 없음).");
        using var reader = new StreamReader(entry.Open());
        return FromJson(reader.ReadToEnd());
    }

    /// <summary>번들을 데이터 폴더에 적용한다. 기존 파일은 backup-preset-시각 폴더에 보관하고,
    /// 설정은 규칙 키만 교체(PC 고유값 유지)한 뒤 config를 다시 읽는다.
    /// 메모리에 이미 올라간 학습 객체(글자 패턴 등)는 호출 측이 다시 로드하거나 재시작한다.</summary>
    public static PresetManifest Import(string bundlePath, AppConfig config)
    {
        var manifest = Inspect(bundlePath);
        if (manifest.Version > Version)
            throw new InvalidDataException(
                $"이 프리셋은 더 새로운 LaVIS에서 만들어졌습니다 (번들 v{manifest.Version}). 프로그램을 업데이트하세요.");
        var temp = Directory.CreateTempSubdirectory("lavis-preset-import");
        try
        {
            ZipFile.ExtractToDirectory(bundlePath, temp.FullName);
            var backupDir = Path.Combine(config.Directory,
                $"backup-preset-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backupDir);
            // 현재 상태 보관 (되돌리기용)
            foreach (var file in DataFiles.Append(AppConfig.SettingsFile))
            {
                var current = Path.Combine(config.Directory, file);
                if (File.Exists(current)) File.Copy(current, Path.Combine(backupDir, file), overwrite: true);
            }
            // 데이터 파일 교체
            foreach (var file in DataFiles)
            {
                var source = Path.Combine(temp.FullName, file);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(config.Directory, file);
                File.Copy(source, target + ".tmp", overwrite: true);
                File.Move(target + ".tmp", target, overwrite: true);
            }
            // 설정 규칙 키 교체
            var settingsFile = Path.Combine(temp.FullName, SettingsEntry);
            if (File.Exists(settingsFile)
                && JsonNode.Parse(File.ReadAllText(settingsFile)) is JsonObject subset)
            {
                foreach (var (key, value) in subset)
                {
                    if (!SettingsKeys.Contains(key)) continue;
                    if (value is JsonObject incoming && config.Settings[key] is JsonObject existing
                        && ExcludedSubKeys.TryGetValue(key, out var keep))
                    {
                        // 제외 하위 키(엔진 선택 등)는 현재 값 유지
                        var merged = incoming.DeepClone().AsObject();
                        foreach (var sub in keep)
                            if (existing[sub] is { } kept) merged[sub] = kept.DeepClone();
                        config.Settings[key] = merged;
                    }
                    else config.Settings[key] = value?.DeepClone();
                }
                config.SaveSettings();
            }
            config.Reload();
            return manifest with { BackupDirectory = backupDir };
        }
        finally { try { temp.Delete(recursive: true); } catch (IOException) { } }
    }

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ToJson(PresetManifest m) => new JsonObject
    {
        ["app"] = AppConfig.AppName,
        ["kind"] = "preset-bundle",
        ["version"] = m.Version,
        ["schema_version"] = m.SchemaVersion,
        ["name"] = m.Name,
        ["note"] = m.Note,
        ["exported_at"] = m.ExportedAt,
        ["contents"] = new JsonArray(m.Contents.Select(c => (JsonNode)c).ToArray()),
    }.ToJsonString(Pretty);

    private static PresetManifest FromJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("프리셋 manifest를 읽을 수 없습니다.");
        if (node["kind"]?.GetValue<string>() != "preset-bundle")
            throw new InvalidDataException(
                "LaVIS 프리셋 번들이 아닙니다. 학습 데이터(.lslearn)는 [학습 데이터 가져오기]를 사용하세요.");
        return new PresetManifest(
            node["name"]?.GetValue<string>() ?? "",
            node["note"]?.GetValue<string>() ?? "",
            node["exported_at"]?.GetValue<string>() ?? "",
            node["version"]?.GetValue<int>() ?? 1,
            node["schema_version"]?.GetValue<int>() ?? 1,
            node["contents"] is JsonArray arr
                ? arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
                : []);
    }
}
