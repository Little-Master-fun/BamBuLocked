using System.Diagnostics;
using PrintGate.Core;

namespace PrintGate.Windows;

internal sealed class ScreenRecorder(Settings settings, string desktop)
{
    private readonly RecordingStorage storage = new(settings.RecordingsDirectory, settings.RecordingRetentionDays,
        settings.RecordingMinimumFreeSpaceMb * 1024L * 1024L);
    private readonly SemaphoreSlim storageGate = new(1, 1);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private RecorderJob? job;
    private Process? process;
    private StreamWriter? input;
    private StreamReader? output;
    private FileStream? lease;
    private RecordingMetadata? metadata;
    private Task? outputPump;
    private long lastProgress;
    public bool HasProcess => process is not null;
    public bool Healthy => process is not null && !process.HasExited && Environment.TickCount64 - Interlocked.Read(ref lastProgress) < 15000;

    public async Task<RecordingCleanupResult> CleanupAsync()
    {
        await storageGate.WaitAsync();
        try { return await Task.Run(() => storage.Cleanup(DateTimeOffset.UtcNow)); }
        finally { storageGate.Release(); }
    }

    public async Task StartAsync(string sessionId, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try { await StartCoreAsync(sessionId, cancellationToken); }
        finally { lifecycle.Release(); }
    }

    private async Task StartCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (process is not null) throw new InvalidOperationException("上一段录像尚未结束。");
        var cleanup = await CleanupAsync();
        if (!cleanup.HasSpace) throw new IOException("清理旧录像后空间仍不足。");
        cancellationToken.ThrowIfCancellationRequested();
        await storageGate.WaitAsync();
        try { (metadata, lease) = storage.Begin(sessionId); }
        finally { storageGate.Release(); }
        try
        {
            var path = storage.VideoPath(metadata.Id);
            var args = "-hide_banner -loglevel error -nostats -stats_period 1 -progress pipe:1 " +
                "-f gdigrab -framerate 10 -draw_mouse 1 -i desktop -an " +
                "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -c:v libx264 -preset ultrafast -crf 28 " +
                "-pix_fmt yuv420p -flush_packets 1 -f matroska -n \"" + path + "\"";
            job = new RecorderJob();
            (process, input, output) = Native.StartRecorder(settings.RecorderExecutable, desktop, args, job.Handle);
            Interlocked.Exchange(ref lastProgress, Environment.TickCount64);
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = output;
            outputPump = Task.Run(async () =>
            {
                try
                {
                    while (await stream.ReadLineAsync() is { } line)
                    {
                        if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6), out var frame) && frame > 0)
                        {
                            Interlocked.Exchange(ref lastProgress, Environment.TickCount64);
                            firstFrame.TrySetResult();
                        }
                    }
                }
                catch (IOException) { }
                finally { firstFrame.TrySetException(new IOException("录屏组件未输出画面。")); }
            });
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (process.HasExited) throw new IOException("录屏组件已退出。");
        }
        catch { await StopCoreAsync("start_failed"); throw; }
    }

    public async Task StopAsync(string status = "completed")
    {
        await lifecycle.WaitAsync();
        try { await StopCoreAsync(status); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync(string status)
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                try { if (input is not null) await input.WriteLineAsync("q"); }
                catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    status = "interrupted";
                    process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            if (process.ExitCode != 0) status = "interrupted";
            // Never release the lease or allow credential entry while capture may still be running.
            process.Dispose(); process = null;
        }
        job?.Dispose(); job = null;
        input?.Dispose(); input = null;
        if (outputPump is not null) await outputPump;
        output?.Dispose(); output = null; outputPump = null;
        await storageGate.WaitAsync();
        try
        {
            if (metadata is not null) storage.Finish(metadata, status);
        }
        finally
        {
            metadata = null; lease?.Dispose(); lease = null;
            storageGate.Release();
        }
    }
}
