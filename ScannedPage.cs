using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanDesk;

public sealed record PageImage(string FilePath, int PixelWidth, int PixelHeight, BitmapSource Thumbnail);

public sealed class ScannedPage : INotifyPropertyChanged
{
    string _filePath = "";
    BitmapSource? _thumbnail;
    int _number;
    int _pixelWidth, _pixelHeight;

    public string FilePath { get => _filePath; set => Set(ref _filePath, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public int Number { get => _number; set => Set(ref _number, value); }
    public int PixelWidth { get => _pixelWidth; set { Set(ref _pixelWidth, value); OnChanged(nameof(SizeText)); } }
    public int PixelHeight { get => _pixelHeight; set { Set(ref _pixelHeight, value); OnChanged(nameof(SizeText)); } }
    public double Dpi { get; set; }
    public bool IsGray { get; set; }

    public string SizeText => $"{PixelWidth} × {PixelHeight} px · {Dpi:0} dpi";

    /// <summary>Earlier versions of this page's image (before crops and rotations), newest on top.</summary>
    readonly Stack<PageImage> _history = new();
    public bool CanUndo => _history.Count > 0;

    /// <summary>Replaces the page image, keeping the current one so it can be restored with <see cref="Undo"/>.</summary>
    public void Apply(PageImage image)
    {
        _history.Push(Current);
        SetImage(image);
        OnChanged(nameof(CanUndo));
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        var discarded = FilePath;
        SetImage(_history.Pop());
        PageStore.TryDelete(discarded);
        OnChanged(nameof(CanUndo));
    }

    /// <summary>All image files this page owns, including undo history.</summary>
    public IEnumerable<string> AllFiles() => _history.Select(h => h.FilePath).Append(FilePath);

    PageImage Current => new(FilePath, PixelWidth, PixelHeight, Thumbnail!);

    void SetImage(PageImage image)
    {
        FilePath = image.FilePath;
        PixelWidth = image.PixelWidth;
        PixelHeight = image.PixelHeight;
        Thumbnail = image.Thumbnail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }
}

/// <summary>Image file handling for pages: loading, importing, rotating and exporting.</summary>
public static class PageStore
{
    public const int ThumbnailWidth = 160;

    static readonly string SessionsRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanDesk", "Sessions");

    /// <summary>Temporary folder for this window's scans; each running instance gets its own.</summary>
    public static string SessionFolder { get; } = Path.Combine(SessionsRoot, Guid.NewGuid().ToString("N"));

    public static void StartSession()
    {
        // Remove leftovers from sessions that crashed more than a day ago.
        try
        {
            if (Directory.Exists(SessionsRoot))
                foreach (var dir in Directory.GetDirectories(SessionsRoot))
                    if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-1))
                        try { Directory.Delete(dir, true); } catch { }
        }
        catch { }
        Directory.CreateDirectory(SessionFolder);
    }

    public static void EndSession()
    {
        try { Directory.Delete(SessionFolder, true); } catch { }
    }

