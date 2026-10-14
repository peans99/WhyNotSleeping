namespace WhyNotSleeping;

/// <summary>Problem: definitely blocking. Warning: probably blocking. Unknown: can't tell. Info: countdowns and the like.</summary>
internal enum Severity { Ok, Info, Unknown, Warning, Problem }

internal sealed record Finding(Severity Severity, string Text);

/// <summary>Explains why the PC isn't sleeping and why the screen isn't turning off, from one snapshot.</summary>
internal static class Diagnosis
{
    public static List<Finding> Sleep(Snapshot s, List<(PowerRequest Request, DateTime Since)> active)
    {
        var p = s.Power;
        var findings = new List<Finding>();
        if (p.SleepTimeout == 0)
            findings.Add(new(Severity.Problem, $"Sleep is set to 'Never' for {(p.OnAC ? "AC power" : "battery")} in plan '{p.SchemeName}' - Windows will never idle-sleep."));

        if (s.Requests == null)
        {
            findings.Add(new(Severity.Unknown, "Not running as administrator - can't list power requests (apps/drivers keeping the PC awake)."));
            if (p.ExecutionState is { } state && (state & 0x41) != 0)
                findings.Add(new(Severity.Problem, $"Some process or driver is requesting the system stay awake ({PowerState.DescribeExecutionState(state)}) - run as admin to see who."));
        }
        else
        {
            var blocking = active.Count(a => a.Request.BlocksSleep);
            var display = active.Count(a => a.Request.KeepsDisplayOn);
            if (blocking > 0)
                findings.Add(new(Severity.Problem, $"{blocking} power request(s) are blocking sleep (see the list of power requests)."));
            if (display > 0)
                findings.Add(new(Severity.Warning, $"{display} request(s) keep the display on, which usually also prevents sleep."));
        }

        findings.AddRange(Input(s));

        if (p.SleepTimeout > 0 && p.UserIdle.TotalSeconds < p.SleepTimeout)
            findings.Add(new(Severity.Info, $"Sleep countdown: {Format.Duration(TimeSpan.FromSeconds(p.SleepTimeout) - p.UserIdle)} left " +
                                            "(any input - mouse jiggle, controller, remote session - resets it)."));
        else if (p.SleepTimeout > 0 && findings.Count == 0)
            findings.Add(new(Severity.Unknown, "Idle time exceeds the sleep timeout but no blocker is visible - check the event log for short-lived requests."));

        if (findings.Count == 0)
            findings.Add(new(Severity.Ok, "Nothing is blocking sleep right now."));
        return findings;
    }

    public static List<Finding> Screen(Snapshot s, List<(PowerRequest Request, DateTime Since)> active)
    {
        var p = s.Power;
        if (DisplayMonitor.State == DisplayState.Off)
            return [new(Severity.Ok, "Screen is off.")];

        var findings = new List<Finding>();
        if (p.DisplayTimeout == 0)
            findings.Add(new(Severity.Problem, $"Display timeout is 'Never' for {(p.OnAC ? "AC power" : "battery")} in plan '{p.SchemeName}' - the screen will never turn off by itself."));

        if (s.Requests == null)
        {
            if (p.ExecutionState is { } state && (state & 0x2) != 0)
                findings.Add(new(Severity.Problem, "Some process or driver is requesting the display stay on (DISPLAY_REQUIRED) - run as admin to see who."));
        }
        else if (active.Count(a => a.Request.KeepsDisplayOn) is var display and > 0)
            findings.Add(new(Severity.Problem, $"{display} power request(s) are keeping the screen on (DISPLAY, see the list of power requests)."));

        findings.AddRange(Input(s));

        if (p.DisplayTimeout > 0 && p.UserIdle.TotalSeconds < p.DisplayTimeout)
            findings.Add(new(Severity.Info, $"Screen-off countdown: {Format.Duration(TimeSpan.FromSeconds(p.DisplayTimeout) - p.UserIdle)} left " +
                                            "(any input resets it)."));
        else if (p.DisplayTimeout > 0 && findings.Count == 0)
            findings.Add(new(Severity.Unknown, "Idle time exceeds the display timeout but no blocker is visible - check the event log for short-lived requests " +
                                               "(or a device sending input - see the input devices)."));

        if (findings.Count == 0)
            findings.Add(new(Severity.Ok, "Nothing is keeping the screen on right now."));
        return findings;
    }

