using System.Text;

namespace WhyNotSleeping;

internal sealed class Options
{
    public int IntervalSeconds { get; private set; } = 5;
    public bool Once { get; private set; }
    public bool NoElevate { get; private set; }
    public string? LogPath { get; private set; } = Path.GetFullPath("whynotsleeping.log");

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-i" or "--interval" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s > 0:
                    o.IntervalSeconds = s; i++; break;
                case "--once": o.Once = true; break;
                case "--no-elevate": o.NoElevate = true; break;
                case "--no-log": o.LogPath = null; break;
                case "--log" when i + 1 < args.Length:
                    o.LogPath = Path.GetFullPath(args[++i]); break;
                default: return null;
            }
        }
        return o;
    }

    /// <summary>Arguments to pass to the elevated copy (log path made absolute, since elevation changes the cwd).</summary>
    public string ToArguments()
    {
        var parts = new List<string> { "--interval", IntervalSeconds.ToString() };
        if (Once) parts.Add("--once");
        parts.Add("--no-elevate");
        if (LogPath is null) parts.Add("--no-log");
        else { parts.Add("--log"); parts.Add($"\"{LogPath}\""); }
        return string.Join(' ', parts);
    }

    public const string Usage = """
        WhyNotSleeping - shows what is keeping Windows awake.

        Usage: WhyNotSleepingCli [options]
          -i, --interval <sec>  Refresh interval (default 5)
              --once            Print a single snapshot and exit
              --log <file>      Event log file (default .\whynotsleeping.log)
              --no-log          Don't write an event log
              --no-elevate      Don't relaunch as administrator (power requests won't be visible)

        Keys: [q]/[Esc] quit, [r] refresh now.
        """;
}

internal static class Program
{
    record struct Line(string Text, ConsoleColor Color = ConsoleColor.Gray);

    static int _lastLineCount;

