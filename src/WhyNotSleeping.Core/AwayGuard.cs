namespace WhyNotSleeping;

/// <summary>
/// The away group: devices, like a HOTAS whose sensor never rests, that keep resetting the idle timer while nobody is
/// there. Once nobody has used the keyboard or mouse for a while and the group is only sending noise, this turns those
/// devices off (until the next restart at most), so the screen can turn off and the PC can sleep; it turns them back
/// on as soon as someone is back. Ticked about once a second on the UI thread.
/// </summary>
internal sealed class AwayGuard(Settings settings, bool isAdmin, Action<string> log)
{
    /// <summary>Input before this doesn't count: the start, or when the devices last came back on.</summary>
    DateTime _watchFrom = DateTime.Now;
    DateTime? _awaySince;
    DateTime? _failedAt;
    string? _failure;
    bool _busy;
    HashSet<string> _off = DeviceSwitch.AwayOffList().Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The group's devices are off; the action to take next (screen off, sleep) is the app's.</summary>
    public event Action<AwayAction>? WentAway;
    /// <summary>A device couldn't be turned off, and why (e.g. Steam has it open).</summary>
    public event Action<string>? Failed;

    /// <summary>Some of the group's devices are off.</summary>
    public bool IsAway => _awaySince != null || _off.Count > 0;
    public bool IsOff(string? instanceId) => instanceId != null && _off.Contains(instanceId);

    /// <summary>What it is doing or waiting for, in a sentence.</summary>
    public string Status { get; private set; } = "";

    public async Task TickAsync()
    {
        if (_busy) return;
        var now = DateTime.Now;
        // Someone is there when they use the keyboard, mouse, touch or pen, or really use any controller: playing
        // a game with only a gamepad doesn't turn the group off, and picking one up brings it back.
        var human = InputMonitor.LastHumanInput;
        var controller = InputMonitor.LastDeliberate;
        var present = controller is { } c && c.Time > human ? c.Time : human;
        if (_awaySince is { } since)
        {
            if (present > since)
                await ComeBackAsync(controller is { } used && used.Time > since && used.Time >= human
                    ? $"{used.Device} was used: {used.What}"
                    : "the keyboard or mouse was used");
            else
                Status = $"Turned off at {since:HH:mm}. Move the mouse, press a key or use a controller to turn {(_off.Count == 1 ? "it" : "them")} back on.";
            return;
        }
        if (_failedAt is { } failed && present > failed)
            _failedAt = null; // they were back: worth another try next time
        var moment = new AwayMoment(now, human, controller, _watchFrom, _failedAt != null ? _failure : null,
            id => InputMonitor.Activity(id).LastInput, FullScreen);
        if (Waiting(settings, isAdmin, moment) is { } waiting)
        {
            Status = waiting;
            return;
        }
        await GoAwayAsync(now, now - Latest(present, _watchFrom));
    }

    /// <summary>
    /// Everything the decision reads, gathered so it can be made (and tested) without the live input monitor.
    /// Failure is why the last try failed, while it waits for the user to have been back.
    /// </summary>
    internal readonly record struct AwayMoment(DateTime Now, DateTime Human, (DateTime Time, string Device, string What)? Controller,
        DateTime WatchFrom, string? Failure, Func<string, DateTime?> LastInput, Func<bool> FullScreen);

    /// <summary>Why it isn't turning the devices off yet, or null when it's time to.</summary>
    internal static string? Waiting(Settings settings, bool isAdmin, AwayMoment m)
    {
        if (settings.AwayDevices.Count == 0)
            return "No devices yet: in Input devices, right-click a controller and choose \"Add to the away group\".";
        if (!settings.AwayEnabled)
            return "Off.";
        if (!isAdmin)
            return "Needs administrator rights to turn devices off: restart WhyNotSleeping as administrator.";
        if (m.Failure != null)
            return $"Couldn't turn them off: {m.Failure} It tries again once you've been back.";

        var need = TimeSpan.FromMinutes(settings.AwayMinutes);
        if (m.Controller is { } c && m.Now - c.Time < need && c.Time > m.Human && c.Time > m.WatchFrom)
            return $"Not now: {c.Device} was used {Format.Duration(m.Now - c.Time)} ago ({c.What}).";
        var idle = m.Now - Latest(Latest(m.Human, m.Controller?.Time ?? DateTime.MinValue), m.WatchFrom);
        if (idle < need)
            return $"Waiting: nobody has used the keyboard, mouse or a controller for {Format.Duration(idle)} of {Format.Duration(need)}.";

        var sending = settings.AwayDevices.Any(d => m.LastInput(d.InstanceId) is { } i && m.Now - i < TimeSpan.FromMinutes(1));
        if (!sending)
            return "Nothing to do: the devices in the group are quiet, so they aren't keeping the PC awake.";
        if (settings.AwaySkipFullScreen && m.FullScreen())
            return "Waiting: a full-screen game, video or presentation is running.";
        return null;
    }

