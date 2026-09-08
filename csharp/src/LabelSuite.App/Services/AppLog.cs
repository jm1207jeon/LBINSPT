using System.IO;
using LabelSuite.Core;

namespace LabelSuite.App;

/// <summary>운영 로그 (%APPDATA%\LaVIS\app.log) — UDInspect와 동일 규범.
/// 상태바의 경고/오류는 다음 메시지에 덮이므로, 여기 남겨 사후 진단이 가능하게 한다.
/// 2 MB를 넘으면 app.log.1로 교체(1세대 보관). 로그 실패는 절대 예외를 내지 않는다.</summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppConfig.DataDir(), "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* 로그 실패는 무시 */ }
    }
}
