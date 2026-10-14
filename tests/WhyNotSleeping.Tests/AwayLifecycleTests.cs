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
        public bool FailOn, ForgetOffRecord, FullScreen;
        public int Actions, OnAttempts;
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
}
