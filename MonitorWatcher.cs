using Microsoft.Win32;

namespace MonitorWindowsRestore;

public class MonitorWatcher
{
    private readonly Config _config;
    private readonly System.Timers.Timer _debounceTimer;
    private int _lastMonitorCount;

    /// <summary>
    /// When true, window captures should be frozen (display is reconfiguring)
    /// </summary>
    public bool IsFrozen { get; private set; }

    public event Action? OnMonitorsRestored;
    public event Action<string>? OnLog;

    public MonitorWatcher(Config config)
    {
        _config = config;
        _lastMonitorCount = Screen.AllScreens.Length;

        _debounceTimer = new System.Timers.Timer(_config.RestoreDelayMs);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (_, _) => CheckAndRestore();
    }

    public void Start()
    {
        SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        OnLog?.Invoke($"Monitor watcher started (currently {_lastMonitorCount} monitors)");
    }

    private void OnDisplaySettingsChanging(object? sender, EventArgs e)
    {
        IsFrozen = true;
        OnLog?.Invoke("Display settings changing - captures frozen");
    }

    public void Stop()
    {
        SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounceTimer.Stop();
        OnLog?.Invoke("Monitor watcher stopped");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        OnLog?.Invoke("Display settings changed, debouncing...");
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void CheckAndRestore()
    {
        int currentCount = Screen.AllScreens.Length;
        OnLog?.Invoke($"Monitor count: {_lastMonitorCount} -> {currentCount}");

        if (currentCount >= _config.RequiredMonitorCount)
        {
            // Required monitors are connected - restore windows and resume capture
            OnLog?.Invoke($"All {_config.RequiredMonitorCount} monitors detected, triggering restore");
            OnMonitorsRestored?.Invoke();
            IsFrozen = false;
            OnLog?.Invoke("Captures resumed");
        }
        else
        {
            // Still missing monitors - stay frozen
            OnLog?.Invoke($"Only {currentCount}/{_config.RequiredMonitorCount} monitors, staying frozen");
        }

        _lastMonitorCount = currentCount;
    }
}
