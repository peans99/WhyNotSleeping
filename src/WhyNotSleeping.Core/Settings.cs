using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhyNotSleeping;

/// <summary>What to do once the away group's devices are off.</summary>
internal enum AwayAction
{
    /// <summary>Nothing more: Windows turns the screen off and sleeps on its usual schedule.</summary>
    DevicesOnly,
    ScreenOff,
    Sleep,
}

/// <summary>The window's settings, in %LOCALAPPDATA%\WhyNotSleeping\settings.json. Read and written on the UI thread.</summary>
internal sealed class Settings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNotSleeping");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");

    /// <summary>Devices to turn off while nobody uses the keyboard or mouse, e.g. a HOTAS whose sensor never rests.</summary>
    public List<TurnedOffDevice> AwayDevices { get; set; } = [];
    public bool AwayEnabled { get; set; }
    public int AwayMinutes { get; set; } = 10;
    public AwayAction AwayAction { get; set; } = AwayAction.ScreenOff;
    /// <summary>Don't turn anything off under a full-screen game, video or presentation.</summary>
    public bool AwaySkipFullScreen { get; set; } = true;
    /// <summary>The "still running in the tray" tip has been shown once.</summary>
    public bool TrayTipShown { get; set; }

    public bool InAwayGroup(string? instanceId) =>
        instanceId != null && AwayDevices.Any(d => d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* start from the defaults */ }
        return new Settings();
    }

    /// <summary>Validate saved values before either the window or the away guard reads them.</summary>
    internal static Settings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize(json, SettingsJson.Default.Settings) ?? new Settings();
        settings.AwayMinutes = Math.Clamp(settings.AwayMinutes, 1, 240);
        if (!Enum.IsDefined(settings.AwayAction)) settings.AwayAction = AwayAction.ScreenOff;
        settings.AwayDevices = (settings.AwayDevices ?? [])
            .Where(d => d != null && !string.IsNullOrWhiteSpace(d.InstanceId) && !string.IsNullOrWhiteSpace(d.Name))
            .DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are best-effort */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