    public static BitmapSource Load(string path, int decodeWidth = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    static (int w, int h) ReadSize(string path)
    {
        using var fs = File.OpenRead(path);
        var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    public static ScannedPage FromScan(ScanResult scan, ColorMode mode, EnhanceOptions enhance)
    {
        bool gray = mode != ColorMode.Color;
        string path = scan.FilePath;
        int w, h;
        if (enhance.Mode != EnhanceMode.Off || enhance.Brightness != 0 || enhance.Contrast != 0 || enhance.Rotation != 0 || enhance.Deskew)
        {
            var processed = ImageEnhancer.Process(Load(scan.FilePath), enhance, scan.Dpi, gray);
            path = Path.Combine(SessionFolder, $"page_{Guid.NewGuid():N}.bmp");
            var enc = new BmpBitmapEncoder(); // fast to write; pages are compressed when saved
            enc.Frames.Add(BitmapFrame.Create(processed));
            using (var fs = File.Create(path)) enc.Save(fs);
            TryDelete(scan.FilePath);
            (w, h) = (processed.PixelWidth, processed.PixelHeight);
        }
        else (w, h) = ReadSize(path);

        return new ScannedPage
        {
            FilePath = path,
            Dpi = scan.Dpi,
            IsGray = gray,
            PixelWidth = w,
            PixelHeight = h,
            Thumbnail = Load(path, ThumbnailWidth),
        };
    }

    /// <summary>Imports an image file from disk. Multi-page TIFFs become several pages.</summary>
    public static List<ScannedPage> Import(string sourcePath)
    {
        var result = new List<ScannedPage>();
        BitmapDecoder decoder;
        using (var fs = File.OpenRead(sourcePath))
            decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        foreach (var frame in decoder.Frames)
        {
            string dest = Path.Combine(SessionFolder, $"import_{Guid.NewGuid():N}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            using (var outFs = File.Create(dest)) enc.Save(outFs);

            double dpi = frame.DpiX;
            double longestPx = Math.Max(frame.PixelWidth, frame.PixelHeight);
            // Photos usually carry 72/96 dpi or none at all; size them to fit an A4 page instead.
            if (dpi < 50 || longestPx / dpi > 17) dpi = longestPx / 11.69;

            result.Add(new ScannedPage
            {
                FilePath = dest,
                Dpi = dpi,
                IsGray = IsGrayFormat(frame.Format),
                PixelWidth = frame.PixelWidth,
                PixelHeight = frame.PixelHeight,
                Thumbnail = Load(dest, ThumbnailWidth),
            });
        }
        return result;
    }

    static bool IsGrayFormat(PixelFormat f) =>
        f == PixelFormats.Gray2 || f == PixelFormats.Gray4 || f == PixelFormats.Gray8 ||
        f == PixelFormats.Gray16 || f == PixelFormats.Gray32Float || f == PixelFormats.BlackWhite;

    /// <summary>Rotates a page image by 90 / -90 / 180 degrees into a new file (the original is kept for undo).</summary>
    public static PageImage Rotate(string path, int degrees) =>
        SaveEdit(new TransformedBitmap(Load(path), new RotateTransform((degrees + 360) % 360)), "rot");

    /// <summary>Cuts out a rectangle (in image pixels) into a new file (the original is kept for undo).</summary>
    public static PageImage Crop(string path, System.Windows.Int32Rect rect) =>
        SaveEdit(new CroppedBitmap(Load(path), rect), "crop");

    static PageImage SaveEdit(BitmapSource image, string prefix)
    {
        string dest = Path.Combine(SessionFolder, $"{prefix}_{Guid.NewGuid():N}.bmp");
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using (var fs = File.Create(dest)) enc.Save(fs);
        return new PageImage(dest, image.PixelWidth, image.PixelHeight, Load(dest, ThumbnailWidth));
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static BitmapSource Prepare(ScannedPage page) =>
        new FormatConvertedBitmap(Load(page.FilePath), page.IsGray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);

    static byte[] EncodeJpeg(BitmapSource src, int quality)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static void ExportPdf(IReadOnlyList<ScannedPage> pages, string path, int quality, IProgress<int>? progress)
    {
        PdfWriter.Write(path, pages.Count, i =>
        {
            progress?.Report(i + 1);
            var page = pages[i];
            var src = Prepare(page);
            return new PdfImagePage(EncodeJpeg(src, quality), src.PixelWidth, src.PixelHeight, page.IsGray,
                src.PixelWidth / page.Dpi * 72.0, src.PixelHeight / page.Dpi * 72.0);
        }, Path.GetFileNameWithoutExtension(path));
    }

    public static void ExportTiff(IReadOnlyList<ScannedPage> pages, string path, IProgress<int>? progress)
    {
        var enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        for (int i = 0; i < pages.Count; i++)
        {
            progress?.Report(i + 1);
            enc.Frames.Add(BitmapFrame.Create(WithDpi(Prepare(pages[i]), pages[i].Dpi)));
        }
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Saves one file per page. Returns the files written.</summary>
    public static List<string> ExportImages(IReadOnlyList<ScannedPage> pages, IReadOnlyList<string> paths, int quality, IProgress<int>? progress)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            progress?.Report(i + 1);
            var src = WithDpi(Prepare(pages[i]), pages[i].Dpi);
            BitmapEncoder enc = Path.GetExtension(paths[i]).ToLowerInvariant() switch
            {
                ".png" => new PngBitmapEncoder(),
                ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
                _ => new JpegBitmapEncoder { QualityLevel = quality },
            };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(paths[i]);
            enc.Save(fs);
        }
        return paths.ToList();
    }

    static BitmapSource WithDpi(BitmapSource src, double dpi)
    {
        if (Math.Abs(src.DpiX - dpi) < 0.5 && Math.Abs(src.DpiY - dpi) < 0.5) return src;
        int stride = (src.PixelWidth * src.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * src.PixelHeight];
        src.CopyPixels(pixels, stride, 0);
        return BitmapSource.Create(src.PixelWidth, src.PixelHeight, dpi, dpi, src.Format, src.Palette, pixels, stride);
    }
}
