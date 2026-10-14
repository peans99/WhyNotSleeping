namespace WhyNotSleeping;

/// <summary>An input device this app turned off: its Windows instance ID and the name it had.</summary>
internal sealed record TurnedOffDevice(string InstanceId, string Name);

/// <summary>
/// Turns an input device off and on again, like Device Manager's Disable and Enable: for a controller whose noisy
/// sensor keeps the screen on, without unplugging it. It stays off, across restarts and replugging, until turned back
/// on. The devices turned off here are remembered so the app can always offer to turn them back on.
/// Needs administrator rights.
/// </summary>
internal static class DeviceSwitch
{
    static readonly string ListPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyNotSleeping", "turned-off-devices.txt");

    /// <summary>\\?\HID#VID_044F&amp;PID_0402#c&amp;3738ddd0&amp;0&amp;0000#{guid} → HID\VID_044F&amp;PID_0402\c&amp;3738ddd0&amp;0&amp;0000</summary>
    public static string? InstanceId(string devicePath)
    {
        var parts = devicePath.TrimStart('\\', '?').Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts[..^1]) : null;
    }

    /// <summary>Returns why it failed, or null when the device is off.</summary>
    public static string? TurnOff(string instanceId, string name)
    {
        var result = Native.CM_Locate_DevNode(out var node, instanceId, 0);
        if (result == Native.CrSuccess)
            result = Native.CM_Disable_DevNode(node, Native.CmDisablePersist);
        if (result == Native.CrRemoveVetoed)
        {
            // Disabling doesn't say who refused; a removal request does. It is refused by the same holder, so it
            // removes nothing, and if the holder let go in between, the device is removed and the retry disables it.
            var holder = new System.Text.StringBuilder(512);
            result = Native.CM_Query_And_Remove_SubTree(node, out var vetoType, holder, holder.Capacity, Native.CmRemoveUiNotOk | Native.CmRemoveNoRestart);
            if (result == Native.CrRemoveVetoed)
                return Vetoed(vetoType, holder.ToString(), instanceId);
            if (result == Native.CrSuccess)
                result = Native.CM_Disable_DevNode(node, Native.CmDisablePersist);
        }
        if (result != Native.CrSuccess)
            return Explain(result);

        var list = Load().Where(d => !d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)).Append(new(instanceId, name));
        Save(list);
        return null;
    }

    /// <summary>Returns why it failed, or null when the device is on again.</summary>
    public static string? TurnOn(string instanceId)
    {
        var result = Native.CM_Locate_DevNode(out var node, instanceId, 0);
        if (result == Native.CrNoSuchDevnode)
            return "The device isn't plugged in. Plug it in, then turn it back on.";
        if (result == Native.CrSuccess)
            result = Native.CM_Enable_DevNode(node, 0);
        if (result != Native.CrSuccess)
            return Explain(result);

        Save(Load().Where(d => !d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)));
        return null;
    }

    /// <summary>
    /// Devices this app turned off that are still off. One turned back on in Device Manager drops off the list; one
    /// that is unplugged stays on it, since it will still be off when it comes back.
    /// </summary>
    public static List<TurnedOffDevice> TurnedOff()
    {
        var all = Load();
        var off = all.Where(d => IsOff(d.InstanceId) != false).ToList();
        if (off.Count != all.Count) Save(off);
        return off;
    }

    /// <summary>True if disabled, false if enabled, null if Windows doesn't know it right now (unplugged).</summary>
    static bool? IsOff(string instanceId)
    {
        if (Native.CM_Locate_DevNode(out var node, instanceId, Native.CmLocateDevnodePhantom) != Native.CrSuccess)
            return null; // never seen, or removed from Windows: nothing to turn back on, but keep it in case
        if (Native.CM_Get_DevNode_Status(out var status, out var problem, node, 0) != Native.CrSuccess)
            return null; // known but not present: unplugged
        return (status & Native.DnHasProblem) != 0 && problem == Native.CmProbDisabled;
    }

    const string UsualHolders = "Games, Steam (Steam Input), web browsers (gamepad support) and the controller's own software often keep controllers open.";

    /// <summary>Who refused to let go of the device, from the PNP_VETO_TYPE and name Windows reports.</summary>
    static string Vetoed(int vetoType, string name, string instanceId) => vetoType switch
    {
        3 => $"\"{name}\" is using the device and won't let go of it. Close it, then try again.", // PNP_VetoWindowsApp
        4 => $"The Windows service \"{name}\" is using the device. Stop it (Services app) or close its program, then try again.", // PNP_VetoWindowsService
        // OutstandingOpen, PendingClose: Windows doesn't say who, so look for the programs holding it.
        5 or 2 => DeviceHolders.Find(instanceId) is { Count: > 0 } holders
            ? $"{string.Join(", ", holders)} {(holders.Count == 1 ? "has" : "have")} the device open and won't let go of it. " +
              $"Close {(holders.Count == 1 ? "it" : "them")} (for Steam: Steam > Exit), then try again."
            : $"An app has the device open and won't let go of it. {UsualHolders} Close them and try again, or unplug the device.",
        6 or 7 => $"Another device or driver depends on it ({name}).", // PNP_VetoDevice, PNP_VetoDriver
        10 => "Windows doesn't allow this device to be turned off.", // PNP_VetoNonDisableable
        _ => $"Something is using the device and won't let go of it{(name.Length > 0 ? $" ({name})" : "")}. {UsualHolders} Close them and try again.",
    };

    static string Explain(uint result) => result switch
    {
        Native.CrAccessDenied => "Windows needs administrator rights for this. Restart WhyNotSleeping as administrator.",
        Native.CrNoSuchDevnode => "Windows can't find the device any more. Is it still plugged in?",
        Native.CrRemoveVetoed => $"Something is using the device and won't let go of it. {UsualHolders} Close them and try again.",
        _ => $"Windows refused (configuration manager error 0x{result:X2}).",
    };

    static List<TurnedOffDevice> Load()
    {
        try
        {
            return File.Exists(ListPath)
                ? File.ReadAllLines(ListPath).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => new TurnedOffDevice(p[0], p[1])).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static void Save(IEnumerable<TurnedOffDevice> devices)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ListPath)!);
            File.WriteAllLines(ListPath, devices.Select(d => $"{d.InstanceId}\t{d.Name}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the device is still off; only the reminder is lost */ }
    }
}
