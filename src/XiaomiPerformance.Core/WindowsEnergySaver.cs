using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XiaomiPerformance;

public sealed record EnergySaverSnapshot(uint Override, uint ChangeStamp, bool Enabled, byte AcLineStatus, int WindowsBuild, string BootId)
{
    public bool SupportsWrites => WindowsBuild == 28000;
    public bool? LegacyBatterySaverEnabled { get; init; }
    public uint? UiStateFlags { get; init; }
}

public interface IWindowsEnergySaverApi
{
    EnergySaverSnapshot Read();
    void WriteOverride(uint value, uint expectedStamp);
}

/// <summary>Private Windows WNF read/write ABI, validated on build 28000 only. Effective state matches the Windows Settings UI.</summary>
public sealed class WindowsEnergySaver : IWindowsEnergySaverApi
{
    private const ulong OverrideName = 0x41c6013da3bc3075;
    private const ulong UiStateName = 0x41840b3ea3bdf075;
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte Ac, BatteryFlag, BatteryPercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus value);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryWnfStateData(ref ulong name, IntPtr typeId, IntPtr scope, out uint stamp, out uint value, ref uint length);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryWnfStateData", ExactSpelling = true)]
    private static extern int NtQueryWnfUiState(ref ulong name, IntPtr typeId, IntPtr scope, out uint stamp, out ulong value, ref uint length);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtUpdateWnfStateData(ref ulong name, ref uint value, uint length, IntPtr typeId, IntPtr scope, uint matchingStamp, uint checkStamp);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(uint informationClass, [Out] byte[] buffer, uint length, out uint returnedLength);

    public EnergySaverSnapshot Read()
    {
        if (Environment.OSVersion.Version.Build != 28000)
            throw new NotSupportedException("Windows 节能状态布局仅验证过 build 28000，当前版本未启用联动读取和写入。");
        ulong name = OverrideName;
        uint length = 4;
        Check(NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out uint stamp, out uint value, ref length), "读取覆盖值");
        if (length != 4 || value > 2) throw new NotSupportedException($"Windows 节能接口返回未知格式：长度 {length}，值 {value}。");
        if (!GetSystemPowerStatus(out var power)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (power.SystemStatusFlag > 1) throw new NotSupportedException("Windows 节能状态未知。");
        ulong uiName = UiStateName;
        uint uiLength = 8;
        Check(NtQueryWnfUiState(ref uiName, IntPtr.Zero, IntPtr.Zero, out _, out ulong uiState, ref uiLength), "读取系统界面节能状态");
        if (uiLength != 8) throw new NotSupportedException($"Windows 节能界面状态长度未知：{uiLength}。");
        uint flags = unchecked((uint)uiState);
        // Same mask as SettingsHandlers_OneCore_BatterySaver.dll callback (build 28000).
        // Legacy SystemStatusFlag stays 0 for AC Energy Saver and is diagnostic only.
        bool enabled = (flags & 0x003FC000) != 0;
        byte[] boot = new byte[32];
        Check(NtQuerySystemInformation(90, boot, (uint)boot.Length, out uint returned), "读取开机标识");
        if (returned < 16 || returned > boot.Length) throw new NotSupportedException("Windows 开机标识格式未知。");
        string bootId = new Guid(boot.AsSpan(0, 16)).ToString("D");
        if (bootId == Guid.Empty.ToString("D")) throw new NotSupportedException("Windows 开机标识不可用。");
        return new(value, stamp, enabled, power.Ac, Environment.OSVersion.Version.Build, bootId)
        {
            LegacyBatterySaverEnabled = power.SystemStatusFlag == 1,
            UiStateFlags = flags
        };
    }

    public void WriteOverride(uint value, uint expectedStamp)
    {
        if (Environment.OSVersion.Version.Build != 28000) throw new NotSupportedException("Windows 节能写入仅验证过 build 28000，当前版本未启用联动写入。");
        if (value > 2) throw new ArgumentOutOfRangeException(nameof(value));
        ulong name = OverrideName;
        // LOGICAL is 32 bits. CheckStamp=1 makes this an atomic conditional write.
        Check(NtUpdateWnfStateData(ref name, ref value, 4, IntPtr.Zero, IntPtr.Zero, expectedStamp, 1), "条件写入");
    }

    private static void Check(int status, string operation)
    {
        if (status < 0) throw new InvalidOperationException($"Windows 节能{operation}失败：NTSTATUS=0x{unchecked((uint)status):X8}。未自动重试。");
    }
}
