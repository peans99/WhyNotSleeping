namespace WhyNotSleeping.Tests;

/// <summary>Reading `powercfg /requests` and `/requestsoverride` output, the way Windows prints it.</summary>
public class PowerCfgTests
{
    const string Requests = """
        DISPLAY:
        [PROCESS] \Device\HarddiskVolume3\Program Files\Mozilla Firefox\firefox.exe
        Playing video

        SYSTEM:
        [DRIVER] NVIDIA High Definition Audio (HDAUDIO\FUNC_01&VEN_10DE&DEV_00AB)
        An audio stream is currently in use.
        [PROCESS] \Device\HarddiskVolume3\Windows\System32\svchost.exe
        Windows Update is installing
        an update

        AWAYMODE:
        None.

        EXECUTION:
        None.

        PERFBOOST:
        None.

        ACTIVELOCKSCREEN:
        None.

        """;

    [Fact]
    public void Requests_are_read_with_their_category_kind_and_reason()
    {
        var requests = PowerCfg.ParseRequests(Requests);

        Assert.Equal(3, requests.Count);
        Assert.Equal(("DISPLAY", "PROCESS", "Playing video"), (requests[0].Category, requests[0].Kind, requests[0].Reason));
        Assert.Equal("NVIDIA High Definition Audio (HDAUDIO\\FUNC_01&VEN_10DE&DEV_00AB)", requests[1].Name);
        Assert.Equal("DRIVER", requests[1].Kind);
    }

    [Fact]
    public void A_reason_over_several_lines_is_joined()
    {
        var update = PowerCfg.ParseRequests(Requests)[2];
        Assert.Equal("Windows Update is installing an update", update.Reason);
    }

    [Fact]
    public void Empty_categories_give_nothing()
    {
        const string nothing = "DISPLAY:\nNone.\n\nSYSTEM:\nNone.\n\nAWAYMODE:\nNone.\n";
        Assert.Empty(PowerCfg.ParseRequests(nothing));
    }

    [Fact]
    public void Windows_line_endings_parse_the_same()
    {
        var crlf = PowerCfg.ParseRequests(Requests.Replace("\n", "\r\n"));
        Assert.Equal(PowerCfg.ParseRequests(Requests).Select(r => r.Key), crlf.Select(r => r.Key));
    }

    [Theory]
    [InlineData("SYSTEM", true, false)]
    [InlineData("EXECUTION", true, false)]
    [InlineData("AWAYMODE", true, false)]
    [InlineData("DISPLAY", false, true)]
    public void What_a_request_blocks(string category, bool blocksSleep, bool keepsDisplayOn)
    {
        var request = new PowerRequest(category, "PROCESS", "app.exe", "");
        Assert.Equal(blocksSleep, request.BlocksSleep);
        Assert.Equal(keepsDisplayOn, request.KeepsDisplayOn);
    }

    [Fact]
    public void A_process_path_is_shortened_to_its_file_name()
    {
        Assert.Equal("Discord.exe", new PowerRequest("SYSTEM", "PROCESS", @"C:\Users\me\AppData\Local\Discord\Discord.exe", "").ShortName);
        Assert.Equal("Audio driver", new PowerRequest("SYSTEM", "DRIVER", "Audio driver", "").ShortName);
    }

    [Fact]
    public void Describe_leaves_out_an_empty_reason()
    {
        Assert.Equal("SYSTEM [PROCESS] app.exe", new PowerRequest("SYSTEM", "PROCESS", "app.exe", "").Describe());
        Assert.Equal("SYSTEM [PROCESS] app.exe - audio", new PowerRequest("SYSTEM", "PROCESS", "app.exe", "audio").Describe());
    }

    [Fact]
    public void Device_paths_become_drive_paths()
    {
        var map = new List<(string Device, string Drive)> { (@"\Device\HarddiskVolume3", "C:"), (@"\Device\HarddiskVolume30", "D:") };
        Assert.Equal(@"C:\Windows\explorer.exe", DevicePaths.ToDosPath(@"\Device\HarddiskVolume3\Windows\explorer.exe", map));
        // Volume30 starts with Volume3: only a whole path segment matches.
        Assert.Equal(@"D:\Games\game.exe", DevicePaths.ToDosPath(@"\Device\HarddiskVolume30\Games\game.exe", map));
        Assert.Equal("Some driver", DevicePaths.ToDosPath("Some driver", map));
    }

    [Fact]
    public void An_override_keeps_the_request_types_off_the_end_of_the_name()
    {
        var o = RequestOverride.Parse("PROCESS", "My App.exe display SYSTEM");
        Assert.Equal("My App.exe", o.Name);
        Assert.Equal(new[] { "DISPLAY", "SYSTEM" }, o.Requests);
        Assert.Equal("PROCESS: My App.exe DISPLAY SYSTEM", o.ToString());
    }

    [Fact]
    public void An_override_named_like_a_request_type_keeps_its_name()
    {
        var o = RequestOverride.Parse("SERVICE", "DISPLAY");
        Assert.Equal("DISPLAY", o.Name);
        Assert.Empty(o.Requests);
    }
}
