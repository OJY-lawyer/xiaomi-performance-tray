using System.Text.Json;

namespace XiaomiPerformance;

/// <summary>Only reads/writes the explicitly identified local simulation JSON file.</summary>
public sealed class SimulationBackend(string stateFile) : IPerformanceBackend
{
    public string StateFile { get; } = Path.GetFullPath(stateFile);
    public string Name => "本地模拟";
    public bool SupportsWrites => true;
    public bool IsSimulation => true;

    public async Task<ModeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var mode = PerformanceMode.Smart;
        if (File.Exists(StateFile))
        {
            await using var file = new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<SimulationState>(file, JsonFormat.Options, cancellationToken)
                ?? throw new InvalidDataException("模拟状态文件为空。");
            if (state.Schema != "xiaomi-performance-simulation/v1" || !Enum.IsDefined(state.Mode))
                throw new InvalidDataException("模拟状态文件格式无效。");
            mode = state.Mode;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(mode, ReadingKind.Simulation, DateTimeOffset.UtcNow, Name, true,
            "仅模拟状态；没有读取或改变电脑的真实性能模式。");
    }

    public async Task SetModeAsync(PerformanceMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var parent = Path.GetDirectoryName(StateFile)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(StateFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                await JsonSerializer.SerializeAsync(file, new SimulationState("xiaomi-performance-simulation/v1", mode), JsonFormat.Options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, StateFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SimulationState(string Schema, PerformanceMode Mode);
}
