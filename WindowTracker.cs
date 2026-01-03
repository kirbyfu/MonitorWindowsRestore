using System.Diagnostics;

namespace MonitorWindowsRestore;

public class WindowTracker
{
    private readonly Config _config;
    private readonly WindowState _state;
    private readonly System.Timers.Timer _timer;
    private readonly HashSet<string> _processNames;

    public event Action<string>? OnLog;

    public WindowTracker(Config config, WindowState state)
    {
        _config = config;
        _state = state;
        _processNames = new HashSet<string>(_config.Programs, StringComparer.OrdinalIgnoreCase);

        _timer = new System.Timers.Timer(_config.PollingIntervalSeconds * 1000);
        _timer.Elapsed += (_, _) => TrackWindows();
        _timer.AutoReset = true;
    }

    public void Start()
    {
        TrackWindows(); // Initial scan
        _timer.Start();
        OnLog?.Invoke($"Window tracking started (polling every {_config.PollingIntervalSeconds}s)");
    }

    public void Stop()
    {
        _timer.Stop();
        OnLog?.Invoke("Window tracking stopped");
    }

    public void TrackWindows()
    {
        // Only track when both monitors are connected
        if (Screen.AllScreens.Length < 2)
        {
            OnLog?.Invoke("Single monitor detected, skipping position tracking");
            return;
        }

        var foundWindows = new HashSet<string>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsAppWindow(hWnd)) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);

            try
            {
                var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName + ".exe";

                if (!_processNames.Contains(processName)) return true;

                // Skip hung/unresponsive windows
                if (NativeMethods.IsHungAppWindow(hWnd))
                {
                    OnLog?.Invoke($"Skipping unresponsive window: {processName}");
                    return true;
                }

                var title = NativeMethods.GetWindowTitle(hWnd);
                if (string.IsNullOrEmpty(title)) return true;

                var id = WindowInfo.GenerateId(processName, title);
                foundWindows.Add(id);

                // Skip minimized windows - keep existing position if we have one
                if (NativeMethods.IsIconic(hWnd)) return true;

                NativeMethods.GetWindowRect(hWnd, out var rect);
                bool isMaximized = NativeMethods.IsZoomed(hWnd);

                var info = new WindowInfo
                {
                    ProcessName = processName,
                    WindowTitle = title,
                    Id = id,
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Right - rect.Left,
                    Height = rect.Bottom - rect.Top,
                    IsMaximized = isMaximized
                };

                _state.Windows[id] = info;
            }
            catch (ArgumentException)
            {
                // Process no longer exists
            }
            catch (InvalidOperationException)
            {
                // Process has exited
            }

            return true;
        }, IntPtr.Zero);

        // Remove windows that no longer exist
        var toRemove = _state.Windows.Keys.Except(foundWindows).ToList();
        foreach (var id in toRemove)
        {
            OnLog?.Invoke($"Removing closed window: {_state.Windows[id].WindowTitle}");
            _state.Windows.Remove(id);
        }

        _state.Save();
        OnLog?.Invoke($"Tracked {_state.Windows.Count} windows");
    }
}
