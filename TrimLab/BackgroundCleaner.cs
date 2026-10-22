using System.Runtime.InteropServices;
using SkiaSharp;
namespace TrimLab;

public static class BackgroundCleaner
{
    /// Flood-fills light, low-saturation (checker) pixels inward from the border,
    /// grows that mask by `grow` px to remove edge halo, and clears it to transparent.
    public static SKBitmap AutoClean(SKBitmap src, int tolerance, int grow)
    {
        var bmp = src.Copy(SKColorType.Rgba8888);
        int w = bmp.Width, h = bmp.Height;
        var px = bmp.Bytes;
        var bg = new bool[w * h];

        bool IsChecker(int i)
        {
            int o = i * 4, r = px[o], g = px[o + 1], b = px[o + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            return max - min <= tolerance && min >= 140;
        }

        var q = new Queue<int>();
        void Seed(int x, int y)
        {
            int i = y * w + x;
            if (!bg[i] && IsChecker(i)) { bg[i] = true; q.Enqueue(i); }
        }
        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w;
            if (x > 0) Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }

        for (int n = 0; n < grow; n++)
        {
            var next = (bool[])bg.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (bg[i]) continue;
                    if ((x > 0 && bg[i - 1]) || (x < w - 1 && bg[i + 1]) ||
                        (y > 0 && bg[i - w]) || (y < h - 1 && bg[i + w])) next[i] = true;
                }
            bg = next;
        }

        for (int i = 0; i < bg.Length; i++)
            if (bg[i]) { int o = i * 4; px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0; }

        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    /// Magic wand: clears the contiguous region around (sx, sy) whose colour is within `tol` of the clicked pixel.
    public static void FloodClear(SKBitmap bmp, int sx, int sy, int tol)
    {
        int w = bmp.Width, h = bmp.Height;
        var px = bmp.Bytes;
        int s = (sy * w + sx) * 4;
        if (px[s + 3] == 0) return;
        int r0 = px[s], g0 = px[s + 1], b0 = px[s + 2];
        var seen = new bool[w * h];
        var q = new Queue<int>();
        seen[sy * w + sx] = true; q.Enqueue(sy * w + sx);

        void Try(int n)
        {
            if (seen[n]) return;
            int p = n * 4;
            if (px[p + 3] == 0) return;
            if (Math.Abs(px[p] - r0) <= tol && Math.Abs(px[p + 1] - g0) <= tol && Math.Abs(px[p + 2] - b0) <= tol)
            { seen[n] = true; q.Enqueue(n); }
        }

        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w, o = i * 4;
            px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0;
            if (x > 0) Try(i - 1);
            if (x < w - 1) Try(i + 1);
            if (y > 0) Try(i - w);
            if (y < h - 1) Try(i + w);
        }
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
    }
}
