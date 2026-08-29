using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CleanSpace.Models;
using CleanSpace.Services;
using CleanSpace.Views;

namespace CleanSpace;

public partial class MainWindow : Window
{
    private enum BusyOperation { None, Scan, Analysis, Cleanup, AppResidual }

    private const int VisibleResultLimit = 10_000;
    private const int VisibleMediaLimit = 5_000;

    private readonly Localizer _text = new();
    private readonly RiskService _risk = new();
    private readonly ScannerService _scanner;
    private readonly DriveService _drives = new();
    private readonly DuplicateService _duplicates = new();
    private readonly MediaService _media = new();
    private readonly InstalledAppService _apps = new();
    private readonly AppResidualService _residuals = new();
    private readonly IndexStore _index = new();
    private readonly RecycleService _recycle = new();
    private readonly RestartCleanupService _restartCleanup = new();
    private readonly HashSet<string> _cleanupPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly TopFileCollector _topFiles = new(VisibleResultLimit);
    private readonly TopFileCollector _topMedia = new(VisibleMediaLimit);
    private readonly DispatcherTimer _filterTimer;
    private CancellationTokenSource? _analysisCts;
    private InstalledApp? _pendingUninstall;
    private IndexStore.IndexWriter? _indexWriter;
    private BusyOperation _busyOperation;
    private long _fileCount;
    private long _totalBytes;
    private long _safeBytes;
    private int _largeFileCount;
    private int _errorCount;
    private bool _languageShown;

    public ObservableCollection<DriveOption> DriveRows { get; } = [];
    public ObservableCollection<FileRecord> SpaceRows { get; } = [];
    public ObservableCollection<FileRecord> MediaRows { get; } = [];
    public ObservableCollection<DuplicateRow> DuplicateRows { get; } = [];
    public ObservableCollection<InstalledApp> AppRows { get; } = [];
    public ObservableCollection<CleanupItem> CleanupRows { get; } = [];
    public ObservableCollection<AppResidual> ResidualRows { get; } = [];
    public ObservableCollection<HistoryItem> HistoryRows { get; } = [];

