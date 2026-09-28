using System.Runtime.InteropServices;

namespace WallhackTerminal.Hid;

public sealed record HidInterfaceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    string? ProductName);

public static unsafe class HidEnumerator
{
    public static List<HidInterfaceInfo> Enumerate(ushort vendorId, ushort productId)
    {
        var result = new List<HidInterfaceInfo>();
        Native.HidD_GetHidGuid(out var hidGuid);
        IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == Native.INVALID_HANDLE_VALUE) return result;

        try
        {
            var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            string vidTag = $"vid_{vendorId:x4}";
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                string? path = GetPath(set, ref ifData);
                if (path is null) continue;
                if (path.Contains("vid_", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains(vidTag, StringComparison.OrdinalIgnoreCase)) continue;

                var info = Query(path);
                if (info is not null && info.VendorId == vendorId && info.ProductId == productId)
                    result.Add(info);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    static string? GetPath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
    {
        Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, buffer, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static HidInterfaceInfo? Query(string path)
    {
        using var handle = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var attributes = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes)) return null;

        Native.HIDP_CAPS caps = default;
        if (Native.HidD_GetPreparsedData(handle, out var preparsed))
        {
            try { Native.HidP_GetCaps(preparsed, out caps); }
            finally { Native.HidD_FreePreparsedData(preparsed); }
        }

        string? product = null;
        char* chars = stackalloc char[128];
        if (Native.HidD_GetProductString(handle, chars, 127 * sizeof(char)))
        {
            chars[127] = '\0';
            product = new string(chars).Trim();
        }

        return new HidInterfaceInfo(path, attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage,
            caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
            string.IsNullOrEmpty(product) ? null : product);
    }
}
