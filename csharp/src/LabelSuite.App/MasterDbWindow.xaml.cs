using System.Collections.ObjectModel;
using System.Windows;
using LabelSuite.Core;

namespace LabelSuite.App;

public partial class MasterDbWindow : Window
{
    public sealed class RowVm
    {
        public string Pn { get; set; } = "";
        public string Products { get; set; } = "";
        public string Ref { get; set; } = "";
        public string Gtin { get; set; } = "";
        public string Symbologies { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private readonly HistoryDb _db;
    private readonly ObservableCollection<RowVm> _rows;
    private readonly HashSet<string> _originalPns;

    public MasterDbWindow(HistoryDb db)
    {
        InitializeComponent();
        _db = db;
        _rows = new ObservableCollection<RowVm>(_db.AllMaster().Select(m => new RowVm
        {
            Pn = m.Pn, Products = m.Products, Ref = m.Ref, Gtin = m.Gtin,
            Symbologies = m.ExpectedSymbologies, Note = m.Note,
        }));
        _originalPns = _rows.Select(r => r.Pn).ToHashSet();
        Grid.ItemsSource = _rows;
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        var selected = Grid.SelectedItems.OfType<RowVm>().ToList();
        foreach (var row in selected) _rows.Remove(row);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit();
        var kept = new HashSet<string>();
        foreach (var row in _rows.Where(r => r.Pn.Trim().Length > 0))
        {
            _db.SaveMasterRow(new MasterRow(row.Pn.Trim(), row.Products.Trim(),
                row.Ref.Trim(), row.Gtin.Trim(), row.Symbologies.Trim(),
                row.Note.Trim()));
            kept.Add(row.Pn.Trim());
        }
        foreach (var pn in _originalPns.Except(kept)) _db.DeleteMaster(pn);
        SaveNote.Text = $"기준정보 {kept.Count}건 저장됨 {DateTime.Now:HH:mm:ss}";
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}
