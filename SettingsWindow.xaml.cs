using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace ScanDesk;

public enum SettingsPage { General, Scanners, About }

/// <summary>What the settings window needs from the main window. Changes apply immediately.</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }
    IReadOnlyList<ScannerInfo> Scanners { get; }
    void SetLanguage(string code);
    void SetAutoUpdate(bool on);
    Task<string> CheckForUpdatesNowAsync();
    Task RefreshScannersAsync();
    void SetScannerVisibility(string? defaultId, IEnumerable<string> hiddenIds, IEnumerable<string> shownIds);
}

public partial class SettingsWindow : Window
{
    readonly ISettingsHost _host;
    List<ManagedScanner> _rows = [];
    List<DeviceLinks> _printers = [];
    bool _loading = true;

    public SettingsWindow(ISettingsHost host, SettingsPage page)
    {
        _host = host;
        InitializeComponent();

        foreach (var (code, name) in Loc.Languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Loc.Language);
        AutoUpdateBox.IsChecked = host.Settings.AutoCheckUpdates;
        VersionText.Text = VersionLabel();
        LoadScanners();
        _loading = false;
        Loaded += async (_, _) => await LoadPrintersAsync();

        (page switch { SettingsPage.Scanners => NavScanners, SettingsPage.About => NavAbout, _ => NavGeneral }).IsChecked = true;
    }

    static string VersionLabel() => UpdateChecker.VersionLabel();

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        string tag = (string)((FrameworkElement)sender).Tag;
        PageGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageScanners.Visibility = tag == "Scanners" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- General ----------

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || (LanguageBox.SelectedItem as ComboBoxItem)?.Tag is not string code) return;
        _host.SetLanguage(code);
        VersionText.Text = VersionLabel();
        foreach (var row in _rows) row.RefreshText();
        foreach (var row in _printers) row.RefreshText();
    }

    void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.SetAutoUpdate(AutoUpdateBox.IsChecked == true);
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateResult.Text = Loc.T("Status.CheckingUpdates");
        UpdateResult.Text = await _host.CheckForUpdatesNowAsync();
        CheckNowButton.IsEnabled = true;
    }

    // ---------- Scanners ----------

    void LoadScanners()
    {
        var s = _host.Settings;
        _rows = _host.Scanners.Select(sc => new ManagedScanner(sc)
        {
            IsHidden = s.HiddenScannerIds.Contains(sc.DeviceId),
            IsDefault = sc.DeviceId == s.DefaultScannerId && !s.HiddenScannerIds.Contains(sc.DeviceId),
        }).ToList();
        ScannerList.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Printers of a known brand whose scanner isn't found, with links to the manufacturer's software.</summary>
    async Task LoadPrintersAsync()
    {
        var scanners = _host.Scanners.ToList();
        var printers = await Task.Run(() => DeviceCatalog.PrintersWithoutScanner(scanners));
        _printers = printers.Select(p => new DeviceLinks(p.Name, p.Support)).ToList();
        PrinterList.ItemsSource = _printers;
        PrinterSection.Visibility = _printers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SupportLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is DeviceLinks { Support: { } s }) OpenUrl(s.SupportUrl);
    }

    void SearchLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is DeviceLinks { Support: { } s }) OpenUrl(s.SearchUrl);
    }

    void SaveScanners() => _host.SetScannerVisibility(
        _rows.FirstOrDefault(r => r.IsDefault)?.Info.DeviceId,
        _rows.Where(r => r.IsHidden).Select(r => r.Info.DeviceId),
        _rows.Where(r => !r.IsHidden).Select(r => r.Info.DeviceId));

    void Default_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading) Dispatcher.BeginInvoke(SaveScanners); // after the binding has updated IsDefault
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ManagedScanner row) return;
        row.IsHidden = !row.IsHidden;
        if (row.IsHidden) row.IsDefault = false;
        SaveScanners();
    }

    async void Search_Click(object sender, RoutedEventArgs e)
    {
        SearchButton.IsEnabled = false;
        await _host.RefreshScannersAsync();
        _loading = true;
        LoadScanners();
        _loading = false;
        await LoadPrintersAsync();
        SearchButton.IsEnabled = true;
    }

    void AddInWindows_Click(object sender, RoutedEventArgs e) => OpenUrl("ms-settings:printers");

    // ---------- About ----------

    void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url) OpenUrl(url);
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>A device row with links to the manufacturer's driver and software pages.</summary>
public class DeviceLinks(string name, DeviceSupport? support) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public DeviceSupport? Support { get; } = support;
    public Visibility LinksVisibility => Support != null ? Visibility.Visible : Visibility.Collapsed;
    public string SupportText => Support != null ? Loc.F("Device.SupportLink", Support.Brand) : "";
    public string SearchText => Support != null ? Loc.F("Device.SearchLink", Support.Model, Support.Domain) : "";

    public virtual void RefreshText()
    {
        OnChanged(nameof(SupportText));
        OnChanged(nameof(SearchText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ManagedScanner(ScannerInfo info) : DeviceLinks(info.Name, DeviceCatalog.Identify(info.Name))
{
    bool _isDefault, _isHidden;

    public ScannerInfo Info { get; } = info;

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

    public override void RefreshText()
    {
        base.RefreshText();
        OnChanged(nameof(Detail));
    }
}
