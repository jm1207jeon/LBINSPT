using System.IO;
using System.Windows;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class MainWindow : Window
{
    public AppConfig Config { get; }
    public HistoryDb History { get; }

    public MainWindow()
    {
        InitializeComponent();
        Config = new AppConfig();
        History = new HistoryDb(Path.Combine(AppConfig.DataDir(), "history.sqlite3"));

        Generator.Initialize(Config);
        Inspector.Initialize(Config, History);
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
        Closed += (_, _) =>
        {
            Inspector.Shutdown();
            History.Dispose();
        };
    }

    private void ShowStatus(string message) =>
        Dispatcher.Invoke(() => StatusText.Text = message);

    private void OnAwsStatus(bool ok, string text) => Dispatcher.Invoke(() =>
    {
        AwsStatusText.Text = text;
        AwsStatusText.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
    });

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(Config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Inspector.ApplyConfig();
            Generator.ApplyConfig();
            ShowStatus("설정이 저장되었습니다.");
        }
    }
}
