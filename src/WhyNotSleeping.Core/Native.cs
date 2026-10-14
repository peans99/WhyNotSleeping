using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WhyNotSleeping;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceNotifySubscribeParameters
    {
        public DeviceNotifyCallback Callback;
        public IntPtr Context;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    public const int SystemExecutionState = 16;
    public const uint DeviceNotifyCallbackFlag = 2;
    public const uint PbtPowerSettingChange = 0x8013;

    public static readonly IntPtr HwndMessage = new(-3);
    public const uint WmInput = 0x00FF;
    public const uint RidevInputSink = 0x00000100;
    public const uint RidInput = 0x10000003;
    public const uint RidiDeviceName = 0x20000007;
    public const uint RidiDeviceInfo = 0x2000000b;
    public const uint RimTypeMouse = 0;
    public const uint RimTypeKeyboard = 1;
    public const uint RimTypeHid = 2;

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[]? data, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, uint length);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle device, byte[] buffer, uint length);

    public const uint RidiPreparsedData = 0x20000005;
    public const int HidpStatusSuccess = 0x00110000;
    public const int HidpInput = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct UsageAndPage
    {
        public ushort Usage;
        public ushort UsagePage;
    }

    // The preparsed data is an opaque blob from GetRawInputDeviceInfo(RIDI_PREPARSEDDATA); a pinned byte[] stands in for the pointer.
    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(byte[] preparsed, byte[] caps);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, byte[] preparsed);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, byte[] preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsagesEx(int reportType, ushort linkCollection, [Out] UsageAndPage[] usages,
        ref uint length, byte[] preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, byte[] preparsed);

    public const uint CrSuccess = 0;
    public const uint CrNoSuchDevnode = 0x0D;
    public const uint CrRemoveVetoed = 0x17;
    public const uint CrAccessDenied = 0x33;
    public const uint CmLocateDevnodePhantom = 1;
    public const uint CmDisablePersist = 8;
    public const uint DnHasProblem = 0x400;
    public const uint CmProbDisabled = 0x16;

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Enable_DevNode(uint devInst, uint flags);

    public const uint CmDrpPhysicalDeviceObjectName = 0x0F;

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    public static extern uint CM_Get_DevNode_Registry_Property(uint devInst, uint property, out uint type, byte[] buffer, ref uint length, uint flags);

    public const int SystemExtendedHandleInformation = 64;
    public const uint StatusInfoLengthMismatch = 0xC0000004;
    public const int ObjectNameInformation = 1;
    public const uint ProcessDupHandle = 0x40;
    public const uint DuplicateSameAccess = 2;
    public const uint FileTypePipe = 3;

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle,
        uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    public static extern uint GetFileType(IntPtr file);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    public const uint CmRemoveUiNotOk = 1;
    public const uint CmRemoveNoRestart = 2;

    /// <summary>Like a safe removal; when something refuses, says who (vetoType is a PNP_VETO_TYPE).</summary>
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Query_And_Remove_SubTreeW", CharSet = CharSet.Unicode)]
    public static extern uint CM_Query_And_Remove_SubTree(uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetKeyNameTextW", CharSet = CharSet.Unicode)]
    public static extern int GetKeyNameText(int lParam, StringBuilder buffer, int size);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags,
        ref DeviceNotifySubscribeParameters recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    public static extern int CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength,
        out uint outputBuffer, uint outputBufferLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maxChars);

    public const int QunsBusy = 2;
    public const int QunsRunningD3dFullScreen = 3;
    public const int QunsPresentationMode = 4;

    /// <summary>Whether a full-screen app or a presentation is running (QUERY_USER_NOTIFICATION_STATE).</summary>
    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    public static readonly IntPtr HwndBroadcast = new(0xFFFF);
    public const uint WmSysCommand = 0x0112;
    public const int ScMonitorPower = 0xF170;
    public const uint MsgfltAllow = 1;

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    /// <summary>Lets a message through to an elevated window from a non-elevated process (UIPI).</summary>
    [DllImport("user32.dll")]
    public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);
}
