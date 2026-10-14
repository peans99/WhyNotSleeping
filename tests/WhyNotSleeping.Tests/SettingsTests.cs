using System.Text.Json;
using System.Xml.Linq;
using WhyNotSleeping.Gui;

namespace WhyNotSleeping.Tests;

/// <summary>What is written to disk: the settings file and the startup task.</summary>
public class SettingsTests
{
    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var settings = new Settings
        {
            AwayEnabled = true,
            AwayMinutes = 25,
            AwayAction = AwayAction.Sleep,
            AwaySkipFullScreen = false,
            AwayDevices = [new TurnedOffDevice(@"HID\VID_044F&PID_0402\c&3738ddd0&0&0000", "Joystick - HOTAS Warthog")],
        };

        var json = JsonSerializer.Serialize(settings, SettingsJson.Default.Settings);
        var back = JsonSerializer.Deserialize(json, SettingsJson.Default.Settings)!;

        Assert.Contains("\"Sleep\"", json); // the action by name, so the file reads
        Assert.Equal((true, 25, AwayAction.Sleep, false), (back.AwayEnabled, back.AwayMinutes, back.AwayAction, back.AwaySkipFullScreen));
        Assert.Equal(settings.AwayDevices, back.AwayDevices);
    }

    [Fact]
    public void A_file_from_an_older_version_gets_the_defaults_for_what_it_lacks()
    {
        var back = JsonSerializer.Deserialize("""{ "AwayEnabled": true }""", SettingsJson.Default.Settings)!;
        Assert.Equal((10, AwayAction.ScreenOff, true), (back.AwayMinutes, back.AwayAction, back.AwaySkipFullScreen));
        Assert.Empty(back.AwayDevices);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(25, 25)]
    [InlineData(241, 240)]
    [InlineData(int.MaxValue, 240)]
    public void Saved_minutes_are_validated_for_the_guard_as_well_as_the_window(int saved, int expected)
    {
        Assert.Equal(expected, Settings.Parse($"{{\"AwayMinutes\":{saved}}}").AwayMinutes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"AwayDevices\":null}")]
    public void Null_saved_settings_or_device_lists_use_the_defaults(string json)
    {
        Assert.Empty(Settings.Parse(json).AwayDevices);
    }

    [Fact]
    public void Invalid_and_duplicate_saved_devices_are_removed()
    {
        var settings = Settings.Parse("""
            {"AwayDevices":[null, {}, {"InstanceId":null,"Name":"bad"}, {"InstanceId":" ","Name":"bad"},
              {"InstanceId":"stick","Name":null}, {"InstanceId":"stick","Name":"Stick"},
              {"InstanceId":"STICK","Name":"Duplicate"}], "AwayAction":99}
            """);

        Assert.Equal(new TurnedOffDevice("stick", "Stick"), Assert.Single(settings.AwayDevices));
        Assert.Equal(AwayAction.ScreenOff, settings.AwayAction);
    }

    [Fact]
    public void The_group_is_matched_without_regard_to_case()
    {
        var settings = new Settings { AwayDevices = [new TurnedOffDevice(@"HID\VID_044F&PID_0402\c&3738ddd0&0&0000", "stick")] };
        Assert.True(settings.InAwayGroup(@"hid\vid_044f&pid_0402\C&3738DDD0&0&0000"));
        Assert.False(settings.InAwayGroup(null));
        Assert.False(settings.InAwayGroup(@"HID\VID_1234&PID_5678\x"));
    }

    [Fact]
    public void The_startup_task_runs_elevated_at_logon_in_the_tray_and_never_stops()
    {
        XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = XDocument.Parse(Startup.TaskXml());

        Assert.Equal("HighestAvailable", task.Descendants(t + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", task.Descendants(t + "LogonType").Single().Value);
        Assert.Single(task.Descendants(t + "LogonTrigger"));
        Assert.Equal("PT0S", task.Descendants(t + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", task.Descendants(t + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", task.Descendants(t + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal(Environment.ProcessPath, task.Descendants(t + "Command").Single().Value);
        Assert.EndsWith("--tray", task.Descendants(t + "Arguments").Single().Value);
    }
}
