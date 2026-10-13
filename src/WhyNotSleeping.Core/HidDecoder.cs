namespace WhyNotSleeping;

/// <summary>One control that moved in a report: "Rz axis" went from 510 to 513, or "button 5" was pressed.</summary>
internal sealed record ControlChange(string Control, long Value, bool IsAxis, long LogicalMin, long LogicalMax)
{
    public long? Previous { get; init; }

    public override string ToString() => IsAxis
        ? $"{Control} {Previous} → {Value}"
        : $"{Control} {(Value != 0 ? "pressed" : "released")}";
}

/// <summary>
/// Decodes a HID device's input reports into named controls with Windows' HID parser, so the app can say which axis
/// or button of a joystick, throttle or pedal set is sending input, and whether it is just jitter.
/// </summary>
internal sealed class HidDecoder
{
    sealed class Value(byte reportId, ushort page, ushort link, ushort usage, int bitSize, long logicalMin, long logicalMax)
    {
        public readonly byte ReportId = reportId;
        public readonly ushort Page = page, Link = link, Usage = usage;
        public readonly int BitSize = bitSize;
        public readonly long LogicalMin = logicalMin, LogicalMax = logicalMax;
        public readonly string Name = ControlName(page, usage);
        public long? Last;
    }

    readonly byte[] _preparsed;
    readonly List<Value> _values;
    readonly Native.UsageAndPage[] _usages;
    HashSet<(ushort Page, ushort Usage)>? _pressed;

    HidDecoder(byte[] preparsed, List<Value> values, uint maxUsages)
    {
        _preparsed = preparsed;
        _values = values;
        _usages = new Native.UsageAndPage[Math.Max(maxUsages, 1)];
    }

    /// <summary>Null when Windows won't hand out the device's report descriptor.</summary>
    public static HidDecoder? Create(IntPtr device)
    {
        uint size = 0;
        Native.GetRawInputDeviceInfo(device, Native.RidiPreparsedData, null, ref size);
        if (size == 0) return null;
        var preparsed = new byte[size];
        if (Native.GetRawInputDeviceInfo(device, Native.RidiPreparsedData, preparsed, ref size) == uint.MaxValue)
            return null;

        // HIDP_CAPS: ..., NumberInputValueCaps @48
        var caps = new byte[64];
        if (Native.HidP_GetCaps(preparsed, caps) != Native.HidpStatusSuccess)
            return null;
        var count = BitConverter.ToUInt16(caps, 48);

        var values = new List<Value>();
        if (count > 0)
        {
            // HIDP_VALUE_CAPS is 72 bytes: UsagePage @0, ReportID @2, LinkCollection @6, IsRange @12, BitSize @18,
            // ReportCount @20, LogicalMin @40, LogicalMax @44, Usage or UsageMin @56, UsageMax @58.
            var buffer = new byte[72 * count];
            if (Native.HidP_GetValueCaps(Native.HidpInput, buffer, ref count, preparsed) != Native.HidpStatusSuccess)
                return null;
            for (var i = 0; i < count; i++)
            {
                var c = buffer.AsSpan(72 * i, 72);
                var isRange = c[12] != 0;
                var reportCount = BitConverter.ToUInt16(c[20..]);
                if (!isRange && reportCount > 1) continue; // value arrays (rare on controllers) need another API
                var page = BitConverter.ToUInt16(c);
                var first = BitConverter.ToUInt16(c[56..]);
                var last = isRange ? BitConverter.ToUInt16(c[58..]) : first;
                for (var usage = first; usage <= last && usage >= first; usage++)
                    values.Add(new Value(c[2], page, BitConverter.ToUInt16(c[6..]), usage, BitConverter.ToUInt16(c[18..]),
                        BitConverter.ToInt32(c[40..]), BitConverter.ToInt32(c[44..])));
            }
        }
        return new HidDecoder(preparsed, values, Native.HidP_MaxUsageListLength(Native.HidpInput, 0, preparsed));
    }

    /// <summary>What changed since the previous report. The first report only sets the baseline.</summary>
    public List<ControlChange> Decode(byte[] report)
    {
        var changes = new List<ControlChange>();
        foreach (var v in _values)
        {
            // Reports with another report ID don't carry this control; the parser says so and it's skipped.
            if (Native.HidP_GetUsageValue(Native.HidpInput, v.Page, v.Link, v.Usage, out var raw, _preparsed, report, (uint)report.Length)
                != Native.HidpStatusSuccess)
                continue;
            long value = raw;
            if (v.LogicalMin < 0 && v.BitSize is > 0 and < 32 && (raw & (1u << (v.BitSize - 1))) != 0)
                value -= 1L << v.BitSize; // sign-extend
            if (v.Last is { } previous && previous != value)
                changes.Add(new ControlChange(v.Name, value, true, v.LogicalMin, v.LogicalMax) { Previous = previous });
            v.Last = value;
        }

        var length = (uint)_usages.Length;
        if (Native.HidP_GetUsagesEx(Native.HidpInput, 0, _usages, ref length, _preparsed, report, (uint)report.Length) == Native.HidpStatusSuccess)
        {
            var pressed = _usages.Take((int)length).Select(u => (Page: u.UsagePage, u.Usage)).ToHashSet();
            if (_pressed != null)
            {
                changes.AddRange(pressed.Except(_pressed).Select(b => new ControlChange(ControlName(b.Page, b.Usage), 1, false, 0, 1)));
                changes.AddRange(_pressed.Except(pressed).Select(b => new ControlChange(ControlName(b.Page, b.Usage), 0, false, 0, 1)));
            }
            _pressed = pressed;
        }
        return changes;
    }

    /// <summary>Every value control with its latest value, then the buttons held down right now.</summary>
    public IEnumerable<(string Name, bool IsAxis, long? Value, long LogicalMin, long LogicalMax)> Readings() =>
        _values.Select(v => (v.Name, true, v.Last, v.LogicalMin, v.LogicalMax))
            .Concat((_pressed ?? []).OrderBy(b => b.Page).ThenBy(b => b.Usage)
                .Select(b => (ControlName(b.Page, b.Usage), false, (long?)1, 0L, 1L)));

    static string ControlName(ushort page, ushort usage) => (page, usage) switch
    {
        (0x01, 0x30) => "X axis",
        (0x01, 0x31) => "Y axis",
        (0x01, 0x32) => "Z axis",
        (0x01, 0x33) => "Rx axis",
        (0x01, 0x34) => "Ry axis",
        (0x01, 0x35) => "Rz axis",
        (0x01, 0x36) => "slider",
        (0x01, 0x37) => "dial",
        (0x01, 0x38) => "wheel",
        (0x01, 0x39) => "hat switch",
        (0x02, 0xB0) => "aileron",
        (0x02, 0xB8) => "elevator",
        (0x02, 0xBA) => "rudder",
        (0x02, 0xBB) => "throttle",
        (0x02, 0xC4) => "accelerator",
        (0x02, 0xC5) => "brake",
        (0x02, 0xC6) => "clutch",
        (0x02, 0xC8) => "steering",
        (0x09, _) => $"button {usage}",
        (0x0C, 0xE9) => "volume up",
        (0x0C, 0xEA) => "volume down",
        (0x0C, 0xE2) => "mute",
        (0x0C, 0xCD) => "play/pause",
        // Outside the standard pages: devices put extra data here, e.g. raw sensor readings next to the real axes,
        // that games don't read but that still makes the device send reports.
        (0x00FF or >= 0xFF00, _) => $"vendor channel {usage}",
        _ => $"control {page:X2}:{usage:X2}",
    };
}
