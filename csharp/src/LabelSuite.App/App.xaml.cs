using System.IO;
using System.Threading;
using System.Windows;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class App : Application
{
    private static string CrashLogPath =>
        Path.Combine(AppConfig.DataDir(), "crash.log");

    // 중복 실행 방지: 두 인스턴스가 학습 데이터(glyphs/교정/병합 JSON)·이력 DB를
    // 동시에 쓰면 파일이 깨질 수 있다 (UDInspect와 동일 규범).
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\LaVIS.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "LaVIS가 이미 실행 중입니다.\n실행 중인 창을 사용하세요 (두 개를 동시에 열면 학습 데이터·이력이 손상될 수 있습니다).",
                "LaVIS", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        AppLog.Info($"LaVIS 시작 (버전 {typeof(App).Assembly.GetName().Version})");
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

    /// <summary>프로그램을 다시 시작한다 (프리셋 적용 등 전체 재로드가 필요할 때).</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            try
            {
                // 뮤텍스는 종료 시 해제되므로 새 프로세스는 잠시 기다렸다 시작
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex) { AppLog.Error("재시작 실패", ex); }
        }
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("LaVIS 종료");
        try { _singleInstance?.ReleaseMutex(); } catch (Exception) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(string kind, Exception? exception)
    {
        AppLog.Error($"{kind} 예외", exception);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {exception}\n\n");
        }
        catch (Exception) { /* 로그 실패는 무시 */ }
    }
}
