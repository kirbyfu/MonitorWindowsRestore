using System.Diagnostics;

namespace MonitorWindowsRestore;

public class WindowRestorer
{
    private readonly Config _config;
    private readonly WindowState _state;
    private readonly HashSet<string> _processNames;

    public event Action<string>? OnLog;

    public WindowRestorer(Config config, WindowState state)
    {
        _config = config;
        _state = state;
        _processNames = new HashSet<string>(_config.Programs, StringComparer.OrdinalIgnoreCase);
    }

    public void RestoreAll()
    {
        // Take a snapshot to avoid collection modified during enumeration
        // (WindowTracker may update _state.Windows on background threads)
        var windowsSnapshot = _state.Windows.ToList();

        OnLog?.Invoke($"Restoring {windowsSnapshot.Count} windows...");

        var toRemove = new List<string>();
        var windowHandles = GetCurrentWindowHandles();

        // Remove any corrupt entries with null values (can occur from malformed state file)
        var nullEntries = windowsSnapshot.Where(w => w.Value == null).Select(w => w.Key).ToList();
        foreach (var id in nullEntries)
        {
            OnLog?.Invoke($"Removing corrupt entry: {id}");
            toRemove.Add(id);
        }

        // Restore in reverse Z-order (bottom windows first) so topmost ends up on top
        foreach (var (id, info) in windowsSnapshot.Where(w => w.Value != null).OrderByDescending(w => w.Value!.ZOrder))
        {
            if (!windowHandles.TryGetValue(id, out var hWnd))
            {
                OnLog?.Invoke($"Window no longer exists: {info.WindowTitle}");
                toRemove.Add(id);
                continue;
            }

            // Skip unresponsive windows
            if (NativeMethods.IsHungAppWindow(hWnd))
            {
                OnLog?.Invoke($"Skipping unresponsive window: {info.WindowTitle}");
                toRemove.Add(id);
                continue;
            }

            RestoreWindow(hWnd, info);
        }

        // Clean up removed windows
        foreach (var id in toRemove)
        {
            _state.Windows.Remove(id);
        }

        if (toRemove.Count > 0)
        {
            _state.Save();
        }

        OnLog?.Invoke("Restore complete");
    }

    private void RestoreWindow(IntPtr hWnd, WindowInfo info)
    {
        try
        {
            // First restore if minimized
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            }

            if (info.IsMaximized)
            {
                // For maximized windows, first move to correct position, then maximize
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOP,
                    info.X, info.Y, info.Width, info.Height,
                    NativeMethods.SWP_NOACTIVATE);
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MAXIMIZE);
            }
            else
            {
                NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOP,
                    info.X, info.Y, info.Width, info.Height,
                    NativeMethods.SWP_NOACTIVATE);
            }

            OnLog?.Invoke($"Restored: {info.WindowTitle} to ({info.X}, {info.Y})");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Failed to restore {info.WindowTitle}: {ex.Message}");
        }
    }

    private Dictionary<string, IntPtr> GetCurrentWindowHandles()
    {
        var handles = new Dictionary<string, IntPtr>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsAppWindow(hWnd)) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);

            try
            {
                var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName + ".exe";

                if (!_processNames.Contains(processName)) return true;

                var title = NativeMethods.GetWindowTitle(hWnd);
                if (string.IsNullOrEmpty(title)) return true;

                var id = WindowInfo.GenerateId(processName, title);
                handles[id] = hWnd;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }

            return true;
        }, IntPtr.Zero);

        return handles;
    }
}
