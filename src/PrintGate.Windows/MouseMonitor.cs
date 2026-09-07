namespace PrintGate.Windows;

// This worker is attached to the Studio desktop, not the WPF authentication desktop.
internal sealed class MouseMonitor
{
    private long lastMovement = Environment.TickCount64;
    private int enabled, failed, attachFailed;
    public long LastMovement => Interlocked.Read(ref lastMovement);
    public bool Failed => Volatile.Read(ref failed) != 0 || Volatile.Read(ref attachFailed) != 0;
    public void Pause() => Volatile.Write(ref enabled, 0);
    public void Resume()
    {
        Interlocked.Exchange(ref lastMovement, Environment.TickCount64);
        Volatile.Write(ref failed, 0);
        Volatile.Write(ref enabled, 1);
    }

    public MouseMonitor(IntPtr desktop)
    {
        var thread = new Thread(() =>
        {
            if (!Native.SetThreadDesktop(desktop)) { Volatile.Write(ref attachFailed, 1); return; }
            Native.Point previous = default;
            bool hadPoint = false;
            while (true)
            {
                Thread.Sleep(200);
                if (Volatile.Read(ref enabled) == 0) { hadPoint = false; continue; }
                if (!Native.GetCursorPos(out var current)) { Volatile.Write(ref failed, 1); continue; }
                if (!hadPoint || previous.X != current.X || previous.Y != current.Y)
                    Interlocked.Exchange(ref lastMovement, Environment.TickCount64);
                previous = current;
                hadPoint = true;
            }
        }) { IsBackground = true, Name = "Studio mouse monitor" };
        thread.Start();
    }
}
