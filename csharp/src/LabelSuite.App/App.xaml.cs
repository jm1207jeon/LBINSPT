using System.IO;
using System.Windows;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class App : Application
{
    private static string CrashLogPath =>
        Path.Combine(AppConfig.DataDir(), "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // UI 스레드 예외: 안내 후 계속 실행
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("UI", args.Exception);
            MessageBox.Show(
                $"예기치 않은 오류가 발생했습니다:\n{args.Exception.Message}\n\n" +
                $"상세 내용은 {CrashLogPath} 에 기록되었습니다.",
                "LaVIS 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // 백그라운드 Task 예외(관찰되지 않은): 프로세스 종료 대신 기록
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };
        // 그 외 스레드의 치명적 예외: 종료 전 원인 기록 ("프로그램이 그냥 꺼짐" 진단용)
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("Fatal", args.ExceptionObject as Exception);
    }

    private static void LogCrash(string kind, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {exception}\n\n");
        }
        catch (Exception) { /* 로그 실패는 무시 */ }
    }
}
