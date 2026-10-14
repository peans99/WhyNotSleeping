namespace WhyNotSleeping;

/// <summary>
/// The away group: devices, like a HOTAS whose sensor never rests, that keep resetting the idle timer while nobody is
/// there. Once nobody has used the keyboard or mouse for a while and the group is only sending noise, this turns those
/// devices off (until the next restart at most), so the screen can turn off and the PC can sleep; it turns them back
/// on as soon as someone is back. Ticked about once a second on the UI thread.
/// </summary>
internal sealed class AwayGuard(Settings settings, bool isAdmin, Action<string> log, AwayGuard.Services? services = null)
{
    // Keep the lifecycle testable without changing real devices or putting the test machine to sleep.
    internal sealed class Services
    {
        public Func<DateTime> Now { get; init; } = () => DateTime.Now;
        public Func<DateTime> Human { get; init; } = () => InputMonitor.LastHumanInput;
        public Func<(DateTime Time, string Device, string What)?> Controller { get; init; } = () => InputMonitor.LastDeliberate;
        public Func<string, DateTime?> LastInput { get; init; } = id => InputMonitor.Activity(id).LastInput;
        public Func<bool> FullScreen { get; init; } = AwayGuard.FullScreen;
        public Func<List<TurnedOffDevice>> Manual { get; init; } = DeviceSwitch.TurnedOff;
        public Func<List<TurnedOffDevice>> LeftOff { get; init; } = DeviceSwitch.AwayOffList;
        public Func<string, bool> IsPresent { get; init; } = DeviceSwitch.IsPresent;
        public Func<string, string, string?> Off { get; init; } = DeviceSwitch.AwayOff;
        public Func<string, string?> On { get; init; } = DeviceSwitch.AwayOn;
    }

    readonly Services _services = services ?? new();
    /// <summary>Input before this doesn't count: the start, or when the devices last came back on.</summary>
    DateTime _watchFrom = services?.Now() ?? DateTime.Now;
    DateTime? _awaySince;
    DateTime? _failedAt;
    string? _failure;
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _stopping;
    string? _returnReason;
    HashSet<string> _off = (services?.LeftOff() ?? DeviceSwitch.AwayOffList()).Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        if (_stopping || !_gate.Wait(0)) return;
        try { await TickCoreAsync(); }
        finally { _gate.Release(); }
    }

    async Task TickCoreAsync()
    {
        var now = _services.Now();
        // Someone is there when they use the keyboard, mouse, touch or pen, or really use any controller: playing
        // a game with only a gamepad doesn't turn the group off, and picking one up brings it back.
        var human = _services.Human();
        var controller = _services.Controller();
        var present = controller is { } c && c.Time > human ? c.Time : human;
        if (IsAway)
        {
            var since = _awaySince ?? _watchFrom;
            if (present > since || !settings.AwayEnabled || _off.Any(id => !settings.InAwayGroup(id)))
                await RestoreAsync(controller is { } used && used.Time > since && used.Time >= human
                    ? $"{used.Device} was used: {used.What}"
                    : present > since ? "the keyboard or mouse was used" : "the away group was changed");
            else
                Status = _awaySince != null
                    ? $"Turned off at {since:HH:mm}. Move the mouse, press a key or use a controller to turn {(_off.Count == 1 ? "it" : "them")} back on."
                    : "Some devices couldn't be turned back on: see the event log. Use the tray menu to retry.";
            return;
        }
        if (_failedAt is { } failed && present > failed)
            _failedAt = null; // they were back: worth another try next time
        var moment = new AwayMoment(now, human, controller, _watchFrom, _failedAt != null ? _failure : null,
            _services.LastInput, _services.FullScreen);
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
        // One the user turned off themselves stays off: turning it back on is theirs to do.
        var manual = _services.Manual().Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = settings.AwayDevices.Where(d => !manual.Contains(d.InstanceId)).ToList();
        var results = await Task.Run(() => devices.Where(d => _services.IsPresent(d.InstanceId))
            .Select(d => (Device: d, Error: _services.Off(d.InstanceId, d.Name))).ToList());
        if (results.Count == 0) return;

        foreach (var (device, error) in results.Where(r => r.Error != null))
            log($"* away: couldn't turn off {device.Name}: {error}");
        var off = results.Where(r => r.Error is null).Select(r => r.Device).ToList();
        var failed = results.FirstOrDefault(r => r.Error != null);
        if (failed.Error != null)
            Failed?.Invoke($"Couldn't turn off {failed.Device.Name}. {failed.Error}");
        if (off.Count == 0)
        {
            _failedAt = _services.Now();
            _failure = failed.Error;
            return;
        }

        _off.UnionWith(off.Select(d => d.InstanceId));
        _awaySince = now;
        // Disabling runs on a worker thread. Input, unlock, settings changes or Exit can arrive while it runs.
        // Recheck before asking Windows to turn off the screen or sleep, then restore under the same gate.
        if (_stopping || _returnReason != null || _services.Human() > now || _services.Controller()?.Time > now ||
            !settings.AwayEnabled || off.Any(d => !settings.InAwayGroup(d.InstanceId)) ||
            (settings.AwaySkipFullScreen && _services.FullScreen()))
        {
            await RestoreAsync(_returnReason ?? "someone returned or the away group was changed");
            return;
        }
        var then = settings.AwayAction switch
        {
            AwayAction.ScreenOff => ", turning the screen off",
            AwayAction.Sleep => ", putting the PC to sleep",
            _ => "",
        };
        log($"* away: turned off {string.Join(", ", off.Select(d => d.Name))} (nobody at the keyboard, mouse or a controller for {Format.Duration(idle)}){then}");
        WentAway?.Invoke(settings.AwayAction);
    }

    /// <summary>Turns the group's devices back on, e.g. on unlocking the PC or from the tray menu.</summary>
    public async Task ComeBackAsync(string reason)
    {
        _returnReason = reason;
        await _gate.WaitAsync();
        try
        {
            await RestoreAsync(reason);
        }
        finally
        {
            _returnReason = null;
            _gate.Release();
        }
    }

    async Task RestoreAsync(string reason)
    {
        // The file is best-effort. Keep devices disabled by this run recoverable even if its write failed.
        // Copy the settings on the UI thread before the worker starts enabling devices.
        var devices = _services.LeftOff().Concat(_off.Select(id =>
                settings.AwayDevices.FirstOrDefault(d => d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? new(id, id)))
            .DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        AfterTurningOn(await Task.Run(() => devices.Select(d => (d, _services.On(d.InstanceId))).ToList()), reason);
    }

    /// <summary>Prevents new disables and waits for any in-flight disable before restoring devices on exit.</summary>
    public Task StopAsync()
    {
        _stopping = true;
        return ComeBackAsync("WhyNotSleeping exited");
    }

    /// <summary>Devices left off by an earlier run that crashed or was killed.</summary>
    public Task RestoreLeftoversAsync() =>
        _off.Count > 0 ? ComeBackAsync("left off when WhyNotSleeping last stopped") : Task.CompletedTask;

    void AfterTurningOn(List<(TurnedOffDevice Device, string? Error)> results, string reason)
    {
        foreach (var (device, error) in results.Where(r => r.Error != null))
            log($"* back: couldn't turn {device.Name} back on: {error}");
        if (results.Where(r => r.Error is null).Select(r => r.Device.Name).ToList() is { Count: > 0 } on)
            log($"* back: turned {string.Join(", ", on)} back on ({reason})");
        _off = results.Where(r => r.Error != null).Select(r => r.Device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _awaySince = null;
        _watchFrom = _services.Now();
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
