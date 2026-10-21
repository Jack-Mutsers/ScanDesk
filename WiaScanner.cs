using System.IO;
using System.Runtime.InteropServices;

namespace ScanDesk;

public enum ColorMode { Color, Grayscale, BlackWhite }

public enum ScanArea { FullGlass, A4, Letter, A5, Photo10x15 }

public sealed record ScannerInfo(string DeviceId, string Name)
{
    public override string ToString() => Name;
}

public sealed record ScanSettings(ColorMode Color, int Dpi, ScanArea Area);

public sealed record ScanResult(string FilePath, double Dpi);

public sealed class ScanException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Talks to scanners through Windows Image Acquisition (WIA 2.0) using late-bound COM,
/// so it works with any scanner that has a WIA driver (Epson, Canon, HP, Brother, ...).
/// All methods must run on an STA thread; use <see cref="RunStaAsync{T}"/>.
/// </summary>
public static class WiaScanner
{
    const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    // WIA property ids
    const int DPS_DOCUMENT_HANDLING_SELECT = 3088;
    const int DPS_HORIZONTAL_BED_SIZE = 3074;
    const int DPS_VERTICAL_BED_SIZE = 3075;
    const int IPA_DATATYPE = 4103;
    const int IPS_CUR_INTENT = 6146;
    const int IPS_XRES = 6147;
    const int IPS_YRES = 6148;
    const int IPS_XPOS = 6149;
    const int IPS_YPOS = 6150;
    const int IPS_XEXTENT = 6151;
    const int IPS_YEXTENT = 6152;

