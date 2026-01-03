using System.Diagnostics;

namespace MonitorWindowsRestore;

public class WindowTracker
{
    private readonly Config _config;
    private readonly WindowState _state;
    private readonly HashSet<string> _processNames;

    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private readonly Dictionary<IntPtr, System.Timers.Timer> _pendingCaptures = new();
    private readonly object _lock = new();
    private int _zOrderCounter;
    private System.Timers.Timer? _saveTimer;
    private bool _savePending;

    public event Action<string>? OnLog;

    public WindowTracker(Config config, WindowState state)
    {
        _config = config;
        _state = state;
        _processNames = new HashSet<string>(_config.Programs, StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        // Initial scan to populate state
        TrackWindows();

        // Keep delegate alive to prevent GC
        _winEventDelegate = OnWindowEvent;

        // Install hooks for foreground (focus) and location changes
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventDelegate,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_foregroundHook == IntPtr.Zero || _locationHook == IntPtr.Zero)
        {
            OnLog?.Invoke("Warning: Failed to install one or more event hooks");
        }

        OnLog?.Invoke("Window tracking started (event hooks)");
    }

    public void Stop()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        // Dispose all pending capture timers
        lock (_lock)
        {
            foreach (var timer in _pendingCaptures.Values)
            {
                timer.Stop();
                timer.Dispose();
            }
            _pendingCaptures.Clear();
        }

        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = null;

        OnLog?.Invoke("Window tracking stopped");
    }

    private void OnWindowEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only handle window-level events, not child objects
        if (idObject != NativeMethods.OBJID_WINDOW) return;
        if (hwnd == IntPtr.Zero) return;

        // Check if this is an app window we care about
        if (!NativeMethods.IsAppWindow(hwnd)) return;

        // Get process info
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        string? processName = null;

        try
        {
            var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName + ".exe";
        }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }

        // Only track configured programs
        if (!_processNames.Contains(processName)) return;

        // Only track when required monitors are connected
        if (Screen.AllScreens.Length < _config.RequiredMonitorCount) return;

        // Update Z-order on focus change
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            Interlocked.Increment(ref _zOrderCounter);
        }

        // Start or reset debounce timer for this window
        StartDebounceTimer(hwnd, eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND);
    }

    private void StartDebounceTimer(IntPtr hwnd, bool isFocusChange)
    {
        lock (_lock)
        {
            if (_pendingCaptures.TryGetValue(hwnd, out var existingTimer))
            {
                // Reset existing timer
                existingTimer.Stop();
                existingTimer.Start();
            }
            else
            {
                // Create new timer
                var timer = new System.Timers.Timer(_config.DebounceDelayMs);
                timer.AutoReset = false;
                var capturedHwnd = hwnd;
                var capturedZOrder = _zOrderCounter;
                timer.Elapsed += (_, _) => CaptureWindow(capturedHwnd, capturedZOrder);
                _pendingCaptures[hwnd] = timer;
                timer.Start();
            }

            // Update the captured Z-order if this is a focus change
            if (isFocusChange && _pendingCaptures.TryGetValue(hwnd, out var t))
            {
                // Re-create timer to capture updated Z-order
                t.Stop();
                t.Dispose();
                var timer = new System.Timers.Timer(_config.DebounceDelayMs);
                timer.AutoReset = false;
                var capturedHwnd = hwnd;
                var capturedZOrder = _zOrderCounter;
                timer.Elapsed += (_, _) => CaptureWindow(capturedHwnd, capturedZOrder);
                _pendingCaptures[hwnd] = timer;
                timer.Start();
            }
        }
    }

    private void CaptureWindow(IntPtr hwnd, int zOrder)
    {
        lock (_lock)
        {
            _pendingCaptures.Remove(hwnd);
        }

        // Check window still exists and get its rect
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        // Skip minimized windows - keep existing position
        if (NativeMethods.IsIconic(hwnd)) return;

        // Skip hung windows
        if (NativeMethods.IsHungAppWindow(hwnd)) return;

        // Get window info
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        string? processName = null;

        try
        {
            var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName + ".exe";
        }
        catch { return; }

        var title = NativeMethods.GetWindowTitle(hwnd);
        if (string.IsNullOrEmpty(title)) return;

        var id = WindowInfo.GenerateId(processName, title);
        bool isMaximized = NativeMethods.IsZoomed(hwnd);

        var info = new WindowInfo
        {
            ProcessName = processName,
            WindowTitle = title,
            Id = id,
            X = rect.Left,
            Y = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
            IsMaximized = isMaximized,
            ZOrder = zOrder
        };

        _state.Windows[id] = info;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            if (_savePending) return;
            _savePending = true;

            _saveTimer?.Stop();
            _saveTimer?.Dispose();
            _saveTimer = new System.Timers.Timer(100); // Batch saves within 100ms
            _saveTimer.AutoReset = false;
            _saveTimer.Elapsed += (_, _) =>
            {
                lock (_lock) { _savePending = false; }
                _state.Save();
                OnLog?.Invoke($"Saved {_state.Windows.Count} windows");
            };
            _saveTimer.Start();
        }
    }

    /// <summary>
    /// Manual full scan - used for "Track Now" menu and initial population
    /// </summary>
    public void TrackWindows()
    {
        // Only track when required monitors are connected
        if (Screen.AllScreens.Length < _config.RequiredMonitorCount)
        {
            OnLog?.Invoke($"Only {Screen.AllScreens.Length}/{_config.RequiredMonitorCount} monitors, skipping tracking");
            return;
        }

        var foundWindows = new HashSet<string>();
        int zOrderCounter = 0;

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
                    IsMaximized = isMaximized,
                    ZOrder = zOrderCounter++
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

        // Update the Z-order counter to be above all enumerated windows
        _zOrderCounter = zOrderCounter;

        _state.Save();
        OnLog?.Invoke($"Tracked {_state.Windows.Count} windows");
    }
}
