// 앱 설정 관리 — 임베디드 기본값을 %APPDATA%/LabelSuite로 복사 후 로드/저장.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSuite.Core;

public class AppConfig
{
    public const string AppName = "LabelSuite";
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

    public AppConfig(string? directory = null)
    {
        Directory = directory ?? DefaultConfigDir();
        EnsureDefaults();
        Settings = Read(SettingsFile);
        StandardsRaw = Read(StandardsFile);
        ColumnMapsRaw = Read(ColumnMapsFile);
        Migrate();
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

    private JsonObject Read(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, name)))!.AsObject();

    private void Migrate()
    {
        // 번들 기본값에 새 키가 추가됐을 때 사용자 settings.json 보충
        var defaults = JsonNode.Parse(ReadEmbedded(SettingsFile))!.AsObject();
        var changed = false;
        foreach (var (key, value) in defaults)
        {
            if (!Settings.ContainsKey(key))
            {
                Settings[key] = value?.DeepClone();
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

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Write(string name, JsonObject data) =>
        File.WriteAllText(Path.Combine(Directory, name), data.ToJsonString(WriteOptions));

    public void SaveSettings() => Write(SettingsFile, Settings);
    public void SaveStandards() => Write(StandardsFile, StandardsRaw);
    public void SaveColumnMaps() => Write(ColumnMapsFile, ColumnMapsRaw);

    public void RestoreDefaults(string name)
    {
        File.WriteAllText(Path.Combine(Directory, name), ReadEmbedded(name));
        if (name == SettingsFile) Settings = Read(name);
        else if (name == StandardsFile) StandardsRaw = Read(name);
        else if (name == ColumnMapsFile) ColumnMapsRaw = Read(name);
    }

    // ---- 편의 접근자 ----
    public string GetString(string key, string fallback = "") =>
        Settings[key]?.GetValue<string>() ?? fallback;

    public int GetInt(string key, int fallback) =>
        Settings[key] is { } node && node.AsValue().TryGetValue<int>(out var v) ? v : fallback;

    public double GetDouble(string key, double fallback) =>
        Settings[key] is { } node && node.AsValue().TryGetValue<double>(out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback) =>
        Settings[key] is { } node && node.AsValue().TryGetValue<bool>(out var v) ? v : fallback;

    public Dictionary<string, string> CountryStandardMap()
    {
        var result = new Dictionary<string, string>();
        if (Settings["country_standard_map"] is JsonObject map)
            foreach (var (k, v) in map) result[k] = v?.GetValue<string>() ?? "";
        return result;
    }
}
