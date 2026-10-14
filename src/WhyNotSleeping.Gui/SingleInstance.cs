namespace WhyNotSleeping.Gui;

/// <summary>
/// One copy at a time: starting the app again (or at sign-in while it already runs) brings the running window forward
/// instead of adding a second tray icon.
/// </summary>
internal static class SingleInstance
{
    const string MutexName = @"Local\WhyNotSleeping";
    static Mutex? _mutex;

    /// <summary>Asks the running copy to show its window; its form listens for it.</summary>
    public static readonly uint ShowMessage = Native.RegisterWindowMessage("WhyNotSleeping.Show");

    /// <summary>True if this is the only copy (and now holds the name); false if another is running.</summary>
    public static bool Claim()
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out var created);
            if (created)
            {
                _mutex = mutex;
                return true;
            }
            mutex.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // it exists, held by an elevated copy
        }
    }

    /// <summary>Lets go before relaunching elevated, so the new copy doesn't mistake this one for a running copy.</summary>
    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    public static void ShowRunningCopy()
    {
        Native.AllowSetForegroundWindow(-1); // ASFW_ANY: let it come to the front
        Native.PostMessage(Native.HwndBroadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }
}
