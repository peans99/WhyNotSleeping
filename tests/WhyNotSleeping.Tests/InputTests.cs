namespace WhyNotSleeping.Tests;

/// <summary>Telling a noisy sensor from someone using a controller, and how devices are named.</summary>
public class InputTests
{
    static ControlStatus Axis(int changes, long min, long max, long logicalMin = 0, long logicalMax = 65535, bool unattended = false) =>
        new("vendor channel 2", true, min, logicalMin, logicalMax, changes, min, max, unattended);

    [Fact]
    public void Many_changes_in_a_sliver_of_the_range_is_jitter()
    {
        // The Warthog's vendor channel: thousands of changes between 33856 and 33879 of 0-65535.
        Assert.True(Axis(1300, 33856, 33879).Jitter);
    }

    [Fact]
    public void A_wide_move_is_not_jitter()
    {
        Assert.False(Axis(1300, 1000, 60000).Jitter);
    }

    [Fact]
    public void A_few_changes_are_not_jitter()
    {
        Assert.False(Axis(9, 33856, 33860).Jitter);
    }

    [Fact]
    public void A_small_range_control_never_jitters()
    {
        // A hat switch or a 3-bit dial has no sliver to wander in.
        Assert.False(Axis(500, 3, 4, 0, 7).Jitter);
    }

    [Fact]
    public void Changing_nonstop_with_nobody_at_the_keyboard_is_on_its_own()
    {
        var swinging = Axis(3000, 1000, 60000, unattended: true);
        Assert.False(swinging.Jitter);
        Assert.True(swinging.OnItsOwn);
    }

    [Theory]
    [InlineData(0, 65535, 2621)]
    [InlineData(0, 1023, 40)]
    [InlineData(0, 10, 2)] // at least 2 counts
    public void The_jitter_band_is_a_25th_of_the_range(long min, long max, long band)
    {
        Assert.Equal(band, ControlStatus.JitterBand(min, max));
    }

    [Theory]
    [InlineData(0, "up")]
    [InlineData(3, "down-right")]
    [InlineData(7, "up-left")]
    [InlineData(8, "centered")] // out of range is how a hat says centered
    public void A_hat_switch_reads_as_a_direction(long value, string expected)
    {
        Assert.Equal(expected, new ControlStatus("hat switch", true, value, 0, 7, 0, null, null).DisplayValue);
    }

    [Fact]
    public void A_button_reads_as_pressed_or_released()
    {
        Assert.Equal("pressed", new ControlStatus("button 1", false, 1, 0, 1, 0, null, null).DisplayValue);
        Assert.Equal("released", new ControlStatus("button 1", false, 0, 0, 1, 0, null, null).DisplayValue);
    }

    static ControlChange Move(string control, long from, long to, long logicalMax = 1023) =>
        new(control, to, true, 0, logicalMax) { Previous = from };

    [Fact]
    public void A_vendor_channel_never_counts_as_use()
    {
        long? rest = null;
        Assert.False(InputMonitor.Deliberate(Move("vendor channel 2", 0, 60000, 65535), ref rest));
    }

    [Fact]
    public void Pressing_a_button_counts_and_releasing_it_does_not()
    {
        long? rest = null;
        Assert.True(InputMonitor.Deliberate(new ControlChange("button 3", 1, false, 0, 1), ref rest));
        Assert.False(InputMonitor.Deliberate(new ControlChange("button 3", 0, false, 0, 1), ref rest));
    }

    [Fact]
    public void Jitter_around_where_an_axis_rests_does_not_count()
    {
        // The Warthog throttle's slider: 512-543 on 0-1023, band 40.
        long? rest = null;
        foreach (var (from, to) in new[] { (527L, 531L), (531L, 512L), (512L, 543L), (543L, 520L) })
            Assert.False(InputMonitor.Deliberate(Move("slider", from, to), ref rest));
        Assert.Equal(527, rest);
    }

    [Fact]
    public void Moving_an_axis_further_than_jitter_counts_and_becomes_the_new_rest()
    {
        long? rest = null;
        Assert.False(InputMonitor.Deliberate(Move("X axis", 500, 510), ref rest));
        Assert.True(InputMonitor.Deliberate(Move("X axis", 510, 800), ref rest));
        Assert.Equal(800, rest);
        // Jitter around the new position doesn't count again.
        Assert.False(InputMonitor.Deliberate(Move("X axis", 800, 790), ref rest));
    }

    [Fact]
    public void Slow_drift_counts_once_it_has_gone_far_enough()
    {
        long? rest = null;
        var hits = 0;
        for (long v = 500; v < 600; v += 5)
            if (InputMonitor.Deliberate(Move("Z axis", v, v + 5), ref rest)) hits++;
        Assert.Equal(2, hits); // 100 counts of drift with a 40-count band
    }

    [Fact]
    public void A_hat_or_few_position_switch_always_counts()
    {
        long? rest = null;
        Assert.True(InputMonitor.Deliberate(Move("hat switch", 8, 2, 7), ref rest));
    }

    [Theory]
    [InlineData("Joystick - HOTAS Warthog (VID 044F PID 0402)", "Joystick - HOTAS Warthog")]
    [InlineData("Razer Basilisk V3", "Razer Basilisk V3")]
    [InlineData("Pad (wireless)", "Pad (wireless)")]
    public void A_short_name_drops_the_ids(string name, string expected)
    {
        Assert.Equal(expected, InputSource.Short(name));
    }

    [Fact]
    public void Only_non_mouse_non_keyboard_input_that_resets_the_timer_is_suspicious()
    {
        static InputSource Source(InputKind kind, int resets) =>
            new(1, kind, "x", DateTime.Now, 1, null, 0, resets, null, null, null);
        Assert.True(Source(InputKind.Controller, 3).Suspicious);
        Assert.True(Source(InputKind.Software, 1).Suspicious);
        Assert.False(Source(InputKind.Controller, 0).Suspicious);
        Assert.False(Source(InputKind.Mouse, 50).Suspicious);
        Assert.False(Source(InputKind.Keyboard, 50).Suspicious);
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_044F&PID_0402#c&3738ddd0&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", @"HID\VID_044F&PID_0402\c&3738ddd0&0&0000")]
    [InlineData(@"\\?\HID#VID_046D&PID_C52B&MI_02&Col01#8&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", @"HID\VID_046D&PID_C52B&MI_02&Col01\8&1a2b3c&0&0000")]
    [InlineData(@"\\?\ACPI#PNP0303", null)]
    public void A_device_path_gives_its_instance_id(string path, string? expected)
    {
        Assert.Equal(expected, DeviceSwitch.InstanceId(path));
    }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(65, "1m 5s")]
    [InlineData(3 * 3600 + 120, "3h 2m")]
    [InlineData(2 * 86400 + 3600, "2d 1h 0m")]
    public void Durations_read_short(int seconds, string expected)
    {
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void A_zero_timeout_is_never()
    {
        Assert.Equal("Never", Format.Timeout(0));
        Assert.Equal("10m 0s", Format.Timeout(600));
    }
}
