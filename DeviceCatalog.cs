using System.Printing;
using System.Text.RegularExpressions;

namespace ScanDesk;

/// <summary>A scanner or printer whose brand was recognised, with links to the manufacturer's official support pages.</summary>
public sealed record DeviceSupport(string Brand, string Model, string SupportUrl, string SearchUrl, string Domain);

/// <summary>A printer installed in Windows whose scanner ScanDesk can't see (usually missing scan software).</summary>
public sealed record PrinterInfo(string Name, DeviceSupport Support);

/// <summary>
/// Recognises the brand and model of scanners and printers from the names Windows reports, and links to the
/// manufacturer's official driver/software pages. Only official sites are linked, never third-party driver sites.
/// </summary>
public static partial class DeviceCatalog
{
    // Pattern recognises the brand (including product lines); Company is the part removed from the model name.
    sealed record Brand(string Name, Regex Pattern, string UrlNl, string UrlEn, string Domain, Regex Company);

    static Regex Words(string pattern) => new($@"\b(?:{pattern})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Order matters: Fujitsu/PFU scanners before Ricoh printers; Samsung printers are supported by HP.
    static Brand B(string name, string pattern, string company, string urlNl, string urlEn, string domain) =>
        new(name, Words(pattern), urlNl, urlEn, domain, Words(company));

    // Order matters: Fujitsu/PFU scanners before Ricoh printers; Samsung printers are supported by HP.
    static readonly Brand[] Brands =
    [
        B("Epson", "epson", "epson",
          "https://www.epson.nl/nl_NL/support", "https://www.epson.eu/en_EU/support", "epson.eu"),
        B("HP", @"hp\b|hewlett|deskjet|officejet|laserjet|envy|smart ?tank|samsung", @"hp\b|hewlett[- ]?packard",
          "https://support.hp.com/nl-nl/drivers", "https://support.hp.com/us-en/drivers", "support.hp.com"),
        B("Canon", "canon|pixma|i-sensys|imageclass|canoscan|maxify", "canon",
          "https://www.canon.nl/support/", "https://www.canon-europe.com/support/", "canon-europe.com"),
        B("Brother", "brother", "brother",
          "https://support.brother.com/g/b/countrytop.aspx?c=nl&lang=nl", "https://support.brother.com/", "support.brother.com"),
        B("Fujitsu / Ricoh (ScanSnap)", @"fujitsu|scansnap|pfu|fi-\d", "fujitsu|pfu",
          "https://www.pfu.ricoh.com/global/scanners/", "https://www.pfu.ricoh.com/global/scanners/", "pfu.ricoh.com"),
        B("Ricoh", "ricoh", "ricoh",
          "https://support.ricoh.com/", "https://support.ricoh.com/", "ricoh.com"),
        B("Kyocera", "kyocera|ecosys|taskalfa", "kyocera",
          "https://www.kyoceradocumentsolutions.eu/en/support/downloads.html",
          "https://www.kyoceradocumentsolutions.eu/en/support/downloads.html", "kyoceradocumentsolutions.eu"),
        B("Xerox", "xerox|versalink|altalink|workcentre", "xerox",
          "https://www.support.xerox.com/", "https://www.support.xerox.com/", "xerox.com"),
        B("Lexmark", "lexmark", "lexmark",
          "https://support.lexmark.com/", "https://support.lexmark.com/", "lexmark.com"),
        B("Konica Minolta", "konica|minolta|bizhub", "konica ?minolta|konica|minolta",
          "https://www.konicaminolta.eu/eu-en/support/download-centre",
          "https://www.konicaminolta.eu/eu-en/support/download-centre", "konicaminolta.eu"),
        B("Sharp", "sharp", "sharp",
          "https://www.sharp.eu/support", "https://www.sharp.eu/support", "sharp.eu"),
        B("Plustek", "plustek|opticbook|opticslim|opticfilm", "plustek",
          "https://plustek.com/", "https://plustek.com/", "plustek.com"),
        B("Visioneer", "visioneer|onetouch|patriot", "visioneer",
          "https://www.visioneer.com/drivers-and-manuals/", "https://www.visioneer.com/drivers-and-manuals/", "visioneer.com"),
        B("Avision", "avision", "avision",
          "https://www.avision.com/en/", "https://www.avision.com/en/", "avision.com"),
    ];

    /// <summary>Recognises the device from any names Windows gives it (name, description, driver). Null if the brand is unknown.</summary>
    public static DeviceSupport? Identify(params string?[] names)
    {
        var texts = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        foreach (var brand in Brands)
        {
            string? match = texts.FirstOrDefault(t => brand.Pattern.IsMatch(t));
            if (match == null) continue;
            string model = ExtractModel(texts, brand.Company);
            string url = Loc.Language == "nl" ? brand.UrlNl : brand.UrlEn;
            string query = $"{model} driver site:{brand.Domain}";
            return new DeviceSupport(brand.Name, model, url,
                "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query), brand.Domain);
        }
        return null;
    }

    /// <summary>
    /// Best-effort model name: "EPSON3FC4A4 (ET-2870 Series)" → "ET-2870", "HP DeskJet 2700 series [1A2B3C]" → "DeskJet 2700".
    /// </summary>
    internal static string ExtractModel(IReadOnlyList<string> texts, Regex company)
    {
        string best = "";
        foreach (var text in texts)
        {
            // Network names often put the model in brackets after a host name.
            var inBrackets = BracketPart().Match(text);
            string candidate = inBrackets.Success ? inBrackets.Groups[1].Value : text;
            candidate = BracketPart().Replace(candidate, " ");
            candidate = SquareBrackets().Replace(candidate, " ");
            candidate = company.Replace(candidate, " ");
            candidate = Noise().Replace(candidate, " ");
            candidate = Spaces().Replace(candidate, " ").Trim(' ', '-', '_', ',');
            if (candidate.Length > best.Length && candidate.Any(char.IsDigit)) best = candidate;
        }
        return best.Length > 0 ? best : texts[0].Trim();
    }

    [GeneratedRegex(@"\(([^)]*)\)")] private static partial Regex BracketPart();
    [GeneratedRegex(@"\[[^\]]*\]")] private static partial Regex SquareBrackets();
    [GeneratedRegex(@"\b(series|printer|scanner|class driver|wia|wsd|twain|usb|network|netwerk|pcl\s?\d*|ps|xps|v4|v3)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>
    /// Printers installed in Windows from a known brand whose scanner isn't in ScanDesk's list
    /// (for example because the manufacturer's scan software or driver is missing).
    /// </summary>
    public static List<PrinterInfo> PrintersWithoutScanner(IEnumerable<ScannerInfo> scanners)
    {
        var known = scanners.Select(s => Identify(s.Name)).Where(d => d != null)
            .Select(d => Normalize(d!.Brand + d.Model)).ToHashSet();
        var result = new List<PrinterInfo>();
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
            foreach (var queue in queues)
            {
                string name = queue.Name;
                string? driver = null;
                try { driver = queue.QueueDriver?.Name; } catch { }
                var support = Identify(name, driver);
                if (support == null || known.Contains(Normalize(support.Brand + support.Model))) continue;
                if (result.Any(p => Normalize(p.Support.Brand + p.Support.Model) == Normalize(support.Brand + support.Model))) continue;
                result.Add(new PrinterInfo(name, support));
            }
        }
        catch { /* print spooler not running: just show nothing */ }
        return result;
    }

    static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
