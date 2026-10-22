using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
namespace TrimLab;

public enum Tool { Crop, Erase, Restore, Wand, Slice }

/// Zoomable/pannable canvas with crop, eraser, restore brush and magic wand.
/// Wheel = zoom at cursor. Middle or right mouse drag = pan.
public class EditorView : SKElement
{
    QueueItem? _item;
    public QueueItem? Item
    {
        get => _item;
        set
        {
            bool hadItem = _item != null;
            _item = value;
            crop = null;
            if (!hadItem || value == null) fit = true;
            RefreshInfo(); InvalidateVisual();
        }
    }
    public Settings Cfg { get; set; } = new();
    public Tool Tool { get; set; } = Tool.Erase;
    public bool ShowOriginal { get; set; }
    public event Action<int, int>? Cropped;   // new width, height after a crop
    public event Action? Edited;            // pixels or size changed
    public event Action<string>? Info;      // status-bar text

    float zoom = 1, panX, panY; bool fit = true;
    // slice grid: inner divider positions in image pixels (cols-1 vertical, rows-1 horizontal)
    float[] xs = Array.Empty<float>(), ys = Array.Empty<float>();
    QueueItem? gridItem; int gridCols, gridRows, gridW, gridH, dragV = -1, dragH = -1;
    SKRect? crop; SKPoint? start, last; Point? panFrom; SKPoint mouse;
    float K => ActualWidth > 0 ? (float)(CanvasSize.Width / ActualWidth) : 1;
    SKBitmap? Shown => Item == null ? null : ShowOriginal ? Item.Original : Item.Working ?? Item.Original;
    SKPoint ToImg(float sx, float sy) => new((sx - panX) / zoom, (sy - panY) / zoom);
    static SKPoint Clamp(SKPoint p, SKBitmap b) => new(Math.Clamp(p.X, 0, b.Width), Math.Clamp(p.Y, 0, b.Height));

    // ---- view ----
    public void Fit() { Cfg.Zoom = 0; fit = true; RefreshInfo(); InvalidateVisual(); }
    public void ZoomTo(float z) => ZoomAt((float)CanvasSize.Width / 2, (float)CanvasSize.Height / 2, z);
    public void ZoomBy(float f) => ZoomTo(zoom * f);

    void ZoomAt(float sx, float sy, float nz)
    {
        nz = Math.Clamp(nz, 0.05f, 64);
        panX = sx - (sx - panX) * nz / zoom; panY = sy - (sy - panY) * nz / zoom;
        zoom = nz; Cfg.Zoom = zoom; fit = false; RefreshInfo(); InvalidateVisual();
    }

    // ---- slice grid ----
    void EnsureGrid()
    {
        var b = Shown;
        if (b == null) return;
        int c = Math.Max(1, Cfg.SliceCols), r = Math.Max(1, Cfg.SliceRows);
        if (gridItem == Item && gridCols == c && gridRows == r && gridW == b.Width && gridH == b.Height) return;
        gridItem = Item; gridCols = c; gridRows = r; gridW = b.Width; gridH = b.Height;
        xs = new float[c - 1]; ys = new float[r - 1];
        for (int i = 0; i < xs.Length; i++) xs[i] = (float)Math.Round((double)(i + 1) * b.Width / c);
        for (int i = 0; i < ys.Length; i++) ys[i] = (float)Math.Round((double)(i + 1) * b.Height / r);
    }

    public void ResetGrid() { gridItem = null; EnsureGrid(); InvalidateVisual(); }

    /// Cells for the current lines, row by row.
    public SKRectI[] SliceCells()
    {
        EnsureGrid();
        var X = new int[xs.Length + 2]; var Y = new int[ys.Length + 2];
        for (int i = 0; i < xs.Length; i++) X[i + 1] = (int)xs[i];
        for (int i = 0; i < ys.Length; i++) Y[i + 1] = (int)ys[i];
        X[^1] = gridW; Y[^1] = gridH;
        var cells = new List<SKRectI>();
        for (int r = 0; r < Y.Length - 1; r++)
            for (int c = 0; c < X.Length - 1; c++) cells.Add(new SKRectI(X[c], Y[r], X[c + 1], Y[r + 1]));
        return cells.ToArray();
    }

    public (float[] X, float[] Y) SliceRatios()
    {
        EnsureGrid();
        return (xs.Select(x => x / gridW).ToArray(), ys.Select(y => y / gridH).ToArray());
    }