    public static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    static dynamic CreateManager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new ScanException("Windows Image Acquisition (WIA) is not available on this PC.");
        return Activator.CreateInstance(type)!;
    }

    public static List<ScannerInfo> GetScanners()
    {
        var list = new List<ScannerInfo>();
        dynamic manager = CreateManager();
        try
        {
            dynamic infos = manager.DeviceInfos;
            int count = infos.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic info = infos[i];
                if ((int)info.Type != 1) continue; // 1 = scanner
                string id = info.DeviceID;
                string name = GetValue(info.Properties, "Name") as string ?? id;
                list.Add(new ScannerInfo(id, name));
            }
        }
        finally { Release(manager); }
        return list;
    }

    public static ScanResult Scan(string deviceId, ScanSettings settings, string outputFolder)
    {
        dynamic manager = CreateManager();
        dynamic? device = null;
        try
        {
            dynamic infos = manager.DeviceInfos;
            int count = infos.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic info = infos[i];
                if ((string)info.DeviceID == deviceId)
                {
                    try { device = info.Connect(); }
                    catch (COMException ex) { throw Translate(ex); }
                    break;
                }
            }
            if (device == null)
                throw new ScanException("The scanner could not be found. Make sure the printer is switched on, then press Refresh.");

            dynamic item = device.Items[1];

            SetValue(device.Properties, DPS_DOCUMENT_HANDLING_SELECT, 2); // flatbed

            var (intent, dataType) = settings.Color switch
            {
                ColorMode.Grayscale => (2, 2),
                ColorMode.BlackWhite => (4, 0),
                _ => (1, 3),
            };
            // Intent first: drivers reset other properties when it changes.
            SetValue(item.Properties, IPS_CUR_INTENT, intent);
            SetValue(item.Properties, IPA_DATATYPE, dataType);
            SetValue(item.Properties, IPS_XRES, settings.Dpi);
            SetValue(item.Properties, IPS_YRES, settings.Dpi);

            double dpi = Convert.ToDouble(GetValue(item.Properties, IPS_XRES) ?? settings.Dpi);

            SetValue(item.Properties, IPS_XPOS, 0);
            SetValue(item.Properties, IPS_YPOS, 0);

            int maxW = GetMax(item.Properties, IPS_XEXTENT) ?? BedPixels(device.Properties, DPS_HORIZONTAL_BED_SIZE, dpi) ?? 0;
            int maxH = GetMax(item.Properties, IPS_YEXTENT) ?? BedPixels(device.Properties, DPS_VERTICAL_BED_SIZE, dpi) ?? 0;
            var (inW, inH) = AreaInches(settings.Area);
            int w = inW > 0 ? (int)Math.Round(inW * dpi) : maxW;
            int h = inH > 0 ? (int)Math.Round(inH * dpi) : maxH;
            if (maxW > 0) w = Math.Min(w, maxW);
            if (maxH > 0) h = Math.Min(h, maxH);
            if (w > 0) SetValue(item.Properties, IPS_XEXTENT, w);
            if (h > 0) SetValue(item.Properties, IPS_YEXTENT, h);

            dynamic image;
            try
            {
                try { image = item.Transfer(FormatBmp); }
                catch (COMException ex) when (!IsDeviceError(ex)) { image = item.Transfer(); }
            }
            catch (COMException ex) { throw Translate(ex); }

            string ext = image.FileExtension;
            if (string.IsNullOrWhiteSpace(ext)) ext = "bmp";
            Directory.CreateDirectory(outputFolder);
            string path = Path.Combine(outputFolder, $"scan_{Guid.NewGuid():N}.{ext}");
            image.SaveFile(path);
            Release(image);
            return new ScanResult(path, dpi);
        }
        finally
        {
            if (device != null) Release(device);
            Release(manager);
        }
    }

    static (double w, double h) AreaInches(ScanArea area) => area switch
    {
        ScanArea.A4 => (210 / 25.4, 297 / 25.4),
        ScanArea.Letter => (8.5, 11),
        ScanArea.A5 => (148 / 25.4, 210 / 25.4),
        ScanArea.Photo10x15 => (100 / 25.4, 150 / 25.4),
        _ => (0, 0),
    };

    static int? BedPixels(dynamic props, int id, double dpi)
    {
        var v = GetValue(props, id);
        return v == null ? null : (int)(Convert.ToDouble(v) / 1000.0 * dpi); // thousandths of an inch
    }

    static dynamic? Find(dynamic props, int id)
    {
        int count = props.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic p = props[i];
            if ((int)p.PropertyID == id) return p;
        }
        return null;
    }

    static object? GetValue(dynamic props, string name)
    {
        try
        {
            int count = props.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic p = props[i];
                if ((string)p.Name == name) return p.Value;
            }
        }
        catch (COMException) { }
        return null;
    }

    static object? GetValue(dynamic props, int id)
    {
        try { return Find(props, id)?.Value; }
        catch (COMException) { return null; }
    }

    static int? GetMax(dynamic props, int id)
    {
        try
        {
            dynamic? p = Find(props, id);
            if (p != null && (int)p.SubType == 1) return (int)p.SubTypeMax; // range
        }
        catch (COMException) { }
        return null;
    }

    /// <summary>Sets a property, snapping to the nearest allowed value if the driver rejects it.</summary>
    static void SetValue(dynamic props, int id, int value)
    {
        dynamic? p;
        try { p = Find(props, id); } catch (COMException) { return; }
        if (p == null) return;
        try { p.Value = value; return; }
        catch (COMException) { }
        try
        {
            int subType = p.SubType;
            int snapped = value;
            if (subType == 1) // range
            {
                int min = p.SubTypeMin, max = p.SubTypeMax, step = Math.Max(1, (int)p.SubTypeStep);
                snapped = Math.Clamp(min + (int)Math.Round((value - min) / (double)step) * step, min, max);
            }
            else if (subType == 2) // list
            {
                dynamic values = p.SubTypeValues;
                int n = values.Count;
                int best = int.MaxValue;
                for (int i = 1; i <= n; i++)
                {
                    int v = Convert.ToInt32(values[i]);
                    if (Math.Abs(v - value) < Math.Abs(best - value)) best = v;
                }
                if (best != int.MaxValue) snapped = best;
            }
            if (snapped != value) p.Value = snapped;
        }
        catch (COMException) { /* driver doesn't support it; keep its default */ }
    }

    static bool IsDeviceError(COMException ex) => (uint)ex.HResult is >= 0x80210001 and <= 0x8021FFFF;

    static ScanException Translate(COMException ex) => (uint)ex.HResult switch
    {
        0x80210006 => new ScanException("The scanner is busy. Wait a moment and try again.", ex),
        0x80210005 or 0x8021000A or 0x80210015 =>
            new ScanException("Can't reach the scanner. Check that the printer is on and connected (USB or Wi-Fi), then press Refresh.", ex),
        0x80210016 => new ScanException("The scanner lid seems to be open. Close it and try again.", ex),
        0x80210003 => new ScanException("There is no paper in the document feeder.", ex),
        0x80210002 => new ScanException("The paper is jammed in the scanner.", ex),
        0x80210007 => new ScanException("The scanner is still warming up. Try again in a few seconds.", ex),
        0x8021000D => new ScanException("The scanner is in use by another program. Close it and try again.", ex),
        _ => new ScanException($"The scanner reported an error (0x{(uint)ex.HResult:X8}): {ex.Message}", ex),
    };

    static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }
}