    public MainWindow()
    {
        _scanner = new ScannerService(_risk);
        InitializeComponent();
        DataContext = this;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            try { await ApplySpaceFilterAsync(); }
            catch { StatusText.Text = _text["filter.failed"]; }
        };
        ContentRendered += MainWindow_ContentRendered;
        Closing += MainWindow_Closing;
        Retranslate();
        RefreshDrives();
        PermissionBanner.Visibility = IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
        _ = RefreshAppsAsync();
        _ = LoadPreviousIndexAsync();
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_languageShown) return;
        _languageShown = true;
        ShowLanguageDialog();
    }

    private void ShowLanguageDialog()
    {
        var dialog = new LanguageWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _text.SetLocale(dialog.SelectedLocale);
            Retranslate();
        }
    }

    private void Retranslate()
    {
        Title = _text["app.title"];
        AuthorText.Text = _text.Locale == LocaleCode.KoKr ? "허준영 제작" : "허준영 制作";
        NavDashboard.Content = _text["nav.dashboard"]; NavSpace.Content = _text["nav.space"];
        NavMedia.Content = _text["nav.media"]; NavDuplicates.Content = _text["nav.duplicates"];
        NavApps.Content = _text["nav.apps"]; NavCleanup.Content = _text["nav.cleanup"];
        NavHistory.Content = _text["nav.history"]; NavSettings.Content = _text["nav.settings"]; NavAbout.Content = _text["nav.about"];
        ScanSystemButton.Content = _text["scan.system"]; ScanAllButton.Content = _text["scan.all"]; RefreshDrivesButton.Content = _text["action.refresh_drives"];
        AdminButton.Content = IsAdministrator() ? _text["action.admin_active"] : _text["action.admin"];
        AdminButton.IsEnabled = !IsAdministrator();
        PermissionTitle.Text = _text["admin.banner_title"]; PermissionText.Text = _text["admin.banner_text"];
        ContinueStandardButton.Content = _text["admin.continue"]; RestartAdminButton.Content = _text["admin.restart"];
        ScanChoiceText.Text = _text["scan.choice"];
        PauseButton.Content = _scanner.IsPaused ? _text["scan.resume"] : _text["scan.pause"]; CancelButton.Content = _text["scan.cancel"];
        DashboardTitle.Text = _text["title.dashboard"]; SpaceTitle.Text = _text["title.space"]; MediaTitle.Text = _text["title.media"];
        DuplicatesTitle.Text = _text["title.duplicates"]; AppsTitle.Text = _text["title.apps"]; CleanupTitle.Text = _text["title.cleanup"];
        HistoryTitle.Text = _text["title.history"]; SettingsTitle.Text = _text["title.settings"]; AboutTitle.Text = _text["title.about"];
        SpaceFilter.ToolTip = _text["filter.placeholder"];
        SpaceAddButton.Content = _text["action.add"]; SpaceOpenButton.Content = _text["action.open"]; SpaceLocateButton.Content = _text["action.locate"];
        MediaCheckButton.Content = _text["action.check_media"]; MediaOpenButton.Content = _text["action.open"]; MediaLocateButton.Content = _text["action.locate"]; MediaAddButton.Content = _text["action.add"];
        FindDuplicatesButton.Content = _text["action.find_duplicates"]; FindSimilarPhotosButton.Content = _text["action.find_similar_photos"]; SelectDuplicatesButton.Content = _text["action.select_duplicates"]; DuplicateLocateButton.Content = _text["action.locate"]; DuplicateAddButton.Content = _text["action.add"];
        AppsRefreshButton.Content = _text["action.refresh"]; AppsUninstallButton.Content = _text["action.uninstall"];
        AppsCheckResidualsButton.Content = _text["action.check_residuals"]; AppsRecycleResidualsButton.Content = _text["action.recycle_residuals"];
        ResidualTitle.Text = _text["residual.title"]; ResidualHint.Text = _text["residual.hint"];
        ResidualSelectColumn.Header = _text["col.select"]; ResidualSourceColumn.Header = _text["col.source"];
        ResidualLocationColumn.Header = _text["col.location"]; ResidualRiskColumn.Header = _text["col.risk"];
        SelectSafeButton.Content = _text["action.select_safe"]; SelectAllowedButton.Content = _text["action.select_allowed"]; ClearSelectionButton.Content = _text["action.clear_selection"]; RemoveCleanupButton.Content = _text["action.remove_cleanup"]; RecycleButton.Content = _text["action.recycle"]; PermanentDeleteButton.Content = _text["cleanup.permanent"];
        LanguageButton.Content = _text["action.language"]; SettingsText.Text = _text["settings.text"]; AboutText.Text = _text["about.text"];
        SpaceNameColumn.Header = _text["col.name"]; SpacePathColumn.Header = _text["col.path"]; SpaceDriveColumn.Header = _text["col.drive"]; SpaceSizeColumn.Header = _text["col.size"]; SpaceRiskColumn.Header = _text["col.risk"];
        MediaNameColumn.Header = _text["col.name"]; MediaPathColumn.Header = _text["col.path"]; MediaSizeColumn.Header = _text["col.size"]; MediaStatusColumn.Header = _text["col.status"];
        DuplicateNameColumn.Header = _text["col.name"]; DuplicatePathColumn.Header = _text["col.path"]; DuplicateSizeColumn.Header = _text["col.size"];
        AppsNameColumn.Header = _text["col.name"]; AppsPublisherColumn.Header = _text["col.publisher"]; AppsVersionColumn.Header = _text["col.version"]; AppsSizeColumn.Header = _text["col.size"]; AppsLocationColumn.Header = _text["col.location"];
        CleanupSelectColumn.Header = _text["col.select"]; CleanupPathColumn.Header = _text["col.path"]; CleanupSizeColumn.Header = _text["col.size"]; CleanupRiskColumn.Header = _text["col.risk"]; CleanupReasonColumn.Header = _text["col.reason"];
        SpaceDetail.Text = _text["detail.none"]; CleanupDetail.Text = _text["detail.none"];
        if (!_scanner.IsRunning) StatusText.Text = _text["status.ready"];
        RefreshDashboardSummary(); RefreshCleanupSummary();
        SpaceGrid.Items.Refresh(); MediaGrid.Items.Refresh(); CleanupGrid.Items.Refresh();
    }

    private async void ScanSystem_Click(object sender, RoutedEventArgs e) => await StartScanAsync(_drives.GetSystemScanRoots());
    private async void ScanAll_Click(object sender, RoutedEventArgs e) => await StartScanAsync(_drives.GetAllScanRoots());
    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrives();
        StatusText.Text = string.Format(_text["drive.refreshed"], DriveRows.Count);
    }

    private async Task StartScanAsync(string[] roots)
    {
        if (_scanner.IsRunning || !TryBeginOperation(BusyOperation.Scan)) return;
        roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) { StatusText.Text = _text["drive.none"]; EndOperation(BusyOperation.Scan); return; }
        _analysisCts?.Cancel();
        _indexWriter?.Dispose();
        _indexWriter = _index.BeginWrite();
        _topFiles.Clear(); _topMedia.Clear(); SpaceRows.Clear(); MediaRows.Clear(); DuplicateRows.Clear();
        foreach (var item in CleanupRows) item.File.PropertyChanged -= CleanupItem_PropertyChanged;
        CleanupRows.Clear(); _cleanupPaths.Clear(); RefreshCleanupSummary();
        _fileCount = 0; _totalBytes = 0; _safeBytes = 0; _largeFileCount = 0; _errorCount = 0;
        SetScanControls(true);
        ScanProgress.IsIndeterminate = true;
        StatusText.Text = string.Format(_text["status.scanning"], string.Join(" + ", roots));

        var progress = new Progress<ScanUpdate>(update =>
        {
            if (update.Batch.Count > 0)
            {
                _indexWriter?.Append(update.Batch);
                _fileCount = update.FileCount;
                _totalBytes = update.TotalBytes;
                _errorCount = update.ErrorCount;
                foreach (var file in update.Batch)
                {
                    _topFiles.Add(file);
                    if (file.IsMedia) _topMedia.Add(file);
                    if (file.Risk == RiskLevel.Safe) _safeBytes += file.Size;
                    if (file.Size >= 1024L * 1024 * 1024) _largeFileCount++;
                    if (SpaceRows.Count < VisibleResultLimit) SpaceRows.Add(file);
                    if (file.IsMedia && MediaRows.Count < VisibleMediaLimit) MediaRows.Add(file);
                }
                RefreshDashboardSummary();
            }
            if (!update.Finished)
                StatusText.Text = string.Format(_text["status.scanning"], update.CurrentPath);
            else
            {
                FinishScan(update);
            }
        });

        try { await _scanner.ScanAsync(roots, progress); }
        catch (Exception ex)
        {
            EndOperation(BusyOperation.Scan); ScanProgress.IsIndeterminate = false;
            MessageBox.Show(this, ex.Message, "CleanSpace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FinishScan(ScanUpdate update)
    {
        ScanProgress.IsIndeterminate = false;
        ReplaceRows(SpaceRows, _topFiles.LargestFirst());
        ReplaceRows(MediaRows, _topMedia.LargestFirst());
        if (update.Cancelled) { _indexWriter?.Dispose(); }
        else
        {
            var summary = _indexWriter?.Complete();
            if (summary is not null)
            {
                _fileCount = summary.FileCount; _totalBytes = summary.TotalBytes;
                _safeBytes = summary.SafeBytes; _largeFileCount = summary.LargeFileCount;
            }
        }
        _indexWriter = null;
        StatusText.Text = update.Cancelled
            ? string.Format(_text["status.cancelled"], update.FileCount)
            : string.Format(_text["status.done"], update.FileCount, SizeFormatter.Format(update.TotalBytes)) + " · " +
              string.Format(_text["status.errors"], update.ErrorCount) + " · " + string.Format(_text["scan.elapsed"], update.Elapsed.TotalSeconds);
        RefreshDashboardSummary(); RefreshDrives();
        if (!update.Cancelled) _ = LoadSafeCandidatesAfterScanAsync();
        else EndOperation(BusyOperation.Scan);
    }

    private async Task LoadPreviousIndexAsync()
    {
        if (!_index.Exists || _scanner.IsRunning || _fileCount > 0) return;
        var summary = _index.ReadSummary();
        if (summary is null) return;
        var records = await _index.QueryLargestAsync(VisibleResultLimit);
        var media = await _index.QueryLargestAsync(VisibleMediaLimit, mediaOnly: true);
        if (_scanner.IsRunning || _fileCount > 0) return;
        _fileCount = summary.FileCount; _totalBytes = summary.TotalBytes; _safeBytes = summary.SafeBytes; _largeFileCount = summary.LargeFileCount;
        ReplaceRows(SpaceRows, records); ReplaceRows(MediaRows, media);
        RefreshDashboardSummary();
        await LoadSafeCandidatesAsync();
    }

    private async Task LoadSafeCandidatesAsync()
    {
        if (!_index.Exists) return;
        var safeRows = await _index.QueryByRiskAsync(RiskLevel.Safe);
        foreach (var file in safeRows)
        {
            if (!_cleanupPaths.Add(file.FullPath)) continue;
            file.PropertyChanged += CleanupItem_PropertyChanged;
            CleanupRows.Add(new CleanupItem { File = file, SourceKey = "source.cache" });
        }
        RefreshCleanupSummary();
    }
    private async Task LoadSafeCandidatesAfterScanAsync()
    {
        try { await LoadSafeCandidatesAsync(); }
        catch { StatusText.Text = _text["scan.results_failed"]; }
        finally { EndOperation(BusyOperation.Scan); }
    }


    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _scanner.TogglePause();
        PauseButton.Content = _scanner.IsPaused ? _text["scan.resume"] : _text["scan.pause"];
        if (_scanner.IsPaused) StatusText.Text = _text["status.paused"];
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busyOperation == BusyOperation.Scan) _scanner.Cancel();
        else if (_busyOperation == BusyOperation.Analysis)
        {
            _analysisCts?.Cancel();
            StatusText.Text = _text["status.cancelling"];
        }
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (IsAdministrator()) return;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        var arguments = Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? $"\"{Assembly.GetEntryAssembly()?.Location}\"" : "";
        try
        {
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception) { StatusText.Text = _text["admin.cancelled"]; }
        catch { StatusText.Text = _text["admin.failed"]; }
    }

    private void ContinueStandard_Click(object sender, RoutedEventArgs e)
    {
        PermissionBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = _text["admin.standard_active"];
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool TryBeginOperation(BusyOperation operation)
    {
        if (_busyOperation != BusyOperation.None)
        {
            StatusText.Text = _text["status.busy"];
            return false;
        }
        _busyOperation = operation;
        ApplyBusyControls();
        return true;
    }

    private void EndOperation(BusyOperation operation)
    {
        if (_busyOperation != operation) return;
        _busyOperation = BusyOperation.None;
        ApplyBusyControls();
    }

    private void ApplyBusyControls()
    {
        var idle = _busyOperation == BusyOperation.None;
        var scanning = _busyOperation == BusyOperation.Scan;
        var analysing = _busyOperation == BusyOperation.Analysis;

        ScanSystemButton.IsEnabled = idle && DriveRows.Any(x => x.IsSystemDrive);
        ScanAllButton.IsEnabled = idle && DriveRows.Count > 0;
        MediaCheckButton.IsEnabled = idle;
        FindDuplicatesButton.IsEnabled = idle;
        FindSimilarPhotosButton.IsEnabled = idle;
        SelectDuplicatesButton.IsEnabled = idle;
        DuplicateAddButton.IsEnabled = idle;
        SpaceAddButton.IsEnabled = idle;
        MediaAddButton.IsEnabled = idle;


        RefreshDrivesButton.IsEnabled = idle;
        PauseButton.IsEnabled = scanning;
        CancelButton.IsEnabled = scanning || analysing;
        if (!scanning) PauseButton.Content = _text["scan.pause"];

        CleanupGrid.IsEnabled = idle;
        SelectSafeButton.IsEnabled = idle;
        SelectAllowedButton.IsEnabled = idle;
        ClearSelectionButton.IsEnabled = idle;
        RemoveCleanupButton.IsEnabled = idle;
        RecycleButton.IsEnabled = idle;
        PermanentDeleteButton.IsEnabled = idle;

        AppsGrid.IsEnabled = idle;
        AppsRefreshButton.IsEnabled = idle;
        AppsUninstallButton.IsEnabled = idle;
        AppsCheckResidualsButton.IsEnabled = idle;
        AppsRecycleResidualsButton.IsEnabled = idle;
    }

    private bool RequireIdle()
    {
        if (_busyOperation == BusyOperation.None) return true;
        StatusText.Text = _text["status.busy"];
        return false;
    }

    private void SetScanControls(bool running) => ApplyBusyControls();

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) ShowPage(page);
    }

    private void ShowPage(string name)
    {
        var pages = new[] { DashboardPage, SpacePage, MediaPage, DuplicatesPage, AppsPage, CleanupPage, HistoryPage, SettingsPage, AboutPage };
        foreach (var page in pages) page.Visibility = page.Name == name ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SpaceFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterTimer.Stop(); _filterTimer.Start();
    }

    private async Task ApplySpaceFilterAsync()
    {
        var query = SpaceFilter.Text.Trim();
        IReadOnlyList<FileRecord> filtered;
        if (_index.Exists && !_scanner.IsRunning)
            filtered = await _index.QueryLargestAsync(VisibleResultLimit, query);
        else
            filtered = SpaceRows.Where(x => string.IsNullOrWhiteSpace(query) || x.FullPath.Contains(query, StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(x => x.Size).ToArray();
        ReplaceRows(SpaceRows, filtered);
    }

    private void RecordGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var file = (sender as DataGrid)?.SelectedItem as FileRecord;
        if (sender == SpaceGrid)
        {
            SpaceDetail.Text = file is null ? _text["detail.none"] : BuildRecordDetail(file);
        }
        else if (sender == MediaGrid)
        {
            MediaDetail.Text = file is null ? _text["detail.none"] : BuildRecordDetail(file);
            MediaPreview.Source = file is { IsMedia: true } ? _media.LoadPreview(file.FullPath) : null;
        }
    }

    private string BuildRecordDetail(FileRecord file)
    {
        var risk = _text[$"risk.{file.Risk.ToString().ToLowerInvariant()}"];
        return $"{file.FullPath}\n{file.SizeText} · {risk}\n{_text[file.ReasonKey]}";
    }

    private void RecordGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is FileRecord file)
            HandleShellResult(ShellService.TryOpenFile(file.FullPath));
    }

    private FileRecord? SelectedRecordFor(object sender)
    {
        if (sender == SpaceOpenButton || sender == SpaceLocateButton || sender == SpaceAddButton)
            return SpaceGrid.SelectedItem as FileRecord;
        if (sender == MediaOpenButton || sender == MediaLocateButton || sender == MediaAddButton)
            return MediaGrid.SelectedItem as FileRecord;
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        var file = SelectedRecordFor(sender);
        if (file is null) { StatusText.Text = _text["action.select_file"]; return; }
        HandleShellResult(ShellService.TryOpenFile(file.FullPath));
    }

    private void LocateSelected_Click(object sender, RoutedEventArgs e)
    {
        var file = SelectedRecordFor(sender);
        if (file is null) { StatusText.Text = _text["action.select_file"]; return; }
        HandleShellResult(ShellService.TryLocateFile(file.FullPath));
    }

    private void HandleShellResult(ShellActionResult result)
    {
        if (!result.Success) StatusText.Text = _text[result.MessageKey];
    }

    private async void MediaGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not FileRecord { IsMedia: true, Thumbnail: null } file) return;
        var image = await Task.Run(() => _media.LoadPreview(file.FullPath, 128));
        file.Thumbnail = image;
    }

    private async void CheckMedia_Click(object sender, RoutedEventArgs e)
    {
        if (MediaRows.Count == 0) { StatusText.Text = _text["media.scan_first"]; return; }
        _analysisCts?.Cancel(); _analysisCts = new CancellationTokenSource();
        if (!TryBeginOperation(BusyOperation.Analysis)) return;
        MediaCheckButton.IsEnabled = false; ScanProgress.IsIndeterminate = true;
        try
        {
            foreach (var file in MediaRows)
            {
                _analysisCts.Token.ThrowIfCancellationRequested();
                file.MediaStatus = await _media.CheckAsync(file, _analysisCts.Token) ? "media.ok" : "media.suspect";
                StatusText.Text = file.FullPath;
            }
        }
        catch (OperationCanceledException) { }
        finally { ScanProgress.IsIndeterminate = false; EndOperation(BusyOperation.Analysis); }
    }

    private async void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (!_index.Exists || _scanner.IsRunning) { StatusText.Text = _text["action.scan_first"]; return; }
        _analysisCts?.Cancel(); _analysisCts = new CancellationTokenSource();
        if (!TryBeginOperation(BusyOperation.Analysis)) return;
        FindDuplicatesButton.IsEnabled = false; ScanProgress.IsIndeterminate = true; DuplicateRows.Clear();
        var progress = new Progress<DuplicateProgress>(p => StatusText.Text = $"{p.Completed}/{p.Total} · {p.CurrentPath}");
        try
        {
            var groups = await _duplicates.FindExactAsync(_index.EnumerateRecords(), progress, _analysisCts.Token);
            ShowDuplicateGroups(groups, "duplicate.none", "duplicate.summary");
        }
        catch (OperationCanceledException) { StatusText.Text = _text["status.cancelled_analysis"]; }
        catch { StatusText.Text = _text["analysis.failed"]; }
        finally { ScanProgress.IsIndeterminate = false; EndOperation(BusyOperation.Analysis); }
    }

    private async void FindSimilarPhotos_Click(object sender, RoutedEventArgs e)
    {
        if (!_index.Exists || _scanner.IsRunning) { StatusText.Text = _text["action.scan_first"]; return; }
        _analysisCts?.Cancel(); _analysisCts = new CancellationTokenSource();
        if (!TryBeginOperation(BusyOperation.Analysis)) return;
        ScanProgress.IsIndeterminate = true;
        DuplicateRows.Clear();
        var progress = new Progress<DuplicateProgress>(p => StatusText.Text = $"{p.Completed}/{p.Total} · {p.CurrentPath}");
        try
        {
            var groups = await _media.FindSimilarImagesAsync(_index.EnumerateRecords(), progress, _analysisCts.Token);
            ShowDuplicateGroups(groups, "similar.none", "similar.summary");
        }
        catch (OperationCanceledException) { StatusText.Text = _text["status.cancelled_analysis"]; }
        catch { StatusText.Text = _text["analysis.failed"]; }
        finally { ScanProgress.IsIndeterminate = false; EndOperation(BusyOperation.Analysis); }
    }

    private void ShowDuplicateGroups(IReadOnlyList<DuplicateGroup> groups, string noneKey, string summaryKey)
    {
        var number = 1;
        foreach (var group in groups)
        {
            var first = true;
            foreach (var file in group.Files)
            {
                DuplicateRows.Add(new DuplicateRow { GroupNumber = number, File = file, KeepRecommended = first });
                first = false;
            }
            number++;
        }
        var reclaim = groups.Sum(x => x.ReclaimableBytes);
        DuplicateSummary.Text = groups.Count == 0
            ? _text[noneKey]
            : string.Format(_text[summaryKey], groups.Count, SizeFormatter.Format(reclaim));
    }
    private void DuplicateGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is DuplicateRow row)
            HandleShellResult(ShellService.TryOpenFile(row.FullPath));
    }
    private void LocateDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (DuplicateGrid.SelectedItem is DuplicateRow row) HandleShellResult(ShellService.TryLocateFile(row.FullPath));
        else StatusText.Text = _text["duplicate.select_row"];
    }
    private void SelectDuplicates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in DuplicateRows) row.IsSelected = !row.KeepRecommended;
        DuplicateGrid.Items.Refresh();
    }
    private void AddDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var rows = DuplicateRows.Where(x => x.IsSelected).Select(x => x.File).ToArray();
        if (rows.Length == 0) { StatusText.Text = _text["duplicate.select_first"]; return; }
        AddCleanupBatch(rows, "source.duplicate");
    }

    private void AddSelectedRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        var file = SelectedRecordFor(sender);
        if (file is null) { StatusText.Text = _text["action.select_file"]; return; }
        AddCleanupBatch([file], "source.manual");
    }

    private void AddCleanupBatch(IEnumerable<FileRecord> files, string source)
    {
        var added = 0; var blocked = 0; var duplicate = 0; var missing = 0;
        foreach (var file in files.DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file.FullPath)) { missing++; continue; }
            if (file.Risk == RiskLevel.Blocked) { blocked++; continue; }
            if (!_cleanupPaths.Add(file.FullPath)) { duplicate++; continue; }
            file.PropertyChanged += CleanupItem_PropertyChanged;
            CleanupRows.Add(new CleanupItem { File = file, SourceKey = source });
            added++;
        }
        RefreshCleanupSummary();
        StatusText.Text = string.Format(_text["cleanup.add_result"], added, duplicate, blocked, missing);
        if (added > 0 || duplicate > 0) ShowPage("CleanupPage");
    }

    private void CleanupItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileRecord.IsSelected)) RefreshCleanupSummary();
    }

    private void SelectSafe_Click(object sender, RoutedEventArgs e) { foreach (var x in CleanupRows) x.IsSelected = x.Risk == RiskLevel.Safe; CleanupGrid.Items.Refresh(); RefreshCleanupSummary(); }
    private void SelectAllowed_Click(object sender, RoutedEventArgs e) { foreach (var x in CleanupRows) x.IsSelected = x.Risk != RiskLevel.Blocked; CleanupGrid.Items.Refresh(); RefreshCleanupSummary(); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { foreach (var x in CleanupRows) x.IsSelected = false; CleanupGrid.Items.Refresh(); RefreshCleanupSummary(); }

    private void RemoveSelectedCleanup_Click(object sender, RoutedEventArgs e)
    {
        var rows = CleanupGrid.SelectedItems.Cast<CleanupItem>().ToArray();
        if (rows.Length == 0 && CleanupGrid.SelectedItem is CleanupItem single) rows = [single];
        if (rows.Length == 0) { StatusText.Text = _text["cleanup.select_row"]; return; }
        foreach (var item in rows)
        {
            item.File.PropertyChanged -= CleanupItem_PropertyChanged;
            item.File.IsSelected = false;
            _cleanupPaths.Remove(item.FullPath);
            CleanupRows.Remove(item);
        }
        CleanupDetail.Text = _text["detail.none"];
        RefreshCleanupSummary();
        StatusText.Text = string.Format(_text["cleanup.removed"], rows.Length);
    }

    private async void PermanentDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = CleanupRows.Where(x => x.IsSelected && x.Risk != RiskLevel.Blocked).ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = _text["cleanup.select_items"];
            return;
        }
        var bytes = selected.Sum(x => x.File.Size);
        var answer = MessageBox.Show(this, string.Format(_text["confirm.permanent"], selected.Length, SizeFormatter.Format(bytes)),
            _text["confirm.title"], MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        if (!TryBeginOperation(BusyOperation.Cleanup)) return;
        try
        {
        RecycleButton.IsEnabled = false; PermanentDeleteButton.IsEnabled = false; PermanentDeleteButton.Content = _text["cleanup.deleting"];
        ScanProgress.Minimum = 0; ScanProgress.Maximum = selected.Length; ScanProgress.Value = 0; ScanProgress.IsIndeterminate = false;
        IProgress<int> progress = new Progress<int>(done => { StatusText.Text = string.Format(_text["cleanup.progress"], done, selected.Length); ScanProgress.Value = done; });
        var results = await Task.Run(() =>
        {
            var output = new List<(CleanupItem Item, string ResultKey, bool Deleted)>();
            var done = 0;
            foreach (var item in selected)
            {
                if (!_risk.IsStillAllowed(item.File))
                {
                    output.Add((item, "cleanup.changed", false));
                }
                else
                {
                    try
                    {
                        if (File.Exists(item.FullPath)) File.Delete(item.FullPath);
                        output.Add((item, "cleanup.deleted", true));
                    }
                    catch (IOException) { output.Add((item, "cleanup.locked", false)); }
                    catch (UnauthorizedAccessException) { output.Add((item, "cleanup.delete_failed", false)); }
                    catch { output.Add((item, "cleanup.delete_failed", false)); }
                }
                progress.Report(++done);
            }
            return output;
        });
        foreach (var result in results)
        {
            HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = result.Item.FullPath, Size = result.Item.File.Size, Result = _text[result.ResultKey] });
            if (result.Deleted) { CleanupRows.Remove(result.Item); _cleanupPaths.Remove(result.Item.FullPath); }
        }
        RecycleButton.IsEnabled = true; PermanentDeleteButton.IsEnabled = true; PermanentDeleteButton.Content = _text["cleanup.permanent"]; ScanProgress.Value = 0; ScanProgress.Maximum = 1;
        RefreshCleanupSummary(); StatusText.Text = _text["status.ready"];
        }
        catch { StatusText.Text = _text["cleanup.operation_failed"]; }
        finally
        {
            PermanentDeleteButton.Content = _text["cleanup.permanent"];
            ScanProgress.IsIndeterminate = false;
            EndOperation(BusyOperation.Cleanup);
        }
    }

    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var selected = CleanupRows.Where(x => x.IsSelected && x.Risk != RiskLevel.Blocked).ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = _text["cleanup.select_items"];
            return;
        }
        var bytes = selected.Sum(x => x.File.Size);
        var answer = MessageBox.Show(this, string.Format(_text["confirm.recycle"], selected.Length, SizeFormatter.Format(bytes)),
            _text["confirm.title"], MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (!TryBeginOperation(BusyOperation.Cleanup)) return;
        try
        {

        RecycleButton.IsEnabled = false;
        ScanProgress.IsIndeterminate = true;
        var pending = selected.ToList();
        while (pending.Count > 0)
        {
            var locked = new List<LockedCleanupItem>();
            var eligible = new List<CleanupItem>();
            foreach (var item in pending)
            {
                if (!_risk.IsStillAllowed(item.File))
                {
                    HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = item.FullPath, Size = item.File.Size, Result = _text["cleanup.changed"] });
                    continue;
                }
                eligible.Add(item);
            }
            StatusText.Text = string.Format(_text["cleanup.progress"], 0, eligible.Count);
            var attempts = await Task.Run(() => _recycle.TryMoveToRecycleBinBatch(eligible.Select(x => x.FullPath).ToArray()));
            var completed = 0;
            foreach (var item in eligible)
            {
                var attempt = attempts.TryGetValue(item.FullPath, out var found)
                    ? found
                    : new RecycleAttempt(false, false, 0, []);
                if (attempt.Success)
                {
                    HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = item.FullPath, Size = item.File.Size, Result = _text["cleanup.recycled"] });
                    CleanupRows.Remove(item); _cleanupPaths.Remove(item.FullPath);
                }
                else locked.Add(new LockedCleanupItem(item, attempt));
                completed++;
                if (completed % 32 == 0 || completed == eligible.Count)
                    StatusText.Text = string.Format(_text["cleanup.progress"], completed, eligible.Count);
            }

            if (locked.Count == 0) break;
            var processes = locked.SelectMany(x => x.Attempt.LockingProcesses).DistinctBy(x => x.ProcessId).ToArray();
            var rows = locked.Select(x => new LockedFileRow(x.Item.FullPath,
                x.Attempt.LockingProcesses.Count == 0 ? _text["locked.unknown_process"] : string.Join(", ", x.Attempt.LockingProcesses.Select(p => $"{p.ApplicationName} (PID {p.ProcessId})")),
                $"0x{x.Attempt.ErrorCode:X}")).ToArray();
            var canClose = _recycle.CanCloseApplications(processes);
            var dialog = new LockedFilesWindow(_text, rows, canClose) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Choice == LockedFileChoice.Skip)
            {
                AddLockedHistory(locked); break;
            }

            if (dialog.Choice == LockedFileChoice.ScheduleOnRestart)
            {
                var schedule = MessageBox.Show(this, _text["locked.schedule_confirm"], _text["confirm.title"],
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (schedule != MessageBoxResult.Yes) { AddLockedHistory(locked); break; }
                StatusText.Text = _text["locked.processing"];
                var scheduled = await Task.Run(() => _restartCleanup.ScheduleDeleteOnNextRestart(locked.Select(x => x.Item.FullPath)));
                foreach (var item in locked)
                {
                    if (scheduled.Scheduled.Contains(item.Item.FullPath, StringComparer.OrdinalIgnoreCase))
                    {
                        HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = item.Item.FullPath, Size = item.Item.File.Size, Result = _text["cleanup.scheduled"] });
                        CleanupRows.Remove(item.Item); _cleanupPaths.Remove(item.Item.FullPath);
                    }
                    else HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = item.Item.FullPath, Size = item.Item.File.Size, Result = _text["locked.schedule_failed"] });
                }
                break;
            }

            if (dialog.Choice == LockedFileChoice.Retry)
            {
                var retryPaths = locked.Select(x => x.Item.FullPath).ToArray();
                var retryAttempts = await Task.Run(() => _recycle.TryMoveToRecycleBinBatch(retryPaths));
                var stillLocked = new List<LockedCleanupItem>();
                foreach (var previous in locked)
                {
                    if (retryAttempts.TryGetValue(previous.Item.FullPath, out var attempt) && attempt.Success)
                    {
                        HistoryRows.Insert(0, new HistoryItem
                        {
                            Time = DateTime.Now,
                            Path = previous.Item.FullPath,
                            Size = previous.Item.File.Size,
                            Result = _text["cleanup.recycled"]
                        });
                        CleanupRows.Remove(previous.Item);
                        _cleanupPaths.Remove(previous.Item.FullPath);
                    }
                    else stillLocked.Add(new(previous.Item, attempt ?? previous.Attempt));
                }
                pending = stillLocked.Select(x => x.Item).ToList();
                continue;
            }
            var forceClose = dialog.Choice == LockedFileChoice.ForceCloseApplicationsAndRetry;
            if (forceClose)
            {
                var names = string.Join("\n", processes.Select(x => $"• {x.ApplicationName} (PID {x.ProcessId})"));
                var force = MessageBox.Show(this, string.Format(_text["locked.force_confirm"], names), _text["confirm.title"],
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (force != MessageBoxResult.Yes) { AddLockedHistory(locked); break; }
            }
            var paths = locked.Select(x => x.Item.FullPath).ToArray();
            StatusText.Text = _text["locked.processing"];
            var retry = await Task.Run(() => _recycle.CloseRecycleAndRestart(paths, forceClose));
            if (!retry.ShutdownSucceeded)
            {
                MessageBox.Show(this, _text["locked.force_failed"], _text["locked.title"], MessageBoxButton.OK, MessageBoxImage.Warning);
                AddLockedHistory(locked); break;
            }
            if (!retry.RestartRequested)
                MessageBox.Show(this, _text["locked.restart_failed"], _text["locked.title"], MessageBoxButton.OK, MessageBoxImage.Information);

            var retryLocked = new List<LockedCleanupItem>();
            foreach (var previous in locked)
            {
                if (retry.Attempts.TryGetValue(previous.Item.FullPath, out var attempt) && attempt.Success)
                {
                    HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = previous.Item.FullPath, Size = previous.Item.File.Size, Result = _text["cleanup.recycled"] });
                    CleanupRows.Remove(previous.Item); _cleanupPaths.Remove(previous.Item.FullPath);
                }
                else retryLocked.Add(new(previous.Item, attempt ?? previous.Attempt));
            }
            pending = retryLocked.Select(x => x.Item).ToList();
        }
        RecycleButton.IsEnabled = true; ScanProgress.IsIndeterminate = false; RefreshCleanupSummary(); StatusText.Text = _text["status.ready"];
        }
        catch { StatusText.Text = _text["cleanup.operation_failed"]; }
        finally
        {
            ScanProgress.IsIndeterminate = false;
            EndOperation(BusyOperation.Cleanup);
        }
    }

    private void AddLockedHistory(IEnumerable<LockedCleanupItem> locked)
    {
        foreach (var item in locked)
            HistoryRows.Insert(0, new HistoryItem { Time = DateTime.Now, Path = item.Item.FullPath, Size = item.Item.File.Size, Result = _text["cleanup.locked"] });
    }

    private void CleanupGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupGrid.SelectedItem is CleanupItem item)
            CleanupDetail.Text = $"{item.FullPath}\n{item.SizeText} · {_text[$"risk.{item.Risk.ToString().ToLowerInvariant()}"]}\n{_text[item.ReasonKey]}";
    }

    private void RefreshCleanupSummary()
    {
        if (CleanupRows.Count == 0) { CleanupSummary.Text = _text["cleanup.empty"]; return; }
        var selected = CleanupRows.Where(x => x.IsSelected).ToArray();
        CleanupSummary.Text = string.Format(_text["cleanup.selected"], selected.Length, SizeFormatter.Format(selected.Sum(x => x.File.Size)));
    }

    private async Task RefreshAppsAsync()
    {
        AppsRefreshButton.IsEnabled = false;
        try
        {
            var rows = await Task.Run(_apps.GetInstalledApps);
            ReplaceRows(AppRows, rows);
            StatusText.Text = string.Format(_text["apps.loaded"], rows.Count);
        }
        catch { StatusText.Text = _text["apps.load_failed"]; }
        finally { AppsRefreshButton.IsEnabled = true; }
    }
    private async void RefreshApps_Click(object sender, RoutedEventArgs e) => await RefreshAppsAsync();
    private void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        if (AppsGrid.SelectedItem is not InstalledApp app) { StatusText.Text = _text["apps.select_first"]; return; }
        var answer = MessageBox.Show(this, string.Format(_text["apps.confirm_uninstall"], app.Name),
            _text["confirm.title"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var result = ShellService.TryLaunchUninstaller(app);
        if (!result.Success) { HandleShellResult(result); return; }
        _pendingUninstall = app;
        ResidualRows.Clear();
        StatusText.Text = _text["apps.uninstall_started"];
    }

    private async void CheckAppResiduals_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUninstall is null)
        {
            StatusText.Text = _text["residual.uninstall_first"];
            return;
        }
        if (!TryBeginOperation(BusyOperation.AppResidual)) return;

        AppsCheckResidualsButton.IsEnabled = false;
        try
        {
            var installed = await Task.Run(_apps.GetInstalledApps);
            ReplaceRows(AppRows, installed);
            if (_residuals.IsStillInstalled(_pendingUninstall, installed))
            {
                StatusText.Text = _text["residual.still_installed"];
                return;
            }

            var rows = await Task.Run(() => _residuals.Find(_pendingUninstall));
            ReplaceRows(ResidualRows, rows);
            StatusText.Text = rows.Count == 0
                ? _text["residual.none"]
                : string.Format(_text["residual.found"], rows.Count);
        }
        catch { StatusText.Text = _text["residual.scan_failed"]; }
        finally { EndOperation(BusyOperation.AppResidual); }
    }

    private async void RecycleAppResiduals_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUninstall is null) { StatusText.Text = _text["residual.uninstall_first"]; return; }
        var selected = ResidualRows.Where(x => x.IsSelected && x.CanRecycle).ToArray();
        if (selected.Length == 0) { StatusText.Text = _text["residual.select_folders"]; return; }

        var current = _residuals.Find(_pendingUninstall)
            .Where(x => x.CanRecycle)
            .Select(x => x.Location)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = selected.Select(x => x.Location).Where(current.Contains).ToArray();
        if (paths.Length == 0) { StatusText.Text = _text["residual.changed"]; return; }

        var answer = MessageBox.Show(this,
            string.Format(_text["residual.confirm_recycle"], paths.Length, _pendingUninstall.Name),
            _text["confirm.title"], MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (!TryBeginOperation(BusyOperation.AppResidual)) return;

        AppsRecycleResidualsButton.IsEnabled = false;
        try
        {
            var attempts = await Task.Run(() => _recycle.TryMoveToRecycleBinBatch(paths));
            var removed = 0;
            foreach (var row in selected)
            {
                if (!attempts.TryGetValue(row.Location, out var attempt) || !attempt.Success) continue;
                ResidualRows.Remove(row);
                removed++;
            }
            StatusText.Text = string.Format(_text["residual.recycled"], removed, paths.Length - removed);
        }
        catch { StatusText.Text = _text["residual.recycle_failed"]; }
        finally { EndOperation(BusyOperation.AppResidual); }
    }

    private void ChangeLanguage_Click(object sender, RoutedEventArgs e) => ShowLanguageDialog();

    private void RefreshDashboardSummary()
    {
        if (_fileCount == 0) { DashboardSummary.Text = _text["dashboard.empty"]; return; }
        DashboardSummary.Text = string.Format(_text["dashboard.summary"], _fileCount, SizeFormatter.Format(_totalBytes), SizeFormatter.Format(_safeBytes), _largeFileCount);
    }

    private void RefreshDrives()
    {
        ReplaceRows(DriveRows, _drives.GetAvailableDrives());
        ApplyBusyControls();
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear(); foreach (var row in rows) target.Add(row);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _scanner.Cancel(); _analysisCts?.Cancel(); _indexWriter?.Dispose(); _scanner.Dispose();
    }
}

internal sealed class TopFileCollector(int capacity)
{
    private readonly PriorityQueue<FileRecord, long> _queue = new();
    public void Add(FileRecord record)
    {
        _queue.Enqueue(record, record.Size);
        if (_queue.Count > capacity) _queue.Dequeue();
    }
    public void Clear() => _queue.Clear();
    public IReadOnlyList<FileRecord> LargestFirst() => _queue.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Size).ToArray();
}

internal sealed record LockedCleanupItem(CleanupItem Item, RecycleAttempt Attempt);