    public static SKRectI[] SliceCellsFor(int width, int height, float[] xRatios, float[] yRatios)
    {
        var X = xRatios.Select(x => Math.Clamp((int)Math.Round(x * width), 1, width - 1)).ToList();
        var Y = yRatios.Select(y => Math.Clamp((int)Math.Round(y * height), 1, height - 1)).ToList();
        X.Insert(0, 0); X.Add(width); Y.Insert(0, 0); Y.Add(height);
        var cells = new List<SKRectI>();
        for (int r = 0; r < Y.Count - 1; r++)
            for (int c = 0; c < X.Count - 1; c++)
                cells.Add(new SKRectI(X[c], Y[r], X[c + 1], Y[r + 1]));
        return cells.ToArray();
    }

    (int v, int h) Hit(float sx, float sy)
    {
        for (int i = 0; i < xs.Length; i++) if (Math.Abs(sx - (panX + xs[i] * zoom)) < 7) return (i, -1);
        for (int i = 0; i < ys.Length; i++) if (Math.Abs(sy - (panY + ys[i] * zoom)) < 7) return (-1, i);
        return (-1, -1);
    }

    // ---- edit commands ----
    /// Crops the selected image; with `alsoCrop`, also every other image of the same size. Returns how many were cropped.
    public int ApplyCrop(IEnumerable<QueueItem>? alsoCrop = null)
    {
        if (Item == null || crop is not SKRect sel || sel.Width < 1 || sel.Height < 1) return 0;
        var r = SKRectI.Round(sel);
        var cur = Item;
        int w0 = cur.Original.Width, h0 = cur.Original.Height, n = 1;
        cur.Crop(r);
        if (alsoCrop != null)
            foreach (var o in alsoCrop)
                if (o != cur && o.Original.Width == w0 && o.Original.Height == h0) { o.Crop(r); n++; }
        crop = null; Fit(); Cropped?.Invoke(r.Width, r.Height); Edited?.Invoke();
        return n;
    }
    public void ResetCrop() { if (Item == null) return; Item.ResetCrop(); crop = null; Fit(); }
    public void ClearSelection() { crop = null; RefreshInfo(); InvalidateVisual(); }
    public void Undo() { Item?.Undo(); Edited?.Invoke(); InvalidateVisual(); }
    public void Redo() { Item?.Redo(); Edited?.Invoke(); InvalidateVisual(); }

    public void RefreshInfo()
    {
        var b = Shown;
        if (b == null) { Info?.Invoke(""); return; }
        var p = ToImg(mouse.X, mouse.Y);
        string s = $"{b.Width} × {b.Height} px   ·   {zoom * 100:0}%   ·   x {(int)p.X}, y {(int)p.Y}";
        if (crop is SKRect r) s += $"   ·   selection {(int)r.Width} × {(int)r.Height}";
        Info?.Invoke(s);
    }

    void Stamp(SKPoint a, SKPoint b)
    {
        var bmp = Item!.Working!;
        float r = Cfg.BrushSize / 2f, step = Math.Max(1, r / 2);
        int n = Math.Max(1, (int)(SKPoint.Distance(a, b) / step));
        using var c = new SKCanvas(bmp);
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
        using var src = new SKPaint { BlendMode = SKBlendMode.Src };
        for (int i = 0; i <= n; i++)
        {
            float x = a.X + (b.X - a.X) * i / n, y = a.Y + (b.Y - a.Y) * i / n;
            if (Tool == Tool.Erase) c.DrawCircle(x, y, r, clear);
            else
            {
                using var path = new SKPath(); path.AddCircle(x, y, r);
                c.Save(); c.ClipPath(path, SKClipOperation.Intersect, false);
                c.DrawBitmap(Item.Original, 0, 0, src); c.Restore();
            }
        }
        bmp.NotifyPixelsChanged();
    }

