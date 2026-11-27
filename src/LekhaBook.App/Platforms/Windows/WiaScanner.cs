using System.Runtime.InteropServices;

namespace LekhaBook.App.Services;

/// <summary>
/// Windows Image Acquisition (WIA) through its automation objects, so no COM interop package is needed. WIA lists the
/// scanners a PC can use, including the scan function of a printer that scans, and its common dialog lets the user
/// choose the device and scan one page. WIA scanners need a driver; without one, the device isn't listed.
/// </summary>
internal static class WiaScanner
{
    private const int ScannerDeviceType = 1;           // WiaDeviceType: Scanner (1)
    private const int ColorIntent = 1;                  // WiaImageIntent: Color (1)
    private const string JpegFormatId = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    /// <summary>True when WIA lists at least one scanner.</summary>
    public static bool HasScanner()
    {
        try
        {
            dynamic manager = CreateComObject("WIA.DeviceManager");
            dynamic devices = manager.DeviceInfos;
            int count = devices.Count;
            for (var i = 1; i <= count; i++) // WIA collections are 1-based
            {
                dynamic device = devices.Item(i);
                if ((int)device.Type == ScannerDeviceType)
                {
                    return true;
                }
            }

            return false;
        }
        catch (COMException)
        {
            // The WIA service isn't running, or no device is installed.
            return false;
        }
    }

    /// <summary>Shows WIA's scan dialog and returns the scanned page as JPEG, or null when the user cancels.</summary>
    public static byte[]? ScanPage()
    {
        dynamic dialog = CreateComObject("WIA.CommonDialog");
        // ShowAcquireImage(DeviceType, Intent, Bias, FormatID, AlwaysSelectDevice, UseCommonUI, CancelError)
        dynamic image = dialog.ShowAcquireImage(ScannerDeviceType, ColorIntent, 0, JpegFormatId, false, true, false);
        if (image == null)
        {
            return null;
        }

        return (byte[])image.FileData.BinaryData;
    }

    private static object CreateComObject(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;
}
