using System.IO;
using System.Windows;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class MainWindow : Window
{
    public AppConfig Config { get; }
    public HistoryDb History { get; }
    public OcrCorrections Corrections { get; }
    public WordMergeRules Merges { get; }
    public GlyphLibrary Glyphs { get; }

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
        Generator.ListGenerated += records =>
        {
            Inspector.LoadRecords(records);
            Tabs.SelectedIndex = 1;
            ShowStatus($"검사 목록 {records.Count}건을 검사 탭으로 전달했습니다.");
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
        Closed += (_, _) =>
        {
            Inspector.Shutdown();
            History.Dispose();
        };
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

    private void ShowStatus(string message) =>
        Dispatcher.Invoke(() => StatusText.Text = message);

    private void OnAwsStatus(bool ok, string text) => Dispatcher.Invoke(() =>
    {
        AwsStatusText.Text = ok ? "AWS: 인증됨" : "AWS: 인증 실패";
        AwsStatusText.ToolTip = text;
        AwsStatusText.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
    });

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(Config, Corrections, Glyphs, Merges)
        { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Inspector.ApplyConfig();
            Generator.ApplyConfig();
            ShowStatus("설정이 저장되었습니다.");
        }
    }
}
