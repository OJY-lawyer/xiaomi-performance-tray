using System.Text.Json;

namespace XiaomiPerformance;

public sealed record EnergySaverCapture(EnergySaverSnapshot? Snapshot, string? Error)
{
    public bool Success => Snapshot is not null && Error is null;
}
public sealed record EnergySaverLinkResult(bool Success, string Message, EnergySaverSnapshot? Snapshot = null);
public sealed record EnergySaverLedger(string BootId, uint OriginalOverride, bool OriginalEnabled, uint AppliedStamp, string Phase);
public interface IEnergySaverLedgerStore
{
    EnergySaverLedger? Load();
    void Save(EnergySaverLedger value);
    void Clear();
}

public sealed class FileEnergySaverLedgerStore(string? path = null) : IEnergySaverLedgerStore
{
    private readonly string file = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaomiPerformance", "energy-saver-link.json");
    public EnergySaverLedger? Load()
    {
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<EnergySaverLedger>(File.ReadAllText(file)) ?? throw new InvalidDataException("Windows 节能联动记录为空。");
    }
    public void Save(EnergySaverLedger value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Clear() => File.Delete(file);
}

/// <summary>Only ApplyAsync and ForceOffAsync mutate. Caller must hold the shared OEM operation mutex and confirm OEM mode first.</summary>
public sealed class EnergySaverLink(IWindowsEnergySaverApi? api = null, IEnergySaverLedgerStore? store = null)
{
    private readonly IWindowsEnergySaverApi windows = api ?? new WindowsEnergySaver();
    private readonly IEnergySaverLedgerStore ledgerStore = store ?? new FileEnergySaverLedgerStore();
    public EnergySaverCapture Read() => Capture();
    public EnergySaverCapture Capture()
    {
        try { return new(windows.Read(), null); }
        catch (Exception error) { return new(null, error.Message); }
    }

    public async Task<EnergySaverLinkResult> ApplyAsync(PerformanceMode mode, EnergySaverCapture before, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (!before.Success) return new(false, $"小米档位已确认，Windows 节能联动未执行：{before.Error}");
            var original = before.Snapshot!;
            var now = windows.Read();
            if (!now.SupportsWrites) return new(false, "小米档位已确认；当前 Windows 版本未验证节能写入，未执行联动。", now);
            if (original.BootId != now.BootId) return new(false, "开机周期发生变化，Windows 节能联动未执行。", now);
            var ledger = ledgerStore.Load();
            if (ledger is not null)
            {
                if (ledger.OriginalOverride > 2 || string.IsNullOrEmpty(ledger.BootId) || ledger.Phase is not ("pending-enable" or "owned" or "pending-restore" or "pending-force-off"))
                    throw new InvalidDataException("Windows 节能联动记录格式不受支持。");
                if (ledger.BootId != now.BootId)
                {
                    ledgerStore.Clear();
                    ledger = null; // WNF stamps are not comparable across boots.
                }
                else if (ledger.Phase != "owned")
                    return new(false, "发现未完成的 Windows 节能联动记录，未自动重试或覆盖当前设置。", now);
                else if (now.ChangeStamp != ledger.AppliedStamp || now.Override != 1)
                {
                    ledgerStore.Clear();
                    // Respect later external changes, even if the user selects green again.
                    return new(true, "检测到 Windows 节能设置已被外部修改，保留当前设置并解除本次联动。", now);
                }
            }
            if (original.ChangeStamp != now.ChangeStamp || original.Override != now.Override)
                return new(false, "切换期间 Windows 节能设置发生变化，未覆盖新设置。", now);

            bool green = mode is PerformanceMode.Eco or PerformanceMode.Quiet;
            if (green)
            {
                if (ledger is not null) return now.Enabled
                    ? new(true, "Windows 节能联动保持。", now)
                    : new(false, "Windows 节能开启请求仍在，但系统当前未实际开启；未重复写入。", now);
                if (original.Enabled || now.Enabled) return new(true, "Windows 节能原本已开启，保留原设置。", now);
                if (now.Override == 1) return new(false, "Windows 已存在开启请求但实际状态未开启，未重复写入。", now);
                var pending = new EnergySaverLedger(now.BootId, original.Override, original.Enabled, unchecked(now.ChangeStamp + 1), "pending-enable");
                cancellationToken.ThrowIfCancellationRequested();
                ledgerStore.Save(pending); // Preserve recovery information before the write.
                windows.WriteOverride(1, now.ChangeStamp);
                var after = await VerifyAsync(now, 1, true, cancellationToken);
                ledgerStore.Save(pending with { Phase = "owned" });
                return new(true, "已同时开启 Windows 节能模式。", after);
            }
            if (ledger is null) return new(true, "Windows 节能设置保持原状。", now);
            cancellationToken.ThrowIfCancellationRequested();
            ledgerStore.Save(ledger with { Phase = "pending-restore" });
            windows.WriteOverride(ledger.OriginalOverride, now.ChangeStamp);
            // Auto may legitimately become enabled as battery conditions change; restore policy, not old effective bool.
            bool? expected = ledger.OriginalOverride switch { 1 => true, 2 => false, _ => null };
            var restored = await VerifyAsync(now, ledger.OriginalOverride, expected, cancellationToken);
            ledgerStore.Clear();
            return new(true, "已恢复进入低功耗档前的 Windows 节能设置。", restored);
        }
        catch (Exception error)
        {
            return new(false, $"小米档位已确认，但 Windows 节能联动未完成：{error.Message}");
        }
    }

