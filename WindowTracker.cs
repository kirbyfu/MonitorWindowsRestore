namespace MonitorWindowsRestore;

public class WindowTracker
{
    private readonly Config _config;
    private readonly WindowState _state;
    private readonly MonitorWatcher _monitorWatcher;
    private readonly HashSet<string> _processNames;

    private IntPtr _locationHook;
    private IntPtr _showHook;
    private IntPtr _nameChangeHook;
    private IntPtr _destroyHook;
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private readonly Dictionary<IntPtr, System.Timers.Timer> _pendingCaptures = new();
    private readonly object _lock = new();
    private System.Timers.Timer? _saveTimer;
    private bool _savePending;

    public event Action<string>? OnLog;

    public WindowTracker(Config config, WindowState state, MonitorWatcher monitorWatcher)
    {
        _config = config;
        _state = state;
        _monitorWatcher = monitorWatcher;
        _processNames = new HashSet<string>(_config.Programs, StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        // Initial scan to populate state
        TrackWindows();

        // Anything already debouncing when the display changes would capture the shoved
        // position once its timer fires, so throw those away.
        _monitorWatcher.OnFreeze += CancelPendingCaptures;

        // Keep delegate alive to prevent GC
        _winEventDelegate = OnWindowEvent;

        // Location changes cover moves, resizes, maximize and minimize. A new window is
        // typically titled and positioned while still hidden, so those events are filtered
        // out and it's the show event that first sees it. Name changes keep the saved title
        // fresh, which is what post-reboot matching relies on.
        _locationHook = Hook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
        _showHook = Hook(NativeMethods.EVENT_OBJECT_SHOW);
        _nameChangeHook = Hook(NativeMethods.EVENT_OBJECT_NAMECHANGE);
        _destroyHook = Hook(NativeMethods.EVENT_OBJECT_DESTROY);

        if (_locationHook == IntPtr.Zero || _showHook == IntPtr.Zero
            || _nameChangeHook == IntPtr.Zero || _destroyHook == IntPtr.Zero)
        {
            OnLog?.Invoke("Warning: Failed to install one or more event hooks");
        }

        OnLog?.Invoke("Window tracking started (event hooks)");
    }

    private IntPtr Hook(uint eventType) => NativeMethods.SetWinEventHook(
        eventType, eventType, IntPtr.Zero, _winEventDelegate!, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }

    public void Stop()
    {
        _monitorWatcher.OnFreeze -= CancelPendingCaptures;

        Unhook(ref _locationHook);
        Unhook(ref _showHook);
        Unhook(ref _nameChangeHook);
        Unhook(ref _destroyHook);

        CancelPendingCaptures();

        lock (_lock)
        {
            _saveTimer?.Stop();
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        OnLog?.Invoke("Window tracking stopped");
    }

    private bool MonitorsPresent() => NativeMethods.MonitorCount() >= _config.RequiredMonitorCount;

    private void OnWindowEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only handle window-level events, not child objects (the cursor fires this constantly)
        if (idObject != NativeMethods.OBJID_WINDOW) return;
        if (hwnd == IntPtr.Zero) return;

        // Child HWNDs raise these too (taskbar bands, browser render widgets). EnumWindows
        // only ever hands out top-level windows, so match that here.
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return;

        if (eventType == NativeMethods.EVENT_OBJECT_DESTROY)
        {
            OnWindowDestroyed(hwnd);
            return;
        }

        // Don't capture during display reconfiguration
        if (_monitorWatcher.IsFrozen) return;

        if (!NativeMethods.IsAppWindow(hwnd)) return;

        var processName = ProcessNames.ForWindow(hwnd);
        if (processName == null || !_processNames.Contains(processName)) return;

        if (!MonitorsPresent()) return;

        StartDebounceTimer(hwnd);
    }

    private void OnWindowDestroyed(IntPtr hwnd)
    {
        // Every destroyed HWND on the system lands here, so keep this to a lookup. Only
        // windows we've saved matter.
        if (!_state.Contains(hwnd)) return;

        lock (_lock)
        {
            if (_pendingCaptures.Remove(hwnd, out var timer))
            {
                timer.Stop();
                timer.Dispose();
            }
        }

        // A window that closes while the display is reconfiguring is still gone, but the
        // shove itself never destroys windows, so this is safe to do while frozen.
        _state.Remove(hwnd.ToInt64());
        ScheduleSave();
    }

    private void StartDebounceTimer(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (_pendingCaptures.TryGetValue(hwnd, out var existingTimer))
            {
                existingTimer.Stop();
                existingTimer.Start();
                return;
            }

            var timer = new System.Timers.Timer(_config.DebounceDelayMs) { AutoReset = false };
            timer.Elapsed += (_, _) => CaptureWindow(hwnd);
            _pendingCaptures[hwnd] = timer;
            timer.Start();
        }
    }

    private void CancelPendingCaptures()
    {
        lock (_lock)
        {
            foreach (var timer in _pendingCaptures.Values)
            {
                timer.Stop();
                timer.Dispose();
            }
            _pendingCaptures.Clear();
        }
    }

    private void CaptureWindow(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (!_pendingCaptures.Remove(hwnd, out var timer)) return; // cancelled
            timer.Dispose();
        }

        // Re-check: the display may have changed since the event that started this timer
        if (_monitorWatcher.IsFrozen || !MonitorsPresent()) return;

        var processName = ProcessNames.ForWindow(hwnd);
        if (processName == null) return;

        var title = NativeMethods.GetWindowTitle(hwnd);
        if (string.IsNullOrEmpty(title)) return;

        var info = WindowInfo.Capture(hwnd, processName, title);
        if (info == null) return;

        _state.Set(hwnd, info);
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
            _saveTimer = new System.Timers.Timer(100) { AutoReset = false }; // Batch saves within 100ms
            _saveTimer.Elapsed += (_, _) =>
            {
                lock (_lock) { _savePending = false; }
                try
                {
                    _state.Save();
                    OnLog?.Invoke($"Saved {_state.Count} windows");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"Failed to save state: {ex.Message}");
                }
            };
            _saveTimer.Start();
        }
    }

    /// <summary>
    /// Full scan - used for "Track Now" and initial population. This is the only place
    /// entries are pruned wholesale: with the required monitors present, what's on screen
    /// right now is authoritative.
    /// </summary>
    public void TrackWindows()
    {
        if (!MonitorsPresent())
        {
            OnLog?.Invoke($"Only {NativeMethods.MonitorCount()}/{_config.RequiredMonitorCount} monitors, skipping tracking");
            return;
        }

        var found = new HashSet<long>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsAppWindow(hWnd)) return true;

            var processName = ProcessNames.ForWindow(hWnd);
            if (processName == null || !_processNames.Contains(processName)) return true;

            var title = NativeMethods.GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            var info = WindowInfo.Capture(hWnd, processName, title);
            if (info == null) return true;

            found.Add(hWnd.ToInt64());
            _state.Set(hWnd, info);
            return true;
        }, IntPtr.Zero);

        foreach (var (hWnd, info) in _state.Snapshot())
        {
            if (found.Contains(hWnd)) continue;
            OnLog?.Invoke($"Removing closed window: {info.WindowTitle}");
            _state.Remove(hWnd);
        }

        _state.Save();
        OnLog?.Invoke($"Tracked {_state.Count} windows");
    }
}
