using System.IO;
using SkiaSharp;
namespace TrimLab;

public static class SpriteExporter
{
    /// Trims each image to its figure, then scales ALL of them by one shared factor
    /// (so the set doesn't jitter) and anchors bottom-centre on an OutW x OutH canvas.
    public static void ExportAll(IList<QueueItem> items, Settings s)
    {
        if (items.Count == 0) return;
        Directory.CreateDirectory(s.OutputFolder);

        var bounds = items.Select(i => Bounds(i.Working!)).ToList();
        float scale = Math.Min((float)s.OutW / bounds.Max(b => b.Width),
                               (float)s.OutH / bounds.Max(b => b.Height));
        if (!s.AllowUpscale) scale = Math.Min(scale, 1f);

        for (int n = 0; n < items.Count; n++)
        {
            var r = bounds[n];
            float dw = r.Width * scale, dh = r.Height * scale;
            var dest = new SKRect((s.OutW - dw) / 2, s.OutH - dh, (s.OutW + dw) / 2, s.OutH);

            using var surface = SKSurface.Create(new SKImageInfo(s.OutW, s.OutH, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            using var img = SKImage.FromBitmap(items[n].Working!);
            surface.Canvas.DrawImage(img, SKRect.Create(r.Left, r.Top, r.Width, r.Height), dest, paint);

            string name = Path.GetFileNameWithoutExtension(items[n].FilePath) + ".png";
            using var fs = File.Create(Path.Combine(s.OutputFolder, name));
            surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100).SaveTo(fs);
        }
    }

    static SKRectI Bounds(SKBitmap bmp)
    {
        var px = bmp.Bytes; int w = bmp.Width, h = bmp.Height;
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 0)
                { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return x1 < 0 ? new SKRectI(0, 0, w, h) : new SKRectI(x0, y0, x1 + 1, y1 + 1);
    }
}
