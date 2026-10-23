using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
namespace TrimLab;

public enum Tool { Select, Wand, Pen, Erase, Restore, Crop, Slice }
public enum SelMode { Replace, Add, Subtract }

/// Zoomable canvas. Wheel = zoom at cursor, middle/right drag = pan.
/// Select / Wand / Pen build a selection; Keep / Delete / Copy-to-new-image act on it.
public class EditorView : SKElement
{
    static readonly SKColor[] Fills = { SKColors.Transparent, SKColors.White, SKColors.Black, new(255, 0, 255), new(0, 255, 0) };

    QueueItem? _item; Tool _tool = Tool.Select;
    public QueueItem? Item
    {
        get => _item;
        set { _item = value; crop = null; pen.Clear(); mask = null; selCount = 0; fit = true; RefreshInfo(); InvalidateVisual(); }
    }
    public Settings Cfg { get; set; } = new();
    public Tool Tool { get => _tool; set { _tool = value; pen.Clear(); selRect = null; Cursor = Cursors.Arrow; InvalidateVisual(); } }
    public SelMode Mode { get; set; } = SelMode.Replace;
    public bool ShowOriginal { get; set; }
    public event Action<int, int>? Cropped;     // new width, height after a crop
    public event Action? Edited;                // pixels or size changed
    public event Action<string>? Info;          // status-bar text
    public event Action<QueueItem>? CutMade;    // "Copy to new image"

    float zoom = 1, panX, panY; bool fit = true;
    SKRect? crop, selRect; SKPoint? start, last; Point? panFrom; SKPoint mouse;
    readonly List<SKPoint> pen = new();
    // selection mask: 255 = selected
    byte[]? mask; int mw, mh, selCount; SKBitmap? overlay; bool overlayDirty;
    // slice grid: inner divider positions in image pixels
    float[] xs = Array.Empty<float>(), ys = Array.Empty<float>();
    QueueItem? gridItem; int gridCols, gridRows, gridW, gridH, dragV = -1, dragH = -1;

    public EditorView() { Focusable = true; }

    float K => ActualWidth > 0 ? (float)(CanvasSize.Width / ActualWidth) : 1;
    SKBitmap? Shown => Item == null ? null : ShowOriginal ? Item.Original : Item.Working ?? Item.Original;
    SKPoint ToImg(float sx, float sy) => new((sx - panX) / zoom, (sy - panY) / zoom);
    static SKPoint Clamp(SKPoint p, SKBitmap b) => new(Math.Clamp(p.X, 0, b.Width), Math.Clamp(p.Y, 0, b.Height));
    float ScreenDist(SKPoint a, SKPoint b) { float dx = (a.X - b.X) * zoom, dy = (a.Y - b.Y) * zoom; return MathF.Sqrt(dx * dx + dy * dy); }

    // ---- view ----
    public void Fit() { fit = true; RefreshInfo(); InvalidateVisual(); }
    public void ZoomTo(float z) => ZoomAt((float)CanvasSize.Width / 2, (float)CanvasSize.Height / 2, z);
    public void ZoomBy(float f) => ZoomTo(zoom * f);

    void ZoomAt(float sx, float sy, float nz)
    {
        nz = Math.Clamp(nz, 0.05f, 64);
        panX = sx - (sx - panX) * nz / zoom; panY = sy - (sy - panY) * nz / zoom;
        zoom = nz; fit = false; RefreshInfo(); InvalidateVisual();
    }

    // ---- selection ----
    void EnsureMask()
    {
        var b = Shown;
        if (b == null) return;
        if (mask == null || mw != b.Width || mh != b.Height) { mw = b.Width; mh = b.Height; mask = new byte[mw * mh]; selCount = 0; }
    }

    void MaskChanged()
    {
        selCount = 0;
        if (mask != null) foreach (var m in mask) if (m != 0) selCount++;
        overlayDirty = true; RefreshInfo(); InvalidateVisual();
    }

