using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
namespace TrimLab;

public partial class MainWindow : Window
{
    readonly Settings S = Settings.Load();
    readonly ObservableCollection<QueueItem> Q = new();
    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    static readonly Dictionary<Tool, string> Hints = new()
    {
        [Tool.Select] = "Drag a rectangle. Click empty space to deselect.",
        [Tool.Wand] = "Click to select similar connected pixels.",
        [Tool.Pen] = "Click points; click the first point, double-click or press Enter to close. Esc cancels.",
        [Tool.Erase] = "Drag to erase. Ctrl+Z undoes a stroke.",
        [Tool.Restore] = "Drag to paint the original pixels back.",
        [Tool.ColorErase] = "Sample color (or Alt+click), then paint: only pixels matching the sample are erased.",
        [Tool.Crop] = "Drag a rectangle, then press Crop.",
        [Tool.Slice] = "Drag the magenta lines, then press Slice.",
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = S;
        List.ItemsSource = Q;
        Editor.Cfg = S;
        Editor.Info += t => Info.Text = t;
        Editor.Cropped += ApplyAutoSize;
        Editor.CutMade += it =>
        {
            Q.Insert(Math.Max(0, Q.IndexOf(Editor.Item!)) + 1, it);
            Notice.Text = $"Added {Path.GetFileName(it.FilePath)} to the queue";
        };
        Editor.Edited += () =>   // editing an approved/exported image puts it back into review
        { if (Editor.Item is { Status: Status.Approved or Status.Exported } it) it.Status = Status.Cleaned; };
        PanelCol.Width = new GridLength(Math.Clamp(S.PanelWidth, 180, 600));
        Editor.Notify += t => Notice.Text = t;
        Editor.Sampled += c =>
        {
            Swatch.Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
            SampleText.Text = $"RGB {c.Red}, {c.Green}, {c.Blue}  (#{c.Red:X2}{c.Green:X2}{c.Blue:X2})";
        };
        Closing += (_, _) => { S.PanelWidth = PanelCol.ActualWidth; S.Save(); };
        ShowOptions(Tool.Select);
    }

    void ShowOptions(Tool t)
    {
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        bool sel = t is Tool.Select or Tool.Wand or Tool.Pen;
        PSelMode.Visibility = PSelActions.Visibility = V(sel);
        PWand.Visibility = V(t == Tool.Wand);
        PErase.Visibility = V(t is Tool.Erase or Tool.Restore or Tool.ColorErase);
        PColor.Visibility = V(t == Tool.ColorErase);
        PCrop.Visibility = V(t == Tool.Crop);
        PSlice.Visibility = V(t == Tool.Slice);
        Hint.Text = Hints[t];
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
        var ready = Q.Where(i => i.Status == Status.Approved).ToList();
        SpriteExporter.ExportAll(ready, S);
        foreach (var i in ready) i.Status = Status.Exported;
        MessageBox.Show($"Exported {ready.Count} file(s) to {S.OutputFolder}");
    }

    // ---- tools ----
    void OnTool(object s, RoutedEventArgs e)
    {
        if (Editor == null) return;
        var t = Enum.Parse<Tool>((string)((RadioButton)s).Tag);
        Editor.Tool = t; ShowOptions(t);
    }

    void OnMode(object s, RoutedEventArgs e)
    {
        if (Editor == null) return;
        Editor.Mode = Enum.Parse<SelMode>((string)((RadioButton)s).Tag);
    }

    void OnKeep(object s, RoutedEventArgs e) => Editor.KeepSelected();
    void OnDelete(object s, RoutedEventArgs e) => Editor.DeleteSelected();
    void OnInvert(object s, RoutedEventArgs e) => Editor.Invert();
    void OnSelAll(object s, RoutedEventArgs e) => Editor.SelectAll();
    void OnDeselect(object s, RoutedEventArgs e) => Editor.Deselect();
    void OnToNew(object s, RoutedEventArgs e) => Editor.CopyToNewImage();

    void OnCrop(object s, RoutedEventArgs e) => Editor.ApplyCrop();
    void OnResetCrop(object s, RoutedEventArgs e) => Editor.ResetCrop();

    void OnCropAll(object s, RoutedEventArgs e)
    {
        int n = Editor.ApplyCrop(Q);
        if (n > 0) Notice.Text = $"Cropped {n} image(s) of the same size";
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

    void OnSample(object s, RoutedEventArgs e) => Editor.StartSampling();

    void OnColorHelp(object s, RoutedEventArgs e) => MessageBox.Show(
        "Color erase removes only pixels that match a colour you pick.\n\n" +
        "1. Press 'Sample color' (or hold Alt and click), then click a visible pixel that has the colour to remove.\n" +
        "2. Set Tolerance: a higher value also removes similar shades.\n" +
        "3. Paint over the area. Only pixels under the brush that match the sample are erased.\n\n" +
        "The swatch and RGB values show the sampled colour. Ctrl+Z undoes a stroke.", "Color erase");

    // ---- view / history ----
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

    /// Runs before any control sees the key, so shortcuts work whatever has focus (except text boxes).
    void OnKey(object s, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.Z) Editor.Undo();
        else if (ctrl && e.Key == Key.Y) Editor.Redo();
        else if (ctrl && e.Key == Key.A) Editor.SelectAll();
        else if (ctrl && e.Key == Key.D) Editor.Deselect();
        else if (e.Key == Key.Delete) Editor.DeleteSelected();
        else if (e.Key == Key.Enter) Editor.ClosePen();
        else if (e.Key == Key.Escape) Editor.Cancel();
        else return;
        e.Handled = true;
    }
}
