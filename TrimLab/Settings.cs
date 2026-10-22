using System.IO;
using System.Text.Json;
namespace TrimLab;

public class Settings
{
    public int OutW { get; set; } = 128;
    public int OutH { get; set; } = 128;
    public int Tolerance { get; set; } = 24;   // max colour spread still counted as checker grey
    public int Grow { get; set; } = 1;         // px eaten into the figure edge to remove halo
    public int BrushSize { get; set; } = 12;
    public bool AutoSize { get; set; } = true;       // set output size to the crop / slice size
    public bool AllowUpscale { get; set; } = false;  // false = never enlarge sprites on export
    public float Zoom { get; set; }
    public int SliceCols { get; set; } = 6;
    public int SliceRows { get; set; } = 1;
    public int CropW { get; set; }
    public int CropH { get; set; }
    public int WandTol { get; set; } = 30;     // colour distance for the magic wand
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrimLab", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
