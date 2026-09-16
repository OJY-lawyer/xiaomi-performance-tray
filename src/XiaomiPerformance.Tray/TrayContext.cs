using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using XiaomiPerformance;

namespace XiaomiPerformance.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly ModeController controller;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem sourceItem = new() { Enabled = false };
    private readonly ToolStripMenuItem energyItem = new() { Enabled = false };
    private readonly Dictionary<PerformanceMode, ToolStripMenuItem> modes = new();
    private readonly Dictionary<PerformanceMode, Icon> icons = new();
    private readonly Icon unknownIcon;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 5000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem nextItem;
    private bool busy, disposed;
    private string? uiFailure;

    public TrayContext(ModeController controller)
    {
        this.controller = controller;
        icons[PerformanceMode.Eco] = CreateIcon(Color.FromArgb(34, 197, 94));
        icons[PerformanceMode.Smart] = CreateIcon(Color.FromArgb(59, 130, 246));
        icons[PerformanceMode.Turbo] = CreateIcon(Color.FromArgb(234, 179, 8));
        icons[PerformanceMode.Quiet] = CreateIcon(Color.FromArgb(34, 197, 94));
        icons[PerformanceMode.Rage] = CreateIcon(Color.FromArgb(234, 179, 8));
        unknownIcon = CreateIcon(Color.FromArgb(128, 133, 143), true);
        menu.Items.Add(statusItem);
        menu.Items.Add(sourceItem);
        menu.Items.Add(energyItem);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var mode in PerformanceModes.Cycle)
        {
            var item = new ToolStripMenuItem(mode.Label()) { Image = icons[mode].ToBitmap(), CheckOnClick = false, Tag = mode };
            item.Click += async (_, _) => { var selected = (PerformanceMode)item.Tag!; await OperateAsync(token => controller.SetAsync(selected, token), true); };
            modes.Add(mode, item);
            menu.Items.Add(item);
        }
        nextItem = new("切换到下一档（双击）");
        nextItem.Click += async (_, _) => await OperateAsync(controller.NextAsync, true);
        menu.Items.Add(nextItem);
        menu.Items.Add(new ToolStripSeparator());
        refreshItem = new("重新读取当前模式");
        refreshItem.Click += async (_, _) => await OperateAsync(controller.RefreshAsync, true);
        menu.Items.Add(refreshItem);
        var helpItem = new ToolStripMenuItem("说明");
        helpItem.Click += (_, _) => MessageBox.Show(
            "双击按绿 → 蓝 → 金循环；右键可直选。\n额定供电：静谧／智能／狂暴。\n其他条件：省电／智能／极速。\n名称自动跟随原厂档位组；灰色表示状态未确认。\n\n" +
            (controller.IsSimulation ? "当前是模拟演示，不改变电脑设置。" : "通过小米原厂服务切换，保留供电限制。请保持管家及原厂服务运行。") +
            "\n\n绿色档位开启 Windows 节能；蓝色或金色恢复此前状态。手动修改 Windows 节能后，以你的设置为准。\n退出托盘只结束显示，不会主动改变任何模式。\n\n仅在 REDMI Book Pro 16 2026（X7 358H）验证。按现状提供，在适用法律允许范围内不承担使用造成的损失。", "小米性能模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(helpItem);
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => { Diagnostics.Write("user_exit"); ExitThread(); };
        menu.Items.Add(exitItem);
        tray = new NotifyIcon { Icon = unknownIcon, Text = "小米性能模式：等待读取", ContextMenuStrip = menu, Visible = true };
        tray.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left) await OperateAsync(controller.NextAsync, true);
        };
        refreshTimer.Tick += async (_, _) =>
        {
            SafeApplyStatus();
            Diagnostics.Heartbeat(controller.Current.ModeLabel);
            await OperateAsync(controller.RefreshAsync, false);
        };
        SafeApplyStatus();
        refreshTimer.Start();
        // WinForms installs the synchronization context when the message loop starts.
        Application.Idle += InitialRefresh;
    }

    private async void InitialRefresh(object? sender, EventArgs e)
    {
        Application.Idle -= InitialRefresh;
        await OperateAsync(controller.RefreshAsync, false);
    }

    private async Task OperateAsync(Func<CancellationToken, Task<OperationResult>> operation, bool notify)
    {
        if (disposed || busy) return;
        busy = true;
        SafeApplyStatus();
        try
        {
            var result = await operation(lifetime.Token);
            if (disposed) return;
            uiFailure = null;
            SafeApplyStatus();
            if (notify) Diagnostics.Write("user_operation", new { result.Code, result.Message });
            if (notify)
            {
                tray.BalloonTipTitle = controller.IsSimulation ? "小米模式 · 模拟演示" : "小米性能模式";
                tray.BalloonTipText = result.Message;
                tray.BalloonTipIcon = result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning;
                tray.ShowBalloonTip(2500);
            }
        }
        catch (OperationCanceledException) when (disposed || lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            uiFailure = "状态读取异常，正在等待重新读取";
            Diagnostics.Write("operation_exception", error.ToString());
            // Never repeat a failed mutation; the next timer tick only reads status.
        }
        finally
        {
            busy = false;
            if (!disposed) SafeApplyStatus();
        }
    }

    private void ApplyStatus()
    {
        var state = controller.Current;
        var isFresh = uiFailure is null && state.IsKnown && DateTimeOffset.UtcNow - state.ObservedAt <= TimeSpan.FromSeconds(20);
        var prefix = controller.IsSimulation ? "模拟 · " : "";
        var label = isFresh ? $"{state.ModeLabel}模式" : "状态未知";
        tray.Icon = isFresh && icons.TryGetValue(state.Mode!.Value, out var icon) ? icon : unknownIcon;
        // Tooltip reflects observed state even while a request is in flight.
        tray.Text = Shorten($"{prefix}小米：{label}{(busy ? " · 正在读取/切换" : "")}", 63);
        statusItem.Text = $"{prefix}{label}{(busy ? "（处理中）" : "")}";
        energyItem.Visible = !controller.IsSimulation;
        var energy = controller.EnergySaver;
        energyItem.Text = energy?.Snapshot is { } power
            ? $"Windows 节能：{(power.Enabled ? "开启" : "关闭")}{(power.SupportsWrites ? "" : "（仅查看）")}" : "Windows 节能：状态未确认";
        sourceItem.Text = controller.IsSimulation ? "模拟状态，未改变电脑设置" : (isFresh ? (state.AllowedTargets?.Count == 1 ? "供电资格待确认，暂仅开放智能" : state.AllowedTargets?.Contains(PerformanceMode.Rage) == true ? "额定供电：静谧／智能／狂暴" : "省电／智能／极速") : "请检查管家或打开电池与性能页同步");
        var activeGroup = state.AllowedTargets is { Count: 3 } ? state.AllowedTargets : PerformanceModes.Cycle;
        int slot = 0;
        foreach (var (mode, item) in modes)
        {
            var target = activeGroup[slot++];
            item.Tag = target;
            item.Text = target.Label();
            item.Checked = isFresh && state.Mode == target;
            item.Enabled = !busy && isFresh && controller.CanSetMode(target);
        }
        nextItem.Enabled = !busy && isFresh && controller.CanSetMode(state.NextInGroup());
        refreshItem.Enabled = !busy;
    }

    private void SafeApplyStatus()
    {
        if (disposed) return;
        try { ApplyStatus(); }
        catch (Exception error)
        {
            uiFailure = "托盘显示暂不可用";
            Diagnostics.Write("render_exception", error.ToString());
            try
            {
                tray.Icon = unknownIcon;
                tray.Text = "小米：状态未知，等待重新读取";
                foreach (var item in modes.Values) item.Enabled = false;
                nextItem.Enabled = false;
            }
            catch { /* Explorer may be rebuilding the notification area. */ }
        }
    }

    protected override void ExitThreadCore()
    {
        DisposeResources();
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeResources();
        base.Dispose(disposing);
    }
    private void DisposeResources()
    {
        if (disposed) return;
        disposed = true;
        Application.Idle -= InitialRefresh;
        refreshTimer.Stop();
        refreshTimer.Dispose();
        lifetime.Cancel();
        tray.Visible = false;
        tray.Dispose();
        foreach (var item in modes.Values) item.Image?.Dispose();
        menu.Dispose();
        foreach (var icon in icons.Values) icon.Dispose();
        unknownIcon.Dispose();
    }
    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length];
    private static Icon CreateIcon(Color color, bool unknown = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var fill = new SolidBrush(color);
        graphics.FillEllipse(fill, 2, 2, 28, 28);
        using var outline = new Pen(Color.FromArgb(200, Color.White), 1.2f);
        graphics.DrawEllipse(outline, 2.8f, 2.8f, 26.4f, 26.4f);
        using var symbol = new SolidBrush(Color.White);
        if (unknown)
        {
            graphics.FillRectangle(symbol, 9, 14, 14, 4);
        }
        else
        {
            graphics.FillPolygon(symbol, [new(17, 7), new(10, 17), new(15, 17), new(13, 25), new(23, 13), new(17, 13)]);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
