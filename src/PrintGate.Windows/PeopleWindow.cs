using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PrintGate.Core;

namespace PrintGate.Windows;

public sealed class PeopleWindow : Window
{
    private readonly AuditReader reader;
    private readonly bool kioskMode;
    private readonly string? administratorName;
    private readonly AuditFilter filter;
    private readonly DataGrid table = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, RowHeight = 48, GridLinesVisibility = DataGridGridLinesVisibility.None, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(239,246,247)) };
    private readonly TextBlock status = new() { Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button previous = new() { Content = "上一页", Padding = new Thickness(16,8,16,8) };
    private readonly Button next = new() { Content = "下一页", Padding = new Thickness(16,8,16,8) };
    private int page = 1;
    public PeopleWindow(AuditReader reader, AuditFilter filter, bool kioskMode = false, string? administratorName = null)
    {
        this.reader = reader; this.filter = filter; this.kioskMode = kioskMode; this.administratorName = administratorName;
        Title = "使用人员与频次 · 当前筛选范围"; Width = 1080; Height = 700; MinWidth = 800; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var heading = new TextBlock { Text = "使用人员与频次\n按学号汇总，按次数排序；频次为当前筛选内会话数。姓名显示最近一次记录。", FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(65,101,109)), Margin = new Thickness(0,0,0,20) };
        DockPanel.SetDock(heading,Dock.Top); root.Children.Add(heading);
        var actions = new WrapPanel { Margin = new Thickness(0,12,0,0) }; DockPanel.SetDock(actions,Dock.Bottom); root.Children.Add(actions);
        var records = new Button { Content = "查看选中人员的筛选记录 / 视频", Padding = new Thickness(12,8,12,8), Background = new SolidColorBrush(Color.FromRgb(145,184,193)) };
        var all = new Button { Content = "查看该人员全部历史 / 视频", Padding = new Thickness(12,8,12,8), Margin = new Thickness(8,0,16,0) };
        actions.Children.Add(records); actions.Children.Add(all); actions.Children.Add(previous); actions.Children.Add(status); actions.Children.Add(next);
        records.Click += (_,_) => OpenRecords(false); all.Click += (_,_) => OpenRecords(true);
        table.MouseDoubleClick += (_,_) => OpenRecords(false);
        previous.Click += async (_,_) => { page--; await Refresh(); }; next.Click += async (_,_) => { page++; await Refresh(); };
        foreach (var col in new[] { ("姓名","Name",150d),("学号","StudentId",160d),("使用次数","Uses",100d),("首次使用","FirstUsed",220d),("最近使用","LastUsed",220d) })
            table.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2), Width = col.Item3 });
        root.Children.Add(table); Loaded += async (_,_) => await Refresh();
    }
    private async Task Refresh()
    {
        previous.IsEnabled = next.IsEnabled = false; status.Text = "正在读取…";
        try
        {
            var result = await Task.Run(() => reader.People(filter,page)); table.ItemsSource = result.Rows;
            status.Text = $"共 {result.Total} 人 · {page}/{Math.Max(1,(int)Math.Ceiling(result.Total/20d))}";
            previous.IsEnabled = page > 1; next.IsEnabled = (long)page*20 < result.Total;
        }
        catch { table.ItemsSource = null; status.Text = "读取失败，请关闭后重新查询。"; }
    }
    private void OpenRecords(bool all)
    {
        if (table.SelectedItem is not AuditPerson person) { status.Text = "请先选择人员。"; return; }
        var selected = all ? new AuditFilter(StudentId: person.StudentId) : filter with { StudentId = person.StudentId };
        new AdminWindow(selected, kioskMode, administratorName) { Owner = this }.ShowDialog();
    }
}
