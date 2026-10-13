using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WhyNotSleeping;

internal enum InputKind { Mouse, Keyboard, Controller, Touchpad, Touch, Pen, MediaKeys, SystemButtons, OtherHid, Software }

/// <summary>
/// A device that has sent input since the monitor started. Not all input counts as user activity: game controllers
/// usually don't reset Windows' idle timer, so IdleResets tells which devices actually keep the PC from idling.
/// </summary>
internal sealed record InputSource(InputKind Kind, string Name, DateTime LastInput, long Events,
    DateTime? LastIdleReset, long IdleResets, int ResetsLastMinute)
{
    /// <summary>Mouse and keyboard input is normally the user; anything else resetting the timer is worth pointing out.</summary>
    public bool Suspicious => Kind is not (InputKind.Mouse or InputKind.Keyboard) && ResetsLastMinute > 0;
}

/// <summary>Input that reset the idle timer after a quiet period: what ended the idle time (e.g. a mouse nudged at 3am).</summary>
internal sealed record InputResumed(DateTime Time, InputKind Kind, string Name, TimeSpan Quiet);

/// <summary>
/// Watches raw input from every mouse, keyboard, game controller, touchpad, touch screen, pen and media/power-key device,
/// so the app can say which one reset the idle timer. Software-injected input (SendInput, remote desktop, jiggler apps)
/// has no device handle and shows up as its own source.
/// </summary>
internal static partial class InputMonitor
{
    /// <summary>Idle-resetting input after this much idle time is logged as an event.</summary>
    public static readonly TimeSpan QuietThreshold = TimeSpan.FromSeconds(60);

    static readonly (ushort Page, ushort Usage)[] Usages =
    [
        (0x01, 0x02), // mouse
        (0x01, 0x06), // keyboard
        (0x01, 0x07), // keypad
        (0x01, 0x04), // joystick
        (0x01, 0x05), // gamepad
        (0x01, 0x08), // multi-axis controller
        (0x01, 0x80), // system control (power/sleep buttons)
        (0x0C, 0x01), // consumer control (media/volume keys, remotes)
        (0x0D, 0x02), // pen
        (0x0D, 0x04), // touch screen
        (0x0D, 0x05), // precision touchpad
    ];

    sealed class Device(InputKind kind, string name)
    {
        public readonly InputKind Kind = kind;
        public readonly string Name = name;
        public DateTime LastInput;
        public long Events;
        public DateTime? LastIdleReset;
        public long IdleResets;
        public readonly Queue<DateTime> RecentResets = new();
        public byte[]? LastReport; // HID only
    }

    // The message loop runs on its own thread; the main loop reads snapshots under this lock.
    static readonly Lock Sync = new();
    static readonly Dictionary<IntPtr, Device> Devices = new();
    static readonly ConcurrentQueue<InputResumed> Pending = new();
    static DateTime _lastReset;
    static uint _lastResetTick;

    public static bool Available { get; private set; }

    public static void Start()
    {
        _lastReset = DateTime.Now - PowerState.CaptureUserIdle();
        new Thread(Run) { IsBackground = true, Name = "raw input" }.Start();
    }

    public static string Label(InputKind kind) => kind switch
    {
        InputKind.Mouse => "mouse",
        InputKind.Keyboard => "keyboard",
        InputKind.Controller => "controller",
        InputKind.Touchpad => "touchpad",
        InputKind.Touch => "touch screen",
        InputKind.Pen => "pen",
        InputKind.MediaKeys => "media keys",
        InputKind.SystemButtons => "power button",
        InputKind.Software => "software",
        _ => "HID device",
    };

    /// <summary>Devices that have sent input, most recent first.</summary>
    public static List<InputSource> Sources()
    {
        var now = DateTime.Now;
        lock (Sync)
        {
            foreach (var device in Devices.Values)
                TrimResets(device, now);
            return Devices.Values.Where(d => d.Events > 0)
                .Select(d => new InputSource(d.Kind, d.Name, d.LastInput, d.Events, d.LastIdleReset, d.IdleResets, d.RecentResets.Count))
                .OrderByDescending(s => s.LastInput)
                .ToList();
        }
    }

