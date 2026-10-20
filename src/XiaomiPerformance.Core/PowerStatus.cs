using System.Runtime.InteropServices;

namespace XiaomiPerformance;

public readonly record struct PowerReading(byte AcLineStatus, byte BatteryPercent)
{
    public bool IsKnown => AcLineStatus is 0 or 1 && BatteryPercent <= 100;
    public bool IsEligible => IsKnown && AcLineStatus == 1 && BatteryPercent > 95;
}

public interface IPowerStatusSource
{
    PowerReading Read();
}

/// <summary>Reads the documented Windows power-status API without changing power settings.</summary>
public sealed class WindowsPowerStatusSource : IPowerStatusSource
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus value);

    public PowerReading Read() => GetSystemPowerStatus(out var status)
        ? new(status.AcLineStatus, status.BatteryLifePercent)
        : new(byte.MaxValue, byte.MaxValue);
}
