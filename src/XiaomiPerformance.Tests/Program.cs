using System.Reflection;
using System.Text.Json;
using XiaomiPerformance;
using XiaomiPerformance.Tray;

internal static class Program
{
    private static readonly List<object> checks = [];
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string directory = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(directory);
        void Check(string name, bool value) { if (!value) throw new Exception(name); checks.Add(new { name, passed = true }); }
        EnergySaverTests.Run(Check);
        Check("IFOK captured nonstandard frame", VendorProtocol.TryParse(Convert.FromHexString("000000000C08031005"), out var frame) && frame == new VendorFrame(3, 5));
        Check("IFOK captured standard frame", VendorProtocol.TryParse(Convert.FromHexString("000000000C08031004"), out frame) && frame == new VendorFrame(3, 4));
        Check("SET ACK is success code, not mode", VendorProtocol.TryParse(Convert.FromHexString("000000000C08011004"), out frame) && frame == new VendorFrame(1, 4));
        Check("REG mode event", VendorProtocol.TryParse(Convert.FromHexString("000000000C0802100B"), out frame) && frame == new VendorFrame(2, 11));
        Check("REG default zero for quiet", VendorProtocol.TryParse(Convert.FromHexString("000000000C0802"), out frame) && frame == new VendorFrame(2, 0));
        Check("Truncated varint rejected", !VendorProtocol.TryParse(Convert.FromHexString("000000000C0880"), out _));
        Check("Foreign command rejected", !VendorProtocol.TryParse(Convert.FromHexString("000000000D08011004"), out _));
        Check("Duplicate operation rejected", !VendorProtocol.TryParse(Convert.FromHexString("000000000C08010802"), out _));
        Check("Overflow rejected", !VendorProtocol.TryParse(Convert.FromHexString("000000000C08FFFFFFFF7F"), out _));
        Check("Unreviewed mode cannot be written", Throws(() => VendorProtocol.SetRequest((PerformanceMode)99)));
        Check("Rated quiet request", Convert.ToHexString(VendorProtocol.SetRequest(PerformanceMode.Quiet)) == "000000000C08011000");
        Check("Rated rage request", Convert.ToHexString(VendorProtocol.SetRequest(PerformanceMode.Rage)) == "000000000C08011003");
        var rated = new TestBackend(PerformanceMode.Smart) { Allowed = PerformanceModes.RatedCycle };
        var ratedController = new ModeController(rated);
        foreach (var target in new[] { PerformanceMode.Rage, PerformanceMode.Quiet, PerformanceMode.Smart })
            Check($"Rated cycle to {target}", ratedController.NextAsync().GetAwaiter().GetResult().Status.Mode == target);
        using (var ratedTray = new TrayContext(ratedController))
        {
            var ratedMenu = (ContextMenuStrip)Field(ratedTray, "menu");
            var rageItem = ratedMenu.Items.OfType<ToolStripMenuItem>().Single(x => x.Text == "狂暴");
            Check("Rated menu exposes native names", rageItem.Enabled && ratedMenu.Items.OfType<ToolStripMenuItem>().Any(x => x.Text == "静谧" && x.Enabled));
            rageItem.PerformClick();
            PumpUntil(() => ratedController.Current.Mode == PerformanceMode.Rage && !(bool)Field(ratedTray, "busy"));
            Check("Rated menu tag routes to rage", rageItem.Checked && ((NotifyIcon)Field(ratedTray, "tray")).Text.Contains("狂暴"));
            typeof(NotifyIcon).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Field(ratedTray, "tray"), [new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0)]);
            PumpUntil(() => ratedController.Current.Mode == PerformanceMode.Quiet && !(bool)Field(ratedTray, "busy"));
            Check("Rated double click follows rated group", ((NotifyIcon)Field(ratedTray, "tray")).Text.Contains("静谧"));
        }

        var guarded = new TestBackend(PerformanceMode.Smart) { Allowed = [PerformanceMode.Smart] };
        var control = new ModeController(guarded);
        var result = control.NextAsync().GetAwaiter().GetResult();
        Check("Power restriction prevents writer call", result.Code == OperationCode.ReadOnly && guarded.Writes == 0);
        var recovering = new TestBackend(PerformanceMode.Quiet) { Allowed = [PerformanceMode.Smart] };
        result = new ModeController(recovering).NextAsync().GetAwaiter().GetResult();
        Check("Other known OEM mode can return smart", result.Success && recovering.Writes == 1 && result.Status.Mode == PerformanceMode.Smart);
        var wrong = new TestBackend(PerformanceMode.Smart) { ApplyRequestedMode = false };
        result = new ModeController(wrong).SetAsync(PerformanceMode.Turbo).GetAwaiter().GetResult();
        Check("Successful setter without mode change fails", !result.Success && result.Status.Mode == PerformanceMode.Smart);
        var unavailable = new ModeController(new UnavailableBackend());
        result = unavailable.SetAsync(PerformanceMode.Turbo).GetAwaiter().GetResult();
        Check("Unavailable backend refuses SET", result.Code == OperationCode.ReadOnly);

        // Exercise the actual TrayContext event handler with an explicit simulation.
        // These are application-event tests, not physical mouse hit-testing evidence.
        string statePath = Path.Combine(directory, "tray-simulation.json");
        File.WriteAllText(statePath, "{\"schema\":\"xiaomi-performance-simulation/v1\",\"mode\":\"smart\"}");
        var simulation = new ModeController(new SimulationBackend(statePath));
        var initial = simulation.RefreshAsync().GetAwaiter().GetResult();
        Check("Simulation initialized", initial.Success);
        using (var context = new TrayContext(simulation))
        {
            var icon = (NotifyIcon)Field(context, "tray");
            var menu = (ContextMenuStrip)Field(context, "menu");
            Check("Exactly three selectable mode entries", menu.Items.OfType<ToolStripMenuItem>().Count(item => item.Text is "省电" or "智能" or "极速") == 3);
            icon.Icon!.ToBitmap().Save(Path.Combine(directory, "blue-smart.png"));
            foreach (var target in new[] { PerformanceMode.Turbo, PerformanceMode.Eco, PerformanceMode.Smart })
            {
                typeof(NotifyIcon).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(icon,
                    [new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0)]);
                PumpUntil(() => simulation.Current.Mode == target && !(bool)Field(context, "busy"));
                Check($"Actual double-click handler reaches {target}", simulation.Current.Mode == target && icon.Text.Contains(target.Label()));
                if (target != PerformanceMode.Smart) icon.Icon!.ToBitmap().Save(Path.Combine(directory, target == PerformanceMode.Eco ? "green-eco.png" : "gold-turbo.png"));
            }
            var eco = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "省电");
            eco.PerformClick();
            PumpUntil(() => simulation.Current.Mode == PerformanceMode.Eco && !(bool)Field(context, "busy"));
            Check("Actual right-menu handler selects eco", eco.Checked && simulation.Current.Mode == PerformanceMode.Eco);
            Func<CancellationToken, Task<OperationResult>> failed = _ => Task.FromException<OperationResult>(new IOException("injected refresh failure"));
            var operate = typeof(TrayContext).GetMethod("OperateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var failure = (Task)operate.Invoke(context, [failed, false])!;
            PumpUntil(() => failure.IsCompleted);
            Check("Unexpected refresh failure does not escape", failure.IsCompletedSuccessfully && !(bool)Field(context, "busy"));
            Check("Unexpected failure disables mode buttons", icon.Text.Contains("状态未知") && !eco.Enabled);
            var recovery = (Task)operate.Invoke(context, [new Func<CancellationToken, Task<OperationResult>>(simulation.RefreshAsync), false])!;
            PumpUntil(() => recovery.IsCompleted);
            Check("Read-only refresh recovers after failure", recovery.IsCompletedSuccessfully && eco.Enabled && simulation.Current.Mode == PerformanceMode.Eco);
        }
        File.WriteAllText(Path.Combine(directory, "core-ui-events.json"), JsonSerializer.Serialize(checks, JsonFormat.Options));
        Console.WriteLine($"PASS {checks.Count} protocol / controller / tray event checks; no real vendor setter was used.");
        return 0;
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static bool Throws(Action action) { try { action(); return false; } catch (ArgumentOutOfRangeException) { return true; } }
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!done()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Tray event timed out"); Application.DoEvents(); Thread.Sleep(10); }
    }
    private sealed class TestBackend(PerformanceMode mode) : IPerformanceBackend
    {
        public string Name => "test only";
        public bool SupportsWrites => true;
        public bool IsSimulation => false;
        public IReadOnlyList<PerformanceMode>? Allowed { get; init; }
        public bool ApplyRequestedMode { get; init; } = true;
        public int Writes { get; private set; }
        public Task<ModeStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new ModeStatus(mode, ReadingKind.Confirmed, DateTimeOffset.UtcNow, Name, true, "test fixture") { AllowedTargets = Allowed });
        public Task SetModeAsync(PerformanceMode target, CancellationToken token) { Writes++; if (ApplyRequestedMode) mode = target; return Task.CompletedTask; }
    }
}