    static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.WriteLine(Options.Usage);
            return args.Any(a => a is "-h" or "--help" or "/?") ? 0 : 1;
        }

        var isAdmin = Elevation.IsAdmin;
        if (!isAdmin && !options.NoElevate)
        {
            if (Elevation.TryRelaunch(options.ToArguments()))
            {
                Console.WriteLine("Relaunched as administrator in a new window.");
                return 0;
            }
            Console.WriteLine("Elevation declined - running in limited mode (power requests not visible).");
            Thread.Sleep(1500);
        }

        Console.OutputEncoding = Encoding.UTF8;
        var monitor = new SleepMonitor(options.IntervalSeconds, options.LogPath);
        monitor.Start(isAdmin);

        if (options.Once || Console.IsOutputRedirected)
        {
            foreach (var line in BuildScreen(monitor.Once(), options, int.MaxValue))
                Console.WriteLine(line.Text);
            return 0;
        }

        Console.CursorVisible = false;
        Console.CancelKeyPress += (_, _) => Console.CursorVisible = true;
        Console.Clear();

        try
        {
            while (true)
            {
                Render(BuildScreen(monitor.Tick(), options, Math.Max(Console.WindowHeight - 1, 10)));

                if (WaitForKey(options.IntervalSeconds) is ConsoleKey.Q or ConsoleKey.Escape)
                    break;
            }
        }
        finally
        {
            Console.CursorVisible = true;
            Console.SetCursorPosition(0, Math.Min(_lastLineCount, Console.BufferHeight - 1));
        }
        monitor.Stop();
        return 0;
    }

    static ConsoleKey? WaitForKey(int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key is ConsoleKey.Q or ConsoleKey.Escape or ConsoleKey.R) return key;
            }
            Thread.Sleep(100);
        }
        return null;
    }

    static IEnumerable<Line> Findings(List<Finding> findings) => findings.Select(f => f.Severity switch
    {
        Severity.Problem => new Line($"  ! {f.Text}", ConsoleColor.Red),
        Severity.Warning => new Line($"  ! {f.Text}", ConsoleColor.Yellow),
        Severity.Unknown => new Line($"  ? {f.Text}", ConsoleColor.Yellow),
        Severity.Info => new Line($"  - {f.Text}", ConsoleColor.DarkGray),
        _ => new Line($"  OK {f.Text}", ConsoleColor.Green),
    });

    static List<Line> BuildScreen(Report r, Options options, int maxLines)
    {
        var s = r.Snapshot;
        var p = s.Power;
        var lines = new List<Line>
        {
            new($"WhyNotSleeping  -  {s.Time:HH:mm:ss}, refresh every {options.IntervalSeconds}s   [q] quit  [r] refresh", ConsoleColor.White),
            new(""),
            new($"Power plan   : {p.SchemeName}"),
            new($"Power source : {(p.OnAC ? "AC" : "Battery")}{(p.BatteryPercent is { } b ? $" (battery {b}%)" : "")}"),
            new($"Sleep after  : {Format.Timeout(p.SleepAc)} on AC / {Format.Timeout(p.SleepDc)} on battery"),
            new($"Hibernate    : {Format.Timeout(p.HibernateAc)} on AC / {Format.Timeout(p.HibernateDc)} on battery"),
            new($"Display off  : {Format.Timeout(p.DisplayAc)} on AC / {Format.Timeout(p.DisplayDc)} on battery"),
            new($"User idle    : {Format.Duration(p.UserIdle)} ({r.LastInputDescription})"),
            new($"Screen       : {r.DisplayDescription}"),
        };
        if (p.ExecutionState is { } es)
            lines.Add(new($"Exec state   : {PowerState.DescribeExecutionState(es)}"));

        lines.Add(new(""));
        lines.Add(new("WHY IT'S NOT SLEEPING", ConsoleColor.White));
        lines.AddRange(Findings(r.Sleep));

        lines.Add(new(""));
        lines.Add(new("WHY THE SCREEN ISN'T TURNING OFF", ConsoleColor.White));
        lines.AddRange(Findings(r.Screen));

        if (s.Requests != null)
        {
            lines.Add(new(""));
            lines.Add(new("ACTIVE POWER REQUESTS (powercfg /requests)", ConsoleColor.White));
            if (r.Active.Count == 0)
                lines.Add(new("  none", ConsoleColor.DarkGray));
            foreach (var (request, since) in r.Active)
            {
                var color = request.BlocksSleep ? ConsoleColor.Red : request.KeepsDisplayOn ? ConsoleColor.Yellow : ConsoleColor.Gray;
                lines.Add(new($"  {request.Category,-16} [{request.Kind}] {request.ShortName}   for {Format.Duration(s.Time - since)}", color));
                lines.Add(new($"  {"",-16} {request.Name}", ConsoleColor.DarkGray));
                if (!string.IsNullOrEmpty(request.Reason))
                    lines.Add(new($"  {"",-16} reason: {request.Reason}", ConsoleColor.DarkGray));
            }
        }

        if (s.Overrides.Count > 0)
        {
            lines.Add(new(""));
            lines.Add(new("REQUEST OVERRIDES (powercfg /requestsoverride - these are ignored by Windows)", ConsoleColor.White));
            lines.AddRange(s.Overrides.Select(o => new Line($"  {o}", ConsoleColor.DarkGray)));
        }

        lines.Add(new(""));
        lines.Add(new("INPUT DEVICES (all input since start, and which of it reset the sleep/screen-off idle timer)", ConsoleColor.White));
        if (s.Inputs.Count == 0)
            lines.Add(new(InputMonitor.Available ? "  no input seen yet" : "  raw input unavailable", ConsoleColor.DarkGray));
        else
            lines.Add(new($"  {"DEVICE",-13} {"LAST INPUT",-12} {"EVENTS",8} {"IDLE RESETS",12} {"LAST MIN",9}   NAME", ConsoleColor.DarkGray));
        foreach (var input in s.Inputs.Take(8))
            lines.Add(new($"  {InputMonitor.Label(input.Kind),-13} {Format.Duration(s.Time - input.LastInput) + " ago",-12} {input.Events,8} {input.IdleResets,12} {input.ResetsLastMinute,9}   {input.Name}",
                input.Suspicious ? ConsoleColor.Red : input.IdleResets == 0 ? ConsoleColor.DarkGray : ConsoleColor.Gray));

        // Recent events fill whatever space is left
        lines.Add(new(""));
        lines.Add(new($"RECENT EVENTS{(options.LogPath is null ? "" : $" (logged to {options.LogPath})")}", ConsoleColor.White));
        var room = Math.Max(maxLines - lines.Count, 3);
        foreach (var e in r.Events.TakeLast(room))
            lines.Add(new($"  {e}", e.Contains("+ new") ? ConsoleColor.Yellow : e.Contains("- released") ? ConsoleColor.Green
            : e.Contains("* input") ? ConsoleColor.Magenta : ConsoleColor.Cyan));

        return lines.Take(maxLines).ToList();
    }

    static void Render(List<Line> lines)
    {
        var width = Math.Max(Console.WindowWidth - 1, 20);
        Console.SetCursorPosition(0, 0);
        foreach (var line in lines)
        {
            Console.ForegroundColor = line.Color;
            var text = line.Text.Length > width ? line.Text[..(width - 3)] + "..." : line.Text;
            Console.WriteLine(text.PadRight(width));
        }
        Console.ResetColor();
        for (var i = lines.Count; i < _lastLineCount; i++)
            Console.WriteLine(new string(' ', width));
        _lastLineCount = lines.Count;
    }
}
