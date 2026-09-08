// 앱 설정 관리 — 임베디드 기본값을 %APPDATA%/LaVIS로 복사 후 로드/저장.
// 저장은 원자적(임시 파일 → 교체), 스키마 버전을 기록해 구버전 파일을 단계 이전한다.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

public class AppConfig
{
    /// <summary>제품명 — LaVIS: Label Verification &amp; Inspection System.</summary>
    public const string AppName = "LaVIS";
    /// <summary>개명 전 데이터 폴더 이름 (기존 사용자 데이터 자동 이전용).</summary>
    public const string LegacyAppName = "LabelSuite";
    public const string SettingsFile = "settings.json";
    public const string StandardsFile = "standards.json";
    public const string ColumnMapsFile = "column_maps.json";
    private static readonly string[] ConfigFiles = [SettingsFile, StandardsFile, ColumnMapsFile];

    public string Directory { get; }
    public JsonObject Settings { get; private set; } = new();
    public JsonObject StandardsRaw { get; private set; } = new();
    public JsonObject ColumnMapsRaw { get; private set; } = new();

    public static string DefaultConfigDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string DataDir() => DefaultConfigDir();

    /// <summary>settings.json 스키마 버전. 구조가 바뀌면 올리고 Migrate()에 단계 이전을 추가한다.
    /// v1: 초기 / v2: 스키마 버전 기록·원자적 저장 도입 (구조 변경 없음).</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>설정 파일이 이 프로그램보다 새 버전(schema_version이 더 큼)이면 true —
    /// 상위 버전에서 만든 프리셋을 가져온 경우로, UI가 안내한다.</summary>
    public bool SettingsFromNewerVersion { get; private set; }

    public AppConfig(string? directory = null)
    {
        if (directory is null) MigrateLegacyDataDir();
        Directory = directory ?? DefaultConfigDir();
        Reload();
    }

    /// <summary>디스크의 설정 3파일을 다시 읽는다 (프리셋 가져오기 뒤 등).
    /// 읽은 뒤 Migrate(누락 키 보충) → Normalize(값 검증·보정)를 순서대로 수행한다.</summary>
    public void Reload()
    {
        EnsureDefaults();
        Settings = Read(SettingsFile);
        StandardsRaw = Read(StandardsFile);
        ColumnMapsRaw = Read(ColumnMapsFile);
        Migrate();
        Corrections.Clear();
        if (!SettingsFromNewerVersion) Normalize();
    }

    /// <summary>번들 기본 설정 파일의 원문 (settings.json / standards.json / column_maps.json).</summary>
    public static string EmbeddedDefault(string name) => ReadEmbedded(name);

