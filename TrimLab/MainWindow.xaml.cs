using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
namespace TrimLab;

public partial class MainWindow : Window
{
    readonly Settings S = Settings.Load();
    readonly ObservableCollection<QueueItem> Q = new();
    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = S;
        List.ItemsSource = Q;
        Editor.Cfg = S;
        Editor.Info += t => Info.Text = t;
        Editor.Cropped += ApplyAutoSize;
        Editor.Edited += () =>   // editing an approved/exported image puts it back into review
        { if (Editor.Item is { Status: Status.Approved or Status.Exported } it) it.Status = Status.Cleaned; };
        Closing += (_, _) => S.Save();
    }

    // ---- queue ----
    void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var files = Directory.Exists(p) ? Directory.GetFiles(p) : new[] { p };
            foreach (var f in files.Where(f => Exts.Contains(Path.GetExtension(f).ToLower()))) Q.Add(new QueueItem(f));
        }
        var first = paths.FirstOrDefault();
        if (first != null) S.InputFolder = Directory.Exists(first) ? first : Path.GetDirectoryName(first)!;
        if (List.SelectedItem == null && Q.Count > 0) List.SelectedIndex = 0;
    }

    void OnDrop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] p) AddPaths(p); }

    void OpenFiles(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, InitialDirectory = S.InputFolder, Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
        if (d.ShowDialog() == true) AddPaths(d.FileNames);
    }

    void OpenFolder(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { InitialDirectory = S.InputFolder };
        if (d.ShowDialog() == true) AddPaths(new[] { d.FolderName });
    }

    void PickOutput(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { InitialDirectory = S.OutputFolder };
        if (d.ShowDialog() == true) { S.OutputFolder = d.FolderName; DataContext = null; DataContext = S; }
    }

    void OnSelect(object s, SelectionChangedEventArgs e) => Editor.Item = List.SelectedItem as QueueItem;

    void OnBatchSlice(object s, RoutedEventArgs e)
    {
        if (Editor.Item == null) { msgs.Text = "Select an image and position the splitter first."; return; }
        var ratios = Editor.SliceRatios();
        if (ratios.X.Length != 1 || ratios.Y.Length != 0)
        {
            msgs.Text = "Batch keep options require a 2 � 1 slice grid.";
            return;
        }

        var targets = SliceScope.SelectedValue?.ToString() == "all"
            ? Q.ToList()
            : List.SelectedItems.Cast<QueueItem>().ToList();
        if (targets.Count == 0) targets.Add(Editor.Item);

        int? keep = SliceKeep.SelectedValue?.ToString() switch
        {
            "left" => 0,
            "right" => 1,
            _ => null
        };
        int changed = 0;
        foreach (var target in targets.ToList())
        {
            int at = Q.IndexOf(target);
            if (at < 0) continue;
            var parts = target.Slice(EditorView.SliceCellsFor(target.Original.Width, target.Original.Height, ratios.X, ratios.Y), keep);
            Q.RemoveAt(at);
            for (int i = 0; i < parts.Count; i++) Q.Insert(at + i, parts[i]);
            changed++;
        }
        if (Q.Count > 0) List.SelectedIndex = Math.Min(0, Q.Count - 1);
        msgs.Text = $"Applied splitter to {changed} image(s).";
    }

    void ClearQueue(object s, RoutedEventArgs e)
    {
        int count = Q.Count;
        Editor.Item = null;
        Q.Clear();
        List.SelectedIndex = -1;
        msgs.Text = count == 0 ? "Queue is already empty." : $"Removed {count} file(s) from the queue. Original files were not deleted.";
    }

    // ---- clean / review / export ----
    void Clean(QueueItem i)
    {
        i.Working = BackgroundCleaner.AutoClean(i.Original, S.Tolerance, S.Grow);
        i.UndoStack.Clear(); i.RedoStack.Clear();
        i.Status = Status.Cleaned;
    }

    async void CleanAll(object s, RoutedEventArgs e)
    {
        var todo = Q.Where(i => i.Status == Status.Pending).ToList();
        await Task.Run(() => Parallel.ForEach(todo, Clean));
        Editor.InvalidateVisual();
    }

    void CleanSelected(object s, RoutedEventArgs e)
    {
        if (List.SelectedItem is QueueItem i) Clean(i);
        Editor.InvalidateVisual();
    }

    void Approve(object s, RoutedEventArgs e)
    {
        if (List.SelectedItem is not QueueItem i || i.Working == null) return;
        i.Status = Status.Approved;
        if (List.SelectedIndex < Q.Count - 1) List.SelectedIndex++;
    }

    void ApproveAll(object s, RoutedEventArgs e)
    {
        foreach (var i in Q.Where(i => i.Status == Status.Cleaned)) i.Status = Status.Approved;
    }

    void Export(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(S.OutputFolder)) { PickOutput(s, e); if (string.IsNullOrWhiteSpace(S.OutputFolder)) return; }
        var ready = Q.Where(i => i.Status is Status.Approved or Status.Exported).ToList();
        SpriteExporter.ExportAll(ready, S);
        foreach (var i in ready) i.Status = Status.Exported;
        msgs.Text = $"Exported {ready.Count} file(s) to {S.OutputFolder}";
    }

    // ---- editor controls ----
    void OnTool(object s, RoutedEventArgs e)
    {
        if (Editor == null) return;
        Editor.Tool = Enum.Parse<Tool>((string)((RadioButton)s).Tag);
        Editor.InvalidateVisual();
    }

    void OnCrop(object s, RoutedEventArgs e) => Editor.ApplyCrop();

    void OnCropAll(object s, RoutedEventArgs e)
    {
        int n = Editor.ApplyCrop(Q);
        if (n > 0) msgs.Text = $"Cropped {n} image(s) of the same size.";
    }

    void OnFixedCrop(object s, RoutedEventArgs e)
    {
        if (S.CropW < 1 || S.CropH < 1)
        {
            msgs.Text = "Enter a crop width and height first.";
            return;
        }

        var targets = CropScope.SelectedValue?.ToString() == "all"
            ? Q.ToList()
            : List.SelectedItems.Cast<QueueItem>().ToList();
        if (targets.Count == 0 && Editor.Item != null) targets.Add(Editor.Item);

        int cropped = 0, skipped = 0;
        foreach (var item in targets.Distinct())
        {
            if (item.Original.Width < S.CropW || item.Original.Height < S.CropH)
            {
                skipped++;
                continue;
            }

            int left = (item.Original.Width - S.CropW) / 2;
            int top = (item.Original.Height - S.CropH) / 2;
            item.Crop(new SkiaSharp.SKRectI(left, top, left + S.CropW, top + S.CropH));
            cropped++;
        }

        if (cropped > 0) Editor.Fit();
        msgs.Text = skipped == 0
            ? $"Applied a centred {S.CropW} � {S.CropH} crop to {cropped} image(s)."
            : $"Cropped {cropped} image(s); skipped {skipped} image(s) smaller than {S.CropW} � {S.CropH}.";
    }

    void OnSlice(object s, RoutedEventArgs e)
    {
        if (List.SelectedItem is not QueueItem cur) return;
        var cells = Editor.SliceCells();
        if (cells.Length < 2) return;
        var parts = cur.Slice(cells);
        int at = Q.IndexOf(cur);
        Q.RemoveAt(at);
        for (int i = 0; i < parts.Count; i++) Q.Insert(at + i, parts[i]);
        List.SelectedIndex = at;
        ApplyAutoSize(parts[0].Original.Width, parts[0].Original.Height);
    }

    /// Puts the original image back in place of its slices (edits made to the slices are discarded).
    void OnUnslice(object s, RoutedEventArgs e)
    {
        if (List.SelectedItem is not QueueItem cur || cur.SliceParent is not QueueItem parent) return;
        var sibs = Q.Where(x => x.SliceParent == parent).ToList();
        int at = sibs.Min(x => Q.IndexOf(x));
        foreach (var x in sibs) Q.Remove(x);
        Q.Insert(at, parent);
        List.SelectedItem = parent;
        ApplyAutoSize(parent.Original.Width, parent.Original.Height);
    }

    void OnResetGrid(object s, RoutedEventArgs e) => Editor.ResetGrid();

    void OnGridChanged(object s, TextChangedEventArgs e)
    {
        if (Editor == null) return;
        Dispatcher.BeginInvoke(new Action(Editor.InvalidateVisual), System.Windows.Threading.DispatcherPriority.Background);
    }

    void ApplyAutoSize(int w, int h)
    {
        if (!S.AutoSize) return;
        S.OutW = w; S.OutH = h;
        DataContext = null; DataContext = S;
    }
    void OnResetCrop(object s, RoutedEventArgs e) => Editor.ResetCrop();
    void OnUndo(object s, RoutedEventArgs e) => Editor.Undo();
    void OnRedo(object s, RoutedEventArgs e) => Editor.Redo();
    void OnFit(object s, RoutedEventArgs e) => Editor.Fit();
    void On100(object s, RoutedEventArgs e) => Editor.ZoomTo(1);
    void OnZoomIn(object s, RoutedEventArgs e) => Editor.ZoomBy(1.25f);
    void OnZoomOut(object s, RoutedEventArgs e) => Editor.ZoomBy(0.8f);

    void OnShowOrig(object s, RoutedEventArgs e)
    {
        Editor.ShowOriginal = ShowOrig.IsChecked == true;
        Editor.RefreshInfo(); Editor.InvalidateVisual();
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) Editor.Undo();
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) Editor.Redo();
        else if (e.Key == Key.Escape) Editor.ClearSelection();
    }
}
