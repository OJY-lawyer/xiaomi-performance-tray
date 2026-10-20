using XiaomiPerformance;

internal static class ChargedAcTests
{
    public static void Run(Action<string, bool> check)
    {
        static Fixture Setup(byte ac = 1, byte percent = 96) => new(ac, percent);

        check("Charged AC requires strictly more than 95 percent", !new PowerReading(1, 95).IsEligible && new PowerReading(1, 96).IsEligible && new PowerReading(1, 100).IsEligible);
        check("Battery without AC never qualifies", !new PowerReading(0, 100).IsEligible && new PowerReading(0, 100).IsKnown);
        check("Unknown AC or battery never qualifies", !new PowerReading(255, 100).IsKnown && !new PowerReading(1, 255).IsKnown && !new PowerReading(1, 101).IsEligible);
        check("Zero-percent known readings remain known", new PowerReading(1, 0).IsKnown && !new PowerReading(1, 0).IsEligible);

        var t = Setup(percent: 95);
        _ = t.Poll();
        check("Exactly 95 percent performs only reads", t.Backend.Writes == 0 && t.Api.Writes == 0 && t.Store.Saves == 0);
        t.Power.Current = new(1, 96);
        var result = t.Poll();
        check("Startup or threshold entry selects allowed Turbo", result.Success && t.Backend.Mode == PerformanceMode.Turbo && t.Backend.Writes == 1);
        check("Charged AC ends preexisting Windows energy saver", !t.Api.State.Enabled && t.Api.State.Override == 2 && t.Api.Writes == 1);
        _ = t.Poll();
        check("Eligible polling never repeatedly sends mode requests", t.Backend.Writes == 1 && t.Api.Writes == 1);

        t.Backend.Mode = PerformanceMode.Smart; // A user changes mode after this period was handled.
        t.Api.State = t.Api.State with { Override = 1, Enabled = true, ChangeStamp = t.Api.State.ChangeStamp + 1 };
        _ = t.Poll();
        check("Manual choices persist within one eligible period", t.Backend.Mode == PerformanceMode.Smart && t.Backend.Writes == 1 && t.Api.State.Enabled && t.Api.Writes == 1);
        t.Power.Current = new(255, 255);
        _ = t.Poll();
        t.Power.Current = new(1, 99);
        _ = t.Poll();
        check("Unknown power cannot rearm an already attempted period", t.Backend.Mode == PerformanceMode.Smart && t.Backend.Writes == 1 && t.Api.Writes == 1);
        t.Power.Current = new(0, 99);
        _ = t.Poll();
        t.Power.Current = new(1, 99);
        result = t.Poll();
        check("Known unplug and replug rearms the rule", result.Success && t.Backend.Mode == PerformanceMode.Turbo && t.Backend.Writes == 2 && !t.Api.State.Enabled && t.Api.Writes == 2);
        t.Backend.Mode = PerformanceMode.Smart;
        t.Power.Current = new(1, 95);
        _ = t.Poll();
        t.Power.Current = new(1, 96);
        _ = t.Poll();
        check("Known drop to 95 and rise to 96 rearms the rule", t.Backend.Mode == PerformanceMode.Turbo && t.Backend.Writes == 3);

        t = Setup();
        t.Backend.Known = false;
        _ = t.Poll();
        check("Unknown OEM state waits without an attempt", t.Backend.Writes == 0 && t.Api.Writes == 0);
        t.Backend.Known = true;
        result = t.Poll();
        check("OEM state recovery triggers within the same power period", result.Success && t.Backend.Writes == 1);

        t = Setup();
        t.Backend.CanSet = false;
        _ = t.Poll();
        t.Backend.CanSet = true;
        result = t.Poll();
        check("Read-only OEM state does not consume the period", result.Success && t.Backend.Writes == 1);
        t = Setup();
        t.Backend.Allowed = [PerformanceMode.Smart];
        _ = t.Poll();
        check("Missing allowed gold mode blocks the automatic request", t.Backend.Writes == 0 && t.Api.Writes == 0);
        t.Backend.Allowed = PerformanceModes.Cycle;
        result = t.Poll();
        check("Gold becoming allowed can trigger without unplugging", result.Success && t.Backend.Writes == 1);
        t = Setup();
        t.Backend.Allowed = null;
        _ = t.Poll();
        check("Unspecified target group never guesses Turbo", t.Backend.Writes == 0 && t.Api.Writes == 0);
        t = Setup();
        t.Backend.Allowed = PerformanceModes.RatedCycle;
        result = t.Poll();
        check("Rated OEM group selects Rage as its gold mode", result.Success && t.Backend.Mode == PerformanceMode.Rage && t.Backend.Writes == 1 && !t.Api.State.Enabled);

        t = Setup();
        t.Backend.Mode = PerformanceMode.Turbo;
        result = t.Poll();
        check("Already-Turbo startup still ends Windows energy saver", result.Success && t.Backend.Writes == 0 && t.Api.Writes == 1 && !t.Api.State.Enabled);
        t = Setup();
        t.Backend.FailSet = true;
        result = t.Poll();
        t.Backend.FailSet = false;
        _ = t.Poll();
        check("Failed OEM SET is not automatically retried", !result.Success && t.Backend.Writes == 1 && t.Api.Writes == 0);
        t = Setup();
        t.Backend.ApplyRequested = false;
        result = t.Poll();
        _ = t.Poll();
        check("Unconfirmed OEM SET cannot disable saver or be replayed", !result.Success && t.Backend.Writes == 1 && t.Api.Writes == 0);
        t = Setup();
        t.Api.RejectWrite = true;
        result = t.Poll();
        _ = t.Poll();
        check("Saver failure preserves known gold and does not replay OEM SET", !result.Success && result.Status.IsKnown && result.Status.Mode == PerformanceMode.Turbo && t.Backend.Writes == 1 && t.Api.Writes == 1);

        t = Setup();
        t.Backend.SupportsWrites = false;
        _ = t.Poll();
        check("Read-only backend never receives automatic SET", t.Backend.Writes == 0 && t.Api.Writes == 0);
        t = Setup();
        t.Backend.IsSimulation = true;
        _ = t.Poll();
        check("Simulation monitor cannot mutate even with an injected saver", t.Backend.Writes == 0 && t.Api.Writes == 0);
        t = Setup();
        t.Power.FailRead = true;
        _ = t.Poll();
        t.Power.FailRead = false;
        result = t.Poll();
        check("Power read failure waits and recovery can trigger", result.Success && t.Backend.Writes == 1);
        t = Setup();
        t.Power.Sequence.Enqueue(new(1, 99));
        t.Power.Sequence.Enqueue(new(0, 99));
        result = t.Poll();
        check("Locked apply rechecks power before sending SET", t.Backend.Writes == 0 && t.Api.Writes == 0);

        t = Setup();
        t.Backend.AfterSet = () => t.Power.Current = new(0, 99);
        result = t.Poll();
        check("Unplug after OEM confirmation prevents saver write", !result.Success && result.Status.IsKnown && result.Status.Mode == PerformanceMode.Turbo && t.Backend.Writes == 1 && t.Api.Writes == 0);
        t = Setup();
        t.Backend.EligibilityChanges = true;
        result = t.Poll();
        check("Definite pre-SET group change remains a wait", result.Code == OperationCode.ReadOnly && t.Backend.Writes == 0 && t.Api.Writes == 0);
        t.Backend.EligibilityChanges = false;
        t.Backend.Allowed = PerformanceModes.RatedCycle;
        result = t.Poll();
        check("Updated allowed group can trigger after a rejected preflight", result.Success && t.Backend.Mode == PerformanceMode.Rage && t.Backend.Writes == 1 && t.Api.Writes == 1);

        t = Setup();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _ = t.Monitor.PollAsync(t.Controller, cancelled.Token).GetAwaiter().GetResult();
        check("Cancelled read cannot send an automatic mode request", t.Backend.Writes == 0 && t.Api.Writes == 0);

        t = Setup();
        t.Backend.ReadWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = t.Monitor.PollAsync(t.Controller);
        var second = t.Monitor.PollAsync(t.Controller).GetAwaiter().GetResult();
        t.Backend.ReadWait.TrySetResult(true);
        result = first.GetAwaiter().GetResult();
        check("Overlapping timer checks produce only one automatic request", second.Code == OperationCode.Busy && result.Success && t.Backend.Writes == 1 && t.Api.Writes == 1);
    }

