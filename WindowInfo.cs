using System.Text.Json;

namespace MonitorWindowsRestore;

public class WindowInfo
{
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";

    // The restored ("normal") rectangle, as reported by GetWindowPlacement. For a
    // maximized window this is the size it returns to when un-maximized, not the
    // monitor-sized rect it currently occupies.
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }

    /// <summary>
    /// Reads the current placement of a window. Returns null if the window is gone.
    /// Works for minimized windows too, since the normal rect is still meaningful.
    /// </summary>
    public static WindowInfo? Capture(IntPtr hWnd, string processName, string title)
    {
        var placement = NativeMethods.WINDOWPLACEMENT.Create();
        if (!NativeMethods.GetWindowPlacement(hWnd, ref placement)) return null;

        var rect = placement.rcNormalPosition;
        bool minimized = placement.showCmd == NativeMethods.SW_SHOWMINIMIZED;
        bool maximized = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED
            || (minimized && (placement.flags & NativeMethods.WPF_RESTORETOMAXIMIZED) != 0);

        return new WindowInfo
        {
            ProcessName = processName,
            WindowTitle = title,
            X = rect.Left,
            Y = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
            IsMaximized = maximized
        };
    }
}

/// <summary>
/// Saved placements keyed by window handle. A handle is stable for the life of a window,
/// so it survives title changes (browser tab switches, etc.) and even survives this app
/// restarting. It does not survive a reboot, which is what <see cref="WindowInfo.ProcessName"/>
/// and <see cref="WindowInfo.WindowTitle"/> are kept for - see WindowRestorer.ResolveHandle.
/// </summary>
public class WindowState
{
    /// <summary>Public for serialization only. Go through the methods below, which lock.</summary>
    public Dictionary<long, WindowInfo> Windows { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.Now;

    private readonly object _lock = new();

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
            var state = JsonSerializer.Deserialize<WindowState>(json, JsonOptions) ?? new WindowState();

            // A hand-edited or truncated file can leave null values behind
            foreach (var key in state.Windows.Where(w => w.Value == null).Select(w => w.Key).ToList())
                state.Windows.Remove(key);

            return state;
        }
        catch
        {
            return new WindowState();
        }
    }

    public int Count
    {
        get { lock (_lock) return Windows.Count; }
    }

    public bool Contains(IntPtr hWnd)
    {
        lock (_lock) return Windows.ContainsKey(hWnd.ToInt64());
    }

    public void Set(IntPtr hWnd, WindowInfo info)
    {
        lock (_lock) Windows[hWnd.ToInt64()] = info;
    }

    public bool Remove(long hWnd)
    {
        lock (_lock) return Windows.Remove(hWnd);
    }

    /// <summary>Re-keys an entry after its window was matched to a different handle.</summary>
    public void Move(long from, IntPtr to)
    {
        lock (_lock)
        {
            if (!Windows.Remove(from, out var info)) return;
            Windows[to.ToInt64()] = info;
        }
    }

    public List<KeyValuePair<long, WindowInfo>> Snapshot()
    {
        lock (_lock) return Windows.ToList();
    }

    public void Save()
    {
        // The write stays inside the lock so the manual scan and the debounced save can't
        // both have the file open at once.
        lock (_lock)
        {
            LastUpdated = DateTime.Now;
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
