using System.Diagnostics;
using System.Windows;
using System.Security.Principal;

namespace PrintGate.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--session-banner" && args[1].StartsWith("PrintGate-Banner-", StringComparison.Ordinal)
            && Guid.TryParseExact(args[1]["PrintGate-Banner-".Length..], "N", out _))
        {
            StudioBanner.Run(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] is "--auth-ui" or "--auth-ui-probe"
            && args[1].StartsWith("PrintGate-Auth-", StringComparison.Ordinal)
            && Guid.TryParseExact(args[1]["PrintGate-Auth-".Length..], "N", out _))
        {
            RunAuthentication(args[1], args[0] == "--auth-ui-probe");
            return;
        }
        var startupProbe = args.Length == 1 && args[0] == "--diagnose-startup";
        if (args.Length == 1 && args[0] is "--admin" or "--configure-local-admin")
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                MessageBox.Show("请使用 Windows 管理员账户，通过 Start-Admin.ps1 提权打开管理页面。", "需要管理员权限");
                return;
            }
            try
            {
                var viewer = new Application();
                viewer.Run(args[0] == "--configure-local-admin" ? new LocalAdministratorSetupWindow() : new AdminWindow());
            }
            catch { MessageBox.Show("管理页面启动失败，请检查 appsettings.json 配置和程序文件。", "PrintGate"); }
            return;
        }
        if (args.Length == 4 && args[0] == "--watchdog")
        {
            Watchdog(args[1], args[2], args[3]);
            return;
        }
        if (args.Length != 0 && !startupProbe) { Native.LockWorkStation(); return; }
        using var singleton = new Mutex(true, @"Local\PrintGate-" + Process.GetCurrentProcess().SessionId + (startupProbe ? "-Probe" : ""), out var created);
        if (!created) return;
        try
        {
            StartupDiagnostics.Write("starting");
            var suffix = Guid.NewGuid().ToString("N");
            var authName = "PrintGate-Auth-" + suffix;
            var authDesktop = Native.CreateDesktop(authName);
            try
            {
                StartupDiagnostics.Write("desktops-created");
                // STA/COM may already own hidden windows. Create the UI process ON its desktop
                // instead of trying to move its initialized main thread with SetThreadDesktop.
                using var ui = Native.StartOnDesktop(Environment.ProcessPath!, authName,
                    $"{(startupProbe ? "--auth-ui-probe" : "--auth-ui")} {authName}");
                if (startupProbe && !ui.WaitForExit(30000))
                {
                    ui.Kill();
                    throw new TimeoutException("Startup probe timed out.");
                }
                ui.WaitForExit();
                Environment.ExitCode = ui.ExitCode;
                StartupDiagnostics.Write(ui.ExitCode == 0 ? "ui-completed" : "ui-failed");
                if (!startupProbe) Native.LockWorkStation();
            }
            finally { Native.CloseDesktop(authDesktop); }
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write("startup-failed", error);
            Environment.ExitCode = 1;
            if (!startupProbe) Native.LockWorkStation();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunAuthentication(string authName, bool startupProbe)
    {
        try
        {
            var authDesktop = Native.OpenDesktopW(authName, 0, false, 0x01ff);
            if (authDesktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            if (Native.CurrentDesktopName() != authName) throw new InvalidOperationException("UI process desktop mismatch.");
            var suffix = authName["PrintGate-Auth-".Length..];
            var studioName = "PrintGate-Studio-" + suffix;
            var studioDesktop = Native.CreateDesktop(studioName);
            StartupDiagnostics.Write("ui-on-auth-desktop");
            if (!startupProbe)
            {
                StartTextInputServices();
            }
            var heartbeatName = @"Local\PrintGate-Heartbeat-" + suffix;
            using var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, heartbeatName);
            using var watchdog = startupProbe ? Process.GetCurrentProcess() : Native.StartOnDesktop(Environment.ProcessPath!, authName,
                $"--watchdog {Environment.ProcessId} {heartbeatName} {authName}");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) => { StartupDiagnostics.Write("dispatcher-failed", e.Exception); e.Handled = true; if (!startupProbe) Native.LockWorkStation(); Environment.Exit(1); };
            var window = new LoginWindow(authDesktop, studioDesktop, studioName, heartbeat, watchdog, startupProbe);
            StartupDiagnostics.Write("login-window-created");
            if (startupProbe)
                window.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        using var banner = new StudioBanner(studioName);
                        await banner.WaitReadyAsync();
                        await banner.ShowAsync(new PrintGate.Core.Identity("000000", "测试使用者"), Stopwatch.GetTimestamp());
                        await Task.Delay(1200);
                        await banner.HideAsync();
                        StartupDiagnostics.Write("session-banner-show-hide-passed");
                        StartupDiagnostics.Write(window.InitializationSucceeded ? "probe-rendered-ready" : "probe-initialization-failed");
                        app.Shutdown(window.InitializationSucceeded ? 0 : 1);
                    }
                    catch (Exception error)
                    {
                        StartupDiagnostics.Write("session-banner-probe-failed", error);
                        app.Shutdown(1);
                    }
                };
            window.Show();
            if (!startupProbe && !Native.SwitchDesktop(authDesktop)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            Environment.ExitCode = app.Run();
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write("startup-failed", error);
            Environment.ExitCode = 1;
            // Never dump HTTP exception objects, which may contain credentials or ticket URLs.
            if (!startupProbe) Native.LockWorkStation();
        }
    }

    private static void StartTextInputServices()
    {
        try
        {
            // ctfmon has a UIAccess manifest. Direct CreateProcess fails with 740 for
            // standard users; let Windows Shell activation handle its signed manifest.
            using var inputService = Process.Start(new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "ctfmon.exe"),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            StartupDiagnostics.Write("text-input-service-started");
        }
        catch (Exception error) { StartupDiagnostics.Write("text-input-service-start-failed", error); }
    }

    private static void Watchdog(string parentId, string eventName, string desktopName)
    {
        try
        {
            using var parent = Process.GetProcessById(int.Parse(parentId));
            using var heartbeat = EventWaitHandle.OpenExisting(eventName);
            while (!parent.HasExited && heartbeat.WaitOne(TimeSpan.FromSeconds(12))) { }
        }
        catch { }
        var desktop = Native.OpenDesktopW(desktopName, 0, false, 0x0100);
        // A process watchdog is not a privileged Windows service or a tamper-proof boundary.
        // Keep requesting OS lock until an administrator signs out this failed session.
        while (true)
        {
            if (desktop != IntPtr.Zero) Native.SwitchDesktop(desktop);
            Native.LockWorkStation();
            Thread.Sleep(1000);
        }
    }
}
