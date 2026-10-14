namespace WhyNotSleeping;

internal enum EventCategory { All, Display, Input, PowerRequests, DeviceActions, Power, Other }

/// <summary>Filters the same log lines shown in the window and exported from the full saved log.</summary>
internal static class EventLogView
{
    public static (string Time, string Text) Split(string line) =>
        line.Length >= 20 && line[4] == '-' && line[10] == ' ' && line[19] == ' '
            ? (line[..19], line[20..]) : ("", line);

    public static EventCategory Category(string line)
    {
        var text = Split(line).Text;
        if (text.StartsWith("* display", StringComparison.Ordinal)) return EventCategory.Display;
        if (text.StartsWith("* input", StringComparison.Ordinal)) return EventCategory.Input;
        if (text.StartsWith("+ new:", StringComparison.Ordinal) || text.StartsWith("- released:", StringComparison.Ordinal)) return EventCategory.PowerRequests;
        if (text.StartsWith("* away", StringComparison.Ordinal) || text.StartsWith("* back", StringComparison.Ordinal) ||
            text.StartsWith("* device", StringComparison.Ordinal)) return EventCategory.DeviceActions;
        if (text.StartsWith("* power", StringComparison.Ordinal) || text.StartsWith("* resumed", StringComparison.Ordinal)) return EventCategory.Power;
        return EventCategory.Other;
    }

    public static bool Matches(string line, EventCategory category, string search) =>
        (category == EventCategory.All || Category(line) == category) && line.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> ReadFile(string path)
    {
        // Monitoring can keep appending while a saved log is exported.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line) yield return line;
    }

    public static int Export(string path, IEnumerable<string> lines, EventCategory category, string search)
    {
        var count = 0;
        AtomicFile.Write(path, writer =>
        {
            foreach (var line in lines.Where(line => Matches(line, category, search)))
            {
                writer.WriteLine(line);
                count++;
            }
        });
        return count;
    }
}
