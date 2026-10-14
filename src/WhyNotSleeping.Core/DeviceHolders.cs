using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WhyNotSleeping;

/// <summary>
/// Finds which programs have a device open, for when Windows refuses to turn it off and only says "an app has it
/// open". Walks the system handle table, as Process Explorer does, and matches each file handle's name against the
/// device's physical device object (\Device\00000123). Needs administrator rights to look inside other processes.
/// </summary>
internal static class DeviceHolders
{
    /// <summary>Process names such as "steam.exe", most handles first; empty when nothing could be found.</summary>
    public static List<string> Find(string instanceId)
    {
        // Only ever an extra hint on an error message, so it must never become an error itself.
        try { return FindUnchecked(instanceId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ExternalException) { return []; }
    }

    static List<string> FindUnchecked(string instanceId)
    {
        if (DeviceObjectName(instanceId) is not { } device) return [];
        var holders = new Dictionary<int, int>(); // process id -> handles on the device
        var processes = new Dictionary<int, IntPtr>();
        using var names = new NameQuery();
        try
        {
            foreach (var (pid, handle, typeIndex) in Handles())
            {
                if (pid == 4 || typeIndex != FileTypeIndex()) continue; // the System process holds kernel handles, not apps
                if (!processes.TryGetValue(pid, out var process))
                    processes[pid] = process = Native.OpenProcess(Native.ProcessDupHandle, false, pid);
                if (process == IntPtr.Zero) continue;
                if (!Native.DuplicateHandle(process, handle, Native.GetCurrentProcess(), out var copy, 0, false, Native.DuplicateSameAccess))
                    continue;
                try
                {
                    // Asking a pipe for its name can block forever; device handles are never pipes.
                    if (Native.GetFileType(copy) == Native.FileTypePipe) continue;
                    if (names.Get(copy) is { } name && (name == device || name.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)))
                        holders[pid] = holders.GetValueOrDefault(pid) + 1;
                }
                finally
                {
                    Native.CloseHandle(copy);
                }
            }
        }
        finally
        {
            foreach (var process in processes.Values.Where(p => p != IntPtr.Zero))
                Native.CloseHandle(process);
        }
        return holders.OrderByDescending(h => h.Value).Select(h => ProcessName(h.Key)).Distinct().ToList();
    }

    static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName + ".exe"; }
        catch (ArgumentException) { return $"process {pid}"; }
    }

    /// <summary>"\Device\00000123": the name file handles opened on the device carry.</summary>
    static string? DeviceObjectName(string instanceId)
    {
        if (Native.CM_Locate_DevNode(out var node, instanceId, 0) != Native.CrSuccess) return null;
        var buffer = new byte[512];
        var length = (uint)buffer.Length;
        if (Native.CM_Get_DevNode_Registry_Property(node, Native.CmDrpPhysicalDeviceObjectName, out _, buffer, ref length, 0) != Native.CrSuccess)
            return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0');
    }

    static int? _fileTypeIndex;

    /// <summary>The handle table's type number for files, learned from a handle of our own on NUL.</summary>
    static int FileTypeIndex()
    {
        if (_fileTypeIndex is { } known) return known;
        using var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read);
        var self = Environment.ProcessId;
        var value = nul.DangerousGetHandle();
        foreach (var (pid, handle, typeIndex) in Handles())
            if (pid == self && handle == value)
                return (int)(_fileTypeIndex = typeIndex);
        return -1;
    }

    /// <summary>SYSTEM_HANDLE_INFORMATION_EX: a count, a reserved word, then 40-byte entries on x64.</summary>
    static List<(int Pid, IntPtr Handle, int TypeIndex)> Handles()
    {
        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.NtQuerySystemInformation(Native.SystemExtendedHandleInformation, buffer, size, out var needed);
                if (status == Native.StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (1 << 16));
                    continue;
                }
                if (status != 0) return [];

                var count = (long)Marshal.ReadIntPtr(buffer);
                var entrySize = 2 * IntPtr.Size + IntPtr.Size + 16; // Object, UniqueProcessId, HandleValue, then 16 bytes
                var first = 2 * IntPtr.Size;
                var result = new List<(int, IntPtr, int)>((int)Math.Min(count, 1 << 22));
                for (long i = 0; i < count; i++)
                {
                    var entry = buffer + (nint)(first + i * entrySize);
                    var pid = (int)Marshal.ReadIntPtr(entry, IntPtr.Size);
                    var handle = Marshal.ReadIntPtr(entry, 2 * IntPtr.Size);
                    var typeIndex = (ushort)Marshal.ReadInt16(entry, 3 * IntPtr.Size + 6); // after GrantedAccess, CreatorBackTraceIndex
                    result.Add((pid, handle, typeIndex));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Asks for handle names on a helper thread, since a few kinds of handle never answer. A query that takes too long
    /// is abandoned with its thread, and a fresh thread takes over.
    /// </summary>
    sealed class NameQuery : IDisposable
    {
        BlockingCollection<(IntPtr Handle, TaskCompletionSource<string?> Result)>? _work;

        public string? Get(IntPtr handle)
        {
            if (_work is null)
            {
                var work = _work = new BlockingCollection<(IntPtr, TaskCompletionSource<string?>)>();
                new Thread(() =>
                {
                    // Each thread owns its buffer, so an abandoned one can't write into its successor's.
                    var buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        foreach (var (h, result) in work.GetConsumingEnumerable())
                            result.TrySetResult(Native.NtQueryObject(h, Native.ObjectNameInformation, buffer, 4096, out _) == 0
                                // OBJECT_NAME_INFORMATION is a UNICODE_STRING: Length (bytes), MaximumLength, then the pointer.
                                ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), Marshal.ReadInt16(buffer) / 2)
                                : null);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }) { IsBackground = true, Name = "handle names" }.Start();
            }

            var query = new TaskCompletionSource<string?>();
            _work.Add((handle, query));
            if (query.Task.Wait(TimeSpan.FromMilliseconds(200)))
                return query.Task.Result;
            // Stuck in the call: let that thread finish (or not) on its own, and start a fresh one next time.
            _work.CompleteAdding();
            _work = null;
            return null;
        }

        public void Dispose() => _work?.CompleteAdding();
    }
}