    /// Merges a region into the selection. Mode comes from the options bar; Shift = add, Alt = subtract.
    void Combine(byte[] region)
    {
        EnsureMask();
        if (mask == null || region.Length != mask.Length) return;
        var mode = Mode;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mode = SelMode.Add;
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mode = SelMode.Subtract;
        for (int i = 0; i < mask.Length; i++)
            mask[i] = mode switch
            {
                SelMode.Add => (byte)(mask[i] | region[i]),
                SelMode.Subtract => (byte)(mask[i] & ~region[i]),
                _ => region[i]
            };
        MaskChanged();
    }

    byte[] RectRegion(SKRect r)
    {
        var b = Shown!; int w = b.Width, h = b.Height;
        var m = new byte[w * h];
        int x0 = (int)Math.Round(r.Left), x1 = (int)Math.Round(r.Right), y0 = (int)Math.Round(r.Top), y1 = (int)Math.Round(r.Bottom);
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++) m[y * w + x] = 255;
        return m;
    }

    byte[] PolyRegion(List<SKPoint> pts)
    {
        var b = Shown!; int w = b.Width, h = b.Height;
        using var path = new SKPath();
        path.MoveTo(pts[0]);
        for (int i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
        path.Close();
        using var a8 = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var c = new SKCanvas(a8))
        {
            c.Clear(SKColors.Transparent);
            using var p = new SKPaint { Color = SKColors.White, IsAntialias = false };
            c.DrawPath(path, p);
        }
        var px = a8.Bytes; int rb = a8.RowBytes;
        var m = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) m[y * w + x] = (byte)(px[y * rb + x] > 127 ? 255 : 0);
        return m;
    }

    public void ClosePen()
    {
        if (pen.Count >= 3 && Shown != null) Combine(PolyRegion(pen));
        pen.Clear(); InvalidateVisual();
    }

    public void SelectAll() { EnsureMask(); if (mask == null) return; Array.Fill(mask, (byte)255); MaskChanged(); }
    public void Deselect() { if (mask == null) return; Array.Clear(mask); MaskChanged(); }
    public void Invert() { EnsureMask(); if (mask == null) return; for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(255 - mask[i]); MaskChanged(); }
    public void Cancel() { pen.Clear(); crop = null; selRect = null; RefreshInfo(); InvalidateVisual(); }

    // ---- selection actions (all undoable) ----
    public void DeleteSelected() => Apply(true);
    public void KeepSelected() => Apply(false);   // clears everything OUTSIDE the selection (or fills it with the chosen colour)

    void Apply(bool deleteInside)
    {
        if (Item == null || ShowOriginal || mask == null || selCount == 0) return;
        var w = Item.EnsureWorking();
        if (w.Width != mw || w.Height != mh) return;
        Item.Snapshot();
        w = Item.Working!;
        var px = w.Bytes;
        var f = deleteInside ? SKColors.Transparent : Fills[Math.Clamp(Cfg.FillIndex, 0, Fills.Length - 1)];
        for (int i = 0; i < mask.Length; i++)
            if ((mask[i] != 0) == deleteInside)
            { int o = i * 4; px[o] = f.Red; px[o + 1] = f.Green; px[o + 2] = f.Blue; px[o + 3] = f.Alpha; }
        Marshal.Copy(px, 0, w.GetPixels(), px.Length);
        w.NotifyPixelsChanged(); Edited?.Invoke(); InvalidateVisual();
    }

    /// Copies the selected pixels (trimmed to their bounding box) into a new queue item.
    public void CopyToNewImage()
    {
        if (Item == null || mask == null || selCount == 0) return;
        using var src = (Item.Working ?? Item.Original).Copy(SKColorType.Rgba8888);
        if (src.Width != mw || src.Height != mh) return;
        int x0 = mw, y0 = mh, x1 = -1, y1 = -1;
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
                if (mask[y * mw + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
        var sp = src.Bytes; var dp = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                if (mask[(y + y0) * mw + x + x0] != 0)
                    Buffer.BlockCopy(sp, ((y + y0) * mw + x + x0) * 4, dp, (y * rw + x) * 4, 4);
        var cut = new SKBitmap(rw, rh, SKColorType.Rgba8888, SKAlphaType.Premul);
        Marshal.Copy(dp, 0, cut.GetPixels(), dp.Length);
        string stem = Path.GetFileNameWithoutExtension(Item.FilePath);
        CutMade?.Invoke(new QueueItem(cut, $"{stem}_part{++Item.Children:00}.png") { Working = cut.Copy(), Status = Status.Cleaned });
    }

    void BuildOverlay()
    {
        overlay?.Dispose();
        overlay = new SKBitmap(mw, mh, SKColorType.Rgba8888, SKAlphaType.Premul);
        var px = new byte[mw * mh * 4];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                int i = y * mw + x;
                if (mask![i] == 0) continue;
                bool edge = x == 0 || y == 0 || x == mw - 1 || y == mh - 1 ||
                            mask[i - 1] == 0 || mask[i + 1] == 0 || mask[i - mw] == 0 || mask[i + mw] == 0;
                int o = i * 4;
                if (edge) { px[o + 1] = 220; px[o + 2] = 255; px[o + 3] = 255; }
                else { px[o + 1] = 90; px[o + 2] = 120; px[o + 3] = 120; }
            }
        Marshal.Copy(px, 0, overlay.GetPixels(), px.Length);
        overlayDirty = false;
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

    (int v, int h) Hit(float sx, float sy)
    {
        for (int i = 0; i < xs.Length; i++) if (Math.Abs(sx - (panX + xs[i] * zoom)) < 7) return (i, -1);
        for (int i = 0; i < ys.Length; i++) if (Math.Abs(sy - (panY + ys[i] * zoom)) < 7) return (-1, i);
        return (-1, -1);
    }

    // ---- crop / history ----
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
        crop = null; mask = null; selCount = 0; Fit(); Cropped?.Invoke(r.Width, r.Height); Edited?.Invoke();
        return n;
    }

    public void ResetCrop() { if (Item == null) return; Item.ResetCrop(); crop = null; mask = null; selCount = 0; Fit(); }
    public void Undo() { Item?.Undo(); Edited?.Invoke(); InvalidateVisual(); }
    public void Redo() { Item?.Redo(); Edited?.Invoke(); InvalidateVisual(); }

    public void RefreshInfo()
    {
        var b = Shown;
        if (b == null) { Info?.Invoke(""); return; }
        var p = ToImg(mouse.X, mouse.Y);
        string s = $"{b.Width} × {b.Height} px   ·   {zoom * 100:0}%   ·   x {(int)p.X}, y {(int)p.Y}";
        if (crop is SKRect r) s += $"   ·   crop {(int)r.Width} × {(int)r.Height}";
        if (selCount > 0) s += $"   ·   {selCount} px selected";
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
        Focus();   // so Ctrl+Z etc. reach the editor instead of a text box
        if (Item == null) return;
        if (e.ChangedButton != MouseButton.Left) { panFrom = e.GetPosition(this); CaptureMouse(); return; }
        if (ShowOriginal && (Tool == Tool.Erase || Tool == Tool.Restore)) return;
        var sp = e.GetPosition(this);
        var pt = ToImg((float)sp.X * K, (float)sp.Y * K);
        var b = Shown!;
        CaptureMouse();
        switch (Tool)
        {
            case Tool.Select: start = Clamp(pt, b); selRect = null; break;
            case Tool.Crop: start = Clamp(pt, b); crop = null; break;
            case Tool.Slice: EnsureGrid(); (dragV, dragH) = Hit((float)sp.X * K, (float)sp.Y * K); break;
            case Tool.Erase:
            case Tool.Restore: Item.Snapshot(); last = pt; Stamp(pt, pt); break;
            case Tool.Wand:
                int wx = (int)pt.X, wy = (int)pt.Y;
                if (wx >= 0 && wy >= 0 && wx < b.Width && wy < b.Height) Combine(BackgroundCleaner.FloodSelect(b, wx, wy, Cfg.WandTol));
                break;
            case Tool.Pen:
                var pp = Clamp(pt, b);
                if (pen.Count >= 3 && (e.ClickCount >= 2 || ScreenDist(pp, pen[0]) < 10)) ClosePen();
                else pen.Add(pp);
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
            Cursor = hv.Item1 >= 0 ? Cursors.SizeWE : hv.Item2 >= 0 ? Cursors.SizeNS
                   : Tool is Tool.Erase or Tool.Restore or Tool.Slice ? Cursors.Arrow : Cursors.Cross;
        }
        if (panFrom is Point pf)
        {
            panX += (float)(sp.X - pf.X) * k; panY += (float)(sp.Y - pf.Y) * k; panFrom = sp; fit = false;
        }
        else if (Item != null && IsMouseCaptured)
        {
            var pt = ToImg(mouse.X, mouse.Y);
            if ((Tool == Tool.Crop || Tool == Tool.Select) && start is SKPoint a)
            {
                var b = Clamp(pt, Shown!);
                var rc = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                if (Tool == Tool.Crop) crop = rc; else selRect = rc;
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
        if (Tool == Tool.Select && start != null && Item != null && e.ChangedButton == MouseButton.Left)
        {
            EnsureMask();   // a plain click (no drag) in "New" mode clears the selection
            Combine(selRect is SKRect sr && sr.Width >= 1 && sr.Height >= 1 ? RectRegion(sr) : new byte[mw * mh]);
        }
        bool stroked = last != null;
        selRect = null; panFrom = null; start = null; last = null; dragV = dragH = -1;
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
            zoom = Math.Min((float)W / bmp.Width, (float)H / bmp.Height);
            panX = (W - bmp.Width * zoom) / 2; panY = (H - bmp.Height * zoom) / 2;
        }

        c.Save(); c.Translate(panX, panY); c.Scale(zoom);
        using var p = new SKPaint { FilterQuality = zoom >= 2 ? SKFilterQuality.None : SKFilterQuality.Medium };
        c.DrawBitmap(bmp, 0, 0, p);

        if (mask != null && selCount > 0 && mw == bmp.Width && mh == bmp.Height)
        {
            if (overlayDirty || overlay == null) BuildOverlay();
            c.DrawBitmap(overlay!, 0, 0, p);
        }
        if (crop is SKRect r)
        {
            using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
            c.Save(); c.ClipRect(r, SKClipOperation.Difference); c.DrawRect(0, 0, bmp.Width, bmp.Height, dim); c.Restore();
            using var line = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.DeepSkyBlue, StrokeWidth = 2 / zoom };
            c.DrawRect(r, line);
        }
        if (selRect is SKRect sr)
        {
            using var fill = new SKPaint { Color = new SKColor(0, 120, 160, 60) };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.DeepSkyBlue, StrokeWidth = 1.5f / zoom };
            c.DrawRect(sr, fill); c.DrawRect(sr, edge);
        }
        if (pen.Count > 0)
        {
            using var pl = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Gold, StrokeWidth = 2 / zoom, IsAntialias = true };
            using var dot = new SKPaint { Color = SKColors.Gold, IsAntialias = true };
            for (int i = 1; i < pen.Count; i++) c.DrawLine(pen[i - 1], pen[i], pl);
            c.DrawLine(pen[^1], ToImg(mouse.X, mouse.Y), pl);
            foreach (var pt in pen) c.DrawCircle(pt, 3 / zoom, dot);
            if (pen.Count >= 3) c.DrawCircle(pen[0], 7 / zoom, pl);   // click here to close
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
