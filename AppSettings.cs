using System.IO;
using System.Text.Json;

namespace ScanDesk;

public sealed class AppSettings
{
    public string Language { get; set; } = Loc.DefaultLanguage;
    public string? ScannerId { get; set; }          // last used
    public string? DefaultScannerId { get; set; }
    public List<string> HiddenScannerIds { get; set; } = [];
    public ColorMode Color { get; set; } = ColorMode.Color;
    public int Dpi { get; set; } = 300;
    public ScanArea Area { get; set; } = ScanArea.A4;
    public int Quality { get; set; } = 85;
    public EnhanceMode Enhance { get; set; } = EnhanceMode.Document;
    public int Rotation { get; set; }
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public bool Deskew { get; set; } = true;
    public int SaveFormatIndex { get; set; } = 1;
    public string? LastFolder { get; set; }

    static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScanDesk", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
