using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PrintGate.Core;

namespace PrintGate.Windows;

internal sealed record BannerState(string Name, string StudentId, long StartedTimestamp, bool Visible);

// WPF windows must be created by a process already attached to the Studio desktop.
internal sealed class StudioBanner : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process process;
    private sealed record UpdateRequest(BannerState State, TaskCompletionSource Applied);
    private readonly Channel<UpdateRequest> updates = Channel.CreateUnbounded<UpdateRequest>();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool failed;
    internal bool Healthy => !failed && !process.HasExited;

    internal StudioBanner(string desktop)
    {
        var name = "PrintGate-Banner-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { process = Native.StartOnDesktop(Environment.ProcessPath!, desktop, $"--session-banner {name}"); }
        catch { pipe.Dispose(); throw; }
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        UpdateRequest? current = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            if (await reader.ReadLineAsync(timeout.Token) != "ready") throw new IOException("Banner did not initialize.");
            ready.TrySetResult();
            await foreach (var update in updates.Reader.ReadAllAsync())
            {
                current = update;
                await writer.WriteLineAsync(JsonSerializer.Serialize(update.State));
                var acknowledgment = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (acknowledgment != (update.State.Visible ? "shown" : "hidden")) throw new IOException("Banner update was not applied.");
                update.Applied.TrySetResult();
                current = null;
            }
        }
        catch (Exception error)
        {
            failed = true;
            ready.TrySetException(error);
            current?.Applied.TrySetException(error);
            updates.Writer.TryComplete(error);
            while (updates.Reader.TryRead(out var pending)) pending.Applied.TrySetException(error);
            pipe.Dispose();
            StartupDiagnostics.Write("session-banner-failed", error);
        }
    }

    internal Task WaitReadyAsync() => ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    internal Task ShowAsync(Identity identity, long timestamp) => ApplyAsync(new(identity.Name, identity.StudentId, timestamp, true));
    internal Task HideAsync() => ApplyAsync(new("", "", 0, false));
    internal void Hide() => _ = HideAsync().ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    private Task ApplyAsync(BannerState state)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!updates.Writer.TryWrite(new(state, completion))) completion.TrySetException(new IOException("Banner is unavailable."));
        return completion.Task;
    }
    public void Dispose()
    {
        updates.Writer.TryComplete();
        pipe.Dispose();
        process.Dispose();
    }

    internal static void Run(string pipeName)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new SessionBannerWindow();
        _ = ReceiveAsync();
        app.Run();
        async Task ReceiveAsync()
        {
            try
            {
                using var connection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await connection.ConnectAsync(10000);
                using var writer = new StreamWriter(connection, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(connection, leaveOpen: true);
                await writer.WriteLineAsync("ready");
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Length > 4096) throw new IOException("Invalid banner state.");
                    var state = JsonSerializer.Deserialize<BannerState>(line) ?? throw new IOException("Missing banner state.");
                    await app.Dispatcher.InvokeAsync(() => window.Update(state));
                    await writer.WriteLineAsync(state.Visible ? "shown" : "hidden");
                }
            }
            catch (Exception error) { StartupDiagnostics.Write("session-banner-disconnected", error); }
            finally { await app.Dispatcher.InvokeAsync(() => app.Shutdown()); }
        }
    }
}

internal sealed class SessionBannerWindow : Window
{
    private readonly TextBlock identityText = new(), timeText = new(), toastText = new();
    private readonly Border toast;
    private readonly Border bar;
    private readonly DispatcherTimer timer;
    private BannerState? state;
    private long shownAt;
    private readonly bool toastOnly;

    internal SessionBannerWindow(bool toastOnly = false)
    {
        this.toastOnly = toastOnly;
        Title = "PrintGate · 当前使用者";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false;
        IsHitTestVisible = false; Focusable = false;
        Width = Math.Min(760, SystemParameters.WorkArea.Width - 24);
        SizeToContent = SizeToContent.Height;
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 8;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        var layout = new StackPanel();
        var row = new DockPanel();
        timeText.Foreground = Brushes.White; timeText.Margin = new Thickness(20, 0, 0, 0);
        DockPanel.SetDock(timeText, Dock.Right); row.Children.Add(timeText);
        identityText.Foreground = Brushes.White; identityText.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(identityText);
        bar = new Border { Background = new SolidColorBrush(Color.FromRgb(35, 67, 76)), CornerRadius = new CornerRadius(13), Padding = new Thickness(20, 12, 20, 12), Child = row };
        toastText.Foreground = new SolidColorBrush(Color.FromRgb(27, 91, 68));
        toastText.TextWrapping = TextWrapping.Wrap; toastText.TextAlignment = TextAlignment.Center;
        toast = new Border { Background = new SolidColorBrush(Color.FromRgb(231, 249, 239)), BorderBrush = new SolidColorBrush(Color.FromRgb(153, 214, 179)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 12, 20, 12), Margin = new Thickness(30, 10, 30, 0), Child = toastText };
        layout.Children.Add(bar); layout.Children.Add(toast); Content = layout;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(handle, -20, new IntPtr(GetWindowLongPtrW(handle, -20).ToInt64() | 0x08000000 | 0x20));
        };
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        timer.Stop();
        Closed += (_, _) => timer.Stop();
    }

    internal void Update(BannerState value)
    {
        state = value;
        if (!value.Visible) { timer.Stop(); identityText.Text = timeText.Text = toastText.Text = ""; Hide(); return; }
        identityText.Text = $"{value.Name}   ·   学号 {value.StudentId}";
        toastText.Text = $"✓  登录成功　{value.Name}　{value.StudentId}";
        bar.Visibility = toastOnly ? Visibility.Collapsed : Visibility.Visible;
        toast.Visibility = Visibility.Visible;
        shownAt = Stopwatch.GetTimestamp(); Refresh(); Show(); timer.Start();
    }

    private void Refresh()
    {
        if (state is null) return;
        var elapsed = Stopwatch.GetElapsedTime(state.StartedTimestamp);
        timeText.Text = $"使用时间  {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (Stopwatch.GetElapsedTime(shownAt).TotalSeconds >= 5)
        {
            if (toastOnly) Close(); else toast.Visibility = Visibility.Collapsed;
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}
