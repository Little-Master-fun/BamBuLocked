using System.ComponentModel;

namespace PrintGate.Windows;

internal static class StartupDiagnostics
{
    internal static void Write(string stage, Exception? error = null)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintGate", "Diagnostics");
            Directory.CreateDirectory(folder);
            var text = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} stage={stage}";
            // Do not record exception messages, URLs, credentials, or authentication payloads.
            for (var current = error; current is not null; current = current.InnerException)
                text += $"\n{current.GetType().FullName} HRESULT=0x{current.HResult:X8}" +
                    (current is Win32Exception native ? $" Win32={native.NativeErrorCode}" : "") + $"\n{current.StackTrace}";
            File.AppendAllText(Path.Combine(folder, $"startup-{Environment.ProcessId}.log"), text + Environment.NewLine);
        }
        catch { /* Failure reporting must not bypass access control. */ }
    }
}
