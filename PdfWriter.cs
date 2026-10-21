using System.Globalization;
using System.IO;
using System.Text;

namespace ScanDesk;

public sealed record PdfImagePage(byte[] Jpeg, int PixelWidth, int PixelHeight, bool Gray, double WidthPt, double HeightPt);

/// <summary>Minimal PDF 1.4 writer: one JPEG image per page, streamed to disk page by page.</summary>
public static class PdfWriter
{
    public static void Write(string path, int pageCount, Func<int, PdfImagePage> getPage, string title)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        int objectCount = 3 + pageCount * 3;
        var offsets = new long[objectCount + 1];

        void W(string s) => fs.Write(Encoding.ASCII.GetBytes(s));
        void Begin(int obj) { offsets[obj] = fs.Position; W($"{obj} 0 obj\n"); }
        static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        W("%PDF-1.4\n");
        fs.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        Begin(1);
        W("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        Begin(2);
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++) kids.Append($"{4 + i * 3} 0 R ");
        W($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        Begin(3);
        W($"<< /Title {PdfString(title)} /Producer (ScanDesk) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            var page = getPage(i);
            int pageObj = 4 + i * 3, contentObj = pageObj + 1, imageObj = pageObj + 2;
            string w = N(page.WidthPt), h = N(page.HeightPt);

            Begin(pageObj);
            W($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
              $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            string content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
            Begin(contentObj);
            W($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

            Begin(imageObj);
            W($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
              $"/ColorSpace /{(page.Gray ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 " +
              $"/Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            fs.Write(page.Jpeg);
            W("\nendstream\nendobj\n");
        }

        long xref = fs.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        W(sb.ToString());
    }

    /// <summary>Encodes text as a UTF-16BE hex string so any characters in the title survive.</summary>
    static string PdfString(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (byte b in Encoding.BigEndianUnicode.GetBytes(s)) sb.Append(b.ToString("X2"));
        return sb.Append('>').ToString();
    }
}
