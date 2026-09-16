using System.Security.Principal;

namespace XiaomiPerformance;

public enum OperationCode { Ok, Unknown, ReadOnly, Busy, Failed, Cancelled }
public sealed record OperationResult(OperationCode Code, ModeStatus Status, string Message)
{
    public bool Success => Code == OperationCode.Ok;
}

/// <summary>Serializes refresh/set/next; never claims a requested mode is an observed mode.</summary>
public sealed class ModeController(IPerformanceBackend backend, EnergySaverLink? energySaver = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static readonly string OperationMutex = $"Global\\OJY.XiaomiPerformance.Operation.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";
    public ModeStatus Current { get; private set; } = ModeStatus.Unknown(backend.Name, "等待读取。");
    public bool IsSimulation => backend.IsSimulation;
    public EnergySaverCapture? EnergySaver { get; private set; }
    public bool CanSet => backend.SupportsWrites && Current.CanSet && Current.IsKnown;
    public bool CanSetMode(PerformanceMode mode) => CanSet && (Current.AllowedTargets is null || Current.AllowedTargets.Contains(mode));

    public Task<OperationResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        RunAsync(null, false, cancellationToken);
    public Task<OperationResult> SetAsync(PerformanceMode mode, CancellationToken cancellationToken = default) =>
        RunAsync(mode, false, cancellationToken);
    public Task<OperationResult> NextAsync(CancellationToken cancellationToken = default) =>
        RunAsync(null, true, cancellationToken);

    private Task<OperationResult> RunAsync(PerformanceMode? requested, bool next, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // Tray and CLI share one vendor endpoint. Acquire and release this mutex on
            // the same worker thread, including when the backend uses asynchronous I/O.
            using var processGate = new Mutex(false, OperationMutex);
            bool acquired;
            try { acquired = processGate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new OperationResult(OperationCode.Busy, Current, "另一个托盘或命令行操作正在读取或切换，请稍后再试。");
            try { return ExecuteAsync(requested, next, cancellationToken).GetAwaiter().GetResult(); }
            finally { processGate.ReleaseMutex(); }
        });

    private async Task<OperationResult> ExecuteAsync(PerformanceMode? requested, bool next, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0))
            return new(OperationCode.Busy, Current, "正在读取或切换，请稍后再试。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Deadline);
        try
        {
            if ((requested.HasValue || next) && !backend.SupportsWrites)
            {
                Current = Validate(await backend.GetStatusAsync(timeout.Token));
                return new(OperationCode.ReadOnly, Current, "当前后端没有启用真实模式切换。");
            }
            Current = Validate(await backend.GetStatusAsync(timeout.Token));
            EnergySaver = IsSimulation ? null : energySaver?.Read();
            if (!Current.IsKnown) return new(OperationCode.Unknown, Current, Current.Detail);
            if (!requested.HasValue && !next) return new(OperationCode.Ok, Current, Current.Detail);
            if (!Current.CanSet) return new(OperationCode.ReadOnly, Current, "当前状态不允许切换模式。");
            var desired = next ? Current.NextInGroup() : requested!.Value;
            if (!Enum.IsDefined(desired)) throw new ArgumentOutOfRangeException(nameof(requested));
            if (!CanSetMode(desired)) return new(OperationCode.ReadOnly, Current, "原厂当前供电条件不允许该档位，未发送切换请求。可保留或切回智能模式。");
            var energyBefore = IsSimulation ? null : energySaver?.Capture();
            if (Current.Mode != desired)
                await backend.SetModeAsync(desired, timeout.Token);
            Current = Validate(await backend.GetStatusAsync(timeout.Token));
            if (!Current.IsKnown || Current.Mode != desired)
                return new(OperationCode.Failed, Current, $"未确认切换到{desired.Label()}；当前读取为{Current.ModeLabel}。");
            if (energySaver is not null && energyBefore is not null && !IsSimulation)
            {
                // The OEM mode is already confirmed. A separate Windows failure must
                // not erase it or provoke a repeated vendor SET.
                try
                {
                    var linked = await energySaver.ApplyAsync(desired, energyBefore, timeout.Token);
                    EnergySaver = energySaver.Read();
                    return new(linked.Success ? OperationCode.Ok : OperationCode.Failed, Current,
                        $"当前为{Current.ModeLabel}模式。{linked.Message}");
                }
                catch (Exception error)
                {
                    EnergySaver = energySaver.Read();
                    return new(OperationCode.Failed, Current, $"已切换为{Current.ModeLabel}；Windows 节能联动未完成：{error.Message}");
                }
            }
            return new(OperationCode.Ok, Current, $"{(IsSimulation ? "模拟：" : "")}当前为{Current.ModeLabel}模式。");
        }
        catch (OperationCanceledException)
        {
            Current = ModeStatus.Unknown(backend.Name, cancellationToken.IsCancellationRequested ? "操作已取消，状态待重新读取。" : "读取或切换超时，真实状态未确认。");
            return new(OperationCode.Cancelled, Current, Current.Detail);
        }
        catch (Exception error)
        {
            Current = ModeStatus.Unknown(backend.Name, $"无法确认当前模式：{error.Message}");
            return new(OperationCode.Failed, Current, Current.Detail);
        }
        finally { gate.Release(); }
    }

    private ModeStatus Validate(ModeStatus status)
    {
        if (status.Reading == ReadingKind.Unknown || status.Mode is null)
            return ModeStatus.Unknown(backend.Name, status.Detail);
        if (!Enum.IsDefined(status.Mode.Value)) throw new InvalidDataException("后端返回了无效的模式值。");
        if (backend.IsSimulation != (status.Reading == ReadingKind.Simulation))
            throw new InvalidDataException("后端状态来源与模拟标记不一致。");
        if (status.Reading != ReadingKind.Confirmed && status.Reading != ReadingKind.Simulation)
            throw new InvalidDataException("后端未确认状态。");
        if (DateTimeOffset.UtcNow - status.ObservedAt > TimeSpan.FromSeconds(20) || status.ObservedAt > DateTimeOffset.UtcNow.AddSeconds(5))
            return ModeStatus.Unknown(backend.Name, "状态已过期，等待重新读取。");
        return status with { CanSet = status.CanSet && backend.SupportsWrites };
    }
}