    // ---- mouse ----
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Item == null) return;
        var p = e.GetPosition(this); float k = K;
        ZoomAt((float)p.X * k, (float)p.Y * k, zoom * (e.Delta > 0 ? 1.25f : 0.8f));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (Item == null) return;
        if (e.ChangedButton != MouseButton.Left) { panFrom = e.GetPosition(this); CaptureMouse(); return; }
        if (ShowOriginal && Tool != Tool.Crop) return;
        var sp = e.GetPosition(this);
        var pt = ToImg((float)sp.X * K, (float)sp.Y * K);
        var b = Shown!;
        CaptureMouse();
        switch (Tool)
        {
            case Tool.Crop: start = Clamp(pt, b); crop = null; break;
            case Tool.Slice: EnsureGrid(); (dragV, dragH) = Hit((float)sp.X * K, (float)sp.Y * K); break;
            case Tool.Erase:
            case Tool.Restore: Item.Snapshot(); last = pt; Stamp(pt, pt); break;
            case Tool.Wand:
                int x = (int)pt.X, y = (int)pt.Y;
                if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) break;
                Item.Snapshot();
                BackgroundCleaner.FloodClear(Item.Working!, x, y, Cfg.WandTol);
                Item.Working!.NotifyPixelsChanged(); Edited?.Invoke();
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var sp = e.GetPosition(this); float k = K;
        mouse = new((float)sp.X * k, (float)sp.Y * k);
        if (!IsMouseCaptured)
        {
            var hv = Tool == Tool.Slice && Item != null ? Hit(mouse.X, mouse.Y) : (-1, -1);
            Cursor = hv.Item1 >= 0 ? Cursors.SizeWE : hv.Item2 >= 0 ? Cursors.SizeNS : Cursors.Arrow;
        }
        if (panFrom is Point pf)
        {
            panX += (float)(sp.X - pf.X) * k; panY += (float)(sp.Y - pf.Y) * k; panFrom = sp; fit = false;
        }
        else if (Item != null && IsMouseCaptured)
        {
            var pt = ToImg(mouse.X, mouse.Y);
            if (Tool == Tool.Crop && start is SKPoint a)
            {
                var b = Clamp(pt, Shown!);
                crop = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            }
            else if ((Tool == Tool.Erase || Tool == Tool.Restore) && last is SKPoint l) { Stamp(l, pt); last = pt; }
            else if (Tool == Tool.Slice)
            {
                if (dragV >= 0)
                {
                    float lo = dragV == 0 ? 0 : xs[dragV - 1], hi = dragV == xs.Length - 1 ? gridW : xs[dragV + 1];
                    xs[dragV] = Math.Min(hi - 1, Math.Max(lo + 1, (float)Math.Round(pt.X)));
                }
                else if (dragH >= 0)
                {
                    float lo = dragH == 0 ? 0 : ys[dragH - 1], hi = dragH == ys.Length - 1 ? gridH : ys[dragH + 1];
                    ys[dragH] = Math.Min(hi - 1, Math.Max(lo + 1, (float)Math.Round(pt.Y)));
                }
            }
        }
        RefreshInfo(); InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        ReleaseMouseCapture();
        bool stroked = last != null;
        panFrom = null; start = null; last = null; dragV = dragH = -1;
        if (stroked) Edited?.Invoke();
        RefreshInfo(); InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    // ---- paint ----
    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas; int W = e.Info.Width, H = e.Info.Height;
        c.Clear(SKColors.White);
        using var grey = new SKPaint { Color = new SKColor(0xDD, 0xDD, 0xDD) };
        for (int y = 0; y < H; y += 16)
            for (int x = (y / 16 % 2) * 16; x < W; x += 32) c.DrawRect(x, y, 16, 16, grey);

        var bmp = Shown;
        if (bmp == null) return;
        if (fit)
        {
            zoom = Cfg.Zoom > 0 ? Cfg.Zoom : Math.Min((float)W / bmp.Width, (float)H / bmp.Height);
            panX = (W - bmp.Width * zoom) / 2; panY = (H - bmp.Height * zoom) / 2;
            fit = false;
        }

        c.Save(); c.Translate(panX, panY); c.Scale(zoom);
        using var p = new SKPaint { FilterQuality = zoom >= 2 ? SKFilterQuality.None : SKFilterQuality.Medium };
        c.DrawBitmap(bmp, 0, 0, p);
        if (crop is SKRect r)
        {
            using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
            c.Save(); c.ClipRect(r, SKClipOperation.Difference); c.DrawRect(0, 0, bmp.Width, bmp.Height, dim); c.Restore();
            using var line = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.DeepSkyBlue, StrokeWidth = 2 / zoom };
            c.DrawRect(r, line);
        }
        if (Tool == Tool.Slice)
        {
            EnsureGrid();
            using var dash = SKPathEffect.CreateDash(new[] { 6 / zoom, 4 / zoom }, 0);
            using var gl = new SKPaint { Color = SKColors.Magenta, StrokeWidth = 2 / zoom, Style = SKPaintStyle.Stroke, PathEffect = dash };
            foreach (var vx in xs) c.DrawLine(vx, 0, vx, bmp.Height, gl);
            foreach (var hy in ys) c.DrawLine(0, hy, bmp.Width, hy, gl);
        }
        c.Restore();

        if ((Tool == Tool.Erase || Tool == Tool.Restore) && IsMouseOver)
        {
            using var ring = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true,
                Color = Tool == Tool.Erase ? SKColors.Red : SKColors.LimeGreen };
            c.DrawCircle(mouse.X, mouse.Y, Cfg.BrushSize / 2f * zoom, ring);
        }
    }
}