    private sealed class Fixture
    {
        public readonly FakePower Power;
        public readonly FakeBackend Backend = new();
        public readonly FakeApi Api = new();
        public readonly FakeStore Store = new();
        public readonly ModeController Controller;
        public readonly ChargedAcMonitor Monitor;
        public Fixture(byte ac, byte percent)
        {
            Power = new() { Current = new(ac, percent) };
            Controller = new(Backend, new EnergySaverLink(Api, Store));
            Monitor = new(Power);
        }
        public OperationResult Poll() => Monitor.PollAsync(Controller).GetAwaiter().GetResult();
    }

    private sealed class FakePower : IPowerStatusSource
    {
        public PowerReading Current;
        public readonly Queue<PowerReading> Sequence = new();
        public bool FailRead;
        public PowerReading Read() => FailRead ? throw new IOException("fake power read failed") : Sequence.Count > 0 ? Sequence.Dequeue() : Current;
    }
    private sealed class FakeBackend : IPerformanceBackend
    {
        public PerformanceMode Mode = PerformanceMode.Smart;
        public bool Known = true, CanSet = true, ApplyRequested = true, FailSet;
        public bool EligibilityChanges;
        public Action? AfterSet;
        public int Writes;
        public TaskCompletionSource<bool>? ReadWait;
        public IReadOnlyList<PerformanceMode>? Allowed = PerformanceModes.Cycle;
        public string Name => "charged-ac-test";
        public bool SupportsWrites { get; set; } = true;
        public bool IsSimulation { get; set; }
        public async Task<ModeStatus> GetStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ReadWait is not null) await ReadWait.Task.WaitAsync(token);
            return Known ? new ModeStatus(Mode, IsSimulation ? ReadingKind.Simulation : ReadingKind.Confirmed, DateTimeOffset.UtcNow, Name, CanSet, "fake") { AllowedTargets = Allowed }
                : ModeStatus.Unknown(Name, "fake OEM state not ready");
        }
        public Task SetModeAsync(PerformanceMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (EligibilityChanges) throw new ModeEligibilityChangedException("fake eligibility changed before SET");
            Writes++;
            if (FailSet) throw new IOException("fake SET failure");
            if (ApplyRequested) Mode = mode;
            AfterSet?.Invoke();
            return Task.CompletedTask;
        }
    }
    private sealed class FakeApi : IWindowsEnergySaverApi
    {
        public EnergySaverSnapshot State = new(1, 10, true, 1, 28000, "charged-ac-test-boot");
        public int Writes;
        public bool RejectWrite;
        public EnergySaverSnapshot Read() => State;
        public void WriteOverride(uint value, uint expectedStamp)
        {
            Writes++;
            if (RejectWrite || State.ChangeStamp != expectedStamp) throw new InvalidOperationException("fake saver write rejected");
            State = State with { Override = value, ChangeStamp = unchecked(State.ChangeStamp + 1), Enabled = value == 1 };
        }
    }
    private sealed class FakeStore : IEnergySaverLedgerStore
    {
        public EnergySaverLedger? Value;
        public int Saves;
        public EnergySaverLedger? Load() => Value;
        public void Save(EnergySaverLedger value) { Saves++; Value = value; }
        public void Clear() => Value = null;
    }
}
