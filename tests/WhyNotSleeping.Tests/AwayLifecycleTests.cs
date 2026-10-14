namespace WhyNotSleeping.Tests;

/// <summary>Real away transitions with fake devices, including input and exit during an in-flight disable.</summary>
public class AwayLifecycleTests
{
    sealed class Fixture
    {
        readonly Lock _sync = new();
        readonly List<TurnedOffDevice> _off = [];
        public DateTime Now = new(2026, 10, 14, 3, 0, 0);
        public DateTime Human;
        public (DateTime Time, string Device, string What)? Controller;
        public Action? BeforeOff;
        public bool FailOn, FailOff, ForgetOffRecord, FullScreen;
        public int Actions, OnAttempts;
        /// <summary>Devices the fake Windows has turned off right now.</summary>
        public int TurnedOff { get { lock (_sync) return _off.Count; } }
        public Settings Settings { get; } = new()
        {
            AwayEnabled = true,
            AwayDevices = [new("stick", "Stick")],
        };
        public AwayGuard Guard { get; }

        public Fixture()
        {
            Human = Now;
            Guard = new AwayGuard(Settings, true, _ => { }, new AwayGuard.Services
            {
                Now = () => Now,
                Human = () => Human,
                Controller = () => Controller,
                LastInput = _ => Now.AddSeconds(-1),
                FullScreen = () => FullScreen,
                Manual = () => [],
                LeftOff = () => { lock (_sync) return ForgetOffRecord ? [] : _off.ToList(); },
                IsPresent = _ => true,
                Off = (id, name) =>
                {
                    BeforeOff?.Invoke();
                    if (FailOff) return "Steam has the device open.";
                    lock (_sync) _off.Add(new(id, name));
                    return null;
                },
                On = id =>
                {
                    OnAttempts++;
                    if (FailOn) return "Device is busy.";
                    lock (_sync) _off.RemoveAll(d => d.InstanceId == id);
                    return null;
                },
            });
            Guard.WentAway += _ => Actions++;
            Now = Now.AddMinutes(15);
        }

        public (Task Entered, Action Release) PauseDisable()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeforeOff = () =>
            {
                entered.SetResult();
                released.Task.GetAwaiter().GetResult();
            };
            return (entered.Task, () => released.SetResult());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_during_disable_restores_devices_without_turning_off_the_screen(bool controller)
    {
        var f = new Fixture();
        var pause = f.PauseDisable();
        var tick = f.Guard.TickAsync();
        try
        {
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            f.Now = f.Now.AddSeconds(1);
            if (controller) f.Controller = (f.Now, "Gamepad", "button 1 pressed");
            else f.Human = f.Now;
        }
        finally { pause.Release(); }
        await tick;

        Assert.False(f.Guard.IsAway);
        Assert.Equal(0, f.Actions);
        Assert.Equal(1, f.OnAttempts);
    }

    [Fact]
    public async Task Uninterrupted_away_transition_runs_the_action_and_restores_on_return()
    {
        var f = new Fixture();
        await f.Guard.TickAsync();
        Assert.True(f.Guard.IsAway);
        Assert.True(f.Guard.IsOff("stick"));
        Assert.Equal(1, f.Actions);

        f.Human = f.Now = f.Now.AddSeconds(1);
        await f.Guard.TickAsync();
        Assert.False(f.Guard.IsAway);
        Assert.Equal(1, f.OnAttempts);
    }

    [Fact]
    public async Task A_failed_disable_is_reported_as_unresolved_until_the_user_returns()
    {
        var f = new Fixture { FailOff = true };
        await f.Guard.TickAsync();
        Assert.Contains("Steam", f.Guard.VerdictGroup!.Failures["stick"]);
        Assert.Contains("Steam", f.Guard.Status);
        Assert.Contains("Steam", f.Guard.Describe(new("stick", "Stick"), f.Now));
        f.FailOff = false;
        f.Human = f.Now = f.Now.AddSeconds(1);
        await f.Guard.TickAsync();
        Assert.Empty(f.Guard.VerdictGroup!.Failures);
    }

    [Fact]
    public async Task Pause_restores_devices_and_automatically_expires()
    {
        var f = new Fixture();
        await f.Guard.TickAsync();
        await f.Guard.PauseAsync(TimeSpan.FromMinutes(30));
        Assert.False(f.Guard.IsAway);
        Assert.Contains("Paused until", f.Guard.VerdictGroup!.PauseReason);
        f.Now = f.Now.AddMinutes(29);
        await f.Guard.TickAsync();
        Assert.False(f.Guard.IsAway);
        f.Now = f.Now.AddMinutes(1);
        await f.Guard.TickAsync();
        Assert.Null(f.Guard.PauseReason);
        Assert.True(f.Guard.IsAway);
    }

    [Fact]
    public async Task A_failed_restoration_while_pausing_is_still_shown_as_a_failure()
    {
        var f = new Fixture();
        await f.Guard.TickAsync();
        f.FailOn = true;
        await f.Guard.PauseAsync(TimeSpan.FromMinutes(30));
        Assert.True(f.Guard.IsAway);
        Assert.Contains("couldn't", f.Guard.Status);
        Assert.Contains("back on", f.Guard.VerdictGroup!.Failures["stick"]);
    }

