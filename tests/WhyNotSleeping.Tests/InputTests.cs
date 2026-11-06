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

    static readonly DateTime T0 = new(2026, 11, 5, 18, 53, 40);

    static DateTime Ms(int milliseconds) => T0.AddMilliseconds(milliseconds);

    [Fact]
    public void A_vendor_channel_never_counts_as_use()
    {
        var motion = new AxisMotion();
        Assert.Null(InputMonitor.Deliberate(Move("vendor channel 2", 0, 60000, 65535), motion, Ms(0)));
        Assert.Null(InputMonitor.Deliberate(Move("vendor channel 2", 60000, 60000, 65535), motion, Ms(1000)));
    }

    [Fact]
    public void Pressing_a_button_counts_and_releasing_it_does_not()
    {
        var motion = new AxisMotion();
        Assert.Equal("button 3 pressed", InputMonitor.Deliberate(new ControlChange("button 3", 1, false, 0, 1), motion, Ms(0)));
        Assert.Null(InputMonitor.Deliberate(new ControlChange("button 3", 0, false, 0, 1), motion, Ms(100)));
    }

    [Fact]
    public void Jitter_around_where_an_axis_rests_does_not_count()
    {
        // The Warthog throttle's slider: 512-543 on 0-1023, band 40.
        var motion = new AxisMotion();
        var t = 0;
        foreach (var (from, to) in new[] { (527L, 531L), (531L, 512L), (512L, 543L), (543L, 520L) })
            Assert.Null(InputMonitor.Deliberate(Move("slider", from, to), motion, Ms(t += 400)));
        Assert.Equal(527, motion.Rest);
    }

    [Fact]
    public void Moving_an_axis_further_than_jitter_counts_once_it_holds_and_becomes_the_new_rest()
    {
        var motion = new AxisMotion();
        Assert.Null(InputMonitor.Deliberate(Move("X axis", 500, 510), motion, Ms(0)));
        Assert.Null(InputMonitor.Deliberate(Move("X axis", 510, 800), motion, Ms(16))); // not yet: it might snap back
        Assert.Null(motion.Settle(Ms(200)));
        Assert.Equal("X axis 500 → 800", motion.Settle(Ms(300)));
        Assert.Equal(800, motion.Rest);
        // Jitter around the new position doesn't count again.
        Assert.Null(InputMonitor.Deliberate(Move("X axis", 800, 790), motion, Ms(1000)));
        Assert.Null(motion.Settle(Ms(2000)));
    }

    [Fact]
    public void A_move_that_keeps_going_counts_without_waiting_for_a_read()
    {
        var motion = new AxisMotion();
        Assert.Null(InputMonitor.Deliberate(Move("Z axis", 0, 100), motion, Ms(0)));
        Assert.Null(InputMonitor.Deliberate(Move("Z axis", 100, 200), motion, Ms(100)));
        Assert.Equal("Z axis 0 → 300", InputMonitor.Deliberate(Move("Z axis", 200, 300), motion, Ms(260)));
    }

    [Fact]
    public void A_slow_push_counts_even_though_every_step_is_small()
    {
        // A throttle pushed from 0 over half a second, 10 counts a report: no single step is past the band.
        var motion = new AxisMotion();
        string? used = null;
        for (var (v, t) = (0L, 0); v < 500; v += 10, t += 5)
            used ??= InputMonitor.Deliberate(Move("Z axis", v, v + 10), motion, Ms(t));
        used ??= motion.Settle(Ms(1000)); // it stopped moving, so it sends nothing more
        Assert.NotNull(used);
    }

    [Fact]
    public void A_worn_slider_spiking_and_snapping_back_never_counts()
    {
        // Recorded from a Warthog throttle nobody was touching: 0-1023, band 40, a report every ~16 ms, flicking
        // from where it rests to 50-85 and back. Before, this read as someone using the throttle 137 times in 75 s.
        long[] values = [6, 62, 18, 7, 50, 6, 27, 51, 6, 50, 7, 62, 8, 41, 6, 50, 6, 29, 7, 61, 18, 7, 62, 6, 85, 17, 30, 74, 30, 6, 73, 6];
        var motion = new AxisMotion();
        var t = 0;
        for (var round = 0; round < 50; round++)
            for (var i = 1; i < values.Length; i++)
            {
                t += 16;
                Assert.Null(InputMonitor.Deliberate(Move("slider", values[i - 1], values[i]), motion, Ms(t)));
                Assert.Null(motion.Settle(Ms(t + 8)));
            }
        Assert.Null(motion.Settle(Ms(t + 1000)));
        Assert.Equal(6, motion.Rest);
    }

    [Fact]
    public void Slow_drift_counts_once_it_has_gone_far_enough()
    {
        var motion = new AxisMotion();
        var hits = 0;
        var t = 0;
        for (long v = 500; v < 600; v += 5)
            if (InputMonitor.Deliberate(Move("Z axis", v, v + 5), motion, Ms(t += 100)) != null) hits++;
        Assert.Equal(1, hits); // 100 counts of drift with a 40-count band, settling at 550-something
    }

    [Fact]
    public void A_hat_or_few_position_switch_always_counts()
    {
        var motion = new AxisMotion();
        Assert.NotNull(InputMonitor.Deliberate(Move("hat switch", 8, 2, 7), motion, Ms(0)));
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
