namespace WhyNotSleeping.Tests;

public class EventLogViewTests
{
    [Theory]
    [InlineData("* display off", (int)EventCategory.Display)]
    [InlineData("* input after 5m quiet: mouse", (int)EventCategory.Input)]
    [InlineData("+ new: SYSTEM [PROCESS] app.exe", (int)EventCategory.PowerRequests)]
    [InlineData("- released: SYSTEM [PROCESS] app.exe", (int)EventCategory.PowerRequests)]
    [InlineData("* away: couldn't turn off Stick", (int)EventCategory.DeviceActions)]
    [InlineData("* back: turned Stick back on", (int)EventCategory.DeviceActions)]
    [InlineData("* device: turned off Stick", (int)EventCategory.DeviceActions)]
    [InlineData("* power changed: battery", (int)EventCategory.Power)]
    [InlineData("* resumed: no samples for 1h", (int)EventCategory.Power)]
    [InlineData("monitor started (admin: True)", (int)EventCategory.Other)]
    public void Saved_and_plain_events_have_the_same_category(string text, int category)
    {
        Assert.Equal((EventCategory)category, EventLogView.Category(text));
        Assert.Equal((EventCategory)category, EventLogView.Category("2026-10-14 03:00:00 " + text));
    }

    [Fact]
    public void Search_and_category_filters_are_combined_and_ignore_case()
    {
        const string line = "2026-10-14 03:00:00 * away: couldn't turn off Stick: Steam has it open.";
        Assert.True(EventLogView.Matches(line, EventCategory.DeviceActions, " STEAM "));
        Assert.True(EventLogView.Matches(line, EventCategory.All, ""));
        Assert.False(EventLogView.Matches(line, EventCategory.Display, "Steam"));
        Assert.False(EventLogView.Matches(line, EventCategory.DeviceActions, "Firefox"));
    }

    [Fact]
    public void Export_reads_the_full_saved_history_and_applies_both_filters()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WhyNotSleeping.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "saved.log");
            var export = Path.Combine(folder, "filtered.log");
            var lines = Enumerable.Range(0, 1500).Select(i => $"2026-10-14 03:00:00 * display off {i}")
                .Append("2026-10-14 03:00:01 * input after quiet: mouse").ToArray();
            File.WriteAllLines(source, lines);
            var count = EventLogView.Export(export, EventLogView.ReadFile(source), EventCategory.Display, "off");
            Assert.Equal(1500, count);
            Assert.Equal(lines.Take(1500), File.ReadAllLines(export));
            Assert.Equal(lines, File.ReadAllLines(source));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Reading_the_log_allows_monitoring_to_keep_appending()
    {
        var file = Path.Combine(Path.GetTempPath(), $"WhyNotSleeping-test-{Guid.NewGuid():N}.log");
        File.WriteAllText(file, "first\n");
        try
        {
            using var reading = EventLogView.ReadFile(file).GetEnumerator();
            Assert.True(reading.MoveNext());
            File.AppendAllText(file, "second\n");
            Assert.True(reading.MoveNext());
            Assert.Equal("second", reading.Current);
        }
        finally { File.Delete(file); }
    }
}
