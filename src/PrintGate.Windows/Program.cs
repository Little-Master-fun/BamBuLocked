using System.Diagnostics;
using System.Windows;
using System.Security.Principal;

namespace PrintGate.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--admin")
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
                viewer.Run(new AdminWindow());
            }
            catch { MessageBox.Show("管理页面启动失败，请检查 appsettings.json 配置和程序文件。", "PrintGate"); }
            return;
        }
        if (args.Length == 4 && args[0] == "--watchdog")
        {
            Watchdog(args[1], args[2], args[3]);
            return;
        }
        if (args.Length != 0) { Native.LockWorkStation(); return; }
        using var singleton = new Mutex(true, @"Local\PrintGate-" + Process.GetCurrentProcess().SessionId, out var created);
        if (!created) return;
        try
        {
            var suffix = Guid.NewGuid().ToString("N");
            var authName = "PrintGate-Auth-" + suffix;
            var studioName = "PrintGate-Studio-" + suffix;
            var authDesktop = Native.CreateDesktop(authName);
            var studioDesktop = Native.CreateDesktop(studioName);
            if (!Native.SetThreadDesktop(authDesktop)) throw new InvalidOperationException("无法创建认证桌面。");
            var heartbeatName = @"Local\PrintGate-Heartbeat-" + suffix;
            using var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, heartbeatName);
            using var watchdog = Native.StartOnDesktop(Environment.ProcessPath!, authName,
                $"--watchdog {Environment.ProcessId} {heartbeatName} {authName}");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Native.LockWorkStation(); Environment.Exit(1); };
            var window = new LoginWindow(authDesktop, studioDesktop, studioName, heartbeat, watchdog);
            window.Show();
            if (!Native.SwitchDesktop(authDesktop)) throw new InvalidOperationException("无法进入认证桌面。");
            app.Run();
        }
        catch
        {
            // Never dump HTTP exception objects, which may contain credentials or ticket URLs.
            Native.LockWorkStation();
        }
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
