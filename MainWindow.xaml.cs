using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ScanDesk;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ScannedPage> _pages = [];
    readonly AppSettings _settings = AppSettings.Load();
    bool _busy;
    bool _unsaved;
    string? _lastSavedPath;
    int _previewVersion;
    Point _dragStart;

    static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif"];

    public MainWindow()
    {
        InitializeComponent();
        PageList.ItemsSource = _pages;
        VersionRun.Text = UpdateChecker.CurrentVersion != null
            ? $"Version {UpdateChecker.CurrentVersionText}"
            : "Development build";
        _pages.CollectionChanged += (_, _) => { Renumber(); UpdateUi(); };

        SelectByTag(ColorBox, _settings.Color.ToString());
        SelectByTag(DpiBox, _settings.Dpi.ToString());
        SelectByTag(AreaBox, _settings.Area.ToString());
        SelectByTag(QualityBox, _settings.Quality.ToString());
        SelectByTag(EnhanceBox, _settings.Enhance.ToString());
        SelectByTag(RotateBox, _settings.Rotation.ToString());
        BrightnessSlider.Value = _settings.Brightness;
        ContrastSlider.Value = _settings.Contrast;
        DeskewBox.IsChecked = _settings.Deskew;
        if (QualityBox.SelectedIndex < 0) QualityBox.SelectedIndex = 1;

        Loaded += async (_, _) =>
        {
            // Image files passed on the command line (e.g. "Open with ScanDesk") are added as pages.
            var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
            if (files.Count > 0) await ImportAsync(files);
            await RefreshScannersAsync();
            await CheckForUpdatesAsync(manual: false);
        };
        UpdateUi();
    }

    // ---------- Scanners ----------

    List<ScannerInfo> _allScanners = [];

    /// <summary>An entry in the scanner drop-down.</summary>
    sealed record ScannerChoice(ScannerInfo Info, bool IsDefault)
    {
        public override string ToString() => IsDefault ? $"{Info.Name}  ★" : Info.Name;
    }

    ScannerInfo? SelectedScanner => (ScannerBox.SelectedItem as ScannerChoice)?.Info;

    async Task RefreshScannersAsync()
    {
        SetBusy(true, "Looking for scanners…");
        try
        {
            _allScanners = await WiaScanner.RunStaAsync(WiaScanner.GetScanners);
            ApplyScannerList(keepSelection: false);
            SetStatus(_allScanners.Count == 0 ? "No scanner found." : $"Found {_allScanners.Count} scanner(s).");
        }
        catch (Exception ex)
        {
            SetStatus("Could not list scanners: " + ex.Message);
        }
        finally { SetBusy(false); }
    }

    /// <summary>Fills the drop-down with the scanners that aren't hidden, picking the default one first.</summary>
    void ApplyScannerList(bool keepSelection)
    {
        string? current = keepSelection ? SelectedScanner?.DeviceId : null;
        var visible = _allScanners
            .Where(s => !_settings.HiddenScannerIds.Contains(s.DeviceId))
            .Select(s => new ScannerChoice(s, s.DeviceId == _settings.DefaultScannerId))
            .ToList();
        ScannerBox.ItemsSource = visible;
        ScannerBox.SelectedItem =
            visible.FirstOrDefault(c => c.Info.DeviceId == current) ??
            visible.FirstOrDefault(c => c.IsDefault) ??
            visible.FirstOrDefault(c => c.Info.DeviceId == _settings.ScannerId) ??
            visible.FirstOrDefault();

        int hidden = _allScanners.Count - visible.Count;
        if (_allScanners.Count == 0)
            ScannerHint.Text = "No scanner found. Switch the printer on, connect it by USB or Wi-Fi, then press refresh.";
        else if (visible.Count == 0)
            ScannerHint.Text = "All scanners are removed from the list. Press the gear button to add one back.";
        else if (visible.Count > 1 && _settings.DefaultScannerId == null)
            ScannerHint.Text = "Your printer may show up twice (USB/network). Use the gear button to pick a default or remove the extra one.";
        else if (hidden > 0)
            ScannerHint.Text = $"{hidden} scanner(s) hidden. Use the gear button to show them.";
        else ScannerHint.Text = "";
        ScannerHint.Visibility = ScannerHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshScannersAsync();

    void Manage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ScannerManagerWindow(_allScanners, _settings.DefaultScannerId, _settings.HiddenScannerIds) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Keep hidden ids of scanners that are offline right now; update the ones shown in the dialog.
        var shown = dlg.ShownIds.ToHashSet();
        _settings.HiddenScannerIds = _settings.HiddenScannerIds.Where(id => !shown.Contains(id)).Union(dlg.HiddenIds).ToList();
        _settings.DefaultScannerId = dlg.DefaultId
            ?? (_allScanners.Any(s => s.DeviceId == _settings.DefaultScannerId) ? null : _settings.DefaultScannerId);
        _settings.Save();
        ApplyScannerList(keepSelection: true);
    }

    // ---------- Scanning ----------

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (SelectedScanner is not { } scanner)
        {
            MessageBox.Show(this, "No scanner selected. Make sure the printer is on and press the refresh button.",
                "ScanDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new ScanSettings(
            Enum.Parse<ColorMode>(TagOf(ColorBox) ?? "Color"),
            int.Parse(TagOf(DpiBox) ?? "300"),
            Enum.Parse<ScanArea>(TagOf(AreaBox) ?? "A4"));
        var enhance = new EnhanceOptions(
            Enum.Parse<EnhanceMode>(TagOf(EnhanceBox) ?? "Document"),
            (int)BrightnessSlider.Value, (int)ContrastSlider.Value,
            int.Parse(TagOf(RotateBox) ?? "0"),
            DeskewBox.IsChecked == true);
        SaveSettings();

        SetBusy(true, $"Scanning page {_pages.Count + 1}…");
        try
        {
            var result = await WiaScanner.RunStaAsync(() => WiaScanner.Scan(scanner.DeviceId, settings, PageStore.SessionFolder));
            SetBusy(true, "Processing…");
            var page = await Task.Run(() => PageStore.FromScan(result, settings.Color, enhance));
            _pages.Add(page);
            _unsaved = true;
            PageList.SelectedItem = page;
            PageList.ScrollIntoView(page);
            SetStatus($"Page {page.Number} scanned. Put the next page on the glass and press Scan, or save.");
        }
        catch (ScanException ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("Scan failed: " + ex.Message);
            MessageBox.Show(this, "Scan failed:\n\n" + ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    void ResetAdvanced_Click(object sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 0;
    }

    // ---------- Import ----------

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add images",
            Multiselect = true,
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) await ImportAsync(dlg.FileNames);
    }

    async Task ImportAsync(IEnumerable<string> files)
    {
        if (_busy) return;
        var list = files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (list.Count == 0) return;
        SetBusy(true, "Adding images…");
        var failed = new List<string>();
        try
        {
            foreach (var file in list)
            {
                try
                {
                    var pages = await Task.Run(() => PageStore.Import(file));
                    foreach (var p in pages) _pages.Add(p);
                }
                catch { failed.Add(Path.GetFileName(file)); }
            }
            _unsaved = true;
            if (_pages.Count > 0) PageList.SelectedItem = _pages[^1];
            SetStatus(failed.Count == 0 ? $"Added {list.Count} file(s)." : $"Could not read: {string.Join(", ", failed)}");
        }
        finally { SetBusy(false); }
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await ImportAsync(files);
    }

    // ---------- Page list ----------

    List<ScannedPage> SelectedPages() => PageList.SelectedItems.Cast<ScannedPage>().OrderBy(p => _pages.IndexOf(p)).ToList();

    void Renumber()
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Number = i + 1;
    }

    void Move(int delta)
    {
        var sel = SelectedPages();
        if (sel.Count == 0) return;
        if (delta < 0 && _pages.IndexOf(sel[0]) == 0) return;
        if (delta > 0 && _pages.IndexOf(sel[^1]) == _pages.Count - 1) return;
        var ordered = delta > 0 ? Enumerable.Reverse(sel) : sel;
        foreach (var p in ordered)
        {
            int i = _pages.IndexOf(p);
            _pages.Move(i, i + delta);
        }
        foreach (var p in sel) PageList.SelectedItems.Add(p);
        PageList.ScrollIntoView(sel[0]);
        _unsaved = true;
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    async Task RotateAsync(int degrees)
    {
        var sel = SelectedPages();
        if (sel.Count == 0 || _busy) return;
        SetBusy(true, "Rotating…");
        try
        {
            foreach (var p in sel)
            {
                string path = p.FilePath;
                p.Apply(await Task.Run(() => PageStore.Rotate(path, degrees)));
            }
            _unsaved = true;
        }
        finally { SetBusy(false); }
        ShowPreview();
    }

    async void RotateLeft_Click(object sender, RoutedEventArgs e) => await RotateAsync(-90);
    async void RotateRight_Click(object sender, RoutedEventArgs e) => await RotateAsync(90);

    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    void DeleteSelected()
    {
        var sel = SelectedPages();
        if (sel.Count == 0 || _busy) return;
        int index = _pages.IndexOf(sel[0]);
        foreach (var p in sel)
        {
            _pages.Remove(p);
            foreach (var f in p.AllFiles()) PageStore.TryDelete(f);
        }
        if (_pages.Count > 0) PageList.SelectedItem = _pages[Math.Min(index, _pages.Count - 1)];
        _unsaved = _pages.Count > 0;
    }

    void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0 || _busy) return;
        if (_unsaved && MessageBox.Show(this, $"Remove all {_pages.Count} page(s)? Unsaved scans will be lost.", "Clear all",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var f in _pages.SelectMany(p => p.AllFiles())) PageStore.TryDelete(f);
        _pages.Clear();
        _unsaved = false;
        SetStatus("All pages removed.");
    }

    void PageList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }

    void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cropMode) EndCropMode();
        ShowPreview();
        UpdateUi();
    }

    // Drag to reorder
    void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _busy) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemAt(e.OriginalSource) is { } page)
            DragDrop.DoDragDrop(PageList, new DataObject(typeof(ScannedPage), page), DragDropEffects.Move);
    }

    void PageList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ScannedPage)) ? DragDropEffects.Move
                  : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void PageList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ScannedPage)) is not ScannedPage dragged) return;
        e.Handled = true;
        int from = _pages.IndexOf(dragged);
        int to = ItemAt(e.OriginalSource) is { } target ? _pages.IndexOf(target) : _pages.Count - 1;
        if (from < 0 || to < 0 || from == to) return;
        _pages.Move(from, to);
        PageList.SelectedItem = dragged;
        _unsaved = true;
    }

    static ScannedPage? ItemAt(object source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as ScannedPage;
    }

    // ---------- Preview ----------

    ScannedPage? PreviewPage =>
        PageList.SelectedItem as ScannedPage ?? (PageList.SelectedItems.Count == 0 ? null : PageList.SelectedItems[0] as ScannedPage);

    async void ShowPreview()
    {
        int version = ++_previewVersion;
        var page = PreviewPage;
        if (page == null)
        {
            PreviewImage.Source = null;
            PreviewTitle.Text = "Preview";
            PreviewInfo.Text = "";
            return;
        }
        PreviewTitle.Text = $"Page {page.Number} of {_pages.Count}";
        PreviewInfo.Text = page.SizeText;
        PreviewImage.Source = page.Thumbnail;
        string path = page.FilePath;
        try
        {
            var img = await Task.Run(() => PageStore.Load(path, Math.Min(page.PixelWidth, 1600)));
            if (version == _previewVersion) PreviewImage.Source = img;
        }
        catch { /* keep thumbnail */ }
    }

    // ---------- Crop & undo ----------

    [Flags] enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Inside = 16 }
    enum CropDrag { None, New, Move, Resize }

    const double HandleTolerance = 9;
    const double MinSelection = 6;

    bool _cropMode;
    Rect? _cropSel;          // selection in normalized image coordinates (0..1), survives window resizes
    CropDrag _cropDrag;
    Edge _cropEdges;
    Point _cropOrigin;
    Rect _cropStartRect;     // selection in canvas coordinates when the drag started

    void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || PreviewPage == null) return;
        _cropMode = true;
        _cropSel = null;
        NormalHeader.Visibility = Visibility.Collapsed;
        CropHeader.Visibility = Visibility.Visible;
        CropCanvas.Visibility = Visibility.Visible;
        RenderCrop();
    }

    void EndCropMode()
    {
        _cropMode = false;
        _cropSel = null;
        _cropDrag = CropDrag.None;
        CropCanvas.ReleaseMouseCapture();
        CropCanvas.Visibility = Visibility.Collapsed;
        CropHeader.Visibility = Visibility.Collapsed;
        NormalHeader.Visibility = Visibility.Visible;
    }

    void CropCancel_Click(object sender, RoutedEventArgs e) => EndCropMode();

    async void CropApply_Click(object sender, RoutedEventArgs e) => await ApplyCropAsync();

    async Task ApplyCropAsync()
    {
        if (_busy || _cropSel is not { } sel || PreviewPage is not { } page) return;
        int x = (int)Math.Round(sel.X * page.PixelWidth), y = (int)Math.Round(sel.Y * page.PixelHeight);
        int w = (int)Math.Round(sel.Width * page.PixelWidth), h = (int)Math.Round(sel.Height * page.PixelHeight);
        x = Math.Clamp(x, 0, page.PixelWidth - 1);
        y = Math.Clamp(y, 0, page.PixelHeight - 1);
        var rect = new Int32Rect(x, y, Math.Clamp(w, 1, page.PixelWidth - x), Math.Clamp(h, 1, page.PixelHeight - y));

        EndCropMode();
        SetBusy(true, "Cropping…");
        try
        {
            string path = page.FilePath;
            page.Apply(await Task.Run(() => PageStore.Crop(path, rect)));
            _unsaved = true;
            SetStatus($"Page {page.Number} cropped to {rect.Width} × {rect.Height} px. Press Undo to get the full page back.");
        }
        catch (Exception ex)
        {
            SetStatus("Cropping failed: " + ex.Message);
        }
        finally { SetBusy(false); }
        ShowPreview();
    }

    // ---------- Updates ----------

    UpdateInfo? _update;

    /// <summary>Looks for a newer release on GitHub. Automatic checks stay silent when offline or up to date.</summary>
    async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && UpdateChecker.CurrentVersion == null) return; // local builds don't nag
        if (manual) SetStatus("Checking for updates…");
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update != null) ShowUpdate(update);
            else if (manual) SetStatus($"You have the latest version ({UpdateChecker.CurrentVersionText}).");
        }
        catch (Exception ex)
        {
            if (manual) SetStatus("Could not check for updates: " + ex.Message);
        }
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    void ShowUpdate(UpdateInfo update)
    {
        _update = update;
        UpdateTitle.Text = $"ScanDesk {update.Version} is available";
        UpdateDetail.Text = UpdateChecker.CurrentVersion != null
            ? $"You have {UpdateChecker.CurrentVersionText}. Settings are kept; ScanDesk reopens by itself."
            : "Your settings are kept and ScanDesk reopens by itself.";
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateButtons.IsEnabled = true;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) UpdateChecker.OpenInBrowser(_update.PageUrl);
    }

    async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update || _busy) return;
        if (_unsaved && _pages.Count > 0 && MessageBox.Show(this,
                $"You have {_pages.Count} page(s) that are not saved. Installing the update closes ScanDesk and they will be lost.\n\nInstall anyway?",
                "Install update", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        UpdateButtons.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"Downloading ScanDesk {update.Version}…";
        double totalMb = update.Size / 1048576.0;
        try
        {
            string installer = await UpdateChecker.DownloadAsync(update, new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateDetail.Text = $"{p * totalMb:0.0} of {totalMb:0.0} MB";
            }));

            UpdateTitle.Text = $"Installing ScanDesk {update.Version}…";
            UpdateDetail.Text = "Approve the Windows prompt. ScanDesk closes and reopens when the update is done.";
            if (!UpdateChecker.LaunchInstaller(installer))
            {
                ShowUpdate(update);
                SetStatus("Update cancelled.");
                return;
            }
            _unsaved = false; // already confirmed above
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ShowUpdate(update);
            SetStatus("Update failed: " + ex.Message);
            MessageBox.Show(this, "The update could not be installed:\n\n" + ex.Message, "ScanDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Fit to page ----------

    void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (FitButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = FitButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    async void FitPaper_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is string tag) await FitToPaperAsync(Enum.Parse<PaperSize>(tag));
    }

    /// <summary>Enlarges the selected pages to fill a full sheet (undoable).</summary>
    async Task FitToPaperAsync(PaperSize paper)
    {
        var sel = SelectedPages();
        if (sel.Count == 0 || _busy) return;
        SetBusy(true, $"Enlarging to {Upscaler.PaperName(paper)}…");
        try
        {
            foreach (var p in sel)
            {
                string path = p.FilePath;
                double dpi = p.Dpi;
                bool gray = p.IsGray;
                p.Apply(await Task.Run(() => PageStore.FitToPaper(path, dpi, gray, paper)));
            }
            _unsaved = true;
            SetStatus(sel.Count == 1
                ? $"Page {sel[0].Number} now fills a full {Upscaler.PaperName(paper)} page. Press Undo to go back."
                : $"{sel.Count} pages now fill a {Upscaler.PaperName(paper)} sheet. Press Undo on a page to go back.");
        }
        catch (Exception ex)
        {
            SetStatus("Enlarging failed: " + ex.Message);
        }
        finally { SetBusy(false); }
        ShowPreview();
    }

    void Undo_Click(object sender, RoutedEventArgs e) => UndoPage();

    void UndoPage()
    {
        if (_busy || PreviewPage is not { CanUndo: true } page) return;
        page.Undo();
        _unsaved = true;
        SetStatus($"Page {page.Number} restored.");
        ShowPreview();
        UpdateUi();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_cropMode)
        {
            if (e.Key == Key.Escape) { EndCropMode(); e.Handled = true; }
            else if (e.Key == Key.Enter && _cropSel != null) { _ = ApplyCropAsync(); e.Handled = true; }
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            UndoPage();
            e.Handled = true;
        }
    }

    /// <summary>Where the page image is drawn inside the crop canvas.</summary>
    Rect ImageRect()
    {
        if (PreviewImage.ActualWidth <= 0) return Rect.Empty;
        var origin = PreviewImage.TranslatePoint(new Point(0, 0), CropCanvas);
        return new Rect(origin, new Size(PreviewImage.ActualWidth, PreviewImage.ActualHeight));
    }

    Rect? SelectionOnCanvas()
    {
        var ir = ImageRect();
        if (_cropSel is not { } s || ir.IsEmpty) return null;
        return new Rect(ir.X + s.X * ir.Width, ir.Y + s.Y * ir.Height, s.Width * ir.Width, s.Height * ir.Height);
    }

    static Point Clamp(Point p, Rect r) => new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    Edge HitTest(Point p)
    {
        if (SelectionOnCanvas() is not { } r) return Edge.None;
        bool inY = p.Y >= r.Top - HandleTolerance && p.Y <= r.Bottom + HandleTolerance;
        bool inX = p.X >= r.Left - HandleTolerance && p.X <= r.Right + HandleTolerance;
        var edges = Edge.None;
        if (inY && Math.Abs(p.X - r.Left) <= HandleTolerance) edges |= Edge.Left;
        if (inY && Math.Abs(p.X - r.Right) <= HandleTolerance) edges |= Edge.Right;
        if (inX && Math.Abs(p.Y - r.Top) <= HandleTolerance) edges |= Edge.Top;
        if (inX && Math.Abs(p.Y - r.Bottom) <= HandleTolerance) edges |= Edge.Bottom;
        if (edges == Edge.None && r.Contains(p)) edges = Edge.Inside;
        return edges;
    }

    static Cursor CursorFor(Edge e) => e switch
    {
        Edge.Inside => Cursors.SizeAll,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        _ => Cursors.Cross,
    };

    void Crop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var ir = ImageRect();
        if (ir.IsEmpty) return;
        var p = e.GetPosition(CropCanvas);
        _cropEdges = HitTest(p);
        _cropStartRect = SelectionOnCanvas() ?? Rect.Empty;
        _cropOrigin = _cropEdges == Edge.None ? Clamp(p, ir) : p;
        _cropDrag = _cropEdges switch
        {
            Edge.None => CropDrag.New,
            Edge.Inside => CropDrag.Move,
            _ => CropDrag.Resize,
        };
        CropCanvas.CaptureMouse();
        e.Handled = true;
    }

    void Crop_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(CropCanvas);
        if (_cropDrag == CropDrag.None)
        {
            CropCanvas.Cursor = CursorFor(HitTest(p));
            return;
        }

        var ir = ImageRect();
        if (ir.IsEmpty) return;
        Rect r;
        switch (_cropDrag)
        {
            case CropDrag.New:
                r = new Rect(_cropOrigin, Clamp(p, ir));
                break;
            case CropDrag.Move:
                var d = p - _cropOrigin;
                double nx = Math.Clamp(_cropStartRect.X + d.X, ir.Left, ir.Right - _cropStartRect.Width);
                double ny = Math.Clamp(_cropStartRect.Y + d.Y, ir.Top, ir.Bottom - _cropStartRect.Height);
                r = new Rect(nx, ny, _cropStartRect.Width, _cropStartRect.Height);
                break;
            default:
                var c = Clamp(p, ir);
                double l = _cropStartRect.Left, t = _cropStartRect.Top, rt = _cropStartRect.Right, b = _cropStartRect.Bottom;
                if (_cropEdges.HasFlag(Edge.Left)) l = c.X;
                if (_cropEdges.HasFlag(Edge.Right)) rt = c.X;
                if (_cropEdges.HasFlag(Edge.Top)) t = c.Y;
                if (_cropEdges.HasFlag(Edge.Bottom)) b = c.Y;
                r = new Rect(new Point(l, t), new Point(rt, b)); // Rect normalizes if dragged past the opposite edge
                break;
        }
        _cropSel = new Rect((r.X - ir.X) / ir.Width, (r.Y - ir.Y) / ir.Height, r.Width / ir.Width, r.Height / ir.Height);
        RenderCrop();
    }

    void Crop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropDrag == CropDrag.None) return;
        _cropDrag = CropDrag.None;
        CropCanvas.ReleaseMouseCapture();
        if (SelectionOnCanvas() is { } r && (r.Width < MinSelection || r.Height < MinSelection)) _cropSel = null;
        RenderCrop();
    }

    void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderCrop();

    void RenderCrop()
    {
        if (!_cropMode) return;
        // Layout may not be final yet (e.g. right after the preview image changed).
        Dispatcher.BeginInvoke(DrawCrop, System.Windows.Threading.DispatcherPriority.Render);
    }

    void DrawCrop()
    {
        if (!_cropMode) return;
        var ir = ImageRect();
        var sel = SelectionOnCanvas();
        ApplyCropButton.IsEnabled = sel != null && !_busy;
        CropHint.Text = sel == null
            ? "Drag over the part of the page you want to keep"
            : "Drag the corners or edges to adjust, or drag inside to move";

        var handles = new[] { CropHandle0, CropHandle1, CropHandle2, CropHandle3 };
        bool show = sel != null && !ir.IsEmpty;
        foreach (UIElement el in new UIElement[] { CropShade, CropOutline, CropDashes, CropSizeLabel }.Concat(handles))
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var r = sel!.Value;
        CropShade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(ir), new RectangleGeometry(r) },
        };
        foreach (var rect in new[] { CropOutline, CropDashes })
        {
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
            rect.Width = r.Width;
            rect.Height = r.Height;
        }
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(handles[i], corners[i].X - handles[i].Width / 2);
            Canvas.SetTop(handles[i], corners[i].Y - handles[i].Height / 2);
        }

        if (PreviewPage is { } page && _cropSel is { } s)
        {
            int pw = (int)Math.Round(s.Width * page.PixelWidth), ph = (int)Math.Round(s.Height * page.PixelHeight);
            CropSizeText.Text = $"{pw} × {ph} px  ·  {pw / page.Dpi * 25.4:0} × {ph / page.Dpi * 25.4:0} mm";
        }
        CropSizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelY = r.Top - CropSizeLabel.DesiredSize.Height - 8;
        Canvas.SetLeft(CropSizeLabel, r.Left);
        Canvas.SetTop(CropSizeLabel, labelY >= 0 ? labelY : r.Top + 10);
    }

    // ---------- Saving ----------

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_pages.Count == 0)
        {
            MessageBox.Show(this, "There is nothing to save yet. Scan a page first.", "ScanDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string folder = _settings.LastFolder is { } f && Directory.Exists(f)
            ? f : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dlg = new SaveFileDialog
        {
            Title = "Save scan",
            InitialDirectory = folder,
            FileName = $"Scan {DateTime.Now:yyyy-MM-dd HHmm}",
            Filter = "PDF document – all pages in one file (*.pdf)|*.pdf|" +
                     "JPEG images – one file per page (*.jpg)|*.jpg|" +
                     "PNG images – one file per page (*.png)|*.png|" +
                     "TIFF image – all pages in one file (*.tif)|*.tif",
            FilterIndex = Math.Clamp(_settings.SaveFormatIndex, 1, 4),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        string path = dlg.FileName;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff"))
        {
            ext = dlg.FilterIndex switch { 2 => ".jpg", 3 => ".png", 4 => ".tif", _ => ".pdf" };
            path += ext;
        }

        _settings.LastFolder = Path.GetDirectoryName(path);
        _settings.SaveFormatIndex = dlg.FilterIndex;
        SaveSettings();

        var pages = _pages.ToList();
        int quality = int.Parse(TagOf(QualityBox) ?? "85");

        // Images: one file per page, numbered when there is more than one page.
        List<string>? imagePaths = null;
        if (ext is ".jpg" or ".jpeg" or ".png")
        {
            string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path);
            imagePaths = pages.Count == 1
                ? [path]
                : pages.Select((_, i) => Path.Combine(dir, $"{name} {i + 1:D3}{ext}")).ToList();
            var existing = imagePaths.Skip(pages.Count == 1 ? 1 : 0).Where(File.Exists).ToList();
            if (existing.Count > 0 && MessageBox.Show(this,
                    $"{existing.Count} file(s) with these names already exist, e.g.\n{Path.GetFileName(existing[0])}\n\nReplace them?",
                    "Replace files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }

        var progress = new Progress<int>(i => BusyText.Text = $"Saving page {i} of {pages.Count}…");
        SetBusy(true, "Saving…");
        try
        {
            await Task.Run(() =>
            {
                if (ext == ".pdf") PageStore.ExportPdf(pages, path, quality, progress);
                else if (ext is ".tif" or ".tiff") PageStore.ExportTiff(pages, path, progress);
                else PageStore.ExportImages(pages, imagePaths!, quality, progress);
            });
            _unsaved = false;
            _lastSavedPath = imagePaths?[0] ?? path;
            SetStatus(imagePaths is { Count: > 1 }
                ? $"Saved {pages.Count} images to {Path.GetDirectoryName(path)}"
                : $"Saved {pages.Count} page(s) to {path}");
            OpenFolderButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetStatus("Saving failed: " + ex.Message);
            MessageBox.Show(this, "Saving failed:\n\n" + ex.Message, "ScanDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedPath != null && File.Exists(_lastSavedPath))
            Process.Start("explorer.exe", $"/select,\"{_lastSavedPath}\"");
    }

    // ---------- Helpers ----------

    void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text != null) BusyText.Text = text;
        UpdateUi();
    }

    void SetStatus(string text) => StatusText.Text = text;

    void UpdateUi()
    {
        bool hasPages = _pages.Count > 0;
        bool hasSel = PageList.SelectedItems.Count > 0;
        EmptyState.Visibility = hasPages ? Visibility.Collapsed : Visibility.Visible;
        PreviewImage.Visibility = hasPages ? Visibility.Visible : Visibility.Collapsed;
        PagesHeader.Text = hasPages ? $"Pages ({_pages.Count})" : "Pages";
        ScanButtonText.Text = hasPages ? "Scan next page" : "Scan page";

        ScanButton.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
        ManageButton.IsEnabled = !_busy;
        ImportButton.IsEnabled = !_busy;
        SaveButton.IsEnabled = !_busy && hasPages;
        ClearButton.IsEnabled = !_busy && hasPages;
        foreach (var b in new[] { UpButton, DownButton, RotLeftButton, RotRightButton, DeleteButton })
            b.IsEnabled = !_busy && hasSel;
        var preview = PreviewPage;
        CropButton.IsEnabled = !_busy && preview != null;
        FitButton.IsEnabled = !_busy && preview != null;
        UndoButton.Visibility = preview?.CanUndo == true ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = !_busy;
        if (!hasPages) { PreviewTitle.Text = "Preview"; PreviewInfo.Text = ""; }
        else if (PageList.SelectedItem is ScannedPage p) PreviewTitle.Text = $"Page {p.Number} of {_pages.Count}";
    }

    void SaveSettings()
    {
        if (SelectedScanner is { } s) _settings.ScannerId = s.DeviceId;
        if (TagOf(ColorBox) is { } c) _settings.Color = Enum.Parse<ColorMode>(c);
        if (TagOf(DpiBox) is { } d) _settings.Dpi = int.Parse(d);
        if (TagOf(AreaBox) is { } a) _settings.Area = Enum.Parse<ScanArea>(a);
        if (TagOf(QualityBox) is { } q) _settings.Quality = int.Parse(q);
        if (TagOf(EnhanceBox) is { } en) _settings.Enhance = Enum.Parse<EnhanceMode>(en);
        if (TagOf(RotateBox) is { } r) _settings.Rotation = int.Parse(r);
        _settings.Brightness = (int)BrightnessSlider.Value;
        _settings.Contrast = (int)ContrastSlider.Value;
        _settings.Deskew = DeskewBox.IsChecked == true;
        _settings.Save();
    }

    static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        if (_unsaved && _pages.Count > 0 && MessageBox.Show(this,
                $"You have {_pages.Count} scanned page(s) that are not saved. Close anyway?",
                "ScanDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SaveSettings();
        base.OnClosing(e);
    }
}
