namespace WhyNotSleeping.Gui;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Listing power requests needs administrator rights; if the UAC prompt is declined the window runs in limited
        // mode and offers to restart elevated.
        var isAdmin = Elevation.IsAdmin;
        if (!isAdmin && !args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase) && Elevation.TryRelaunch("--no-elevate"))
            return;

        ApplicationConfiguration.Initialize();
        // Follows Windows' app theme; --theme light|dark overrides it.
        var theme = args.SkipWhile(a => !a.Equals("--theme", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()?.ToLowerInvariant();
        Application.SetColorMode(theme switch { "light" => SystemColorMode.Classic, "dark" => SystemColorMode.Dark, _ => SystemColorMode.System });
        MainForm.Theme = Application.IsDarkModeEnabled ? Palette.Dark : Palette.Light;
        Application.Run(new MainForm(isAdmin, LogPath()));
    }

    /// <summary>%LOCALAPPDATA%\WhyNotSleeping\whynotsleeping.log, since the exe may live somewhere read-only.</summary>
    static string? LogPath()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNotSleeping");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "whynotsleeping.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