    /// <summary>
    /// Devices other than mouse/keyboard that reset the idle timer recently, e.g. a controller with jittery axes. One in
    /// the away group is only a warning: it is turned off once nobody is there.
    /// </summary>
    static IEnumerable<Finding> Input(Snapshot s) =>
        s.Inputs.Where(i => i.Suspicious).OrderByDescending(i => i.ResetsLastMinute).Select(i => s.Away is { } failedGroup &&
            failedGroup.Contains(i.InstanceId) && failedGroup.Failures.TryGetValue(i.InstanceId!, out var failure)
            ? new Finding(Severity.Problem, $"{InputMonitor.Label(i.Kind)} '{i.ShortName}' is an unresolved blocker: {failure}")
            : s.Away is { PauseReason: { } pause } pausedGroup && pausedGroup.Contains(i.InstanceId)
            ? new Finding(Severity.Problem, $"{InputMonitor.Label(i.Kind)} '{i.ShortName}' reset the idle timer {i.ResetsLastMinute} time(s) in the last minute - away group: {pause}")
            : s.Away is { } away && away.Contains(i.InstanceId)
            ? new Finding(Severity.Warning,
                $"{InputMonitor.Label(i.Kind)} '{i.ShortName}' reset the idle timer {i.ResetsLastMinute} time(s) in the last minute" +
                (i.Busiest is { OnItsOwn: true } own ? $" (its {own.Name} is moving on its own)" : "") +
                $" - it's in the away group, so it is turned off after {away.Minutes} min without keyboard, mouse or controller.")
            : new Finding(Severity.Problem,
                $"{InputMonitor.Label(i.Kind)} '{i.ShortName}' reset the idle timer {i.ResetsLastMinute} time(s) in the last minute" +
                (i.Kind == InputKind.Software ? " - something is injecting input."
                    : i.Busiest is { OnItsOwn: true } own2 ? $": its {OnItsOwn(own2)}"
                    : i.Busiest is { } busiest ? $", mostly its {busiest.Name} - if nobody is using it, turn it off or unplug it."
                    : " - if nobody is using it, turn it off or unplug it.")));

    /// <summary>What a control moving by itself is doing, and what can be done about it.</summary>
    static string OnItsOwn(ControlStatus c)
    {
        // Vendor channels are data games never read, so no deadzone or calibration reaches them.
        var vendor = c.Name.StartsWith("vendor channel");
        var what = c.Jitter ? $"is jittering ({c.Min}–{c.Max})" : $"keeps changing with the keyboard and mouse idle ({c.Min}–{c.Max})";
        return $"{c.Name}{(vendor ? " (raw data games don't use)" : "")} {what} - " + (vendor
            ? "settings can't fix that; turn it off or unplug it when you're not using it."
            : c.Jitter ? "recalibrate it, add a deadzone, or turn it off when you're not using it."
            : "if nobody is touching it, it's moving on its own: turn it off or unplug it when you're not using it.");
    }

    public static string DescribeDisplayChange(DisplayChange change, PowerState power)
    {
        var name = change.State.ToString().ToLowerInvariant();
        if (change.State == DisplayState.On)
            return $"* display {name}";
        // Comparing idle time to the timeout shows whether it went off on schedule or late (something held it on).
        var timeout = power.DisplayTimeout;
        var late = timeout > 0 && change.UserIdle.TotalSeconds > timeout + 30
            ? $" - {Format.Duration(change.UserIdle - TimeSpan.FromSeconds(timeout))} late"
            : "";
        return $"* display {name} after {Format.Duration(change.UserIdle)} idle (timeout {Format.Timeout(timeout)}){late}";
    }

    public static string DescribeInputResumed(InputResumed r) =>
        $"* input after {Format.Duration(r.Quiet)} quiet: {InputMonitor.Label(r.Kind)} - {r.Name}{(r.What is { } what ? $" ({what})" : "")}";

    public static string DescribeLastInput(Snapshot s) =>
        s.Inputs.Where(i => i.LastIdleReset != null).MaxBy(i => i.LastIdleReset) is { } last
            ? $"last reset by {InputMonitor.Label(last.Kind)} - {last.Name}"
            : "since last keyboard/mouse input";

    public static string DescribeDisplay(DateTime now)
    {
        if (DisplayMonitor.State == DisplayState.Unknown)
            return DisplayMonitor.Available ? "unknown (waiting for Windows)" : "unknown (display notifications unavailable)";
        var since = DisplayMonitor.Since is { } t ? $" since {t:HH:mm:ss} ({Format.Duration(now - t)})" : "";
        return DisplayMonitor.State + since;
    }
}
