using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ufo.Device;

/// <summary>Raw report access to the dongle's vendor-defined HID collection (MI_05, COL02).</summary>
internal sealed class HidChannel : IDisposable
{
    private const string VendorCollectionTag = "vid_413c&pid_a529&mi_05&col02";
    private static readonly Guid HidInterfaceClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    private readonly SafeFileHandle handle;

    private HidChannel(SafeFileHandle handle) => this.handle = handle;

    /// <summary>Opens the dongle, or returns null when it is not plugged in.</summary>
    public static HidChannel? Open()
    {
        string? path = FindDevicePath();
        if (path is null)
        {
            return null;
        }
        SafeFileHandle opened = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (opened.IsInvalid)
        {
            opened.Dispose();
            return null;
        }
        return new HidChannel(opened);
    }

    public bool Send(byte[] report) => HidD_SetOutputReport(handle, report, report.Length);

    public bool Receive(byte[] report) => HidD_GetInputReport(handle, report, report.Length);

    public void Dispose() => handle.Dispose();

    private static string? FindDevicePath()
    {
        Guid interfaceClass = HidInterfaceClass;
        if (CM_Get_Device_Interface_List_SizeW(out uint size, ref interfaceClass, null, InterfaceListPresent) != 0 || size <= 1)
        {
            return null;
        }
        char[] buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, size, InterfaceListPresent) != 0)
        {
            return null;
        }
        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(path => path.Contains(VendorCollectionTag, StringComparison.OrdinalIgnoreCase));
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint InterfaceListPresent = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] report, int length);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] report, int length);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, uint length, uint flags);
}
