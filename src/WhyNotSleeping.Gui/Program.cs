namespace WhyNotSleeping.Gui;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Already running (in the tray, say): bring that window forward instead of starting a second copy.
        if (!SingleInstance.Claim())
        {
            SingleInstance.ShowRunningCopy();
            return;
        }

        // --tray starts in the notification area without the window, as at sign-in.
        var tray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        // Listing power requests needs administrator rights; if the UAC prompt is declined the window runs in limited
        // mode and offers to restart elevated.
        var isAdmin = Elevation.IsAdmin;
        if (!isAdmin && !args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase))
        {
            SingleInstance.Release(); // the elevated copy takes the name
            if (Elevation.TryRelaunch(tray ? "--no-elevate --tray" : "--no-elevate"))
                return;
            if (!SingleInstance.Claim())
            {
                SingleInstance.ShowRunningCopy();
                return;
            }
        }

        ApplicationConfiguration.Initialize();
        // Follows Windows' app theme; --theme light|dark overrides it.
        var theme = args.SkipWhile(a => !a.Equals("--theme", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()?.ToLowerInvariant();
        Application.SetColorMode(theme switch { "light" => SystemColorMode.Classic, "dark" => SystemColorMode.Dark, _ => SystemColorMode.System });
        MainForm.Theme = Application.IsDarkModeEnabled ? Palette.Dark : Palette.Light;
        Application.Run(new TrayApp(isAdmin, LogPath(), tray));
    }

    /// <summary>%LOCALAPPDATA%\WhyNotSleeping\whynotsleeping.log, since the exe may live somewhere read-only.</summary>
    static string? LogPath()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            return Path.Combine(Settings.Folder, "whynotsleeping.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
