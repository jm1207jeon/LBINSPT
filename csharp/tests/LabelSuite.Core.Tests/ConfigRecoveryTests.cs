// 설정 파일 손상 시 자동 복구 — 손상 하나로 프로그램이 시작 실패하지 않아야 한다.
using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class ConfigRecoveryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void CorruptSettingsAreBackedUpAndRestoredToDefaults()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, AppConfig.SettingsFile);
        File.WriteAllText(settingsPath, "{ this is not json");

        var config = new AppConfig(_directory);   // 예외 없이 로드돼야 한다

        Assert.Contains(AppConfig.SettingsFile, config.RecoveredFiles);
        Assert.Equal(36, config.GetInt("shelf_life_months", -1));   // 기본값 복구
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        // 다른 파일은 정상이므로 복구 대상 아님
        Assert.DoesNotContain(AppConfig.StandardsFile, config.RecoveredFiles);
    }

    [Fact]
    public void IntactSettingsAreNotTouched()
    {
        var config = new AppConfig(_directory);
        Assert.Empty(config.RecoveredFiles);
        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
    }

    [Fact]
    public void MigrateAddsOverlayShowZonesAndZonesKeepMode()
    {
        // 구버전 settings.json: overlay 섹션은 있지만 show_zones 없음, zones/export 섹션 없음
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, AppConfig.SettingsFile), """
            { "schema_version": 1, "shelf_life_months": 12,
              "overlay": { "thickness": 3, "fill_alpha": 90, "show_numbers": true } }
            """);
        var config = new AppConfig(_directory);
        Assert.True(config.SectionBool("overlay", "show_zones", false));
        Assert.True(config.Section("overlay").ContainsKey("show_zones"));
        Assert.True(config.Section("zones").ContainsKey("keep_mode"));
        Assert.False(config.SectionBool("zones", "keep_mode", true));
        Assert.True(config.Section("export").ContainsKey("csv_text_protect"));
        Assert.True(config.SectionBool("export", "csv_text_protect", false));
        // 사용자 값은 유지
        Assert.Equal(3, config.SectionInt("overlay", "thickness", -1));
        Assert.True(config.SectionBool("overlay", "show_numbers", false));
        Assert.Equal(12, config.GetInt("shelf_life_months", -1));
        Assert.Empty(config.Corrections);
        // 디스크에도 반영
        var reloaded = new AppConfig(_directory);
        Assert.True(reloaded.SectionBool("overlay", "show_zones", false));
        Assert.True(reloaded.Section("zones").ContainsKey("keep_mode"));
    }

    [Fact]
    public void PresetImportedSettingsAreNormalizedOnNextLoad()
    {
        // 프리셋 가져오기 뒤 재시작 = 새 AppConfig 생성 → 생성자의 Normalize가 보정한다
        Directory.CreateDirectory(_directory);
        var config = new AppConfig(_directory);
        config.Settings["pdf_render_zoom"] = 99;   // 범위 밖 값이 파일에 그대로 쓰였다고 가정
        config.SaveSettings();
        var next = new AppConfig(_directory);
        Assert.Equal(8.0, next.GetDouble("pdf_render_zoom", -1));
        Assert.Single(next.Corrections);
        // Reload()도 같은 경로
        config.Settings["pdf_render_zoom"] = 0.1;
        config.SaveSettings();
        config.Reload();
        Assert.Equal(1.0, config.GetDouble("pdf_render_zoom", -1));
        Assert.Single(config.Corrections);
    }
}
