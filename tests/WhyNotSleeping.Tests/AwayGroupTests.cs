namespace WhyNotSleeping.Tests;

/// <summary>When the away group turns its devices off, and every reason it waits instead.</summary>
public class AwayGroupTests
{
    static readonly DateTime Now = new(2026, 10, 14, 3, 0, 0);
    const string Stick = @"HID\VID_044F&PID_0402\c&3738ddd0&0&0000";

    static Settings Group(bool enabled = true, int minutes = 10, bool skipFullScreen = true) => new()
    {
        AwayEnabled = enabled,
        AwayMinutes = minutes,
        AwaySkipFullScreen = skipFullScreen,
        AwayDevices = [new TurnedOffDevice(Stick, "Joystick - HOTAS Warthog")],
    };

    /// <summary>By default: nobody for 15 minutes, the stick jittering a second ago, nothing full screen.</summary>
    static AwayGuard.AwayMoment Moment(TimeSpan? sinceHuman = null, (DateTime, string, string)? controller = null,
        DateTime? lastInput = null, bool fullScreen = false, string? failure = null, DateTime? watchFrom = null) =>
        new(Now, Now - (sinceHuman ?? TimeSpan.FromMinutes(15)), controller, watchFrom ?? Now.AddHours(-1), failure,
            _ => lastInput ?? Now.AddSeconds(-1), () => fullScreen);

    [Fact]
    public void After_long_enough_with_only_noise_it_acts()
    {
        Assert.Null(AwayGuard.Waiting(Group(), true, Moment()));
    }

    [Fact]
    public void It_waits_until_nobody_has_been_there_long_enough()
    {
        var waiting = AwayGuard.Waiting(Group(), true, Moment(sinceHuman: TimeSpan.FromMinutes(3)));
        Assert.Equal("Waiting: nobody has used the keyboard, mouse or a controller for 3m 0s of 10m 0s.", waiting);
    }

    [Fact]
    public void Playing_with_a_controller_keeps_it_from_acting()
    {
        // Someone flying with a gamepad, no keyboard or mouse for 15 minutes.
        var pad = (Now.AddSeconds(-20), "Xbox Controller", "button 1 pressed");
        Assert.Equal("Not now: Xbox Controller was used 20s ago (button 1 pressed).", AwayGuard.Waiting(Group(), true, Moment(controller: pad)));
    }

    [Fact]
    public void A_controller_used_long_ago_does_not_hold_it_back()
    {
        var pad = (Now.AddMinutes(-30), "Xbox Controller", "button 1 pressed");
        Assert.Null(AwayGuard.Waiting(Group(), true, Moment(controller: pad)));
    }

    [Fact]
    public void Controller_use_before_the_devices_last_came_back_does_not_count()
    {
        // Picking the stick up turned the group back on; that same use mustn't read as someone still playing.
        var used = (Now.AddMinutes(-5), "Joystick - HOTAS Warthog", "button 3 pressed");
        var waiting = AwayGuard.Waiting(Group(), true, Moment(controller: used, watchFrom: Now.AddMinutes(-4)));
        Assert.StartsWith("Waiting: nobody has used", waiting);
    }

    [Fact]
    public void Quiet_devices_are_left_alone()
    {
        var waiting = AwayGuard.Waiting(Group(), true, Moment(lastInput: Now.AddMinutes(-5)));
        Assert.StartsWith("Nothing to do", waiting);
    }

    [Fact]
    public void A_device_never_seen_is_quiet()
    {
        var moment = Moment() with { LastInput = _ => null };
        Assert.StartsWith("Nothing to do", AwayGuard.Waiting(Group(), true, moment));
    }

    [Fact]
    public void Full_screen_holds_it_back_unless_told_otherwise()
    {
        Assert.StartsWith("Waiting: a full-screen", AwayGuard.Waiting(Group(), true, Moment(fullScreen: true)));
        Assert.Null(AwayGuard.Waiting(Group(skipFullScreen: false), true, Moment(fullScreen: true)));
    }

    [Fact]
    public void Off_without_admin_or_without_devices_it_never_acts()
    {
        Assert.Equal("Off.", AwayGuard.Waiting(Group(enabled: false), true, Moment()));
        Assert.StartsWith("Needs administrator", AwayGuard.Waiting(Group(), false, Moment()));
        Assert.StartsWith("No devices yet", AwayGuard.Waiting(new Settings { AwayEnabled = true }, true, Moment()));
    }

    [Fact]
    public void After_a_failure_it_waits_for_the_user_to_have_been_back()
    {
        var waiting = AwayGuard.Waiting(Group(), true, Moment(failure: "Steam has the device open."));
        Assert.Equal("Couldn't turn them off: Steam has the device open. It tries again once you've been back.", waiting);
    }

    [Fact]
    public void The_time_counts_from_when_the_devices_came_back_on()
    {
        // Unlocking the PC turned them back on two minutes ago, without any keyboard or mouse input since.
        var waiting = AwayGuard.Waiting(Group(), true, Moment(watchFrom: Now.AddMinutes(-2)));
        Assert.StartsWith("Waiting: nobody has used the keyboard, mouse or a controller for 2m 0s", waiting);
    }
}
