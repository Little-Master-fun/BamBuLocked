using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
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
    private LoginAuthenticator? authenticator;
    private MouseMonitor? mouse;
    private MouseMonitor? adminMouse;
    private AdminWindow? adminWindow;
    private Process? studio;
    private StudioBanner? studioBanner;
    private long studioLaunchedAt;
    private bool busy, faulted;
    private int authenticationEpoch;
    private ScreenRecorder? recorder;
    private CancellationTokenSource? captureCancellation;
    private bool recordingStopping, cleanupBusy;
    private long lastCleanup;
    private readonly bool startupProbe;
    internal bool InitializationSucceeded => !faulted;

    internal LoginWindow(IntPtr authDesktop, IntPtr studioDesktop, string studioDesktopName,
        EventWaitHandle heartbeat, Process watchdog, bool startupProbe = false)
    {
        this.startupProbe = startupProbe;
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
        if (!startupProbe) timer.Start();
        heartbeat.Set();
        if (!startupProbe) SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
                or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                Dispatcher.BeginInvoke(() => ReturnToAuthentication("windows_session_locked"));
        };
        try
        {
            settings = Settings.Load();
            DeviceLabel.Text = settings.ComputerId;
            IdleLabel.Text = $"使用期间会进行操作录屏。\n鼠标连续 {settings.IdleSeconds} 秒未移动，将自动关闭操作页面并返回登录页。";
            var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintGate", "Data", "audit.db");
            audit = new AuditStore(dataPath);
            if (!startupProbe) audit.RecoverInterrupted();
            session = new SessionCoordinator(audit, settings.ComputerId);
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), MaxResponseContentBufferSize = 128 * 1024 };
            authenticator = new LoginAuthenticator(new CasAuthenticator(client, new Uri(settings.CasBaseUrl), settings.CasService), LocalAdministratorSettings.Load(), CampusCredentialStore.Load());
            mouse = new MouseMonitor(studioDesktop);
            recorder = new ScreenRecorder(settings, studioDesktopName);
            if (!startupProbe) CleanupRecordings();
            // The diagnostic mode never takes ownership of Studio or starts it.
            if (!startupProbe) foreach (var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.StudioPath)))
            {
                using (existing)
                    if (existing.SessionId == Process.GetCurrentProcess().SessionId)
                        throw new InvalidOperationException("检测到已有 Studio 进程。请管理员注销此 Windows 会话后重新进入，避免接管未确认的工程。");
            }
            if (!startupProbe)
            {
                studioBanner = new StudioBanner(studioDesktopName);
                EnsureStudioPreloaded();
            }
        }
        catch (InvalidOperationException e) { StartupDiagnostics.Write("login-initialization-failed", e); Fault(e.Message); }
        catch (Exception e) { StartupDiagnostics.Write("login-initialization-failed", e); Fault("初始化失败，请管理员检查配置及日志目录的写入权限。"); }
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
        LocalAdminLogin.IsEnabled = false;
        StatusLabel.Text = "正在验证身份，请稍候…";
        var password = PasswordInput.Password;
        var epoch = authenticationEpoch;
        PasswordInput.Clear();
        try
        {
            var login = await authenticator.AuthenticateAsync(AccountBox.Text.Trim(), password, LocalAdminLogin.IsChecked == true, CancellationToken.None);
            var identity = login.Identity;
            if (faulted || epoch != authenticationEpoch) return;
            if (login.IsLocalAdministrator || AdministratorAccess.IsAllowed(identity, settings.AdministratorStudentIds))
            {
                audit.Record(null, "admin_view_opened");
                AccountBox.Clear(); OrganizationBox.Clear();
                adminMouse ??= new MouseMonitor(authDesktop);
                adminMouse.Resume();
                adminWindow = new AdminWindow(kioskMode: true, administratorName: login.IsLocalAdministrator ? "本地管理员 / " + identity.Name : identity.Name + " / " + identity.StudentId) { Owner = this };
                var adminToast = new SessionBannerWindow(toastOnly: true);
                adminToast.Update(new BannerState(identity.Name, login.IsLocalAdministrator ? "本地管理员" : identity.StudentId, Stopwatch.GetTimestamp(), true));
                try { adminWindow.ShowDialog(); }
                finally
                {
                    adminToast.Close();
                    adminWindow = null; adminMouse.Pause();
                    LocalAdminLogin.IsChecked = false;
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
            EnsureStudioPreloaded();
            if (studioBanner is null) throw new InvalidOperationException("使用者信息栏未准备就绪。");
            await studioBanner.WaitReadyAsync();
            if (faulted || epoch != authenticationEpoch) return;
            var resuming = session.RetainedOwner is not null;
            session.Enter(identity, organization);
            await studioBanner.ShowAsync(identity, Stopwatch.GetTimestamp());
            if (faulted || epoch != authenticationEpoch) return;
            if (!Native.SwitchDesktop(studioDesktop)) throw new InvalidOperationException("无法进入打印桌面。");
            captureCancellation?.Dispose();
            captureCancellation = new CancellationTokenSource();
            await recorder.StartAsync(session.SessionId!, captureCancellation.Token);
            if (faulted || epoch != authenticationEpoch) return;
            audit.Record(session.SessionId, "recording_started");
            if (studio is null || studio.HasExited) throw new InvalidOperationException("预启动的 Bambu 已退出。");
            audit.Record(session.SessionId, resuming ? "studio_resumed" : "studio_preloaded_opened");
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
                if (!faulted && Environment.TickCount64 - studioLaunchedAt < 5000)
                {
                    Fault("Bambu 预启动后立即退出，请管理员检查软件配置。");
                    return;
                }
                try { session?.Lock("studio_closed_while_locked", false); }
                catch { Fault("日志写入失败，请联系管理员。"); }
                studio.Dispose(); studio = null;
            }
            if (!faulted && !busy && !recordingStopping)
            {
                try { EnsureStudioPreloaded(); }
                catch { Fault("无法提前启动 Bambu，请管理员检查软件路径和运行权限。"); }
            }
            return;
        }
        if (busy) return; // The prestarted Studio is ready; wait for recording startup to finish.
        if (studioBanner?.Healthy != true) { Fault("使用者信息栏已退出，请管理员重新启动会话。"); return; }
        if (recorder is null || !recorder.Healthy) { Fault("录屏中断，已锁定。请管理员检查录屏组件或磁盘。"); return; }
        if (studio is null || studio.HasExited) ReturnToAuthentication("studio_closed");
        else if (mouse!.Failed) ReturnToAuthentication("input_desktop_unavailable");
        else if (Environment.TickCount64 - mouse.LastMovement >= settings!.IdleSeconds * 1000L)
            ReturnToAuthentication("mouse_idle_timeout");
    }

    private async void ReturnToAuthentication(string reason)
    {
        authenticationEpoch++;
        studioBanner?.Hide();
        adminWindow?.Close();
        adminMouse?.Pause();
        captureCancellation?.Cancel();
        if (recordingStopping) return;
        recordingStopping = true;
        SetAuthenticationInputs();
        PasswordInput.Clear();
        AccountBox.Clear();
        OrganizationBox.Clear();
        LocalAdminLogin.IsChecked = false;
        mouse?.Pause();
        var switched = Native.SwitchDesktop(authDesktop); // Hide access before writing logs.
        if (!switched) Native.LockWorkStation();
        var recordingSession = session?.SessionId;
        try
        {
            var forceCloseStudio = reason == "mouse_idle_timeout";
            if (forceCloseStudio) ForceCloseStudioForIdleTimeout(recordingSession);
            session?.Lock(reason, !forceCloseStudio && studio is not null && !studio.HasExited);
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
        finally
        {
            recordingStopping = false;
            if (!faulted && !startupProbe)
            {
                try { EnsureStudioPreloaded(); }
                catch { faulted = true; StatusLabel.Text = "无法提前启动 Bambu，请管理员检查软件配置。"; }
            }
            SetAuthenticationInputs();
        }
    }

    private void ForceCloseStudioForIdleTimeout(string? recordingSession)
    {
        if (studio is null) return;
        try
        {
            if (!studio.HasExited)
            {
                // The user has already lost access to the Studio desktop. End the whole
                // Bambu process tree before the next prelaunch, so a stuck project cannot
                // be inherited by the next authenticated user.
                studio.Kill(entireProcessTree: true);
                if (!studio.WaitForExit(10000))
                    throw new TimeoutException("Bambu did not exit after the idle timeout.");
            }
            audit?.Record(recordingSession, "studio_forced_closed_timeout");
            studio.Dispose();
            studio = null;
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write("studio-force-close-failed", error);
            throw new InvalidOperationException("超时后无法关闭 Bambu，已停止后续使用，请管理员检查。", error);
        }
    }

    private void EnsureStudioPreloaded()
    {
        if (startupProbe || faulted || settings is null) return;
        if (studio is not null && !studio.HasExited) return;
        // Never replace an existing user's unsaved project. A closed Studio is prestarted
        // on its hidden desktop while the next user is still on the authentication page.
        studio?.Dispose();
        studio = Native.StartOnDesktop(settings.StudioPath, studioDesktopName);
        studioLaunchedAt = Environment.TickCount64;
        audit?.Record(null, "studio_prestarted");
    }

    private void Fault(string message)
    {
        authenticationEpoch++;
        faulted = true;
        if (!startupProbe) ReturnToAuthentication("application_fault");
        StatusLabel.Text = message;
    }

    private void OrganizationFocused(object sender, KeyboardFocusChangedEventArgs e) => ApplyOrganizationInputLanguage();

    private void ApplyOrganizationInputLanguage()
    {
        try
        {
            var manager = InputLanguageManager.Current;
            var language = manager.AvailableInputLanguages.Cast<CultureInfo>()
                .FirstOrDefault(x => x.Name == "zh-CN")
                ?? manager.AvailableInputLanguages.Cast<CultureInfo>().FirstOrDefault(x => x.TwoLetterISOLanguageName == "zh");
            if (language is null)
            {
                StatusLabel.Text = "当前 Windows 账户没有可用的中文输入法，请管理员添加微软拼音。";
                return;
            }
            if (language is not null)
            {
                InputLanguageManager.SetInputLanguage(OrganizationBox, language);
                manager.CurrentInputLanguage = language;
            }
            InputMethod.SetIsInputMethodEnabled(OrganizationBox, true);
            InputMethod.SetPreferredImeState(OrganizationBox, InputMethodState.On);
            InputMethod.SetPreferredImeConversionMode(OrganizationBox, ImeConversionModeValues.Native);
            InputMethod.Current.ImeState = InputMethodState.On;
            InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write("organization-input-switch-failed", error);
            StatusLabel.Text = "输入法切换失败，请尝试 Shift 或 Ctrl+空格，或联系管理员。";
        }
    }

    private void SetAuthenticationInputs()
    {
        var enabled = !faulted && !busy && !recordingStopping && recorder?.HasProcess != true && session?.IsAuthorized != true;
        LoginButton.IsEnabled = AccountBox.IsEnabled = PasswordInput.IsEnabled = LocalAdminLogin.IsEnabled = enabled;
        OrganizationBox.IsEnabled = enabled && LocalAdminLogin.IsChecked != true;
    }

    private void AdministratorModeChanged(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        AccountBox.Tag = LocalAdminLogin.IsChecked == true ? "本地管理员用户名" : "学号 / 统一认证账号";
        if (LocalAdminLogin.IsChecked == true) OrganizationBox.Clear();
        SetAuthenticationInputs();
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
