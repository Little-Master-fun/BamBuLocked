using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintGate.Windows;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? reserved; public string? desktop; public string? title;
        public uint x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2; public IntPtr reservedPointer, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo
    { public IntPtr process, thread; public uint processId, threadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] internal static extern bool LockWorkStation();
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startupInfo, out ProcessInfo processInfo);

    internal static IntPtr CreateDesktop(string name)
    {
        var handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    internal static Process StartOnDesktop(string executable, string desktop, string arguments = "")
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = @"winsta0\" + desktop };
        // executable is an administrator-controlled absolute path; arguments are generated internally.
        if (!CreateProcessW(executable, new StringBuilder($"\"{executable}\" {arguments}"), IntPtr.Zero,
            IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Process.GetProcessById((int)info.processId); }
        finally { CloseHandle(info.process); CloseHandle(info.thread); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    internal static (Process Process, StreamWriter Input, StreamReader Output) StartRecorder(string executable, string desktop, string arguments, IntPtr job)
    {
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(), desktop = @"winsta0\" + desktop, flags = 0x100,
                stdInput = input.ClientSafePipeHandle.DangerousGetHandle(),
                stdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
                stdError = output.ClientSafePipeHandle.DangerousGetHandle()
            };
            if (!CreateProcessW(executable, new StringBuilder($"\"{executable}\" {arguments}"), IntPtr.Zero,
                IntPtr.Zero, true, 0x08000004, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var process = Process.GetProcessById((int)info.processId);
                _ = process.Handle; // Retain a waitable handle before the suspended encoder can exit.
                if (!AssignProcessToJobObject(job, info.process) || ResumeThread(info.thread) == uint.MaxValue)
                {
                    process.Dispose();
                    TerminateProcess(info.process, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle();
                return (process, new StreamWriter(input) { AutoFlush = true }, new StreamReader(output));
            }
            finally { CloseHandle(info.process); CloseHandle(info.thread); }
        }
        catch { input.Dispose(); output.Dispose(); throw; }
    }
}