    static void TrimResets(Device device, DateTime now)
    {
        while (device.RecentResets.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(1))
            device.RecentResets.Dequeue();
    }

    public static List<InputResumed> Drain()
    {
        var resumed = new List<InputResumed>();
        while (Pending.TryDequeue(out var r))
            resumed.Add(r);
        return resumed;
    }

    static void Run()
    {
        // A message-only window receives WM_INPUT; RIDEV_INPUTSINK delivers it even though it is never in the foreground.
        var hwnd = Native.CreateWindowEx(0, "STATIC", "WhyNotSleeping input", 0, 0, 0, 0, 0,
            Native.HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;

        var registered = 0;
        foreach (var (page, usage) in Usages)
        {
            // One call per usage, so a usage the system refuses doesn't take the others down with it.
            var device = new[] { new Native.RawInputDevice { UsagePage = page, Usage = usage, Flags = Native.RidevInputSink, Target = hwnd } };
            if (Native.RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<Native.RawInputDevice>()))
                registered++;
        }
        Available = registered > 0;

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == Native.WmInput)
                OnInput(msg.LParam, msg.Time);
            Native.DispatchMessage(ref msg); // DefWindowProc releases the raw input buffer
        }
    }

    static void OnInput(IntPtr rawInput, uint time)
    {
        var headerSize = (uint)Marshal.SizeOf<Native.RawInputHeader>();
        uint size = 0;
        Native.GetRawInputData(rawInput, Native.RidInput, IntPtr.Zero, ref size, headerSize);
        if (size < headerSize) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(rawInput, Native.RidInput, buffer, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<Native.RawInputHeader>(buffer);
            var data = buffer + (int)headerSize;
            byte[]? report = null;

            switch (header.Type)
            {
                case Native.RimTypeMouse:
                    // RAWMOUSE: usFlags, usButtonFlags @4 (buttons and wheel), lLastX @12, lLastY @16. Some mice send empty reports.
                    if (Marshal.ReadInt16(data, 4) == 0 && Marshal.ReadInt32(data, 12) == 0 && Marshal.ReadInt32(data, 16) == 0)
                        return;
                    break;
                case Native.RimTypeHid:
                    // RAWHID: dwSizeHid, dwCount, bRawData[dwSizeHid * dwCount]
                    var length = (long)Marshal.ReadInt32(data, 0) * Marshal.ReadInt32(data, 4);
                    if (length <= 0 || 8 + length > size - headerSize) return;
                    report = new byte[length];
                    Marshal.Copy(data + 8, report, 0, (int)length);
                    break;
            }
            Record(header.Device, header.Type, report, time);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static void Record(IntPtr handle, uint type, byte[]? report, uint time)
    {
        var now = DateTime.Now;
        lock (Sync)
        {
            if (!Devices.TryGetValue(handle, out var device))
                Devices[handle] = device = Identify(handle, type);

            if (report != null)
            {
                // Controllers stream their state continuously; only a changed report means someone touched it.
                var unchanged = device.LastReport is null || report.AsSpan().SequenceEqual(device.LastReport);
                device.LastReport = report;
                if (unchanged) return;
            }

            device.Events++;
            device.LastInput = now;

            // Windows stamps the last-input time with the time of the input that reset it, so the first input whose
            // message time matches a new last-input time is the one that reset the idle timer. The tick is ~16ms,
            // so a device sending in the same tick as another can occasionally be credited instead.
            var lii = new Native.LastInputInfo { cbSize = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
            if (!Native.GetLastInputInfo(ref lii) || lii.dwTime != time || lii.dwTime == _lastResetTick)
                return;
            _lastResetTick = lii.dwTime;
            device.IdleResets++;
            device.LastIdleReset = now;
            device.RecentResets.Enqueue(now);
            TrimResets(device, now);
            if (now - _lastReset >= QuietThreshold)
                Pending.Enqueue(new InputResumed(now, device.Kind, device.Name, now - _lastReset));
            _lastReset = now;
        }
    }

    static Device Identify(IntPtr handle, uint type)
    {
        if (handle == IntPtr.Zero)
            return new Device(InputKind.Software, "no physical device: injected input (remote desktop, automation, mouse-jiggler app...)");
        var kind = type switch
        {
            Native.RimTypeMouse => InputKind.Mouse,
            Native.RimTypeKeyboard => InputKind.Keyboard,
            _ => ClassifyHid(handle),
        };
        return new Device(kind, DescribeDevice(handle));
    }

    static InputKind ClassifyHid(IntPtr handle)
    {
        // RID_DEVICE_INFO: cbSize, dwType, then for HID: dwVendorId, dwProductId, dwVersionNumber, usUsagePage @20, usUsage @22
        var info = new byte[32];
        BitConverter.TryWriteBytes(info, info.Length);
        var size = (uint)info.Length;
        if (Native.GetRawInputDeviceInfo(handle, Native.RidiDeviceInfo, info, ref size) == uint.MaxValue)
            return InputKind.OtherHid;
        return (BitConverter.ToUInt16(info, 20), BitConverter.ToUInt16(info, 22)) switch
        {
            (0x01, 0x04 or 0x05 or 0x08) => InputKind.Controller,
            (0x01, 0x80) => InputKind.SystemButtons,
            (0x0C, _) => InputKind.MediaKeys,
            (0x0D, 0x02) => InputKind.Pen,
            (0x0D, 0x04) => InputKind.Touch,
            (0x0D, 0x05) => InputKind.Touchpad,
            _ => InputKind.OtherHid,
        };
    }

    /// <summary>"Logitech USB Receiver (VID 046D PID C52B)", falling back to the IDs or the device path.</summary>
    static string DescribeDevice(IntPtr handle)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfo(handle, Native.RidiDeviceName, null, ref chars);
        if (chars == 0) return $"device 0x{handle:X}";
        var buffer = new byte[chars * 2];
        if (Native.GetRawInputDeviceInfo(handle, Native.RidiDeviceName, buffer, ref chars) == uint.MaxValue)
            return $"device 0x{handle:X}";
        var path = Encoding.Unicode.GetString(buffer).TrimEnd('\0');

        var product = HidString(path);
        var match = VidPid().Match(path);
        // Bluetooth paths carry an 8-digit vendor ID with a source prefix (VID&0002046d); the last 4 digits are the vendor.
        var ids = match.Success ? $"VID {match.Groups[1].Value[^4..].ToUpperInvariant()} PID {match.Groups[2].Value.ToUpperInvariant()}" : null;
        return (product, ids) switch
        {
            (not null, not null) => $"{product} ({ids})",
            (not null, null) => product,
            (null, not null) => ids,
            // Non-HID devices such as PS/2 keyboards: \\?\ACPI#PNP0303#... -> ACPI\PNP0303
            _ => string.Join('\\', path.TrimStart('\\', '?').Split('#').Take(2)),
        };
    }

    static string? HidString(string path)
    {
        // Zero access rights: enough to query strings, even for mice and keyboards the system holds open exclusively.
        using var file = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (file.IsInvalid) return null;
        var buffer = new byte[256];
        foreach (var read in new Func<SafeFileHandle, byte[], uint, bool>[] { Native.HidD_GetProductString, Native.HidD_GetManufacturerString })
        {
            Array.Clear(buffer);
            if (!read(file, buffer, (uint)buffer.Length)) continue;
            var text = Encoding.Unicode.GetString(buffer);
            text = text[..(text.IndexOf('\0') is var end and >= 0 ? end : text.Length)].Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }

    [GeneratedRegex(@"VID[_&]([0-9A-F]{4,8})[_&]PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();
}
