using System.Runtime.InteropServices;
using System.Text;

namespace WhyNotSleeping;

/// <summary>Power plan timeouts, power source, user idle time and the aggregate execution state.</summary>
internal sealed record PowerState(
    string SchemeName,
    bool OnAC,
    int? BatteryPercent,
    uint SleepAc, uint SleepDc,
    uint HibernateAc, uint HibernateDc,
    uint DisplayAc, uint DisplayDc,
    TimeSpan UserIdle,
    uint? ExecutionState)
{
    static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");
    static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    /// <summary>Sleep timeout in seconds for the current power source (0 = never).</summary>
    public uint SleepTimeout => OnAC ? SleepAc : SleepDc;

    /// <summary>Display-off timeout in seconds for the current power source (0 = never).</summary>
    public uint DisplayTimeout => OnAC ? DisplayAc : DisplayDc;

    public static TimeSpan CaptureUserIdle()
    {
        var lii = new Native.LastInputInfo { cbSize = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
        return Native.GetLastInputInfo(ref lii)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - lii.dwTime))
            : TimeSpan.Zero;
    }

    public static PowerState Capture()
    {
        var scheme = Guid.Empty;
        var schemeName = "(unknown)";
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) == 0)
        {
            scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            Native.LocalFree(schemePtr);
            schemeName = ReadSchemeName(scheme);
        }

        Native.GetSystemPowerStatus(out var status);
        var onAC = status.ACLineStatus != 0; // 1 = online, 255 = unknown (desktops)
        int? battery = (status.BatteryFlag & 128) != 0 || status.BatteryLifePercent == 255
            ? null
            : status.BatteryLifePercent;

        var idle = CaptureUserIdle();

        uint? execState = Native.CallNtPowerInformation(Native.SystemExecutionState, IntPtr.Zero, 0, out var es, 4) == 0
            ? es
            : null;

        return new PowerState(
            schemeName, onAC, battery,
            Read(scheme, SubSleep, StandbyIdle, ac: true), Read(scheme, SubSleep, StandbyIdle, ac: false),
            Read(scheme, SubSleep, HibernateIdle, ac: true), Read(scheme, SubSleep, HibernateIdle, ac: false),
            Read(scheme, SubVideo, VideoIdle, ac: true), Read(scheme, SubVideo, VideoIdle, ac: false),
            idle, execState);
    }

    static uint Read(Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        uint value;
        var result = ac
            ? Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value)
            : Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        return result == 0 ? value : 0;
    }

    static string ReadSchemeName(Guid scheme)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return scheme.ToString();
        var buffer = new byte[size];
        if (Native.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
            return scheme.ToString();
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static string DescribeExecutionState(uint state)
    {
        var flags = new List<string>();
        if ((state & 0x00000001) != 0) flags.Add("SYSTEM_REQUIRED");
        if ((state & 0x00000002) != 0) flags.Add("DISPLAY_REQUIRED");
        if ((state & 0x00000004) != 0) flags.Add("USER_PRESENT");
        if ((state & 0x00000040) != 0) flags.Add("AWAYMODE_REQUIRED");
        if ((state & 0x80000000) != 0) flags.Add("CONTINUOUS");
        return flags.Count == 0 ? "none" : string.Join(" | ", flags);
    }
}
