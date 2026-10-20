namespace XiaomiPerformance;

/// <summary>
/// Attempts the charged-and-plugged-in rule once per known qualifying period.
/// A later manual selection is preserved until known power conditions leave and re-enter that period.
/// </summary>
public sealed class ChargedAcMonitor(IPowerStatusSource powerSource)
{
    private readonly SemaphoreSlim pollGate = new(1, 1);
    private bool attempted;
    private string attemptedText = "自动极速：本次条件已处理，保留后续手动选择";
    public string StatusText { get; private set; } = "自动极速：接电且电量大于 95% 时启用";

    public async Task<OperationResult> PollAsync(ModeController controller, CancellationToken cancellationToken = default)
    {
        if (!await pollGate.WaitAsync(0))
            return new(OperationCode.Busy, controller.Current, "正在检查自动极速条件，请稍后再试。");
        try
        {
            // Refresh is read-only. Never treat a cached requested mode as OEM confirmation.
            var refreshed = await controller.RefreshAsync(cancellationToken);
            PowerReading power;
            try { power = powerSource.Read(); }
            catch { power = new(byte.MaxValue, byte.MaxValue); }

            if (controller.IsSimulation)
            {
                StatusText = "自动极速：模拟模式不执行";
                return refreshed;
            }
            if (!power.IsKnown)
            {
                StatusText = "自动极速：等待确认电量和供电状态";
                return refreshed; // Unknown readings must not re-arm a completed attempt.
            }
            if (!power.IsEligible)
            {
                attempted = false;
                StatusText = "自动极速：接电且电量大于 95% 时启用";
                return refreshed;
            }
            if (attempted)
            {
                StatusText = attemptedText;
                return refreshed;
            }
            var state = controller.Current;
            if (!refreshed.Success || !state.IsKnown || !controller.CanSet)
            {
                StatusText = "自动极速：条件已满足，等待原厂模式就绪";
                return refreshed;
            }
            if (state.AllowedTargets is null ||
                !state.AllowedTargets.Any(mode => mode is PerformanceMode.Turbo or PerformanceMode.Rage))
            {
                StatusText = "自动极速：条件已满足，等待原厂允许极速档";
                return new(OperationCode.ReadOnly, state, StatusText);
            }

            // Latch before awaiting so another timer callback cannot duplicate an attempt.
            attempted = true;
            var result = await controller.ApplyChargedAcRuleAsync(powerSource, cancellationToken);
            if (result.Code is OperationCode.Busy or OperationCode.Unknown or OperationCode.ReadOnly)
            {
                attempted = false; // No SET was sent; readiness may recover on a later poll.
                StatusText = "自动极速：条件已满足，等待原厂模式就绪";
            }
            else
            {
                attemptedText = result.Success
                    ? "自动极速：本次已启用，保留后续手动选择"
                    : "自动极速：本次未完成，请手动重试或等待条件重新满足";
                StatusText = attemptedText;
            }
            return result;
        }
        catch (Exception error)
        {
            StatusText = attempted
                ? attemptedText = "自动极速：本次未完成，请手动重试或等待条件重新满足"
                : "自动极速：暂时无法检查，等待重新读取";
            return new(OperationCode.Failed, controller.Current, $"自动极速检查未完成：{error.Message}");
        }
        finally { pollGate.Release(); }
    }
}
