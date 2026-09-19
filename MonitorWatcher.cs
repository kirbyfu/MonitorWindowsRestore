using Microsoft.Win32;

namespace MonitorWindowsRestore;

public class MonitorWatcher
{
    private readonly Config _config;
    private readonly System.Timers.Timer _debounceTimer;
    private volatile bool _frozen;
    private int _lowestCountSeen;

    /// <summary>
    /// When true, window captures should be frozen (display is reconfiguring, or the
    /// required monitors are missing).
    /// </summary>
    public bool IsFrozen => _frozen;

    /// <summary>Raised the moment a display change is noticed, before anything else runs.</summary>
    public event Action? OnFreeze;

    public event Action? OnMonitorsRestored;
    public event Action<string>? OnLog;

    public MonitorWatcher(Config config)
    {
        _config = config;
        _lowestCountSeen = NativeMethods.MonitorCount();

        _debounceTimer = new System.Timers.Timer(_config.RestoreDelayMs);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (_, _) => CheckAndRestore();
    }

    public void Start()
    {
        // Both of these are raised back to back from the same WM_DISPLAYCHANGE, after the
        // change has already happened, so there is no true "before" notification. Freezing
        // in Changing and debouncing in Changed still keeps the two concerns separate.
        SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        OnLog?.Invoke($"Monitor watcher started (currently {NativeMethods.MonitorCount()} monitors)");
    }

    public void Stop()
    {
        SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounceTimer.Stop();
        OnLog?.Invoke("Monitor watcher stopped");
    }

    private void OnDisplaySettingsChanging(object? sender, EventArgs e)
    {
        if (!_frozen)
        {
            _frozen = true;
            OnLog?.Invoke("Display settings changing - captures frozen");
        }

        OnFreeze?.Invoke();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Sample here as well as after the debounce: a monitor that blinks off and back on
        // within the debounce window still shoves every window, and we'd otherwise never
        // see the count dip.
        int count = NativeMethods.MonitorCount();
        _lowestCountSeen = Math.Min(_lowestCountSeen, count);

        OnLog?.Invoke($"Display settings changed ({count} monitors), debouncing...");
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void CheckAndRestore()
    {
        int currentCount = NativeMethods.MonitorCount();
        int lowest = Math.Min(_lowestCountSeen, currentCount);

        if (currentCount < _config.RequiredMonitorCount)
        {
            OnLog?.Invoke($"Only {currentCount}/{_config.RequiredMonitorCount} monitors, staying frozen");
            _lowestCountSeen = lowest;
            return;
        }

        if (lowest < _config.RequiredMonitorCount)
        {
            OnLog?.Invoke($"Monitors back ({lowest} -> {currentCount}), triggering restore");
            OnMonitorsRestored?.Invoke();
        }
        else
        {
            // Resolution, DPI or HDR change with every monitor still attached: nothing was
            // shoved, so there's nothing to put back.
            OnLog?.Invoke($"Display changed with {currentCount} monitors still attached, no restore needed");
        }

        _lowestCountSeen = currentCount;
        _frozen = false;
        OnLog?.Invoke("Captures resumed");
    }
}
