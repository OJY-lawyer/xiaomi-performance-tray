using XiaomiPerformance;

internal static class EnergySaverTests
{
    public static void Run(Action<string, bool> check)
    {
        static (FakeApi api, FakeStore store, EnergySaverLink link) Setup(uint original = 2, bool enabled = false)
        {
            var api = new FakeApi { State = new(original, 10, enabled, 1, 28000, "boot-a") };
            var store = new FakeStore();
            return (api, store, new EnergySaverLink(api, store));
        }
        static EnergySaverLinkResult Apply(EnergySaverLink link, PerformanceMode mode, EnergySaverCapture? before = null) =>
            link.ApplyAsync(mode, before ?? link.Capture()).GetAwaiter().GetResult();
        static EnergySaverLinkResult ForceOff(EnergySaverLink link, EnergySaverCapture? before = null) =>
            link.ForceOffAsync(before ?? link.Capture()).GetAwaiter().GetResult();

        var t = Setup();
        _ = t.link.Read(); _ = t.link.Capture();
        check("Energy read is mutation free", t.api.Writes == 0 && t.store.Saves == 0 && t.store.Clears == 0);
        var result = Apply(t.link, PerformanceMode.Eco);
        check("Eco enables and owns verified state", result.Success && t.api.State.Enabled && t.api.Writes == 1 && t.store.Value is { Phase: "owned", OriginalOverride: 2, AppliedStamp: 11 });
        result = Apply(t.link, PerformanceMode.Quiet);
        check("Green to green does not recapture baseline", result.Success && t.api.Writes == 1 && t.store.Value?.OriginalOverride == 2);
        t.api.State = t.api.State with { Enabled = false };
        result = Apply(t.link, PerformanceMode.Eco);
        check("Owned override without effective saver is not reported as enabled", !result.Success && t.api.Writes == 1 && t.store.Value?.Phase == "owned");
        t.api.State = t.api.State with { Enabled = true };
        result = Apply(t.link, PerformanceMode.Smart);
        check("Blue restores exact baseline", result.Success && !t.api.State.Enabled && t.api.State.Override == 2 && t.store.Value is null);

        t = Setup();
        check("Quiet also enables energy saver", Apply(t.link, PerformanceMode.Quiet).Success && t.api.State.Enabled);
        check("Gold restores linked override", Apply(t.link, PerformanceMode.Rage).Success && t.api.State.Override == 2);
        t = Setup(1, true);
        _ = Apply(t.link, PerformanceMode.Eco); _ = Apply(t.link, PerformanceMode.Turbo);
        check("Preexisting enabled state is never owned or disabled", t.api.Writes == 0 && t.store.Saves == 0 && t.api.State.Enabled);

        t = Setup(0);
        _ = Apply(t.link, PerformanceMode.Eco);
        t.api.AutoEnabled = true; // Battery reached automatic threshold while green.
        result = Apply(t.link, PerformanceMode.Smart);
        check("Automatic policy is restored instead of force off", result.Success && t.api.State.Override == 0 && t.api.State.Enabled);

        t = Setup();
        _ = Apply(t.link, PerformanceMode.Eco);
        t.api.State = t.api.State with { ChangeStamp = 12, Override = 2, Enabled = false };
        result = Apply(t.link, PerformanceMode.Smart);
        check("External write wins over restore", result.Success && t.api.Writes == 1 && t.store.Value is null && t.api.State.Override == 2);
        t = Setup();
        var captured = t.link.Capture();
        t.api.State = t.api.State with { ChangeStamp = 11 };
        check("Change between capture and OEM completion blocks write", !Apply(t.link, PerformanceMode.Eco, captured).Success && t.api.Writes == 0);

        t = Setup();
        t.api.State = t.api.State with { WindowsBuild = 26100 };
        check("Unverified Windows build cannot write", !Apply(t.link, PerformanceMode.Eco).Success && t.api.Writes == 0);
        t = Setup();
        t.api.FailRead = true;
        check("Read failure captured without throwing", !t.link.Read().Success);
        check("Failed capture prevents write", !Apply(t.link, PerformanceMode.Eco).Success && t.api.Writes == 0);

        t = Setup();
        t.store.FailSave = true;
        check("Ledger save failure prevents native write", !Apply(t.link, PerformanceMode.Eco).Success && t.api.Writes == 0);
        t = Setup();
        t.api.RejectWrite = true;
        check("Conditional write failure is not retried", !Apply(t.link, PerformanceMode.Eco).Success && t.api.Writes == 1 && t.store.Value?.Phase == "pending-enable");
        _ = Apply(t.link, PerformanceMode.Eco);
        check("Unresolved pending write blocks replay", t.api.Writes == 1);

        t = Setup();
        t.api.ConcurrentAfterWrite = true;
        check("Post-write unexpected stamp is not claimed", !Apply(t.link, PerformanceMode.Eco).Success && t.store.Value?.Phase == "pending-enable");
        t = Setup();
        t.store.Value = new("old-boot", 2, false, 11, "owned");
        result = Apply(t.link, PerformanceMode.Smart);
        check("Prior boot ledger never restores into new boot", result.Success && t.api.Writes == 0 && t.store.Value is null);

        t = Setup();
        t.api.NoEffectiveChange = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        result = t.link.ApplyAsync(PerformanceMode.Eco, t.link.Capture(), cts.Token).GetAwaiter().GetResult();
        check("Cancellation during validation preserves pending evidence", !result.Success && t.api.Writes == 1 && t.store.Value?.Phase == "pending-enable");

        t = Setup(1, true);
        result = ForceOff(t.link);
        check("Automatic high performance disables preexisting energy saver", result.Success && t.api.Writes == 1 && t.api.State is { Override: 2, Enabled: false } && t.store.Value is null);
        t = Setup(0);
        result = ForceOff(t.link);
        check("Automatic high performance replaces automatic saver policy with off", result.Success && t.api.Writes == 1 && t.api.State is { Override: 2, Enabled: false } && t.store.Value is null);
        t = Setup(0, true);
        result = ForceOff(t.link);
        check("Automatic high performance disables an effective automatic saver", result.Success && t.api.Writes == 1 && !t.api.State.Enabled);
        t = Setup();
        result = ForceOff(t.link);
        check("Force off is mutation free when override and effective state are off", result.Success && t.api.Writes == 0 && t.store.Saves == 0 && t.store.Clears == 0);
        t = Setup(2, true);
        result = ForceOff(t.link);
        check("Force off can refresh a stale effective state once", result.Success && t.api.Writes == 1 && !t.api.State.Enabled);

        t = Setup(0);
        _ = Apply(t.link, PerformanceMode.Eco);
        t.api.AutoEnabled = true;
        result = ForceOff(t.link);
        var offWrites = t.api.Writes;
        var smart = Apply(t.link, PerformanceMode.Smart);
        check("Force off releases green ownership so blue cannot turn saver back on", result.Success && smart.Success && offWrites == 2 && t.api.Writes == offWrites && t.api.State is { Override: 2, Enabled: false } && t.store.Value is null);
        t = Setup();
        t.store.Value = new("boot-a", 0, false, 9, "owned");
        result = ForceOff(t.link);
        check("Already off clears superseded ownership without a native write", result.Success && t.api.Writes == 0 && t.store.Clears == 1 && t.store.Value is null);

        t = Setup(1, true);
        captured = t.link.Capture();
        t.api.State = t.api.State with { ChangeStamp = 11 };
        result = ForceOff(t.link, captured);
        check("Force off respects changes made while OEM mode was switching", !result.Success && t.api.Writes == 0 && t.store.Saves == 0 && t.api.State.Enabled);
        t = Setup(1, true);
        t.api.ConcurrentAfterWrite = true;
        result = ForceOff(t.link);
        check("Force off does not claim a concurrent post-write state", !result.Success && t.api.Writes == 1 && t.store.Value?.Phase == "pending-force-off");
        _ = ForceOff(t.link); _ = Apply(t.link, PerformanceMode.Smart);
        check("Uncertain force off blocks both replay and old baseline restoration", t.api.Writes == 1 && t.store.Value?.Phase == "pending-force-off");
        t = Setup(1, true);
        t.api.RejectWrite = true;
        result = ForceOff(t.link);
        _ = ForceOff(t.link);
        check("Rejected force off is not retried and preserves pending evidence", !result.Success && t.api.Writes == 1 && t.store.Value?.Phase == "pending-force-off");
        t = Setup(1, true);
        t.store.FailSave = true;
        check("Force off requires a saved pending record before native write", !ForceOff(t.link).Success && t.api.Writes == 0);
        t = Setup(1, true);
        t.store.Value = new("boot-a", 2, false, 10, "pending-enable");
        check("Force off preserves an unresolved earlier enable", !ForceOff(t.link).Success && t.api.Writes == 0 && t.store.Value?.Phase == "pending-enable");
        t.store.Value = t.store.Value! with { Phase = "pending-restore" };
        check("Force off preserves an unresolved earlier restore", !ForceOff(t.link).Success && t.api.Writes == 0 && t.store.Value?.Phase == "pending-restore");
        t = Setup(1, true);
        t.store.Value = new("old-boot", 2, false, 10, "pending-force-off");
        result = ForceOff(t.link);
        check("Force off discards stamps from an earlier boot", result.Success && t.api.Writes == 1 && t.store.Value is null && !t.api.State.Enabled);
        t = Setup(2, true);
        t.api.NoEffectiveChange = true;
        using var offCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        result = t.link.ForceOffAsync(t.link.Capture(), offCts.Token).GetAwaiter().GetResult();
        _ = ForceOff(t.link);
        check("Unconfirmed effective off is recorded without repeated same-value writes", !result.Success && t.api.Writes == 1 && t.store.Value?.Phase == "pending-force-off");

        t = Setup();
        var backend = new FakeBackend { Mode = PerformanceMode.Eco };
        var controller = new ModeController(backend, t.link);
        _ = controller.RefreshAsync().GetAwaiter().GetResult();
        check("Controller refresh never applies energy link", t.api.Writes == 0 && t.store.Saves == 0);
        var operation = controller.SetAsync(PerformanceMode.Eco).GetAwaiter().GetResult();
        check("Same OEM target still reconciles saver", operation.Success && backend.Writes == 0 && t.api.Writes == 1);
        t = Setup();
        t.api.RejectWrite = true;
        backend = new FakeBackend();
        operation = new ModeController(backend, t.link).SetAsync(PerformanceMode.Eco).GetAwaiter().GetResult();
        check("Link failure preserves confirmed OEM result", !operation.Success && operation.Status.IsKnown && operation.Status.Mode == PerformanceMode.Eco && backend.Writes == 1);
        t = Setup();
        backend = new FakeBackend { ApplyRequested = false };
        operation = new ModeController(backend, t.link).SetAsync(PerformanceMode.Eco).GetAwaiter().GetResult();
        check("Unconfirmed OEM result cannot change Windows saver", !operation.Success && t.api.Writes == 0);
        t = Setup();
        backend = new FakeBackend { IsSimulation = true };
        operation = new ModeController(backend, t.link).SetAsync(PerformanceMode.Eco).GetAwaiter().GetResult();
        check("Simulation cannot write Windows even with injected link", operation.Success && t.api.Writes == 0 && t.store.Saves == 0);
    }

