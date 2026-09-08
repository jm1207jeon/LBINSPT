using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class MainWindow : Window
{
    public AppConfig Config { get; }
    public HistoryDb History { get; }
    public OcrCorrections Corrections { get; }
    public WordMergeRules Merges { get; }
    public GlyphLibrary Glyphs { get; }

    /// <summary>경고/오류 메시지가 다음 정보 메시지에 바로 덮이지 않도록 고정하는 시간.</summary>
    private static readonly TimeSpan AlertHold = TimeSpan.FromSeconds(5);
    private DateTime _alertUntil = DateTime.MinValue;
    private string? _pendingInfo;
    private readonly DispatcherTimer _alertTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow()
    {
        InitializeComponent();
        Config = new AppConfig();
        History = OpenHistoryWithRecovery(
            Path.Combine(AppConfig.DataDir(), "history.sqlite3"));
        Corrections = new OcrCorrections(
            Path.Combine(AppConfig.DataDir(), "ocr_corrections.json"));
        Glyphs = new GlyphLibrary(Path.Combine(AppConfig.DataDir(), "glyphs.json"));
        Merges = new WordMergeRules(Path.Combine(AppConfig.DataDir(), "word_merges.json"));

        Generator.Initialize(Config);
        Inspector.Initialize(Config, History, Corrections, Glyphs, Merges);
        HistoryPage.Initialize(Config, History);

        Generator.StatusMessage += ShowStatus;
        Inspector.StatusMessage += ShowStatus;
        Inspector.AwsStatusChanged += OnAwsStatus;
        Inspector.ProgressChanged += OnProgress;
        Generator.ListGenerated += records =>
        {
            Inspector.LoadRecords(records);
            Tabs.SelectedIndex = 1;
            ShowStatus($"검사 목록 {records.Count}건을 검사 탭으로 전달했습니다.", StatusLevel.Info);
        };
        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedIndex == 2) HistoryPage.Refresh();
        };
        // 검사 탭 전역 단축키: ←/→ 페이지, WASD 이동, Q/E 줌 (텍스트 입력 중 제외)
        PreviewKeyDown += (_, e) =>
        {
            if (Tabs.SelectedIndex == 1) Inspector.HandleGlobalKey(e);
        };
        _alertTimer.Tick += (_, _) =>
        {
            if (DateTime.Now < _alertUntil) return;
            _alertTimer.Stop();
            if (_pendingInfo is { } pending) { _pendingInfo = null; ApplyStatus(pending, StatusLevel.Info); }
        };
        // 미저장 결과가 있으면 종료 전 확인 (기본 버튼 '아니오' — Enter 오조작 방지)
        Closing += (_, e) =>
        {
            var unsaved = Inspector.UnsavedCount;
            if (unsaved == 0) return;
            var answer = MessageBox.Show(this,
                $"판정된 페이지 중 {unsaved}건의 결과가 아직 저장되지 않았습니다.\n" +
                "([결과 저장] 또는 [자동 저장]으로 결과 이미지·이력이 저장됩니다)\n\n" +
                "그래도 종료하시겠습니까?",
                "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            Inspector.Shutdown();
            History.Dispose();
        };
        if (Config.SettingsFromNewerVersion)
            Loaded += (_, _) => ShowStatus(
                "설정 파일이 이 프로그램보다 새 버전에서 만들어졌습니다 — 일부 항목이 무시될 수 있습니다. LaVIS를 업데이트하세요.",
                StatusLevel.Warn);
        if (Config.RecoveredFiles.Count > 0)
            Loaded += (_, _) => MessageBox.Show(
                $"손상된 설정 파일을 기본값으로 복구했습니다: " +
                $"{string.Join(", ", Config.RecoveredFiles)}\n" +
                "이전 파일은 같은 폴더에 .corrupt-시각 이름으로 보관되어 있습니다.",
                "설정 복구", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>이력 DB가 손상돼 열리지 않으면 보관 후 새로 만든다 — DB 하나 때문에
    /// 프로그램이 시작조차 못 하는 상황 방지.</summary>
    private static HistoryDb OpenHistoryWithRecovery(string path)
    {
        try { return new HistoryDb(path); }
        catch (Exception first)
        {
            try
            {
                if (File.Exists(path))
                    File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}",
                              overwrite: true);
                var db = new HistoryDb(path);
                MessageBox.Show(
                    $"검사 이력 DB를 열 수 없어 새로 만들었습니다.\n원인: {first.Message}\n" +
                    "이전 DB는 .corrupt-시각 이름으로 보관되어 있습니다.",
                    "이력 DB 복구", MessageBoxButton.OK, MessageBoxImage.Warning);
                return db;
            }
            catch (Exception) { throw first; }
        }
    }

    // ---------------- 상태바: 3단계 메시지 (UDInspect 규범) ----------------

    private void ShowStatus(string message, StatusLevel level) =>
        Dispatcher.Invoke(() =>
        {
            if (level == StatusLevel.Info && DateTime.Now < _alertUntil)
            {
                // 경고/오류 고정 중 — 정보 메시지는 고정이 풀린 뒤 표시
                _pendingInfo = message;
                if (!_alertTimer.IsEnabled) _alertTimer.Start();
                return;
            }
            ApplyStatus(message, level);
        });

    private void ApplyStatus(string message, StatusLevel level)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (level != StatusLevel.Info) AppLog.Warn(message);
        StatusText.Foreground = level switch
        {
            StatusLevel.Error => (Brush)FindResource("StatusErrorBrush"),
            StatusLevel.Warn => (Brush)FindResource("StatusWarnBrush"),
            _ => (Brush)FindResource("TextBrush"),
        };
        StatusText.FontWeight = level == StatusLevel.Info ? FontWeights.Normal : FontWeights.Bold;
        if (level != StatusLevel.Info)
        {
            _alertUntil = DateTime.Now + AlertHold;
            _pendingInfo = null;
        }
    }

    private void OnProgress(int done, int total) => Dispatcher.Invoke(() =>
    {
        ProgressBarMain.Maximum = Math.Max(1, total);
        ProgressBarMain.Value = Math.Min(done, total);
        BufferText.Text = total == 0 ? "대기"
            : done >= total ? $"OCR 완료 {done}/{total}" : $"OCR {done}/{total}";
    });

    private void OnAwsStatus(bool ok, string text) => Dispatcher.Invoke(() =>
    {
        AwsStatusText.Text = ok ? (text.StartsWith("OCR:") ? text : "OCR: AWS 인증됨")
                                : "OCR: AWS 인증 실패";
        AwsStatusText.ToolTip = text;
        AwsStatusText.Foreground = ok
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("StatusErrorBrush");
    });

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(Config, Corrections, Glyphs, Merges)
        { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Inspector.ApplyConfig();
            Generator.ApplyConfig();
            ShowStatus("설정이 저장되었습니다 — 즉시 적용되고 다음 실행에도 유지됩니다.", StatusLevel.Info);
        }
    }
}
