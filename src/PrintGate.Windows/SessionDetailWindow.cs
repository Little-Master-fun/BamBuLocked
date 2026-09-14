using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PrintGate.Core;

namespace PrintGate.Windows;

public sealed class SessionDetailWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly DataGrid videos = Table();
    private readonly DataGrid events = Table();
    private readonly RecordingIndex index;
    private readonly AuditSession session;
    private readonly bool kioskMode;

    public SessionDetailWindow(AuditReader reader, string directory, AuditSession session, bool kioskMode = false)
    {
        this.session = session; this.kioskMode = kioskMode; index = new RecordingIndex(directory);
        Title = "使用详情 · " + session.Name; Width = 1000; Height = 760;
        MinWidth = 800; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PrintGate;component/AdminTheme.xaml", UriKind.Relative) });
        FontFamily = new FontFamily("Microsoft YaHei UI"); Foreground = new SolidColorBrush(Color.FromRgb(33,61,66));
        Background = new SolidColorBrush(Color.FromRgb(244,247,247));
        var root = new DockPanel { Margin = new Thickness(28) }; Content = root;
        var heading = new StackPanel();
        var headingCard = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(0,0,0,18), Child = heading };
        DockPanel.SetDock(headingCard, Dock.Top); root.Children.Add(headingCard);
        heading.Children.Add(new TextBlock { Text = session.Name + "  /  " + session.StudentId, FontSize = 25, Foreground = new SolidColorBrush(Color.FromRgb(82,123,134)) });
        heading.Children.Add(new TextBlock { Text = $"组织：{session.OrganizationLabel}    电脑：{session.ComputerId}\n开始：{session.StartedLabel}    结束：{session.EndedLabel}\n时长：{session.DurationLabel}    状态：{session.ReasonLabel}\n会话：{session.SessionId}", TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 25, Margin = new Thickness(0,12,0,16) });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var open = new Button { Content = "播放选中录像", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0) };
        videos.SelectionChanged += (_,_) => open.IsEnabled = videos.SelectedItem is IndexedRecording { CanOpen: true };
        videos.MouseDoubleClick += (_,e) => { if (open.IsEnabled) OpenClicked(open,e); };
        open.Click += OpenClicked; footer.Children.Add(open); footer.Children.Add(status);
        var tabs = new TabControl { BorderThickness = new Thickness(0), Background = Brushes.White, Padding = new Thickness(12) }; root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "关联录像", Content = videos });
        tabs.Items.Add(new TabItem { Header = "事件记录 · 最近 500 条", Content = events });
        Column(videos,"录制开始","StartedLabel",190); Column(videos,"状态","StatusLabel",170); Column(videos,"大小","SizeLabel",100); Column(videos,"录像编号","Id",280);
        Column(events,"时间","TimeLabel",190); Column(events,"事件","Description",240); Column(events,"事件代码","Type",280);
        Loaded += async (_, _) =>
        {
            status.Text = "正在读取详情…";
            try
            {
                events.ItemsSource = await Task.Run(() => reader.Events(session.SessionId, new()));
                var result = await Task.Run(() => index.ForSession(session.SessionId));
                videos.ItemsSource = result.Recordings;
                status.Text = result.Recordings.Count == 0
                    ? (session.HasRecordingEvent ? "曾录屏，当前未找到录像；可能已按保留策略清理。" : "该会话没有关联录像。")
                    : "选择录像后点击播放，或双击直接打开。未结束或已清理的录像无法播放。";
                if (result.Skipped > 0) status.Text += $" 录像目录中有 {result.Skipped} 个无法读取的元数据文件。";
            }
            catch { status.Text = "详情读取失败，请检查数据库、录像目录配置及访问权限。"; }
        };
    }
    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        if (videos.SelectedItem is not IndexedRecording selected) { status.Text = "请先选择一条录像。"; return; }
        try
        {
            var path = index.ResolveForPlayback(session.SessionId, selected.Id);
            if (kioskMode)
            {
                var player = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
                var panel = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(24,43,48)) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
                DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(player);
                var play = new Button { Content = "播放", Padding = new Thickness(20,10,20,10) };
                var pause = new Button { Content = "暂停", Padding = new Thickness(20,10,20,10) };
                var close = new Button { Content = "关闭录像", Padding = new Thickness(20,10,20,10) };
                buttons.Children.Add(play); buttons.Children.Add(pause); buttons.Children.Add(close);
                var window = new Window { Owner = this, Title = "使用录像", Width = 1000, Height = 700, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PrintGate;component/AdminTheme.xaml", UriKind.Relative) });
                play.Click += (_,_) => player.Play(); pause.Click += (_,_) => player.Pause(); close.Click += (_,_) => window.Close();
                player.MediaFailed += (_,_) => { status.Text = "内置播放器无法解码此录像，请由 Windows 管理员在桌面管理模式中打开。"; window.Close(); };
                window.Loaded += (_,_) => player.Play(); window.Closed += (_,_) => { player.Stop(); player.Close(); player.Source = null; };
                window.ShowDialog();
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            status.Text = "已请求 Windows 默认播放器打开录像。";
        }
        catch { status.Text = "无法打开：录像未结束、已清理、无访问权限，或未安装支持 MKV 的播放器。"; }
    }
    private static DataGrid Table() => new();
    private static void Column(DataGrid grid, string title, string binding, double width) => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = width });
}
