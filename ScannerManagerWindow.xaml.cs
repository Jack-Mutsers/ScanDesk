using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace ScanDesk;

public partial class ScannerManagerWindow : Window
{
    readonly List<ManagedScanner> _rows;

    public ScannerManagerWindow(IReadOnlyList<ScannerInfo> scanners, string? defaultId, IReadOnlyCollection<string> hiddenIds)
    {
        InitializeComponent();
        _rows = scanners.Select(s => new ManagedScanner(s)
        {
            IsHidden = hiddenIds.Contains(s.DeviceId),
            IsDefault = s.DeviceId == defaultId && !hiddenIds.Contains(s.DeviceId),
        }).ToList();
        ScannerList.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public string? DefaultId => _rows.FirstOrDefault(r => r.IsDefault)?.Info.DeviceId;

    /// <summary>Hidden scanners among the ones shown here; the caller keeps hidden ids of scanners that are currently offline.</summary>
    public IEnumerable<string> HiddenIds => _rows.Where(r => r.IsHidden).Select(r => r.Info.DeviceId);
    public IEnumerable<string> ShownIds => _rows.Where(r => !r.IsHidden).Select(r => r.Info.DeviceId);

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ManagedScanner row) return;
        row.IsHidden = !row.IsHidden;
        if (row.IsHidden) row.IsDefault = false;
    }

    void Done_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public sealed class ManagedScanner(ScannerInfo info) : INotifyPropertyChanged
{
    bool _isDefault, _isHidden;

    public ScannerInfo Info { get; } = info;
    public string Name => Info.Name;

    /// <summary>Tells duplicate entries of the same printer apart.</summary>
    public string Detail => Info.DeviceId.StartsWith(@"SWD\Escl", StringComparison.OrdinalIgnoreCase)
        ? Loc.T("Manage.DriverWindows")
        : Loc.T("Manage.DriverVendor");

    public bool IsDefault { get => _isDefault; set { if (_isDefault != value) { _isDefault = value; OnChanged(); } } }

    public bool IsHidden
    {
        get => _isHidden;
        set { if (_isHidden != value) { _isHidden = value; OnChanged(); OnChanged(nameof(IsVisible)); } }
    }

    public bool IsVisible => !IsHidden;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
