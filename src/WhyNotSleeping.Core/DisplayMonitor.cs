using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WhyNotSleeping;

internal enum DisplayState { Unknown = -1, Off = 0, On = 1, Dimmed = 2 }

/// <summary>A display state transition, with how long the user had been idle when it happened.</summary>
internal sealed record DisplayChange(DateTime Time, DisplayState State, TimeSpan UserIdle);

/// <summary>
/// Tracks whether the screen is actually on, off or dimmed via GUID_CONSOLE_DISPLAY_STATE notifications.
/// Windows sends the current state right after registering, then one notification per change.
/// </summary>
internal static class DisplayMonitor
{
    static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    // The callback runs on a system thread; the main loop drains these changes.
    static readonly ConcurrentQueue<DisplayChange> Pending = new();
    static Native.DeviceNotifyCallback? _callback; // kept alive for the lifetime of the registration
    static IntPtr _handle;

    public static DisplayState State { get; private set; } = DisplayState.Unknown;
    /// <summary>When the current state began; null until the first transition is seen.</summary>
    public static DateTime? Since { get; private set; }
    public static bool Available { get; private set; }

    public static void Start()
    {
        _callback = OnNotify;
        var parameters = new Native.DeviceNotifySubscribeParameters { Callback = _callback };
        var guid = ConsoleDisplayState;
        Available = Native.PowerSettingRegisterNotification(ref guid, Native.DeviceNotifyCallbackFlag, ref parameters, out _handle) == 0;
    }

    /// <summary>For one-shot runs: give Windows a moment to deliver the initial state.</summary>
    public static void WaitForInitialState(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Available && Pending.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        Drain();
    }

    /// <summary>Applies changes received since the last call and returns the transitions (not the initial state).</summary>
    public static List<DisplayChange> Drain()
    {
        var changes = new List<DisplayChange>();
        while (Pending.TryDequeue(out var change))
        {
            if (change.State == State) continue;
            if (State != DisplayState.Unknown)
            {
                Since = change.Time;
                changes.Add(change);
            }
            State = change.State;
        }
        return changes;
    }

    static uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        if (type != Native.PbtPowerSettingChange || setting == IntPtr.Zero)
            return 0;

        // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[]
        var guid = Marshal.PtrToStructure<Guid>(setting);
        var length = Marshal.ReadInt32(setting, 16);
        if (guid != ConsoleDisplayState || length < 4)
            return 0;

        var state = (DisplayState)Marshal.ReadInt32(setting, 20);
        Pending.Enqueue(new DisplayChange(DateTime.Now, state, PowerState.CaptureUserIdle()));
        return 0;
    }
}