    async Task GoAwayAsync(DateTime now, TimeSpan idle)
    {
        _busy = true;
        try
        {
            // One the user turned off themselves stays off: turning it back on is theirs to do.
            var manual = DeviceSwitch.TurnedOff().Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var devices = settings.AwayDevices.Where(d => !manual.Contains(d.InstanceId)).ToList();
            var results = await Task.Run(() => devices.Where(d => DeviceSwitch.IsPresent(d.InstanceId))
                .Select(d => (Device: d, Error: DeviceSwitch.AwayOff(d.InstanceId, d.Name))).ToList());
            if (results.Count == 0) return;

            foreach (var (device, error) in results.Where(r => r.Error != null))
                log($"* away: couldn't turn off {device.Name}: {error}");
            var off = results.Where(r => r.Error is null).Select(r => r.Device).ToList();
            var failed = results.FirstOrDefault(r => r.Error != null);
            if (failed.Error != null)
                Failed?.Invoke($"Couldn't turn off {failed.Device.Name}. {failed.Error}");
            if (off.Count == 0)
            {
                _failedAt = DateTime.Now;
                _failure = failed.Error;
                return;
            }

            _off.UnionWith(off.Select(d => d.InstanceId));
            _awaySince = now;
            var then = settings.AwayAction switch
            {
                AwayAction.ScreenOff => ", turning the screen off",
                AwayAction.Sleep => ", putting the PC to sleep",
                _ => "",
            };
            log($"* away: turned off {string.Join(", ", off.Select(d => d.Name))} (nobody at the keyboard, mouse or a controller for {Format.Duration(idle)}){then}");
            WentAway?.Invoke(settings.AwayAction);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Turns the group's devices back on, e.g. on unlocking the PC or from the tray menu.</summary>
    public async Task ComeBackAsync(string reason)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            AfterTurningOn(await Task.Run(TurnOn), reason);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The same, synchronously, for when the app exits.</summary>
    public void ComeBack(string reason)
    {
        if (!_busy && IsAway) AfterTurningOn(TurnOn(), reason);
    }

    /// <summary>Devices left off by an earlier run that crashed or was killed.</summary>
    public Task RestoreLeftoversAsync() =>
        _off.Count > 0 ? ComeBackAsync("left off when WhyNotSleeping last stopped") : Task.CompletedTask;

    static List<(TurnedOffDevice Device, string? Error)> TurnOn() =>
        DeviceSwitch.AwayOffList().Select(d => (d, DeviceSwitch.AwayOn(d.InstanceId))).ToList();

    void AfterTurningOn(List<(TurnedOffDevice Device, string? Error)> results, string reason)
    {
        foreach (var (device, error) in results.Where(r => r.Error != null))
            log($"* back: couldn't turn {device.Name} back on: {error}");
        if (results.Where(r => r.Error is null).Select(r => r.Device.Name).ToList() is { Count: > 0 } on)
            log($"* back: turned {string.Join(", ", on)} back on ({reason})");
        _off = results.Where(r => r.Error != null).Select(r => r.Device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _awaySince = null;
        _watchFrom = DateTime.Now;
        Status = _off.Count > 0 ? "Some devices couldn't be turned back on: see the event log." : "Back on.";
    }

    /// <summary>For the group list: what a device is doing, as far as the away group is concerned.</summary>
    public string Describe(TurnedOffDevice device, DateTime now)
    {
        if (IsOff(device.InstanceId)) return "turned off while you're away";
        var (input, used, how) = InputMonitor.Activity(device.InstanceId);
        if (used is { } u && now - u < TimeSpan.FromMinutes(settings.AwayMinutes))
            return $"used {Format.Duration(now - u)} ago ({how})";
        if (input is { } i && now - i < TimeSpan.FromMinutes(1))
            return "sending input on its own (noise)";
        return input is null ? "no input seen yet" : $"quiet for {Format.Duration(now - input.Value)}";
    }

    static DateTime Latest(DateTime a, DateTime b) => a > b ? a : b;

    static bool FullScreen() =>
        Native.SHQueryUserNotificationState(out var state) == 0 &&
        state is Native.QunsBusy or Native.QunsRunningD3dFullScreen or Native.QunsPresentationMode;
}
