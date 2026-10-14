namespace WhyNotSleeping.Tests;

/// <summary>The two verdicts, from snapshots made up for the case at hand.</summary>
public class DiagnosisTests
{
    static readonly DateTime Now = new(2026, 10, 14, 3, 0, 0);

    static PowerState Power(uint sleep = 1800, uint display = 600, TimeSpan? idle = null) =>
        new("Balanced", true, null, sleep, sleep, 0, 0, display, display, idle ?? TimeSpan.FromSeconds(30), 0);

    static Snapshot Snap(PowerState power, List<PowerRequest>? requests = null, params InputSource[] inputs) =>
        new(Now, power, requests ?? [], [], inputs.ToList(), []);

    static InputSource Hotas(int resets, string? instanceId = @"HID\VID_044F&PID_0402\c&3738ddd0&0&0000") =>
        new(1, InputKind.Controller, "Joystick - HOTAS Warthog (VID 044F PID 0402)", Now, 5000, Now, 5000, resets, "vendor data",
            new ControlStatus("vendor channel 2", true, 33870, 0, 65535, 1300, 33856, 33879), instanceId);

    static List<(PowerRequest Request, DateTime Since)> Active(params PowerRequest[] requests) =>
        requests.Select(r => (r, Now.AddMinutes(-5))).ToList();

    [Fact]
    public void Nothing_in_the_way_is_ok()
    {
        var s = Snap(Power());
        Assert.DoesNotContain(Diagnosis.Sleep(s, []), f => f.Severity > Severity.Info);
        Assert.DoesNotContain(Diagnosis.Screen(s, []), f => f.Severity > Severity.Info);
    }

    [Fact]
    public void Sleep_never_is_a_problem_for_sleep_only()
    {
        var s = Snap(Power(sleep: 0));
        Assert.Contains(Diagnosis.Sleep(s, []), f => f.Severity == Severity.Problem && f.Text.Contains("'Never'"));
        Assert.DoesNotContain(Diagnosis.Screen(s, []), f => f.Severity == Severity.Problem);
    }

    [Fact]
    public void A_display_request_keeps_the_screen_on()
    {
        var video = new PowerRequest("DISPLAY", "PROCESS", "firefox.exe", "Playing video");
        var s = Snap(Power(), [video]);
        Assert.Contains(Diagnosis.Screen(s, Active(video)), f => f.Severity == Severity.Problem);
        Assert.Contains(Diagnosis.Sleep(s, Active(video)), f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void A_noisy_controller_is_a_problem_naming_its_control()
    {
        var finding = Diagnosis.Screen(Snap(Power(), null, Hotas(1300)), []).Single(f => f.Severity == Severity.Problem);
        Assert.Contains("HOTAS Warthog", finding.Text);
        Assert.Contains("vendor channel 2", finding.Text);
        Assert.Contains("raw data games don't use", finding.Text);
    }

    [Fact]
    public void A_noisy_controller_in_the_away_group_is_only_a_warning()
    {
        var hotas = Hotas(1300);
        var s = Snap(Power(), null, hotas) with
        {
            Away = new AwayGroup(new HashSet<string>([hotas.InstanceId!], StringComparer.OrdinalIgnoreCase), 10),
        };
        var findings = Diagnosis.Screen(s, []);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Problem);
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Text.Contains("away group") && f.Text.Contains("10 min"));
    }

    [Fact]
    public void A_failed_away_action_remains_a_problem_and_names_the_failure()
    {
        var hotas = Hotas(1300);
        var s = Snap(Power(), null, hotas) with
        {
            Away = new AwayGroup(new HashSet<string>([hotas.InstanceId!]), 10)
            {
                Failures = new Dictionary<string, string> { [hotas.InstanceId!] = "Steam has the device open." },
            },
        };
        Assert.Contains(Diagnosis.Sleep(s, []), f => f.Severity == Severity.Problem && f.Text.Contains("Steam"));
        Assert.Contains(Diagnosis.Screen(s, []), f => f.Severity == Severity.Problem && f.Text.Contains("Steam"));
    }

    [Fact]
    public void A_paused_away_group_does_not_hide_a_noisy_controller()
    {
        var hotas = Hotas(1300);
        var s = Snap(Power(), null, hotas) with
        {
            Away = new AwayGroup(new HashSet<string>([hotas.InstanceId!]), 10) { PauseReason = "Paused until 04:00." },
        };
        Assert.Contains(Diagnosis.Sleep(s, []), f => f.Severity == Severity.Problem && f.Text.Contains("Paused until 04:00"));
        Assert.Contains(Diagnosis.Screen(s, []), f => f.Severity == Severity.Problem && f.Text.Contains("Paused until 04:00"));
    }

    [Fact]
    public void Another_device_than_the_away_group_stays_a_problem()
    {
        var s = Snap(Power(), null, Hotas(1300, @"HID\VID_1234&PID_5678\x")) with
        {
            Away = new AwayGroup(new HashSet<string>([@"HID\VID_044F&PID_0402\c&3738ddd0&0&0000"]), 10),
        };
        Assert.Contains(Diagnosis.Screen(s, []), f => f.Severity == Severity.Problem);
    }

    [Fact]
    public void Without_admin_rights_sleep_is_unknown()
    {
        var s = new Snapshot(Now, Power(), null, [], [], []);
        Assert.Contains(Diagnosis.Sleep(s, []), f => f.Severity == Severity.Unknown);
    }

    [Fact]
    public void Countdowns_say_how_long_is_left()
    {
        var s = Snap(Power(sleep: 1800, display: 600, idle: TimeSpan.FromMinutes(4)));
        Assert.Contains(Diagnosis.Sleep(s, []), f => f.Severity == Severity.Info && f.Text.Contains("26m 0s left"));
        Assert.Contains(Diagnosis.Screen(s, []), f => f.Severity == Severity.Info && f.Text.Contains("6m 0s left"));
    }

    [Fact]
    public void A_screen_turning_off_late_says_by_how_much()
    {
        var change = new DisplayChange(Now, DisplayState.Off, TimeSpan.FromMinutes(25));
        Assert.Equal("* display off after 25m 0s idle (timeout 10m 0s) - 15m 0s late", Diagnosis.DescribeDisplayChange(change, Power()));
    }

    [Fact]
    public void A_screen_turning_off_on_time_says_nothing_more()
    {
        var change = new DisplayChange(Now, DisplayState.Off, TimeSpan.FromSeconds(605));
        Assert.Equal("* display off after 10m 5s idle (timeout 10m 0s)", Diagnosis.DescribeDisplayChange(change, Power()));
    }
}
