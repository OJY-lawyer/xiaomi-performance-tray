using System.Runtime.InteropServices;
using System.Text.Json;

namespace XiaomiPerformance.Tray;

internal static class Diagnostics
{
    private static readonly object Gate = new();
    private static bool enabled;
    private static DateTime nextHeartbeat;
    internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OJY", "XiaomiPerformance", "logs");
    internal static void Initialize(bool autostart)
    {
        enabled = true;
        IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob);
        Write("started", new { autostart, inJob, version = typeof(Diagnostics).Assembly.GetName().Version?.ToString(), executable = Environment.ProcessPath });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("process_exit", new { code = Environment.ExitCode });
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("unhandled_exception", e.ExceptionObject.ToString());
    }
    internal static void Heartbeat(string status)
    {
        if (DateTime.UtcNow < nextHeartbeat) return;
        nextHeartbeat = DateTime.UtcNow.AddMinutes(1);
        Write("heartbeat", status);
    }
    internal static void Write(string name, object? detail = null)
    {
        if (!enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "lifecycle.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, Path.Combine(DirectoryPath, "lifecycle.previous.jsonl"), true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { time = DateTimeOffset.Now, pid = Environment.ProcessId, name, detail }) + "\n");
            }
        }
        catch { /* Logging must never terminate a tray or cause a repeated command. */ }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