    /// <summary>LabelSuite → LaVIS 개명: 기존 %APPDATA%\LabelSuite 데이터
    /// (설정·학습 패턴·교정 사전·검사 이력·캐시)를 새 폴더로 통째로 이전한다.</summary>
    private static void MigrateLegacyDataDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var newDir = Path.Combine(appData, AppName);
        var oldDir = Path.Combine(appData, LegacyAppName);
        if (System.IO.Directory.Exists(newDir) || !System.IO.Directory.Exists(oldDir))
            return;
        try { System.IO.Directory.Move(oldDir, newDir); }
        catch (IOException) { /* 이전 실패 시 새 폴더에서 새로 시작 */ }
        catch (UnauthorizedAccessException) { }
    }

    private void EnsureDefaults()
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var name in ConfigFiles)
        {
            var target = Path.Combine(Directory, name);
            if (!File.Exists(target)) File.WriteAllText(target, ReadEmbedded(name));
        }
    }

    private static string ReadEmbedded(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>설정 파일 읽기. 손상(JSON 파싱 실패)이면 파일을 .corrupt-시각 으로
    /// 보관하고 기본값으로 복구한다 — 손상된 설정 하나로 프로그램이 매번 시작
    /// 실패하는 상황을 막는다.</summary>
    private JsonObject Read(string name)
    {
        var path = Path.Combine(Directory, name);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed) return parsed;
        }
        catch (JsonException) { }
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Move(path, backup, overwrite: true); }
        catch (IOException) { }
        File.WriteAllText(path, ReadEmbedded(name));
        RecoveredFiles.Add(name);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    /// <summary>이번 로드에서 손상 복구된 설정 파일 이름 (UI 안내용).</summary>
    public List<string> RecoveredFiles { get; } = [];

    private void Migrate()
    {
        // 스키마 버전 단계 이전 (구조가 바뀐 버전만 case 추가)
        var version = Settings["schema_version"] is { } v
                      && v.AsValue().TryGetValue<int>(out var parsed) ? parsed : 1;
        var changed = false;
        SettingsFromNewerVersion = version > CurrentSchemaVersion;
        while (version < CurrentSchemaVersion)
        {
            switch (version)
            {
                case 1:
                    // v1 → v2: 구조 변경 없음 (누락 키 보충은 아래 공통 단계가 처리)
                    break;
            }
            version++;
            Settings["schema_version"] = version;
            changed = true;
        }

        // 번들 기본값에 새 키가 추가됐을 때 사용자 settings.json 보충
        var defaults = JsonNode.Parse(ReadEmbedded(SettingsFile))!.AsObject();
        foreach (var (key, value) in defaults)
        {
            if (!Settings.ContainsKey(key))
            {
                Settings[key] = value?.DeepClone();
                changed = true;
            }
            // 섹션 안에 새 하위 키가 추가된 경우 (예: fields.charsets) 보충
            else if (value is JsonObject defaultSection
                     && Settings[key] is JsonObject userSection)
                foreach (var (subKey, subValue) in defaultSection)
                    if (!userSection.ContainsKey(subKey))
                    {
                        userSection[subKey] = subValue?.DeepClone();
                        changed = true;
                    }
        }
        if (changed) SaveSettings();

        // 기존 사용자 standards.json에 새 규격 속성(display_name 등) 보충
        var standardsDefaults = JsonNode.Parse(ReadEmbedded(StandardsFile))!.AsObject();
        var standardsChanged = false;
        if (standardsDefaults["standards"] is JsonObject defaultSpecs
            && StandardsRaw["standards"] is JsonObject userSpecs)
        {
            foreach (var (name, defaultSpec) in defaultSpecs)
            {
                if (userSpecs[name] is not JsonObject userSpec) continue;
                foreach (var (prop, value) in defaultSpec!.AsObject())
                {
                    if (prop == "counts") continue;   // 사용자 편집값은 유지
                    if (!userSpec.ContainsKey(prop))
                    {
                        userSpec[prop] = value?.DeepClone();
                        standardsChanged = true;
                    }
                }
            }
        }
        if (standardsChanged) SaveStandards();
    }

    // ---------------- 값 검증·보정 (SettingRanges 범위표 기준) ----------------

    /// <summary>이번 로드(또는 마지막 Normalize 호출)에서 보정한 항목 — '경로: 이전 → 이후' 형식.
    /// UI는 개수가 0보다 크면 상태바 경고와 app.log로 알린다.</summary>
    public List<string> Corrections { get; } = [];

    /// <summary>설정 값을 허용 범위로 보정한다: 범위 밖 숫자 clamp·타입 오류는 기본값,
    /// ocr.engine 소문자·허용값, prefetch_policy, save_directory 경로 규칙, 필드 배열 항목 검증
    /// (region이 0~100 숫자 4개가 아닌 영역은 0으로 대체하지 않고 제거). 보정이 있으면 저장하고
    /// Corrections에 기록한다. 반환값은 이번 호출의 보정 건수.</summary>
    public int Normalize()
    {
        var before = Corrections.Count;

        foreach (var def in SettingRanges.All)
        {
            if (def.Path.Contains("[]")) continue;   // 배열 항목은 아래 개별 규칙
            var (parent, key) = Locate(def.Path);
            if (parent is null || !parent.ContainsKey(key)) continue;   // 누락 보충은 Migrate 몫
            if (SettingRanges.TryNormalize(def, parent[key], out var fixedNode, out var note))
            {
                parent[key] = fixedNode;
                Corrections.Add($"{def.Path}: {note}");
            }
        }

        NormalizeEngine();
        NormalizePrefetchPolicy();
        NormalizeSaveDirectory();
        var fields = Section("fields");
        NormalizeRegionArray(fields, "zones", "fields.zones");
        NormalizeCustomFields(fields);
        NormalizeCharsets(fields);
        NormalizeSameValue(fields);
        NormalizeDisabled(fields);
        NormalizeRegionArray(Section("label_forms"), "rules", "label_forms.rules");

        var count = Corrections.Count - before;
        if (count > 0) SaveSettings();
        return count;
    }

    private (JsonObject? parent, string key) Locate(string path)
    {
        var dot = path.IndexOf('.');
        if (dot < 0) return (Settings, path);
        return (Settings[path[..dot]] as JsonObject, path[(dot + 1)..]);
    }

    private void NormalizeEngine()
    {
        var ocr = Section("ocr");
        if (!ocr.ContainsKey("engine")) return;
        var raw = ocr["engine"]?.GetValueKind() == JsonValueKind.String
            ? ocr["engine"]!.GetValue<string>() : null;
        var lowered = raw?.Trim().ToLowerInvariant();
        var fixedName = lowered is not null && SettingRanges.EngineNames.Contains(lowered)
            ? lowered : SettingRanges.DefaultEngine;
        if (fixedName == raw) return;
        Corrections.Add($"ocr.engine: {SettingRanges.Describe(ocr["engine"])} → \"{fixedName}\"");
        ocr["engine"] = fixedName;
    }

    private void NormalizePrefetchPolicy()
    {
        if (!Settings.ContainsKey("prefetch_policy")) return;
        var node = Settings["prefetch_policy"];
        if (SettingRanges.IsValidPrefetchPolicy(node)) return;
        Corrections.Add($"prefetch_policy: {SettingRanges.Describe(node)} → \"all\"");
        Settings["prefetch_policy"] = "all";
    }

    private void NormalizeSaveDirectory()
    {
        if (!Settings.ContainsKey("save_directory")) return;
        var node = Settings["save_directory"];
        if (node is null) { Settings["save_directory"] = ""; return; }
        if (node.GetValueKind() == JsonValueKind.String)
        {
            var value = node.GetValue<string>();
            if (value.Length == 0 || PathRules.IsValidDirectory(value)) return;
            Corrections.Add($"save_directory: \"{value}\" → \"\" (경로 규칙 위반, 기본 폴더 사용)");
        }
        else Corrections.Add($"save_directory: {node.ToJsonString()} → \"\" (문자열이 아님)");
        Settings["save_directory"] = "";
    }

    /// <summary>region이 0~100 숫자 4개가 아닌 항목은 제거한다 (0으로 대체하면 좌상단 1%
    /// 영역이 되어 해당 필드가 영구 미검출되는 문제 방지).</summary>
    private void NormalizeRegionArray(JsonObject section, string key, string path)
    {
        if (!section.ContainsKey(key)) return;
        if (section[key] is not JsonArray array)
        {
            Corrections.Add($"{path}: {SettingRanges.Describe(section[key])} → [] (배열이 아님)");
            section[key] = new JsonArray();
            return;
        }
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is JsonObject obj && FieldZoneStore.TryRegion(obj, out _)) continue;
            Corrections.Add($"{path}[{i}]: {SettingRanges.Describe(array[i])} → 제거 (region은 0~100 숫자 4개)");
            array.RemoveAt(i);
        }
    }

    private static string? NameOf(JsonNode? node, string prop) =>
        node is JsonObject obj && obj[prop]?.GetValueKind() == JsonValueKind.String
            ? obj[prop]!.GetValue<string>().Trim() : null;

    private void NormalizeCustomFields(JsonObject fields)
    {
        if (fields["custom"] is not JsonArray array) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var drop = new List<int>();
        for (var i = 0; i < array.Count; i++)
        {
            var name = NameOf(array[i], "name");
            if (string.IsNullOrEmpty(name))
            {
                Corrections.Add($"fields.custom[{i}]: {SettingRanges.Describe(array[i])} → 제거 (name 비어 있음)");
                drop.Add(i);
            }
            else if (!seen.Add(name))
            {
                Corrections.Add($"fields.custom[{i}]: \"{name}\" → 제거 (중복 name, 첫 항목 유지)");
                drop.Add(i);
            }
        }
        for (var k = drop.Count - 1; k >= 0; k--) array.RemoveAt(drop[k]);
    }

    private void NormalizeCharsets(JsonObject fields)
    {
        if (fields["charsets"] is not JsonArray array) return;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(NameOf(array[i], "field"))) continue;
            Corrections.Add($"fields.charsets[{i}]: {SettingRanges.Describe(array[i])} → 제거 (field 비어 있음)");
            array.RemoveAt(i);
        }
    }

    private void NormalizeSameValue(JsonObject fields)
    {
        if (fields["same_value"] is not JsonArray array) return;
        var def = SettingRanges.Find("fields.same_value[].min_instances")!;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject obj || !obj.ContainsKey("min_instances")) continue;
            if (SettingRanges.TryNormalize(def, obj["min_instances"], out var fixedNode, out var note))
            {
                obj["min_instances"] = fixedNode;
                Corrections.Add($"fields.same_value[{i}].min_instances: {note}");
            }
        }
    }

    /// <summary>fields.disabled — 비활성 가능한 필드만 남긴다 (LOT은 매칭 기준이라 제외 불가).</summary>
    private void NormalizeDisabled(JsonObject fields)
    {
        if (fields["disabled"] is not JsonArray array) return;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            var name = array[i]?.GetValueKind() == JsonValueKind.String
                ? array[i]!.GetValue<string>() : null;
            if (name is not null && SettingRanges.DisableableFields.Contains(name)) continue;
            Corrections.Add($"fields.disabled[{i}]: {SettingRanges.Describe(array[i])} → 제거 (비활성 불가 필드)");
            array.RemoveAt(i);
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>원자적 저장: 임시 파일에 완전히 쓴 뒤 교체한다 — 저장 도중 정전·강제 종료로
    /// 설정 파일이 반쯤 쓰인 채 남는(=다음 시작 때 손상 복구로 초기화되는) 일을 막는다.</summary>
    private void Write(string name, JsonObject data) =>
        WriteAtomic(Path.Combine(Directory, name), data.ToJsonString(WriteOptions));

    public static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public void SaveSettings() => Write(SettingsFile, Settings);
    public void SaveStandards() => Write(StandardsFile, StandardsRaw);

    // ---- 편의 접근자 ----
    public string GetString(string key, string fallback = "") =>
        Settings[key]?.GetValue<string>() ?? fallback;

    public int GetInt(string key, int fallback) =>
        Settings[key] is { } node && node.AsValue().TryGetValue<int>(out var v) ? v : fallback;

    public double GetDouble(string key, double fallback) =>
        Settings[key] is { } node && node.AsValue().TryGetValue<double>(out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback) =>
        Settings[key] is { } node && node.AsValue().TryGetValue<bool>(out var v) ? v : fallback;

    /// <summary>중첩 설정 섹션 접근 (없으면 생성).</summary>
    public JsonObject Section(string name)
    {
        if (Settings[name] is not JsonObject section)
        {
            section = new JsonObject();
            Settings[name] = section;
        }
        return section;
    }

    public int SectionInt(string section, string key, int fallback) =>
        Section(section)[key] is { } node && node.AsValue().TryGetValue<int>(out var v)
            ? v : fallback;

    public bool SectionBool(string section, string key, bool fallback) =>
        Section(section)[key] is { } node && node.AsValue().TryGetValue<bool>(out var v)
            ? v : fallback;

    public Dictionary<string, string> CountryStandardMap()
    {
        var result = new Dictionary<string, string>();
        if (Settings["country_standard_map"] is JsonObject map)
            foreach (var (k, v) in map) result[k] = v?.GetValue<string>() ?? "";
        return result;
    }
}
