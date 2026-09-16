using System.Security.Principal;
using XiaomiPerformance;

namespace XiaomiPerformance.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool autostart = args.Contains("--autostart");
        args = args.Where(arg => arg != "--autostart").ToArray();
        Diagnostics.Initialize(autostart);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Diagnostics.Write("ui_unhandled_exception", e.Exception.ToString());
            Environment.ExitCode = 1;
            Application.Exit();
        };
        try { Run(args, autostart); }
        catch (Exception error)
        {
            Diagnostics.Write("startup_or_loop_failure", error.ToString());
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args, bool autostart)
    {
        ApplicationConfiguration.Initialize();
        AppOptions options;
        try { options = AppOptions.Parse(args, "tray"); }
        catch (ArgumentException error) { MessageBox.Show(error.Message, "小米性能模式", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (options.Command == "help")
        {
            MessageBox.Show("双击托盘：省电 → 智能 → 极速 → 省电。\n右键可直接选模式或刷新。\n\n通过原厂服务切换，保留额定适配器限制；需要小米电脑管家运行。\n添加 --simulate 可演示颜色和交互，明确标为模拟。\n命令行请使用 XiaomiPerformance.Cli.exe。", "小米性能模式");
            return;
        }
        if (options.Command != "tray") { MessageBox.Show("命令行操作请使用 XiaomiPerformance.Cli.exe。", "小米性能模式"); return; }
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var instance = new Mutex(true, $"Local\\OJY.XiaomiPerformance.Tray.{identity}", out var first);
        if (!first)
        {
            Diagnostics.Write("duplicate_instance");
            if (!autostart) MessageBox.Show("小米性能模式托盘已经在运行。请查看右下角或隐藏图标区域。", "小米性能模式");
            return;
        }
        try { Application.Run(new TrayContext(BackendFactory.CreateController(options))); }
        finally { Diagnostics.Write("message_loop_ended"); instance.ReleaseMutex(); }
    }
}
