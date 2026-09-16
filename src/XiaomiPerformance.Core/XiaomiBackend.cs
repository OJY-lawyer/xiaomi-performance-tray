using Microsoft.Win32;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaomiPerformance;

/// <summary>
/// A narrow adapter to the existing Xiaomi service. It never sends performance GET
/// (which has brightness side effects), never writes the mode registry, and never
/// calls WMI/EC/Intel tuning setters. Status is the original manager's published
/// mode, corroborated by a notification in the current manager/service lifetime.
/// </summary>
public sealed class XiaomiBackend : IPerformanceBackend
{
    internal const string PipeName = "E09DF953-5B84-4CB7-A23B-969218E24781";
    internal const string RegistryPath = @"Software\MI\SvrCModule\PerformanceMode\POWER";
    internal const string ManagerRoot = @"C:\Program Files\MI\XiaomiPCManager\5.8.1.121";
    internal const string ServiceRoot = @"C:\Program Files (x86)\Timi Personal Computing\MiService\4.1.1.648\MiDeviceService";
    internal const string ManagerLog = @"C:\ProgramData\MI\XiaomiPcManager\Log\XiaomiPcManager_Log.txt";
    private readonly PublishedModeLog log = new(ManagerLog);
    private readonly Dictionary<string, (long Length, DateTime Modified)> checkedFiles = new();
    private RuntimeState? runtime;
    private DateTime runtimeChecked;
    public string Name => "小米电脑管家";
    public bool SupportsWrites => true;
    public bool IsSimulation => false;

