using System.Diagnostics;
using System.Text;

namespace WhyNotSleeping;

/// <summary>One entry from `powercfg /requests`, e.g. SYSTEM / [PROCESS] chrome.exe / "Playing audio".</summary>
internal sealed record PowerRequest(string Category, string Kind, string Name, string Reason)
{
    public string Key => $"{Category}|{Kind}|{Name}|{Reason}";

    /// <summary>SYSTEM, EXECUTION and AWAYMODE requests keep the machine from idle-sleeping.</summary>
    public bool BlocksSleep => Category is "SYSTEM" or "EXECUTION" or "AWAYMODE";

    /// <summary>DISPLAY requests keep the screen on, which in practice also keeps most PCs awake.</summary>
    public bool KeepsDisplayOn => Category == "DISPLAY";

    /// <summary>"Discord.exe" for process paths, the full name otherwise.</summary>
    public string ShortName => Name.Contains(":\\") ? Path.GetFileName(Name) : Name;

    public string Describe() =>
        string.IsNullOrEmpty(Reason) ? $"{Category} [{Kind}] {Name}" : $"{Category} [{Kind}] {Name} - {Reason}";
}

internal static class PowerCfg
{
    public static string Run(string arguments)
    {
        var result = ProcessRunner.RunAsync(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), arguments),
            TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"powercfg failed (exit code {result.ExitCode}). {(result.Output + result.Error).Trim()}");
        return result.Output + result.Error;
    }

    /// <summary>Returns null when not elevated (powercfg /requests requires admin).</summary>
    public static List<PowerRequest>? GetRequests()
    {
        // Checked directly rather than by powercfg's refusal, which is translated: on French Windows it doesn't say
        // "administrator privileges", and the refusal would be parsed as a list with nothing in it.
        if (!Elevation.IsAdmin)
            return null;
        var output = Run("/requests");
        if (output.Contains("administrator privileges", StringComparison.OrdinalIgnoreCase))
            return null;
        return ParseRequests(output);
    }

    internal static List<PowerRequest> ParseRequests(string output)
    {
        var devices = DevicePaths.Snapshot();
        var result = new List<PowerRequest>();
        string? category = null, kind = null, name = null;
        var reason = new StringBuilder();

        void Flush()
        {
            if (category != null && kind != null && name != null)
                result.Add(new PowerRequest(category, kind, DevicePaths.ToDosPath(name, devices), reason.ToString().Trim()));
            kind = name = null;
            reason.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "None.")
                continue;

            if (line.EndsWith(':') && line.TrimEnd(':').All(char.IsUpper))
            {
                Flush();
                category = line.TrimEnd(':');
            }
            else if (line.StartsWith('[') && line.IndexOf(']') is var close and > 0)
            {
                Flush();
                kind = line[1..close];
                name = line[(close + 1)..].Trim();
            }
            else if (name != null)
            {
                if (reason.Length > 0) reason.Append(' ');
                reason.Append(line);
            }
        }
        Flush();
        return result;
    }

    /// <summary>Entries registered with `powercfg /requestsoverride` (these hide requests from Windows).</summary>
    public static List<RequestOverride> GetOverrides()
    {
        var overrides = new List<RequestOverride>();
        string? section = null;
        foreach (var raw in Run("/requestsoverride").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
                section = line.Trim('[', ']');
            else if (section != null)
                overrides.Add(RequestOverride.Parse(section, line));
        }
        return overrides;
    }

    /// <summary>
    /// Makes Windows ignore the given request types from a caller; no request types removes the override.
    /// Requires administrator rights. Returns powercfg's error output, or null on success.
    /// </summary>
    public static string? SetOverride(string callerType, string name, IEnumerable<string> requests)
    {
        var output = Run($"/requestsoverride {callerType} \"{name}\" {string.Join(' ', requests)}").Trim();
        return output.Length == 0 ? null : output;
    }
}

/// <summary>One `powercfg /requestsoverride` entry, e.g. PROCESS / discord.exe / EXECUTION SYSTEM.</summary>
internal sealed record RequestOverride(string CallerType, string Name, IReadOnlyList<string> Requests)
{
    static readonly string[] RequestTypes = ["DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION"];

    /// <summary>The line is the caller name followed by the overridden request types; names may contain spaces.</summary>
    public static RequestOverride Parse(string callerType, string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var requests = new List<string>();
        while (words.Count > 1 && RequestTypes.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
        {
            requests.Insert(0, words[^1].ToUpperInvariant());
            words.RemoveAt(words.Count - 1);
        }
        return new RequestOverride(callerType, string.Join(' ', words), requests);
    }

    public override string ToString() => $"{CallerType}: {Name} {string.Join(' ', Requests)}".TrimEnd();
}

/// <summary>Maps \Device\HarddiskVolumeN\... paths to C:\... paths.</summary>
internal static class DevicePaths
{
    public static List<(string Device, string Drive)> Snapshot()
    {
        var map = new List<(string, string)>();
        var target = new StringBuilder(1024);
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd('\\');
            if (Native.QueryDosDevice(drive, target, target.Capacity) != 0)
                map.Add((target.ToString(), drive));
        }
        return map;
    }

    public static string ToDosPath(string path, List<(string Device, string Drive)> map)
    {
        foreach (var (device, drive) in map)
            if (path.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return drive + path[device.Length..];
        return path;
    }
}
