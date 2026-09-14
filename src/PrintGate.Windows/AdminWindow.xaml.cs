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
    private readonly string recordingDirectory;
    private readonly DispatcherTimer refreshTimer;
    private AuditFilter filter = new();
    private SessionView view = SessionView.All;
    private enum Section { Records, People, Events }
    private Section section;
    private int page = 1, generation;
    private long total;
    private bool loading, exporting;
    private const int PageSize = 20;

    public AdminWindow(AuditFilter? initialFilter = null, bool kioskMode = false, string? administratorName = null)
    {
        InitializeComponent();
        this.kioskMode = kioskMode;
        if (kioskMode) { WindowState = WindowState.Maximized; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; }
        AdminIdentity.Text = administratorName ?? WindowsIdentity.GetCurrent().Name;
        var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))) ?? new Settings();
        if (!Path.IsPathFullyQualified(config.RecordingsDirectory)) throw new InvalidOperationException("录像目录配置无效。");
        recordingDirectory = config.RecordingsDirectory;
        reader = new AuditReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintGate", "Data", "audit.db"));
        filter = initialFilter ?? new(); view = filter.View;
        SyncInputs(); UpdateNavigation();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        refreshTimer.Tick += async (_, _) => { if (AutoRefresh.IsChecked == true && !loading && !exporting && OwnedWindows.Count == 0) await RefreshAsync(); };
        Loaded += async (_, _) => { refreshTimer.Start(); await RefreshAsync(); };
        Closed += (_, _) => { generation++; refreshTimer.Stop(); };
    }
    private void SyncInputs()
    {
        SearchBox.Text = filter.Keyword;
        FromDate.SelectedDate = filter.At?.LocalDateTime.Date;
        FromTime.Text = filter.At?.LocalDateTime.ToString("HH:mm:ss") ?? "00:00:00";
    }
    private AuditFilter ReadFilter()
    {
        DateTimeOffset? at = null;
        if (FromDate.SelectedDate.HasValue)
        {
            if (!TimeOnly.TryParseExact(FromTime.Text.Trim(), "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var clock))
                throw new ArgumentException("时间格式不正确，请输入时:分:秒，例如 14:30:00。");
            at = new DateTimeOffset(DateTime.SpecifyKind(FromDate.SelectedDate.Value.Date + clock.ToTimeSpan(), DateTimeKind.Local)).ToUniversalTime();
        }
        return new(SearchBox.Text.Trim(), View: view, StudentId: filter.StudentId, At: at);
    }
    private void UpdateNavigation()
    {
        static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        SessionsGrid.Visibility = Visible(section == Section.Records);
        PeopleGrid.Visibility = Visible(section == Section.People);
        EventsGrid.Visibility = Visible(section == Section.Events);
        RecordTabs.Visibility = Visible(section == Section.Records);
        TableCaption.Visibility = Visible(section != Section.Records);
        StatsPanel.Visibility = Visible(section != Section.Events);
        KeywordPanel.Visibility = Visible(section != Section.Events);
        Pagination.Visibility = Visible(section != Section.Events);
        ExportButton.Visibility = Visible(!kioskMode && section == Section.Records);
        PageTitle.Text = section switch { Section.People => "使用人员", Section.Events => "系统事件", _ => "操作记录" };
        PageSubtitle.Text = section switch { Section.People => "按学号汇总使用频次，查看每个人的使用历史", Section.Events => "查看认证、录屏与工作站运行事件", _ => "查看每一次使用，以及对应的操作录像" };
        TableCaption.Text = section == Section.People ? "人员按使用次数排序" : "按发生时间倒序 · 最多 500 条";
        QueryButton.Content = section == Section.Events ? "查询事件" : section == Section.People ? "查询人员" : "查询记录";
        ClearPersonButton.Visibility = Visible(filter.StudentId is not null);
        FilterHint.Text = filter.StudentId is not null ? "当前人员：" + filter.StudentId + " · 可清除限定返回全部人员。" : section == Section.Events ? "不选日期查看全部时间；输入时刻查询该秒内的事件。" : "不选日期查看全部记录；输入一个时刻，查找当时所属的使用区间。";
        foreach (var (button, target) in new[] { (RecordsNav, Section.Records), (PeopleNav, Section.People), (EventsNav, Section.Events) })
        {
            button.Background = new SolidColorBrush(target == section ? Colors.White : Colors.Transparent);
            button.Foreground = new SolidColorBrush(target == section ? Color.FromRgb(45,91,97) : Color.FromRgb(232,242,242));
        }
        foreach (var tab in new[] { AllTab, RecordingTab, UnfinishedTab, AbnormalTab })
        {
            var selected = (string)tab.Tag == view.ToString();
            tab.Background = new SolidColorBrush(selected ? Color.FromRgb(232,242,240) : Colors.White);
            tab.Foreground = new SolidColorBrush(selected ? Color.FromRgb(52,111,113) : Color.FromRgb(112,134,138));
        }
    }
    private async Task RefreshAsync()
    {
        var version = ++generation; var selectedFilter = filter; var selectedPage = page; var selectedSection = section;
        loading = true; StatusLabel.Text = "正在读取记录…"; PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var audit = selectedSection == Section.Events ? null : reader.Query(selectedFilter, selectedPage, PageSize);
                var people = selectedSection == Section.People ? reader.People(selectedFilter, selectedPage, PageSize) : null;
                var events = selectedSection == Section.Events ? reader.Events(null, selectedFilter) : null;
                return (audit, people, events);
            });
            if (version != generation) return;
            total = result.people?.Total ?? result.events?.Count ?? result.audit!.Summary.Sessions;
            if (page > 1 && (long)(page-1)*PageSize >= total) { page = Math.Max(1,(int)Math.Ceiling(total/(double)PageSize)); await RefreshAsync(); return; }
            SessionsGrid.ItemsSource = result.audit?.Rows; PeopleGrid.ItemsSource = result.people?.Rows; EventsGrid.ItemsSource = result.events;
            if (result.audit is { } audit)
            {
                SessionCount.Text = audit.Summary.Sessions.ToString(); UserCount.Text = audit.Summary.Users.ToString();
                UnfinishedCount.Text = audit.Summary.Unfinished.ToString(); AbnormalCount.Text = audit.Summary.Abnormal.ToString();
            }
            EmptyLabel.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyLabel.Text = "没有匹配的记录\n试试清除时间或人员条件";
            ResultLabel.Text = (filter.At is { } at ? $"{at.LocalDateTime:yyyy-MM-dd HH:mm:ss} · " : "全部时间 · ") + $"共 {total} " + (section == Section.People ? "人" : "条");
            PageLabel.Text = $"{page} / {Math.Max(1,(int)Math.Ceiling(total/(double)PageSize))}";
            PreviousButton.IsEnabled = page > 1; NextButton.IsEnabled = (long)page*PageSize < total;
            StatusLabel.Text = $"更新于 {DateTime.Now:HH:mm:ss} · 统计对应当前查询。未结束不代表在线；录像可能因保留期限或磁盘空间提前清理。";
            UpdateNavigation();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (version != generation) return;
            SessionsGrid.ItemsSource = PeopleGrid.ItemsSource = EventsGrid.ItemsSource = null; total = 0;
            SessionCount.Text = UserCount.Text = UnfinishedCount.Text = AbnormalCount.Text = "—";
            EmptyLabel.Visibility = Visibility.Visible; EmptyLabel.Text = "暂时无法读取记录\n请检查数据文件权限后刷新重试";
            ResultLabel.Text = "读取失败"; StatusLabel.Text = "数据库可能被占用、损坏或无访问权限。";
        }
        finally { if (version == generation) loading = false; }
    }
    private async void SearchClicked(object sender, RoutedEventArgs e)
    {
        try { var nextFilter = ReadFilter(); ValidationLabel.Visibility = Visibility.Collapsed; filter = nextFilter; page = 1; await RefreshAsync(); }
        catch (ArgumentException error) { ValidationLabel.Text = error.Message; ValidationLabel.Visibility = Visibility.Visible; FromTime.Focus(); }
    }
    private void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SearchClicked(sender,e); }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        filter = new(); view = SessionView.All; SyncInputs(); SearchClicked(sender,e);
    }
    private async void TabClicked(object sender, RoutedEventArgs e)
    {
        view = Enum.Parse<SessionView>((string)((Button)sender).Tag); filter = filter with { View = view }; page = 1; UpdateNavigation(); await RefreshAsync();
    }
    private async Task Navigate(Section target)
    {
        section = target; page = 1; view = SessionView.All;
        filter = filter with { View = view, StudentId = null }; ValidationLabel.Visibility = Visibility.Collapsed;
        SyncInputs(); UpdateNavigation(); await RefreshAsync();
    }
    private async void RecordsClicked(object sender, RoutedEventArgs e) => await Navigate(Section.Records);
    private async void PeopleClicked(object sender, RoutedEventArgs e) => await Navigate(Section.People);
    private async void SystemEventsClicked(object sender, RoutedEventArgs e) => await Navigate(Section.Events);
    private async void ClearPersonClicked(object sender, RoutedEventArgs e) { filter = filter with { StudentId = null }; page = 1; await RefreshAsync(); }
    private async Task PersonRecords(AuditPerson person, bool all)
    {
        filter = all ? new(StudentId: person.StudentId) : filter with { StudentId = person.StudentId, View = SessionView.All };
        section = Section.Records; view = SessionView.All; page = 1; SyncInputs(); UpdateNavigation(); await RefreshAsync();
    }
    private async void PersonClicked(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is AuditPerson p) await PersonRecords(p,false); }
    private async void PersonAllClicked(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is AuditPerson p) await PersonRecords(p,true); }
    private async void PersonDoubleClicked(object sender, MouseButtonEventArgs e) { if (PeopleGrid.SelectedItem is AuditPerson p) await PersonRecords(p,false); }
    private async void PreviousClicked(object sender, RoutedEventArgs e) { if (!loading && page > 1) { page--; await RefreshAsync(); } }
    private async void NextClicked(object sender, RoutedEventArgs e) { if (!loading && (long)page*PageSize < total) { page++; await RefreshAsync(); } }
    private void DetailClicked(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is AuditSession item) ShowDetail(item); }
    private void RowDoubleClicked(object sender, MouseButtonEventArgs e) { if (SessionsGrid.SelectedItem is AuditSession item) ShowDetail(item); }
    private void ShowDetail(AuditSession item) => new SessionDetailWindow(reader,recordingDirectory,item,kioskMode) { Owner = this }.ShowDialog();
    private void ExitClicked(object sender, RoutedEventArgs e) => Close();
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
