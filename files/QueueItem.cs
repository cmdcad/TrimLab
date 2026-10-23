using System.IO;
using System.ComponentModel;
using SkiaSharp;
namespace TrimLab;

public enum Status { Pending, Cleaned, Approved, Exported }

public class QueueItem : INotifyPropertyChanged
{
    public string FilePath { get; }
    public SKBitmap Source { get; }                 // uncropped, for Reset crop
    public SKBitmap Original { get; private set; }  // current (cropped) image
    public SKBitmap? Working { get; set; }          // cleaned / hand-edited copy
    public Stack<SKBitmap> UndoStack { get; } = new();
    public Stack<SKBitmap> RedoStack { get; } = new();

    Status _status;
    public Status Status
    {
        get => _status;
        set { _status = value; Notify(); }
    }

    public string Display => $"{Path.GetFileName(FilePath)}  ·  {Status}";
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify() => PropertyChanged?.Invoke(this, new(null));

    public QueueItem(string path)
    {
        FilePath = path;
        Source = Original = SKBitmap.Decode(path);
    }

    /// In-memory item (used for slices); `name` only decides the exported file name.
    public QueueItem(SKBitmap bmp, string name)
    {
        FilePath = name;
        Source = Original = bmp;
    }

    public int Children { get; set; }                     // numbering for "Copy to new image"
    public QueueItem? SliceParent { get; private set; }   // the image this was sliced from (for Unslice)

    /// Cuts the current image (and its cleaned copy, if any) into the given cells. This item is left untouched.
    public List<QueueItem> Slice(IEnumerable<SKRectI> cells)
    {
        string stem = Path.GetFileNameWithoutExtension(FilePath);
        int n = 0;
        var list = new List<QueueItem>();
        foreach (var rect in cells)
        {
            var item = new QueueItem(Sub(Original, rect), $"{stem}_{++n:00}.png") { SliceParent = this };
            if (Working != null) { item.Working = Sub(Working, rect); item.Status = Status.Cleaned; }
            list.Add(item);
        }
        return list;
    }

    /// Editable copy; created from the original on first use (so you can hand-clean without auto-clean).
    public SKBitmap EnsureWorking()
    {
        if (Working == null)
        {
            Working = Original.Copy(SKColorType.Rgba8888);
            if (_status == Status.Pending) Status = Status.Cleaned;
        }
        return Working;
    }

    public void Snapshot() { UndoStack.Push(EnsureWorking().Copy()); RedoStack.Clear(); }

    public void Undo()
    {
        if (UndoStack.Count == 0 || Working == null) return;
        RedoStack.Push(Working); Working = UndoStack.Pop(); Notify();
    }

    public void Redo()
    {
        if (RedoStack.Count == 0 || Working == null) return;
        UndoStack.Push(Working); Working = RedoStack.Pop(); Notify();
    }

    public void Crop(SKRectI r)
    {
        Original = Sub(Original, r);
        if (Working != null) Working = Sub(Working, r);
        UndoStack.Clear(); RedoStack.Clear();
        if (Status is Status.Approved or Status.Exported) Status = Status.Cleaned; else Notify();
    }

    public void ResetCrop()
    {
        Original = Source; Working = null;
        UndoStack.Clear(); RedoStack.Clear(); Status = Status.Pending;
    }

    static SKBitmap Sub(SKBitmap b, SKRectI r) { var s = new SKBitmap(); b.ExtractSubset(s, r); return s.Copy(); }
}
