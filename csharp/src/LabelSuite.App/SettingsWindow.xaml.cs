// 환경설정 — 일반/AWS/검사 기준. 컬럼 매핑·중국 매핑 등 고급 설정은
// '설정 폴더 열기'로 JSON을 직접 편집한다 (README 참고).
using System.Data;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using LabelSuite.Core;
using Microsoft.Win32;

namespace LabelSuite.App;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private static readonly string[] CountFields =
        ["LOT", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN", "CHINA"];

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadValues();
    }

    private void LoadValues()
    {
        SaveDirBox.Text = _config.GetString("save_directory");
        ShelfLifeBox.Text = _config.GetInt("shelf_life_months", 36).ToString();
        RenderZoomBox.Text = _config.GetDouble("pdf_render_zoom", 4.0).ToString("F1");
        var aws = _config.Settings["aws"]?.AsObject();
        AwsRegionBox.Text = aws?["region"]?.GetValue<string>() ?? "ap-northeast-2";
        AwsProfileBox.Text = aws?["profile"]?.GetValue<string>() ?? "";

        var policy = _config.Settings["prefetch_policy"];
        PrefetchCombo.SelectedIndex = policy?.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => policy.GetValue<int>() switch
            { 1 => 1, 2 => 2, 5 => 3, 0 => 4, _ => 0 },
            _ => 0,
        };

        var table = new DataTable();
        table.Columns.Add("규격");
        foreach (var field in CountFields) table.Columns.Add(field, typeof(int));
        if (_config.StandardsRaw["standards"] is JsonObject standards)
            foreach (var (name, node) in standards)
            {
                var row = table.NewRow();
                row["규격"] = name;
                var counts = node?["counts"]?.AsObject();
                foreach (var field in CountFields)
                    row[field] = counts?[field]?.GetValue<int>() ?? 0;
                table.Rows.Add(row);
            }
        CountsGrid.ItemsSource = table.DefaultView;
    }

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "결과 저장 폴더" };
        if (dialog.ShowDialog() == true) SaveDirBox.Text = dialog.FolderName;
    }

    private async void OnCheckAws(object sender, RoutedEventArgs e)
    {
        AwsCheckLabel.Text = "확인 중…";
        var client = new TextractClient(
            AwsRegionBox.Text.Trim(),
            AwsProfileBox.Text.Trim().Length > 0 ? AwsProfileBox.Text.Trim() : null);
        var status = await client.ValidateCredentialsAsync();
        AwsCheckLabel.Text = status.Ok ? $"확인됨: {status.IdentityArn}" : $"실패: {status.Error}";
        AwsCheckLabel.Foreground = status.Ok
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_config.Directory) { UseShellExecute = true });

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _config.Settings["save_directory"] = SaveDirBox.Text.Trim();
        if (int.TryParse(ShelfLifeBox.Text, out var shelfLife) && shelfLife > 0)
            _config.Settings["shelf_life_months"] = shelfLife;
        if (double.TryParse(RenderZoomBox.Text, out var zoom) && zoom is >= 1 and <= 8)
            _config.Settings["pdf_render_zoom"] = zoom;
        _config.Settings["prefetch_policy"] = PrefetchCombo.SelectedIndex switch
        {
            1 => JsonValue.Create(1), 2 => JsonValue.Create(2),
            3 => JsonValue.Create(5), 4 => JsonValue.Create(0),
            _ => JsonValue.Create("all"),
        };
        _config.Settings["aws"] = new JsonObject
        {
            ["region"] = AwsRegionBox.Text.Trim().Length > 0
                ? AwsRegionBox.Text.Trim() : "ap-northeast-2",
            ["profile"] = AwsProfileBox.Text.Trim(),
        };
        _config.SaveSettings();

        if (CountsGrid.ItemsSource is DataView view
            && _config.StandardsRaw["standards"] is JsonObject standards)
        {
            foreach (DataRowView rowView in view)
            {
                var name = rowView["규격"]?.ToString() ?? "";
                if (standards[name] is not JsonObject spec) continue;
                var counts = spec["counts"]?.AsObject() ?? new JsonObject();
                foreach (var field in CountFields)
                    counts[field] = rowView[field] is int value ? value
                        : int.TryParse(rowView[field]?.ToString(), out var parsed) ? parsed : 0;
                spec["counts"] = counts;
            }
            _config.SaveStandards();
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