    public Task<ModeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var current = InspectRuntime();
            var published = ReadPublished(current);
            bool powerKnown = Native.GetSystemPowerStatus(out var power) && power.AcLineStatus != 255;
            bool chargerKnown = log.StandardCharger is not null && log.StandardCharger.At >= current.ManagerStarted && log.StandardCharger.At >= current.ServiceStarted;
            IReadOnlyList<PerformanceMode> allowed = !powerKnown || (power.AcLineStatus == 1 && !chargerKnown)
                ? [PerformanceMode.Smart]
                : power.AcLineStatus == 1 && log.StandardCharger?.Raw == 1 ? PerformanceModes.RatedCycle : PerformanceModes.Cycle;
            var detail = $"管家已发布的{published.Mode.Label()}模式；对应原厂通知 {published.At.LocalDateTime:MM-dd HH:mm:ss}。仅读取状态，不轮询带副作用的原厂 GET。";
            detail += allowed.Count == 1 ? " 供电资格尚未确认，暂仅开放智能。" : $" 当前原厂档位组：{string.Join("／", allowed.Select(x => x.Label()))}。";
            return Task.FromResult(new ModeStatus(published.Mode, ReadingKind.Confirmed, DateTimeOffset.UtcNow, Name, true, detail)
                { SourceUpdatedAt = published.At, AllowedTargets = allowed });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Task.FromResult(ModeStatus.Unknown(Name, error.Message));
        }
    }

    public async Task SetModeAsync(PerformanceMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var current = InspectRuntime(force: true);
        var before = ReadPublished(current);
        if (before.Mode == mode) return;
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(cancellationToken);
        pipe.ReadMode = PipeTransmissionMode.Message;
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid) || serverPid != current.ServicePid)
            throw new InvalidOperationException("连接到的进程不是当前小米设备服务，已停止切换。");

        // IFOK is a verified read-only eligibility query. A malformed OEM reply can
        // default to 4 (restricted), but 5 requires the validated nonstandard branch.
        await pipe.WriteAsync(VendorProtocol.ChargerQuery, cancellationToken);
        var charger = await ReceiveUntilAsync(pipe, frame => frame.Operation == 3, cancellationToken);
        if (charger.Value is not (4 or 5)) throw new InvalidDataException("无法确认原厂适配器资格，未发送切换请求。");
        if (!Native.GetSystemPowerStatus(out var power) || power.AcLineStatus == 255)
            throw new InvalidOperationException("无法确认供电状态，未发送切换请求。");
        bool rated = power.AcLineStatus == 1 && charger.Value == 4;
        var permitted = rated ? PerformanceModes.RatedCycle : PerformanceModes.Cycle;
        if (!permitted.Contains(mode))
            throw new InvalidOperationException("供电条件对应的原厂档位组已变化，请刷新后按当前名称选择。");

        // Do not retry SET after a timeout or uncertain reply. Only three reviewed
        // mode values in the active group are allowed; the original service owns all side effects.
        var sentAt = DateTimeOffset.Now;
        await pipe.WriteAsync(VendorProtocol.SetRequest(mode), cancellationToken);
        bool acknowledged = false, observed = false;
        while (!acknowledged || !observed)
        {
            var frame = await ReceiveUntilAsync(pipe, frame => frame.Operation is 1 or 2, cancellationToken);
            if (frame.Operation == 1)
            {
                if (frame.Value != 4) throw new InvalidOperationException("原厂服务未接受模式切换。请查看管家当前状态。");
                acknowledged = true;
            }
            else if (frame.Value == VendorProtocol.ModeCode(mode)) observed = true;
        }
        // A success ACK alone is insufficient: the existing manager must receive its
        // notification and publish the same mode, preserving its Bluetooth/Share path.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = ReadPublished(current, allowPending: true);
            if (after.Mode == mode && after.At >= sentAt.AddSeconds(-1)) return;
            await Task.Delay(70, cancellationToken);
        }
    }

    private static async Task<VendorFrame> ReceiveUntilAsync(NamedPipeClientStream pipe,
        Func<VendorFrame, bool> predicate, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        while (true)
        {
            int total = 0;
            do
            {
                int received = await pipe.ReadAsync(buffer.AsMemory(total), token);
                if (received == 0) throw new EndOfStreamException("小米服务已断开。");
                total += received;
                if (total == buffer.Length && !pipe.IsMessageComplete) throw new InvalidDataException("原厂消息超出预期大小。");
            } while (!pipe.IsMessageComplete);
            if (VendorProtocol.TryParse(buffer.AsSpan(0, total), out var frame) && predicate(frame)) return frame;
        }
    }

    private PublishedMode ReadPublished(RuntimeState state, bool allowPending = false)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        if (key?.GetValue("ifDecepticon") is not int raw || !VendorProtocol.TryMode(raw, out var mode))
            throw new InvalidOperationException("管家当前档位不属于省电／智能／极速三档，或尚未同步。请先打开管家“电池与性能”页确认。");
        log.Refresh();
        var entry = log.Latest;
        if (entry is null || entry.At.UtcDateTime < state.ManagerStarted.UtcDateTime || entry.At.UtcDateTime < state.ServiceStarted.UtcDateTime)
            throw new InvalidOperationException("缺少本次原厂运行期间的模式通知，请先打开管家“电池与性能”页同步状态。");
        if (entry.Raw != raw)
        {
            if (allowPending) return new PublishedMode(mode, DateTimeOffset.MinValue);
            throw new InvalidOperationException("管家模式正在更新，等待下一次读取。");
        }
        return new PublishedMode(mode, entry.At);
    }

    private RuntimeState InspectRuntime(bool force = false)
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (!Equals(bios?.GetValue("SystemManufacturer"), "XIAOMI") || !Equals(bios?.GetValue("BaseBoardProduct"), "TM2425"))
            throw new InvalidOperationException("此版本仅核验过本机 REDMI Book Pro 16 2026（TM2425），当前机型不在支持范围。");
        var managers = Process.GetProcessesByName("XiaomiPcManager");
        var services = Process.GetProcessesByName("MiDeviceService");
        var tuning = Process.GetProcessesByName("MiSmartTuning");
        try
        {
            if (managers.Length != 1 || services.Length != 1 || tuning.Length != 1)
                throw new InvalidOperationException("请保持小米电脑管家及原厂设备、智能调度服务正常运行，再使用托盘。");
            if (!string.Equals(managers[0].MainModule?.FileName, Path.Combine(ManagerRoot, "XiaomiPcManager.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("管家版本或路径已变化，需要重新核验接口。");
            using var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MiService");
            if (serviceKey?.GetValue("ImagePath") is not string imagePath || !imagePath.Contains(@"\MiService\4.1.1.648\MiService.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("小米服务版本已变化，需要重新核验接口。");
            CheckFile(Path.Combine(ManagerRoot, "SvrCModule.dll"), "5E4FE0B950CCC280E5E37566B56A11AB0F2D5DE3E686481797A30A038ED3E55C", force);
            CheckFile(Path.Combine(ServiceRoot, "MiDeviceServiceEntry.dll"), "F292C9DAE9AAC37BBCE14F1A65179BCF53B0A328F28B1FD8F452DF3968A4A268", force);
            CheckFile(Path.Combine(ServiceRoot, "MiSmartTuning.exe"), "A07CBF86993289BC73A7D2B1500A1EF81C5F2903CEDD4848F9EE2D28DC01602E", force);
            if (force || runtime is null || runtime.ServicePid != services[0].Id || runtime.ManagerPid != managers[0].Id || DateTime.UtcNow - runtimeChecked > TimeSpan.FromMinutes(1))
            {
                runtime = new RuntimeState(managers[0].Id, services[0].Id,
                    new DateTimeOffset(managers[0].StartTime), ReadServiceStart(services[0].Id));
                runtimeChecked = DateTime.UtcNow;
            }
            return runtime;
        }
        finally { foreach (var process in managers.Concat(services).Concat(tuning)) process.Dispose(); }
    }

    private void CheckFile(string path, string expected, bool force)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("缺少已核验的原厂文件，请先恢复小米电脑管家。", path);
        if (!force && checkedFiles.TryGetValue(path, out var check) && check.Length == info.Length && check.Modified == info.LastWriteTimeUtc) return;
        using var input = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(input)) != expected)
            throw new InvalidOperationException("原厂文件已更新，与已核验版本不符；为避免错误调用，已暂停切换。");
        checkedFiles[path] = (info.Length, info.LastWriteTimeUtc);
    }

    private static DateTimeOffset ReadServiceStart(int pid)
    {
        // WMI process metadata is readable without opening Xiaomi's protected process.
        // This is a SELECT in root/cimv2, not a vendor root/wmi method invocation.
        object? locator = null, connection = null, row = null, properties = null, property = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            connection = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
            row = ((dynamic)connection).Get($"Win32_Process.Handle=\"{pid}\"");
            properties = ((dynamic)row).Properties_;
            property = ((dynamic)properties).Item("CreationDate", 0);
                    string value = (string)((dynamic)property).Value;
                    var date = DateTime.ParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
                    var micros = int.Parse(value.Substring(15, 6), CultureInfo.InvariantCulture);
                    var offset = int.Parse(value.Substring(22, 3), CultureInfo.InvariantCulture) * (value[21] == '-' ? -1 : 1);
                    return new DateTimeOffset(date.AddTicks(micros * 10L), TimeSpan.FromMinutes(offset));
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            if (properties is not null) Marshal.FinalReleaseComObject(properties);
            if (row is not null) Marshal.FinalReleaseComObject(row);
            if (connection is not null) Marshal.FinalReleaseComObject(connection);
            if (locator is not null) Marshal.FinalReleaseComObject(locator);
        }
    }

    private sealed record RuntimeState(int ManagerPid, int ServicePid, DateTimeOffset ManagerStarted, DateTimeOffset ServiceStarted);
    private sealed record PublishedMode(PerformanceMode Mode, DateTimeOffset At);
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct PowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out int processId);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSystemPowerStatus(out PowerStatus status);
    }
}

