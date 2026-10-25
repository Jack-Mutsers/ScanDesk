using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanDesk;

public enum PaperSize { A4, Letter }

/// <summary>
/// Enlarges a page (an A5 scan, a cropped part, a small photo) to fill a full sheet of paper.
/// Uses Lanczos resampling plus a light unsharp mask, which keeps text and edges much crisper than
/// the plain stretching a PDF viewer or printer would otherwise do.
/// </summary>
public static class Upscaler
{
    public static (double W, double H) PaperInches(PaperSize paper) =>
        paper == PaperSize.Letter ? (8.5, 11) : (210 / 25.4, 297 / 25.4);

    public static string PaperName(PaperSize paper) => paper == PaperSize.Letter ? "Letter" : "A4";

    /// <summary>
    /// Scales the image to fit the paper (keeping its proportions), centered on a white sheet.
    /// Portrait or landscape follows the image. The result is 300–600 dpi.
    /// </summary>
    public static BitmapSource FitToPaper(BitmapSource src, double srcDpi, PaperSize paper, bool gray, out double dpi)
    {
        dpi = Math.Clamp(srcDpi, 300, 600);
        var (pw, ph) = PaperInches(paper);
        if (src.PixelWidth > src.PixelHeight) (pw, ph) = (ph, pw);
        int canvasW = (int)Math.Round(pw * dpi), canvasH = (int)Math.Round(ph * dpi);

        double scale = Math.Min((double)canvasW / src.PixelWidth, (double)canvasH / src.PixelHeight);
        int w = Math.Clamp((int)Math.Round(src.PixelWidth * scale), 1, canvasW);
        int h = Math.Clamp((int)Math.Round(src.PixelHeight * scale), 1, canvasH);

        var fmt = gray ? PixelFormats.Gray8 : PixelFormats.Bgr24;
        int ch = gray ? 1 : 3;
        var conv = src.Format == fmt ? src : new FormatConvertedBitmap(src, fmt, null, 0);
        int sStride = (src.PixelWidth * ch + 3) & ~3;
        var px = new byte[(long)sStride * src.PixelHeight];
        conv.CopyPixels(px, sStride, 0);

        var resized = Resize(px, src.PixelWidth, src.PixelHeight, sStride, ch, w, h, out int rStride);

        // Enlarging softens edges; sharpen a little more the further the image was blown up.
        if (scale > 1.15)
        {
            int radius = Math.Max(1, (int)Math.Round(dpi / 300.0 * Math.Min(scale, 3) / 1.5));
            double amount = Math.Min(0.7, 0.25 + 0.15 * scale);
            // A high threshold sharpens real edges (text, lines) but leaves paper grain and print dots alone.
            ImageEnhancer.Sharpen(resized, w, h, rStride, ch, radius, amount, threshold: 12);
        }

        // Center on a white sheet.
        int cStride = (canvasW * ch + 3) & ~3;
        var canvas = new byte[(long)cStride * canvasH];
        Array.Fill(canvas, (byte)255);
        int ox = (canvasW - w) / 2, oy = (canvasH - h) / 2;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(resized, y * rStride, canvas, (oy + y) * cStride + ox * ch, w * ch);

        var result = BitmapSource.Create(canvasW, canvasH, dpi, dpi, fmt, null, canvas, cStride);
        result.Freeze();
        return result;
    }

    /// <summary>Separable Lanczos-3 resize (works for both enlarging and shrinking).</summary>
    static byte[] Resize(byte[] src, int sw, int sh, int sStride, int ch, int dw, int dh, out int dStride)
    {
        var xw = Weights(sw, dw);
        var yw = Weights(sh, dh);

        // Horizontal pass: sw x sh -> dw x sh
        int tStride = dw * ch;
        var tmp = new byte[(long)tStride * sh];
        Parallel.For(0, sh, y =>
        {
            int srow = y * sStride, trow = y * tStride;
            for (int x = 0; x < dw; x++)
            {
                var (idx, wts) = xw[x];
                for (int c = 0; c < ch; c++)
                {
                    float sum = 0;
                    for (int k = 0; k < idx.Length; k++) sum += src[srow + idx[k] * ch + c] * wts[k];
                    tmp[trow + x * ch + c] = ClampByte(sum);
                }
            }
        });

        // Vertical pass: dw x sh -> dw x dh
        int stride = (dw * ch + 3) & ~3;
        var dst = new byte[(long)stride * dh];
        Parallel.For(0, dh, y =>
        {
            var (idx, wts) = yw[y];
            int drow = y * stride;
            for (int i = 0; i < tStride; i++)
            {
                float sum = 0;
                for (int k = 0; k < idx.Length; k++) sum += tmp[idx[k] * tStride + i] * wts[k];
                dst[drow + i] = ClampByte(sum);
            }
        });
        dStride = stride;
        return dst;
    }

    static byte ClampByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    static (int[] Index, float[] Weight)[] Weights(int srcLen, int dstLen)
    {
        double scale = (double)dstLen / srcLen;
        double stretch = Math.Max(1, 1 / scale); // widen the filter when shrinking to avoid aliasing
        double support = 3 * stretch;
        var result = new (int[], float[])[dstLen];
        for (int i = 0; i < dstLen; i++)
        {
            double center = (i + 0.5) / scale - 0.5;
            int start = (int)Math.Floor(center - support) + 1, end = (int)Math.Floor(center + support);
            var idx = new int[end - start + 1];
            var wts = new float[idx.Length];
            double total = 0;
            for (int j = start; j <= end; j++)
            {
                double wgt = Lanczos3((j - center) / stretch);
                idx[j - start] = Math.Clamp(j, 0, srcLen - 1);
                wts[j - start] = (float)wgt;
                total += wgt;
            }
            for (int k = 0; k < wts.Length; k++) wts[k] = (float)(wts[k] / total);
            result[i] = (idx, wts);
        }
        return result;
    }

    static double Lanczos3(double x)
    {
        if (x == 0) return 1;
        if (Math.Abs(x) >= 3) return 0;
        double px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }
}
