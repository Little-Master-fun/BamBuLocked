using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintGate.Core;

namespace PrintGate.Windows;

public partial class AdminWindow : Window
{
    private readonly AuditReader reader;
    private readonly bool kioskMode;
    private readonly string? administratorName;
    private readonly string recordingDirectory;
    private readonly DispatcherTimer refreshTimer;
    private AuditFilter filter = new();
    private SessionView view = SessionView.All;
    private int page = 1, generation;
    private long total;
    private bool loading, exporting;
    private const int PageSize = 20;

    public AdminWindow(AuditFilter? initialFilter = null, bool kioskMode = false, string? administratorName = null)
    {
        InitializeComponent();
        this.kioskMode = kioskMode; this.administratorName = administratorName;
        if (kioskMode)
        {
            WindowState = WindowState.Maximized;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ExportButton.Visibility = Visibility.Collapsed;
        }
        AdminIdentity.Text = administratorName ?? WindowsIdentity.GetCurrent().Name;
        // Reading records must not require Studio or FFmpeg to be installed/running.
        var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))) ?? new Settings();
        if (!Path.IsPathFullyQualified(config.RecordingsDirectory)) throw new InvalidOperationException("录像目录配置无效。");
        recordingDirectory = config.RecordingsDirectory;
        reader = new AuditReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintGate", "Data", "audit.db"));
        filter = initialFilter ?? new(Overlap: true);
        view = filter.View;
        SearchBox.Text = filter.Keyword;
        FromDate.SelectedDate = filter.From?.LocalDateTime.Date;
        FromTime.Text = filter.From?.LocalDateTime.ToString("HH:mm:ss") ?? "00:00:00";
        UntilDate.SelectedDate = filter.Until?.AddSeconds(-1).LocalDateTime.Date;
        UntilTime.Text = filter.Until?.AddSeconds(-1).LocalDateTime.ToString("HH:mm:ss") ?? "23:59:59";
        OverlapCheck.IsChecked = filter.Overlap;
        if (filter.StudentId is not null) Title = "使用记录与录像 · 学号 " + filter.StudentId;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        refreshTimer.Tick += async (_, _) => { if (AutoRefresh.IsChecked == true && !loading && !exporting) await RefreshAsync(); };
        Loaded += async (_, _) => { refreshTimer.Start(); await RefreshAsync(); };
        Closed += (_, _) => { generation++; refreshTimer.Stop(); };
    }

    private AuditFilter ReadFilter()
    {
        static DateTimeOffset? Boundary(DateTime? date, string time, bool end)
        {
            if (!date.HasValue) return null;
            if (!TimeOnly.TryParseExact(time.Trim(), "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var clock))
                throw new ArgumentException("时间请输入 HH:mm:ss，例如 14:30:00。");
            var local = DateTime.SpecifyKind(date.Value.Date + clock.ToTimeSpan(), DateTimeKind.Local);
            return new DateTimeOffset(end ? local.AddSeconds(1) : local).ToUniversalTime();
        }
        var from = Boundary(FromDate.SelectedDate, FromTime.Text, false);
        var until = Boundary(UntilDate.SelectedDate, UntilTime.Text, true);
        if (from.HasValue && until.HasValue && from >= until) throw new ArgumentException("结束时间不能早于开始时间。");
        return new(SearchBox.Text.Trim(), from, until, view, filter.StudentId, OverlapCheck.IsChecked == true);
    }
    private void PeopleClicked(object sender, RoutedEventArgs e)
    {
        try { new PeopleWindow(reader, ReadFilter(), kioskMode, administratorName) { Owner = this }.ShowDialog(); }
        catch (ArgumentException error) { StatusLabel.Text = error.Message; }
    }
    private void TimePopupClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var query = ReadFilter();
            if (query.From is null || query.Until is null) throw new ArgumentException("请填写开始、结束日期与时间；查询某一秒时将两者设为相同时间。");
            new AdminWindow(query, kioskMode, administratorName) { Owner = this, Title = "时间查询结果 · 使用记录与录像" }.ShowDialog();
        }
        catch (ArgumentException error) { StatusLabel.Text = error.Message; }
    }

    private async Task RefreshAsync()
    {
        var version = ++generation;
        var selectedFilter = filter; var selectedPage = page;
        loading = true; StatusLabel.Text = "正在读取记录…";
        try
        {
            var result = await Task.Run(() => reader.Query(selectedFilter, selectedPage, PageSize));
            if (version != generation) return;
            total = result.Summary.Sessions;
            if (page > 1 && (long)(page - 1) * PageSize >= total) { page = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)); await RefreshAsync(); return; }
            SessionsGrid.ItemsSource = result.Rows;
            SessionCount.Text = total.ToString(); UserCount.Text = result.Summary.Users.ToString();
            UnfinishedCount.Text = result.Summary.Unfinished.ToString(); AbnormalCount.Text = result.Summary.Abnormal.ToString();
            EmptyLabel.Visibility = result.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyLabel.Text = result.DatabaseExists ? "当前筛选下没有记录" : "尚未发现使用记录，请先部署并使用工作站";
            ResultLabel.Text = $"共 {total} 条 · 每页 {PageSize} 条";
            PageLabel.Text = $"{page} / {Math.Max(1, (int)Math.Ceiling(total / (double)PageSize))}";
            PreviousButton.IsEnabled = page > 1; NextButton.IsEnabled = (long)page * PageSize < total;
            StatusLabel.Text = $"更新于 {DateTime.Now:HH:mm:ss} · 录像保留 7 天，空间不足时可能提前清理。";
            foreach (var tab in new[] { AllTab, RecordingTab, UnfinishedTab, AbnormalTab })
            {
                var selected = (string)tab.Tag == view.ToString();
                tab.Background = new SolidColorBrush(selected ? Color.FromRgb(145,184,193) : Colors.White);
                tab.Foreground = new SolidColorBrush(selected ? Colors.White : Color.FromRgb(82,123,134));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (version != generation) return;
            SessionsGrid.ItemsSource = null; total = 0;
            SessionCount.Text = UserCount.Text = UnfinishedCount.Text = AbnormalCount.Text = "—";
            PreviousButton.IsEnabled = NextButton.IsEnabled = false;
            ResultLabel.Text = "记录读取失败";
            EmptyLabel.Visibility = Visibility.Visible; EmptyLabel.Text = "无法读取记录";
            StatusLabel.Text = "请检查数据库权限、格式或文件占用情况，修复后点击刷新。";
        }
        finally { if (version == generation) loading = false; }
    }
    private async void SearchClicked(object sender, RoutedEventArgs e)
    {
        try { filter = ReadFilter(); page = 1; await RefreshAsync(); }
        catch (ArgumentException error) { StatusLabel.Text = error.Message; }
    }
    private void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SearchClicked(sender, e); }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear(); FromDate.SelectedDate = UntilDate.SelectedDate = null; FromTime.Text = "00:00:00"; UntilTime.Text = "23:59:59"; OverlapCheck.IsChecked = false;
        view = SessionView.All; SearchClicked(sender, e);
    }
    private void TabClicked(object sender, RoutedEventArgs e)
    {
        view = Enum.Parse<SessionView>((string)((Button)sender).Tag); SearchClicked(sender, e);
    }
    private async void PreviousClicked(object sender, RoutedEventArgs e) { if (page > 1) { page--; await RefreshAsync(); } }
    private async void NextClicked(object sender, RoutedEventArgs e) { if ((long)page * PageSize < total) { page++; await RefreshAsync(); } }
    private void DetailClicked(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is AuditSession item) ShowDetail(item); }
    private void RowDoubleClicked(object sender, MouseButtonEventArgs e) { if (SessionsGrid.SelectedItem is AuditSession item) ShowDetail(item); }
    private void ShowDetail(AuditSession item) => new SessionDetailWindow(reader, recordingDirectory, item, kioskMode) { Owner = this }.ShowDialog();
    private void ExitClicked(object sender, RoutedEventArgs e) => Close();

    private async void SystemEventsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var events = await Task.Run(() => reader.Events(null, filter));
            var table = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = events, Margin = new Thickness(18), CanUserAddRows = false };
            table.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new System.Windows.Data.Binding("TimeLabel"), Width = 175 });
            table.Columns.Add(new DataGridTextColumn { Header = "事件", Binding = new System.Windows.Data.Binding("Description"), Width = 220 });
            table.Columns.Add(new DataGridTextColumn { Header = "会话编号", Binding = new System.Windows.Data.Binding("SessionId"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            new Window { Owner = this, Title = "系统事件 · 按当前日期范围 · 最近 500 条", Width = 850, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = table }.ShowDialog();
        }
        catch { StatusLabel.Text = "无法读取系统事件，请检查数据库。"; }
    }
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (exporting || loading) return;
        var dialog = new SaveFileDialog { Title = "导出当前筛选的全部记录", Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"PrintGate-使用记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        exporting = true; ExportButton.IsEnabled = false;
        var selectedFilter = filter;
        try
        {
            var count = await Task.Run(() =>
            {
                var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    long count;
                    using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true))) count = reader.Export(selectedFilter, writer);
                    File.Move(temporary, dialog.FileName, true); return count;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            StatusLabel.Text = $"已导出 {count} 条记录到 {dialog.FileName}（全部筛选结果）。";
        }
        catch { StatusLabel.Text = "导出失败，请检查数据库和目标目录权限。"; }
        finally { exporting = false; ExportButton.IsEnabled = true; }
    }
}
