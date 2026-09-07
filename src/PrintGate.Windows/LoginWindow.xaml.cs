using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintGate.Core;

namespace PrintGate.Windows;

public partial class LoginWindow : Window
{
    private readonly IntPtr authDesktop, studioDesktop;
    private readonly string studioDesktopName;
    private readonly EventWaitHandle heartbeat;
    private readonly Process watchdog;
    private readonly DispatcherTimer timer;
    private Settings? settings;
    private AuditStore? audit;
    private SessionCoordinator? session;
    private IAuthenticator? authenticator;
    private MouseMonitor? mouse;
    private MouseMonitor? adminMouse;
    private AdminWindow? adminWindow;
    private Process? studio;
    private bool busy, faulted;
    private int authenticationEpoch;
    private ScreenRecorder? recorder;
    private CancellationTokenSource? captureCancellation;
    private bool recordingStopping, cleanupBusy;
    private long lastCleanup;

    internal LoginWindow(IntPtr authDesktop, IntPtr studioDesktop, string studioDesktopName,
        EventWaitHandle heartbeat, Process watchdog)
    {
        this.authDesktop = authDesktop;
        this.studioDesktop = studioDesktop;
        this.studioDesktopName = studioDesktopName;
        this.heartbeat = heartbeat;
        this.watchdog = watchdog;
        InitializeComponent();
        PasswordInput.PasswordChanged += (_, _) => PasswordHint.Visibility =
            PasswordInput.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Closing += (_, e) => e.Cancel = true;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, Tick, Dispatcher);
        timer.Start();
        heartbeat.Set();
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
                or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                Dispatcher.BeginInvoke(() => ReturnToAuthentication("windows_session_locked"));
        };
        try
        {
            settings = Settings.Load();
            DeviceLabel.Text = settings.ComputerId;
            IdleLabel.Text = $"登录后录制打印桌面操作（不录音），录像保留 7 天；空间不足时优先删除最早录像。\n鼠标连续 {settings.IdleSeconds} 秒未移动将重新锁定。\n系统记录姓名、学号、组织及使用时间，不保存密码。管理员认证后进入记录页，组织可留空。";
            var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintGate", "Data", "audit.db");
            audit = new AuditStore(dataPath);
            audit.RecoverInterrupted();
            session = new SessionCoordinator(audit, settings.ComputerId);
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), MaxResponseContentBufferSize = 128 * 1024 };
            authenticator = new CasAuthenticator(client, new Uri(settings.CasBaseUrl), settings.CasService);
            mouse = new MouseMonitor(studioDesktop);
            recorder = new ScreenRecorder(settings, studioDesktopName);
            CleanupRecordings();
            foreach (var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.StudioPath)))
            {
                using (existing)
                    if (existing.SessionId == Process.GetCurrentProcess().SessionId)
                        throw new InvalidOperationException("检测到已有 Studio 进程。请管理员注销此 Windows 会话后重新进入，避免接管未确认的工程。");
            }
        }
        catch (InvalidOperationException e) { Fault(e.Message); }
        catch { Fault("初始化失败，请管理员检查配置及日志目录的写入权限。"); }
        Loaded += (_, _) => AccountBox.Focus();
    }

    private async void LoginClicked(object sender, RoutedEventArgs e)
    {
        if (busy || faulted || recordingStopping || recorder is null || recorder.HasProcess || session is null || authenticator is null || settings is null || audit is null) return;
        busy = true;
        LoginButton.IsEnabled = false;
        AccountBox.IsEnabled = false;
        PasswordInput.IsEnabled = false;
        OrganizationBox.IsEnabled = false;
        StatusLabel.Text = "正在验证身份，请稍候…";
        var password = PasswordInput.Password;
        var epoch = authenticationEpoch;
        PasswordInput.Clear();
        try
        {
            var identity = await authenticator.AuthenticateAsync(AccountBox.Text.Trim(), password, CancellationToken.None);
            if (faulted || epoch != authenticationEpoch) return;
            if (AdministratorAccess.IsAllowed(identity, settings.AdministratorStudentIds))
            {
                audit.Record(null, "admin_view_opened");
                AccountBox.Clear(); OrganizationBox.Clear();
                adminMouse ??= new MouseMonitor(authDesktop);
                adminMouse.Resume();
                adminWindow = new AdminWindow(kioskMode: true, administratorName: identity.Name + " / " + identity.StudentId) { Owner = this };
                try { adminWindow.ShowDialog(); }
                finally
                {
                    adminWindow = null; adminMouse.Pause();
                    audit.Record(null, "admin_view_closed");
                    StatusLabel.Text = "管理员已退出，请重新认证。";
                }
                return;
            }
            string organization;
            try { organization = Organization.Normalize(OrganizationBox.Text); }
            catch (ArgumentException error) { StatusLabel.Text = error.Message; return; }
            if (!session.CanEnter(identity))
            {
                audit.Record(null, "retained_owner_mismatch");
                StatusLabel.Text = "上次工程仍在保留中，请原操作人认证后保存并关闭 Studio，或联系管理员。";
                return;
            }
            session.Enter(identity, organization);
            if (!Native.SwitchDesktop(studioDesktop)) throw new InvalidOperationException("无法进入打印桌面。");
            captureCancellation?.Dispose();
            captureCancellation = new CancellationTokenSource();
            await recorder.StartAsync(session.SessionId!, captureCancellation.Token);
            if (faulted || epoch != authenticationEpoch) return;
            audit.Record(session.SessionId, "recording_started");
            if (studio is null || studio.HasExited)
            {
                studio?.Dispose();
                studio = Native.StartOnDesktop(settings.StudioPath, studioDesktopName);
                audit.Record(session.SessionId, "studio_started");
            }
            else audit.Record(session.SessionId, "studio_resumed");
            mouse!.Resume();
            AccountBox.Clear();
            OrganizationBox.Clear();
            StatusLabel.Text = "打印会话进行中，正在录屏。";
        }
        catch (AuthenticationException error)
        {
            StatusLabel.Text = error.Message;
            try { audit.Record(null, "authentication_failed"); }
            catch { Fault("无法保存操作日志，已禁止进入。请联系管理员。"); }
        }
        catch (OperationCanceledException) when (epoch != authenticationEpoch) { }
        catch
        {
            ReturnToAuthentication("launch_or_audit_failure");
            Fault("启动或记录失败，已收回操作权限。请联系管理员。");
        }
        finally
        {
            password = string.Empty; // No persistent credentials; managed strings cannot be reliably zeroed.
            busy = false;
            SetAuthenticationInputs();
            PasswordInput.Focus();
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        heartbeat.Set();
        if (!cleanupBusy && Environment.TickCount64 - lastCleanup >= 30000) CleanupRecordings();
        if (watchdog.HasExited) { Fault("故障监控已停止，请管理员重新启动会话。"); Native.LockWorkStation(); return; }
        if (adminWindow is not null)
        {
            if (adminMouse is null || adminMouse.Failed || Environment.TickCount64 - adminMouse.LastMovement >= settings!.IdleSeconds * 1000L)
                ReturnToAuthentication("admin_idle_timeout");
            return;
        }
        if (session is null || !session.IsAuthorized)
        {
            // Retry if a secure Windows desktop temporarily prevented switching.
            Native.SwitchDesktop(authDesktop);
            if (studio is not null && studio.HasExited)
            {
                try { session?.Lock("studio_closed_while_locked", false); }
                catch { Fault("日志写入失败，请联系管理员。"); }
                studio.Dispose(); studio = null;
            }
            return;
        }
        if (busy) return; // Waiting for the encoder's first frame before launching Studio.
        if (recorder is null || !recorder.Healthy) { Fault("录屏中断，已锁定。请管理员检查录屏组件或磁盘。"); return; }
        if (studio is null || studio.HasExited) ReturnToAuthentication("studio_closed");
        else if (mouse!.Failed) ReturnToAuthentication("input_desktop_unavailable");
        else if (Environment.TickCount64 - mouse.LastMovement >= settings!.IdleSeconds * 1000L)
            ReturnToAuthentication("mouse_idle_timeout");
    }

    private async void ReturnToAuthentication(string reason)
    {
        authenticationEpoch++;
        adminWindow?.Close();
        adminMouse?.Pause();
        captureCancellation?.Cancel();
        if (recordingStopping) return;
        recordingStopping = true;
        SetAuthenticationInputs();
        PasswordInput.Clear();
        AccountBox.Clear();
        OrganizationBox.Clear();
        mouse?.Pause();
        var switched = Native.SwitchDesktop(authDesktop); // Hide access before writing logs.
        if (!switched) Native.LockWorkStation();
        var recordingSession = session?.SessionId;
        try
        {
            session?.Lock(reason, studio is not null && !studio.HasExited);
            if (!faulted) StatusLabel.Text = "已锁定，正在保存录像…";
            if (recorder is not null) await recorder.StopAsync();
            if (recordingSession is not null) audit?.Record(recordingSession, "recording_stopped");
            if (!faulted) StatusLabel.Text = session?.RetainedOwner is null
                ? "本次使用已结束，请重新认证。"
                : "已锁定，工程仍保留。请原操作人重新认证后继续。";
        }
        catch
        {
            faulted = true;
            StatusLabel.Text = "已锁定，但录像或日志保存失败。请管理员处理。";
            // Even a log failure must not leave capture running indefinitely.
            try { if (recorder is not null) await recorder.StopAsync("interrupted"); } catch { Native.LockWorkStation(); }
        }
        finally { recordingStopping = false; SetAuthenticationInputs(); }
    }

    private void Fault(string message)
    {
        authenticationEpoch++;
        faulted = true;
        ReturnToAuthentication("application_fault");
        StatusLabel.Text = message;
    }

    private void SetAuthenticationInputs()
    {
        var enabled = !faulted && !busy && !recordingStopping && recorder?.HasProcess != true && session?.IsAuthorized != true;
        LoginButton.IsEnabled = AccountBox.IsEnabled = PasswordInput.IsEnabled = OrganizationBox.IsEnabled = enabled;
    }

    private async void CleanupRecordings()
    {
        if (recorder is null || cleanupBusy) return;
        cleanupBusy = true;
        lastCleanup = Environment.TickCount64;
        try
        {
            var result = await recorder.CleanupAsync();
            if (result.Deleted > 0) audit?.Record(null, "old_recordings_cleaned");
            if (!result.HasSpace && session?.IsAuthorized == true)
                Fault("已清理可删除的旧录像，磁盘空间仍不足，已锁定。请管理员处理。");
        }
        catch { Fault("无法检查录像保存空间，已禁止继续使用。请管理员处理。"); }
        finally { cleanupBusy = false; }
    }

    private void MaintenanceClicked(object sender, RoutedEventArgs e)
    {
        ReturnToAuthentication("maintenance_requested");
        Native.LockWorkStation();
    }

    private void ArtworkSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ArtworkImage?.Source is not BitmapSource image || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        var scale = Math.Max(e.NewSize.Width / image.PixelWidth, e.NewSize.Height / image.PixelHeight);
        ArtworkImage.Width = image.PixelWidth * scale;
        ArtworkImage.Height = image.PixelHeight * scale;
        Canvas.SetLeft(ArtworkImage, (e.NewSize.Width - ArtworkImage.Width) * 0.5);
        Canvas.SetTop(ArtworkImage, (e.NewSize.Height - ArtworkImage.Height) * 0.8);
    }
}
