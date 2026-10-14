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
    Dictionary<string, string> _deviceFailures = new(StringComparer.OrdinalIgnoreCase);
    DateTime? _pauseUntil;
    bool _pauseUntilRestart;
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _stopping;
    string? _returnReason;
    Task? _disabling; // the worker turning devices off, while it runs
    List<TurnedOffDevice> _disablingDevices = [];
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

    public string? PauseReason => _pauseUntilRestart ? "Paused until WhyNotSleeping restarts."
        : _pauseUntil is { } until && _services.Now() < until ? $"Paused until {until:HH:mm}." : null;

    /// <summary>A copy for the monitor's worker thread: failures and pauses remain unresolved blockers.</summary>
    public AwayGroup? VerdictGroup => settings.AwayEnabled && isAdmin && settings.AwayDevices.Count > 0
        ? new AwayGroup(settings.AwayDevices.Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase), settings.AwayMinutes)
        {
            Failures = new Dictionary<string, string>(_deviceFailures, StringComparer.OrdinalIgnoreCase),
            PauseReason = PauseReason,
        } : null;

    public async Task PauseAsync(TimeSpan? duration)
    {
        if (duration is { } time && time <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        _pauseUntilRestart = duration == null;
        _pauseUntil = duration is { } span ? _services.Now() + span : null;
        log($"* away: {PauseReason}");
        await ComeBackAsync("the away group was paused");
        Status = IsAway ? "Pause requested, but some devices couldn't be turned back on: see the event log."
            : PauseReason ?? "Back on.";
    }

    public void Resume()
    {
        _pauseUntilRestart = false;
        _pauseUntil = null;
        _watchFrom = _services.Now();
        Status = "Resumed: starting a new away countdown.";
        log("* away: resumed");
    }

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
            if (present > since || PauseReason != null || !settings.AwayEnabled || _off.Any(id => !settings.InAwayGroup(id)))
                await RestoreAsync(controller is { } used && used.Time > since && used.Time >= human
                    ? $"{used.Device} was used: {used.What}"
                    : present > since ? "the keyboard or mouse was used" : "the away group was changed");
            else
                Status = _awaySince != null
                    ? $"Turned off at {since:HH:mm}. Move the mouse, press a key or use a controller to turn {(_off.Count == 1 ? "it" : "them")} back on."
                    : "Some devices couldn't be turned back on: see the event log. Use the tray menu to retry.";
            return;
        }
        if (PauseReason is { } pause)
        {
            Status = pause;
            return;
        }
        if (_failedAt is { } failed && present > failed)
        {
            _failedAt = null; // they were back: worth another try next time
            _deviceFailures.Clear();
        }
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
        _disablingDevices = devices;
        var disabling = Task.Run(() => devices.Where(d => _services.IsPresent(d.InstanceId))
            .Select(d => (Device: d, Error: _services.Off(d.InstanceId, d.Name))).ToList());
        _disabling = disabling;
        var results = await disabling;
        _disabling = null;
        _disablingDevices = [];
        if (results.Count == 0) return;

        foreach (var (device, error) in results.Where(r => r.Error != null))
        {
            log($"* away: couldn't turn off {device.Name}: {error}");
            _deviceFailures[device.InstanceId] = $"Couldn't turn off {device.Name}: {error}";
        }
        var off = results.Where(r => r.Error is null).Select(r => r.Device).ToList();
        var failed = results.FirstOrDefault(r => r.Error != null);
        if (failed.Error != null)
        {
            _failedAt = _services.Now();
            _failure = $"{failed.Device.Name}: {failed.Error}";
            Failed?.Invoke($"Couldn't turn off {failed.Device.Name}. {failed.Error}");
        }
        if (off.Count == 0)
        {
            Status = $"Couldn't turn them off: {_failure} It tries again once you've been back.";
            return;
        }

        _off.UnionWith(off.Select(d => d.InstanceId));
        _awaySince = now;
        // Disabling runs on a worker thread. Input, unlock, settings changes or Exit can arrive while it runs.
        // Recheck before asking Windows to turn off the screen or sleep, then restore under the same gate.
        if (_stopping || _returnReason != null || PauseReason != null || _services.Human() > now || _services.Controller()?.Time > now ||
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
        var devices = ToRestore();
        AfterTurningOn(await Task.Run(() => devices.Select(d => (d, _services.On(d.InstanceId))).ToList()), reason);
    }

    /// <summary>
    /// The file is best-effort. Keep devices disabled by this run recoverable even if its write failed.
    /// Copied on the UI thread before a worker starts enabling devices.
    /// </summary>
    List<TurnedOffDevice> ToRestore() =>
        _services.LeftOff().Concat(_off.Select(id =>
                settings.AwayDevices.FirstOrDefault(d => d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? new(id, id)))
            .DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Prevents new disables and waits for any in-flight disable before restoring devices on exit.</summary>
    public Task StopAsync()
    {
        _stopping = true;
        return ComeBackAsync("WhyNotSleeping exited");
    }

    /// <summary>
    /// Windows is signing out or shutting down: the process may be ended as soon as the UI thread returns, so
    /// nothing can be awaited. Waits a moment for a disable in flight (it runs on a worker, so this can't
    /// deadlock), then turns everything back on before returning, including what that disable was turning off.
    /// </summary>
    public void StopNow(string reason)
    {
        _stopping = true;
        try { _disabling?.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { /* its failures are logged by GoAwayAsync, if it ever gets to run */ }
        var devices = ToRestore().Concat(_disablingDevices).DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        if (devices.Count > 0)
            AfterTurningOn(devices.Select(d => (d, _services.On(d.InstanceId))).ToList(), reason);
    }

    /// <summary>Devices left off by an earlier run that crashed or was killed.</summary>
    public Task RestoreLeftoversAsync() =>
        _off.Count > 0 ? ComeBackAsync("left off when WhyNotSleeping last stopped") : Task.CompletedTask;

    void AfterTurningOn(List<(TurnedOffDevice Device, string? Error)> results, string reason)
    {
        _failedAt = null;
        _failure = null;
        _deviceFailures = results.Where(r => r.Error != null).ToDictionary(r => r.Device.InstanceId,
            r => $"Couldn't turn {r.Device.Name} back on: {r.Error}", StringComparer.OrdinalIgnoreCase);
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
        if (_deviceFailures.TryGetValue(device.InstanceId, out var failure)) return failure;
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
