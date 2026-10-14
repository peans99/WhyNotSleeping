using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WhyNotSleeping;

internal sealed class RequestTracker(string? logPath)
{
    readonly Dictionary<string, (PowerRequest Request, DateTime Since)> _active = new();
    readonly List<string> _events = new();

    public IEnumerable<(PowerRequest Request, DateTime Since)> Active => _active.Values;
    public IReadOnlyList<string> Events => _events;
    /// <summary>Events recorded since start, including ones dropped from <see cref="Events"/>.</summary>
    public long TotalEvents { get; private set; }

    public void Update(List<PowerRequest> current, DateTime now)
    {
        var keys = current.Select(r => r.Key).ToHashSet();
        foreach (var gone in _active.Where(kv => !keys.Contains(kv.Key)).ToList())
        {
            _active.Remove(gone.Key);
            Event(now, $"- released: {gone.Value.Request.Describe()} (held {Format.Duration(now - gone.Value.Since)})");
        }
        foreach (var request in current.Where(r => !_active.ContainsKey(r.Key)))
        {
            _active[request.Key] = (request, now);
            Event(now, $"+ new:      {request.Describe()}");
        }
    }

    public void Event(DateTime when, string message)
    {
        var line = $"{when:yyyy-MM-dd HH:mm:ss} {message}";
        _events.Add(line);
        TotalEvents++;
        if (_events.Count > 200) _events.RemoveAt(0);
        if (logPath is null) return;
        try { File.AppendAllText(logPath, line + Environment.NewLine); }
        catch (IOException) { /* log is best-effort */ }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class Format
{
    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
        : $"{t.Seconds}s";

    public static string Timeout(uint seconds) => seconds == 0 ? "Never" : Duration(TimeSpan.FromSeconds(seconds));
}

/// <summary>The away group as the verdicts see it: which devices it turns off, after how many minutes.</summary>
internal sealed record AwayGroup(IReadOnlySet<string> InstanceIds, int Minutes)
{
    public bool Contains(string? instanceId) => instanceId != null && InstanceIds.Contains(instanceId);
}

internal sealed record Snapshot(DateTime Time, PowerState Power, List<PowerRequest>? Requests, List<RequestOverride> Overrides, List<InputSource> Inputs,
    List<TurnedOffDevice> TurnedOff)
{
    /// <summary>Set when an away group is on, so a noisy device in it reads as taken care of.</summary>
    public AwayGroup? Away { get; init; }

    public static Snapshot Capture() =>
        new(DateTime.Now, PowerState.Capture(), PowerCfg.GetRequests(), PowerCfg.GetOverrides(), InputMonitor.Sources(), DeviceSwitch.TurnedOff());
}

/// <summary>Everything one refresh found, copied out of the monitor so a UI thread can read it safely.</summary>
internal sealed record Report(
    Snapshot Snapshot,
    List<(PowerRequest Request, DateTime Since)> Active,
    List<string> Events,
    long TotalEvents,
    List<Finding> Sleep,
    List<Finding> Screen,
    DisplayState Display,
    string DisplayDescription,
    string LastInputDescription);

/// <summary>Samples the power state on each tick and turns changes into events (requests, sleep gaps, display, input).</summary>
internal sealed class SleepMonitor(int intervalSeconds, string? logPath)
{
    readonly RequestTracker _tracker = new(logPath);
    readonly Lock _sync = new(); // a GUI ticks on a worker thread but stops from the UI thread
    DateTime? _lastTick;
    PowerState? _lastPower;

    public int IntervalSeconds => intervalSeconds;
    public string? LogPath => logPath;
    /// <summary>The away group, set from the UI thread whenever it changes; null when it is off.</summary>
    public AwayGroup? Away { get; set; }

    public void Start(bool isAdmin)
    {
        _tracker.Event(DateTime.Now, $"monitor started (admin: {isAdmin})");
        DisplayMonitor.Start();
        InputMonitor.Start();
    }

    public void Stop() => Log("monitor stopped");

    /// <summary>Adds an event of the app's own, e.g. devices turned off while the user is away.</summary>
    public void Log(string message)
    {
        lock (_sync) _tracker.Event(DateTime.Now, message);
    }

    /// <summary>A single snapshot without the change tracking, after giving the display and input monitors a moment.</summary>
    public Report Once()
    {
        DisplayMonitor.WaitForInitialState(TimeSpan.FromSeconds(1));
        Thread.Sleep(TimeSpan.FromSeconds(3)); // sample input devices for a moment
        var snapshot = Snapshot.Capture() with { Away = Away };
        if (snapshot.Requests != null) _tracker.Update(snapshot.Requests, snapshot.Time);
        return BuildReport(snapshot);
    }

    public Report Tick()
    {
        lock (_sync) return TickLocked();
    }

    Report TickLocked()
    {
        var snapshot = Snapshot.Capture() with { Away = Away };

        // A large gap between ticks means the machine was asleep/hibernated (or the process was frozen).
        if (_lastTick is { } prev && snapshot.Time - prev > TimeSpan.FromSeconds(intervalSeconds + 30))
            _tracker.Event(snapshot.Time, $"* resumed: no samples for {Format.Duration(snapshot.Time - prev)} (system was probably asleep)");
        if (_lastPower is { } lp && (lp.OnAC != snapshot.Power.OnAC || lp.SchemeName != snapshot.Power.SchemeName))
            _tracker.Event(snapshot.Time, $"* power changed: {(snapshot.Power.OnAC ? "AC" : "battery")}, plan '{snapshot.Power.SchemeName}'");
        var changes = DisplayMonitor.Drain().Select(c => (c.Time, Text: Diagnosis.DescribeDisplayChange(c, snapshot.Power)))
            .Concat(InputMonitor.Drain().Select(r => (r.Time, Text: Diagnosis.DescribeInputResumed(r))));
        foreach (var (time, text) in changes.OrderBy(c => c.Time))
            _tracker.Event(time, text);
        _lastTick = snapshot.Time;
        _lastPower = snapshot.Power;

        if (snapshot.Requests != null) _tracker.Update(snapshot.Requests, snapshot.Time);
        return BuildReport(snapshot);
    }

    Report BuildReport(Snapshot snapshot)
    {
        var active = _tracker.Active.OrderBy(a => a.Since).ToList();
        return new Report(snapshot, active, _tracker.Events.ToList(), _tracker.TotalEvents,
            Diagnosis.Sleep(snapshot, active), Diagnosis.Screen(snapshot, active),
            DisplayMonitor.State, Diagnosis.DescribeDisplay(snapshot.Time), Diagnosis.DescribeLastInput(snapshot));
    }
}

internal static class Elevation
{
    /// <summary>Fixed for the life of the process: elevating means starting a new one.</summary>
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts an elevated copy of this app (UAC prompt). Returns false if the user declined.</summary>
    public static bool TryRelaunch(string arguments)
    {
        var exe = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments = $"\"{Environment.GetCommandLineArgs()[0]}\" {arguments}";

        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // user clicked "No" on the UAC prompt
        {
            return false;
        }
    }
}