public readonly record struct VendorFrame(int Operation, int Value);
public static class VendorProtocol
{
    public static byte[] ChargerQuery => [0, 0, 0, 0, 12, 8, 3];
    public static int ModeCode(PerformanceMode mode) => mode switch
    { PerformanceMode.Eco => 11, PerformanceMode.Smart => 10, PerformanceMode.Turbo => 2, PerformanceMode.Quiet => 0, PerformanceMode.Rage => 3, _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
    public static byte[] SetRequest(PerformanceMode mode) => [0, 0, 0, 0, 12, 8, 1, 16, (byte)ModeCode(mode)];
    public static bool TryMode(int raw, out PerformanceMode mode)
    { mode = raw switch { 11 => PerformanceMode.Eco, 10 => PerformanceMode.Smart, 2 => PerformanceMode.Turbo, 0 => PerformanceMode.Quiet, 3 => PerformanceMode.Rage, _ => (PerformanceMode)(-1) }; return Enum.IsDefined(mode); }
    public static bool TryParse(ReadOnlySpan<byte> bytes, out VendorFrame frame)
    {
        frame = default;
        if (bytes.Length < 5 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0 || bytes[4] != 12) return false;
        int op = 0, value = 0, position = 5; bool sawOp = false, sawValue = false;
        while (position < bytes.Length)
        {
            if (!ReadVarint(bytes, ref position, out var tag) || (tag is not (8 or 16)) || !ReadVarint(bytes, ref position, out var number)) return false;
            if (tag == 8) { if (sawOp) return false; op = number; sawOp = true; }
            else { if (sawValue) return false; value = number; sawValue = true; }
        }
        if (op is not (0 or 1 or 2 or 3)) return false;
        frame = new(op, value); return true;
    }
    private static bool ReadVarint(ReadOnlySpan<byte> bytes, ref int position, out int value)
    {
        value = 0;
        for (int shift = 0; shift <= 28 && position < bytes.Length; shift += 7)
        {
            byte b = bytes[position++];
            if (shift == 28 && b > 7) return false;
            value |= (b & 127) << shift;
            if ((b & 128) == 0) return true;
        }
        return false;
    }
}

internal sealed class PublishedModeLog(string path)
{
    private long position;
    private DateTime created;
    public sealed record Entry(int Raw, DateTimeOffset At);
    public Entry? Latest { get; private set; }
    public Entry? StandardCharger { get; private set; }
    private static readonly Regex ModeLine = new(@"^(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) .*ApplicationSideActionService-HandleWorkLoadModeChange updated _currentPerformanceMode to: .*\((?<mode>\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ChargerLine = new(@"^(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) .*HandleGetWorkLoadModeDeceptionEnableSuccess enable: (?<mode>[01]), _isStandardCharger:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public void Refresh()
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("缺少管家的模式通知记录，无法确认状态。", path);
        if (info.CreationTimeUtc != created || info.Length < position) { position = 0; created = info.CreationTimeUtc; }
        if (info.Length == position) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length - position > 64 * 1024 * 1024) throw new InvalidDataException("管家日志过大，需重新核验读取范围。");
        stream.Position = position;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var bytes = buffer.ToArray(); int lastNewline = Array.LastIndexOf(bytes, (byte)10);
        if (lastNewline < 0) return;
        var text = Encoding.UTF8.GetString(bytes, 0, lastNewline + 1);
        foreach (var line in text.Split('\n'))
        {
            var match = ModeLine.Match(line);
            if (match.Success && DateTimeOffset.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                Latest = new Entry(int.Parse(match.Groups["mode"].Value, CultureInfo.InvariantCulture), at);
            var charger = ChargerLine.Match(line);
            if (charger.Success && DateTimeOffset.TryParse(charger.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var chargerAt))
                StandardCharger = new Entry(int.Parse(charger.Groups["mode"].Value, CultureInfo.InvariantCulture), chargerAt);
        }
        position += lastNewline + 1;
    }
}
