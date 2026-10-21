namespace ScanDesk;

/// <summary>
/// Straightens pages that were placed on the glass at a slight angle.
/// The angle is found with a projection profile: horizontal edges (text lines, picture borders)
/// line up into sharp peaks when projected at the correct angle.
/// </summary>
public static class Deskew
{
    const double MaxAngle = 6;      // degrees; larger tilts are almost certainly intentional
    const double MinAngle = 0.15;   // below this the correction isn't visible
    const int MaxPoints = 80_000;

    /// <summary>Returns the page tilt in degrees (positive = lines slope down to the right), or 0 if unsure.</summary>
    public static double DetectAngle(byte[] px, int w, int h, int stride, int ch)
    {
        // Work on a ~1000 px wide grayscale copy.
        int f = Math.Max(1, w / 1000);
        int sw = w / f, sh = h / f;
        if (sw < 50 || sh < 50) return 0;
        var g = new byte[sw * sh];
        for (int y = 0; y < sh; y++)
        {
            int row = y * f * stride;
            for (int x = 0; x < sw; x++)
            {
                int i = row + x * f * ch;
                g[y * sw + x] = ch == 1 ? px[i] : (byte)((px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8);
            }
        }

        // Horizontal edge points, ignoring a margin where the scanner bed edge often shows.
        var xs = new List<int>();
        var ys = new List<int>();
        int margin = Math.Max(2, sw / 50);
        for (int y = margin; y < sh - margin; y++)
            for (int x = margin; x < sw - margin; x++)
                if (Math.Abs(g[y * sw + x] - g[(y - 1) * sw + x]) > 48) { xs.Add(x); ys.Add(y); }
        if (xs.Count < 200) return 0;

        // Thin out evenly if there are too many points.
        int step = Math.Max(1, xs.Count / MaxPoints);
        int n = (xs.Count + step - 1) / step;
        var px2 = new int[n];
        var py2 = new int[n];
        for (int i = 0, j = 0; i < xs.Count; i += step, j++) { px2[j] = xs[i]; py2[j] = ys[i]; }

        int offset = (int)(sw * Math.Tan(MaxAngle * Math.PI / 180)) + 2;
        var bins = new int[sh + 2 * offset];
        double Score(double deg)
        {
            Array.Clear(bins);
            double t = Math.Tan(deg * Math.PI / 180);
            for (int i = 0; i < n; i++) bins[(int)Math.Round(py2[i] - px2[i] * t) + offset]++;
            double s = 0;
            foreach (int b in bins) s += (double)b * b;
            return s;
        }

        double best = 0, bestScore = Score(0), zeroScore = bestScore;
        for (double a = -MaxAngle; a <= MaxAngle + 1e-9; a += 0.2)
        {
            double sc = Score(a);
            if (sc > bestScore) { bestScore = sc; best = a; }
        }
        double coarse = best;
        for (double a = coarse - 0.2; a <= coarse + 0.2 + 1e-9; a += 0.02)
        {
            double sc = Score(a);
            if (sc > bestScore) { bestScore = sc; best = a; }
        }

        // Only correct when the tilted projection is clearly better than the straight one.
        if (Math.Abs(best) < MinAngle || bestScore < zeroScore * 1.03) return 0;
        return Math.Round(best, 2);
    }

    /// <summary>Rotates the image so content tilted by <paramref name="angle"/> degrees becomes level. Corners are filled white.</summary>
    public static byte[] Rotate(byte[] src, int w, int h, int stride, int ch, double angle)
    {
        var dst = new byte[src.Length];
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;

        Parallel.For(0, h, y =>
        {
            double dy = y - cy;
            int drow = y * stride;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                double sx = cx + cos * dx - sin * dy;
                double sy = cy + sin * dx + cos * dy;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                int di = drow + x * ch;
                if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1)
                {
                    for (int c = 0; c < ch; c++) dst[di + c] = 255;
                    continue;
                }
                double fx = sx - x0, fy = sy - y0;
                int i00 = y0 * stride + x0 * ch, i10 = i00 + ch, i01 = i00 + stride, i11 = i01 + ch;
                for (int c = 0; c < ch; c++)
                {
                    double top = src[i00 + c] + (src[i10 + c] - src[i00 + c]) * fx;
                    double bot = src[i01 + c] + (src[i11 + c] - src[i01 + c]) * fx;
                    dst[di + c] = (byte)(top + (bot - top) * fy + 0.5);
                }
            }
        });
        return dst;
    }
}
