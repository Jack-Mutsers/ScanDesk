using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanDesk;

public enum EnhanceMode { Document, Photo, Off }

public sealed record EnhanceOptions(EnhanceMode Mode, int Brightness, int Contrast, int Rotation, bool Deskew);

/// <summary>
/// Post-processing applied to raw scanner output, similar to what Epson Scan 2 does:
/// straightening crooked pages, auto levels (deep blacks, clean white paper), brightness/contrast and an unsharp mask.
/// </summary>
public static class ImageEnhancer
{
    public static BitmapSource Process(BitmapSource src, EnhanceOptions opt, double dpi, bool gray)
    {
        bool adjust = opt.Mode != EnhanceMode.Off || opt.Brightness != 0 || opt.Contrast != 0;
        bool blackWhite = src.Format == PixelFormats.BlackWhite;
        BitmapSource result = src;

        // 1-bit black & white scans get no tone correction (there are no tones), only straightening.
        if ((adjust && !blackWhite) || opt.Deskew)
        {
            gray |= blackWhite;
            var fmt = gray ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            var conv = src.Format == fmt ? src : new FormatConvertedBitmap(src, fmt, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, ch = gray ? 1 : 3;
            int stride = (w * ch + 3) & ~3;
            var px = new byte[(long)stride * h];
            conv.CopyPixels(px, stride, 0);
            bool changed = false;

            if (opt.Deskew)
            {
                double angle = Deskew.DetectAngle(px, w, h, stride, ch);
                if (angle != 0)
                {
                    px = Deskew.Rotate(px, w, h, stride, ch, angle);
                    if (blackWhite) for (int i = 0; i < px.Length; i++) px[i] = px[i] < 128 ? (byte)0 : (byte)255;
                    changed = true;
                }
            }

            if (adjust && !blackWhite)
            {
                var lut = BuildLut(Histogram(px, w, h, stride, ch), opt);
                ApplyLut(px, w, h, stride, ch, lut);

                if (opt.Mode != EnhanceMode.Off)
                {
                    int radius = Math.Max(1, (int)Math.Round(dpi / 300.0));
                    double amount = opt.Mode == EnhanceMode.Document ? 0.75 : 0.5;
                    Sharpen(px, w, h, stride, ch, radius, amount, threshold: 4);
                }
                changed = true;
            }
            if (changed) result = BitmapSource.Create(w, h, dpi, dpi, fmt, null, px, stride);
        }

        if (opt.Rotation % 360 != 0)
            result = new TransformedBitmap(result, new RotateTransform(opt.Rotation));
        result.Freeze();
        return result;
    }

    static long[] Histogram(byte[] px, int w, int h, int stride, int ch)
    {
        var hist = new long[256];
        for (int y = 0; y < h; y += 2)
        {
            int row = y * stride;
            for (int x = 0; x < w; x += 2)
            {
                int i = row + x * ch;
                int lum = ch == 1 ? px[i] : (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;
                hist[lum]++;
            }
        }
        return hist;
    }

    static double Percentile(long[] hist, double p)
    {
        long total = hist.Sum(), target = (long)(total * p), acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc > target) return i;
        }
        return 255;
    }

