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
}