    private sealed class FakeBackend : IPerformanceBackend
    {
        public PerformanceMode Mode = PerformanceMode.Smart;
        public bool ApplyRequested = true;
        public int Writes;
        public string Name => "energy-test";
        public bool SupportsWrites => true;
        public bool IsSimulation { get; init; }
        public Task<ModeStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new ModeStatus(
            Mode, IsSimulation ? ReadingKind.Simulation : ReadingKind.Confirmed, DateTimeOffset.UtcNow, Name, true, "fake") { AllowedTargets = PerformanceModes.Cycle });
        public Task SetModeAsync(PerformanceMode mode, CancellationToken token) { Writes++; if (ApplyRequested) Mode = mode; return Task.CompletedTask; }
    }

    private sealed class FakeApi : IWindowsEnergySaverApi
    {
        public EnergySaverSnapshot State = null!;
        public int Writes;
        public bool FailRead, RejectWrite, ConcurrentAfterWrite, NoEffectiveChange, AutoEnabled;
        public EnergySaverSnapshot Read() => FailRead ? throw new IOException("fake read failed") : State;
        public void WriteOverride(uint value, uint expectedStamp)
        {
            Writes++;
            if (RejectWrite || State.ChangeStamp != expectedStamp) throw new InvalidOperationException("fake stamp mismatch");
            State = State with
            {
                Override = value,
                ChangeStamp = unchecked(State.ChangeStamp + (ConcurrentAfterWrite ? 2u : 1u)),
                Enabled = NoEffectiveChange ? State.Enabled : value == 1 || (value == 0 && AutoEnabled)
            };
        }
    }
    private sealed class FakeStore : IEnergySaverLedgerStore
    {
        public EnergySaverLedger? Value;
        public int Saves, Clears;
        public bool FailSave;
        public EnergySaverLedger? Load() => Value;
        public void Save(EnergySaverLedger value)
        {
            Saves++;
            if (FailSave) throw new IOException("fake disk full");
            Value = value;
        }
        public void Clear() { Clears++; Value = null; }
    }
}