    static byte[] BuildLut(long[] hist, EnhanceOptions opt)
    {
        // Auto levels: pull the darkest tones down to near-black and grayish paper up to white.
        // Anchored at white so paper stays clean; the slope is capped to avoid harsh results.
        double slope = 1, inHi = 255;
        if (opt.Mode != EnhanceMode.Off)
        {
            bool doc = opt.Mode == EnhanceMode.Document;
            double lo = Percentile(hist, doc ? 0.005 : 0.001);
            double hi = Percentile(hist, doc ? 0.99 : 0.999);
            double targetBlack = doc ? 34 : 16, maxSlope = doc ? 1.6 : 1.3;
            if (hi > 180 && hi < 255) inHi = hi;
            // Documents are mostly paper: treat the typical background tone as white.
            double median = Percentile(hist, 0.5);
            if (doc && median > 200 && median < inHi) inHi = median;
            double outLo = lo > targetBlack ? targetBlack : lo;
            slope = Math.Clamp((255 - outLo) / Math.Max(1, inHi - lo), 1, maxSlope);
        }

        double contrast = 1 + opt.Contrast / 100.0;   // -50..50 → 0.5..1.5
        double brightness = opt.Brightness * 1.5;     // -50..50 → -75..75
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double o = 255 - (inHi - v) * slope;
            o = (o - 128) * contrast + 128 + brightness;
            lut[v] = (byte)Math.Clamp(Math.Round(o), 0, 255);
        }
        return lut;
    }

    static void ApplyLut(byte[] px, int w, int h, int stride, int ch, byte[] lut)
    {
        int rowBytes = w * ch;
        Parallel.For(0, h, y =>
        {
            int row = y * stride;
            for (int i = row; i < row + rowBytes; i++) px[i] = lut[px[i]];
        });
    }

    /// <summary>
    /// Unsharp mask (Gaussian approximated by three box blurs). Processes the image in horizontal
    /// strips in place so very large scans (1200 dpi) don't need a second full-size buffer.
    /// </summary>
    internal static void Sharpen(byte[] px, int w, int h, int stride, int ch, int radius, double amount, int threshold)
    {
        const int StripRows = 256;
        int rowBytes = w * ch;
        int pad = 3 * radius + 1;
        var src = new byte[(StripRows + 2 * pad) * rowBytes];
        var blur = new byte[src.Length];
        var tmp = new byte[src.Length];
        var tail = new byte[pad * rowBytes]; // original (unsharpened) rows just above the current strip
        int tailStartY = 0;

        for (int y0 = 0; y0 < h; y0 += StripRows)
        {
            int y1 = Math.Min(h, y0 + StripRows);
            int s0 = Math.Max(0, y0 - pad), s1 = Math.Min(h, y1 + pad), rows = s1 - s0;

            for (int y = s0; y < s1; y++)
            {
                if (y < y0) Buffer.BlockCopy(tail, (y - tailStartY) * rowBytes, src, (y - s0) * rowBytes, rowBytes);
                else Buffer.BlockCopy(px, y * stride, src, (y - s0) * rowBytes, rowBytes);
            }

            int count = rows * rowBytes;
            Buffer.BlockCopy(src, 0, blur, 0, count);
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(blur, tmp, w, rows, ch, radius);
                BoxV(tmp, blur, w, rows, ch, radius);
            }

            tailStartY = Math.Max(s0, y1 - pad);
            for (int y = tailStartY; y < y1; y++)
                Buffer.BlockCopy(src, (y - s0) * rowBytes, tail, (y - tailStartY) * rowBytes, rowBytes);

            Parallel.For(y0, y1, y =>
            {
                int si = (y - s0) * rowBytes, di = y * stride;
                for (int i = 0; i < rowBytes; i++)
                {
                    int o = src[si + i], d = o - blur[si + i];
                    px[di + i] = Math.Abs(d) < threshold ? (byte)o : (byte)Math.Clamp(o + (int)(amount * d), 0, 255);
                }
            });
        }
    }

    static void BoxH(byte[] input, byte[] output, int w, int rows, int ch, int r)
    {
        int rowBytes = w * ch, div = 2 * r + 1;
        Parallel.For(0, rows, y =>
        {
            int row = y * rowBytes;
            for (int c = 0; c < ch; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += input[row + Math.Clamp(k, 0, w - 1) * ch + c];
                for (int x = 0; x < w; x++)
                {
                    output[row + x * ch + c] = (byte)((sum + r) / div);
                    sum += input[row + Math.Min(x + r + 1, w - 1) * ch + c] - input[row + Math.Max(x - r, 0) * ch + c];
                }
            }
        });
    }

    static void BoxV(byte[] input, byte[] output, int w, int rows, int ch, int r)
    {
        int rowBytes = w * ch, div = 2 * r + 1;
        Parallel.For(0, rowBytes, i =>
        {
            int sum = 0;
            for (int k = -r; k <= r; k++) sum += input[Math.Clamp(k, 0, rows - 1) * rowBytes + i];
            for (int y = 0; y < rows; y++)
            {
                output[y * rowBytes + i] = (byte)((sum + r) / div);
                sum += input[Math.Min(y + r + 1, rows - 1) * rowBytes + i] - input[Math.Max(y - r, 0) * rowBytes + i];
            }
        });
    }
}