    [Fact]
    public async Task Pause_until_restart_survives_time_passing_and_resume_starts_a_new_countdown()
    {
        var f = new Fixture();
        await f.Guard.PauseAsync(null);
        f.Now = f.Now.AddDays(2);
        await f.Guard.TickAsync();
        Assert.False(f.Guard.IsAway);
        Assert.Contains("restarts", f.Guard.PauseReason);
        f.Guard.Resume();
        await f.Guard.TickAsync();
        Assert.Null(f.Guard.PauseReason);
        Assert.False(f.Guard.IsAway);
        f.Now = f.Now.AddMinutes(10);
        await f.Guard.TickAsync();
        Assert.True(f.Guard.IsAway);
    }

    [Fact]
    public async Task Pausing_during_disable_cancels_the_action_and_restores_devices()
    {
        var f = new Fixture();
        var blocked = f.PauseDisable();
        var tick = f.Guard.TickAsync();
        Task pause;
        try
        {
            await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            pause = f.Guard.PauseAsync(TimeSpan.FromHours(1));
        }
        finally { blocked.Release(); }
        await Task.WhenAll(tick, pause);
        Assert.Equal(0, f.Actions);
        Assert.Equal(0, f.TurnedOff);
        Assert.NotNull(f.Guard.PauseReason);
    }

    [Fact]
    public async Task Unlock_during_disable_waits_and_restores_devices()
    {
        var f = new Fixture();
        var pause = f.PauseDisable();
        var tick = f.Guard.TickAsync();
        Task back;
        try
        {
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            back = f.Guard.ComeBackAsync("the PC was unlocked");
        }
        finally { pause.Release(); }
        await Task.WhenAll(tick, back);

        Assert.False(f.Guard.IsAway);
        Assert.Equal(0, f.Actions);
        Assert.Equal(1, f.OnAttempts);
    }

    [Fact]
    public async Task Exit_during_disable_restores_devices()
    {
        var f = new Fixture();
        var pause = f.PauseDisable();
        var tick = f.Guard.TickAsync();
        Task stop;
        try
        {
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            stop = f.Guard.StopAsync();
        }
        finally { pause.Release(); }
        await Task.WhenAll(tick, stop);

        Assert.False(f.Guard.IsAway);
        Assert.Equal(0, f.Actions);
        Assert.Equal(1, f.OnAttempts);
        await f.Guard.TickAsync();
        Assert.False(f.Guard.IsAway);
    }

    [Fact]
    public async Task Failed_restoration_retries_on_later_input()
    {
        var f = new Fixture();
        await f.Guard.TickAsync();
        f.FailOn = true;
        await f.Guard.ComeBackAsync("the PC was unlocked");
        Assert.True(f.Guard.IsAway);
        f.FailOn = false;
        f.Human = f.Now = f.Now.AddSeconds(1);
        await f.Guard.TickAsync();

        Assert.False(f.Guard.IsAway);
        Assert.Equal(2, f.OnAttempts);
    }

    [Fact]
    public async Task Devices_are_restored_even_when_the_saved_record_could_not_be_written()
    {
        var f = new Fixture { ForgetOffRecord = true };
        await f.Guard.TickAsync();
        await f.Guard.ComeBackAsync("the PC was unlocked");

        Assert.Equal(1, f.OnAttempts);
        Assert.False(f.Guard.IsAway);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabling_the_group_or_removing_a_device_restores_it(bool remove)
    {
        var f = new Fixture();
        await f.Guard.TickAsync();
        if (remove) f.Settings.AwayDevices.Clear();
        else f.Settings.AwayEnabled = false;
        await f.Guard.TickAsync();

        Assert.False(f.Guard.IsAway);
        Assert.Equal(1, f.OnAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_the_group_or_starting_a_full_screen_app_during_disable_cancels_the_action(bool fullScreen)
    {
        var f = new Fixture();
        var pause = f.PauseDisable();
        var tick = f.Guard.TickAsync();
        try
        {
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (fullScreen) f.FullScreen = true;
            else f.Settings.AwayDevices.Clear();
        }
        finally { pause.Release(); }
        await tick;

        Assert.False(f.Guard.IsAway);
        Assert.Equal(0, f.Actions);
        Assert.Equal(1, f.OnAttempts);
    }

    [Fact]
    public async Task Session_end_while_away_restores_devices_before_returning()
    {
        var f = new Fixture();
        await f.Guard.TickAsync();
        Assert.Equal(1, f.TurnedOff);

        // Nothing awaited: Windows may end the process the moment this returns.
        f.Guard.StopNow("Windows is signing out or shutting down");

        Assert.Equal(0, f.TurnedOff);
        Assert.False(f.Guard.IsAway);
    }

    [Fact]
    public async Task Session_end_during_disable_waits_for_it_then_restores_before_returning()
    {
        var f = new Fixture();
        var pause = f.PauseDisable();
        var tick = f.Guard.TickAsync();
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // The worker finishes turning the stick off while StopNow waits for it.
        var finish = Task.Run(async () => { await Task.Delay(200); pause.Release(); });
        f.Guard.StopNow("Windows is signing out or shutting down");

        Assert.Equal(0, f.TurnedOff);
        await Task.WhenAll(tick, finish);
        Assert.Equal(0, f.TurnedOff);
        Assert.False(f.Guard.IsAway);
        Assert.Equal(0, f.Actions); // no screen-off or sleep after it
    }
}