    /// <summary>For the confirmed automatic high-performance policy: force saver off and release any old green-mode ownership.</summary>
    public async Task<EnergySaverLinkResult> ForceOffAsync(EnergySaverCapture before, CancellationToken cancellationToken = default)
    {
        EnergySaverSnapshot? observed = null;
        try
        {
            if (!before.Success) return new(false, $"小米高性能档已确认，Windows 节能关闭未执行：{before.Error}");
            var original = before.Snapshot!;
            var now = observed = windows.Read();
            if (!now.SupportsWrites) return new(false, "小米高性能档已确认；当前 Windows 版本未验证节能写入，未关闭节能。", now);
            if (original.BootId != now.BootId) return new(false, "开机周期发生变化，Windows 节能关闭未执行。", now);

            var ledger = ledgerStore.Load();
            if (ledger is not null)
            {
                if (ledger.OriginalOverride > 2 || string.IsNullOrEmpty(ledger.BootId) || ledger.Phase is not ("pending-enable" or "owned" or "pending-restore" or "pending-force-off"))
                    throw new InvalidDataException("Windows 节能联动记录格式不受支持。");
                if (ledger.BootId != now.BootId)
                {
                    ledgerStore.Clear();
                    ledger = null;
                }
                else if (ledger.Phase != "owned")
                    return new(false, "发现未完成的 Windows 节能联动记录，未自动重试关闭或覆盖当前设置。", now);
            }
            if (original.ChangeStamp != now.ChangeStamp || original.Override != now.Override)
                return new(false, "切换期间 Windows 节能设置发生变化，未覆盖新设置。", now);

            cancellationToken.ThrowIfCancellationRequested();
            if (now.Override == 2 && !now.Enabled)
            {
                if (ledger is not null) ledgerStore.Clear();
                return new(true, "Windows 节能已关闭，已解除此前低功耗档的联动。", now);
            }

            // This policy deliberately replaces the old restore baseline. A pending record must
            // survive failed verification so neither automatic replay nor manual restore can hide it.
            var pending = new EnergySaverLedger(now.BootId, now.Override, now.Enabled, unchecked(now.ChangeStamp + 1), "pending-force-off");
            ledgerStore.Save(pending);
            windows.WriteOverride(2, now.ChangeStamp);
            observed = await VerifyAsync(now, 2, false, cancellationToken);
            ledgerStore.Clear();
            return new(true, "已关闭 Windows 节能模式，并解除此前低功耗档的联动。", observed);
        }
        catch (Exception error)
        {
            return new(false, $"小米高性能档已确认，但 Windows 节能关闭未完成：{error.Message}", observed);
        }
    }

    private async Task<EnergySaverSnapshot> VerifyAsync(EnergySaverSnapshot before, uint value, bool? enabled, CancellationToken cancellationToken)
    {
        uint expectedStamp = unchecked(before.ChangeStamp + 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var after = windows.Read();
            if (after.BootId != before.BootId || after.ChangeStamp != expectedStamp || after.Override != value)
                throw new InvalidOperationException("Windows 节能写入后出现并发变化，未认领或继续写入。");
            if (enabled is null || after.Enabled == enabled) return after;
            if (watch.Elapsed >= TimeSpan.FromSeconds(2))
                throw new TimeoutException("Windows 节能请求已发送，但两秒内未确认实际状态；保留待检查记录。");
            await Task.Delay(100, cancellationToken);
        }
    }
}
