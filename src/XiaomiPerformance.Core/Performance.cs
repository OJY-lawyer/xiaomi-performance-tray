using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiaomiPerformance;

public enum PerformanceMode { Eco, Smart, Turbo, Quiet, Rage }
public enum ReadingKind { Unknown, Confirmed, Simulation }

public static class PerformanceModes
{
    public static readonly PerformanceMode[] Cycle = [PerformanceMode.Eco, PerformanceMode.Smart, PerformanceMode.Turbo];
    public static readonly PerformanceMode[] RatedCycle = [PerformanceMode.Quiet, PerformanceMode.Smart, PerformanceMode.Rage];
    public static PerformanceMode NextInGroup(this ModeStatus state)
    {
        var group = state.AllowedTargets;
        if (group is { Count: 3 })
        {
            for (int i = 0; i < group.Count; i++)
                if (group[i] == state.Mode) return group[(i + 1) % group.Count];
            return PerformanceMode.Smart;
        }
        return state.Mode!.Value.Next();
    }
    public static string Label(this PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => "省电", PerformanceMode.Smart => "智能", PerformanceMode.Turbo => "极速",
        PerformanceMode.Quiet => "静谧", PerformanceMode.Rage => "狂暴",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static PerformanceMode Next(this PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => PerformanceMode.Smart, PerformanceMode.Smart => PerformanceMode.Turbo,
        PerformanceMode.Turbo => PerformanceMode.Eco, PerformanceMode.Quiet or PerformanceMode.Rage => PerformanceMode.Smart,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static bool TryParse(string value, out PerformanceMode mode)
    {
        mode = value.ToLowerInvariant() switch
        {
            "eco" or "省电" => PerformanceMode.Eco,
            "smart" or "智能" => PerformanceMode.Smart,
            "turbo" or "极速" => PerformanceMode.Turbo,
            "quiet" or "静谧" => PerformanceMode.Quiet,
            "rage" or "狂暴" => PerformanceMode.Rage,
            _ => (PerformanceMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
}

public sealed record ModeStatus(
    PerformanceMode? Mode, ReadingKind Reading, DateTimeOffset ObservedAt,
    string Backend, bool CanSet, string Detail)
{
    public DateTimeOffset? SourceUpdatedAt { get; init; }
    public IReadOnlyList<PerformanceMode>? AllowedTargets { get; init; }
    public bool IsKnown => Mode.HasValue && Reading != ReadingKind.Unknown;
    public string ModeLabel => Mode?.Label() ?? "未知";
    public static ModeStatus Unknown(string backend, string detail) =>
        new(null, ReadingKind.Unknown, DateTimeOffset.UtcNow, backend, false, detail);
}

/// <summary>
/// Integration contract: reads must report actual device state, not a cached request.
/// A successful SetModeAsync means the native operation finished; the controller then
/// reads again and requires the requested mode to be confirmed. Respect cancellation.
/// SupportsWrites must remain false until the native write path is reviewed and enabled.
/// Never map generic Windows power plans to a Xiaomi mode without evidence.
/// </summary>
public interface IPerformanceBackend
{
    string Name { get; }
    bool SupportsWrites { get; }
    bool IsSimulation { get; }
    Task<ModeStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task SetModeAsync(PerformanceMode mode, CancellationToken cancellationToken);
}

public static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>No WMI, driver, registry, powercfg, service, or vendor calls.</summary>
public sealed class UnavailableBackend : IPerformanceBackend
{
    public string Name => "未接入小米接口";
    public bool SupportsWrites => false;
    public bool IsSimulation => false;
    public Task<ModeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ModeStatus.Unknown(Name, "真实读写接口尚未接入，无法读取或切换电脑模式。"));
    }
    public Task SetModeAsync(PerformanceMode mode, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("真实模式切换尚未启用。"));
}

public static class BackendFactory
{
    public static ModeController CreateController(AppOptions options) =>
        new(Create(options), options.Simulate ? null : new EnergySaverLink());
    public static IPerformanceBackend Create(AppOptions options) => options.Simulate
        ? new SimulationBackend(options.StateFile ?? Path.Combine(AppContext.BaseDirectory, "simulation-state.json"))
        : new XiaomiBackend();
}

public sealed record AppOptions(bool Simulate, string? StateFile, bool Json, string Command, string? Mode)
{
    public static AppOptions Parse(string[] args, string defaultCommand)
    {
        bool simulate = false, json = false;
        string? stateFile = null;
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--simulate": simulate = true; break;
                case "--json": json = true; break;
                case "--state-file":
                    if (++i >= args.Length || args[i].StartsWith("--"))
                        throw new ArgumentException("--state-file 后需要模拟状态文件路径。");
                    stateFile = Path.GetFullPath(args[i]); break;
                case "--help" or "-h": positionals.Add("help"); break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"未知参数：{args[i]}");
                    positionals.Add(args[i]); break;
            }
        }
        if (stateFile is not null && !simulate) throw new ArgumentException("--state-file 仅可与 --simulate 一起使用。");
        if (positionals.Count > 2) throw new ArgumentException("参数过多。");
        var command = positionals.FirstOrDefault()?.ToLowerInvariant() ?? defaultCommand;
        var mode = positionals.ElementAtOrDefault(1);
        if (command is not ("status" or "set" or "next" or "tray" or "help"))
            throw new ArgumentException($"未知命令：{command}");
        if (command == "set" && (mode is null || !PerformanceModes.TryParse(mode, out _)))
            throw new ArgumentException("set 后需要 eco / smart / turbo，或省电 / 智能 / 极速。");
        if (command != "set" && mode is not null) throw new ArgumentException("该命令不接受模式参数。");
        return new(simulate, stateFile, json, command, mode);
    }
}
