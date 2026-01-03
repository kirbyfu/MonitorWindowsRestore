using System.Text.Json;

namespace MonitorWindowsRestore;

public class WindowInfo
{
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }

    public static string GenerateId(string processName, string windowTitle)
    {
        var hash = windowTitle.GetHashCode();
        return $"{processName}_{hash:X8}";
    }
}

public class WindowState
{
    public Dictionary<string, WindowInfo> Windows { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.Now;

    private static readonly string StatePath = Path.Combine(
        AppContext.BaseDirectory, "window-state.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static WindowState Load()
    {
        if (!File.Exists(StatePath))
            return new WindowState();

        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<WindowState>(json, JsonOptions) ?? new WindowState();
        }
        catch
        {
            return new WindowState();
        }
    }

    public void Save()
    {
        LastUpdated = DateTime.Now;
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(StatePath, json);
    }

    public void RemoveWindow(string id)
    {
        Windows.Remove(id);
        Save();
    }
}
